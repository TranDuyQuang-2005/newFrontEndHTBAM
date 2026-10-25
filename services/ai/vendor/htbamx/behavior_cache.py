from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any
import json
import math
import hashlib

import cv2
import numpy as np
import pandas as pd
from htbam_builder.model_assets import select_phone_detector, sha256_file

from htbam_builder.core import expand_bbox_xyxy

D6_CACHE_SCHEMA_VERSION = "2.1-streaming-square-mediapipe"

BEHAVIORS = ["Focused", "Distracted", "Sleepy", "Active"]
BEHAVIOR_TO_ID = {name: i for i, name in enumerate(BEHAVIORS)}
ATOMIC_NAMES = ["phone_use", "hand_up", "eyes_closed", "head_away"]

SEMANTIC_NAMES = [
    "ear", "eye_open", "mar", "pitch", "yaw", "roll", "gaze_away",
    "hand_up", "phone_use", "posture_upright", "motion", "face_visible",
    "face_quality", "pose_quality"
]


def _dist(a: tuple[float, float], b: tuple[float, float]) -> float:
    return float(math.hypot(a[0] - b[0], a[1] - b[1]))


def _ear(lm: list[Any], idx: list[int]) -> float:
    p = [(float(lm[i].x), float(lm[i].y)) for i in idx]
    den = 2.0 * _dist(p[0], p[3])
    if den <= 1e-9:
        return 0.0
    return (_dist(p[1], p[5]) + _dist(p[2], p[4])) / den


def _mar(lm: list[Any]) -> float:
    top = (float(lm[13].x), float(lm[13].y))
    bottom = (float(lm[14].x), float(lm[14].y))
    left = (float(lm[78].x), float(lm[78].y))
    right = (float(lm[308].x), float(lm[308].y))
    den = _dist(left, right)
    return _dist(top, bottom) / den if den > 1e-9 else 0.0


def _head_pose(lm: list[Any], width: int, height: int) -> tuple[float, float, float]:
    if width <= 0 or height <= 0:
        return 0.0, 0.0, 0.0
    image_points = np.array([
        (lm[1].x * width, lm[1].y * height),
        (lm[152].x * width, lm[152].y * height),
        (lm[33].x * width, lm[33].y * height),
        (lm[263].x * width, lm[263].y * height),
        (lm[61].x * width, lm[61].y * height),
        (lm[291].x * width, lm[291].y * height),
    ], dtype=np.float64)
    model_points = np.array([
        (0.0, 0.0, 0.0), (0.0, -63.6, -12.5), (-43.3, 32.7, -26.0),
        (43.3, 32.7, -26.0), (-28.9, -28.9, -24.1), (28.9, -28.9, -24.1),
    ], dtype=np.float64)
    focal = float(max(width, height))
    camera = np.array([[focal, 0, width / 2], [0, focal, height / 2], [0, 0, 1]], dtype=np.float64)
    ok, rvec, _ = cv2.solvePnP(model_points, image_points, camera, np.zeros((4, 1)), flags=cv2.SOLVEPNP_ITERATIVE)
    if not ok:
        return 0.0, 0.0, 0.0
    rmat, _ = cv2.Rodrigues(rvec)
    sy = math.sqrt(rmat[0, 0] ** 2 + rmat[1, 0] ** 2)
    if sy >= 1e-6:
        pitch = math.atan2(rmat[2, 1], rmat[2, 2])
        yaw = math.atan2(-rmat[2, 0], sy)
        roll = math.atan2(rmat[1, 0], rmat[0, 0])
    else:
        pitch = math.atan2(-rmat[1, 2], rmat[1, 1])
        yaw = math.atan2(-rmat[2, 0], sy)
        roll = 0.0
    return tuple(map(math.degrees, (pitch, yaw, roll)))


def _iris_gaze_away(lm: list[Any]) -> float:
    if len(lm) < 478:
        return 0.0
    vals: list[float] = []
    for iris_idx, c1, c2 in [(range(468, 473), 33, 133), (range(473, 478), 362, 263)]:
        iris_x = float(np.mean([lm[i].x for i in iris_idx]))
        x1, x2 = float(lm[c1].x), float(lm[c2].x)
        lo, hi = min(x1, x2), max(x1, x2)
        if hi - lo > 1e-6:
            pos = (iris_x - lo) / (hi - lo)
            vals.append(float(np.clip(abs(pos - 0.5) * 2.0, 0.0, 1.0)))
    return float(np.mean(vals)) if vals else 0.0


def _pose_vector(pose_lm: list[Any] | None) -> tuple[np.ndarray, float, float, float, bool, bool]:
    vec = np.zeros((33, 3), dtype=np.float32)
    if pose_lm is None or len(pose_lm) < 33:
        return vec, 0.0, 0.0, 0.0, False, False
    for i, p in enumerate(pose_lm[:33]):
        vis = float(getattr(p, "visibility", 1.0))
        vec[i] = [float(p.x), float(p.y), vis]
    # Classroom desks hide legs: quality focuses on head/upper-body + hips, not feet.
    quality_ids=list(range(0,17))+[23,24]
    quality = float(np.mean(vec[quality_ids, 2]))
    hand_up = 0.0; hand_valid=False
    for wrist, shoulder in [(15, 11), (16, 12)]:
        if vec[wrist, 2] > 0.4 and vec[shoulder, 2] > 0.4:
            hand_valid=True
            if vec[wrist, 1] < vec[shoulder, 1]: hand_up = 1.0
    posture_valid = bool(min(vec[11,2],vec[12,2],vec[23,2],vec[24,2]) > 0.35)
    upright=0.0
    if posture_valid:
        sx, sy = float((vec[11, 0] + vec[12, 0]) / 2), float((vec[11, 1] + vec[12, 1]) / 2)
        hx, hy = float((vec[23, 0] + vec[24, 0]) / 2), float((vec[23, 1] + vec[24, 1]) / 2)
        dx, dy = hx - sx, hy - sy
        angle = math.degrees(math.atan2(abs(dx), max(abs(dy), 1e-6)))
        upright = float(np.clip(1.0 - angle / 45.0, 0.0, 1.0))
    return vec, quality, hand_up, upright, hand_valid, posture_valid


def _blur_quality(crop: np.ndarray) -> float:
    if crop.size == 0:
        return 0.0
    gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)
    var = float(cv2.Laplacian(gray, cv2.CV_64F).var())
    return float(np.clip(var / 200.0, 0.0, 1.0))


@dataclass
class MPDetectors:
    face: Any
    pose: Any

    @classmethod
    def create(cls, face_model: str | Path, pose_model: str | Path) -> "MPDetectors":
        try:
            import mediapipe as mp
            from mediapipe.tasks import python
            from mediapipe.tasks.python import vision
        except ImportError as e:
            raise RuntimeError("D6 multimodal cần mediapipe. Cài: pip install mediapipe") from e
        face_path, pose_path = Path(face_model), Path(pose_model)
        if not face_path.exists() or not pose_path.exists():
            raise FileNotFoundError(f"Thiếu MediaPipe model: {face_path} hoặc {pose_path}")
        face_opts = vision.FaceLandmarkerOptions(
            base_options=python.BaseOptions(model_asset_path=str(face_path)),
            running_mode=vision.RunningMode.IMAGE,
            num_faces=1,
        )
        pose_opts = vision.PoseLandmarkerOptions(
            base_options=python.BaseOptions(model_asset_path=str(pose_path)),
            running_mode=vision.RunningMode.IMAGE,
            num_poses=1,
        )
        return cls(
            vision.FaceLandmarker.create_from_options(face_opts),
            vision.PoseLandmarker.create_from_options(pose_opts),
        )

    def detect(self, crop_bgr: np.ndarray) -> tuple[list[Any] | None, list[Any] | None]:
        import mediapipe as mp
        rgb = cv2.cvtColor(crop_bgr, cv2.COLOR_BGR2RGB)
        image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)
        fr = self.face.detect(image)
        pr = self.pose.detect(image)
        face_lm = fr.face_landmarks[0] if fr.face_landmarks else None
        pose_lm = pr.pose_landmarks[0] if pr.pose_landmarks else None
        return face_lm, pose_lm

    def close(self) -> None:
        self.face.close()
        self.pose.close()


class PhoneDetector:
    def __init__(self, enabled: bool, weights: str, class_id: int, conf: float, device: Any = 0):
        self.enabled = bool(enabled)
        self.class_id = int(class_id)
        self.conf = float(conf)
        self.model = None
        self.device = device
        if self.enabled:
            try:
                from ultralytics import YOLO
            except ImportError as e:
                raise RuntimeError("Phone detector cần ultralytics") from e
            self.model = YOLO(weights)

    def score(self, crop_bgr: np.ndarray) -> float:
        if self.model is None or crop_bgr.size == 0:
            return 0.0
        pred = self.model.predict(
            crop_bgr, classes=[self.class_id], conf=self.conf, device=self.device,
            verbose=False, imgsz=max(320, min(640, max(crop_bgr.shape[:2]))),
        )[0]
        if pred.boxes is None or len(pred.boxes) == 0:
            return 0.0
        return float(pred.boxes.conf.max().detach().cpu())


def _square_pad(crop: np.ndarray) -> np.ndarray:
    """Pad a person crop to a square without distorting geometry.

    MediaPipe internally projects normalized landmarks through a NORM_RECT. Feeding a
    non-square crop can trigger projection warnings and, more importantly, makes x/y
    geometry use different scales. Square padding preserves the original person aspect
    ratio while giving landmark geometry an isotropic coordinate system.
    """
    h, w = crop.shape[:2]
    if h <= 0 or w <= 0:
        raise ValueError("empty crop")
    side = max(h, w)
    canvas = np.zeros((side, side, 3), dtype=np.uint8)
    y0, x0 = (side - h) // 2, (side - w) // 2
    canvas[y0:y0+h, x0:x0+w] = crop
    return canvas


def _square_resize(crop: np.ndarray, size: int) -> np.ndarray:
    canvas = _square_pad(crop)
    side = canvas.shape[0]
    return cv2.resize(canvas, (size, size), interpolation=cv2.INTER_AREA if side > size else cv2.INTER_LINEAR)


def _nearest_track_row(track_rows: pd.DataFrame, target_frame: int, max_gap_frames: int) -> pd.Series | None:
    frames = track_rows["frame_index"].to_numpy(dtype=np.int64)
    if len(frames) == 0:
        return None
    idx = int(np.argmin(np.abs(frames - target_frame)))
    if abs(int(frames[idx]) - int(target_frame)) > max_gap_frames:
        return None
    return track_rows.iloc[idx]


def _sha256_file(path: str | Path) -> str:
    h = hashlib.sha256()
    with Path(path).open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def _cfg_fingerprint(cfg: dict) -> str:
    stable = {k: v for k, v in cfg.items() if k not in {"overwrite_cache", "device"}}
    payload = json.dumps(stable, sort_keys=True, ensure_ascii=False, default=str).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def build_behavior_segment_cache(
    track_csv: str | Path,
    cache_root: str | Path,
    cfg: dict,
    project_root: str | Path,
) -> Path:
    """Build D6 segment cache in ONE sequential pass over the video.

    Important performance invariant: the video is decoded once; phone detection is run at most
    once per sampled full frame; face/pose are run once per visible person crop on sampled frames.
    """
    from htbam_builder.core import add_tracklet_ids

    tracks = pd.read_csv(track_csv)
    if tracks.empty:
        raise RuntimeError(f"Không có track trong {track_csv}")
    required = {"class_id", "session_id", "video_path", "frame_index", "timestamp_sec", "track_id", "x1", "y1", "x2", "y2", "fps"}
    if not required.issubset(tracks.columns):
        raise ValueError(f"Track CSV thiếu cột: {sorted(required-set(tracks.columns))}")
    class_id, session_id = str(tracks.iloc[0].class_id), str(tracks.iloc[0].session_id)
    video_path = Path(str(tracks.iloc[0].video_path))
    fps = float(tracks.iloc[0].fps)
    if fps <= 0:
        raise ValueError("fps phải > 0")
    segment_seconds = float(cfg.get("segment_seconds", 2.0))
    frames_per_segment = int(cfg.get("frames_per_segment", 16))
    sample_fps = float(cfg.get("sample_fps", 8.0))
    crop_size = int(cfg.get("crop_size", 224))
    if segment_seconds <= 0 or frames_per_segment < 2 or sample_fps <= 0 or crop_size < 64:
        raise ValueError("D6 config không hợp lệ")
    # One track ID may disappear/re-enter. Split it into continuity-safe tracklets first.
    tracks = add_tracklet_ids(tracks, fps=fps, break_gap_sec=float(cfg.get("max_bbox_gap_sec", 0.75)))
    sample_step = max(1, int(round(fps / sample_fps)))
    out_dir = Path(cache_root) / class_id / session_id
    out_dir.mkdir(parents=True, exist_ok=True)
    manifest_path = out_dir / "segments.csv"
    meta_path = out_dir / "cache_meta.json"
    track_sha256 = _sha256_file(track_csv)
    root = Path(project_root)
    face_model_path = root / cfg["mediapipe_face_model"]
    pose_model_path = root / cfg["mediapipe_pose_model"]
    if not face_model_path.is_file() or not pose_model_path.is_file():
        raise FileNotFoundError(f"Thiếu MediaPipe model: face={face_model_path}, pose={pose_model_path}")
    phone_enabled = bool(cfg.get("enable_phone_detector", True))
    phone_selected = select_phone_detector(root, cfg) if phone_enabled else None
    source_fingerprint = {
        "cache_schema_version": D6_CACHE_SCHEMA_VERSION,
        "track_csv_sha256": track_sha256,
        "face_model_sha256": sha256_file(face_model_path),
        "pose_model_sha256": sha256_file(pose_model_path),
        "phone_enabled": phone_enabled,
        "phone_weights_sha256": sha256_file(phone_selected.path) if phone_selected else None,
        "phone_weights_source": phone_selected.source if phone_selected else None,
        "phone_class_id": int(phone_selected.class_id) if phone_selected else None,
        "d6_config_sha256": _cfg_fingerprint(cfg),
    }
    overwrite = bool(cfg.get("overwrite_cache", False))
    if manifest_path.exists() and not overwrite:
        try:
            old_manifest = pd.read_csv(manifest_path)
            old_meta = json.loads(meta_path.read_text(encoding="utf-8")) if meta_path.exists() else {}
            current_cols = {"segment_id", "video_path", "cache_path", "tracklet_id", "segment_index"}
            schema_ok = current_cols.issubset(old_manifest.columns)
            prefix = f"{class_id}__{session_id}__"
            ids_ok = (not old_manifest.empty) and old_manifest["segment_id"].astype(str).str.startswith(prefix).all()
            source_ok = old_meta.get("source_fingerprint") == source_fingerprint
            if schema_ok and ids_ok and source_ok:
                return manifest_path
        except Exception:
            pass
        raise RuntimeError(
            f"D6 cache cũ/không tương thích hoặc tracking đã thay đổi: {out_dir}. "
            "Đặt d6.overwrite_cache: true để rebuild D6 an toàn."
        )
    if overwrite and out_dir.exists():
        for stale in out_dir.glob("*.npz"):
            stale.unlink(missing_ok=True)
        manifest_path.unlink(missing_ok=True)
        meta_path.unlink(missing_ok=True)

    mpd = MPDetectors.create(face_model_path, pose_model_path)
    phone = PhoneDetector(
        enabled=phone_enabled,
        weights=str(phone_selected.path) if phone_selected else "",
        class_id=int(phone_selected.class_id) if phone_selected else 0,
        conf=float(cfg.get("phone_confidence", 0.20)),
        device=cfg.get("device", 0),
    )

    cap = cv2.VideoCapture(str(video_path))
    if not cap.isOpened():
        mpd.close(); raise RuntimeError(f"Không mở được video {video_path}")

    # Only rows that can actually be sampled are materialized.  This prevents holding
    # hundreds of thousands of person RGB crops in RAM for a long classroom session.
    sampled_tracks = tracks.loc[(tracks["frame_index"].astype(np.int64) % sample_step) == 0].copy()
    by_frame = {int(k): g for k, g in sampled_tracks.groupby("frame_index", sort=False)}
    last_sample_frame = sampled_tracks.groupby("tracklet_id")["frame_index"].max().astype(int).to_dict()

    # Streaming state: at most one 2-second bucket per currently active tracklet.
    # A completed bucket is written to NPZ immediately and released from memory.
    states: dict[str, dict[str, Any]] = {}
    prev_gray: dict[str, np.ndarray] = {}
    rows: list[dict[str, Any]] = []
    min_h, min_w = int(cfg.get("min_person_height_px", 70)), int(cfg.get("min_person_width_px", 30))
    pad = float(cfg.get("person_padding_ratio", 0.08))
    min_obs = max(4, frames_per_segment // 4)
    video_total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
    video_duration = float(video_total_frames / fps) if fps > 0 and video_total_frames > 0 else 0.0
    progress_every_sec = max(10.0, float(cfg.get("progress_every_sec", 60.0)))
    next_progress_sec = progress_every_sec

    def flush_bucket(tracklet_id: str) -> None:
        st = states.get(tracklet_id)
        if not st:
            return
        items = st.get("items", [])
        if len(items) < min_obs:
            st["items"] = []
            return
        choose = np.linspace(0, len(items) - 1, frames_per_segment).round().astype(int)
        sel = [items[i] for i in choose]
        video_arr = np.stack([r["video"] for r in sel]).astype(np.uint8)
        sem_arr = np.stack([r["semantic"] for r in sel]).astype(np.float32)
        mask_arr = np.stack([r["mask"] for r in sel]).astype(np.float32)
        pose_arr = np.stack([r["pose"] for r in sel]).astype(np.float32)
        atomic_arr = np.stack([r["atomic"] for r in sel]).astype(np.float32)
        atomic_mask_arr = np.stack([r["atomic_mask"] for r in sel]).astype(np.float32)
        track_id = int(sel[0]["track_id"])
        bi = int(st["segment_index"])
        segment_id = f"{class_id}__{session_id}__{tracklet_id}_SEG{bi:05d}"
        npz_path = out_dir / f"{segment_id}.npz"
        np.savez_compressed(
            npz_path,
            video=video_arr,
            semantic=sem_arr,
            semantic_mask=mask_arr,
            pose=pose_arr,
            atomic=atomic_arr,
            atomic_mask=atomic_mask_arr,
            frame_valid=np.ones(frames_per_segment, dtype=np.float32),
        )
        rows.append({
            "class_id": class_id,
            "session_id": session_id,
            "track_id": track_id,
            "tracklet_id": tracklet_id,
            "segment_index": bi,
            "segment_id": segment_id,
            "start_sec": float(sel[0]["timestamp"]),
            "end_sec": float(sel[-1]["timestamp"]),
            "video_path": str(video_path.resolve()),
            "cache_path": str(npz_path.resolve()),
            "observability": float(mask_arr.mean()),
            "valid_frame_ratio": 1.0,
        })
        st["items"] = []

    frame_idx = 0
    try:
        while True:
            ok, frame = cap.read()
            if not ok:
                break
            tdf = by_frame.get(frame_idx)
            if tdf is None:
                frame_idx += 1
                continue

            fh, fw = frame.shape[:2]
            timestamp_sec = frame_idx / fps
            if timestamp_sec >= next_progress_sec:
                pct = (100.0 * timestamp_sec / video_duration) if video_duration > 0 else 0.0
                print(f"[D6] {timestamp_sec:.0f}/{video_duration:.0f}s ({pct:.1f}%) | segments_written={len(rows)}", flush=True)
                next_progress_sec += progress_every_sec

            # Phone detector ONCE per sampled full frame.
            phone_boxes: list[tuple[float, float, float, float, float]] = []
            if phone.model is not None:
                pred = phone.model.predict(
                    frame,
                    classes=[phone.class_id],
                    conf=phone.conf,
                    device=phone.device,
                    verbose=False,
                    imgsz=max(640, min(1280, max(fh, fw))),
                )[0]
                if pred.boxes is not None and len(pred.boxes):
                    xy = pred.boxes.xyxy.detach().cpu().numpy()
                    cf = pred.boxes.conf.detach().cpu().numpy()
                    phone_boxes = [
                        (float(a), float(b), float(c), float(d), float(e))
                        for (a, b, c, d), e in zip(xy, cf)
                    ]

            for _, tr in tdf.iterrows():
                tracklet_id = str(tr.tracklet_id)
                track_id = int(tr.track_id)
                try:
                    x1, y1, x2, y2 = expand_bbox_xyxy([tr.x1, tr.y1, tr.x2, tr.y2], pad, fw, fh)
                except ValueError:
                    continue
                if (y2 - y1) < min_h or (x2 - x1) < min_w:
                    continue
                crop = frame[y1:y2, x1:x2]
                if crop.size == 0:
                    continue

                # RGB backbone input is fixed 224x224, while MediaPipe receives a square
                # padded crop at native resolution to preserve landmark geometry.
                video = _square_resize(crop, crop_size)
                mp_crop = _square_pad(crop)
                face_lm, pose_lm = mpd.detect(mp_crop)

                sem = np.zeros(len(SEMANTIC_NAMES), np.float32)
                mask = np.zeros_like(sem)
                pose_vec, pose_q, hand_up, upright, hand_valid, posture_valid = _pose_vector(pose_lm)
                if pose_lm is not None:
                    j = SEMANTIC_NAMES.index("pose_quality"); sem[j] = pose_q; mask[j] = 1.0
                    if hand_valid:
                        j = SEMANTIC_NAMES.index("hand_up"); sem[j] = hand_up; mask[j] = 1.0
                    if posture_valid:
                        j = SEMANTIC_NAMES.index("posture_upright"); sem[j] = upright; mask[j] = 1.0
                if face_lm is not None:
                    left = _ear(face_lm, [33, 160, 158, 133, 153, 144])
                    right = _ear(face_lm, [362, 385, 387, 263, 373, 380])
                    ear = (left + right) / 2.0
                    mar = _mar(face_lm)
                    pitch, yaw, roll = _head_pose(face_lm, mp_crop.shape[1], mp_crop.shape[0])
                    vals = {
                        "ear": ear,
                        "eye_open": float(ear >= 0.20),
                        "mar": mar,
                        "pitch": pitch,
                        "yaw": yaw,
                        "roll": roll,
                        "gaze_away": _iris_gaze_away(face_lm),
                        "face_visible": 1.0,
                        "face_quality": _blur_quality(crop),
                    }
                    for name, val in vals.items():
                        j = SEMANTIC_NAMES.index(name); sem[j] = float(val); mask[j] = 1.0

                gray = cv2.cvtColor(_square_resize(crop, 96), cv2.COLOR_BGR2GRAY)
                motion = 0.0 if tracklet_id not in prev_gray else float(
                    np.mean(cv2.absdiff(prev_gray[tracklet_id], gray)) / 255.0
                )
                prev_gray[tracklet_id] = gray
                j = SEMANTIC_NAMES.index("motion"); sem[j] = float(np.clip(motion * 4.0, 0.0, 1.0)); mask[j] = 1.0

                phone_score = 0.0
                if phone.model is not None:
                    for px1, py1, px2, py2, pc in phone_boxes:
                        pcx, pcy = (px1 + px2) / 2, (py1 + py2) / 2
                        if x1 <= pcx <= x2 and y1 <= pcy <= y2:
                            phone_score = max(phone_score, pc)
                    j = SEMANTIC_NAMES.index("phone_use"); sem[j] = phone_score; mask[j] = 1.0

                atomic = np.zeros(len(ATOMIC_NAMES), np.float32)
                atomic_mask = np.zeros_like(atomic)
                if phone.model is not None:
                    atomic[0] = phone_score; atomic_mask[0] = 1.0
                if hand_valid:
                    atomic[1] = hand_up; atomic_mask[1] = 1.0
                eo = SEMANTIC_NAMES.index("eye_open")
                yw = SEMANTIC_NAMES.index("yaw")
                gz = SEMANTIC_NAMES.index("gaze_away")
                if mask[eo]:
                    atomic[2] = 1.0 - sem[eo]; atomic_mask[2] = 1.0
                if mask[yw]:
                    atomic[3] = max(min(abs(sem[yw]) / 45.0, 1.0), sem[gz]); atomic_mask[3] = 1.0

                ts = float(tr.timestamp_sec)
                st = states.get(tracklet_id)
                if st is None:
                    st = {"base": ts, "segment_index": 0, "items": []}
                    states[tracklet_id] = st
                bi = int(math.floor((ts - float(st["base"])) / segment_seconds + 1e-9))
                if bi != int(st["segment_index"]):
                    flush_bucket(tracklet_id)
                    st["segment_index"] = bi
                st["items"].append({
                    "timestamp": ts,
                    "track_id": track_id,
                    "tracklet_id": tracklet_id,
                    "video": video,
                    "semantic": sem,
                    "mask": mask,
                    "pose": pose_vec,
                    "atomic": atomic,
                    "atomic_mask": atomic_mask,
                })

                # Once the last sampled frame of a tracklet is consumed, write its final
                # partial bucket immediately and release all per-tracklet buffers.
                if frame_idx >= int(last_sample_frame.get(tracklet_id, frame_idx)):
                    flush_bucket(tracklet_id)
                    states.pop(tracklet_id, None)
                    prev_gray.pop(tracklet_id, None)

            frame_idx += 1
    finally:
        cap.release()
        mpd.close()

    # Defensive final flush (normally states is already empty because last_sample_frame is known).
    for tid in list(states):
        flush_bucket(tid)
        states.pop(tid, None)
        prev_gray.pop(tid, None)

    manifest = pd.DataFrame(rows)
    if manifest.empty:
        raise RuntimeError(f"D6 không tạo được segment nào cho {class_id}/{session_id}")
    manifest = manifest.sort_values(["tracklet_id", "segment_index"]).reset_index(drop=True)
    manifest.to_csv(manifest_path, index=False)
    with (out_dir / "cache_meta.json").open("w", encoding="utf-8") as f:
        json.dump({
            "class_id": class_id,
            "session_id": session_id,
            "video_path": str(video_path),
            "semantic_names": SEMANTIC_NAMES,
            "atomic_names": ATOMIC_NAMES,
            "behaviors": BEHAVIORS,
            "frames_per_segment": frames_per_segment,
            "segment_seconds": segment_seconds,
            "sample_fps": sample_fps,
            "crop_size": crop_size,
            "streaming_cache": True,
            "cache_schema_version": D6_CACHE_SCHEMA_VERSION,
            "track_csv": str(Path(track_csv).resolve()),
            "track_csv_sha256": track_sha256,
            "source_fingerprint": source_fingerprint,
        }, f, indent=2, ensure_ascii=False)
    return manifest_path

def merge_behavior_manifests(
    manifests: list[str | Path],
    output_csv: str | Path,
    stable_identity_maps: list[str | Path] | None = None,
) -> Path:
    """Merge per-session D6 manifests and backfill stable identity metadata.

    Identity is metadata only; HTBAM-X behavior training must not use it as an input feature.
    Stable IDs can be regenerated from D5 without rewriting the expensive D6 NPZ cache.
    """
    frames = [pd.read_csv(p) for p in manifests]
    if not frames:
        raise ValueError("Không có behavior manifest")
    df = pd.concat(frames, ignore_index=True)
    if df.empty:
        raise RuntimeError("Behavior manifest rỗng")
    required = {"class_id", "session_id", "track_id", "tracklet_id", "segment_index",
                "segment_id", "start_sec", "end_sec", "video_path", "cache_path"}
    missing = required - set(df.columns)
    if missing:
        raise ValueError(f"D6 manifest thiếu cột: {sorted(missing)}")
    if df["segment_id"].duplicated().any():
        dup = df.loc[df["segment_id"].duplicated(keep=False), "segment_id"].tolist()[:10]
        raise ValueError(f"segment_id bị trùng: {dup}")
    df["session_key"] = df["class_id"].astype(str) + "__" + df["session_id"].astype(str)

    # Backfill D5 stable identity by tracklet. Missing/unresolved identity remains blank; this
    # never changes segment_id/cache_path and therefore does not require D6 extraction again.
    maps = []
    for mp in (stable_identity_maps or []):
        mp = Path(mp)
        if not mp.is_file():
            continue
        m = pd.read_csv(mp, dtype={"class_id": str, "session_id": str, "tracklet_id": str, "student_id": str})
        need = {"class_id", "session_id", "tracklet_id", "stable_person_id"}
        if not need.issubset(m.columns):
            raise ValueError(f"D5 stable_person_map thiếu cột: {sorted(need-set(m.columns))} tại {mp}")
        keep = [c for c in ["class_id","session_id","tracklet_id","stable_person_id","student_id","resolution_status","resolution_confidence"] if c in m.columns]
        maps.append(m[keep].drop_duplicates(["class_id","session_id","tracklet_id"]))
    if maps:
        ident = pd.concat(maps, ignore_index=True)
        if ident.duplicated(["class_id","session_id","tracklet_id"]).any():
            raise ValueError("D5 stable identity map có tracklet trùng giữa các file")
        ident = ident.rename(columns={"student_id": "resolved_student_id"})
        df = df.merge(ident, on=["class_id","session_id","tracklet_id"], how="left", validate="many_to_one")
    else:
        df["stable_person_id"] = ""
        df["resolved_student_id"] = ""
        df["resolution_status"] = ""
        df["resolution_confidence"] = np.nan

    for col in ["stable_person_id", "resolved_student_id", "resolution_status"]:
        if col in df.columns:
            df[col] = df[col].fillna("")
    df = df.sort_values(["class_id", "session_id", "tracklet_id", "segment_index"]).reset_index(drop=True)
    out = Path(output_csv)
    out.parent.mkdir(parents=True, exist_ok=True)
    df.to_csv(out, index=False)
    return out

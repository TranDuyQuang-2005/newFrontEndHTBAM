from __future__ import annotations

from pathlib import Path
import json
import cv2
import pandas as pd

from .core import SessionRef, clip_bbox_xyxy
from .model_assets import select_person_detector

TRACK_COLUMNS = [
    "class_id", "session_id", "video_path", "frame_index", "timestamp_sec",
    "track_id", "x1", "y1", "x2", "y2", "confidence", "frame_width", "frame_height", "fps"
]


def video_metadata(video_path: str | Path) -> dict:
    cap = cv2.VideoCapture(str(video_path))
    if not cap.isOpened():
        raise RuntimeError(f"Cannot open video: {video_path}")
    fps = float(cap.get(cv2.CAP_PROP_FPS))
    width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    frame_count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    cap.release()
    if fps <= 0 or width <= 0 or height <= 0:
        raise RuntimeError(f"Invalid video metadata for {video_path}: fps={fps}, size={width}x{height}")
    return {
        "fps": fps,
        "width": width,
        "height": height,
        "frame_count": frame_count,
        "duration_sec": frame_count / fps if frame_count > 0 else None,
    }


def track_session(session: SessionRef, out_dir: str | Path, cfg: dict, project_root: str | Path = ".") -> Path:
    """Run person detection + MOT. Output is PSEUDO tracking, not final ground truth."""
    try:
        from ultralytics import YOLO
    except ImportError as e:
        raise RuntimeError("Install ultralytics before tracking: pip install ultralytics") from e

    out_dir = Path(out_dir) / session.class_id / session.session_id
    out_dir.mkdir(parents=True, exist_ok=True)
    meta = video_metadata(session.video_path)
    selected = select_person_detector(project_root, cfg)
    model = YOLO(str(selected.path))
    cap = cv2.VideoCapture(str(session.video_path))
    if not cap.isOpened():
        raise RuntimeError(f"Cannot open video: {session.video_path}")

    writer = None
    if bool(cfg.get("save_preview", True)):
        preview_path = out_dir / "tracking_preview.mp4"
        fourcc = cv2.VideoWriter_fourcc(*"mp4v")
        writer = cv2.VideoWriter(str(preview_path), fourcc, meta["fps"], (meta["width"], meta["height"]))
        if not writer.isOpened():
            raise RuntimeError(f"Cannot create preview video: {preview_path}")

    rows = []
    frame_index = 0
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        # persist=True is safe here because this model instance is used for only one video stream.
        result = model.track(
            frame,
            persist=True,
            tracker=cfg.get("tracker", "botsort.yaml"),
            classes=[int(selected.class_id)],
            conf=float(cfg.get("confidence", 0.25)),
            iou=float(cfg.get("iou", 0.5)),
            imgsz=int(cfg.get("imgsz", 1280)),
            device=cfg.get("device", 0),
            verbose=False,
        )[0]
        boxes = result.boxes
        if boxes is not None and boxes.id is not None:
            ids = boxes.id.int().cpu().tolist()
            xyxy = boxes.xyxy.cpu().tolist()
            confs = boxes.conf.cpu().tolist()
            for tid, box, conf in zip(ids, xyxy, confs):
                try:
                    x1, y1, x2, y2 = clip_bbox_xyxy(*box, meta["width"], meta["height"])
                except ValueError:
                    continue
                rows.append({
                    "class_id": session.class_id,
                    "session_id": session.session_id,
                    "video_path": str(session.video_path.resolve()),
                    "frame_index": frame_index,
                    "timestamp_sec": frame_index / meta["fps"],
                    "track_id": int(tid),
                    "x1": x1, "y1": y1, "x2": x2, "y2": y2,
                    "confidence": float(conf),
                    "frame_width": meta["width"], "frame_height": meta["height"], "fps": meta["fps"],
                })
        if writer is not None:
            preview = frame.copy()

            if boxes is not None and boxes.id is not None:
                ids = boxes.id.int().cpu().tolist()
                xyxy = boxes.xyxy.cpu().tolist()
                confs = boxes.conf.cpu().tolist()

                for tid, box, conf in zip(ids, xyxy, confs):
                    try:
                        x1, y1, x2, y2 = clip_bbox_xyxy(
                            *box,
                            meta["width"],
                            meta["height"]
                        )
                    except ValueError:
                        continue

                    # BBox mỏng hơn
                    cv2.rectangle(
                        preview,
                        (x1, y1),
                        (x2, y2),
                        (255, 0, 0),
                        2
                    )

                    # Label ngắn: TrackID | confidence
                    label = f"{tid} | {conf:.2f}"

                    font = cv2.FONT_HERSHEY_SIMPLEX
                    font_scale = 0.45
                    thickness = 1

                    (text_w, text_h), baseline = cv2.getTextSize(
                        label,
                        font,
                        font_scale,
                        thickness
                    )

                    text_y = max(y1 - 4, text_h + 4)

                    # nền nhỏ phía sau chữ
                    cv2.rectangle(
                        preview,
                        (x1, text_y - text_h - 3),
                        (x1 + text_w + 4, text_y + baseline),
                        (255, 0, 0),
                        -1
                    )

                    cv2.putText(
                        preview,
                        label,
                        (x1 + 2, text_y - 2),
                        font,
                        font_scale,
                        (255, 255, 255),
                        thickness,
                        cv2.LINE_AA
                    )

            writer.write(preview)
        frame_index += 1

    cap.release()
    if writer is not None:
        writer.release()
    df = pd.DataFrame(rows, columns=TRACK_COLUMNS)
    csv_path = out_dir / "tracks_pseudo.csv"
    df.to_csv(csv_path, index=False)
    with (out_dir / "video_metadata.json").open("w", encoding="utf-8") as f:
        json.dump({**meta, "class_id": session.class_id, "session_id": session.session_id,
                   "video_path": str(session.video_path.resolve()),
                   "detector_weights": str(selected.path.resolve()),
                   "detector_source": selected.source,
                   "detector_class_id": selected.class_id}, f, ensure_ascii=False, indent=2)
    return csv_path


def preferred_track_csv(session_dir: str | Path) -> Path:
    """Prefer human-reviewed tracks when available; otherwise fall back to pseudo tracks."""
    session_dir = Path(session_dir)
    reviewed = session_dir / "tracks_reviewed.csv"
    pseudo = session_dir / "tracks_pseudo.csv"
    if reviewed.exists():
        return reviewed
    if pseudo.exists():
        return pseudo
    raise FileNotFoundError(f"No tracks_reviewed.csv or tracks_pseudo.csv in {session_dir}")

from __future__ import annotations
from pathlib import Path
import cv2
import pandas as pd

from .core import add_tracklet_ids, attach_identity, expand_bbox_xyxy, load_identity_map, should_sample, validate_identity_temporal_consistency
from .d3 import body_track_purity_diagnostics


def build_d4(track_csv: str | Path, identity_map_path: str | Path, output_root: str | Path, cfg: dict) -> Path:
    tracks = pd.read_csv(track_csv)
    if tracks.empty:
        raise RuntimeError(f"No tracks in {track_csv}")
    identity_map = load_identity_map(identity_map_path)
    validate_identity_temporal_consistency(tracks, identity_map)
    tracks = attach_identity(tracks, identity_map)
    fps = float(tracks.iloc[0].fps)
    tracks = add_tracklet_ids(tracks, fps=fps, break_gap_sec=float(cfg.get("tracklet_break_gap_sec", 0.5)))
    class_id, session_id = str(tracks.iloc[0].class_id), str(tracks.iloc[0].session_id)
    video_path = Path(str(tracks.iloc[0].video_path))
    out_dir = Path(output_root) / "D4_REID" / class_id / session_id
    img_root = out_dir / "images"
    central_root = out_dir / "central_images"
    img_root.mkdir(parents=True, exist_ok=True)
    central_root.mkdir(parents=True, exist_ok=True)

    by_frame = {int(k): g for k, g in tracks.groupby("frame_index")}
    cap = cv2.VideoCapture(str(video_path))
    if not cap.isOpened():
        raise RuntimeError(f"Cannot open video: {video_path}")
    last_saved: dict[str, float] = {}
    rows = []
    frame_idx = 0
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        tdf = by_frame.get(frame_idx)
        if tdf is None:
            frame_idx += 1
            continue
        h, w = frame.shape[:2]
        purity_by_track = body_track_purity_diagnostics(tdf, cfg)
        for _, tr in tdf.iterrows():
            tracklet_id = str(tr.tracklet_id)
            ts = float(tr.timestamp_sec)
            if not should_sample(last_saved.get(tracklet_id), ts, float(cfg.get("sample_every_sec", 1.0))):
                continue
            try:
                x1, y1, x2, y2 = expand_bbox_xyxy(
                    [tr.x1, tr.y1, tr.x2, tr.y2], float(cfg.get("padding_ratio", 0.03)), w, h
                )
            except ValueError:
                continue
            crop = frame[y1:y2, x1:x2]
            if crop.shape[1] < int(cfg.get("min_width_px", 30)) or crop.shape[0] < int(cfg.get("min_height_px", 70)):
                continue
            sid = str(tr.student_id) if pd.notna(tr.student_id) and str(tr.student_id) else f"TRACK_{int(tr.track_id):05d}"
            state = "known" if pd.notna(tr.student_id) and str(tr.student_id) else "unassigned"
            rel_dir = Path(state) / sid / tracklet_id
            (img_root / rel_dir).mkdir(parents=True, exist_ok=True)
            filename = f"{class_id}_{session_id}_{tracklet_id}_F{frame_idx:08d}.jpg"
            img_path = img_root / rel_dir / filename
            cv2.imwrite(str(img_path), crop, [cv2.IMWRITE_JPEG_QUALITY, int(cfg.get("jpeg_quality", 95))])
            # Central-torso crop is a second, less crowd-sensitive ReID view. It deliberately
            # downweights the head/outer edges where neighbour contamination is most common.
            ch, cw = crop.shape[:2]
            cx1 = int(round(float(cfg.get("central_crop_x1", 0.14)) * cw))
            cx2 = int(round(float(cfg.get("central_crop_x2", 0.86)) * cw))
            cy1 = int(round(float(cfg.get("central_crop_y1", 0.18)) * ch))
            cy2 = int(round(float(cfg.get("central_crop_y2", 0.82)) * ch))
            central = crop[max(0,cy1):max(cy1+1,min(ch,cy2)), max(0,cx1):max(cx1+1,min(cw,cx2))]
            central_rel = ""
            if central.size and central.shape[1] >= 20 and central.shape[0] >= 40:
                central_dir = central_root / state / sid / tracklet_id
                central_dir.mkdir(parents=True, exist_ok=True)
                central_path = central_dir / filename
                cv2.imwrite(str(central_path), central, [cv2.IMWRITE_JPEG_QUALITY, int(cfg.get("jpeg_quality", 95))])
                central_rel = str(central_path.relative_to(out_dir))
            last_saved[tracklet_id] = ts
            pdiag = purity_by_track.get(int(tr.track_id), {})
            rows.append({
                "class_id": class_id, "session_id": session_id, "frame_index": frame_idx,
                "timestamp_sec": ts, "track_id": int(tr.track_id), "tracklet_id": tracklet_id,
                "student_id": tr.student_id if pd.notna(tr.student_id) else "",
                "identity_review_status": tr.identity_review_status,
                "x1": x1, "y1": y1, "x2": x2, "y2": y2,
                "body_purity_status": pdiag.get("body_purity_status", "UNKNOWN"),
                "body_purity_score": pdiag.get("body_purity_score", 0.0),
                "face_purity_status": pdiag.get("face_purity_status", "UNKNOWN"),
                "face_purity_score": pdiag.get("face_purity_score", 0.0),
                "body_purity_max_target_overlap": pdiag.get("body_purity_max_target_overlap", float("nan")),
                "body_purity_max_other_overlap": pdiag.get("body_purity_max_other_overlap", float("nan")),
                "body_purity_weighted_overlap": pdiag.get("body_purity_weighted_overlap", float("nan")),
                "body_purity_intruder_count": pdiag.get("body_purity_intruder_count", 0),
                "body_purity_head_intruder_count": pdiag.get("body_purity_head_intruder_count", 0),
                "image_path": str(img_path.relative_to(out_dir)),
                "central_image_path": central_rel,
            })
        frame_idx += 1
    cap.release()
    pd.DataFrame(rows).to_csv(out_dir / "metadata.csv", index=False)
    return out_dir

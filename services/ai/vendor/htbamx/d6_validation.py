from __future__ import annotations

from pathlib import Path
import hashlib
import json

import numpy as np
import pandas as pd

from .behavior_cache import BEHAVIORS, SEMANTIC_NAMES, ATOMIC_NAMES

BASE_REQUIRED = {
    "class_id", "session_id", "track_id", "tracklet_id", "segment_index",
    "segment_id", "start_sec", "end_sec", "video_path", "cache_path", "session_key",
}


def sha256_file(path: str | Path) -> str:
    h = hashlib.sha256()
    with Path(path).open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def validate_d6_manifest(manifest_csv: str | Path, full_cache_check: bool = True) -> dict:
    p = Path(manifest_csv)
    if not p.exists():
        raise FileNotFoundError(f"Không thấy D6 manifest: {p}")
    df = pd.read_csv(p)
    if df.empty:
        raise RuntimeError("D6 manifest rỗng")
    missing = BASE_REQUIRED - set(df.columns)
    if missing:
        raise ValueError(f"D6 manifest thiếu cột: {sorted(missing)}")
    if df["segment_id"].duplicated().any():
        dup = df.loc[df["segment_id"].duplicated(keep=False), "segment_id"].astype(str).tolist()[:10]
        raise ValueError(f"segment_id trùng: {dup}")
    missing_videos = [v for v in sorted(set(df["video_path"].astype(str))) if not Path(v).exists()]
    if missing_videos:
        raise FileNotFoundError(f"D6 manifest tham chiếu video không tồn tại: {missing_videos[:10]}")
    if df["cache_path"].duplicated().any():
        dup = df.loc[df["cache_path"].duplicated(keep=False), "cache_path"].astype(str).tolist()[:10]
        raise ValueError(f"cache_path trùng: {dup}")
    if (pd.to_numeric(df["end_sec"], errors="coerce") <= pd.to_numeric(df["start_sec"], errors="coerce")).any():
        raise ValueError("Có segment end_sec <= start_sec")
    if pd.to_numeric(df["segment_index"], errors="coerce").isna().any() or (pd.to_numeric(df["segment_index"]) < 0).any():
        raise ValueError("segment_index phải là số nguyên không âm")
    expected_session = df["class_id"].astype(str) + "__" + df["session_id"].astype(str)
    if not (expected_session == df["session_key"].astype(str)).all():
        raise ValueError("session_key không khớp class_id/session_id")

    cache_errors: list[str] = []
    frame_counts: list[int] = []
    if full_cache_check:
        for r in df.itertuples(index=False):
            cp = Path(str(r.cache_path))
            if not cp.exists():
                cache_errors.append(f"missing:{cp}")
                continue
            try:
                with np.load(cp) as z:
                    req = {"video", "semantic", "semantic_mask", "pose", "atomic", "atomic_mask", "frame_valid"}
                    miss = req - set(z.files)
                    if miss:
                        cache_errors.append(f"{cp}:missing_keys={sorted(miss)}")
                        continue
                    video = z["video"]
                    sem = z["semantic"]
                    sm = z["semantic_mask"]
                    pose = z["pose"]
                    atomic = z["atomic"]
                    am = z["atomic_mask"]
                    fv = z["frame_valid"]
                    if video.ndim != 4 or video.shape[-1] != 3:
                        raise ValueError(f"video shape {video.shape}")
                    f = int(video.shape[0]); frame_counts.append(f)
                    if sem.shape != (f, len(SEMANTIC_NAMES)):
                        raise ValueError(f"semantic shape {sem.shape}")
                    if sm.shape != sem.shape:
                        raise ValueError(f"semantic_mask shape {sm.shape}")
                    if pose.shape != (f, 33, 3):
                        raise ValueError(f"pose shape {pose.shape}")
                    if atomic.shape != (f, len(ATOMIC_NAMES)) or am.shape != atomic.shape:
                        raise ValueError(f"atomic shapes {atomic.shape}/{am.shape}")
                    if fv.shape != (f,):
                        raise ValueError(f"frame_valid shape {fv.shape}")
                    for name, arr in [("semantic", sem), ("semantic_mask", sm), ("pose", pose), ("atomic", atomic), ("atomic_mask", am), ("frame_valid", fv)]:
                        if not np.isfinite(arr).all():
                            raise ValueError(f"{name} contains NaN/Inf")
                    if ((sm < 0) | (sm > 1)).any() or ((am < 0) | (am > 1)).any() or ((fv < 0) | (fv > 1)).any():
                        raise ValueError("mask ngoài [0,1]")
            except Exception as e:
                cache_errors.append(f"{cp}:{type(e).__name__}:{e}")
        if cache_errors:
            raise RuntimeError("D6 cache validation failed:\n" + "\n".join(cache_errors[:20]))

    sessions = sorted(df["session_key"].astype(str).unique().tolist())
    return {
        "manifest": str(p.resolve()),
        "sha256": sha256_file(p),
        "segments": int(len(df)),
        "sessions": sessions,
        "num_sessions": len(sessions),
        "tracklets": int(df[["class_id", "session_id", "tracklet_id"]].drop_duplicates().shape[0]),
        "frame_count_min": int(min(frame_counts)) if frame_counts else None,
        "frame_count_max": int(max(frame_counts)) if frame_counts else None,
        "full_cache_check": bool(full_cache_check),
    }


def validate_training_manifest(manifest_csv: str | Path, require_test: bool = True) -> dict:
    base = validate_d6_manifest(manifest_csv, full_cache_check=False)
    df = pd.read_csv(manifest_csv)
    required = {"split", "pseudo_label", "gold_label", "label_source"}
    missing = required - set(df.columns)
    if missing:
        raise ValueError(f"Training manifest thiếu cột: {sorted(missing)}")
    allowed_splits = {"train", "val", "test"}
    bad_splits = sorted(set(df["split"].astype(str)) - allowed_splits)
    if bad_splits:
        raise ValueError(f"Split không hợp lệ: {bad_splits}")
    for col in ("pseudo_label", "gold_label"):
        vals = pd.to_numeric(df[col], errors="coerce")
        if vals.isna().any() or not vals.isin([-1, 0, 1, 2, 3]).all():
            raise ValueError(f"{col} chỉ được -1 hoặc 0..3")

    # Hard anti-leakage invariant: one session belongs to exactly one split.
    leakage = df.groupby("session_key")["split"].nunique()
    if (leakage > 1).any():
        raise RuntimeError(f"Session leakage: {leakage[leakage > 1].index.tolist()}")

    summary = []
    for split in ("train", "val", "test"):
        sub = df[df["split"] == split]
        if split in {"train", "val"} and sub.empty:
            raise RuntimeError(f"Split {split} rỗng")
        if split == "test" and require_test and sub.empty:
            raise RuntimeError("Split test rỗng")
        gold = int((sub["gold_label"] >= 0).sum()) if not sub.empty else 0
        pseudo = int((sub["pseudo_label"] >= 0).sum()) if not sub.empty else 0
        if split in {"val", "test"} and not sub.empty and gold == 0:
            raise RuntimeError(f"Split {split} không có Gold Label. Final evaluation tuyệt đối không dùng pseudo-only.")
        summary.append({
            "split": split,
            "segments": int(len(sub)),
            "sessions": int(sub["session_key"].nunique()) if not sub.empty else 0,
            "gold_segments": gold,
            "gold_coverage": float(gold / len(sub)) if len(sub) else 0.0,
            "pseudo_segments": pseudo,
        })
    base["split_summary"] = summary
    return base


def save_validation_report(report: dict, output_json: str | Path) -> Path:
    p = Path(output_json)
    p.parent.mkdir(parents=True, exist_ok=True)
    with p.open("w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
    return p

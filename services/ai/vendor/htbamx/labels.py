from __future__ import annotations

from pathlib import Path
import math
import shutil

import numpy as np
import pandas as pd

from htbam_builder.core import split_session_keys
from .behavior_cache import BEHAVIORS, BEHAVIOR_TO_ID, SEMANTIC_NAMES

REVIEWED_VALUES = {"REVIEWED", "APPROVED", "CONFIRMED", "OK"}
SKIP_VALUES = {"SKIP", "IGNORE", "UNOBSERVABLE"}


def _masked_mean(values: np.ndarray, mask: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    values = np.asarray(values, dtype=np.float32)
    mask = np.asarray(mask, dtype=np.float32)
    if values.shape != mask.shape or values.ndim != 2 or values.shape[1] != len(SEMANTIC_NAMES):
        raise ValueError(f"semantic/mask shape không hợp lệ: {values.shape}/{mask.shape}")
    denom = np.maximum(mask.sum(axis=0), 1e-6)
    means = (values * mask).sum(axis=0) / denom
    valid_ratio = mask.mean(axis=0)
    return means, valid_ratio


def infer_pseudo_label(semantic: np.ndarray, semantic_mask: np.ndarray, min_observability: float = 0.20) -> dict:
    """Generate a conservative bootstrap label from D6 semantic evidence.

    This is intentionally NOT ground truth. The returned confidence is only a
    heuristic review priority score, not a calibrated probability.
    """
    means, valid_ratio = _masked_mean(semantic, semantic_mask)
    obs = float(np.asarray(semantic_mask, dtype=np.float32).mean())
    if obs < float(min_observability):
        return {
            "pseudo_label": -1,
            "behavior": "UNOBSERVABLE",
            "confidence": 0.0,
            "observability": obs,
            "reason": "observability_below_threshold",
        }
    d = dict(zip(SEMANTIC_NAMES, means.tolist()))
    valid = dict(zip(SEMANTIC_NAMES, valid_ratio.tolist()))

    face_available = valid.get("face_visible", 0.0) >= 0.25
    phone = float(np.clip(d.get("phone_use", 0.0), 0.0, 1.0))
    hand = float(np.clip(d.get("hand_up", 0.0), 0.0, 1.0))
    motion = float(np.clip(d.get("motion", 0.0), 0.0, 1.0))
    eye_open = float(np.clip(d.get("eye_open", 0.5), 0.0, 1.0)) if face_available else 0.5
    eyes_closed = 1.0 - eye_open if face_available else 0.0
    yaw = abs(float(d.get("yaw", 0.0)))
    gaze = float(np.clip(d.get("gaze_away", 0.0), 0.0, 1.0)) if valid.get("gaze_away", 0.0) > 0 else 0.0
    head_away = max(min(yaw / 45.0, 1.0), gaze)
    pitch = float(d.get("pitch", 0.0))
    head_down = min(max(pitch, 0.0) / 35.0, 1.0) if face_available else 0.0

    # Soft heuristic scores. They exist only to sort what the human should
    # review first; final report evaluation must use Gold labels.
    scores = {
        "Distracted": max(phone, 0.70 * head_away * (1.0 - 0.20 * motion)),
        "Sleepy": (0.60 * eyes_closed + 0.25 * head_down + 0.15 * (1.0 - motion)) if face_available else 0.0,
        "Active": max(0.70 * hand, 0.45 * motion),
        "Focused": 0.40 * (1.0 - phone) + 0.25 * (1.0 - head_away) + 0.20 * eye_open + 0.15 * (1.0 - min(motion, 1.0)),
    }
    # Preserve the domain-specific priority of highly specific evidence.
    if phone >= 0.25:
        winner, reason = "Distracted", "phone_evidence"
    elif face_available and eyes_closed >= 0.55 and (head_down >= 0.35 or motion < 0.12):
        winner, reason = "Sleepy", "eyes_closed_head_down_or_low_motion"
    elif hand >= 0.25:
        winner, reason = "Active", "hand_raise_evidence"
    elif head_away >= 0.65 and motion < 0.30:
        winner, reason = "Distracted", "sustained_head_or_gaze_away"
    else:
        winner = max(scores, key=scores.get)
        reason = "soft_score"

    ordered = sorted(scores.values(), reverse=True)
    margin = max(0.0, ordered[0] - ordered[1]) if len(ordered) > 1 else ordered[0]
    confidence = float(np.clip(0.50 * scores[winner] + 0.30 * margin + 0.20 * obs, 0.0, 1.0))
    return {
        "pseudo_label": int(BEHAVIOR_TO_ID[winner]),
        "behavior": winner,
        "confidence": confidence,
        "observability": obs,
        "reason": reason,
    }


def generate_pseudo_labels(
    manifest_csv: str | Path,
    output_csv: str | Path,
    min_observability: float = 0.20,
    review_template_csv: str | Path | None = None,
    overwrite_review_template: bool = False,
) -> Path:
    manifest_csv = Path(manifest_csv)
    df = pd.read_csv(manifest_csv)
    required = {"segment_id", "class_id", "session_id", "track_id", "tracklet_id", "start_sec", "end_sec", "video_path", "cache_path"}
    missing = required - set(df.columns)
    if missing:
        raise ValueError(f"D6 manifest thiếu cột: {sorted(missing)}")
    if df["segment_id"].duplicated().any():
        raise ValueError("D6 manifest có segment_id trùng; không thể gán nhãn an toàn")

    rows = []
    for r in df.itertuples(index=False):
        cache = Path(str(r.cache_path))
        if not cache.exists():
            raise FileNotFoundError(f"Thiếu D6 cache: {cache}")
        with np.load(cache) as z:
            if "semantic" not in z.files or "semantic_mask" not in z.files:
                raise ValueError(f"D6 cache thiếu semantic/semantic_mask: {cache}")
            pred = infer_pseudo_label(z["semantic"], z["semantic_mask"], min_observability)
        rows.append({
            "segment_id": str(r.segment_id),
            "class_id": str(r.class_id),
            "session_id": str(r.session_id),
            "track_id": int(r.track_id),
            "tracklet_id": str(r.tracklet_id),
            "stable_person_id": str(getattr(r, "stable_person_id", "") or ""),
            "resolved_student_id": str(getattr(r, "resolved_student_id", "") or ""),
            "start_sec": float(r.start_sec),
            "end_sec": float(r.end_sec),
            "video_path": str(r.video_path),
            "pseudo_label": int(pred["pseudo_label"]),
            "suggested_behavior": str(pred["behavior"]),
            "pseudo_confidence": float(pred["confidence"]),
            "observability": float(pred["observability"]),
            "pseudo_reason": str(pred["reason"]),
        })
    out = Path(output_csv)
    out.parent.mkdir(parents=True, exist_ok=True)
    pseudo = pd.DataFrame(rows).sort_values(["class_id", "session_id", "tracklet_id", "start_sec"])
    pseudo.to_csv(out, index=False)

    if review_template_csv:
        rt = Path(review_template_csv)
        review_cols = [c for c in ["segment_id", "class_id", "session_id", "track_id", "tracklet_id", "stable_person_id", "resolved_student_id", "start_sec", "end_sec", "video_path", "suggested_behavior", "pseudo_confidence"] if c in pseudo.columns]
        current = pseudo[review_cols].copy()
        current["behavior"] = current["suggested_behavior"]
        current["review_status"] = "PENDING"
        current["review_note"] = ""
        if overwrite_review_template or not rt.exists():
            review = current
        else:
            # Incremental-safe sync: keep human edits for existing segments and append only new segments.
            old_review = pd.read_csv(rt)
            if old_review["segment_id"].duplicated().any():
                raise ValueError("Gold review hiện tại có segment_id trùng")
            keep_cols = [c for c in ["segment_id", "behavior", "review_status", "review_note"] if c in old_review.columns]
            preserved = old_review[keep_cols].copy()
            review = current.merge(preserved, on="segment_id", how="left", suffixes=("", "_old"), validate="one_to_one")
            for col in ["behavior", "review_status", "review_note"]:
                old_col = f"{col}_old"
                if old_col in review.columns:
                    mask = review[old_col].notna()
                    review.loc[mask, col] = review.loc[mask, old_col]
                    review = review.drop(columns=[old_col])
        rt.parent.mkdir(parents=True, exist_ok=True)
        review = review.sort_values(["class_id", "session_id", "tracklet_id", "start_sec"])
        review.to_csv(rt, index=False)
    return out


def validate_gold_review(
    review_csv: str | Path,
    manifest_csv: str | Path,
    output_gold_csv: str | Path,
) -> Path:
    review_csv, manifest_csv = Path(review_csv), Path(manifest_csv)
    if not review_csv.exists():
        raise FileNotFoundError(f"Chưa có file review Gold: {review_csv}")
    review = pd.read_csv(review_csv)
    manifest = pd.read_csv(manifest_csv)
    required = {"segment_id", "class_id", "session_id", "track_id", "tracklet_id", "start_sec", "end_sec", "behavior", "review_status"}
    missing = required - set(review.columns)
    if missing:
        raise ValueError(f"Gold review thiếu cột: {sorted(missing)}")
    if review["segment_id"].duplicated().any():
        dup = review.loc[review["segment_id"].duplicated(keep=False), "segment_id"].tolist()[:10]
        raise ValueError(f"Gold review có segment_id trùng: {dup}")
    known = set(manifest["segment_id"].astype(str))
    unknown = sorted(set(review["segment_id"].astype(str)) - known)
    if unknown:
        raise ValueError(f"Gold review chứa segment không có trong D6: {unknown[:10]}")

    canonical = []
    for r in review.itertuples(index=False):
        status = str(r.review_status).strip().upper()
        if status in SKIP_VALUES or status in {"", "PENDING", "TODO", "UNREVIEWED", "NAN"}:
            continue
        if status not in REVIEWED_VALUES:
            raise ValueError(f"review_status không hợp lệ tại {r.segment_id}: {r.review_status}")
        behavior = str(r.behavior).strip()
        if behavior not in BEHAVIORS:
            raise ValueError(f"Behavior không hợp lệ tại {r.segment_id}: {behavior}; chỉ dùng {BEHAVIORS}")
        if not math.isfinite(float(r.start_sec)) or not math.isfinite(float(r.end_sec)) or float(r.end_sec) <= float(r.start_sec):
            raise ValueError(f"Khoảng thời gian Gold lỗi tại {r.segment_id}")
        canonical.append({
            "segment_id": str(r.segment_id),
            "class_id": str(r.class_id),
            "session_id": str(r.session_id),
            "track_id": int(r.track_id),
            "tracklet_id": str(r.tracklet_id),
            "start_sec": float(r.start_sec),
            "end_sec": float(r.end_sec),
            "video_path": str(getattr(r, "video_path", "")),
            "behavior": behavior,
            "gold_label": int(BEHAVIOR_TO_ID[behavior]),
            "review_status": "REVIEWED",
            "review_note": str(getattr(r, "review_note", "")) if not pd.isna(getattr(r, "review_note", "")) else "",
        })
    if not canonical:
        raise RuntimeError("Chưa có dòng nào review_status=REVIEWED; chưa thể tạo Gold Label")
    gold = pd.DataFrame(canonical).sort_values(["class_id", "session_id", "tracklet_id", "start_sec"])
    out = Path(output_gold_csv)
    out.parent.mkdir(parents=True, exist_ok=True)
    gold.to_csv(out, index=False)
    return out


def build_training_manifest(
    manifest_csv: str | Path,
    pseudo_csv: str | Path,
    gold_csv: str | Path,
    output_csv: str | Path,
    split_cfg: dict,
) -> Path:
    base = pd.read_csv(manifest_csv)
    pseudo = pd.read_csv(pseudo_csv)
    gold_path = Path(gold_csv)
    gold = pd.read_csv(gold_path) if gold_path.exists() and gold_path.stat().st_size else pd.DataFrame(columns=["segment_id", "gold_label", "behavior"])

    if base["segment_id"].duplicated().any():
        raise ValueError("Base D6 manifest có segment_id trùng")
    if pseudo["segment_id"].duplicated().any():
        raise ValueError("Pseudo label có segment_id trùng")
    if not gold.empty and gold["segment_id"].duplicated().any():
        raise ValueError("Gold label có segment_id trùng")

    pseudo_cols = [c for c in ["segment_id", "pseudo_label", "suggested_behavior", "pseudo_confidence", "pseudo_reason"] if c in pseudo.columns]
    df = base.merge(pseudo[pseudo_cols], on="segment_id", how="left", validate="one_to_one")
    df["pseudo_label"] = pd.to_numeric(df.get("pseudo_label", -1), errors="coerce").fillna(-1).astype(int)
    if not gold.empty:
        g = gold[["segment_id", "gold_label", "behavior"]].rename(columns={"behavior": "gold_behavior"})
        df = df.merge(g, on="segment_id", how="left", validate="one_to_one")
    if "gold_label" not in df.columns:
        df["gold_label"] = -1
    df["gold_label"] = pd.to_numeric(df["gold_label"], errors="coerce").fillna(-1).astype(int)
    df["label_source"] = np.where(df["gold_label"] >= 0, "GOLD", np.where(df["pseudo_label"] >= 0, "PSEUDO", "UNLABELED"))

    session_keys = df["class_id"].astype(str) + "__" + df["session_id"].astype(str)
    mapping = split_session_keys(
        session_keys.tolist(), float(split_cfg["train_ratio"]), float(split_cfg["val_ratio"]),
        float(split_cfg["test_ratio"]), int(split_cfg.get("seed", 2026)),
    )
    df["session_key"] = session_keys
    df["split"] = df["session_key"].map(mapping)
    if df["split"].isna().any():
        raise RuntimeError("Có session chưa được gán split")

    # Val/Test are never allowed to silently fall back to pseudo labels.
    for split in ("val", "test"):
        sub = df[df["split"] == split]
        if sub.empty:
            raise RuntimeError(f"Split {split} rỗng. Cần ít nhất 3 session; với 4 session sẽ là 2/1/1.")
        n_gold = int((sub["gold_label"] >= 0).sum())
        if n_gold == 0:
            sessions = sorted(sub["session_key"].unique().tolist())
            raise RuntimeError(f"Split {split} chưa có Gold Label. Hãy review ít nhất các session: {sessions}")

    out = Path(output_csv)
    out.parent.mkdir(parents=True, exist_ok=True)
    df.sort_values(["split", "class_id", "session_id", "tracklet_id", "segment_index"]).to_csv(out, index=False)

    split_dir = out.parent / "splits"
    split_dir.mkdir(parents=True, exist_ok=True)
    pd.DataFrame(sorted(mapping.items()), columns=["session_key", "split"]).to_csv(split_dir / "session_split.csv", index=False)
    for split in ("train", "val", "test"):
        df[df["split"] == split].to_csv(split_dir / f"{split}.csv", index=False)
    return out

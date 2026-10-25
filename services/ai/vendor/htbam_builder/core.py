from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Sequence
import hashlib
import math
import random

import numpy as np
import pandas as pd


VIDEO_EXTENSIONS = {".mp4", ".mov", ".avi", ".mkv", ".m4v"}


@dataclass(frozen=True)
class SessionRef:
    class_id: str
    session_id: str
    video_path: Path

    @property
    def key(self) -> str:
        return f"{self.class_id}__{self.session_id}"


def scan_raw_sessions(raw_root: str | Path) -> list[SessionRef]:
    root = Path(raw_root)
    if not root.exists():
        raise FileNotFoundError(f"RAW root not found: {root}")
    sessions: list[SessionRef] = []
    for class_dir in sorted(p for p in root.iterdir() if p.is_dir()):
        for session_dir in sorted(p for p in class_dir.iterdir() if p.is_dir()):
            videos = sorted(
                p for p in session_dir.iterdir()
                if p.is_file() and p.suffix.lower() in VIDEO_EXTENSIONS
            )
            if not videos:
                continue
            for video in videos:
                # If one session contains multiple cameras, keep the camera filename in key via stem.
                session_id = session_dir.name if len(videos) == 1 else f"{session_dir.name}__{video.stem}"
                sessions.append(SessionRef(class_dir.name, session_id, video))
    if not sessions:
        raise RuntimeError(
            f"No videos found under {root}. Expected RAW/CLASS_x/SESSION_x/video.mp4"
        )
    return sessions


def clip_bbox_xyxy(
    x1: float, y1: float, x2: float, y2: float, width: int, height: int
) -> tuple[int, int, int, int]:
    if width <= 0 or height <= 0:
        raise ValueError("width and height must be positive")
    xa = max(0, min(width - 1, int(math.floor(x1))))
    ya = max(0, min(height - 1, int(math.floor(y1))))
    xb = max(0, min(width, int(math.ceil(x2))))
    yb = max(0, min(height, int(math.ceil(y2))))
    if xb <= xa or yb <= ya:
        raise ValueError(f"Invalid bbox after clipping: {(xa, ya, xb, yb)}")
    return xa, ya, xb, yb


def expand_bbox_xyxy(
    bbox: Sequence[float], padding_ratio: float, width: int, height: int
) -> tuple[int, int, int, int]:
    x1, y1, x2, y2 = map(float, bbox)
    bw, bh = x2 - x1, y2 - y1
    pad_x, pad_y = bw * padding_ratio, bh * padding_ratio
    return clip_bbox_xyxy(x1 - pad_x, y1 - pad_y, x2 + pad_x, y2 + pad_y, width, height)


def should_sample(last_time: float | None, current_time: float, every_sec: float) -> bool:
    if every_sec <= 0:
        return True
    if last_time is None:
        return True
    return current_time - last_time >= every_sec - 1e-9


def add_tracklet_ids(df: pd.DataFrame, fps: float, break_gap_sec: float) -> pd.DataFrame:
    """Split each tracker ID into contiguous tracklets without changing the tracker ID.

    This avoids windows/crops silently crossing a long period where the track was absent.
    """
    required = {"track_id", "frame_index"}
    if not required.issubset(df.columns):
        raise ValueError(f"Missing columns: {sorted(required - set(df.columns))}")
    if fps <= 0:
        raise ValueError("fps must be > 0")
    max_gap_frames = max(1, int(round(fps * break_gap_sec)))
    out = df.copy().sort_values(["track_id", "frame_index"]).reset_index(drop=True)
    tracklet_ids: list[str] = [""] * len(out)
    for track_id, idxs in out.groupby("track_id", sort=False).groups.items():
        segment = 0
        prev_frame = None
        for idx in idxs:
            frame = int(out.at[idx, "frame_index"])
            if prev_frame is not None and frame - prev_frame > max_gap_frames:
                segment += 1
            tracklet_ids[idx] = f"T{int(track_id):05d}_{segment:03d}"
            prev_frame = frame
    out["tracklet_id"] = tracklet_ids
    return out


def load_identity_map(path: str | Path) -> pd.DataFrame:
    path = Path(path)
    cols = ["class_id", "session_id", "track_id", "student_id", "review_status"]
    if not path.exists():
        return pd.DataFrame(columns=cols)
    df = pd.read_csv(path, dtype={"class_id": str, "session_id": str, "student_id": str})
    missing = [c for c in cols if c not in df.columns]
    if missing:
        raise ValueError(f"identity_map missing columns: {missing}")
    if df.empty:
        return df[cols]
    df["track_id"] = pd.to_numeric(df["track_id"], errors="raise").astype(int)
    dup = df.groupby(["class_id", "session_id", "track_id"])["student_id"].nunique()
    bad = dup[dup > 1]
    if not bad.empty:
        raise ValueError(
            "One track_id maps to multiple student_id values. Fix identity_map first: "
            + str(bad.index.tolist()[:10])
        )
    return df[cols].drop_duplicates()


def attach_identity(tracks: pd.DataFrame, identity_map: pd.DataFrame) -> pd.DataFrame:
    out = tracks.copy()
    if identity_map.empty:
        out["student_id"] = pd.NA
        out["identity_review_status"] = "UNASSIGNED"
        return out
    m = identity_map.rename(columns={"review_status": "identity_review_status"})
    out = out.merge(
        m[["class_id", "session_id", "track_id", "student_id", "identity_review_status"]],
        on=["class_id", "session_id", "track_id"],
        how="left",
        validate="many_to_one",
    )
    out["identity_review_status"] = out["identity_review_status"].fillna("UNASSIGNED")
    return out



def validate_identity_temporal_consistency(tracks: pd.DataFrame, identity_map: pd.DataFrame) -> None:
    """Reject impossible reviewed mappings where one student occupies two tracks at once.

    Multiple track IDs for one student are valid for re-entry, but their frame ranges should not
    overlap in the same class/session. Unassigned or non-reviewed mappings are ignored.
    """
    if tracks.empty or identity_map.empty:
        return
    approved = identity_map[identity_map["review_status"].astype(str).str.upper().isin({"APPROVED","OK","REVIEWED","CONFIRMED"})].copy()
    if approved.empty:
        return
    stats = tracks.groupby(["class_id","session_id","track_id"]).frame_index.agg(["min","max"]).reset_index()
    mapped = approved.merge(stats,on=["class_id","session_id","track_id"],how="left",validate="many_to_one")
    mapped = mapped.dropna(subset=["min","max"])
    for (class_id,session_id,student_id), g in mapped.groupby(["class_id","session_id","student_id"]):
        rows = list(g.sort_values("min").itertuples(index=False))
        for i in range(len(rows)):
            for j in range(i+1,len(rows)):
                a,b=rows[i],rows[j]
                if int(b.min) > int(a.max):
                    break
                if int(a.track_id) != int(b.track_id):
                    raise ValueError(
                        f"Identity mapping conflict: {student_id} is assigned to overlapping tracks "
                        f"{a.track_id} and {b.track_id} in {class_id}/{session_id}."
                    )

def stable_hash(value: str, seed: int = 0) -> int:
    return int(hashlib.sha256(f"{seed}:{value}".encode("utf-8")).hexdigest()[:16], 16)


def split_session_keys(
    keys: Sequence[str], train_ratio: float, val_ratio: float, test_ratio: float, seed: int
) -> dict[str, str]:
    total = train_ratio + val_ratio + test_ratio
    if not math.isclose(total, 1.0, rel_tol=0, abs_tol=1e-6):
        raise ValueError(f"Split ratios must sum to 1.0, got {total}")
    unique = sorted(set(keys), key=lambda k: stable_hash(k, seed))
    n = len(unique)
    if n == 0:
        return {}
    # Largest-remainder allocation, but force at least one test/val when enough sessions exist.
    raw = np.array([train_ratio, val_ratio, test_ratio], dtype=float) * n
    base = np.floor(raw).astype(int)
    remain = n - int(base.sum())
    for i in np.argsort(-(raw - base))[:remain]:
        base[i] += 1
    if n >= 3:
        for idx in (1, 2):
            if base[idx] == 0:
                donor = int(np.argmax(base))
                if base[donor] > 1:
                    base[donor] -= 1
                    base[idx] += 1
    n_train, n_val, n_test = map(int, base)
    mapping: dict[str, str] = {}
    for k in unique[:n_train]:
        mapping[k] = "train"
    for k in unique[n_train:n_train+n_val]:
        mapping[k] = "val"
    for k in unique[n_train+n_val:n_train+n_val+n_test]:
        mapping[k] = "test"
    return mapping


def cosine_similarity(a: np.ndarray, b: np.ndarray) -> float:
    a = np.asarray(a, dtype=np.float64).ravel()
    b = np.asarray(b, dtype=np.float64).ravel()
    denom = np.linalg.norm(a) * np.linalg.norm(b)
    if denom <= 1e-12:
        return 0.0
    return float(np.dot(a, b) / denom)

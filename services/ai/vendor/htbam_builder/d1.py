from __future__ import annotations
from pathlib import Path
import json
import pandas as pd


def validate_track_df(df: pd.DataFrame) -> None:
    required = {"frame_index", "track_id", "x1", "y1", "x2", "y2", "confidence", "class_id", "session_id"}
    missing = required - set(df.columns)
    if missing:
        raise ValueError(f"Track CSV missing columns: {sorted(missing)}")
    if df.empty:
        return
    if (df[["x2", "y2"]].values <= df[["x1", "y1"]].values).any():
        raise ValueError("Invalid bbox found: x2/y2 must be larger than x1/y1")
    if (df["frame_index"] < 0).any() or (df["track_id"] < 0).any():
        raise ValueError("frame_index and track_id must be non-negative")


def to_motchallenge(df: pd.DataFrame) -> pd.DataFrame:
    validate_track_df(df)
    if df.empty:
        return pd.DataFrame(columns=["frame","id","x","y","w","h","conf","class","visibility"])
    out = pd.DataFrame({
        "frame": df["frame_index"].astype(int) + 1,  # MOT is 1-based
        "id": df["track_id"].astype(int),
        "x": df["x1"].astype(float),
        "y": df["y1"].astype(float),
        "w": (df["x2"] - df["x1"]).astype(float),
        "h": (df["y2"] - df["y1"]).astype(float),
        "conf": df["confidence"].astype(float),
        "class": 1,
        "visibility": 1.0,
    })
    return out.sort_values(["frame", "id"]).reset_index(drop=True)


def build_d1(track_csv: str | Path, output_root: str | Path) -> Path:
    track_csv = Path(track_csv)
    df = pd.read_csv(track_csv)
    validate_track_df(df)
    if df.empty:
        raise RuntimeError(f"No person tracks in {track_csv}")
    class_id = str(df.iloc[0]["class_id"])
    session_id = str(df.iloc[0]["session_id"])
    out_dir = Path(output_root) / "D1_MOT" / class_id / session_id
    (out_dir / "annotations").mkdir(parents=True, exist_ok=True)
    reviewed = "reviewed" in track_csv.name.lower()
    track_name = "tracks_gt.csv" if reviewed else "tracks_pseudo.csv"
    mot_name = "mot_gt.txt" if reviewed else "mot_pseudo.txt"
    df.to_csv(out_dir / "annotations" / track_name, index=False)
    mot = to_motchallenge(df)
    mot.to_csv(out_dir / "annotations" / mot_name, index=False, header=False)
    manifest = {
        "class_id": class_id,
        "session_id": session_id,
        "video_path": str(df.iloc[0].get("video_path", "")),
        "annotation_status": "REVIEWED_GT" if reviewed else "PSEUDO_NEEDS_REVIEW",
        "warning": "" if reviewed else "Do not report MOT metrics against this file as GT until IDs/bboxes are reviewed.",
    }
    with (out_dir / "manifest.json").open("w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
    return out_dir

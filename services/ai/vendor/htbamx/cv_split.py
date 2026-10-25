from __future__ import annotations

from pathlib import Path
import json
import re

import numpy as np
import pandas as pd
from sklearn.model_selection import GroupShuffleSplit

from .behavior_cache import BEHAVIORS


def _session_key(df: pd.DataFrame) -> pd.Series:
    return df["class_id"].astype(str) + "__" + df["session_id"].astype(str)


def _tracklet_key(df: pd.DataFrame) -> pd.Series:
    return (
        df["class_id"].astype(str)
        + "__"
        + df["session_id"].astype(str)
        + "__"
        + df["tracklet_id"].astype(str)
    )


def _counts(df: pd.DataFrame) -> list[int]:
    y = pd.to_numeric(df["gold_label"], errors="coerce").fillna(-1).astype(int)
    return [int((y == i).sum()) for i in range(len(BEHAVIORS))]


def _safe_name(s: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", str(s)).strip("_")


def _merge_manifest(
    d6_manifest_csv: str | Path,
    pseudo_csv: str | Path,
    gold_csv: str | Path,
) -> pd.DataFrame:
    base = pd.read_csv(d6_manifest_csv)
    if base["segment_id"].duplicated().any():
        raise ValueError("D6 manifest có segment_id trùng")

    pseudo_path = Path(pseudo_csv)
    if pseudo_path.exists() and pseudo_path.stat().st_size:
        pseudo = pd.read_csv(pseudo_path)
        if pseudo["segment_id"].duplicated().any():
            raise ValueError("Pseudo labels có segment_id trùng")
        keep = [
            c for c in
            ["segment_id", "pseudo_label", "suggested_behavior",
             "pseudo_confidence", "pseudo_reason", "observability"]
            if c in pseudo.columns
        ]
        base = base.merge(pseudo[keep], on="segment_id", how="left", validate="one_to_one")

    if "pseudo_label" not in base.columns:
        base["pseudo_label"] = -1
    base["pseudo_label"] = pd.to_numeric(
        base["pseudo_label"], errors="coerce"
    ).fillna(-1).astype(int)

    gold = pd.read_csv(gold_csv)
    if gold.empty:
        raise RuntimeError("gold_labels.csv rỗng")
    if gold["segment_id"].duplicated().any():
        raise ValueError("Gold labels có segment_id trùng")

    gcols = ["segment_id", "gold_label"]
    if "behavior" in gold.columns:
        gcols.append("behavior")
    g = gold[gcols].copy()
    if "behavior" in g.columns:
        g = g.rename(columns={"behavior": "gold_behavior"})
    base = base.merge(g, on="segment_id", how="left", validate="one_to_one")

    base["gold_label"] = pd.to_numeric(
        base["gold_label"], errors="coerce"
    ).fillna(-1).astype(int)
    base["label_source"] = np.where(base["gold_label"] >= 0, "GOLD", "UNLABELED")
    base["session_key"] = _session_key(base)
    base["tracklet_key"] = _tracklet_key(base)

    # Only keep tracklets that contain at least one Human Gold segment.
    # Unreviewed segments inside those tracklets remain as context with gold_label=-1.
    reviewed_tracklets = set(
        base.loc[base["gold_label"] >= 0, "tracklet_key"].astype(str)
    )
    base = base[base["tracklet_key"].astype(str).isin(reviewed_tracklets)].copy()

    if base.empty:
        raise RuntimeError("Không còn tracklet nào chứa Human Gold")

    return base


def _choose_tracklet_val_split(
    gold_dev: pd.DataFrame,
    val_ratio: float,
    seed: int,
    trials: int,
) -> tuple[set[str], dict]:
    if not 0.05 <= float(val_ratio) <= 0.40:
        raise ValueError("val_ratio_within_dev phải trong [0.05, 0.40]")

    groups = gold_dev["tracklet_key"].astype(str).to_numpy()
    y = gold_dev["gold_label"].astype(int).to_numpy()

    unique_groups = np.unique(groups)
    if len(unique_groups) < 2:
        raise RuntimeError("Development pool có ít hơn 2 tracklet; không thể chia train/val")

    global_counts = np.bincount(y, minlength=len(BEHAVIORS)).astype(float)
    global_prop = global_counts / max(global_counts.sum(), 1.0)

    best = None
    best_score = float("inf")

    for i in range(max(int(trials), 50)):
        splitter = GroupShuffleSplit(
            n_splits=1,
            test_size=float(val_ratio),
            random_state=int(seed) + i,
        )
        tr_idx, va_idx = next(splitter.split(gold_dev, y, groups))

        tr_y, va_y = y[tr_idx], y[va_idx]
        tr_counts = np.bincount(tr_y, minlength=len(BEHAVIORS))
        va_counts = np.bincount(va_y, minlength=len(BEHAVIORS))

        # Training must contain all four behavior classes.
        if np.any(tr_counts == 0):
            continue

        va_prop = va_counts / max(va_counts.sum(), 1)
        ratio_err = abs(len(va_idx) / len(gold_dev) - float(val_ratio))
        class_err = float(np.abs(va_prop - global_prop).mean())
        missing_val = int((va_counts == 0).sum())

        # Prefer validation containing all classes when possible, but do not
        # sacrifice train class coverage.
        score = ratio_err + class_err + 0.75 * missing_val

        if score < best_score:
            best_score = score
            best = (tr_idx, va_idx, tr_counts, va_counts)

    if best is None:
        raise RuntimeError(
            "Không tìm được tracklet-group split có đủ 4 class trong train. "
            "Không được hạ điều kiện này để ép train."
        )

    tr_idx, va_idx, tr_counts, va_counts = best
    val_groups = set(gold_dev.iloc[va_idx]["tracklet_key"].astype(str))

    report = {
        "gold_dev_rows": int(len(gold_dev)),
        "train_gold_counts": {BEHAVIORS[i]: int(tr_counts[i]) for i in range(len(BEHAVIORS))},
        "val_gold_counts": {BEHAVIORS[i]: int(va_counts[i]) for i in range(len(BEHAVIORS))},
        "val_gold_ratio": float(len(va_idx) / max(len(gold_dev), 1)),
        "val_tracklets": int(len(val_groups)),
        "all_dev_tracklets": int(gold_dev["tracklet_key"].nunique()),
        "val_missing_classes": [BEHAVIORS[i] for i, c in enumerate(va_counts) if c == 0],
    }
    return val_groups, report


def validate_cv_manifest(manifest_csv: str | Path, require_test: bool = True) -> dict:
    p = Path(manifest_csv)
    if not p.is_file():
        raise FileNotFoundError(p)

    df = pd.read_csv(p)
    required = {
        "segment_id", "class_id", "session_id", "tracklet_id",
        "segment_index", "cache_path", "pseudo_label", "gold_label", "split",
    }
    missing = required - set(df.columns)
    if missing:
        raise ValueError(f"CV manifest thiếu cột: {sorted(missing)}")
    if df["segment_id"].duplicated().any():
        raise ValueError("CV manifest có segment_id trùng")

    allowed = {"train", "val", "test"}
    bad = sorted(set(df["split"].astype(str)) - allowed)
    if bad:
        raise ValueError(f"CV manifest có split không hợp lệ: {bad}")

    if require_test and not (df["split"] == "test").any():
        raise RuntimeError("CV manifest không có test split")

    sk = _session_key(df)
    tk = _tracklet_key(df)
    df = df.copy()
    df["_session_key"] = sk
    df["_tracklet_key"] = tk

    # No tracklet can cross train/val/test.
    leak = df.groupby("_tracklet_key")["split"].nunique()
    leaking_tracklets = leak[leak > 1]
    if len(leaking_tracklets):
        raise RuntimeError(
            f"Tracklet leakage giữa split: {leaking_tracklets.index[:10].tolist()}"
        )

    test_sessions = set(df.loc[df["split"] == "test", "_session_key"].astype(str))
    dev_sessions = set(df.loc[df["split"].isin(["train", "val"]), "_session_key"].astype(str))
    overlap = sorted(test_sessions & dev_sessions)
    if overlap:
        raise RuntimeError(f"Outer-test session leakage: {overlap}")
    if len(test_sessions) != 1:
        raise RuntimeError(f"Mỗi LOSO fold phải có đúng 1 test session, hiện có: {sorted(test_sessions)}")

    gold = df[df["gold_label"] >= 0].copy()
    summaries = []
    for split in ("train", "val", "test"):
        sub = gold[gold["split"] == split]
        counts = _counts(sub)
        summaries.append({
            "split": split,
            "rows": int((df["split"] == split).sum()),
            "gold_segments": int(len(sub)),
            **{f"gold_{BEHAVIORS[i]}": int(counts[i]) for i in range(len(BEHAVIORS))},
        })
        if len(sub) == 0:
            raise RuntimeError(f"Split {split} không có Human Gold")

    train_counts = _counts(gold[gold["split"] == "train"])
    if any(c == 0 for c in train_counts):
        missing_train = [BEHAVIORS[i] for i, c in enumerate(train_counts) if c == 0]
        raise RuntimeError(f"Train split thiếu class {missing_train}")

    return {
        "scheme": "3-fold LOSO outer test + tracklet-group inner validation",
        "manifest": str(p),
        "rows": int(len(df)),
        "gold_segments": int((df["gold_label"] >= 0).sum()),
        "test_session": sorted(test_sessions)[0],
        "development_sessions": sorted(dev_sessions),
        "split_summary": summaries,
        "tracklet_leakage": 0,
        "outer_test_session_leakage": 0,
    }


def build_loso_cv_manifests(
    d6_manifest_csv: str | Path,
    pseudo_csv: str | Path,
    gold_csv: str | Path,
    output_dir: str | Path,
    *,
    val_ratio_within_dev: float = 0.20,
    seed: int = 2026,
    split_trials: int = 2000,
) -> Path:
    out_root = Path(output_dir)
    out_root.mkdir(parents=True, exist_ok=True)

    # Replace current fold manifests; do not keep stale CV outputs.
    for old in out_root.glob("fold_*"):
        if old.is_dir():
            import shutil
            shutil.rmtree(old)

    base = _merge_manifest(d6_manifest_csv, pseudo_csv, gold_csv)
    sessions = sorted(base.loc[base["gold_label"] >= 0, "session_key"].astype(str).unique())

    if len(sessions) != 3:
        raise RuntimeError(
            f"Pipeline này yêu cầu đúng 3 Human-Gold sessions cho 3-fold LOSO; hiện có {len(sessions)}: {sessions}"
        )

    index_rows = []
    global_reports = []

    for fold_no, held_out in enumerate(sessions, start=1):
        fold_name = f"fold_{fold_no:02d}_{_safe_name(held_out)}"
        fold_dir = out_root / fold_name
        fold_dir.mkdir(parents=True, exist_ok=True)

        gold_dev = base[
            (base["gold_label"] >= 0) & (base["session_key"].astype(str) != held_out)
        ].copy()

        val_groups, inner_report = _choose_tracklet_val_split(
            gold_dev,
            float(val_ratio_within_dev),
            int(seed) + fold_no * 10000,
            int(split_trials),
        )

        fold = base.copy()
        is_test = fold["session_key"].astype(str).eq(held_out)
        is_val = fold["tracklet_key"].astype(str).isin(val_groups) & ~is_test

        fold["split"] = np.where(is_test, "test", np.where(is_val, "val", "train"))
        fold["cv_fold"] = fold_no
        fold["outer_test_session"] = held_out
        fold["split_scheme"] = "loso_outer_test__tracklet_group_inner_val"

        sort_cols = [c for c in ["split", "class_id", "session_id", "tracklet_id", "segment_index"] if c in fold.columns]
        fold = fold.sort_values(sort_cols)

        manifest = fold_dir / "training_manifest.csv"
        fold.to_csv(manifest, index=False)

        validation = validate_cv_manifest(manifest, require_test=True)
        validation["inner_split"] = inner_report
        (fold_dir / "validation.json").write_text(
            json.dumps(validation, indent=2, ensure_ascii=False),
            encoding="utf-8",
        )

        gold_fold = fold[fold["gold_label"] >= 0]
        summary = pd.crosstab(
            gold_fold["split"],
            gold_fold["gold_label"],
            margins=False,
        ).reindex(index=["train", "val", "test"], columns=range(len(BEHAVIORS)), fill_value=0)
        summary.columns = BEHAVIORS
        summary.insert(0, "split", summary.index)
        summary.to_csv(fold_dir / "gold_split_counts.csv", index=False)

        index_rows.append({
            "fold": fold_no,
            "fold_name": fold_name,
            "test_session": held_out,
            "manifest": manifest.as_posix(),
            "validation": (fold_dir / "validation.json").as_posix(),
        })
        global_reports.append(validation)

    index_path = out_root / "cv_index.csv"
    pd.DataFrame(index_rows).to_csv(index_path, index=False)

    (out_root / "cv_summary.json").write_text(
        json.dumps(
            {
                "scheme": "3-fold Leave-One-Session-Out",
                "inner_validation": "tracklet-group holdout within the two development sessions",
                "gold_only": True,
                "sessions": sessions,
                "folds": global_reports,
            },
            indent=2,
            ensure_ascii=False,
        ),
        encoding="utf-8",
    )
    return index_path

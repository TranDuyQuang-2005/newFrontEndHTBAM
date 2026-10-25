from __future__ import annotations
from pathlib import Path
import json
import math
import random

import numpy as np
import pandas as pd
import torch
from torch import nn
from torch.utils.data import DataLoader

from .dataset import BehaviorWindowDataset
from .model import HTBAMX
from .metrics import classification_metrics
from .behavior_cache import BEHAVIORS
from .d6_validation import validate_training_manifest, sha256_file
from .reporting import (
    reset_output_dir, save_json, save_training_curves,
    save_confusion_artifacts, save_predictions,
)


def seed_all(seed: int):
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(seed)


def _class_counts(
    dataset: BehaviorWindowDataset,
    n: int = 4,
) -> np.ndarray:
    """Count exactly the labels supervised by behavior loss.

    For CausalGoldTargetDataset, historical context segments must not
    contribute to class weighting. One Human Gold = one supervised target.
    """
    counts = np.zeros(n, dtype=np.int64)

    target_ids = getattr(dataset, "target_segment_ids", None)
    if target_ids is not None:
        if "segment_id" not in dataset.df.columns:
            raise RuntimeError(
                "Dataset exposes target_segment_ids but df has no segment_id"
            )

        ids_as_str = [str(x) for x in target_ids]
        if len(ids_as_str) != len(set(ids_as_str)):
            raise RuntimeError(
                "target_segment_ids contains duplicates"
            )

        df = dataset.df.copy()
        df["_segment_id_str"] = df["segment_id"].astype(str)

        if df["_segment_id_str"].duplicated().any():
            raise RuntimeError(
                "dataset.df contains duplicated segment_id"
            )

        lookup = df.set_index("_segment_id_str", drop=False)

        for sid in ids_as_str:
            if sid not in lookup.index:
                raise RuntimeError(
                    f"Target segment_id not found in dataset.df: {sid}"
                )

            row = lookup.loc[sid]
            y = int(dataset._label_for_row(row))

            if y < 0:
                raise RuntimeError(
                    f"Target has no supervised label: {sid}"
                )
            if y >= n:
                raise RuntimeError(
                    f"Target label out of range: segment={sid}, label={y}, n={n}"
                )

            counts[y] += 1

        if int(counts.sum()) != len(ids_as_str):
            raise RuntimeError(
                f"Target count mismatch: counts={counts.tolist()}, "
                f"targets={len(ids_as_str)}"
            )

        return counts

    # Generic fallback: count only actual supervised labels returned by dataset.
    for i in range(len(dataset)):
        item = dataset[i]
        y = item["label"]

        if torch.is_tensor(y):
            values = y.detach().cpu().reshape(-1).tolist()
        else:
            values = np.asarray(y).reshape(-1).tolist()

        for v in values:
            cls = int(v)
            if 0 <= cls < n:
                counts[cls] += 1

    return counts


def _class_weights(
    dataset: BehaviorWindowDataset,
    n: int = 4,
    beta: float = 0.99,
) -> torch.Tensor:
    """Class-Balanced weights using Effective Number of Samples.

    effective_n = (1 - beta**count) / (1 - beta)
    raw_weight  = (1 - beta) / (1 - beta**count)

    Non-zero class weights are normalized to mean 1.0 so the overall
    CrossEntropy scale remains comparable to the previous setup.
    """
    if not (0.0 <= float(beta) < 1.0):
        raise ValueError(f"class_balance_beta must be in [0,1), got {beta}")

    counts = _class_counts(dataset, n).astype(np.float64)
    weights = np.zeros(n, dtype=np.float64)
    nonzero = counts > 0

    if np.any(nonzero):
        if float(beta) == 0.0:
            raw = np.ones(int(nonzero.sum()), dtype=np.float64)
        else:
            c = counts[nonzero]
            raw = (1.0 - float(beta)) / (
                1.0 - np.power(float(beta), c)
            )

        raw_mean = float(raw.mean())
        if not np.isfinite(raw_mean) or raw_mean <= 0:
            raise RuntimeError(
                f"Invalid effective-number weights: counts={counts.tolist()}, "
                f"beta={beta}, raw={raw.tolist()}"
            )

        weights[nonzero] = raw / raw_mean

    return torch.from_numpy(weights.astype(np.float32))


def _move(batch: dict, device: torch.device) -> dict:
    """Move tensor values in a DataLoader batch to the selected device.

    Metadata such as session_key, segment_id and target_segment_id stays on CPU
    as normal Python objects. temporal_mask is a tensor, so it is moved too.
    """
    return {
        key: (
            value.to(device, non_blocking=True)
            if torch.is_tensor(value)
            else value
        )
        for key, value in batch.items()
    }



def behavior_loss_safe(ce, logits: torch.Tensor, y: torch.Tensor) -> torch.Tensor:
    flat_y = y.reshape(-1)
    valid = flat_y >= 0
    if not torch.any(valid):
        return logits.sum() * 0.0
    flat_logits = logits.reshape(-1, logits.shape[-1])
    return ce(flat_logits[valid], flat_y[valid])


def temporal_smoothness(logits: torch.Tensor, temporal_mask: torch.Tensor | None = None) -> torch.Tensor:
    if logits.shape[1] < 2:
        return logits.new_tensor(0.0)
    probs = torch.softmax(logits, dim=-1)
    pair_delta = torch.mean(torch.abs(probs[:, 1:] - probs[:, :-1]), dim=-1)
    if temporal_mask is None:
        return pair_delta.mean()
    tm = temporal_mask.to(device=logits.device, dtype=torch.bool)
    valid_pairs = tm[:, 1:] & tm[:, :-1]
    if not torch.any(valid_pairs):
        return logits.new_tensor(0.0)
    return pair_delta[valid_pairs].mean()


def evaluate(model, loader, device, ce, bce, atomic_w: float, smooth_w: float, amp: bool,
             return_predictions: bool = False):
    model.eval()
    losses, ys, ps = [], [], []
    with torch.no_grad():
        for batch in loader:
            batch = _move(batch, device)
            with torch.autocast(device_type=device.type, enabled=amp and device.type == "cuda"):
                out = model(
                    batch["video"], batch["semantic"], batch["semantic_mask"], batch["pose"],
                    batch.get("rgb_quality"), batch.get("rgb_embedding"), batch.get("temporal_mask"),
                )
                y = batch["label"]
                loss_b = behavior_loss_safe(ce, out.behavior_logits, y)
                raw_a = bce(out.atomic_logits, batch["atomic"])
                am = batch.get("atomic_mask", torch.ones_like(raw_a))
                loss_a = (raw_a * am).sum() / am.sum().clamp_min(1.0)
                loss = loss_b + atomic_w * loss_a + smooth_w * temporal_smoothness(out.behavior_logits, batch.get("temporal_mask"))
            losses.append(float(loss.detach().cpu()))
            pred = out.behavior_logits.argmax(-1)
            mask = y >= 0
            ys.extend(y[mask].detach().cpu().tolist())
            ps.extend(pred[mask].detach().cpu().tolist())
    met = classification_metrics(ys, ps, 4)
    met["loss"] = float(np.mean(losses)) if losses else math.inf
    if return_predictions:
        return met, ys, ps
    return met


def _make_optimizer(model: HTBAMX, cfg: dict, include_backbone: bool) -> torch.optim.Optimizer:
    head_params = [p for n, p in model.named_parameters() if not n.startswith("rgb_backbone.") and p.requires_grad]
    groups = [{"params": head_params, "lr": float(cfg.get("lr_head", 3e-4))}]
    if include_backbone and model.rgb_backbone is not None:
        back_params = [p for n, p in model.named_parameters() if n.startswith("rgb_backbone.") and p.requires_grad]
        if back_params:
            groups.append({"params": back_params, "lr": float(cfg.get("lr_backbone", 3e-5))})
    return torch.optim.AdamW(groups, weight_decay=float(cfg.get("weight_decay", 1e-4)))


def _infer_precomputed_dim(manifest_csv: str | Path) -> int:
    df = pd.read_csv(manifest_csv)
    if "rgb_embedding_path" not in df.columns:
        raise ValueError("Training manifest chưa có rgb_embedding_path. Chạy 10_cache_rgb_embeddings.py.")
    for v in df["rgb_embedding_path"].dropna().astype(str):
        p = Path(v)
        if p.exists():
            arr = np.load(p)
            if arr.ndim != 1 or arr.size <= 0:
                raise ValueError(f"RGB embedding shape sai: {p} -> {arr.shape}")
            return int(arr.size)
    raise FileNotFoundError("Không tìm thấy RGB embedding cache hợp lệ")


def _save_dataset_reports(manifest_csv: str | Path, out_dir: Path, train_mode: str) -> None:
    df = pd.read_csv(manifest_csv)
    rows = []
    for split in ("train", "val", "test"):
        sub = df[df["split"] == split]
        rows.append({
            "split": split,
            "segments": int(len(sub)),
            "sessions": int(sub["session_key"].nunique()),
            "tracklets": int(sub[["class_id", "session_id", "tracklet_id"]].drop_duplicates().shape[0]),
            "gold_segments": int((sub["gold_label"] >= 0).sum()),
            "gold_coverage": float((sub["gold_label"] >= 0).mean()) if len(sub) else 0.0,
            "pseudo_segments": int((sub["pseudo_label"] >= 0).sum()),
        })
    pd.DataFrame(rows).to_csv(out_dir / "reports" / "dataset_split_summary.csv", index=False)

    dist_rows = []
    for split in ("train", "val", "test"):
        sub = df[df["split"] == split]
        for source, col in (("gold", "gold_label"), ("pseudo", "pseudo_label")):
            for cid, name in enumerate(BEHAVIORS):
                dist_rows.append({
                    "split": split, "label_source": source, "class_id": cid,
                    "class_name": name, "count": int((sub[col] == cid).sum()),
                })
    pd.DataFrame(dist_rows).to_csv(out_dir / "reports" / "segment_class_distribution.csv", index=False)


def train_htbamx(manifest_csv: str | Path, out_dir: str | Path, cfg: dict, d6_cfg: dict) -> Path:
    manifest_csv = Path(manifest_csv)
    validation = validate_training_manifest(manifest_csv, require_test=True)

    seed = int(cfg.get("seed", 2026))
    seed_all(seed)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    context = int(d6_cfg.get("context_segments", 3))
    stride = int(d6_cfg.get("context_stride_segments", 1))
    use_pre = bool(cfg.get("use_precomputed_rgb", False))
    train_mode = str(cfg.get("train_label_mode", "auto"))
    if train_mode not in {"auto", "gold", "pseudo"}:
        raise ValueError("htbamx.train_label_mode phải là auto/gold/pseudo")

    # Train can use Gold->Pseudo fallback. Validation/Test are ALWAYS Gold only.
    train_ds = BehaviorWindowDataset(manifest_csv, "train", context, stride, train_mode, True, use_pre, require_labeled=True)
    val_ds = BehaviorWindowDataset(manifest_csv, "val", context, stride, "gold", False, use_pre, require_labeled=True)
    test_ds = BehaviorWindowDataset(manifest_csv, "test", context, stride, "gold", False, use_pre, require_labeled=True)

    workers = int(cfg.get("num_workers", 0))
    bs = int(cfg.get("batch_size", 1))
    train_loader = DataLoader(train_ds, batch_size=bs, shuffle=True, num_workers=workers, pin_memory=device.type == "cuda")
    val_loader = DataLoader(val_ds, batch_size=bs, shuffle=False, num_workers=workers, pin_memory=device.type == "cuda")
    test_loader = DataLoader(test_ds, batch_size=bs, shuffle=False, num_workers=workers, pin_memory=device.type == "cuda")

    pre_dim = _infer_precomputed_dim(manifest_csv) if use_pre else None
    model = HTBAMX(
        semantic_dim=14,
        num_behaviors=int(cfg.get("num_behaviors", 4)),
        num_atomic=int(cfg.get("num_atomic", 4)),
        backbone=str(cfg.get("backbone", "mvit_v2_s")),
        pretrained_backbone=bool(cfg.get("pretrained_backbone", True)),
        model_dim=int(cfg.get("model_dim", 256)),
        transformer_layers=int(cfg.get("transformer_layers", 2)),
        transformer_heads=int(cfg.get("transformer_heads", 4)),
        ff_dim=int(cfg.get("ff_dim", 768)),
        dropout=float(cfg.get("dropout", 0.2)),
        precomputed_rgb_dim=pre_dim,
    ).to(device)

    counts = _class_counts(train_ds, 4)
    if bool(cfg.get("require_all_train_classes", True)) and np.any(counts == 0):
        missing = [BEHAVIORS[i] for i, c in enumerate(counts) if c == 0]
        raise RuntimeError(f"Train split thiếu class {missing}. Không train để tránh class collapse.")

    warmup = int(cfg.get("warmup_epochs", 3))
    if not use_pre:
        model.freeze_rgb(warmup > 0)
    ce = nn.CrossEntropyLoss(
        ignore_index=-1,
        label_smoothing=float(cfg.get("label_smoothing", 0.0)),
    )
    bce = nn.BCEWithLogitsLoss(reduction="none")
    opt = _make_optimizer(model, cfg, include_backbone=(not use_pre and warmup == 0))

    epochs = int(cfg.get("epochs", 30))
    accum = max(1, int(cfg.get("grad_accum_steps", 1)))
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=max(1, epochs))
    amp = bool(cfg.get("amp", True))
    atomic_w = float(cfg.get("atomic_loss_weight", 0.25))
    smooth_w = float(cfg.get("temporal_smooth_weight", 0.05))

    # Retraining REPLACES the previous checkpoint/report set by design.
    out_dir = reset_output_dir(out_dir)
    save_json(out_dir / "training_config.json", {
        "manifest_csv": str(manifest_csv.resolve()),
        "manifest_sha256": sha256_file(manifest_csv),
        "train_label_mode": train_mode,
        "validation_label_mode": "gold",
        "test_label_mode": "gold",
        "device": str(device),
        "htbamx": cfg,
        "d6": d6_cfg,
        "behavior_labels": BEHAVIORS,
        "behavior_loss": "unweighted_cross_entropy",
        "behavior_loss_label_smoothing": float(cfg.get("label_smoothing", 0.0)),
        "dataset_validation": validation,
    })
    _save_dataset_reports(manifest_csv, out_dir, train_mode)
    pd.DataFrame({
        "class_id": list(range(4)),
        "class_name": BEHAVIORS,
        "effective_train_count": counts.tolist(),
        "class_weight_method": ["none_unweighted"] * 4,
        "class_weight": [1.0] * 4,
    }).to_csv(out_dir / "reports" / "train_class_distribution.csv", index=False)
    total_params = sum(p.numel() for p in model.parameters())
    trainable_params = sum(p.numel() for p in model.parameters() if p.requires_grad)
    (out_dir / "reports" / "model_summary.txt").write_text(
        f"HTBAM-X\nTotal parameters: {total_params}\nTrainable parameters at start: {trainable_params}\n"
        f"Use precomputed RGB: {use_pre}\nPrecomputed RGB dim: {pre_dim}\n",
        encoding="utf-8",
    )

    best = -1.0
    patience = 0
    history = []
    for epoch in range(1, epochs + 1):
        if (not use_pre) and epoch == warmup + 1 and warmup > 0:
            model.freeze_rgb(False)
            opt = _make_optimizer(model, cfg, include_backbone=True)
            scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=max(1, epochs - epoch + 1))

        model.train()
        opt.zero_grad(set_to_none=True)
        running, train_y, train_p = [], [], []
        for step, batch in enumerate(train_loader, 1):
            batch = _move(batch, device)
            with torch.autocast(device_type=device.type, enabled=amp and device.type == "cuda"):
                out = model(
                    batch["video"], batch["semantic"], batch["semantic_mask"], batch["pose"],
                    batch.get("rgb_quality"), batch.get("rgb_embedding"), batch.get("temporal_mask"),
                )
                loss_b = behavior_loss_safe(ce, out.behavior_logits, batch["label"])
                raw_a = bce(out.atomic_logits, batch["atomic"])
                am = batch.get("atomic_mask", torch.ones_like(raw_a))
                loss_a = (raw_a * am).sum() / am.sum().clamp_min(1.0)
                loss = (loss_b + atomic_w * loss_a + smooth_w * temporal_smoothness(out.behavior_logits, batch.get("temporal_mask"))) / accum
            loss.backward()
            running.append(float(loss.detach().cpu()) * accum)

            y = batch["label"]
            pred = out.behavior_logits.detach().argmax(-1)
            valid = y >= 0
            train_y.extend(y[valid].detach().cpu().tolist())
            train_p.extend(pred[valid].detach().cpu().tolist())

            if step % accum == 0 or step == len(train_loader):
                torch.nn.utils.clip_grad_norm_(model.parameters(), float(cfg.get("grad_clip", 1.0)))
                opt.step()
                opt.zero_grad(set_to_none=True)

        train_met = classification_metrics(train_y, train_p, 4)
        val = evaluate(model, val_loader, device, ce, bce, atomic_w, smooth_w, amp)
        current_lr = float(opt.param_groups[0]["lr"])
        row = {
            "epoch": epoch,
            "train_loss": float(np.mean(running)) if running else math.inf,
            "train_accuracy": train_met["accuracy"],
            "train_balanced_accuracy": train_met["balanced_accuracy"],
            "train_macro_f1": train_met["macro_f1"],
            **{f"val_{k}": v for k, v in val.items() if k not in {"confusion_matrix", "per_class_precision", "per_class_recall", "per_class_f1"}},
            "lr": current_lr,
        }
        history.append(row)
        print(json.dumps(row, ensure_ascii=False))

        score = float(val["macro_f1"])
        if score > best + 1e-6:
            best, patience = score, 0
            torch.save({
                "model": model.state_dict(),
                "config": cfg,
                "d6_config": d6_cfg,
                "epoch": epoch,
                "train_label_mode": train_mode,
                "val_label_mode": "gold",
                "test_label_mode": "gold",
                "behavior_labels": BEHAVIORS,
                "val": val,
                "manifest_sha256": sha256_file(manifest_csv),
                "precomputed_rgb_dim": pre_dim,
            }, out_dir / "best.pt")
        else:
            patience += 1

        scheduler.step()
        if patience >= int(cfg.get("early_stopping_patience", 8)):
            break

    history_df = pd.DataFrame(history)
    history_df.to_csv(out_dir / "history.csv", index=False)
    save_training_curves(history_df, out_dir)

    ckpt = torch.load(out_dir / "best.pt", map_location=device, weights_only=False)
    model.load_state_dict(ckpt["model"])

    val_final, val_y, val_p = evaluate(model, val_loader, device, ce, bce, atomic_w, smooth_w, amp, return_predictions=True)
    save_confusion_artifacts(val_y, val_p, "val", out_dir, BEHAVIORS)
    save_predictions(val_y, val_p, "val", out_dir, BEHAVIORS)

    test_final, test_y, test_p = evaluate(model, test_loader, device, ce, bce, atomic_w, smooth_w, amp, return_predictions=True)
    save_confusion_artifacts(test_y, test_p, "test", out_dir, BEHAVIORS)
    save_predictions(test_y, test_p, "test", out_dir, BEHAVIORS)

    final = {
        "best_epoch": int(ckpt["epoch"]),
        "best_val": val_final,
        "test": test_final,
        "train_label_mode": train_mode,
        "validation_label_mode": "gold",
        "test_label_mode": "gold",
        "behavior_labels": BEHAVIORS,
    }
    save_json(out_dir / "metrics.json", final)
    return out_dir / "best.pt"

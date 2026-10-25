from __future__ import annotations
from pathlib import Path

import numpy as np
import pandas as pd
import torch
from torch.utils.data import Dataset

from .behavior_cache import SEMANTIC_NAMES


class BehaviorWindowDataset(Dataset):
    def __init__(
        self,
        manifest_csv: str | Path,
        split: str,
        context_segments: int = 3,
        stride_segments: int = 1,
        label_mode: str = "auto",
        training: bool = False,
        use_precomputed_rgb: bool = False,
        require_labeled: bool = False,
    ):
        self.df = pd.read_csv(manifest_csv)
        required = {"split", "class_id", "session_id", "tracklet_id", "segment_index", "cache_path", "pseudo_label", "gold_label"}
        missing = required - set(self.df.columns)
        if missing:
            raise ValueError(f"Training manifest thiếu cột: {sorted(missing)}")
        self.df = self.df[self.df["split"] == split].copy()
        self.context_segments = int(context_segments)
        self.stride_segments = int(stride_segments)
        self.label_mode = str(label_mode)
        self.training = bool(training)
        self.use_precomputed_rgb = bool(use_precomputed_rgb)
        self.require_labeled = bool(require_labeled)
        if self.use_precomputed_rgb and "rgb_embedding_path" not in self.df.columns:
            raise ValueError("Manifest chưa có rgb_embedding_path. Chạy 10_cache_rgb_embeddings.py trước.")
        if self.context_segments < 1 or self.stride_segments < 1:
            raise ValueError("context_segments/stride phải >= 1")
        if self.label_mode not in {"auto", "gold", "pseudo"}:
            raise ValueError("label_mode phải là auto/gold/pseudo")

        self.windows: list[list[int]] = []
        group_cols = ["class_id", "session_id", "tracklet_id"]
        for _, g in self.df.groupby(group_cols, sort=False):
            g = g.sort_values("segment_index")
            idxs = g.index.tolist()
            segs = g["segment_index"].astype(int).tolist()
            for start in range(0, len(idxs) - self.context_segments + 1, self.stride_segments):
                selected = idxs[start:start + self.context_segments]
                s = segs[start:start + self.context_segments]
                if any(b - a != 1 for a, b in zip(s, s[1:])):
                    continue
                if self.require_labeled:
                    labels = [self._label_for_row(self.df.loc[i]) for i in selected]
                    if not any(y >= 0 for y in labels):
                        continue
                self.windows.append(selected)
        if not self.windows:
            raise RuntimeError(
                f"Không tạo được window cho split={split}, label_mode={self.label_mode}. "
                "Kiểm tra số segment liên tục và Gold Label của split."
            )

    def __len__(self) -> int:
        return len(self.windows)

    def _label_for_row(self, row: pd.Series) -> int:
        gold = int(row.get("gold_label", -1))
        pseudo = int(row.get("pseudo_label", -1))
        if self.label_mode == "gold":
            return gold
        if self.label_mode == "pseudo":
            return pseudo
        return gold if gold >= 0 else pseudo

    @staticmethod
    def _normalize_video(v: np.ndarray) -> torch.Tensor:
        # np [F,H,W,3] BGR -> torch [3,F,H,W] RGB, Kinetics normalization.
        v = v[..., ::-1].copy()
        x = torch.from_numpy(v).permute(3, 0, 1, 2).float().div_(255.0)
        mean = torch.tensor([0.45, 0.45, 0.45], dtype=x.dtype).view(3, 1, 1, 1)
        std = torch.tensor([0.225, 0.225, 0.225], dtype=x.dtype).view(3, 1, 1, 1)
        return (x - mean) / std

    def __getitem__(self, idx: int) -> dict[str, torch.Tensor | str]:
        rows = self.df.loc[self.windows[idx]]
        videos = []
        rgb_embeddings = []
        rgb_qualities = []
        semantics = []
        sem_masks = []
        poses = []
        atomics = []
        atomic_masks = []
        labels = []
        segment_ids = []

        for _, r in rows.iterrows():
            cache_path = Path(str(r.cache_path))
            if not cache_path.exists():
                raise FileNotFoundError(f"Thiếu D6 cache: {cache_path}")
            with np.load(str(cache_path)) as z:
                if not self.use_precomputed_rgb:
                    videos.append(self._normalize_video(z["video"]))
                sem = z["semantic"].astype(np.float32)
                sm = z["semantic_mask"].astype(np.float32)
                pose = z["pose"].astype(np.float32)
                atomic = z["atomic"].astype(np.float32)
                atomic_mask = z["atomic_mask"].astype(np.float32) if "atomic_mask" in z.files else np.ones_like(atomic)
                frame_valid = z["frame_valid"].astype(np.float32) if "frame_valid" in z.files else np.ones(len(z["video"]), np.float32)
            if self.use_precomputed_rgb:
                ep = Path(str(r.rgb_embedding_path))
                if not ep.exists():
                    raise FileNotFoundError(f"Thiếu RGB embedding cache: {ep}")
                rgb_embeddings.append(torch.from_numpy(np.load(str(ep)).astype(np.float32)))

            denom = np.maximum(sm.sum(axis=0), 1e-6)
            sem_mean = (sem * sm).sum(axis=0) / denom
            sem_mean = sem_mean.astype(np.float32)
            for name, scale in [("ear", 0.5), ("mar", 0.8), ("pitch", 60.0), ("yaw", 60.0), ("roll", 60.0)]:
                j = SEMANTIC_NAMES.index(name)
                sem_mean[j] = float(np.clip(sem_mean[j] / scale, -1.0, 1.0))
            sem_valid = (sm.sum(axis=0) > 0).astype(np.float32)

            rgb_qualities.append(torch.tensor(float(frame_valid.mean()), dtype=torch.float32))
            semantics.append(torch.from_numpy(sem_mean))
            sem_masks.append(torch.from_numpy(sem_valid))
            poses.append(torch.from_numpy(pose))
            adenom = np.maximum(atomic_mask.sum(axis=0), 1e-6)
            atomics.append(torch.from_numpy(((atomic * atomic_mask).sum(axis=0) / adenom).astype(np.float32)))
            atomic_masks.append(torch.from_numpy((atomic_mask.sum(axis=0) > 0).astype(np.float32)))
            labels.append(self._label_for_row(r))
            segment_ids.append(str(r.segment_id))

        return {
            "video": torch.stack(videos, dim=0) if videos else torch.empty(0),
            "rgb_embedding": torch.stack(rgb_embeddings, dim=0) if rgb_embeddings else torch.empty(0),
            "rgb_quality": torch.stack(rgb_qualities, dim=0),
            "semantic": torch.stack(semantics, dim=0),
            "semantic_mask": torch.stack(sem_masks, dim=0),
            "pose": torch.stack(poses, dim=0),
            "atomic": torch.stack(atomics, dim=0),
            "atomic_mask": torch.stack(atomic_masks, dim=0),
            "label": torch.tensor(labels, dtype=torch.long),
            "session_key": str(rows.iloc[0].session_key),
            "segment_id": segment_ids,
        }

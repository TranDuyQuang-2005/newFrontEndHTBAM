from __future__ import annotations

from pathlib import Path

import pandas as pd
import torch

from .dataset import BehaviorWindowDataset


class CausalGoldTargetDataset(BehaviorWindowDataset):
    """One Human-Gold target per item with causal temporal context.

    Context uses only the target t and up to context_segments-1 previous
    contiguous segments from the same class/session/tracklet. No t+1 is used.

    Short history is LEFT padded to fixed T=context_segments. `temporal_mask`
    marks real timesteps. Behavior and atomic supervision are target-only.
    """

    PAD_SEGMENT_ID = "<PAD>"

    def __init__(
        self,
        manifest_csv: str | Path,
        split: str,
        context_segments: int = 3,
        stride_segments: int = 1,
        label_mode: str = "gold",
        training: bool = False,
        use_precomputed_rgb: bool = False,
        require_labeled: bool = True,
    ):
        if str(label_mode) != "gold":
            raise ValueError(
                "CausalGoldTargetDataset only supports label_mode='gold'."
            )

        super().__init__(
            manifest_csv,
            split,
            context_segments,
            stride_segments,
            label_mode,
            training,
            use_precomputed_rgb,
            require_labeled=False,
        )

        self.require_labeled = bool(require_labeled)
        self.target_segment_ids: list[str] = []
        self.real_lengths: list[int] = []

        windows: list[list[int]] = []
        group_cols = ["class_id", "session_id", "tracklet_id"]

        for _, g in self.df.groupby(group_cols, sort=False):
            g = g.sort_values("segment_index")
            idxs = g.index.tolist()
            segs = g["segment_index"].astype(int).tolist()

            runs: list[list[int]] = []
            current: list[int] = []
            prev_seg = None

            for row_idx, seg_idx in zip(idxs, segs):
                if prev_seg is None or seg_idx - prev_seg == 1:
                    current.append(row_idx)
                else:
                    if current:
                        runs.append(current)
                    current = [row_idx]
                prev_seg = seg_idx

            if current:
                runs.append(current)

            for run in runs:
                for p, target_idx in enumerate(run):
                    target_label = int(self._label_for_row(self.df.loc[target_idx]))
                    if target_label < 0:
                        continue

                    start = max(0, p - self.context_segments + 1)
                    selected = run[start : p + 1]

                    windows.append(selected)
                    self.real_lengths.append(len(selected))
                    self.target_segment_ids.append(
                        str(self.df.loc[target_idx, "segment_id"])
                    )

        if not windows:
            raise RuntimeError(
                f"No causal Human-Gold targets for split={split}"
            )

        ids = pd.Series(self.target_segment_ids, dtype="object")
        if ids.duplicated().any():
            dup = ids[ids.duplicated()].unique().tolist()
            raise RuntimeError(
                f"Duplicated causal target segment_id, e.g. {dup[:10]}"
            )

        expected = set(
            self.df.loc[
                self.df["gold_label"].astype(int) >= 0, "segment_id"
            ].astype(str)
        )
        actual = set(self.target_segment_ids)
        missing = sorted(expected - actual)
        extra = sorted(actual - expected)
        if missing or extra:
            raise RuntimeError(
                "Causal target coverage does not match Human Gold. "
                f"missing={missing[:10]}, extra={extra[:10]}"
            )

        self.windows = windows

    @staticmethod
    def _left_pad_time_tensor(
        x: torch.Tensor,
        pad: int,
        pad_value: float | int = 0,
    ) -> torch.Tensor:
        if pad <= 0:
            return x
        if x.ndim < 1:
            raise RuntimeError(f"Tensor has no time axis: shape={tuple(x.shape)}")
        shape = (pad,) + tuple(x.shape[1:])
        prefix = torch.full(
            shape, pad_value, dtype=x.dtype, device=x.device
        )
        return torch.cat([prefix, x], dim=0)

    def __getitem__(self, idx: int):
        item = super().__getitem__(idx)

        real_len = int(self.real_lengths[idx])
        pad = self.context_segments - real_len
        if pad < 0:
            raise RuntimeError(
                f"real_len={real_len} > context_segments={self.context_segments}"
            )

        labels = item["label"].clone()
        if labels.numel() != real_len:
            raise RuntimeError("label/time mismatch")

        target_label = int(labels[-1].item())
        if target_label < 0:
            raise RuntimeError(
                f"Target has no Human Gold: {self.target_segment_ids[idx]}"
            )

        labels[:] = -1
        labels[-1] = target_label

        atomic_mask = item["atomic_mask"].clone()
        if atomic_mask.shape[0] != real_len:
            raise RuntimeError("atomic_mask/time mismatch")
        target_atomic_mask = atomic_mask[-1].clone()
        atomic_mask.zero_()
        atomic_mask[-1] = target_atomic_mask
        item["atomic_mask"] = atomic_mask

        for key in [
            "video",
            "rgb_embedding",
            "rgb_quality",
            "semantic",
            "semantic_mask",
            "pose",
            "atomic",
            "atomic_mask",
        ]:
            x = item[key]
            if isinstance(x, torch.Tensor) and x.numel() > 0:
                item[key] = self._left_pad_time_tensor(x, pad, 0)

        item["label"] = self._left_pad_time_tensor(labels, pad, -1)

        temporal_mask = torch.zeros(
            self.context_segments, dtype=torch.bool
        )
        temporal_mask[pad:] = True
        item["temporal_mask"] = temporal_mask

        item["segment_id"] = (
            [self.PAD_SEGMENT_ID] * pad + list(item["segment_id"])
        )
        item["target_segment_id"] = str(self.target_segment_ids[idx])
        item["target_position"] = self.context_segments - 1
        item["real_context_length"] = real_len

        if len(item["segment_id"]) != self.context_segments:
            raise RuntimeError("segment_id padding length mismatch")
        if item["label"].shape[0] != self.context_segments:
            raise RuntimeError("label padding length mismatch")
        if int((item["label"] >= 0).sum().item()) != 1:
            raise RuntimeError("Expected exactly one supervised target")
        if int(item["label"][-1].item()) < 0:
            raise RuntimeError("Causal target must be the final timestep")

        return item


GoldUniqueEvalDataset = CausalGoldTargetDataset
GoldUniqueTargetDataset = CausalGoldTargetDataset

from __future__ import annotations
from pathlib import Path
import json
from typing import Any

import numpy as np
import torch

API_LABELS = ["FOCUSED", "DISTRACTED", "SLEEPY", "ACTIVE"]

class HtbamRuntime:
    def __init__(self, model_dir: Path, device: str = "auto"):
        self.model_dir = Path(model_dir)
        self.device = self._resolve_device(device)
        self.model_device = torch.device("cpu")
        self.model = None
        self.metadata: dict[str, Any] = {}
        self.error: str | None = None
        self._load()

    @staticmethod
    def _resolve_device(device: str) -> torch.device:
        if device == "auto":
            return torch.device("cuda" if torch.cuda.is_available() else "cpu")
        if device.startswith("cuda") and not torch.cuda.is_available():
            return torch.device("cpu")
        return torch.device(device)

    def _load(self) -> None:
        model_path = self.model_dir / "htbamx_classifier.ts"
        metadata_path = self.model_dir / "model_metadata.json"
        if not model_path.exists():
            self.error = f"missing {model_path}"
            return
        if not metadata_path.exists():
            self.error = f"missing {metadata_path}"
            return
        try:
            self.metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
            self.model = torch.jit.load(str(model_path), map_location=self.model_device).to(self.model_device).eval()
            self.error = None
        except Exception as exc:
            self.model = None
            self.error = f"{type(exc).__name__}: {exc}"

    @property
    def ready(self) -> bool:
        return self.model is not None and self.error is None

    @property
    def model_version(self) -> str:
        return str(self.metadata.get("modelVersion", "htbamx-not-loaded"))

    @property
    def loaded_models(self) -> list[str]:
        return [f"HTBAM-X:{self.model_version}"] if self.ready else []

    def status(self) -> dict[str, Any]:
        return {
            "ready": self.ready,
            "device": str(self.device),
            "classifierDevice": str(self.model_device),
            "modelVersion": self.model_version,
            "error": self.error,
            "modelDir": str(self.model_dir),
            "contextSegments": self.metadata.get("contextSegments"),
            "requiresExactD6FeatureExtractor": self.metadata.get("requiresExactD6FeatureExtractor", True),
            "requiresExactRgbEmbeddingPipeline": self.metadata.get("requiresExactRgbEmbeddingPipeline", True),
        }

    def predict(self, req) -> dict[str, Any]:
        if not self.ready:
            raise RuntimeError(self.error or "HTBAM-X classifier is not loaded")

        rgb = np.asarray(req.rgbEmbedding, dtype=np.float32)
        rgb_q = np.asarray(req.rgbQuality, dtype=np.float32)
        semantic = np.asarray(req.semantic, dtype=np.float32)
        semantic_mask = np.asarray(req.semanticMask, dtype=np.float32)
        pose = np.asarray(req.pose, dtype=np.float32)
        temporal_mask = np.asarray(req.temporalMask, dtype=np.bool_)

        context = int(self.metadata.get("contextSegments", 3))
        rgb_dim = int(self.metadata.get("precomputedRgbDim", rgb.shape[-1] if rgb.ndim == 2 else -1))
        sem_dim = int(self.metadata.get("semanticDim", 14))

        if rgb.ndim != 2 or rgb.shape != (context, rgb_dim):
            raise ValueError(f"rgbEmbedding must be [{context},{rgb_dim}], got {rgb.shape}")
        if rgb_q.shape != (context,):
            raise ValueError(f"rgbQuality must be [{context}], got {rgb_q.shape}")
        if semantic.shape != (context, sem_dim):
            raise ValueError(f"semantic must be [{context},{sem_dim}], got {semantic.shape}")
        if semantic_mask.shape != semantic.shape:
            raise ValueError(f"semanticMask must match semantic, got {semantic_mask.shape}")
        if pose.ndim != 4 or pose.shape[0] != context or pose.shape[-2:] != (33, 3):
            raise ValueError(f"pose must be [T,F,33,3] with T={context}, got {pose.shape}")
        if temporal_mask.shape != (context,):
            raise ValueError(f"temporalMask must be [{context}], got {temporal_mask.shape}")
        if not temporal_mask[-1]:
            raise ValueError("causal target timestep t must be real")

        tensors = (
            torch.from_numpy(rgb).unsqueeze(0).to(self.model_device),
            torch.from_numpy(rgb_q).unsqueeze(0).to(self.model_device),
            torch.from_numpy(semantic).unsqueeze(0).to(self.model_device),
            torch.from_numpy(semantic_mask).unsqueeze(0).to(self.model_device),
            torch.from_numpy(pose).unsqueeze(0).to(self.model_device),
            torch.from_numpy(temporal_mask).unsqueeze(0).to(self.model_device),
        )

        tensors = tuple(t.to(self.model_device) for t in tensors)

        with torch.inference_mode():
            logits = self.model(*tensors)
            if isinstance(logits, (tuple, list)):
                logits = logits[0]
            probs = torch.softmax(logits[:, -1, :], dim=-1)[0].detach().cpu().numpy()

        idx = int(np.argmax(probs))
        return {
            "label": API_LABELS[idx],
            "classId": idx,
            "probability": float(probs[idx]),
            "probabilities": {API_LABELS[i]: float(probs[i]) for i in range(min(4, len(probs)))},
            "modelVersion": self.model_version,
            "device": str(self.device),
            "classifierDevice": str(self.model_device),
        }

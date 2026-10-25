from __future__ import annotations

from collections import deque
from dataclasses import dataclass
from types import SimpleNamespace
from typing import Any

import numpy as np
import torch

from htbamx.model import make_video_backbone

SEMANTIC_NAMES = ['ear', 'eye_open', 'mar', 'pitch', 'yaw', 'roll', 'gaze_away', 'hand_up', 'phone_use', 'posture_upright', 'motion', 'face_visible', 'face_quality', 'pose_quality']
KINETICS_MEAN = [0.45, 0.45, 0.45]
KINETICS_STD = [0.225, 0.225, 0.225]
SEMANTIC_SCALES = [('ear', 0.5), ('mar', 0.8), ('pitch', 60.0), ('yaw', 60.0), ('roll', 60.0)]

EXPECTED_FRAMES = 16
EXPECTED_SIZE = 224
CONTEXT_SEGMENTS = 3


def _normalize_video_exact(video: np.ndarray) -> torch.Tensor:
    """Exact local copy of BehaviorWindowDataset._normalize_video."""
    v = np.asarray(video)
    v = v[..., ::-1].copy()
    x = torch.from_numpy(v).permute(3, 0, 1, 2).float().div_(255.0)
    mean = torch.tensor(KINETICS_MEAN, dtype=x.dtype).view(3, 1, 1, 1)
    std = torch.tensor(KINETICS_STD, dtype=x.dtype).view(3, 1, 1, 1)
    return (x - mean) / std


@dataclass(frozen=True)
class PreparedSegment:
    rgb_embedding: np.ndarray
    rgb_quality: float
    semantic: np.ndarray
    semantic_mask: np.ndarray
    pose: np.ndarray


class ExactRgbEmbedder:
    def __init__(self, device, backbone_name="mvit_v2_s", pretrained=True, expected_dim=None):
        self.device = torch.device(device)
        self.backbone_name = str(backbone_name)
        self.backbone, self.dim = make_video_backbone(self.backbone_name, bool(pretrained))
        self.backbone = self.backbone.to(self.device).eval()
        if expected_dim is not None and int(expected_dim) != int(self.dim):
            raise RuntimeError(
                f"RGB embedding dim mismatch: backbone={self.dim}, expected={expected_dim}"
            )

    @torch.inference_mode()
    def encode(self, video: np.ndarray) -> np.ndarray:
        video = np.asarray(video)
        if video.shape != (EXPECTED_FRAMES, EXPECTED_SIZE, EXPECTED_SIZE, 3):
            raise ValueError(f"video must be [16,224,224,3], got {video.shape}")
        if video.dtype != np.uint8:
            raise ValueError(f"video dtype must be uint8, got {video.dtype}")

        x = _normalize_video_exact(video).unsqueeze(0).to(self.device)
        e = self.backbone(x).squeeze(0).detach().float().cpu().numpy()
        if e.shape != (self.dim,):
            raise RuntimeError(f"Embedding shape mismatch: {e.shape}, expected {(self.dim,)}")
        return e.astype(np.float32, copy=False)


class ExactSegmentFeatureExtractor:
    def __init__(self, rgb: ExactRgbEmbedder):
        self.rgb = rgb

    def prepare(self, video, semantic_frames, semantic_mask_frames, pose_frames, frame_valid=None):
        video = np.asarray(video)
        sem = np.asarray(semantic_frames, dtype=np.float32)
        sm = np.asarray(semantic_mask_frames, dtype=np.float32)
        pose = np.asarray(pose_frames, dtype=np.float32)

        if sem.shape != (EXPECTED_FRAMES, len(SEMANTIC_NAMES)):
            raise ValueError(f"semantic_frames must be [16,14], got {sem.shape}")
        if sm.shape != sem.shape:
            raise ValueError(f"semantic_mask_frames must match semantic_frames, got {sm.shape}")
        if pose.shape != (EXPECTED_FRAMES, 33, 3):
            raise ValueError(f"pose_frames must be [16,33,3], got {pose.shape}")

        if frame_valid is None:
            fv = np.ones(EXPECTED_FRAMES, dtype=np.float32)
        else:
            fv = np.asarray(frame_valid, dtype=np.float32)
            if fv.shape != (EXPECTED_FRAMES,):
                raise ValueError(f"frame_valid must be [16], got {fv.shape}")

        denom = np.maximum(sm.sum(axis=0), 1e-6)
        sem_mean = ((sem * sm).sum(axis=0) / denom).astype(np.float32)
        for name, scale in SEMANTIC_SCALES:
            j = SEMANTIC_NAMES.index(name)
            sem_mean[j] = float(np.clip(sem_mean[j] / float(scale), -1.0, 1.0))
        sem_valid = (sm.sum(axis=0) > 0).astype(np.float32)

        return PreparedSegment(
            rgb_embedding=self.rgb.encode(video),
            rgb_quality=float(fv.mean()),
            semantic=sem_mean,
            semantic_mask=sem_valid,
            pose=pose,
        )


class CausalTrackBuffer:
    def __init__(self, context_segments=CONTEXT_SEGMENTS):
        if int(context_segments) != 3:
            raise ValueError("Production HTBAM-X requires context_segments=3")
        self.context_segments = 3
        self._tracks = {}

    def clear(self, stable_id=None):
        if stable_id is None:
            self._tracks.clear()
        else:
            self._tracks.pop(str(stable_id), None)

    def push(self, stable_id, segment: PreparedSegment):
        key = str(stable_id)
        q = self._tracks.setdefault(key, deque(maxlen=3))
        q.append(segment)

        real = list(q)
        pad = 3 - len(real)
        rgb_dim = int(segment.rgb_embedding.shape[0])
        frames = int(segment.pose.shape[0])

        rgb = np.zeros((3, rgb_dim), dtype=np.float32)
        rgb_q = np.zeros((3,), dtype=np.float32)
        sem = np.zeros((3, len(SEMANTIC_NAMES)), dtype=np.float32)
        sem_mask = np.zeros_like(sem)
        pose = np.zeros((3, frames, 33, 3), dtype=np.float32)
        temporal_mask = np.zeros((3,), dtype=np.bool_)

        for i, seg in enumerate(real, start=pad):
            rgb[i] = seg.rgb_embedding
            rgb_q[i] = seg.rgb_quality
            sem[i] = seg.semantic
            sem_mask[i] = seg.semantic_mask
            pose[i] = seg.pose
            temporal_mask[i] = True

        return {
            "rgbEmbedding": rgb,
            "rgbQuality": rgb_q,
            "semantic": sem,
            "semanticMask": sem_mask,
            "pose": pose,
            "temporalMask": temporal_mask,
        }


class ExactSegmentInferenceCore:
    def __init__(self, classifier: Any, device=None):
        if not getattr(classifier, "ready", False):
            raise RuntimeError("HTBAM-X classifier is not ready")

        metadata = getattr(classifier, "metadata", {}) or {}
        context = int(metadata.get("contextSegments", 3))
        if context != 3:
            raise RuntimeError(f"Expected contextSegments=3, got {context}")

        expected_dim = int(metadata.get("precomputedRgbDim", 768))
        backbone_name = str(metadata.get("rgbBackbone", "mvit_v2_s"))
        requested_device = device
        if requested_device is None or str(requested_device).strip().lower() == "auto":
            requested_device = getattr(classifier, "device", "cpu")
        dev = torch.device(requested_device)

        self.rgb = ExactRgbEmbedder(
            dev,
            backbone_name=backbone_name,
            pretrained=True,
            expected_dim=expected_dim,
        )
        self.features = ExactSegmentFeatureExtractor(self.rgb)
        self.buffer = CausalTrackBuffer(context)
        self.classifier = classifier

    def reset_track(self, stable_id):
        self.buffer.clear(stable_id)

    def predict_segment(self, stable_id, video, semantic_frames, semantic_mask_frames, pose_frames, frame_valid=None):
        segment = self.features.prepare(
            video, semantic_frames, semantic_mask_frames, pose_frames, frame_valid
        )
        ctx = self.buffer.push(stable_id, segment)
        req = SimpleNamespace(
            rgbEmbedding=ctx["rgbEmbedding"].tolist(),
            rgbQuality=ctx["rgbQuality"].tolist(),
            semantic=ctx["semantic"].tolist(),
            semanticMask=ctx["semanticMask"].tolist(),
            pose=ctx["pose"].tolist(),
            temporalMask=ctx["temporalMask"].tolist(),
        )
        result = dict(self.classifier.predict(req))
        result["stableId"] = str(stable_id)
        result["temporalMask"] = ctx["temporalMask"].astype(int).tolist()
        result["rgbQuality"] = float(segment.rgb_quality)
        return result

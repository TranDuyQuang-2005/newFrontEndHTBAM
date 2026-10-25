from __future__ import annotations
from dataclasses import dataclass

import torch
from torch import nn


class TinyVideoBackbone(nn.Module):
    """Backbone nhỏ chỉ dùng smoke test/offline debug."""
    out_dim = 128
    def __init__(self):
        super().__init__()
        self.net = nn.Sequential(
            nn.Conv3d(3, 32, 3, stride=(1,2,2), padding=1), nn.BatchNorm3d(32), nn.GELU(),
            nn.Conv3d(32, 64, 3, stride=(2,2,2), padding=1), nn.BatchNorm3d(64), nn.GELU(),
            nn.Conv3d(64, 128, 3, stride=(2,2,2), padding=1), nn.BatchNorm3d(128), nn.GELU(),
            nn.AdaptiveAvgPool3d(1), nn.Flatten(),
        )
    def forward(self, x): return self.net(x)


def make_video_backbone(name: str, pretrained: bool) -> tuple[nn.Module, int]:
    name = name.lower()
    if name == "tiny":
        m = TinyVideoBackbone(); return m, m.out_dim
    if name == "mvit_v2_s":
        from torchvision.models.video import mvit_v2_s, MViT_V2_S_Weights
        weights = MViT_V2_S_Weights.KINETICS400_V1 if pretrained else None
        m = mvit_v2_s(weights=weights)
        dim = int(m.head[1].in_features)
        m.head = nn.Identity()
        return m, dim
    if name == "swin3d_t":
        from torchvision.models.video import swin3d_t, Swin3D_T_Weights
        weights = Swin3D_T_Weights.KINETICS400_V1 if pretrained else None
        m = swin3d_t(weights=weights)
        dim = int(m.head.in_features)
        m.head = nn.Identity()
        return m, dim
    raise ValueError(f"Backbone không hỗ trợ: {name}")


class PoseTemporalEncoder(nn.Module):
    def __init__(self, model_dim: int):
        super().__init__()
        self.frame_proj = nn.Sequential(nn.Linear(33*3, 192), nn.LayerNorm(192), nn.GELU())
        self.temporal = nn.Sequential(
            nn.Conv1d(192, 192, kernel_size=3, padding=1), nn.GELU(),
            nn.Conv1d(192, model_dim, kernel_size=3, padding=1), nn.GELU(),
        )
    def forward(self, pose: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        # pose [B,S,F,33,3], visibility in channel 2
        b,s,f,j,c = pose.shape
        vis = pose[...,2].clamp(0,1)
        q = vis.mean(dim=(-1,-2))  # [B,S]
        p = pose.clone()
        # missing joints are explicitly downweighted.
        p[...,:2] = p[...,:2] * vis.unsqueeze(-1)
        x = self.frame_proj(p.reshape(b*s, f, j*c))       # [BS,F,192]
        x = self.temporal(x.transpose(1,2)).mean(dim=-1)  # [BS,D]
        return x.reshape(b,s,-1), q


class QualityFusion(nn.Module):
    def __init__(self, model_dim: int, n_modalities: int = 3):
        super().__init__()
        self.scorers = nn.ModuleList([
            nn.Sequential(nn.Linear(model_dim + 1, model_dim//2), nn.GELU(), nn.Linear(model_dim//2,1))
            for _ in range(n_modalities)
        ])
    def forward(self, mods: list[torch.Tensor], qualities: list[torch.Tensor]) -> tuple[torch.Tensor, torch.Tensor]:
        scores=[]
        for i,(m,q) in enumerate(zip(mods,qualities)):
            qq=q.unsqueeze(-1).clamp(0,1)
            scores.append(self.scorers[i](torch.cat([m,qq],dim=-1)))
        score=torch.cat(scores,dim=-1)  # [B,S,M]
        qstack=torch.stack(qualities,dim=-1).clamp(0,1)
        score=score + torch.log(qstack + 1e-4)
        weights=torch.softmax(score,dim=-1)
        fused=sum(m*weights[...,i:i+1] for i,m in enumerate(mods))
        return fused, weights


@dataclass
class HTBAMXOutput:
    behavior_logits: torch.Tensor
    atomic_logits: torch.Tensor
    embedding: torch.Tensor
    modality_weights: torch.Tensor


class HTBAMX(nn.Module):
    """HTBAM-X V2: person-centric RGB video + pose + semantic quality-aware temporal model."""
    def __init__(
        self, semantic_dim: int = 14, num_behaviors: int = 4, num_atomic: int = 4,
        backbone: str = "mvit_v2_s", pretrained_backbone: bool = True,
        model_dim: int = 256, transformer_layers: int = 2, transformer_heads: int = 4,
        ff_dim: int = 768, dropout: float = 0.2, precomputed_rgb_dim: int | None = None,
    ):
        super().__init__()
        if precomputed_rgb_dim is not None:
            if int(precomputed_rgb_dim) <= 0:
                raise ValueError("precomputed_rgb_dim phải > 0")
            self.rgb_backbone = None
            rgb_dim = int(precomputed_rgb_dim)
        else:
            self.rgb_backbone, rgb_dim = make_video_backbone(backbone, pretrained_backbone)
        self.rgb_proj = nn.Sequential(nn.Linear(rgb_dim, model_dim), nn.LayerNorm(model_dim), nn.GELU())
        self.semantic_encoder = nn.Sequential(
            nn.Linear(semantic_dim*2, 192), nn.LayerNorm(192), nn.GELU(), nn.Dropout(dropout),
            nn.Linear(192, model_dim), nn.LayerNorm(model_dim), nn.GELU(),
        )
        self.pose_encoder = PoseTemporalEncoder(model_dim)
        self.fusion = QualityFusion(model_dim, 3)
        self.tcn = nn.Sequential(
            nn.Conv1d(model_dim, model_dim, 3, padding=1), nn.GELU(), nn.Dropout(dropout),
            nn.Conv1d(model_dim, model_dim, 3, padding=1), nn.GELU(),
        )
        enc_layer = nn.TransformerEncoderLayer(
            d_model=model_dim, nhead=transformer_heads, dim_feedforward=ff_dim,
            dropout=dropout, activation="gelu", batch_first=True, norm_first=True,
        )
        self.temporal = nn.TransformerEncoder(enc_layer, num_layers=transformer_layers)
        self.norm = nn.LayerNorm(model_dim)
        self.behavior_head = nn.Linear(model_dim, num_behaviors)
        self.atomic_head = nn.Linear(model_dim, num_atomic)
        self.embedding_head = nn.Sequential(nn.Linear(model_dim, 128), nn.LayerNorm(128))

    def freeze_rgb(self, frozen: bool = True) -> None:
        if self.rgb_backbone is None:
            return
        for p in self.rgb_backbone.parameters():
            p.requires_grad = not frozen

    def forward(self, video, semantic, semantic_mask, pose, rgb_quality=None, rgb_embedding=None, temporal_mask=None) -> HTBAMXOutput:
        # Either raw video [B,S,C,F,H,W] or a frozen precomputed RGB embedding [B,S,R].
        if rgb_embedding is not None and rgb_embedding.numel() > 0:
            b,s,_=rgb_embedding.shape
            rgb=self.rgb_proj(rgb_embedding)
            base_device=rgb_embedding.device; base_dtype=rgb_embedding.dtype
        else:
            if self.rgb_backbone is None:
                raise RuntimeError("Model được khởi tạo cho precomputed RGB nhưng batch không có rgb_embedding")
            b,s,c,f,h,w=video.shape
            rgb_feats=[]
            for i in range(s):
                rgb_feats.append(self.rgb_backbone(video[:,i]))
            rgb=self.rgb_proj(torch.stack(rgb_feats,dim=1))
            base_device=video.device; base_dtype=video.dtype
        if temporal_mask is None:
            temporal_mask = torch.ones((b, s), device=base_device, dtype=torch.bool)
        else:
            temporal_mask = temporal_mask.to(device=base_device, dtype=torch.bool)
            if temporal_mask.shape != (b, s):
                raise ValueError(
                    f"temporal_mask shape sai: {tuple(temporal_mask.shape)}, expected {(b, s)}"
                )
            if torch.any(temporal_mask.sum(dim=1) == 0):
                raise ValueError("Moi sequence phai co it nhat 1 real timestep")

        valid_t = temporal_mask.to(dtype=rgb.dtype).unsqueeze(-1)

        rgb_q = rgb_quality.float().clamp(0,1) if rgb_quality is not None else torch.ones((b,s),device=base_device,dtype=base_dtype)
        rgb_q = rgb_q * temporal_mask.to(dtype=rgb_q.dtype)
        rgb = rgb * valid_t

        sem_in = torch.cat([semantic*semantic_mask, semantic_mask], dim=-1)
        sem = self.semantic_encoder(sem_in) * valid_t
        sem_q = semantic_mask.mean(dim=-1) * temporal_mask.to(dtype=semantic_mask.dtype)

        pose_feat, pose_q = self.pose_encoder(pose)
        pose_feat = pose_feat * valid_t
        pose_q = pose_q * temporal_mask.to(dtype=pose_q.dtype)

        fused, weights = self.fusion([rgb, sem, pose_feat], [rgb_q, sem_q, pose_q])
        fused = fused * valid_t
        local = self.tcn(fused.transpose(1,2)).transpose(1,2) * valid_t

        key_padding_mask = ~temporal_mask
        hseq = self.temporal(
            fused + local,
            src_key_padding_mask=key_padding_mask,
        )
        hseq = self.norm(hseq) * valid_t
        return HTBAMXOutput(
            behavior_logits=self.behavior_head(hseq),
            atomic_logits=self.atomic_head(hseq),
            embedding=self.embedding_head(hseq),
            modality_weights=weights,
        )

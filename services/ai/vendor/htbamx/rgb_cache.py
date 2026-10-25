from __future__ import annotations
from pathlib import Path
import numpy as np
import pandas as pd
import torch

from .model import make_video_backbone
from .dataset import BehaviorWindowDataset


def cache_rgb_embeddings(manifest_csv: str | Path, backbone_name: str='mvit_v2_s', pretrained: bool=True,
                         device: str | None=None, overwrite: bool=False) -> Path:
    """Cache frozen Kinetics-pretrained RGB embeddings ONCE per 2-second person segment."""
    manifest_csv=Path(manifest_csv); df=pd.read_csv(manifest_csv)
    if df.empty: raise RuntimeError('D6 manifest rỗng')
    dev=torch.device(device or ('cuda' if torch.cuda.is_available() else 'cpu'))
    backbone,dim=make_video_backbone(backbone_name,pretrained)
    backbone=backbone.to(dev).eval()
    emb_paths=[]
    with torch.no_grad():
        for r in df.itertuples(index=False):
            cache_path=Path(str(r.cache_path)); ep=cache_path.with_suffix(f'.{backbone_name}.npy')
            if not ep.exists() or overwrite:
                with np.load(cache_path) as z: video=z['video']
                x=BehaviorWindowDataset._normalize_video(video).unsqueeze(0).to(dev)
                e=backbone(x).squeeze(0).detach().float().cpu().numpy()
                if e.shape!=(dim,): raise RuntimeError(f'Embedding shape sai: {e.shape}, expected {(dim,)}')
                np.save(ep,e.astype(np.float32))
            emb_paths.append(str(ep.resolve()))
    df['rgb_embedding_path']=emb_paths
    df.to_csv(manifest_csv,index=False)
    return manifest_csv

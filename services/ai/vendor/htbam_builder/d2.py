from __future__ import annotations
from pathlib import Path
import random
import pandas as pd


def download_celeba(root: str | Path, accept_license: bool) -> Path:
    if not accept_license:
        raise RuntimeError(
            "CelebA is research-only/non-commercial. Re-run with --accept-license after reading "
            "https://mmlab.ie.cuhk.edu.hk/projects/CelebA.html"
        )
    try:
        from torchvision.datasets import CelebA
    except ImportError as e:
        raise RuntimeError("Install torch, torchvision and gdown first") from e
    root = Path(root)
    root.mkdir(parents=True, exist_ok=True)
    ds = CelebA(root=str(root), split="all", target_type="identity", download=True)
    # Torchvision keeps aligned images at root/celeba/img_align_celeba and exposes filenames/identity.
    identities = ds.identity[:, 0].cpu().numpy().astype(int)
    rows = []
    for filename, identity in zip(ds.filename, identities):
        rows.append({
            "image_path": str(Path("celeba") / "img_align_celeba" / filename),
            "identity_id": int(identity),
        })
    manifest = root / "all_manifest.csv"
    pd.DataFrame(rows).to_csv(manifest, index=False)
    return manifest


def split_identities_identity_disjoint(
    manifest_df: pd.DataFrame,
    train_identity_ratio: float = 0.90,
    min_images_per_identity: int = 5,
    seed: int = 2026,
) -> tuple[pd.DataFrame, pd.DataFrame]:
    required = {"image_path", "identity_id"}
    if not required.issubset(manifest_df.columns):
        raise ValueError(f"Manifest missing columns: {sorted(required - set(manifest_df.columns))}")
    if not 0 < train_identity_ratio < 1:
        raise ValueError("train_identity_ratio must be in (0,1)")
    counts = manifest_df.groupby("identity_id").size()
    keep_ids = counts[counts >= min_images_per_identity].index.tolist()
    if len(keep_ids) < 2:
        raise ValueError("Not enough identities after min_images_per_identity filtering")
    rng = random.Random(seed)
    rng.shuffle(keep_ids)
    n_train = max(1, min(len(keep_ids)-1, int(round(len(keep_ids)*train_identity_ratio))))
    train_ids = set(keep_ids[:n_train])
    val_ids = set(keep_ids[n_train:])
    train = manifest_df[manifest_df.identity_id.isin(train_ids)].copy().reset_index(drop=True)
    val = manifest_df[manifest_df.identity_id.isin(val_ids)].copy().reset_index(drop=True)
    label_map = {identity: idx for idx, identity in enumerate(sorted(train_ids))}
    train["class_index"] = train.identity_id.map(label_map).astype(int)
    val["class_index"] = -1
    assert set(train.identity_id).isdisjoint(set(val.identity_id)), "Identity leakage between train and val"
    return train, val


def make_verification_pairs(
    val_df: pd.DataFrame,
    num_positive: int = 2000,
    num_negative: int = 2000,
    seed: int = 2026,
) -> pd.DataFrame:
    rng = random.Random(seed)
    groups = {int(k): g.image_path.tolist() for k, g in val_df.groupby("identity_id") if len(g) >= 2}
    ids = sorted(groups)
    if len(ids) < 2:
        raise ValueError("Need at least 2 held-out identities with >=2 images to create verification pairs")
    positives = set()
    attempts = 0
    max_attempts = max(1000, num_positive*20)
    while len(positives) < num_positive and attempts < max_attempts:
        identity = rng.choice(ids)
        a, b = rng.sample(groups[identity], 2)
        key = tuple(sorted((a, b)))
        positives.add(key)
        attempts += 1
    negatives = set()
    attempts = 0
    max_attempts = max(1000, num_negative*20)
    while len(negatives) < num_negative and attempts < max_attempts:
        ia, ib = rng.sample(ids, 2)
        a, b = rng.choice(groups[ia]), rng.choice(groups[ib])
        key = tuple(sorted((a, b)))
        negatives.add(key)
        attempts += 1
    rows = [{"image_a":a,"image_b":b,"same":1} for a,b in sorted(positives)]
    rows += [{"image_a":a,"image_b":b,"same":0} for a,b in sorted(negatives)]
    rng.shuffle(rows)
    return pd.DataFrame(rows)


def prepare_celeba(
    root: str | Path,
    train_identity_ratio: float = 0.90,
    min_images_per_identity: int = 5,
    num_positive_pairs: int = 2000,
    num_negative_pairs: int = 2000,
    seed: int = 2026,
) -> dict[str, Path]:
    root = Path(root)
    all_manifest = root / "all_manifest.csv"
    if not all_manifest.exists():
        raise FileNotFoundError(f"Run the D2 downloader first: {all_manifest}")
    df = pd.read_csv(all_manifest)
    train, val = split_identities_identity_disjoint(df, train_identity_ratio, min_images_per_identity, seed)
    pairs = make_verification_pairs(val, num_positive_pairs, num_negative_pairs, seed)
    train_path, val_path, pairs_path = root/"train_manifest.csv", root/"val_manifest.csv", root/"val_pairs.csv"
    train.to_csv(train_path,index=False); val.to_csv(val_path,index=False); pairs.to_csv(pairs_path,index=False)
    return {"train":train_path,"val":val_path,"pairs":pairs_path}

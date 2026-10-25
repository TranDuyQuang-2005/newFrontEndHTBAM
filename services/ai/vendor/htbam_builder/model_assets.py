from __future__ import annotations

from dataclasses import dataclass, asdict
from pathlib import Path
from typing import Any
import hashlib
import json
import shutil


MIN_WEIGHT_BYTES = 1_000_000


@dataclass(frozen=True)
class WeightSelection:
    role: str
    path: Path
    source: str  # custom | pretrained
    class_id: int
    asset_name: str | None = None

    def as_dict(self) -> dict[str, Any]:
        d = asdict(self)
        d["path"] = str(self.path)
        return d


def _valid_file(path: Path) -> bool:
    return path.is_file() and path.stat().st_size >= MIN_WEIGHT_BYTES


def sha256_file(path: str | Path) -> str:
    h = hashlib.sha256()
    with Path(path).open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def _resolve(root: Path, value: str | Path | None) -> Path | None:
    if value in (None, ""):
        return None
    p = Path(value)
    return p if p.is_absolute() else root / p


def prepare_yolo_asset(
    project_root: str | Path,
    asset_name: str,
    target: str | Path,
    *,
    release: str = "v8.4.0",
    overwrite: bool = False,
) -> Path:
    """Download an official Ultralytics release asset and copy it into project/pretrained.

    This intentionally uses Ultralytics' own asset downloader instead of hard-coding a mirror.
    The official docs expose attempt_download_asset() and currently use release v8.4.0 for YOLO26.
    """
    root = Path(project_root).resolve()
    target_path = _resolve(root, target)
    assert target_path is not None
    target_path.parent.mkdir(parents=True, exist_ok=True)

    if _valid_file(target_path) and not overwrite:
        return target_path

    try:
        from ultralytics.utils.downloads import attempt_download_asset
    except ImportError as e:
        raise RuntimeError(
            "Thiếu ultralytics. Cài requirements-vision.txt trước khi tải YOLO weights."
        ) from e

    downloaded = Path(attempt_download_asset(asset_name, repo="ultralytics/assets", release=release))
    if not downloaded.is_file():
        raise RuntimeError(f"Ultralytics không tải được asset {asset_name}: {downloaded}")
    if downloaded.stat().st_size < MIN_WEIGHT_BYTES:
        raise RuntimeError(f"YOLO asset quá nhỏ/có vẻ hỏng: {downloaded}")

    if downloaded.resolve() != target_path.resolve():
        shutil.copy2(downloaded, target_path)
    if not _valid_file(target_path):
        raise RuntimeError(f"Weight sau khi copy không hợp lệ: {target_path}")
    return target_path


def validate_yolo_weight(
    path: str | Path,
    *,
    required_class_ids: dict[int, str] | None = None,
) -> dict[str, Any]:
    """Load a YOLO checkpoint and verify it is a detection model with required classes."""
    p = Path(path)
    if not _valid_file(p):
        raise FileNotFoundError(f"YOLO weight thiếu hoặc hỏng: {p}")
    try:
        from ultralytics import YOLO
    except ImportError as e:
        raise RuntimeError("Thiếu ultralytics để validate YOLO weight") from e

    model = YOLO(str(p))
    task = getattr(model, "task", None)
    if task not in (None, "detect"):
        raise ValueError(f"{p} không phải detection checkpoint (task={task})")
    names_raw = getattr(model, "names", {}) or {}
    if isinstance(names_raw, list):
        names = {i: n for i, n in enumerate(names_raw)}
    else:
        names = {int(k): str(v) for k, v in dict(names_raw).items()}

    for cid, expected_name in (required_class_ids or {}).items():
        if cid not in names:
            raise ValueError(f"{p} không có class_id={cid}; classes={sorted(names)[:20]}...")
        actual = names[cid].strip().lower().replace("_", " ")
        expected = expected_name.strip().lower().replace("_", " ")
        if actual != expected:
            raise ValueError(f"{p}: class_id={cid} là '{names[cid]}', không phải '{expected_name}'")

    return {
        "path": str(p.resolve()),
        "size_bytes": p.stat().st_size,
        "sha256": sha256_file(p),
        "task": task or "detect",
        "num_classes": len(names),
    }


def select_person_detector(project_root: str | Path, tracking_cfg: dict) -> WeightSelection:
    root = Path(project_root).resolve()
    custom = _resolve(root, tracking_cfg.get("custom_weights"))
    pretrained = _resolve(root, tracking_cfg.get("pretrained_weights"))
    class_id = int(tracking_cfg.get("person_class_id", 0))
    asset = str(tracking_cfg.get("pretrained_asset", "yolo26m.pt"))

    if custom is not None and _valid_file(custom):
        return WeightSelection("person_detector", custom, "custom", class_id, None)
    if pretrained is not None and _valid_file(pretrained):
        return WeightSelection("person_detector", pretrained, "pretrained", class_id, asset)
    raise FileNotFoundError(
        "Không tìm thấy Person YOLO weights. Chạy: python scripts/00_prepare_models.py\n"
        f"Custom checked: {custom}\nPretrained checked: {pretrained}"
    )


def select_phone_detector(project_root: str | Path, d6_cfg: dict) -> WeightSelection:
    root = Path(project_root).resolve()
    custom = _resolve(root, d6_cfg.get("phone_custom_weights"))
    pretrained = _resolve(root, d6_cfg.get("phone_pretrained_weights"))
    asset = str(d6_cfg.get("phone_pretrained_asset", "yolo26m.pt"))

    if custom is not None and _valid_file(custom):
        return WeightSelection(
            "phone_detector", custom, "custom",
            int(d6_cfg.get("phone_custom_class_id", 0)), None,
        )
    if pretrained is not None and _valid_file(pretrained):
        return WeightSelection(
            "phone_detector", pretrained, "pretrained",
            int(d6_cfg.get("phone_pretrained_class_id", 67)), asset,
        )
    raise FileNotFoundError(
        "Không tìm thấy Phone YOLO weights. Chạy: python scripts/00_prepare_models.py\n"
        f"Custom checked: {custom}\nPretrained checked: {pretrained}"
    )



def prepare_google_drive_asset(
    project_root: str | Path,
    file_id: str,
    target: str | Path,
    *,
    overwrite: bool = False,
) -> Path:
    """Download a pinned pretrained asset from Google Drive using gdown.

    Used for the official Torchreid model-zoo checkpoint. The caller supplies the
    exact file id so the project never silently changes models.
    """
    root = Path(project_root).resolve()
    target_path = _resolve(root, target)
    assert target_path is not None
    target_path.parent.mkdir(parents=True, exist_ok=True)
    if _valid_file(target_path) and not overwrite:
        return target_path
    try:
        import gdown
    except ImportError as e:
        raise RuntimeError(
            "Thiếu gdown để tải OSNet ReID checkpoint. Cài requirements-vision.txt trước."
        ) from e
    if target_path.exists() and overwrite:
        target_path.unlink()
    result = gdown.download(id=str(file_id), output=str(target_path), quiet=False)
    if result is None or not _valid_file(target_path):
        raise RuntimeError(
            "Không tải được OSNet ReID checkpoint từ Google Drive. "
            f"file_id={file_id}; target={target_path}"
        )
    return target_path


def validate_binary_weight(path: str | Path, *, role: str) -> dict[str, Any]:
    p = Path(path)
    if not _valid_file(p):
        raise FileNotFoundError(f"{role} weight thiếu hoặc hỏng: {p}")
    return {
        "path": str(p.resolve()),
        "size_bytes": p.stat().st_size,
        "sha256": sha256_file(p),
    }

def write_model_manifest(path: str | Path, records: list[dict[str, Any]]) -> Path:
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding="utf-8")
    return p

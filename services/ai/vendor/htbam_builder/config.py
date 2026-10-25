from __future__ import annotations

from pathlib import Path
from typing import Any
import yaml


def load_config(path: str | Path) -> dict[str, Any]:
    path = Path(path)
    if not path.exists():
        raise FileNotFoundError(f"Config not found: {path}")
    with path.open("r", encoding="utf-8") as f:
        cfg = yaml.safe_load(f) or {}
    required = ["paths", "tracking", "face", "body", "reentry", "d6", "split"]
    missing = [k for k in required if k not in cfg]
    if missing:
        raise ValueError(f"Missing config sections: {missing}")
    return cfg


def resolve_path(root: str | Path, value: str | Path) -> Path:
    value = Path(value)
    if value.is_absolute():
        return value
    return Path(root) / value

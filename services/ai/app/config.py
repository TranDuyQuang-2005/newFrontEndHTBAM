from __future__ import annotations
from dataclasses import dataclass
from pathlib import Path
import os

@dataclass(frozen=True)
class AiSettings:
    model_dir: Path
    device: str
    full_session_pipeline: bool

    @staticmethod
    def from_env() -> "AiSettings":
        raw = os.getenv("HTBAM_MODEL_DIR", "/models/htbamx")
        device = os.getenv("HTBAM_DEVICE", "auto").strip().lower()
        full = os.getenv("HTBAM_FULL_SESSION_PIPELINE", "false").strip().lower() in {"1","true","yes","on"}
        return AiSettings(Path(raw), device, full)

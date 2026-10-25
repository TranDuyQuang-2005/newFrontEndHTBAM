from __future__ import annotations

from typing import Iterable, Any

CUDA_PROVIDER = "CUDAExecutionProvider"


def prepare_onnxruntime_for_providers(
    providers: Iterable[str] | None,
    ort_module: Any | None = None,
) -> None:
    """Preload ONNX Runtime CUDA/cuDNN DLLs before InsightFace creates sessions.

    ONNX Runtime can report CUDAExecutionProvider as *available* while the provider DLL
    still cannot be loaded because CUDA/cuDNN DLL directories are not on Windows' DLL
    search path.  onnxruntime.preload_dlls(directory="") loads DLLs installed by the
    ``onnxruntime-gpu[cuda,cudnn]`` extras.  This must happen before FaceAnalysis() creates
    any InferenceSession.

    CPU-only configurations are untouched.
    """
    requested = [str(p) for p in (providers or [])]
    if CUDA_PROVIDER not in requested:
        return

    if ort_module is None:
        try:
            import onnxruntime as ort_module  # type: ignore[no-redef]
        except ImportError as exc:
            raise RuntimeError(
                "CUDAExecutionProvider was requested but onnxruntime-gpu is not installed."
            ) from exc

    preload = getattr(ort_module, "preload_dlls", None)
    if callable(preload):
        try:
            # Empty string means: preload NVIDIA runtime DLLs provided by pip packages.
            preload(directory="")
        except TypeError:
            # Compatibility fallback for older ORT signatures.
            preload()
        except Exception as exc:
            raise RuntimeError(
                "Failed to preload ONNX Runtime CUDA/cuDNN DLLs. Install the pinned Windows "
                "runtime with: python -m pip install --upgrade --no-cache-dir "
                '"onnxruntime-gpu[cuda,cudnn]==1.29.0"'
            ) from exc

    available = list(getattr(ort_module, "get_available_providers")())
    if CUDA_PROVIDER not in available:
        raise RuntimeError(
            "CUDAExecutionProvider was requested but ONNX Runtime does not expose it. "
            f"Available providers: {available}. Install onnxruntime-gpu==1.29.0."
        )


def assert_insightface_cuda_active(app: Any, providers: Iterable[str] | None) -> None:
    """Fail fast if InsightFace silently fell back from CUDA to CPU."""
    requested = [str(p) for p in (providers or [])]
    if CUDA_PROVIDER not in requested:
        return

    models = getattr(app, "models", {}) or {}
    checked = 0
    failures: list[str] = []
    for name, model in models.items():
        session = getattr(model, "session", None)
        get_providers = getattr(session, "get_providers", None)
        if not callable(get_providers):
            continue
        checked += 1
        actual = list(get_providers())
        if CUDA_PROVIDER not in actual:
            failures.append(f"{name}: {actual}")

    if checked and failures:
        raise RuntimeError(
            "InsightFace requested CUDAExecutionProvider but one or more models fell back "
            "to CPU. Refusing to continue a long D3/D5 build. Provider details: "
            + "; ".join(failures)
            + ". Verify with an explicit ORT CUDA session and ensure ORT DLLs are preloaded."
        )

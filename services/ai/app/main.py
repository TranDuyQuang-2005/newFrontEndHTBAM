from fastapi import FastAPI, HTTPException

from .config import AiSettings
from .htbam.runtime import HtbamRuntime
from .models import (
    StartJobRequest,
    StartJobResponse,
    StopJobResponse,
    CapabilitiesResponse,
    FaceEnrollmentManifest,
    StartFaceEnrollmentResponse,
    HtbamPredictRequest,
    HtbamPredictResponse,
)

app = FastAPI(title="HTBAM AI Service", version="3.1.0-htbamx-stage1")
settings = AiSettings.from_env()
htbam = HtbamRuntime(settings.model_dir, settings.device)

@app.get("/health")
async def health():
    return {
        "status": "ok",
        "mode": "HTBAM_CLASSIFIER_READY" if htbam.ready else "CONTRACT_ONLY",
        "loadedModels": htbam.loaded_models,
        "htbam": htbam.status(),
    }

@app.get("/capabilities", response_model=CapabilitiesResponse)
async def capabilities():
    return CapabilitiesResponse(
        sessionInference=False,
        faceEnrollment=False,
        annotatedVideoOutput=False,
        loadedModels=htbam.loaded_models,
    )

@app.post("/htbam/predict", response_model=HtbamPredictResponse)
async def htbam_predict(req: HtbamPredictRequest):
    if not htbam.ready:
        raise HTTPException(503, htbam.error or "HTBAM-X classifier is not loaded")
    try:
        return HtbamPredictResponse(**htbam.predict(req))
    except ValueError as exc:
        raise HTTPException(422, str(exc))
    except Exception as exc:
        raise HTTPException(500, f"HTBAM-X inference failed: {exc}")

@app.post("/jobs/start", response_model=StartJobResponse)
async def start_job(req: StartJobRequest):
    raise HTTPException(
        503,
        "HTBAM-X classifier is integrated, but full Session inference still requires "
        "YOLO/BoT-SORT + exact D6 feature extractor + RGB embedding runtime + identity resolver.",
    )

@app.post("/jobs/{job_id}/stop", response_model=StopJobResponse)
async def stop_job(job_id: str):
    raise HTTPException(503, "No full production inference job is running")

@app.post("/face-enrollments/start", response_model=StartFaceEnrollmentResponse)
async def start_face_enrollment(req: FaceEnrollmentManifest):
    raise HTTPException(503, "Face enrollment model is not available yet")

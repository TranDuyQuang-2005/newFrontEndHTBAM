from pathlib import Path
from uuid import uuid4
import os

from fastapi import BackgroundTasks, FastAPI, HTTPException, File, UploadFile
from fastapi.responses import FileResponse

from .config import AiSettings
from .face_enrollment_runtime import FaceEnrollmentRuntime
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
from .session_runtime import SessionInferenceManager

app = FastAPI(title="HTBAM AI Service", version="4.0.0-production-session")
settings = AiSettings.from_env()
htbam = HtbamRuntime(settings.model_dir, settings.device)
face_enrollment = FaceEnrollmentRuntime(Path(settings.model_dir).parent / "insightface")
session_manager = SessionInferenceManager(
    htbam,
    face_enrollment.app if face_enrollment.ready else None,
    Path(os.getenv("HTBAM_TRAINING_ROOT", "/training")),
    enabled=settings.full_session_pipeline,
)


def loaded_models() -> list[str]:
    return [*htbam.loaded_models, *face_enrollment.loaded_models, *session_manager.loaded_models]


@app.get("/health")
async def health():
    return {
        "status": "ok",
        "mode": "HTBAM_FULL_SESSION_READY" if session_manager.ready else (
            "HTBAM_CLASSIFIER_READY" if htbam.ready else "CONTRACT_ONLY"
        ),
        "loadedModels": loaded_models(),
        "htbam": htbam.status(),
        "faceEnrollment": face_enrollment.status(),
        "sessionPipeline": session_manager.status(),
    }


@app.get("/capabilities", response_model=CapabilitiesResponse)
async def capabilities():
    return CapabilitiesResponse(
        sessionInference=session_manager.ready,
        faceEnrollment=face_enrollment.ready,
        annotatedVideoOutput=True,
        loadedModels=loaded_models(),
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
    if not session_manager.ready:
        raise HTTPException(503, session_manager.error or "Session pipeline is not ready")
    try:
        return StartJobResponse(**session_manager.start(req))
    except ValueError as exc:
        raise HTTPException(422, str(exc))
    except Exception as exc:
        raise HTTPException(503, f"Cannot start Session inference: {exc}")


@app.get("/jobs/{job_id}")
async def get_job(job_id: str):
    try:
        return session_manager.job_status(job_id)
    except KeyError:
        raise HTTPException(404, "Job not found")


@app.post("/jobs/{job_id}/browser-chunks", status_code=202)
async def upload_browser_chunk(job_id: str, file: UploadFile = File(...)):
    try:
        data = await file.read()
        suffix = Path(file.filename or "webcam.webm").suffix or ".webm"
        return session_manager.enqueue_browser_chunk(job_id, data, suffix, file.filename)
    except KeyError:
        raise HTTPException(status_code=404, detail="Job not found")
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))
    except RuntimeError as exc:
        raise HTTPException(status_code=409, detail=str(exc))
    except Exception as exc:
        raise HTTPException(status_code=503, detail=f"Browser camera chunk rejected: {exc}")




@app.post("/jobs/{job_id}/fast-overlay")
async def fast_overlay(job_id: str, file: UploadFile = File(...)):
    try:
        return session_manager.fast_overlay(job_id, await file.read())
    except KeyError:
        raise HTTPException(status_code=404, detail="Job not found")
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))
    except FileNotFoundError as exc:
        raise HTTPException(status_code=503, detail=str(exc))
    except Exception as exc:
        raise HTTPException(status_code=503, detail=f"Realtime overlay failed: {exc}")

@app.get("/jobs/{job_id}/overlay-preview")
async def overlay_preview(job_id: str):
    try:
        path, generated_at, media_time, delay_sec = session_manager.overlay_preview(job_id)
    except KeyError:
        raise HTTPException(status_code=404, detail="Job not found")
    except FileNotFoundError:
        raise HTTPException(status_code=404, detail="AI overlay preview is not ready")
    return FileResponse(
        path,
        media_type="image/jpeg",
        headers={
            "Cache-Control": "no-store, no-cache, must-revalidate",
            "X-HTBAM-Generated-At": generated_at,
            "X-HTBAM-Media-Time": media_time,
            "X-HTBAM-Delay-Seconds": delay_sec,
        },
    )

@app.post("/jobs/{job_id}/stop", response_model=StopJobResponse)
async def stop_job(job_id: str):
    try:
        return StopJobResponse(**session_manager.stop(job_id))
    except KeyError:
        raise HTTPException(404, "Job not found")
    except Exception as exc:
        raise HTTPException(500, f"Cannot finalize AI job: {exc}")


@app.post("/face-enrollments/start", response_model=StartFaceEnrollmentResponse)
async def start_face_enrollment(req: FaceEnrollmentManifest, background_tasks: BackgroundTasks):
    if not face_enrollment.ready:
        raise HTTPException(503, face_enrollment.error or "Face enrollment runtime is not ready")
    job_id = f"face-enroll-{req.enrollmentId}-{uuid4().hex[:10]}"
    background_tasks.add_task(face_enrollment.process_job, req, job_id)
    return StartFaceEnrollmentResponse(jobId=job_id, status="STARTED")

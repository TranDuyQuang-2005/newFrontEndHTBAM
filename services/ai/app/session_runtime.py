from __future__ import annotations
import queue
import subprocess
import hashlib
import json

import inspect
import logging
import math
import os
import shutil
import threading
import time
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from io import BytesIO
from pathlib import Path
from typing import Any
from uuid import uuid4

import cv2
import httpx
import numpy as np
import pandas as pd
from PIL import Image, ImageDraw, ImageFont

import torch
LOGGER = logging.getLogger("htbam.session_runtime")
MODEL_VERSION = "htbamx-prod-20261022"
THRESHOLD_VERSION = "session-runtime-v1"
BEHAVIOR_THRESHOLD_VERSION = "htbamx-segment-v1"


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


def _l2(v: np.ndarray | None) -> np.ndarray | None:
    if v is None:
        return None
    x = np.asarray(v, dtype=np.float32).reshape(-1)
    if x.shape != (512,) or not np.isfinite(x).all():
        return None
    n = float(np.linalg.norm(x))
    if n <= 1e-12:
        return None
    return (x / n).astype(np.float32, copy=False)


def _npy_template(data: bytes, expected_dim: int = 512) -> np.ndarray:
    arr = np.load(BytesIO(data), allow_pickle=False)
    x = np.asarray(arr, dtype=np.float32).reshape(-1)
    if x.shape != (expected_dim,):
        raise ValueError(f"ArcFace template must be {expected_dim}-D, got {x.shape}")
    y = _l2(x)
    if y is None:
        raise ValueError("Invalid ArcFace template")
    return y


def _make_session_ref(SessionRef, class_id: str, session_id: str, video_path: Path):
    sig = inspect.signature(SessionRef)
    aliases = {
        "class_id": class_id, "class_name": class_id, "class_key": class_id,
        "session_id": session_id, "session_name": session_id, "session_key": session_id,
        "video_path": video_path, "path": video_path, "video": video_path, "source": video_path,
    }
    kwargs, unresolved = {}, []
    for name, param in sig.parameters.items():
        if name in aliases:
            kwargs[name] = aliases[name]
        elif param.default is inspect.Parameter.empty:
            unresolved.append(name)
    if unresolved:
        raise RuntimeError(f"Unsupported SessionRef signature {sig}; unresolved={unresolved}")
    return SessionRef(**kwargs)


@dataclass
class FacePrototype:
    vector: np.ndarray
    samples: int
    reliability: float


@dataclass
class StableState:
    stable_id: str
    student_id: int | None = None
    face_proto: np.ndarray | None = None
    last_media_sec: float = 0.0
    last_wall_time: datetime = field(default_factory=_utcnow)
    active: bool = True


@dataclass
class JobState:
    job_id: str
    request: Any
    work_dir: Path
    status: str = "RUNNING"
    error: str | None = None
    source_exhausted: bool = False
    stop_event: threading.Event = field(default_factory=threading.Event)
    worker: threading.Thread | None = None
    finalizer: threading.Thread | None = None
    artifact: dict[str, Any] | None = None
    stop_requested_at: datetime | None = None
    finished_at: datetime | None = None
    sequence: int = 0
    base_time: datetime = field(default_factory=_utcnow)
    stable_counter: int = 0
    stable_states: dict[str, StableState] = field(default_factory=dict)
    stable_by_student: dict[int, str] = field(default_factory=dict)
    last_behavior_end: dict[str, datetime] = field(default_factory=dict)
    lost_sent: set[str] = field(default_factory=set)
    event_ids: dict[str, str] = field(default_factory=dict)
    accepted_chunks: int = 0
    processed_chunks: int = 0
    behavior_events_emitted: int = 0
    browser_failed_chunks: set[int] = field(default_factory=set)
    browser_chunk_meta: dict[int, tuple[Path, Path | None, float, float]] = field(default_factory=dict)
    browser_chunk_receipts: dict[str, dict[str, Any]] = field(default_factory=dict)
    browser_ingest_lock: threading.Lock = field(default_factory=threading.Lock)
    recording_clips: dict[int, Path] = field(default_factory=dict)
    annotation_clips: dict[int, Path] = field(default_factory=dict)
    annotation_source_clips: dict[int, Path] = field(default_factory=dict)
    annotation_behavior_segments: dict[int, list[dict[str, Any]]] = field(default_factory=dict)
    annotation_stats: dict[int, dict[str, Any]] = field(default_factory=dict)
    browser_roster: list[dict[str, Any]] = field(default_factory=list)


class SessionInferenceManager:
    """VIDEO/CAMERA -> exact tracking/D6 -> HTBAM-X + ArcFace roster -> backend callbacks."""

    def __init__(self, classifier, face_app, training_root: str | Path, enabled: bool = True):
        self.classifier = classifier
        self.face_app = face_app
        self.training_root = Path(training_root)
        self.enabled = bool(enabled)
        self.ready = False
        self.error: str | None = None
        self.jobs: dict[str, JobState] = {}
        self.jobs_lock = threading.Lock()
        self.inference_lock = threading.Lock()

        self.chunk_seconds = max(4.0, float(os.getenv("HTBAM_CHUNK_SECONDS", "12")))
        self.identity_min_similarity = float(os.getenv("HTBAM_IDENTITY_MIN_SIMILARITY", "0.60"))
        self.identity_min_margin = float(os.getenv("HTBAM_IDENTITY_MIN_MARGIN", "0.10"))
        self.identity_min_samples = max(2, int(os.getenv("HTBAM_IDENTITY_MIN_FACE_SAMPLES", "2")))
        self.unknown_link_similarity = float(os.getenv("HTBAM_UNKNOWN_LINK_SIMILARITY", "0.55"))
        self.max_unknown_link_gap_sec = float(os.getenv("HTBAM_UNKNOWN_LINK_MAX_GAP_SEC", "120"))
        self.min_observation = float(os.getenv("HTBAM_MIN_OBSERVATION", "0.20"))
        self.stop_drain_timeout = max(30.0, float(os.getenv("HTBAM_STOP_DRAIN_TIMEOUT_SECONDS", "300")))
        self.browser_queue_size = max(3, int(os.getenv("HTBAM_BROWSER_QUEUE_SIZE", "120")))

        try:
            if not self.enabled:
                raise RuntimeError("HTBAM_FULL_SESSION_PIPELINE is disabled")
            if not getattr(classifier, "ready", False):
                raise RuntimeError(getattr(classifier, "error", None) or "HTBAM-X classifier is not ready")
            if face_app is None:
                raise RuntimeError("ArcFace/InsightFace runtime is not ready")
            config_path = self.training_root / "configs" / "config.yaml"
            if not config_path.is_file():
                raise FileNotFoundError(config_path)

            from htbam_builder.config import load_config
            from htbam_builder.core import SessionRef, add_tracklet_ids
            from htbam_builder.tracking import track_session
            from htbam_builder.d3 import assign_faces_to_tracks_with_diagnostics
            from htbamx.behavior_cache import build_behavior_segment_cache
            from app.htbam.segment_runtime import ExactSegmentInferenceCore

            self._SessionRef = SessionRef
            self._add_tracklet_ids = add_tracklet_ids
            self._track_session = track_session
            self._assign_faces = assign_faces_to_tracks_with_diagnostics
            self._build_behavior = build_behavior_segment_cache

            self.cfg = load_config(config_path)
            self.tracking_cfg = dict(self.cfg["tracking"])
            self.d6_cfg = dict(self.cfg["d6"])
            self.d3_cfg = dict(self.cfg.get("d3", self.cfg.get("face", {})))
            self.d6_cfg["overwrite_cache"] = True

            requested = getattr(classifier, "device", "cpu")
            self.segment_core = ExactSegmentInferenceCore(classifier, device=str(requested))
            self.ready = True
        except Exception as exc:
            self.error = f"{type(exc).__name__}: {exc}"
            LOGGER.exception("Session inference runtime initialization failed")

    @property
    def loaded_models(self) -> list[str]:
        return [f"SessionPipeline:{MODEL_VERSION}"] if self.ready else []

    def status(self) -> dict[str, Any]:
        with self.jobs_lock:
            running = sum(1 for j in self.jobs.values() if j.status == "RUNNING")
        return {
            "ready": self.ready,
            "error": self.error,
            "trainingRoot": str(self.training_root),
            "chunkSeconds": self.chunk_seconds,
            "identityMinSimilarity": self.identity_min_similarity,
            "identityMinMargin": self.identity_min_margin,
            "identityMinFaceSamples": self.identity_min_samples,
            "unknownLinkSimilarity": self.unknown_link_similarity,
            "minObservation": self.min_observation,
            "browserQueueSize": self.browser_queue_size,
            "runningJobs": running,
            "modelVersion": MODEL_VERSION,
        }

    def _preflight_source(self, request) -> None:
        source_type = str(request.sourceType).strip().upper()
        if source_type not in {"VIDEO", "CAMERA", "BROWSER"}:
            raise ValueError(f"Unsupported sourceType: {request.sourceType}")
        if not str(request.source).strip():
            raise ValueError("Empty session source")
        if source_type == "CAMERA":
            cap = cv2.VideoCapture(str(request.source))
            ok = cap.isOpened()
            cap.release()
            if not ok:
                raise RuntimeError("Không mở được nguồn camera/RTSP.")

    def start(self, request) -> dict[str, str]:
        if not self.ready:
            raise RuntimeError(self.error or "Session pipeline is not ready")
        self._preflight_source(request)
        job_id = f"session-{int(request.sessionId)}-{uuid4().hex[:12]}"
        work = Path("/tmp/htbam_session_jobs") / job_id
        work.mkdir(parents=True, exist_ok=True)
        job = JobState(job_id=job_id, request=request, work_dir=work)
        thread = threading.Thread(target=self._run_job, args=(job,), name=f"htbam-{job_id}", daemon=True)
        job.worker = thread
        with self.jobs_lock:
            self.jobs[job_id] = job
        thread.start()
        return {"jobId": job_id, "status": "RUNNING", "modelVersion": MODEL_VERSION}

    def stop(self, job_id: str) -> dict[str, Any]:
        with self.jobs_lock:
            job = self.jobs.get(job_id)
            if job is None:
                raise KeyError(f"Unknown job: {job_id}")
            if job.status == "COMPLETED":
                return self._job_response(job)
            if job.status == "FAILED":
                return self._job_response(job)
            if job.status == "FINALIZING" and job.finalizer is not None and job.finalizer.is_alive():
                return self._job_response(job)

            # Serialize the ingestion gate with chunk acceptance: after FINALIZING is
            # visible, no request can add a chunk behind the worker drain boundary.
            with job.browser_ingest_lock:
                job.error = None
                job.status = "FINALIZING"
                job.stop_requested_at = job.stop_requested_at or _utcnow()
                job.stop_event.set()
            finalizer = threading.Thread(
                target=self._finalize_job,
                args=(job,),
                name=f"htbam-finalize-{job.job_id}",
                daemon=True,
            )
            job.finalizer = finalizer
            response = self._job_response(job)
        finalizer.start()
        return response

    def _finalize_job(self, job: JobState) -> None:
        try:
            worker = job.worker
            while worker is not None and worker.is_alive() and worker is not threading.current_thread():
                worker.join(timeout=self.stop_drain_timeout)
                if worker.is_alive():
                    # Finalization already runs on a background thread. A large browser-camera
                    # backlog can legitimately take longer than one drain interval, especially
                    # on CPU. Keep the session FINALIZING while progress is still owned by the
                    # worker instead of turning a healthy backlog into a permanent failure.
                    LOGGER.warning(
                        "Session job %s is still draining after %.0fs accepted=%s processed=%s failed=%s",
                        job.job_id,
                        self.stop_drain_timeout,
                        job.accepted_chunks,
                        job.processed_chunks,
                        len(job.browser_failed_chunks),
                    )
            if job.error:
                with self.jobs_lock:
                    job.status = "FAILED"
                return

            self._retry_failed_browser_chunks(job)
            self._finalize_video_artifact(job)
            # Worker callbacks are durable before terminal identity callbacks and before
            # COMPLETED becomes externally visible.
            self._send_lost_for_all(job)
            with self.jobs_lock:
                job.status = "COMPLETED"
                job.finished_at = _utcnow()
                job.error = None
        except Exception as exc:
            with self.jobs_lock:
                job.error = f"{type(exc).__name__}: {exc}"
                job.status = "FINALIZE_FAILED"
            LOGGER.exception("Session job %s finalize failed", job.job_id)

    @staticmethod
    def _job_response(job: JobState) -> dict[str, Any]:
        return {"jobId": job.job_id, "status": job.status, "artifact": job.artifact, "error": job.error}

    def job_status(self, job_id: str) -> dict[str, Any]:
        with self.jobs_lock:
            job = self.jobs.get(job_id)
        if job is None:
            raise KeyError(job_id)
        return {
            "jobId": job.job_id,
            "status": job.status,
            "error": job.error,
            "sourceExhausted": job.source_exhausted,
            "stableIdentities": len(job.stable_states),
            "artifact": job.artifact,
            "stopRequestedAt": job.stop_requested_at.isoformat() if job.stop_requested_at else None,
            "finishedAt": job.finished_at.isoformat() if job.finished_at else None,
            "acceptedChunks": job.accepted_chunks,
            "processedChunks": job.processed_chunks,
            "failedChunks": len(job.browser_failed_chunks),
            "behaviorEventsEmitted": job.behavior_events_emitted,
        }

    def _next_event_id(self, job: JobState, kind: str) -> str:
        job.sequence += 1
        return f"{job.job_id}:{job.sequence:010d}:{kind}"

    def _post_event(self, job: JobState, payload: dict[str, Any]) -> None:
        # Events produced by a chunk already accepted before stop must still be persisted.
        # New browser chunks are rejected once status becomes FINALIZING.
        payload = dict(payload)
        event_type = str(payload.get("eventType", "EVENT"))
        canonical = json.dumps(payload, sort_keys=True, separators=(",", ":"), default=str)
        event_key = hashlib.sha256(canonical.encode("utf-8")).hexdigest()
        payload["eventId"] = job.event_ids.setdefault(
            event_key,
            f"{job.job_id}:{event_type[:24]}:{event_key[:24]}",
        )
        payload["sessionId"] = int(job.request.sessionId)
        payload.setdefault("modelVersion", MODEL_VERSION)
        payload.setdefault("thresholdVersion", THRESHOLD_VERSION)
        headers = {"X-AI-API-Key": str(job.request.callbackApiKey)}
        last = None
        for attempt in range(4):
            try:
                with httpx.Client(follow_redirects=True, timeout=30.0) as client:
                    res = client.post(str(job.request.callbackUrl), json=payload, headers=headers)
                    if (
                        res.status_code == 409
                        and str(payload.get("eventType", "")).upper() in {"TRACK_LOST", "IDENTITY_LOST"}
                    ):
                        LOGGER.info(
                            "Dropping stale terminal callback job=%s event=%s status=409",
                            job.job_id,
                            payload.get("eventType"),
                        )
                        return
                    if res.is_error:
                        detail = (res.text or res.reason_phrase or "callback rejected").strip()[:500]
                        raise RuntimeError(f"AI callback HTTP {res.status_code}: {detail}")
                    return
            except Exception as exc:
                last = exc
                LOGGER.warning("Callback failed %s attempt %s: %s", payload["eventId"], attempt + 1, exc)
                if attempt < 3:
                    time.sleep(0.8 * (attempt + 1))
        raise RuntimeError(f"AI callback failed: {last}")

    def _load_roster(self, request) -> list[dict[str, Any]]:
        roster = []
        with httpx.Client(follow_redirects=True, timeout=30.0) as client:
            for c in list(request.rosterCandidates or []):
                if int(c.embeddingDimension) != 512:
                    continue
                try:
                    res = client.get(str(c.templateReadUrl))
                    res.raise_for_status()
                    roster.append({
                        "student_id": int(c.studentId),
                        "student_code": str(getattr(c, "studentCode", "") or "").strip(),
                        "full_name": str(getattr(c, "fullName", "") or "").strip(),
                        "vector": _npy_template(res.content, 512),
                        "model_name": str(c.modelName),
                        "template_version": str(c.templateVersion),
                    })
                except Exception as exc:
                    LOGGER.warning("Unable to load roster template student=%s: %s", c.studentId, exc)
        return roster

    def _download_video(self, job: JobState) -> Path:
        source = str(job.request.source)
        dst = job.work_dir / "source.mp4"
        if source.lower().startswith(("http://", "https://")):
            with httpx.stream("GET", source, follow_redirects=True, timeout=120.0) as res:
                res.raise_for_status()
                with dst.open("wb") as f:
                    for block in res.iter_bytes(1024 * 1024):
                        if job.stop_event.is_set():
                            break
                        f.write(block)
            if not dst.is_file() or dst.stat().st_size == 0:
                raise RuntimeError("Không tải được video nguồn.")
            return dst
        src = Path(source)
        if not src.is_file():
            raise FileNotFoundError(source)
        shutil.copy2(src, dst)
        return dst

    def _chunk_stream(self, job: JobState):
        source_type = str(job.request.sourceType).upper()
        capture_source = self._download_video(job) if source_type == "VIDEO" else job.request.source
        source_has_audio = False
        if source_type == "VIDEO":
            source_has_audio = self._probe_has_audio(self._probe_media(Path(capture_source)))
        cap = cv2.VideoCapture(str(capture_source))
        if not cap.isOpened():
            raise RuntimeError("Không mở được nguồn video/camera.")
        try:
            fps = float(cap.get(cv2.CAP_PROP_FPS) or 0.0)
            if not math.isfinite(fps) or fps < 1.0 or fps > 120.0:
                fps = 30.0
            width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH) or 0) or 1280
            height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0) or 720
            frames_per_chunk = max(1, int(round(self.chunk_seconds * fps)))
            media_sec, chunk_index = 0.0, 0
            while not job.stop_event.is_set():
                clip = job.work_dir / f"chunk_{chunk_index:06d}.mp4"
                writer = cv2.VideoWriter(str(clip), cv2.VideoWriter_fourcc(*"mp4v"), fps, (width, height))
                if not writer.isOpened():
                    raise RuntimeError(f"Không tạo được chunk: {clip}")
                frames = 0
                while frames < frames_per_chunk and not job.stop_event.is_set():
                    ok, frame = cap.read()
                    if not ok:
                        break
                    if frame.shape[1] != width or frame.shape[0] != height:
                        frame = cv2.resize(frame, (width, height))
                    writer.write(frame)
                    frames += 1
                writer.release()
                if frames == 0:
                    clip.unlink(missing_ok=True)
                    break
                duration = frames / fps
                if duration < 1.5:
                    clip.unlink(missing_ok=True)
                    break
                if source_has_audio:
                    muxed = job.work_dir / f"chunk_{chunk_index:06d}_audio.tmp.mp4"
                    command = [
                        "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                        "-i", str(clip),
                        "-ss", f"{media_sec:.6f}", "-t", f"{duration:.6f}",
                        "-i", str(capture_source),
                        "-map", "0:v:0", "-map", "1:a:0",
                        "-c:v", "copy", "-c:a", "aac", "-b:a", "128k",
                        "-t", f"{duration:.6f}",
                        str(muxed),
                    ]
                    try:
                        proc = subprocess.run(
                            command,
                            capture_output=True,
                            text=True,
                            timeout=max(90, duration * 2),
                        )
                        if proc.returncode != 0 or not muxed.is_file() or muxed.stat().st_size <= 0:
                            detail = (proc.stderr or proc.stdout or "unknown ffmpeg error")[-1000:]
                            raise RuntimeError(f"Source audio preservation failed: {detail}")
                        muxed.replace(clip)
                    except subprocess.TimeoutExpired as exc:
                        raise RuntimeError("Source audio preservation timed out") from exc
                    finally:
                        self._safe_recording_unlink(muxed, job.work_dir)
                yield chunk_index, clip, media_sec, duration, fps
                media_sec += duration
                chunk_index += 1
                if source_type == "VIDEO" and frames < frames_per_chunk:
                    break
        finally:
            cap.release()

    def _track_chunk(self, job: JobState, chunk_index: int, clip: Path):
        class_id = f"WEB_SESSION_{int(job.request.sessionId)}"
        session_id = f"{job.job_id}_C{chunk_index:06d}"
        session_ref = _make_session_ref(self._SessionRef, class_id, session_id, clip.resolve())
        tracking_cfg = dict(self.tracking_cfg)
        if not torch.cuda.is_available():
            tracking_cfg["device"] = "cpu"
            if "half" in tracking_cfg:
                tracking_cfg["half"] = False
        track_csv = Path(self._track_session(session_ref, job.work_dir / "tracks", tracking_cfg, project_root=self.training_root))
        tracks = pd.read_csv(track_csv)
        if tracks.empty:
            return tracks, tracks, track_csv
        fps = float(tracks.iloc[0].fps)
        enriched = self._add_tracklet_ids(tracks, fps=fps, break_gap_sec=float(self.d6_cfg.get("tracklet_break_gap_sec", 0.50)))
        return tracks, enriched, track_csv

    def _face_prototypes(self, clip: Path, enriched: pd.DataFrame) -> dict[str, FacePrototype]:
        if enriched.empty:
            return {}
        selected_frames: set[int] = set()
        for _, g in enriched.groupby("tracklet_id", sort=False):
            idx = g["frame_index"].astype(int).to_numpy()
            picks = np.arange(len(idx)) if len(idx) <= 4 else np.linspace(0, len(idx) - 1, 4).round().astype(int)
            selected_frames.update(int(idx[int(p)]) for p in picks)

        samples: dict[str, list[np.ndarray]] = {}
        scores: dict[str, list[float]] = {}
        cap = cv2.VideoCapture(str(clip))
        if not cap.isOpened():
            return {}
        try:
            for frame_idx in sorted(selected_frames):
                cap.set(cv2.CAP_PROP_POS_FRAMES, frame_idx)
                ok, frame = cap.read()
                if not ok:
                    continue
                rows = enriched[enriched["frame_index"].astype(int).eq(frame_idx)]
                if rows.empty:
                    continue
                faces = []
                for face in self.face_app.get(frame):
                    emb = _l2(getattr(face, "normed_embedding", None))
                    if emb is None:
                        emb = _l2(getattr(face, "embedding", None))
                    if emb is None:
                        continue
                    faces.append({
                        "bbox": np.asarray(face.bbox, dtype=np.float32),
                        "kps": np.asarray(face.kps, dtype=np.float32),
                        "det_score": float(getattr(face, "det_score", 0.0) or 0.0),
                        "embedding": emb,
                    })
                if not faces:
                    continue
                assigned, _ = self._assign_faces(faces, rows, self.d3_cfg)
                for track_id, face in assigned.items():
                    match = rows[rows["track_id"].astype(int).eq(int(track_id))]
                    if match.empty:
                        continue
                    tid = str(match.iloc[0].tracklet_id)
                    emb = _l2(face.get("embedding"))
                    if emb is None:
                        continue
                    samples.setdefault(tid, []).append(emb)
                    scores.setdefault(tid, []).append(float(np.clip(float(face.get("association_score", 1.0)) * float(face.get("det_score", 0.0)), 0, 1)))
        finally:
            cap.release()

        out: dict[str, FacePrototype] = {}
        for tid, vecs in samples.items():
            if len(vecs) == 1:
                out[tid] = FacePrototype(vecs[0], 1, scores.get(tid, [0.5])[0])
                continue
            mat = np.stack(vecs).astype(np.float32)
            sims = mat @ mat.T
            seed = int(np.argmax(np.median(sims, axis=1)))
            keep = np.where(sims[seed] >= 0.45)[0]
            if len(keep) < 2:
                continue
            proto = _l2(mat[keep].mean(axis=0))
            if proto is None:
                continue
            consistency = float(np.mean(mat[keep] @ proto))
            assoc = float(np.mean([scores[tid][int(i)] for i in keep]))
            out[tid] = FacePrototype(proto, int(len(keep)), float(np.clip(0.65 * consistency + 0.35 * assoc, 0, 1)))
        return out

    def _roster_match(self, proto: FacePrototype | None, roster: list[dict[str, Any]]):
        if proto is None or proto.samples < self.identity_min_samples or not roster:
            return None, None, None
        scored = sorted([(float(np.dot(proto.vector, r["vector"])), int(r["student_id"])) for r in roster], reverse=True)
        best_score, best_student = scored[0]
        second = scored[1][0] if len(scored) > 1 else -1.0
        margin = float(best_score - second) if len(scored) > 1 else 1.0
        if best_score >= self.identity_min_similarity and margin >= self.identity_min_margin:
            return best_student, best_score, margin
        return None, best_score, margin

    def _new_stable(self, job: JobState, proto: FacePrototype | None, media_sec: float) -> StableState:
        job.stable_counter += 1
        state = StableState(
            stable_id=f"S{int(job.request.sessionId)}-{job.stable_counter:05d}",
            face_proto=proto.vector.copy() if proto is not None else None,
            last_media_sec=media_sec,
        )
        job.stable_states[state.stable_id] = state
        return state

    def _resolve_stables(self, job: JobState, enriched: pd.DataFrame, prototypes, roster, media_sec: float):
        result, claimed = {}, set()
        candidates = []
        for tid in sorted(enriched["tracklet_id"].astype(str).unique()):
            proto = prototypes.get(tid)
            student_id, score, margin = self._roster_match(proto, roster)
            candidates.append((student_id is not None, score or -1.0, tid, proto, student_id, score, margin))
        candidates.sort(reverse=True)

        for _, _, tid, proto, student_id, score, margin in candidates:
            state = None
            if student_id is not None:
                existing = job.stable_by_student.get(int(student_id))
                if existing and existing not in claimed:
                    state = job.stable_states[existing]
                elif not existing:
                    state = self._new_stable(job, proto, media_sec)
                    state.student_id = int(student_id)
                    job.stable_by_student[int(student_id)] = state.stable_id
            if state is None and proto is not None:
                best_state, best_sim = None, -1.0
                for other in job.stable_states.values():
                    if other.stable_id in claimed or other.face_proto is None:
                        continue
                    if media_sec - other.last_media_sec > self.max_unknown_link_gap_sec:
                        continue
                    if student_id is not None and other.student_id not in {None, int(student_id)}:
                        continue
                    sim = float(np.dot(proto.vector, other.face_proto))
                    if sim > best_sim:
                        best_sim, best_state = sim, other
                if best_state is not None and best_sim >= self.unknown_link_similarity:
                    state = best_state
            if state is None:
                state = self._new_stable(job, proto, media_sec)
            if student_id is not None and state.student_id is None and int(student_id) not in job.stable_by_student:
                state.student_id = int(student_id)
                job.stable_by_student[int(student_id)] = state.stable_id
            if proto is not None:
                if state.face_proto is None:
                    state.face_proto = proto.vector.copy()
                else:
                    merged = _l2(0.70 * state.face_proto + 0.30 * proto.vector)
                    if merged is not None:
                        state.face_proto = merged
            state.last_media_sec = media_sec
            state.last_wall_time = _utcnow()
            state.active = True
            claimed.add(state.stable_id)
            result[tid] = (state, score, margin)
        return result

    def _emit_track_identity(self, job, chunk_index, enriched, resolved, prototypes, chunk_offset):
        for tid, (state, score, margin) in resolved.items():
            g = enriched[enriched["tracklet_id"].astype(str).eq(tid)]
            if g.empty:
                continue
            track_id = int(g.iloc[0].track_id)
            ts = job.base_time + timedelta(seconds=chunk_offset + float(g["timestamp_sec"].min()))
            track_token = f"C{chunk_index:06d}:T{track_id}"
            quality = float(prototypes.get(tid).reliability if tid in prototypes else 0.5)
            self._post_event(job, {
                "eventType":"TRACK_ACTIVE","timestamp":ts.isoformat(),"stableId":state.stable_id,"trackId":track_token,
                "studentId":None,"identityConfidence":None,"identityMargin":None,"behaviorLabel":None,"behaviorProbability":None,
                "observationQuality":quality,"alertType":None,"alertDurationSeconds":None,"startedAt":None,"endedAt":None,"identityDecision":None,
            })
            if state.student_id is not None and score is not None:
                self._post_event(job, {
                    "eventType":"IDENTITY_MATCH","timestamp":ts.isoformat(),"stableId":state.stable_id,"trackId":track_token,
                    "studentId":int(state.student_id),"identityConfidence":float(np.clip(score,0,1)),"identityMargin":float(max(0.0,margin or 0.0)),
                    "behaviorLabel":None,"behaviorProbability":None,"observationQuality":quality,"alertType":None,"alertDurationSeconds":None,
                    "startedAt":None,"endedAt":None,"identityDecision":"ACCEPTED",
                    "thresholdVersion":f"arcface-roster-provisional-v1:sim={self.identity_min_similarity:.2f}:margin={self.identity_min_margin:.2f}:samples={self.identity_min_samples}",
                })

    def _behavior_chunk(self, job, chunk_index, track_csv: Path, resolved, chunk_offset: float) -> int:
        try:
            manifest_path = Path(self._build_behavior(
                track_csv,
                job.work_dir / "d6_cache",
                self.d6_cfg,
                project_root=self.training_root,
            ))
        except RuntimeError as exc:
            # A short/sparse chunk can contain real tracked people but still be too short
            # for D6 to form a temporal window. That means "no behavior observation",
            # not corrupt media. Preserve an explicitly empty behavior list so rendering
            # can continue without inventing a label; all other D6 errors remain fatal.
            if not str(exc).startswith("D6 không tạo được segment nào cho "):
                raise
            job.annotation_behavior_segments[chunk_index] = []
            LOGGER.info(
                "Session job %s chunk=%s has no eligible D6 behavior window",
                job.job_id,
                chunk_index,
            )
            return 0
        manifest = pd.read_csv(manifest_path)
        segments: list[dict[str, Any]] = []
        job.annotation_behavior_segments[chunk_index] = segments
        if manifest.empty:
            return 0
        sent = 0
        manifest = manifest.sort_values(["start_sec","tracklet_id","segment_index"]).reset_index(drop=True)
        for row in manifest.itertuples(index=False):
            tid = str(row.tracklet_id)
            if tid not in resolved:
                continue
            state, _, _ = resolved[tid]
            with np.load(Path(str(row.cache_path))) as z:
                frame_valid = z["frame_valid"].astype(np.float32) if "frame_valid" in z.files else np.ones(len(z["video"]),np.float32)
                with self.inference_lock:
                    pred = self.segment_core.predict_segment(
                        state.stable_id,z["video"].astype(np.uint8),z["semantic"].astype(np.float32),
                        z["semantic_mask"].astype(np.float32),z["pose"].astype(np.float32),frame_valid,
                    )
            obs = float(np.clip(float(getattr(row,"observability",pred.get("rgbQuality",0.0))),0,1))
            label, probability = str(pred["label"]).upper(), float(pred["probability"])
            if obs < self.min_observation:
                label, probability = "OUT_OF_VIEW", float(np.clip(1.0-obs,0,1))
            local_start = float(row.start_sec)
            local_end = float(row.end_sec)
            start = job.base_time + timedelta(seconds=chunk_offset+float(row.start_sec))
            end = job.base_time + timedelta(seconds=chunk_offset+float(row.end_sec))
            last = job.last_behavior_end.get(state.stable_id)
            if last is not None and start < last:
                start = last
            if end <= start:
                continue
            self._post_event(job, {
                "eventType":"BEHAVIOR_SEGMENT","timestamp":end.isoformat(),"stableId":state.stable_id,
                "trackId":f"C{chunk_index:06d}:T{int(row.track_id)}","studentId":None,
                "identityConfidence":None,"identityMargin":None,"behaviorLabel":label,"behaviorProbability":probability,
                "observationQuality":obs,"alertType":None,"alertDurationSeconds":None,
                "modelVersion":str(pred.get("modelVersion",MODEL_VERSION)),"thresholdVersion":BEHAVIOR_THRESHOLD_VERSION,
                "startedAt":start.isoformat(),"endedAt":end.isoformat(),"identityDecision":None,
            })
            segments.append({
                "trackletId": tid,
                "stableId": state.stable_id,
                "trackId": int(row.track_id),
                "startSec": local_start,
                "endSec": local_end,
                "label": label,
                "probability": probability,
                "observationQuality": obs,
            })
            if not hasattr(job, "overlay_behaviors"):
                job.overlay_behaviors = {}
            job.overlay_behaviors[state.stable_id] = {
                "label": label,
                "probability": float(probability),
                "endedAt": end.isoformat(),
            }
            job.last_behavior_end[state.stable_id]=end
            sent+=1
        return sent


    def _annotation_font(self, size: int):
        cache = getattr(self, "_annotation_font_cache", None)
        if cache is None:
            cache = {}
            self._annotation_font_cache = cache
        if size in cache:
            return cache[size]
        configured = str(os.getenv("HTBAM_ANNOTATION_FONT", "")).strip()
        candidates = [
            configured,
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "C:/Windows/Fonts/arial.ttf",
        ]
        for candidate in candidates:
            if not candidate or not Path(candidate).is_file():
                continue
            try:
                font = ImageFont.truetype(candidate, size=size)
                cache[size] = font
                return font
            except OSError:
                continue
        raise RuntimeError(
            "No Unicode annotation font is available. Install fonts-dejavu-core or set HTBAM_ANNOTATION_FONT"
        )

    @staticmethod
    def _behavior_for_frame(
        segments_by_tracklet: dict[str, list[dict[str, Any]]],
        tracklet_id: str,
        timestamp_sec: float,
    ) -> dict[str, Any] | None:
        matched = None
        for segment in segments_by_tracklet.get(tracklet_id, []):
            if float(segment["startSec"]) <= timestamp_sec <= float(segment["endSec"]):
                matched = segment
            elif float(segment["startSec"]) > timestamp_sec:
                break
        return matched

    def _render_annotated_chunk(
        self,
        job: JobState,
        clip: Path,
        chunk_index: int,
        enriched: pd.DataFrame,
        resolved: dict[str, Any],
        roster: list[dict[str, Any]],
        chunk_offset: float,
    ) -> Path:
        required = {"frame_index", "track_id", "tracklet_id", "x1", "y1", "x2", "y2"}
        missing = sorted(required - set(enriched.columns))
        if missing:
            raise RuntimeError(f"Annotation render is missing tracking columns: {', '.join(missing)}")
        if enriched.empty:
            raise RuntimeError("Annotation render has no real person detections")

        cap = cv2.VideoCapture(str(clip))
        if not cap.isOpened():
            raise RuntimeError(f"Annotation render cannot open source clip {clip.name}")
        fps = float(cap.get(cv2.CAP_PROP_FPS) or 0.0)
        width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
        height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
        expected_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
        if not math.isfinite(fps) or fps <= 0 or width <= 0 or height <= 0:
            cap.release()
            raise RuntimeError(
                f"Annotation source metadata is invalid: fps={fps}, size={width}x{height}"
            )

        rows_by_frame: dict[int, list[dict[str, Any]]] = {}
        for row in enriched.to_dict(orient="records"):
            rows_by_frame.setdefault(int(row["frame_index"]), []).append(row)
        behavior_by_tracklet: dict[str, list[dict[str, Any]]] = {}
        for segment in job.annotation_behavior_segments.get(chunk_index, []):
            behavior_by_tracklet.setdefault(str(segment["trackletId"]), []).append(segment)
        for values in behavior_by_tracklet.values():
            values.sort(key=lambda item: (float(item["startSec"]), float(item["endSec"])))

        identities: dict[str, dict[str, Any]] = {}
        roster_by_student = {int(item["student_id"]): item for item in roster}
        for tracklet_id, item in resolved.items():
            state, score, margin = item
            student_id = getattr(state, "student_id", None)
            details = roster_by_student.get(int(student_id), {}) if student_id is not None else {}
            identities[str(tracklet_id)] = {
                "stableId": str(getattr(state, "stable_id", "") or ""),
                "studentId": int(student_id) if student_id is not None else None,
                "studentCode": str(details.get("student_code") or "").strip(),
                "fullName": str(details.get("full_name") or "").strip(),
                "confidence": float(score) if score is not None else None,
                "margin": float(margin) if margin is not None else None,
            }

        video_only = job.work_dir / f"annotation-video-{chunk_index:06d}-{uuid4().hex}.tmp.mp4"
        muxed = job.work_dir / f"annotation-muxed-{chunk_index:06d}-{uuid4().hex}.tmp.mp4"
        output = job.work_dir / f"annotation-chunk-{chunk_index:06d}.mp4"
        writer = cv2.VideoWriter(
            str(video_only),
            cv2.VideoWriter_fourcc(*"mp4v"),
            fps,
            (width, height),
        )
        if not writer.isOpened():
            cap.release()
            raise RuntimeError(f"Annotation render cannot create output for chunk {chunk_index}")

        font = self._annotation_font(max(14, min(26, int(round(height / 36)))))
        small_font = self._annotation_font(max(12, min(20, int(round(height / 48)))))
        frame_index = 0
        rendered_frames = 0
        boxes_drawn = 0
        labeled_frames = 0
        try:
            while True:
                ok, frame = cap.read()
                if not ok:
                    break
                rows = rows_by_frame.get(frame_index, [])
                image = Image.fromarray(cv2.cvtColor(frame, cv2.COLOR_BGR2RGB))
                draw = ImageDraw.Draw(image)
                frame_has_overlay = False
                timestamp_sec = frame_index / fps

                for row in rows:
                    x1 = max(0, min(width - 1, int(round(float(row["x1"])))))
                    y1 = max(0, min(height - 1, int(round(float(row["y1"])))))
                    x2 = max(0, min(width - 1, int(round(float(row["x2"])))))
                    y2 = max(0, min(height - 1, int(round(float(row["y2"])))))
                    if x2 <= x1 or y2 <= y1:
                        continue
                    tracklet_id = str(row["tracklet_id"])
                    behavior = self._behavior_for_frame(behavior_by_tracklet, tracklet_id, timestamp_sec)
                    color = (22, 163, 74) if behavior is None else {
                        "FOCUSED": (22, 163, 74),
                        "DISTRACTED": (234, 88, 12),
                        "SLEEPY": (220, 38, 38),
                        "ACTIVE": (37, 99, 235),
                        "OUT_OF_VIEW": (107, 114, 128),
                    }.get(str(behavior.get("label") or "").upper(), (126, 34, 206))
                    draw.rectangle((x1, y1, x2, y2), outline=color, width=max(2, width // 640))

                    track_id = int(row["track_id"])
                    detection_confidence = row.get("confidence")
                    lines = [
                        f"Track {track_id}"
                        + (
                            f" | det {float(detection_confidence):.2f}"
                            if detection_confidence is not None and math.isfinite(float(detection_confidence))
                            else ""
                        )
                    ]
                    identity = identities.get(tracklet_id)
                    if identity is not None and identity["studentId"] is not None:
                        identity_parts = []
                        if identity["studentCode"]:
                            identity_parts.append(identity["studentCode"])
                        if identity["fullName"]:
                            identity_parts.append(identity["fullName"])
                        if not identity_parts:
                            identity_parts.append(f"StudentId {identity['studentId']}")
                        identity_line = " | ".join(identity_parts)
                        if identity["confidence"] is not None:
                            identity_line += f" | ID {identity['confidence']:.2f}"
                        lines.append(identity_line)
                    if behavior is not None:
                        lines.append(
                            f"{str(behavior['label']).upper()} | {float(behavior['probability']):.2f}"
                        )

                    line_height = max(font.size + 5, 20)
                    text_width = max(
                        int(draw.textbbox((0, 0), line, font=font)[2]) for line in lines
                    )
                    block_height = line_height * len(lines) + 6
                    top = y1 - block_height if y1 >= block_height else min(height - block_height, y1 + 2)
                    left = min(x1, max(0, width - text_width - 12))
                    draw.rectangle(
                        (left, top, min(width - 1, left + text_width + 12), min(height - 1, top + block_height)),
                        fill=(15, 23, 42),
                    )
                    for line_no, line in enumerate(lines):
                        draw.text((left + 6, top + 3 + line_no * line_height), line, font=font, fill=(255, 255, 255))
                    boxes_drawn += 1
                    frame_has_overlay = True

                elapsed = float(chunk_offset) + timestamp_sec
                time_text = f"{elapsed:010.3f}s | frame {frame_index}"
                time_box = draw.textbbox((0, 0), time_text, font=small_font)
                time_width = int(time_box[2] - time_box[0])
                draw.rectangle((8, 8, time_width + 22, small_font.size + 18), fill=(15, 23, 42))
                draw.text((14, 11), time_text, font=small_font, fill=(255, 255, 255))
                frame_has_overlay = True

                rendered = cv2.cvtColor(np.asarray(image), cv2.COLOR_RGB2BGR)
                writer.write(rendered)
                rendered_frames += 1
                if frame_has_overlay:
                    labeled_frames += 1
                frame_index += 1
        finally:
            cap.release()
            writer.release()

        try:
            if rendered_frames <= 0 or boxes_drawn <= 0:
                raise RuntimeError(
                    f"Annotation render produced no usable overlays: frames={rendered_frames}, boxes={boxes_drawn}"
                )
            if expected_frames > 0 and rendered_frames != expected_frames:
                raise RuntimeError(
                    f"Annotation frame count mismatch: source={expected_frames}, rendered={rendered_frames}"
                )
            source_probe = self._probe_media(clip)
            has_audio = self._probe_has_audio(source_probe)
            candidate = video_only
            if has_audio:
                command = [
                    "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                    "-i", str(video_only), "-i", str(clip),
                    "-map", "0:v:0", "-map", "1:a:0",
                    "-c:v", "copy", "-c:a", "aac", "-b:a", "128k",
                    str(muxed),
                ]
                proc = subprocess.run(command, capture_output=True, text=True, timeout=max(90, rendered_frames / fps * 2))
                if proc.returncode != 0 or not muxed.is_file() or muxed.stat().st_size <= 0:
                    detail = (proc.stderr or proc.stdout or "unknown ffmpeg error")[-1000:]
                    raise RuntimeError(f"Annotation audio mux failed: {detail}")
                candidate = muxed

            candidate.replace(output)
            output_probe = self._probe_media(output)
            output_videos = [s for s in output_probe.get("streams", []) if s.get("codec_type") == "video"]
            if len(output_videos) != 1:
                raise RuntimeError("Annotated chunk does not contain exactly one video stream")
            output_audio = self._probe_has_audio(output_probe)
            if output_audio != has_audio:
                raise RuntimeError("Annotated chunk audio presence does not match its source")
            source_duration = rendered_frames / fps
            output_duration = self._positive_duration(output_probe)
            duration_tolerance = max(0.15, 2.0 / fps)
            if abs(output_duration - source_duration) > duration_tolerance:
                raise RuntimeError(
                    f"Annotation duration mismatch: source={source_duration:.3f}s, rendered={output_duration:.3f}s"
                )
            job.annotation_stats[chunk_index] = {
                "frames": rendered_frames,
                "labeledFrames": labeled_frames,
                "boxes": boxes_drawn,
                "duration": output_duration,
                "hasAudio": has_audio,
            }
            return output
        except Exception:
            self._safe_recording_unlink(output, job.work_dir)
            raise
        finally:
            self._safe_recording_unlink(video_only, job.work_dir)
            self._safe_recording_unlink(muxed, job.work_dir)


    def _write_overlay_preview(self, job, clip: Path, chunk_index: int, enriched, resolved, chunk_offset: float) -> None:
        """Render the newest analyzed frame so boxes and pixels share the same timestamp."""
        if enriched is None or getattr(enriched, "empty", True):
            return

        frame_col = next((c for c in ("frame_idx", "frame_index", "frame_id", "frame_no", "frame") if c in enriched.columns), None)
        time_col = next((c for c in ("timestamp_sec", "time_sec", "timestamp", "t_sec") if c in enriched.columns), None)
        tracklet_col = next((c for c in ("tracklet_id", "trackletId") if c in enriched.columns), None)
        track_col = next((c for c in ("track_id", "trackId") if c in enriched.columns), None)

        bbox_xyxy = None
        for cols in (
            ("x1", "y1", "x2", "y2"),
            ("bbox_x1", "bbox_y1", "bbox_x2", "bbox_y2"),
            ("left", "top", "right", "bottom"),
        ):
            if all(c in enriched.columns for c in cols):
                bbox_xyxy = cols
                break

        bbox_xywh = None
        if bbox_xyxy is None:
            for cols in (("x", "y", "w", "h"), ("bbox_x", "bbox_y", "bbox_w", "bbox_h")):
                if all(c in enriched.columns for c in cols):
                    bbox_xywh = cols
                    break

        if bbox_xyxy is None and bbox_xywh is None:
            LOGGER.warning("Overlay preview skipped: bbox columns not found. columns=%s", list(enriched.columns))
            return

        cap = cv2.VideoCapture(str(clip))
        if not cap.isOpened():
            return

        fps = float(cap.get(cv2.CAP_PROP_FPS) or 0.0)
        total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
        rows = enriched
        frame_no = max(0, total - 1)
        media_rel = 0.0

        if frame_col is not None:
            target = float(enriched[frame_col].max())
            rows = enriched[enriched[frame_col].astype(float).eq(target)]
            frame_no = max(0, int(round(target)))
            if fps > 0:
                media_rel = frame_no / fps
        elif time_col is not None:
            target = float(enriched[time_col].max())
            rows = enriched[(enriched[time_col].astype(float) - target).abs() <= 0.20]
            media_rel = target
            if fps > 0:
                frame_no = max(0, int(round(target * fps)))

        cap.set(cv2.CAP_PROP_POS_FRAMES, frame_no)
        ok, frame = cap.read()
        if not ok:
            cap.set(cv2.CAP_PROP_POS_FRAMES, max(0, total - 1))
            ok, frame = cap.read()
        cap.release()

        if not ok or frame is None:
            return

        height, width = frame.shape[:2]
        colors = {
            "FOCUSED": (34, 197, 94),
            "DISTRACTED": (11, 158, 245),
            "SLEEPY": (38, 38, 220),
            "ACTIVE": (235, 110, 37),
            "OUT_OF_VIEW": (128, 128, 128),
        }
        labels_vi = {
            "FOCUSED": "Tap trung",
            "DISTRACTED": "Mat tap trung",
            "SLEEPY": "Buon ngu",
            "ACTIVE": "Hoat dong",
            "OUT_OF_VIEW": "Khong quan sat",
        }

        drawn = 0
        snapshot_boxes = []
        overlay_behaviors = getattr(job, "overlay_behaviors", {}) or {}

        def resolve_item(key):
            candidates = [key, str(key)]
            try:
                candidates.append(int(key))
            except Exception:
                pass
            for candidate in candidates:
                if candidate in resolved:
                    item = resolved[candidate]
                    return item[0] if isinstance(item, (tuple, list)) else item
            return None

        for _, row in rows.iterrows():
            if bbox_xyxy is not None:
                x1, y1, x2, y2 = [float(row[c]) for c in bbox_xyxy]
            else:
                x, y, bw, bh = [float(row[c]) for c in bbox_xywh]
                x1, y1, x2, y2 = x, y, x + bw, y + bh

            if max(abs(x1), abs(x2)) <= 2.0 and max(abs(y1), abs(y2)) <= 2.0:
                x1, x2 = x1 * width, x2 * width
                y1, y2 = y1 * height, y2 * height

            x1 = int(max(0, min(width - 1, round(x1))))
            y1 = int(max(0, min(height - 1, round(y1))))
            x2 = int(max(0, min(width - 1, round(x2))))
            y2 = int(max(0, min(height - 1, round(y2))))
            if x2 <= x1 or y2 <= y1:
                continue

            key = row[tracklet_col] if tracklet_col is not None else (row[track_col] if track_col is not None else None)
            state = resolve_item(key) if key is not None else None
            stable_id = str(getattr(state, "stable_id", "")) if state is not None else ""
            student_id = getattr(state, "student_id", None) if state is not None else None

            behavior = overlay_behaviors.get(stable_id, {}) if stable_id else {}
            label = str(behavior.get("label") or "").upper()
            raw_probability = behavior.get("probability")
            probability = float(raw_probability) if raw_probability is not None else None
            color = colors.get(label, (128, 128, 128))

            who = f"SV#{student_id}" if student_id is not None else (stable_id or "UNKNOWN")
            title = who
            if label and probability is not None:
                behavior_text = labels_vi.get(label, label)
                title += f" | {behavior_text} {probability * 100:.0f}%"

            cv2.rectangle(frame, (x1, y1), (x2, y2), color, 2, cv2.LINE_AA)
            (tw, th), _ = cv2.getTextSize(title, cv2.FONT_HERSHEY_SIMPLEX, 0.55, 2)
            top = max(0, y1 - th - 12)
            cv2.rectangle(frame, (x1, top), (min(width - 1, x1 + tw + 12), y1), color, -1)
            cv2.putText(
                frame,
                title,
                (x1 + 6, max(th + 2, y1 - 7)),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.55,
                (255, 255, 255),
                2,
                cv2.LINE_AA,
            )
            snapshot_boxes.append({
                "bbox": [x1 / max(1, width), y1 / max(1, height), x2 / max(1, width), y2 / max(1, height)],
                "stableId": stable_id or None,
                "studentId": student_id,
                "behavior": label or None,
                "behaviorProbability": probability,
            })
            drawn += 1

        job.overlay_boxes = snapshot_boxes
        media_time = job.base_time + timedelta(seconds=float(chunk_offset) + float(media_rel))
        delay = max(0.0, (_utcnow() - media_time).total_seconds())
        banner = f"AI PHAN TICH | tre {delay:.1f}s | bbox {drawn}"
        cv2.rectangle(frame, (10, 10), (min(width - 10, 430), 46), (15, 23, 42), -1)
        cv2.putText(
            frame,
            banner,
            (20, 35),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.62,
            (255, 255, 255),
            2,
            cv2.LINE_AA,
        )

        out = job.work_dir / "overlay_preview.jpg"
        tmp = job.work_dir / "overlay_preview.tmp.jpg"
        if cv2.imwrite(str(tmp), frame, [int(cv2.IMWRITE_JPEG_QUALITY), 88]):
            tmp.replace(out)
            job.overlay_preview_path = out
            job.overlay_generated_at = _utcnow().isoformat()
            job.overlay_media_time = media_time.isoformat()
            job.overlay_delay_sec = delay


    def fast_overlay(self, job_id: str, image_bytes: bytes):
        """Realtime browser overlay. Runs YOLO/ByteTrack only; temporal behavior is reused."""
        with self.jobs_lock:
            job = self.jobs.get(job_id)
        if job is None:
            raise KeyError(job_id)
        if not image_bytes:
            raise ValueError("Empty realtime frame")

        if not hasattr(job, "fast_overlay_lock"):
            job.fast_overlay_lock = threading.Lock()
        if not job.fast_overlay_lock.acquire(blocking=False):
            return {"busy": True, "generatedAt": _utcnow().isoformat(), "detections": []}

        t0 = time.perf_counter()
        try:
            frame = cv2.imdecode(np.frombuffer(image_bytes, dtype=np.uint8), cv2.IMREAD_COLOR)
            if frame is None:
                raise ValueError("Invalid realtime JPEG")
            h, w = frame.shape[:2]

            if not hasattr(job, "fast_yolo"):
                from ultralytics import YOLO
                from htbam_builder.model_assets import select_person_detector

                selected = select_person_detector(self.training_root, self.tracking_cfg)
                job.fast_yolo = YOLO(str(selected.path))
                job.fast_person_class_id = int(
                    getattr(selected, "class_id", self.tracking_cfg.get("person_class_id", 0))
                )
                job.fast_tracker = str(self.tracking_cfg.get("tracker", "botsort.yaml"))
                job.fast_track_links = {}
                LOGGER.info(
                    "Realtime overlay detector ready job=%s weights=%s class_id=%s tracker=%s",
                    job.job_id,
                    selected.path,
                    job.fast_person_class_id,
                    job.fast_tracker,
                )

            try:
                import torch
                device = 0 if torch.cuda.is_available() else "cpu"
            except Exception:
                device = "cpu"

            conf = float(self.tracking_cfg.get("conf", self.tracking_cfg.get("conf_thres", 0.25)))
            iou = float(self.tracking_cfg.get("iou", self.tracking_cfg.get("iou_thres", 0.50)))
            results = job.fast_yolo.track(
                source=frame,
                persist=True,
                classes=[int(getattr(job, "fast_person_class_id", self.tracking_cfg.get("person_class_id", 0)))],
                conf=max(0.05, min(0.95, conf)),
                iou=max(0.10, min(0.95, iou)),
                device=device,
                verbose=False,
                tracker=str(getattr(job, "fast_tracker", self.tracking_cfg.get("tracker", "botsort.yaml"))),
            )

            previous = getattr(job, "overlay_boxes", []) or []
            behaviors = getattr(job, "overlay_behaviors", {}) or {}
            links = getattr(job, "fast_track_links", {}) or {}

            def iou_box(a, b):
                ix1, iy1 = max(a[0], b[0]), max(a[1], b[1])
                ix2, iy2 = min(a[2], b[2]), min(a[3], b[3])
                inter = max(0., ix2-ix1) * max(0., iy2-iy1)
                aa = max(0., a[2]-a[0]) * max(0., a[3]-a[1])
                bb = max(0., b[2]-b[0]) * max(0., b[3]-b[1])
                d = aa + bb - inter
                return inter / d if d > 1e-9 else 0.

            def center_dist(a, b):
                ax, ay = (a[0]+a[2])/2, (a[1]+a[3])/2
                bx, by = (b[0]+b[2])/2, (b[1]+b[3])/2
                return ((ax-bx)**2 + (ay-by)**2) ** .5

            detections = []
            result = results[0] if results else None
            boxes = getattr(result, "boxes", None) if result is not None else None
            if boxes is not None and len(boxes) > 0:
                xyxy = boxes.xyxy.detach().cpu().numpy()
                confs = boxes.conf.detach().cpu().numpy() if boxes.conf is not None else np.ones(len(xyxy))
                tids = boxes.id.detach().cpu().numpy().astype(int) if boxes.id is not None else np.arange(1, len(xyxy)+1)
                for coords, det_conf, tid in zip(xyxy, confs, tids):
                    x1, y1, x2, y2 = map(float, coords)
                    norm = [max(0., min(1., x1/max(1,w))), max(0., min(1., y1/max(1,h))),
                            max(0., min(1., x2/max(1,w))), max(0., min(1., y2/max(1,h)))]
                    linked = None
                    stable = links.get(int(tid))
                    if stable:
                        linked = next((p for p in previous if p.get("stableId") == stable), None)
                    if linked is None and previous:
                        scored = []
                        for p in previous:
                            old = p.get("bbox")
                            if not old or len(old) != 4:
                                continue
                            ov, dist = iou_box(norm, old), center_dist(norm, old)
                            scored.append((ov*2-dist, ov, dist, p))
                        if scored:
                            scored.sort(key=lambda x: x[0], reverse=True)
                            _, ov, dist, candidate = scored[0]
                            if ov >= .10 or dist <= .16:
                                linked = candidate
                                if candidate.get("stableId"):
                                    links[int(tid)] = candidate["stableId"]
                    stable = linked.get("stableId") if linked else None
                    student = linked.get("studentId") if linked else None
                    behavior, probability = None, None
                    if stable and stable in behaviors:
                        current = behaviors.get(stable) or {}
                        behavior, probability = current.get("label"), current.get("probability")
                    elif linked:
                        behavior, probability = linked.get("behavior"), linked.get("behaviorProbability")
                    detections.append({
                        "trackId": int(tid), "stableId": stable, "studentId": student,
                        "bbox": norm, "detectionConfidence": float(det_conf),
                        "behavior": behavior,
                        "behaviorProbability": float(probability) if probability is not None else None,
                    })
            job.fast_track_links = links
            return {
                "busy": False, "generatedAt": _utcnow().isoformat(),
                "frameWidth": int(w), "frameHeight": int(h),
                "processingMs": round((time.perf_counter()-t0)*1000., 1),
                "detections": detections,
            }
        finally:
            job.fast_overlay_lock.release()

    def overlay_preview(self, job_id: str):
        with self.jobs_lock:
            job = self.jobs.get(job_id)
        if job is None:
            raise KeyError(job_id)
        path = getattr(job, "overlay_preview_path", None)
        if path is None or not Path(path).is_file():
            raise FileNotFoundError("AI overlay preview is not ready")
        return (
            Path(path),
            str(getattr(job, "overlay_generated_at", "")),
            str(getattr(job, "overlay_media_time", "")),
            str(getattr(job, "overlay_delay_sec", "")),
        )

    def _process_chunk(self, job, roster, chunk_index, clip, chunk_offset) -> int:
        tracks,enriched,track_csv=self._track_chunk(job,chunk_index,clip)
        if tracks.empty:
            raise RuntimeError(
                f"Chunk {chunk_index} has no real person detections; refusing to create an unannotated artifact"
            )
        prototypes=self._face_prototypes(clip,enriched)
        resolved=self._resolve_stables(job,enriched,prototypes,roster,chunk_offset)
        self._emit_track_identity(job,chunk_index,enriched,resolved,prototypes,chunk_offset)
        sent = self._behavior_chunk(job,chunk_index,track_csv,resolved,chunk_offset)
        annotated = self._render_annotated_chunk(
            job, clip, chunk_index, enriched, resolved, roster, chunk_offset
        )
        job.annotation_clips[chunk_index] = annotated
        job.annotation_source_clips[chunk_index] = clip
        try:
            self._write_overlay_preview(job, clip, chunk_index, enriched, resolved, chunk_offset)
        except Exception:
            LOGGER.exception("Unable to render AI overlay preview job=%s chunk=%s", job.job_id, chunk_index)
        return sent

    def _send_lost_for_all(self, job: JobState) -> None:
        now=_utcnow()
        failures = []
        for state in list(job.stable_states.values()):
            if state.stable_id in job.lost_sent:
                continue
            try:
                self._post_event(job, {
                    "eventType":"TRACK_LOST","timestamp":now.isoformat(),"stableId":state.stable_id,"trackId":None,
                    "studentId":None,"identityConfidence":None,"identityMargin":None,"behaviorLabel":None,"behaviorProbability":None,
                    "observationQuality":0.0,"alertType":None,"alertDurationSeconds":None,"startedAt":None,"endedAt":None,"identityDecision":None,
                })
                job.lost_sent.add(state.stable_id)
                state.active=False
            except Exception as exc:
                failures.append(f"{state.stable_id}: {exc}")
                LOGGER.exception("Unable to send TRACK_LOST stable=%s",state.stable_id)
        if failures:
            raise RuntimeError("Terminal callbacks were not persisted: " + "; ".join(failures))


    def _ensure_browser_state(self, job) -> None:
        if not hasattr(job, "browser_queue"):
            job.browser_queue = queue.Queue(maxsize=self.browser_queue_size)
        if not hasattr(job, "browser_media_sec"):
            job.browser_media_sec = 0.0
        if not hasattr(job, "browser_chunk_index"):
            job.browser_chunk_index = 0

    def enqueue_browser_chunk(self, job_id: str, data: bytes, suffix: str = ".webm", chunk_id: str | None = None):
        with self.jobs_lock:
            job = self.jobs.get(job_id)
        if job is None:
            raise KeyError(job_id)
        with job.browser_ingest_lock:
            if str(job.request.sourceType).strip().upper() != "BROWSER":
                raise ValueError("Job is not a browser-camera session")
            if str(getattr(job, "status", "")).upper() != "RUNNING" or job.stop_event.is_set():
                raise RuntimeError(f"Job is not accepting browser chunks: {getattr(job, 'status', 'UNKNOWN')}")
            if not data:
                raise ValueError("Empty browser camera chunk")
            self._ensure_browser_state(job)
            receipt_key = (chunk_id or "").strip()
            if receipt_key and receipt_key in job.browser_chunk_receipts:
                return dict(job.browser_chunk_receipts[receipt_key])
            index = int(job.browser_chunk_index)
            job.browser_chunk_index = index + 1

            safe_suffix = suffix.lower() if suffix.lower() in {".webm", ".mp4", ".mkv"} else ".webm"
            raw = job.work_dir / f"browser_raw_{index:06d}{safe_suffix}"
            raw.write_bytes(data)
            try:
                job.browser_queue.put_nowait((index, raw))
            except queue.Full:
                # Never acknowledge a chunk and then evict an older acknowledged chunk.
                # The caller may retry this newest chunk while every accepted file remains
                # available for analysis and final video assembly.
                raw.unlink(missing_ok=True)
                raise RuntimeError("AI browser-camera queue is full; retry this chunk")

            job.accepted_chunks += 1

            result = {
                "queued": True,
                "chunkIndex": index,
                "queueSize": job.browser_queue.qsize(),
                "droppedOldest": False,
            }
            if receipt_key:
                job.browser_chunk_receipts[receipt_key] = dict(result)
            return result

    def _prepare_browser_clip(self, raw: Path, index: int) -> Path:
        out = raw.parent / f"browser_chunk_{index:06d}.mp4"
        has_audio = self._probe_has_audio(self._probe_media(raw))
        cmd = [
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
            "-i", str(raw),
            "-c:v", "mpeg4",
            "-q:v", "4",
            "-pix_fmt", "yuv420p",
        ]
        if has_audio:
            cmd.extend(["-c:a", "aac", "-b:a", "128k"])
        else:
            cmd.append("-an")
        cmd.append(str(out))
        try:
            proc = subprocess.run(cmd, capture_output=True, text=True, timeout=90)
        except FileNotFoundError as exc:
            self._safe_recording_unlink(out, raw.parent)
            raise RuntimeError("ffmpeg is required to prepare browser camera chunks") from exc
        except subprocess.TimeoutExpired as exc:
            self._safe_recording_unlink(out, raw.parent)
            raise RuntimeError("ffmpeg browser chunk conversion timed out") from exc
        if proc.returncode != 0 or not out.is_file() or out.stat().st_size == 0:
            detail = (proc.stderr or proc.stdout or "unknown ffmpeg error")[-1000:]
            self._safe_recording_unlink(out, raw.parent)
            raise RuntimeError(f"ffmpeg browser chunk conversion failed: {detail}")
        return out

    def _call_browser_roster_loader(self, job):
        loader = getattr(self, "_load_roster", None)
        if loader is None:
            raise RuntimeError("Session runtime does not expose _load_roster")
        params = inspect.signature(loader).parameters
        kwargs = {}
        for name in params:
            lower = name.lower()
            if "request" in lower:
                kwargs[name] = job.request
            elif lower in {"job", "state", "job_state"}:
                kwargs[name] = job
        if len(kwargs) == len(params):
            return loader(**kwargs)
        try:
            return loader(job.request)
        except TypeError:
            return loader(job)

    def _call_browser_process_chunk(self, job, roster, index: int, clip: Path, offset: float):
        process = getattr(self, "_process_chunk", None)
        if process is None:
            raise RuntimeError("Session runtime does not expose _process_chunk")
        params = inspect.signature(process).parameters
        kwargs = {}
        for name in params:
            lower = name.lower()
            if lower in {"job", "state", "job_state"}:
                kwargs[name] = job
            elif "roster" in lower or "candidate" in lower:
                kwargs[name] = roster
            elif "index" in lower or lower in {"i", "chunk_no", "chunk_number"}:
                kwargs[name] = index
            elif "path" in lower or "video" in lower or "clip" in lower or "source" in lower:
                kwargs[name] = clip
            elif "offset" in lower or "start_sec" in lower or "media_sec" in lower:
                kwargs[name] = offset
        if len(kwargs) != len(params):
            missing = [name for name in params if name not in kwargs]
            raise RuntimeError("Unsupported _process_chunk signature. Missing mapping for: " + ", ".join(missing))
        return process(**kwargs)

    def _run_browser_job(self, job) -> None:
        self._ensure_browser_state(job)
        try:
            time.sleep(1.0)
            roster = self._call_browser_roster_loader(job)
            job.browser_roster = roster
            LOGGER.info(
                "Browser-camera job %s started roster=%s",
                job.job_id,
                len(getattr(job.request, "rosterStudentIds", []) or []),
            )

            while not job.stop_event.is_set() or getattr(job.browser_queue, "unfinished_tasks", 0) > 0:
                try:
                    index, raw = job.browser_queue.get(timeout=0.5)
                except queue.Empty:
                    continue

                clip = None
                offset = float(job.browser_media_sec)
                duration = 12.0
                media_advanced = False
                try:
                    clip = self._prepare_browser_clip(raw, index)
                    cap = cv2.VideoCapture(str(clip))
                    fps = float(cap.get(cv2.CAP_PROP_FPS) or 0.0)
                    frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
                    cap.release()
                    duration = frames / fps if fps > 0 and frames > 0 else 12.0
                    job.browser_media_sec = offset + max(0.1, duration)
                    media_advanced = True
                    job.browser_chunk_meta[index] = (raw, clip, offset, duration)
                    result = self._call_browser_process_chunk(job, roster, index, clip, offset)
                    annotated = job.annotation_clips.get(index)
                    if annotated is None or not annotated.is_file():
                        raise RuntimeError(f"Annotated browser chunk {index} was not rendered")
                    job.recording_clips[index] = annotated
                    job.browser_failed_chunks.discard(index)
                    job.processed_chunks += 1
                    job.behavior_events_emitted += max(0, int(result or 0))
                    LOGGER.info(
                        "Browser-camera job %s chunk=%s duration=%.2fs result=%s",
                        job.job_id, index, duration, result,
                    )
                except Exception:
                    if not media_advanced:
                        job.browser_media_sec = offset + max(0.1, duration)
                    job.browser_failed_chunks.add(index)
                    if index not in job.browser_chunk_meta:
                        job.browser_chunk_meta[index] = (raw, clip, offset, duration)
                    LOGGER.exception("Browser-camera job %s chunk=%s failed", job.job_id, index)
                finally:
                    job.browser_queue.task_done()
        except Exception as exc:
            job.error = f"{type(exc).__name__}: {exc}"
            job.status = "FAILED"
            LOGGER.exception("Browser-camera job %s failed", job.job_id)

    def _retry_failed_browser_chunks(self, job: JobState) -> None:
        if str(job.request.sourceType).strip().upper() != "BROWSER":
            return
        if job.accepted_chunks == 0:
            raise RuntimeError("No browser camera chunks were accepted; no session output can be finalized")
        for index in sorted(list(job.browser_failed_chunks)):
            raw, clip, offset, duration = job.browser_chunk_meta[index]
            try:
                if clip is None or not clip.is_file() or clip.stat().st_size == 0:
                    clip = self._prepare_browser_clip(raw, index)
                result = self._call_browser_process_chunk(job, job.browser_roster, index, clip, offset)
                job.browser_chunk_meta[index] = (raw, clip, offset, duration)
                annotated = job.annotation_clips.get(index)
                if annotated is None or not annotated.is_file():
                    raise RuntimeError(f"Annotated browser chunk {index} was not rendered")
                job.recording_clips[index] = annotated
                job.browser_failed_chunks.discard(index)
                job.processed_chunks += 1
                job.behavior_events_emitted += max(0, int(result or 0))
            except Exception as exc:
                raise RuntimeError(f"Browser chunk {index} could not be recovered: {exc}") from exc
        if job.processed_chunks != job.accepted_chunks:
            raise RuntimeError(
                f"Browser chunk persistence incomplete: accepted={job.accepted_chunks}, processed={job.processed_chunks}"
            )

    @staticmethod
    def _probe_media(path: Path) -> dict[str, Any]:
        command = [
            "ffprobe", "-v", "error",
            "-show_streams", "-show_format",
            "-of", "json",
            str(path),
        ]
        try:
            proc = subprocess.run(command, capture_output=True, text=True, timeout=30)
        except FileNotFoundError as exc:
            raise RuntimeError("ffprobe is required to validate session video artifacts") from exc
        except subprocess.TimeoutExpired as exc:
            raise RuntimeError(f"ffprobe timed out while validating {path.name}") from exc
        if proc.returncode != 0:
            detail = (proc.stderr or proc.stdout or "unknown ffprobe error")[-1000:]
            raise RuntimeError(f"ffprobe could not inspect {path.name}: {detail}")
        try:
            result = json.loads(proc.stdout or "{}")
        except json.JSONDecodeError as exc:
            raise RuntimeError(f"ffprobe returned invalid JSON for {path.name}") from exc
        if not isinstance(result, dict):
            raise RuntimeError(f"ffprobe returned an invalid result for {path.name}")
        return result

    @staticmethod
    def _probe_has_audio(probe: dict[str, Any]) -> bool:
        return any(stream.get("codec_type") == "audio" for stream in probe.get("streams", []))

    @staticmethod
    def _positive_duration(probe: dict[str, Any]) -> float:
        candidates = [
            stream.get("duration")
            for stream in probe.get("streams", [])
            if stream.get("codec_type") == "video"
        ]
        candidates.append(probe.get("format", {}).get("duration"))
        for raw in candidates:
            try:
                duration = float(raw)
            except (TypeError, ValueError):
                continue
            if math.isfinite(duration) and duration > 0:
                return duration
        raise RuntimeError("Encoded session recording has no positive duration")

    @staticmethod
    def _mp4_box_offsets(path: Path) -> dict[bytes, int]:
        offsets: dict[bytes, int] = {}
        file_size = path.stat().st_size
        offset = 0
        with path.open("rb") as stream:
            while offset + 8 <= file_size:
                stream.seek(offset)
                header = stream.read(8)
                if len(header) != 8:
                    break
                box_size = int.from_bytes(header[:4], "big")
                box_type = header[4:8]
                header_size = 8
                if box_size == 1:
                    extended = stream.read(8)
                    if len(extended) != 8:
                        break
                    box_size = int.from_bytes(extended, "big")
                    header_size = 16
                elif box_size == 0:
                    box_size = file_size - offset
                if box_size < header_size or offset + box_size > file_size:
                    break
                offsets.setdefault(box_type, offset)
                offset += box_size
        return offsets

    def _validate_session_recording(self, path: Path, expect_audio: bool) -> dict[str, Any]:
        if not path.is_file() or path.stat().st_size <= 0:
            raise RuntimeError("Encoded session recording is empty")
        probe = self._probe_media(path)
        video_streams = [
            stream for stream in probe.get("streams", []) if stream.get("codec_type") == "video"
        ]
        if len(video_streams) != 1:
            raise RuntimeError(f"Encoded session recording must contain one video stream, got {len(video_streams)}")
        video = video_streams[0]
        if video.get("codec_name") != "h264":
            raise RuntimeError(
                f"Encoded session recording codec is {video.get('codec_name') or 'unknown'}, expected h264"
            )
        if video.get("pix_fmt") != "yuv420p":
            raise RuntimeError(
                f"Encoded session recording pixel format is {video.get('pix_fmt') or 'unknown'}, expected yuv420p"
            )
        if str(video.get("codec_tag_string") or "").lower() != "avc1":
            raise RuntimeError(
                f"Encoded session recording codec tag is {video.get('codec_tag_string') or 'unknown'}, expected avc1"
            )

        audio_streams = [
            stream for stream in probe.get("streams", []) if stream.get("codec_type") == "audio"
        ]
        if expect_audio and not audio_streams:
            raise RuntimeError("Source recording contains audio but encoded artifact does not")
        if not expect_audio and audio_streams:
            raise RuntimeError("Encoded artifact unexpectedly contains an audio stream")
        if any(stream.get("codec_name") != "aac" for stream in audio_streams):
            codecs = ", ".join(str(stream.get("codec_name") or "unknown") for stream in audio_streams)
            raise RuntimeError(f"Encoded session recording audio codec is {codecs}, expected aac")

        duration = self._positive_duration(probe)
        actual_size = path.stat().st_size
        try:
            reported_size = int(probe.get("format", {}).get("size"))
        except (TypeError, ValueError) as exc:
            raise RuntimeError("ffprobe did not report a valid session recording size") from exc
        if reported_size <= 0 or reported_size != actual_size:
            raise RuntimeError(
                f"Encoded session recording size mismatch: file={actual_size}, ffprobe={reported_size}"
            )
        boxes = self._mp4_box_offsets(path)
        if b"moov" not in boxes or b"mdat" not in boxes:
            raise RuntimeError("Encoded session recording is missing required MP4 moov/mdat boxes")
        if boxes[b"moov"] > boxes[b"mdat"]:
            raise RuntimeError("Encoded session recording is not faststart (moov appears after mdat)")
        return {"duration": duration, "sizeBytes": actual_size}

    @staticmethod
    def _safe_recording_unlink(path: Path | None, work_dir: Path) -> None:
        if path is None:
            return
        try:
            resolved = path.resolve()
            root = work_dir.resolve()
            if resolved == root or root not in resolved.parents:
                LOGGER.warning("Refusing to clean recording path outside job directory: %s", resolved)
                return
            resolved.unlink(missing_ok=True)
        except OSError:
            LOGGER.warning("Could not clean temporary recording file %s", path, exc_info=True)

    def _cleanup_recording_sources(self, job: JobState) -> None:
        paths: set[Path] = set(job.recording_clips.values())
        paths.update(job.annotation_clips.values())
        paths.update(job.annotation_source_clips.values())
        for raw, clip, _offset, _duration in job.browser_chunk_meta.values():
            paths.add(raw)
            if clip is not None:
                paths.add(clip)
        for path in paths:
            self._safe_recording_unlink(path, job.work_dir)
        job.recording_clips.clear()
        job.annotation_clips.clear()
        job.annotation_source_clips.clear()
        job.browser_chunk_meta.clear()
        self._safe_recording_unlink(job.work_dir / "source.mp4", job.work_dir)

    def _finalize_video_artifact(self, job: JobState) -> None:
        if job.artifact is not None:
            return
        source_type = str(job.request.sourceType).strip().upper()
        if source_type not in {"BROWSER", "CAMERA", "VIDEO"}:
            return
        target = getattr(job.request, "annotatedVideoUpload", None)
        if target is None:
            raise RuntimeError("Session recording upload target was not provided")
        if not job.annotation_clips:
            raise RuntimeError("No successfully rendered annotation chunks are available")
        if set(job.recording_clips) != set(job.annotation_clips):
            raise RuntimeError("Annotated recording is incomplete; source and annotation chunk sets differ")
        if job.accepted_chunks > 0 and (
            job.processed_chunks != job.accepted_chunks
            or len(job.annotation_clips) != job.accepted_chunks
        ):
            raise RuntimeError(
                "Annotated recording is incomplete: "
                f"accepted={job.accepted_chunks}, processed={job.processed_chunks}, "
                f"rendered={len(job.annotation_clips)}"
            )
        for index, path in job.recording_clips.items():
            if path != job.annotation_clips.get(index):
                raise RuntimeError(f"Recording chunk {index} is not the rendered annotation output")
            if not path.is_file() or path.stat().st_size <= 0:
                raise RuntimeError(f"Rendered annotation chunk {index} is missing or empty")
            stats = job.annotation_stats.get(index, {})
            if int(stats.get("boxes", 0)) <= 0 or int(stats.get("frames", 0)) <= 0:
                raise RuntimeError(f"Annotated recording chunk {index} has no verified overlays")
        clips = [path for _, path in sorted(job.annotation_clips.items())]
        if not clips:
            raise RuntimeError("No rendered annotation chunks are available for session recording")

        manifest = job.work_dir / f"recording-concat-{uuid4().hex}.txt"
        output = job.work_dir / f"session-recording-{uuid4().hex}.tmp.mp4"
        upload_succeeded = False
        try:
            manifest.write_text(
                "".join(f"file '{path.resolve().as_posix()}'\n" for path in clips),
                encoding="utf-8",
            )
            audio_presence = [self._probe_has_audio(self._probe_media(path)) for path in clips]
            if any(audio_presence) and not all(audio_presence):
                raise RuntimeError("Processed recording chunks have inconsistent audio streams")
            has_audio = bool(audio_presence and audio_presence[0])

            command = [
                "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                "-f", "concat", "-safe", "0", "-i", str(manifest),
                "-map", "0:v:0",
                "-c:v", "libx264",
                "-pix_fmt", "yuv420p",
                "-tag:v", "avc1",
                "-preset", "medium",
                "-crf", "23",
            ]
            if has_audio:
                command.extend(["-map", "0:a:0", "-c:a", "aac", "-b:a", "128k"])
            else:
                # Do not pass audio encoder/bitrate options when the source has no audio.
                command.append("-an")
            command.extend(["-movflags", "+faststart", str(output)])
            try:
                proc = subprocess.run(
                    command,
                    capture_output=True,
                    text=True,
                    timeout=max(180, len(clips) * 15),
                )
            except FileNotFoundError as exc:
                raise RuntimeError("ffmpeg with libx264 support is required to encode session artifacts") from exc
            except subprocess.TimeoutExpired as exc:
                raise RuntimeError("H.264 session recording encoding timed out") from exc
            if proc.returncode != 0:
                detail = (proc.stderr or proc.stdout or "unknown ffmpeg error")[-1000:]
                raise RuntimeError(f"H.264 session recording encoding failed: {detail}")

            media = self._validate_session_recording(output, expect_audio=has_audio)
            digest = hashlib.sha256()
            with output.open("rb") as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(block)
            with output.open("rb") as stream, httpx.Client(follow_redirects=True, timeout=120.0) as client:
                response = client.put(
                    str(target.uploadUrl),
                    content=iter(lambda: stream.read(1024 * 1024), b""),
                    headers={
                        "Content-Type": "video/mp4",
                        "Content-Length": str(media["sizeBytes"]),
                    },
                )
                response.raise_for_status()
            job.artifact = {
                "objectKey": str(target.objectKey),
                "fileName": str(target.fileName),
                "contentType": "video/mp4",
                "sizeBytes": media["sizeBytes"],
                "sha256": digest.hexdigest(),
            }
            upload_succeeded = True
        finally:
            self._safe_recording_unlink(manifest, job.work_dir)
            self._safe_recording_unlink(output, job.work_dir)
            if upload_succeeded:
                self._cleanup_recording_sources(job)

    def _run_job(self, job: JobState) -> None:
        # HTBAM_BROWSER_CAMERA_DISPATCH
        if str(job.request.sourceType).strip().upper() == "BROWSER":
            self._run_browser_job(job)
            return

        try:
            time.sleep(1.5)
            roster=self._load_roster(job.request)
            LOGGER.info("Session job %s source=%s roster=%s templates=%s",job.job_id,job.request.sourceType,len(job.request.rosterStudentIds),len(roster))
            for chunk_index,clip,offset,duration,_fps in self._chunk_stream(job):
                job.accepted_chunks += 1
                sent=self._process_chunk(job,roster,chunk_index,clip,offset)
                job.processed_chunks += 1
                job.behavior_events_emitted += max(0, int(sent or 0))
                annotated = job.annotation_clips.get(chunk_index)
                if annotated is None or not annotated.is_file():
                    raise RuntimeError(f"Annotated {str(job.request.sourceType).lower()} chunk {chunk_index} was not rendered")
                job.recording_clips[chunk_index] = annotated
                LOGGER.info("Job %s chunk=%s offset=%.2f duration=%.2f events=%s",job.job_id,chunk_index,offset,duration,sent)
            if not job.stop_event.is_set():
                job.source_exhausted=True
                self._send_lost_for_all(job)
        except Exception as exc:
            job.error=f"{type(exc).__name__}: {exc}"
            job.status="FAILED"
            LOGGER.exception("Session job %s failed",job.job_id)

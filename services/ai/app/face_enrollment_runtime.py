from __future__ import annotations

import asyncio
import logging
from io import BytesIO
from pathlib import Path
from typing import Any

import cv2
import httpx
import numpy as np

LOGGER = logging.getLogger("htbam.face_enrollment")

MODEL_NAME = "ArcFace/buffalo_l"
MODEL_VERSION = "buffalo_l-arcface-512-v1"
EMBEDDING_DIM = 512

MIN_DET_SCORE = 0.50
MIN_FACE_SIZE_PX = 28
MIN_BLUR_VAR = 35.0
MIN_BRIGHTNESS = 30.0
MAX_BRIGHTNESS = 225.0
CONSENSUS_SIMILARITY = 0.45
REQUIRED_POSES = {"FRONT", "LEFT", "RIGHT"}
MIN_PASS_IMAGES = 6


def _l2(x: np.ndarray) -> np.ndarray:
    a = np.asarray(x, dtype=np.float32).reshape(-1)
    if a.shape != (EMBEDDING_DIM,):
        raise ValueError(f"Expected ArcFace {EMBEDDING_DIM}-D embedding, got {a.shape}")
    if not np.isfinite(a).all():
        raise ValueError("ArcFace embedding contains non-finite values")
    n = float(np.linalg.norm(a))
    if n <= 1e-12:
        raise ValueError("ArcFace embedding norm is zero")
    return (a / n).astype(np.float32, copy=False)


def _encode_npy(embedding: np.ndarray) -> bytes:
    bio = BytesIO()
    np.save(bio, _l2(embedding).astype(np.float32), allow_pickle=False)
    return bio.getvalue()


def _quality_score(det_score: float, blur_var: float, brightness: float, face_size: float) -> float:
    blur_score = float(np.clip((blur_var - MIN_BLUR_VAR) / 180.0, 0.0, 1.0))
    bright_score = float(np.clip(1.0 - abs(brightness - 127.5) / 127.5, 0.0, 1.0))
    size_score = float(np.clip(face_size / 160.0, 0.0, 1.0))
    return float(np.clip(
        0.40 * float(np.clip(det_score, 0.0, 1.0))
        + 0.25 * blur_score
        + 0.20 * bright_score
        + 0.15 * size_score,
        0.0, 1.0,
    ))


class FaceEnrollmentRuntime:
    def __init__(self, model_root: str | Path):
        self.model_root = Path(model_root)
        self.ready = False
        self.error: str | None = None
        self.app = None
        self._lock = asyncio.Lock()

        try:
            expected = self.model_root / "models" / "buffalo_l"
            onnx_files = sorted(expected.glob("*.onnx")) if expected.is_dir() else []
            if not onnx_files:
                raise FileNotFoundError(
                    f"InsightFace buffalo_l assets are missing under {expected}"
                )

            from insightface.app import FaceAnalysis

            self.app = FaceAnalysis(
                name="buffalo_l",
                root=str(self.model_root),
                providers=["CUDAExecutionProvider", "CPUExecutionProvider"],
            )
            self.app.prepare(ctx_id=0, det_size=(640, 640))
            if self.app.models.get("recognition") is None:
                raise RuntimeError("InsightFace buffalo_l recognition model is unavailable")
            self.ready = True
        except Exception as exc:
            self.error = f"{type(exc).__name__}: {exc}"
            LOGGER.exception("Face enrollment runtime failed to initialize")

    @property
    def loaded_models(self) -> list[str]:
        return [MODEL_NAME] if self.ready else []

    def status(self) -> dict[str, Any]:
        return {
            "ready": self.ready,
            "model": MODEL_NAME,
            "modelVersion": MODEL_VERSION,
            "embeddingDimension": EMBEDDING_DIM,
            "device": "cpu",
            "error": self.error,
            "modelRoot": str(self.model_root),
        }

    def _inspect_image(self, image_bytes: bytes, capture_pose: str) -> dict[str, Any]:
        if not self.ready or self.app is None:
            raise RuntimeError(self.error or "Face enrollment runtime is not ready")

        arr = np.frombuffer(image_bytes, dtype=np.uint8)
        image = cv2.imdecode(arr, cv2.IMREAD_COLOR)
        if image is None or image.size == 0:
            return {
                "qualityStatus": "REJECTED",
                "qualityReason": "Không đọc được file ảnh.",
                "qualityScore": 0.0,
                "embedding": None,
                "capturePose": capture_pose,
            }

        faces = self.app.get(image)
        if len(faces) == 0:
            return {
                "qualityStatus": "RETAKE",
                "qualityReason": "Không phát hiện được khuôn mặt.",
                "qualityScore": 0.0,
                "embedding": None,
                "capturePose": capture_pose,
            }
        if len(faces) != 1:
            return {
                "qualityStatus": "RETAKE",
                "qualityReason": "Ảnh phải chỉ có một khuôn mặt.",
                "qualityScore": 0.0,
                "embedding": None,
                "capturePose": capture_pose,
            }

        face = faces[0]
        bbox = np.asarray(face.bbox, dtype=np.float32)
        fw = max(0.0, float(bbox[2] - bbox[0]))
        fh = max(0.0, float(bbox[3] - bbox[1]))
        face_size = min(fw, fh)
        det_score = float(getattr(face, "det_score", 0.0) or 0.0)

        if det_score < MIN_DET_SCORE or face_size < MIN_FACE_SIZE_PX:
            return {
                "qualityStatus": "RETAKE",
                "qualityReason": "Khuôn mặt quá nhỏ hoặc độ tin cậy phát hiện thấp.",
                "qualityScore": float(np.clip(det_score, 0.0, 1.0)),
                "embedding": None,
                "capturePose": capture_pose,
            }

        from insightface.utils import face_align
        kps = np.asarray(face.kps, dtype=np.float32)
        aligned = face_align.norm_crop(image, landmark=kps, image_size=112)
        gray = cv2.cvtColor(aligned, cv2.COLOR_BGR2GRAY)
        blur_var = float(cv2.Laplacian(gray, cv2.CV_64F).var())
        brightness = float(gray.mean())
        q = _quality_score(det_score, blur_var, brightness, face_size)

        if blur_var < MIN_BLUR_VAR:
            return {
                "qualityStatus": "RETAKE",
                "qualityReason": f"Ảnh bị mờ (blur={blur_var:.1f}).",
                "qualityScore": q,
                "embedding": None,
                "capturePose": capture_pose,
            }
        if brightness < MIN_BRIGHTNESS:
            return {
                "qualityStatus": "RETAKE",
                "qualityReason": f"Ảnh quá tối (brightness={brightness:.1f}).",
                "qualityScore": q,
                "embedding": None,
                "capturePose": capture_pose,
            }
        if brightness > MAX_BRIGHTNESS:
            return {
                "qualityStatus": "RETAKE",
                "qualityReason": f"Ảnh quá sáng (brightness={brightness:.1f}).",
                "qualityScore": q,
                "embedding": None,
                "capturePose": capture_pose,
            }

        emb = getattr(face, "normed_embedding", None)
        if emb is None:
            emb = getattr(face, "embedding", None)
        if emb is None:
            raise RuntimeError("InsightFace did not return a recognition embedding")

        return {
            "qualityStatus": "PASS",
            "qualityReason": None,
            "qualityScore": q,
            "embedding": _l2(np.asarray(emb, dtype=np.float32)),
            "capturePose": capture_pose,
        }

    async def _callback(self, client: httpx.AsyncClient, req, payload: dict[str, Any]) -> None:
        last: Exception | None = None
        for attempt in range(4):
            try:
                response = await client.post(
                    req.callbackUrl,
                    json=payload,
                    headers={"X-AI-API-Key": req.callbackApiKey},
                    timeout=30.0,
                )
                response.raise_for_status()
                return
            except Exception as exc:
                last = exc
                LOGGER.warning("Face enrollment callback attempt %s failed: %s", attempt + 1, exc)
                if attempt < 3:
                    await asyncio.sleep(1.5 * (attempt + 1))
        raise RuntimeError(f"Face enrollment callback failed after retries: {last}")

    async def process_job(self, req, job_id: str) -> None:
        async with self._lock:
            async with httpx.AsyncClient(follow_redirects=True) as client:
                image_results: list[dict[str, Any]] = []
                try:
                    for item in req.images:
                        try:
                            response = await client.get(item.readUrl, timeout=45.0)
                            response.raise_for_status()
                            inspected = await asyncio.to_thread(
                                self._inspect_image,
                                response.content,
                                str(item.capturePose).strip().upper(),
                            )
                        except Exception as exc:
                            inspected = {
                                "qualityStatus": "REJECTED",
                                "qualityReason": f"Lỗi xử lý ảnh: {type(exc).__name__}: {exc}",
                                "qualityScore": 0.0,
                                "embedding": None,
                                "capturePose": str(item.capturePose).strip().upper(),
                            }

                        image_results.append({
                            "imageId": int(item.imageId),
                            "qualityScore": float(inspected["qualityScore"]),
                            "qualityStatus": str(inspected["qualityStatus"]),
                            "qualityReason": inspected["qualityReason"],
                            "_embedding": inspected["embedding"],
                            "_pose": inspected["capturePose"],
                        })

                    accepted = [
                        x for x in image_results
                        if x["qualityStatus"] == "PASS" and x["_embedding"] is not None
                    ]

                    if len(accepted) >= 2:
                        mat = np.stack([x["_embedding"] for x in accepted]).astype(np.float32)
                        sims = mat @ mat.T
                        med = np.median(sims, axis=1)
                        seed = int(np.argmax(med))
                        inliers = set(
                            int(i) for i in np.where(sims[seed] >= CONSENSUS_SIMILARITY)[0].tolist()
                        )
                        for local_idx, item in enumerate(accepted):
                            if local_idx not in inliers:
                                item["qualityStatus"] = "RETAKE"
                                item["qualityReason"] = "Khuôn mặt không nhất quán với các ảnh đăng ký còn lại."
                                item["_embedding"] = None

                    accepted = [
                        x for x in image_results
                        if x["qualityStatus"] == "PASS" and x["_embedding"] is not None
                    ]
                    pass_poses = {str(x["_pose"]).upper() for x in accepted}
                    ready = len(accepted) >= MIN_PASS_IMAGES and REQUIRED_POSES.issubset(pass_poses)

                    template_ref = None
                    quality_score = None
                    failure_reason = None

                    if ready:
                        mat = np.stack([x["_embedding"] for x in accepted]).astype(np.float32)
                        template = _l2(mat.mean(axis=0))
                        similarities = mat @ template
                        consistency = float(np.mean(similarities))
                        mean_quality = float(np.mean([x["qualityScore"] for x in accepted]))
                        quality_score = float(np.clip(
                            0.55 * mean_quality + 0.45 * consistency, 0.0, 1.0
                        ))

                        upload = await client.put(
                            req.templateUploadUrl,
                            content=_encode_npy(template),
                            headers={"Content-Type": "application/octet-stream"},
                            timeout=45.0,
                        )
                        upload.raise_for_status()
                        template_ref = req.templateObjectKey
                    else:
                        missing_poses = sorted(REQUIRED_POSES - pass_poses)
                        parts = [f"Ảnh PASS: {len(accepted)}/{MIN_PASS_IMAGES}."]
                        if missing_poses:
                            parts.append("Thiếu góc PASS: " + ", ".join(missing_poses) + ".")
                        failure_reason = " ".join(parts)

                    payload = {
                        "status": "READY" if ready else "NEEDS_RETAKE",
                        "failureReason": failure_reason,
                        "templateRef": template_ref,
                        "modelName": MODEL_NAME,
                        "templateVersion": MODEL_VERSION,
                        "embeddingDimension": EMBEDDING_DIM,
                        "qualityScore": quality_score,
                        "poseCoverage": ",".join(sorted(pass_poses)),
                        "images": [
                            {
                                "imageId": int(x["imageId"]),
                                "qualityScore": float(x["qualityScore"]),
                                "qualityStatus": str(x["qualityStatus"]),
                                "qualityReason": x["qualityReason"],
                            }
                            for x in image_results
                        ],
                    }
                    await self._callback(client, req, payload)

                except Exception as exc:
                    LOGGER.exception("Face enrollment job %s failed", job_id)
                    payload = {
                        "status": "FAILED",
                        "failureReason": f"{type(exc).__name__}: {exc}",
                        "templateRef": None,
                        "modelName": MODEL_NAME,
                        "templateVersion": MODEL_VERSION,
                        "embeddingDimension": EMBEDDING_DIM,
                        "qualityScore": None,
                        "poseCoverage": "",
                        "images": [
                            {
                                "imageId": int(x["imageId"]),
                                "qualityScore": float(x["qualityScore"]),
                                "qualityStatus": str(x["qualityStatus"]),
                                "qualityReason": x["qualityReason"],
                            }
                            for x in image_results
                        ],
                    }
                    try:
                        await self._callback(client, req, payload)
                    except Exception:
                        LOGGER.exception("Unable to report failed face enrollment job %s", job_id)

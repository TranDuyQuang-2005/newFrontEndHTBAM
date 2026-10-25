from __future__ import annotations

from dataclasses import dataclass
from io import BytesIO
from typing import Iterable
from urllib.request import Request, urlopen

import numpy as np

ARC_FACE_DIM = 512
SUPPORTED_MODEL_NAMES = {"ARCFACE", "BUFFALO_L", "INSIGHTFACE"}
TEMPLATE_FORMAT = "npy-float32-l2"
TEMPLATE_BASENAME = "arcface.npy"


def _l2(x: np.ndarray) -> np.ndarray:
    x = np.asarray(x, dtype=np.float32).reshape(-1)
    if x.shape != (ARC_FACE_DIM,):
        raise ValueError(f"ArcFace embedding must be [{ARC_FACE_DIM}], got {x.shape}")
    if not np.isfinite(x).all():
        raise ValueError("ArcFace embedding contains non-finite values")
    n = float(np.linalg.norm(x))
    if n <= 1e-12:
        raise ValueError("ArcFace embedding norm is zero")
    return (x / n).astype(np.float32, copy=False)


def decode_template_npy(data: bytes, expected_dim: int = ARC_FACE_DIM) -> np.ndarray:
    try:
        arr = np.load(BytesIO(data), allow_pickle=False)
    except Exception as exc:
        raise ValueError(f"Invalid NumPy .npy face template: {exc}") from exc
    arr = np.asarray(arr, dtype=np.float32).reshape(-1)
    if arr.shape != (int(expected_dim),):
        raise ValueError(
            f"Face template dimension mismatch: expected {expected_dim}, got {arr.shape}"
        )
    return _l2(arr)


def encode_template_npy(embedding: np.ndarray) -> bytes:
    emb = _l2(embedding)
    bio = BytesIO()
    np.save(bio, emb.astype(np.float32), allow_pickle=False)
    return bio.getvalue()


@dataclass(frozen=True)
class RosterTemplate:
    student_id: int
    embedding: np.ndarray
    template_version: str
    model_name: str
    embedding_dimension: int
    source_url: str


@dataclass(frozen=True)
class MatchEvidence:
    student_id: int | None
    best_score: float | None
    second_score: float | None
    margin: float | None
    decision: str
    candidate_count: int


class ArcFaceRoster:
    """
    Roster template loader/ranker.

    Default behavior is evidence-only because this project does not yet have a
    calibrated roster-specific ArcFace similarity+margin threshold.
    """

    def __init__(self, templates: Iterable[RosterTemplate]):
        items = list(templates)
        self._templates = items
        self._by_student = {int(x.student_id): x for x in items}
        if len(self._by_student) != len(items):
            raise ValueError("Duplicate studentId in roster templates")

    @property
    def size(self) -> int:
        return len(self._templates)

    @staticmethod
    def _validate_candidate(candidate) -> None:
        model_name = str(candidate.modelName).strip()
        upper = model_name.upper()
        if not any(token in upper for token in SUPPORTED_MODEL_NAMES):
            raise ValueError(
                f"Unsupported face model for student {candidate.studentId}: {model_name}"
            )
        if int(candidate.embeddingDimension) != ARC_FACE_DIM:
            raise ValueError(
                f"student {candidate.studentId}: expected ArcFace dim {ARC_FACE_DIM}, "
                f"got {candidate.embeddingDimension}"
            )
        if not str(candidate.templateReadUrl):
            raise ValueError(f"student {candidate.studentId}: empty templateReadUrl")

    @classmethod
    def from_candidates(cls, candidates, timeout_sec: float = 20.0):
        templates = []
        for candidate in candidates:
            cls._validate_candidate(candidate)
            req = Request(str(candidate.templateReadUrl), method="GET")
            try:
                with urlopen(req, timeout=float(timeout_sec)) as response:
                    data = response.read()
            except Exception as exc:
                raise RuntimeError(
                    f"Failed to download face template for student {candidate.studentId}: {exc}"
                ) from exc
            embedding = decode_template_npy(
                data,
                expected_dim=int(candidate.embeddingDimension),
            )
            templates.append(
                RosterTemplate(
                    student_id=int(candidate.studentId),
                    embedding=embedding,
                    template_version=str(candidate.templateVersion),
                    model_name=str(candidate.modelName),
                    embedding_dimension=int(candidate.embeddingDimension),
                    source_url=str(candidate.templateReadUrl),
                )
            )
        return cls(templates)

    @classmethod
    def from_vectors(cls, vectors: dict[int, np.ndarray]):
        return cls(
            RosterTemplate(
                student_id=int(student_id),
                embedding=_l2(vec),
                template_version="synthetic-smoke",
                model_name="ArcFace",
                embedding_dimension=ARC_FACE_DIM,
                source_url="memory://synthetic",
            )
            for student_id, vec in vectors.items()
        )

    def rank(self, query_embedding: np.ndarray) -> MatchEvidence:
        if not self._templates:
            return MatchEvidence(
                student_id=None,
                best_score=None,
                second_score=None,
                margin=None,
                decision="NO_ROSTER_TEMPLATE",
                candidate_count=0,
            )

        q = _l2(query_embedding)
        scored = sorted(
            (
                (float(np.dot(q, item.embedding)), int(item.student_id))
                for item in self._templates
            ),
            reverse=True,
        )
        best_score, best_student = scored[0]
        second_score = scored[1][0] if len(scored) > 1 else None
        margin = float(best_score - second_score) if second_score is not None else None
        return MatchEvidence(
            student_id=best_student,
            best_score=float(best_score),
            second_score=float(second_score) if second_score is not None else None,
            margin=margin,
            decision="EVIDENCE_ONLY",
            candidate_count=len(scored),
        )

    def decide(
        self,
        query_embedding: np.ndarray,
        *,
        min_similarity: float | None = None,
        min_margin: float | None = None,
    ) -> MatchEvidence:
        evidence = self.rank(query_embedding)
        if evidence.student_id is None:
            return evidence

        if min_similarity is None or min_margin is None:
            return evidence

        if evidence.margin is None:
            return MatchEvidence(
                student_id=evidence.student_id,
                best_score=evidence.best_score,
                second_score=evidence.second_score,
                margin=evidence.margin,
                decision="INSUFFICIENT_MARGIN_EVIDENCE",
                candidate_count=evidence.candidate_count,
            )

        if evidence.best_score is None or evidence.best_score < float(min_similarity):
            decision = "BELOW_SIMILARITY_THRESHOLD"
        elif evidence.margin < float(min_margin):
            decision = "AMBIGUOUS_MARGIN"
        else:
            decision = "ACCEPTED"

        return MatchEvidence(
            student_id=evidence.student_id,
            best_score=evidence.best_score,
            second_score=evidence.second_score,
            margin=evidence.margin,
            decision=decision,
            candidate_count=evidence.candidate_count,
        )

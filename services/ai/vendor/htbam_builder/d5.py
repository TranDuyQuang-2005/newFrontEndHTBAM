from __future__ import annotations

from bisect import bisect_left, bisect_right
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable
import hashlib
import json
import math
import shutil

import cv2
import numpy as np
import pandas as pd

from .core import cosine_similarity
from .ort_runtime import prepare_onnxruntime_for_providers, assert_insightface_cuda_active


CANDIDATE_COLUMNS = [
    "class_id", "session_id", "pre_tracklet", "post_tracklet",
    "pre_track_id", "post_track_id", "pre_student_id", "post_student_id",
    "gap_sec", "face_similarity", "body_similarity", "appearance_similarity",
    "body_pair_median", "body_pair_min", "body_pair_support", "body_pair_count",
    "temporal_score", "fusion_score",
    "pre_face_reliability", "post_face_reliability",
    "pre_face_samples", "post_face_samples", "face_pair_trusted",
    "pre_body_clean_ratio", "post_body_clean_ratio",
    "pre_body_end_clean_ratio", "post_body_start_clean_ratio",
    "pre_body_end_purity", "post_body_start_purity",
    "pre_body_end_usable_samples", "post_body_start_usable_samples",
    "pre_body_contaminated_samples", "post_body_contaminated_samples",
    "body_purity_pair_ok", "spatial_distance", "spatial_scale_log", "spatial_score",
    "label", "decision", "review_status", "decision_reason",
]


FACE_TARGET_AUDIT_COLUMNS = [
    "class_id", "session_id", "tracklet_id", "track_id", "frame_index", "timestamp_sec",
    "body_image_path", "target_face_image_path", "association_status",
    "n_faces_detected", "n_plausible_faces", "association_score",
    "association_runner_up_score", "association_margin", "det_score",
    "face_center_x_rel", "face_center_y_rel", "face_person_ratio",
    "d3_temporal_status", "d3_temporal_similarity", "d3_temporal_dominance",
    "body_purity_status", "body_purity_score", "face_purity_status", "face_purity_score",
    "used_for_consensus", "consensus_similarity",
]


def _clean_sid(value) -> str:
    if value is None or pd.isna(value):
        return ""
    s = str(value).strip()
    return "" if s.lower() in {"", "nan", "none", "<na>"} else s


def _load_session_calibration(cfg: dict, root: Path, class_id: str, session_id: str) -> tuple[dict, dict]:
    """Load an optional QC-derived promotion profile for one session.

    The profile is intentionally session-scoped and may only override keys prefixed
    ``calibrated_``. It can therefore add a conservative review->AUTO promotion gate
    without silently changing the base ArcFace/OSNet/purity/spatial thresholds.
    """
    out = dict(cfg)
    profile_path = root / "work" / "d5_calibration" / class_id / session_id / "calibration_profile.json"
    info = {"enabled": False, "profile_path": str(profile_path), "profile_id": "", "overrides": {}}
    if not profile_path.is_file():
        return out, info
    try:
        data = json.loads(profile_path.read_text(encoding="utf-8"))
    except Exception as e:
        raise RuntimeError(f"Invalid D5 calibration profile: {profile_path}: {e}") from e
    if not bool(data.get("enabled", False)):
        return out, info
    overrides = data.get("overrides", {})
    if not isinstance(overrides, dict):
        raise RuntimeError(f"Invalid calibration overrides in {profile_path}")
    illegal = [k for k in overrides if not str(k).startswith("calibrated_")]
    if illegal:
        raise RuntimeError("Calibration profile may only override calibrated_* keys: " + ", ".join(sorted(illegal)))
    out.update(overrides)
    info = {
        "enabled": True,
        "profile_path": str(profile_path),
        "profile_id": str(data.get("profile_id", "")),
        "reviewed_pairs": int(data.get("reviewed_pairs", 0) or 0),
        "estimated_precision": data.get("estimated_precision", None),
        "estimated_promotions": int(data.get("estimated_promotions", 0) or 0),
        "overrides": overrides,
    }
    return out, info


def _l2(v: np.ndarray | None) -> np.ndarray | None:
    if v is None:
        return None
    v = np.asarray(v, dtype=np.float32).reshape(-1)
    if not np.isfinite(v).all():
        return None
    n = float(np.linalg.norm(v))
    if n <= 1e-12:
        return None
    return v / n


def _mean_normalized(vectors: Iterable[np.ndarray | None]) -> np.ndarray | None:
    xs = [np.asarray(v, dtype=np.float32).reshape(-1) for v in vectors if v is not None]
    if not xs:
        return None
    return _l2(np.mean(np.stack(xs), axis=0))


def _sha256(path: Path) -> str | None:
    if not path.is_file():
        return None
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


class BodyReIDExtractor:
    """OSNet-AIN body feature extractor backed by Torchreid."""

    def __init__(self, model_name: str, model_path: str | Path, device: str = "cuda"):
        model_path = Path(model_path)
        if not model_path.is_file():
            raise FileNotFoundError(
                f"ReID checkpoint not found: {model_path}. Run python scripts/00_prepare_models.py"
            )
        try:
            from torchreid.utils import FeatureExtractor
        except Exception:
            try:
                from torchreid.reid.utils import FeatureExtractor
            except Exception as e:
                raise RuntimeError(
                    "Torchreid is required for D5 body ReID. Install requirements-vision.txt."
                ) from e
        self.extractor = FeatureExtractor(
            model_name=model_name,
            model_path=str(model_path),
            device=device,
        )

    def extract_paths(self, paths: list[Path], batch_size: int = 64) -> dict[str, np.ndarray]:
        out: dict[str, np.ndarray] = {}
        if not paths:
            return out
        bs = max(1, int(batch_size))
        for i in range(0, len(paths), bs):
            chunk = paths[i:i + bs]
            feats = self.extractor([str(p) for p in chunk])
            if hasattr(feats, "detach"):
                feats = feats.detach().cpu().numpy()
            feats = np.asarray(feats, dtype=np.float32)
            for p, f in zip(chunk, feats):
                nf = _l2(f)
                if nf is not None:
                    out[str(p.resolve())] = nf
            print(f"[D5 ReID] {min(i + bs, len(paths))}/{len(paths)} body crops", flush=True)
        return out


class D3AlignedFaceEmbedder:
    """ArcFace embedding extractor for faces already assigned in D3 on the full frame.

    D5 must not rediscover a face from a D4 body crop because that crop can contain a
    neighbouring person's face. D3 performs the safer operation: detect faces on the original
    full frame, score face↔person geometry for all visible tracks, enforce one-to-one ownership,
    and reject ambiguous assignments. D5 only embeds those accepted, track-owned aligned faces.
    """

    def __init__(self, model_name: str, providers: list[str], det_size: int = 640):
        prepare_onnxruntime_for_providers(providers)
        try:
            from insightface.app import FaceAnalysis
        except ImportError as e:
            raise RuntimeError("InsightFace is required for D5 D3-face embeddings") from e
        self.app = FaceAnalysis(name=model_name, providers=providers)
        assert_insightface_cuda_active(self.app, providers)
        ctx_id = 0 if "CUDAExecutionProvider" in providers else -1
        self.app.prepare(ctx_id=ctx_id, det_size=(int(det_size), int(det_size)))
        self.model = self.app.models.get("recognition")
        if self.model is None or not hasattr(self.model, "get_feat"):
            raise RuntimeError("InsightFace recognition model is unavailable in FaceAnalysis")

    def extract_paths(self, paths: list[Path], batch_size: int = 128) -> dict[str, np.ndarray]:
        out: dict[str, np.ndarray] = {}
        if not paths:
            return out
        bs = max(1, int(batch_size))
        for i in range(0, len(paths), bs):
            chunk = paths[i:i + bs]
            imgs, valid = [], []
            for p in chunk:
                img = cv2.imread(str(p))
                if img is None or img.size == 0:
                    continue
                imgs.append(img)
                valid.append(p)
            if imgs:
                feats = self.model.get_feat(imgs)
                feats = np.asarray(feats, dtype=np.float32)
                if feats.ndim == 1:
                    feats = feats[None, :]
                for p, f in zip(valid, feats):
                    nf = _l2(f)
                    if nf is not None:
                        out[str(p.resolve())] = nf
            print(f"[D5 ArcFace/D3] {min(i + bs, len(paths))}/{len(paths)} aligned D3 faces", flush=True)
        return out


@dataclass
class TrackletEvidence:
    class_id: str
    session_id: str
    tracklet_id: str
    track_id: int
    student_id: str
    start_sec: float
    end_sec: float
    start_frame: int
    end_frame: int
    body_start: np.ndarray | None = None
    body_end: np.ndarray | None = None
    face: np.ndarray | None = None
    n_body_start: int = 0
    n_body_end: int = 0
    n_face: int = 0
    face_reliability: float = 0.0
    face_association_status: str = "NO_TARGET_FACE"
    face_best_image_path: str = ""
    body_start_samples: list[np.ndarray] = field(default_factory=list)
    body_end_samples: list[np.ndarray] = field(default_factory=list)
    face_owner_margin: float = 1.0
    face_temporal_dominance: float = 1.0
    # Permissive defaults keep programmatic/unit-test TrackletEvidence construction backward-compatible.
    # Real dataset builds overwrite every field below from D3/D4 graded-purity measurements.
    body_clean_ratio: float = 1.0
    body_start_clean_ratio: float = 1.0
    body_end_clean_ratio: float = 1.0
    body_start_purity: float = 1.0
    body_end_purity: float = 1.0
    body_start_usable_samples: int = 3
    body_end_usable_samples: int = 3
    body_contaminated_samples: int = 0
    body_purity_samples: int = 3
    start_cx: float = 0.0
    start_cy: float = 0.0
    start_w: float = 0.0
    start_h: float = 0.0
    end_cx: float = 0.0
    end_cy: float = 0.0
    end_w: float = 0.0
    end_h: float = 0.0


def _select_rep_paths(g: pd.DataFrame, n: int) -> tuple[list[str], list[str]]:
    g = g.sort_values("timestamp_sec")
    n = max(1, int(n))
    start = list(dict.fromkeys(g.head(n)["image_path"].astype(str).tolist()))
    end = list(dict.fromkeys(g.tail(n)["image_path"].astype(str).tolist()))
    return start, end


def _select_spread_rows(g: pd.DataFrame, n: int) -> pd.DataFrame:
    g = g.sort_values("timestamp_sec").reset_index(drop=True)
    if g.empty:
        return g
    n = max(1, int(n))
    if len(g) <= n:
        return g
    idx = sorted(set(int(round(x)) for x in np.linspace(0, len(g) - 1, n)))
    return g.iloc[idx].copy()


def robust_face_consensus(
    vectors: list[np.ndarray], scores: list[float], cfg: dict
) -> tuple[np.ndarray | None, list[int], float, str]:
    """Robustly aggregate target-face samples and drop a residual identity outlier."""
    xs = [_l2(v) for v in vectors]
    good = [i for i, v in enumerate(xs) if v is not None]
    if not good:
        return None, [], 0.0, "NO_TARGET_FACE"
    if len(good) == 1:
        i = good[0]
        reliability = min(0.58, 0.45 + 0.15 * float(np.clip(scores[i], 0.0, 1.0)))
        return xs[i], [i], float(reliability), "SINGLE_TARGET_FACE"

    mat = np.stack([xs[i] for i in good])
    sims = mat @ mat.T
    # Pick the medoid, then keep only samples consistent with that identity.
    med = np.median(sims, axis=1)
    seed_local = int(np.argmax(med))
    inlier_thr = float(cfg.get("target_face_consensus_inlier_similarity", 0.45))
    keep_local = np.where(sims[seed_local] >= inlier_thr)[0].tolist()
    keep = [good[i] for i in keep_local]
    min_inliers = max(2, int(cfg.get("target_face_consensus_min_samples", 2)))
    if len(keep) < min_inliers:
        return None, [], 0.0, "INCONSISTENT_TARGET_FACES"

    consensus = _mean_normalized([xs[i] for i in keep])
    if consensus is None:
        return None, [], 0.0, "INCONSISTENT_TARGET_FACES"
    cos = [float(np.dot(xs[i], consensus)) for i in keep]
    consistency = float(np.mean(cos))
    assoc = float(np.mean([float(np.clip(scores[i], 0.0, 1.0)) for i in keep]))
    support = min(1.0, len(keep) / max(2.0, float(cfg.get("target_face_reliability_full_samples", 3))))
    # Reliability combines identity consensus, geometric target-association quality, and support.
    consistency01 = float(np.clip((consistency - 0.25) / 0.70, 0.0, 1.0))
    reliability = float(np.clip(0.50 * consistency01 + 0.30 * assoc + 0.20 * support, 0.0, 1.0))
    status = "VALIDATED_TARGET_FACE" if reliability >= float(cfg.get("target_face_valid_reliability", 0.58)) else "WEAK_TARGET_FACE"
    return consensus, keep, reliability, status


def _enrich_body_purity_from_d3(body_df: pd.DataFrame, face_df: pd.DataFrame, cfg: dict) -> pd.DataFrame:
    """Attach BodyTrackPurityGuard fields to D4 rows.

    New D4 builds already persist these fields.  For --refresh-identity on an existing dataset,
    D4 is intentionally not regenerated, so we transfer purity from the freshly rebuilt D3 rows
    by exact tracklet and nearest timestamp.  Unknown purity is conservative and never AUTO-eligible.
    """
    out = body_df.copy()
    cols = [
        "body_purity_status", "body_purity_score", "face_purity_status", "face_purity_score",
        "body_purity_max_target_overlap", "body_purity_max_other_overlap", "body_purity_weighted_overlap",
        "body_purity_intruder_count", "body_purity_head_intruder_count",
        "body_purity_foreign_head_overlap", "body_purity_foreign_head_target_overlap",
    ]
    # Always refresh these diagnostics from the freshly rebuilt D3.  D4 may contain purity
    # fields produced by an older hard-gate implementation; reusing them would preserve the
    # exact recall regression this revision is meant to remove.
    for c in cols:
        if c not in out.columns:
            out[c] = "UNKNOWN" if c in {"body_purity_status", "face_purity_status"} else np.nan
    if face_df.empty or not {"tracklet_id", "timestamp_sec", "body_purity_status"}.issubset(face_df.columns):
        return out

    max_dt = float(cfg.get("body_purity_nearest_max_sec", 0.60))
    face_groups = {str(k): g.sort_values("timestamp_sec") for k, g in face_df.groupby(face_df["tracklet_id"].astype(str), sort=False)}
    for tid, idxs in out.groupby(out["tracklet_id"].astype(str), sort=False).groups.items():
        fg = face_groups.get(str(tid))
        if fg is None or fg.empty:
            continue
        ft = pd.to_numeric(fg["timestamp_sec"], errors="coerce").to_numpy(dtype=float)
        valid = np.isfinite(ft)
        if not valid.any():
            continue
        fg = fg.iloc[np.where(valid)[0]].reset_index(drop=True)
        ft = ft[valid]
        for oi in idxs:
            try:
                ts = float(out.at[oi, "timestamp_sec"])
            except Exception:
                continue
            j = int(np.argmin(np.abs(ft - ts)))
            if abs(float(ft[j]) - ts) > max_dt:
                continue
            rr = fg.iloc[j]
            for c in cols:
                if c in rr.index:
                    out.at[oi, c] = rr[c]
    return out


def _purity_stats(g: pd.DataFrame, endpoint_n: int, cfg: dict) -> tuple[float, float, float, float, float, int, int, int, int]:
    """Return graded purity statistics without invalidating a whole tracklet.

    The key quantities for re-entry are the *best usable endpoint samples*, not the percentage of
    perfectly clean frames over the entire tracklet.
    """
    if g.empty or 'body_purity_score' not in g.columns:
        return 0.0, 0.0, 0.0, 0.0, 0.0, 0, 0, 0, 0
    score = pd.to_numeric(g['body_purity_score'], errors='coerce').fillna(0.0).clip(0.0, 1.0)
    status = g.get('body_purity_status', pd.Series('UNKNOWN', index=g.index)).fillna('UNKNOWN').astype(str).str.upper()
    total = int(len(g))
    contaminated = int(status.eq('BODY_CROP_CONTAMINATED').sum())
    usable_thr = float(cfg.get('body_purity_min_sample_score', 0.55))
    usable = score.ge(usable_thr) & ~status.eq('BODY_CROP_CONTAMINATED')
    overall = float(score.mean()) if total else 0.0
    n = max(1, int(endpoint_n))
    ts = pd.to_numeric(g['timestamp_sec'], errors='coerce')
    t0, t1 = float(ts.min()), float(ts.max())
    window = float(cfg.get('body_endpoint_window_sec', 5.0))
    smask, emask = ts.le(t0 + window), ts.ge(t1 - window)
    sidx = score[smask].sort_values(ascending=False).head(n).index
    eidx = score[emask].sort_values(ascending=False).head(n).index
    sh, eh = score.loc[sidx], score.loc[eidx]
    su, eu = usable.loc[sidx], usable.loc[eidx]
    return (
        overall,
        float(sh.mean()) if len(sh) else 0.0,
        float(eh.mean()) if len(eh) else 0.0,
        float(sh.max()) if len(sh) else 0.0,
        float(eh.max()) if len(eh) else 0.0,
        int(su.sum()), int(eu.sum()), contaminated, total,
    )


def _select_quality_endpoint_rows(g: pd.DataFrame, n: int, cfg: dict) -> tuple[pd.DataFrame, pd.DataFrame]:
    """Pick the highest-quality samples from short start/end windows.

    A tracklet can be crowded for most of its duration and still have excellent re-entry evidence at
    its endpoints.  This is deliberately different from the previous whole-track hard purity gate.
    """
    if g.empty:
        return g.copy(), g.copy()
    x = g.sort_values('timestamp_sec').copy()
    n = max(1, int(n))
    window = float(cfg.get('body_endpoint_window_sec', 5.0))
    min_score = float(cfg.get('body_purity_min_sample_score', 0.55))
    score = pd.to_numeric(x.get('body_purity_score', 0.0), errors='coerce').fillna(0.0)
    status = x.get('body_purity_status', pd.Series('UNKNOWN', index=x.index)).fillna('UNKNOWN').astype(str).str.upper()
    x['_purity'] = score
    x['_usable'] = score.ge(min_score) & ~status.eq('BODY_CROP_CONTAMINATED')
    t0, t1 = float(x.timestamp_sec.min()), float(x.timestamp_sec.max())
    start_pool = x[x.timestamp_sec <= t0 + window].copy()
    end_pool = x[x.timestamp_sec >= t1 - window].copy()

    def pick(pool: pd.DataFrame, reverse_time: bool) -> pd.DataFrame:
        if pool.empty:
            return pool
        usable_pool = pool[pool['_usable']].copy()
        src = usable_pool if not usable_pool.empty else pool.copy()
        # Purity dominates; timestamp breaks ties toward the actual endpoint.
        src = src.sort_values(['_purity', 'timestamp_sec'], ascending=[False, not reverse_time])
        return src.head(n).sort_values('timestamp_sec').drop(columns=['_purity','_usable'], errors='ignore')

    return pick(start_pool, False), pick(end_pool, True)


def _make_central_crop(src: Path, dst: Path, cfg: dict) -> Path | None:
    img = cv2.imread(str(src))
    if img is None or img.size == 0:
        return None
    h, w = img.shape[:2]
    x1 = int(round(float(cfg.get('central_crop_x1', 0.14)) * w))
    x2 = int(round(float(cfg.get('central_crop_x2', 0.86)) * w))
    y1 = int(round(float(cfg.get('central_crop_y1', 0.18)) * h))
    y2 = int(round(float(cfg.get('central_crop_y2', 0.82)) * h))
    x1, y1 = max(0,x1), max(0,y1)
    x2, y2 = min(w,x2), min(h,y2)
    if x2-x1 < 20 or y2-y1 < 40:
        return None
    crop = img[y1:y2, x1:x2]
    dst.parent.mkdir(parents=True, exist_ok=True)
    if not cv2.imwrite(str(dst), crop, [cv2.IMWRITE_JPEG_QUALITY, 95]):
        return None
    return dst


def _body_purity_auto_ok(pre: TrackletEvidence, post: TrackletEvidence, cfg: dict) -> bool:
    """Endpoint-quality gate: do not require the entire tracklet to be clean."""
    min_count = max(1, int(cfg.get('body_endpoint_min_usable_samples', 2)))
    min_best = float(cfg.get('body_endpoint_min_best_purity_for_auto', 0.66))
    min_mean = float(cfg.get('body_endpoint_min_mean_purity_for_auto', 0.58))
    return bool(
        pre.body_end_usable_samples >= min_count
        and post.body_start_usable_samples >= min_count
        and pre.body_end_purity >= min_best
        and post.body_start_purity >= min_best
        and pre.body_end_clean_ratio >= min_mean
        and post.body_start_clean_ratio >= min_mean
    )


def _calibrated_promotion_reason(
    pre: TrackletEvidence, post: TrackletEvidence, cfg: dict, *, original_reason: str,
    gap: float, face_sim: float | None, body_sim: float | None, body_pair_median: float,
    body_pair_min: float, body_pair_support: float, body_pair_count: int, fusion: float,
    spatial_score: float, face_trusted: bool, body_purity_pair_ok: bool,
    spatial_contradiction: bool,
) -> str | None:
    """Return a QC-calibrated promotion reason, or None.

    This gate never overrides contradictions, reviewed identities, or hard cluster constraints.
    It only promotes previously-NEEDS_REVIEW visual pairs when a session-specific profile,
    fitted from human QC, says the pair is inside the validated high-precision region.
    """
    if not bool(cfg.get("calibrated_promotion_enabled", False)):
        return None
    r = str(original_reason or "").upper()
    blocked_tokens = ("CONFLICT", "CONTRADICTION", "DIFFERENT", "CLUSTER_", "KNOWN_IDENTITY")
    if spatial_contradiction or any(t in r for t in blocked_tokens):
        return None
    allowed = cfg.get("calibrated_allowed_review_reasons", [
        "FACE_STRONG_BODY_NOT_CORROBORATED",
        "FACE_EVIDENCE_NOT_TRUSTED_FOR_AUTO",
        "AMBIGUOUS_FACE_REID",
        "BODY_PURITY_INSUFFICIENT",
    ])
    allowed = {str(x).upper() for x in allowed}
    if r not in allowed:
        return None
    if body_sim is None or face_sim is None:
        return None
    if gap > float(cfg.get("calibrated_max_gap_sec", 8.0)):
        return None
    if body_pair_count < int(cfg.get("calibrated_body_pair_min_count", 4)):
        return None
    if float(body_sim) < float(cfg.get("calibrated_body_similarity_min", 0.90)):
        return None
    if not np.isfinite(body_pair_median) or body_pair_median < float(cfg.get("calibrated_body_pair_median_min", 0.90)):
        return None
    if not np.isfinite(body_pair_min) or body_pair_min < float(cfg.get("calibrated_body_pair_min_similarity", 0.76)):
        return None
    if body_pair_support < float(cfg.get("calibrated_body_pair_support_min", 0.80)):
        return None
    if fusion < float(cfg.get("calibrated_fusion_min", 0.76)):
        return None
    if spatial_score < float(cfg.get("calibrated_spatial_min", 0.45)):
        return None

    purity_ok = bool(body_purity_pair_ok)
    if not purity_ok and bool(cfg.get("calibrated_allow_relaxed_purity", False)):
        min_n = int(cfg.get("calibrated_endpoint_min_usable_samples", 2))
        min_p = float(cfg.get("calibrated_endpoint_min_purity", 0.60))
        min_mean = float(cfg.get("calibrated_endpoint_min_clean_ratio", 0.50))
        purity_ok = bool(
            pre.body_end_usable_samples >= min_n and post.body_start_usable_samples >= min_n
            and pre.body_end_purity >= min_p and post.body_start_purity >= min_p
            and pre.body_end_clean_ratio >= min_mean and post.body_start_clean_ratio >= min_mean
        )
    if not purity_ok:
        return None

    if face_trusted:
        if float(face_sim) < float(cfg.get("calibrated_trusted_face_similarity_min", 0.62)):
            return None
        return "QC_CALIBRATED_PROMOTION_TRUSTED_FACE"

    min_rel = min(float(pre.face_reliability), float(post.face_reliability))
    min_samples = min(int(pre.n_face), int(post.n_face))
    if min_rel < float(cfg.get("calibrated_weak_face_reliability_min", 0.58)):
        return None
    if min_samples < int(cfg.get("calibrated_weak_face_min_samples", 2)):
        return None
    if float(face_sim) < float(cfg.get("calibrated_weak_face_similarity_min", 0.68)):
        return None
    return "QC_CALIBRATED_PROMOTION_WEAK_FACE"


def _spatial_pair_stats(pre: TrackletEvidence, post: TrackletEvidence, gap: float, cfg: dict) -> tuple[float, float, float]:
    """Fixed-camera spatial continuity normalized by person height."""
    h = max(8.0, 0.5*(float(pre.end_h) + float(post.start_h)))
    if h <= 8.0 or pre.end_h <= 0 or post.start_h <= 0:
        return float('nan'), float('nan'), 0.5
    dx = float(post.start_cx - pre.end_cx)
    dy = float(post.start_cy - pre.end_cy)
    dist = float(math.sqrt(dx*dx + dy*dy) / h)
    scale_log = float(abs(math.log(max(post.start_h,1.0) / max(pre.end_h,1.0))))
    # Allow larger displacement as the temporal gap grows, but keep short-gap jumps expensive.
    tau0 = float(cfg.get('spatial_tau_body_heights', 0.85))
    tau = tau0 + min(max(gap,0.0), 20.0)/20.0 * float(cfg.get('spatial_tau_gap_gain', 1.15))
    scale_tau = float(cfg.get('spatial_scale_tau', 0.50))
    score = math.exp(-dist/max(tau,1e-6)) * math.exp(-scale_log/max(scale_tau,1e-6))
    return dist, scale_log, float(np.clip(score,0.0,1.0))

def _valid_d3_faces_for_tracklet(
    face_df: pd.DataFrame, tr: TrackletEvidence, cfg: dict
) -> pd.DataFrame:
    """Return only full-frame D3 faces that are explicitly owned by this tracklet.

    Ownership is intentionally stricter than "face happens to lie inside the D4 body crop".
    The row must have been globally assigned by D3 to this exact track ID and fall inside this
    tracklet's frame interval. Additional D5 score/margin guards can only reject rows; they can
    never reassign a face to another body.
    """
    if face_df.empty:
        return face_df.iloc[0:0].copy()
    fi = pd.to_numeric(face_df["frame_index"], errors="coerce")
    status = face_df["status"].astype(str).str.upper()
    assign = face_df["face_assignment_status"].astype(str).str.upper()
    temporal = face_df["face_temporal_status"].astype(str).str.upper()
    image_ok = face_df["image_path"].fillna("").astype(str).str.strip().ne("")
    # Exact contiguous tracklet ownership is mandatory. Numeric tracker IDs can be reused after
    # an occlusion/ID switch, so D5 must not borrow a face from another segment of the same ID.
    tracklet = face_df["tracklet_id"].astype(str)
    g = face_df[
        tracklet.eq(str(tr.tracklet_id))
        & fi.ge(int(tr.start_frame))
        & fi.le(int(tr.end_frame))
        & status.eq("ACCEPT")
        & assign.eq("ASSIGNED")
        # D3 must independently verify that the face identity is temporally coherent inside
        # this tracklet. SINGLE_SAMPLE/AMBIGUOUS/CONFLICT faces are audit-only, never D5 identity evidence.
        & temporal.eq("TEMPORAL_CONSISTENT")
        # Body overlap and face ownership are intentionally independent. Mild body overlap must
        # not discard a geometrically/temporally valid face; only FACE_CONTAMINATED is blocked.
        & ~face_df.get("face_purity_status", pd.Series("UNKNOWN", index=face_df.index)).astype(str).str.upper().eq("FACE_CONTAMINATED")
        & image_ok
    ].copy()
    if g.empty:
        return g

    min_score = float(cfg.get("d3_face_min_association_score", 0.55))
    score = pd.to_numeric(g.get("face_person_association_score", np.nan), errors="coerce")
    g = g[score.ge(min_score)].copy()
    if g.empty:
        return g

    # NaN margin means D3 saw only one plausible owner/face alternative and is acceptable.
    min_margin = float(cfg.get("d3_face_min_association_margin", 0.10))
    margin = pd.to_numeric(g.get("face_person_association_margin", np.nan), errors="coerce")
    g = g[margin.isna() | margin.ge(min_margin)].copy()
    if g.empty:
        return g
    min_owner_margin = float(cfg.get("d3_face_min_owner_margin", 0.16))
    owner_raw = g["face_person_owner_margin"] if "face_person_owner_margin" in g.columns else pd.Series(np.nan, index=g.index)
    owner_margin = pd.to_numeric(owner_raw, errors="coerce")
    g = g[owner_margin.isna() | np.isinf(owner_margin) | owner_margin.ge(min_owner_margin)].copy()
    return g.sort_values("frame_index")


def summarize_tracklets(
    d3_dir: str | Path,
    d4_dir: str | Path,
    cfg: dict,
    project_root: str | Path,
    face_target_output_dir: str | Path,
) -> tuple[list[TrackletEvidence], pd.DataFrame]:
    """Build body evidence from D4 and face evidence only from strict full-frame D3 ownership.

    Root-cause fix for "body A, face B": D5 no longer runs face detection inside a D4 body crop.
    A body crop may contain multiple people. Instead D5 consumes only D3 rows that were assigned
    on the original full frame using global one-to-one face↔track association and ambiguity
    rejection, then applies ArcFace consensus across multiple samples of the same tracklet.
    """
    d3_dir, d4_dir, root = Path(d3_dir), Path(d4_dir), Path(project_root)
    face_target_output_dir = Path(face_target_output_dir)
    d4_meta = d4_dir / "metadata.csv"
    d3_meta = d3_dir / "metadata.csv"
    if not d4_meta.is_file():
        raise FileNotFoundError(f"D4 metadata not found: {d4_meta}")
    if not d3_meta.is_file():
        raise FileNotFoundError(
            f"D3 metadata not found: {d3_meta}. Run --refresh-identity once before --refresh-d5."
        )

    body_df = pd.read_csv(d4_meta, dtype={"student_id": str, "tracklet_id": str}, low_memory=False)
    face_df = pd.read_csv(d3_meta, dtype={"student_id": str, "tracklet_id": str}, low_memory=False)
    if body_df.empty:
        return [], pd.DataFrame(columns=FACE_TARGET_AUDIT_COLUMNS)
    required_d3 = {
        "track_id", "tracklet_id", "frame_index", "timestamp_sec", "image_path", "status",
        "face_assignment_status", "face_person_association_score",
        "face_person_association_margin", "face_person_owner_margin", "face_temporal_status",
        "face_temporal_similarity", "face_temporal_dominance",
        "body_purity_status", "body_purity_score", "face_purity_status", "face_purity_score",
    }
    missing = required_d3.difference(face_df.columns)
    if missing:
        raise RuntimeError(
            "D3 metadata is from an older/non-strict build; missing columns: "
            + ", ".join(sorted(missing))
            + ". Run --refresh-identity to rebuild D3."
        )

    body_df = _enrich_body_purity_from_d3(body_df, face_df, cfg)

    representative_images = int(cfg.get("representative_images", 3))
    face_rep_images = int(cfg.get("target_face_representative_images", 5))

    tracklets: list[TrackletEvidence] = []
    body_reps: dict[str, tuple[list[str], list[str]]] = {}
    face_rep_rows: dict[str, pd.DataFrame] = {}
    for tracklet_id, g in body_df.groupby("tracklet_id", sort=False):
        g = g.sort_values("timestamp_sec")
        tr = TrackletEvidence(
            class_id=str(g.iloc[0].class_id),
            session_id=str(g.iloc[0].session_id),
            tracklet_id=str(tracklet_id),
            track_id=int(g.iloc[0].track_id),
            student_id=_clean_sid(g.iloc[0].student_id),
            start_sec=float(g.timestamp_sec.min()),
            end_sec=float(g.timestamp_sec.max()),
            start_frame=int(g.frame_index.min()),
            end_frame=int(g.frame_index.max()),
        )
        overall, start_mean, end_mean, start_best, end_best, start_usable, end_usable, contaminated, purity_samples = _purity_stats(g, representative_images, cfg)
        tr.body_clean_ratio = overall
        tr.body_start_clean_ratio = start_mean
        tr.body_end_clean_ratio = end_mean
        tr.body_start_purity = start_best
        tr.body_end_purity = end_best
        tr.body_start_usable_samples = start_usable
        tr.body_end_usable_samples = end_usable
        tr.body_contaminated_samples = contaminated
        tr.body_purity_samples = purity_samples
        # Fixed-camera spatial endpoints from the available D4 samples.
        first, last = g.iloc[0], g.iloc[-1]
        tr.start_cx = 0.5*(float(first.x1)+float(first.x2)); tr.start_cy = 0.5*(float(first.y1)+float(first.y2))
        tr.start_w = max(1.0,float(first.x2)-float(first.x1)); tr.start_h = max(1.0,float(first.y2)-float(first.y1))
        tr.end_cx = 0.5*(float(last.x1)+float(last.x2)); tr.end_cy = 0.5*(float(last.y1)+float(last.y2))
        tr.end_w = max(1.0,float(last.x2)-float(last.x1)); tr.end_h = max(1.0,float(last.y2)-float(last.y1))
        tracklets.append(tr)
        start_rows, end_rows = _select_quality_endpoint_rows(g, representative_images, cfg)
        body_reps[tr.tracklet_id] = (
            start_rows["image_path"].astype(str).tolist(),
            end_rows["image_path"].astype(str).tolist(),
        )
        owned = _valid_d3_faces_for_tracklet(face_df, tr, cfg)
        face_rep_rows[tr.tracklet_id] = _select_spread_rows(owned, face_rep_images)

    body_rel_paths: list[str] = []
    for start, end in body_reps.values():
        body_rel_paths.extend(start); body_rel_paths.extend(end)
    body_paths = list(dict.fromkeys((d4_dir / rel).resolve() for rel in body_rel_paths if str(rel).strip()))

    # Build central-torso views on-the-fly so --refresh-identity can benefit without regenerating D4.
    central_root = face_target_output_dir.parent / "body_central"
    central_map: dict[str, Path] = {}
    for src in body_paths:
        try:
            rel_key = str(src.resolve())
            dst = central_root / (src.stem + "_central.jpg")
            made = _make_central_crop(src, dst, cfg)
            if made is not None:
                central_map[rel_key] = made.resolve()
        except Exception:
            continue

    reid_weights = Path(str(cfg.get("reid_weights", "pretrained/reid/osnet_ain_x1_0_msmt17.pth")))
    if not reid_weights.is_absolute():
        reid_weights = root / reid_weights
    body_extractor = BodyReIDExtractor(
        str(cfg.get("reid_model_name", "osnet_ain_x1_0")), reid_weights,
        device=str(cfg.get("device", "cuda")),
    )
    all_body_paths = body_paths + list(dict.fromkeys(central_map.values()))
    body_feats = body_extractor.extract_paths(all_body_paths, batch_size=int(cfg.get("embedding_batch_size", 64)))

    def fused_body(rel: str) -> np.ndarray | None:
        if not str(rel).strip():
            return None
        src = (d4_dir / rel).resolve()
        full = body_feats.get(str(src))
        cent_path = central_map.get(str(src))
        cent = body_feats.get(str(cent_path)) if cent_path is not None else None
        if full is not None and cent is not None:
            return _mean_normalized([full, cent])
        return _l2(full if full is not None else cent)

    for tr in tracklets:
        start_rels, end_rels = body_reps[tr.tracklet_id]
        start_vecs = [fused_body(rel) for rel in start_rels]
        end_vecs = [fused_body(rel) for rel in end_rels]
        tr.body_start_samples = [np.asarray(v,dtype=np.float32) for v in start_vecs if v is not None]
        tr.body_end_samples = [np.asarray(v,dtype=np.float32) for v in end_vecs if v is not None]
        tr.body_start = _mean_normalized(tr.body_start_samples)
        tr.body_end = _mean_normalized(tr.body_end_samples)
        tr.n_body_start = len(tr.body_start_samples); tr.n_body_end = len(tr.body_end_samples)

    # Embed D3-aligned faces directly. No second face detector is run inside body crops.
    d3_face_paths: list[Path] = []
    for g in face_rep_rows.values():
        for rel in g.get("image_path", pd.Series(dtype=str)).fillna("").astype(str):
            if rel.strip():
                d3_face_paths.append((d3_dir / rel).resolve())
    d3_face_paths = list(dict.fromkeys(d3_face_paths))
    face_embedder = D3AlignedFaceEmbedder(
        str(cfg.get("face_model_name", "buffalo_l")),
        list(cfg.get("face_providers", ["CUDAExecutionProvider", "CPUExecutionProvider"])),
        int(cfg.get("target_face_det_size", 640)),
    )
    face_feats = face_embedder.extract_paths(
        d3_face_paths, batch_size=int(cfg.get("face_embedding_batch_size", 128))
    )

    face_target_output_dir.mkdir(parents=True, exist_ok=True)
    audit_rows: list[dict] = []
    total_samples = sum(len(g) for g in face_rep_rows.values())
    processed = 0

    for tr in tracklets:
        samples = face_rep_rows[tr.tracklet_id]
        embeddings: list[np.ndarray] = []
        assoc_scores: list[float] = []
        local_audit_indices: list[int] = []

        for _, r in samples.iterrows():
            processed += 1
            src_rel = str(r.image_path)
            src = (d3_dir / src_rel).resolve()
            emb = face_feats.get(str(src))
            face_rel = ""
            status = "ASSIGNED" if emb is not None else "EMBEDDING_FAILED"
            if emb is not None and src.is_file():
                out_dir = face_target_output_dir / tr.tracklet_id
                out_dir.mkdir(parents=True, exist_ok=True)
                dst = out_dir / f"{tr.tracklet_id}_F{int(r.frame_index):08d}.jpg"
                shutil.copy2(src, dst)
                face_rel = str(dst.relative_to(face_target_output_dir.parent))
                embeddings.append(np.asarray(emb, dtype=np.float32))
                assoc_scores.append(float(r.face_person_association_score))

            audit_rows.append({
                "class_id": tr.class_id,
                "session_id": tr.session_id,
                "tracklet_id": tr.tracklet_id,
                "track_id": tr.track_id,
                "frame_index": int(r.frame_index),
                "timestamp_sec": float(r.timestamp_sec),
                "body_image_path": "",
                "target_face_image_path": face_rel,
                "association_status": status,
                "n_faces_detected": np.nan,
                "n_plausible_faces": int(r.get("n_plausible_faces", 0)) if pd.notna(r.get("n_plausible_faces", np.nan)) else 0,
                "association_score": float(r.face_person_association_score),
                "association_runner_up_score": np.nan,
                "association_margin": r.face_person_association_margin,
                "det_score": r.get("det_score", np.nan),
                "face_center_x_rel": r.get("face_center_x_rel", np.nan),
                "face_center_y_rel": r.get("face_center_y_rel", np.nan),
                "face_person_ratio": np.nan,
                "d3_temporal_status": r.get("face_temporal_status", ""),
                "d3_temporal_similarity": r.get("face_temporal_similarity", np.nan),
                "d3_temporal_dominance": r.get("face_temporal_dominance", np.nan),
                "body_purity_status": r.get("body_purity_status", "UNKNOWN"),
                "body_purity_score": r.get("body_purity_score", np.nan),
                "face_purity_status": r.get("face_purity_status", "UNKNOWN"),
                "face_purity_score": r.get("face_purity_score", np.nan),
                "used_for_consensus": False,
                "consensus_similarity": np.nan,
            })
            if emb is not None:
                local_audit_indices.append(len(audit_rows) - 1)

            if processed % 250 == 0 or processed == total_samples:
                print(f"[D5 D3Face] {processed}/{total_samples} strict full-frame faces", flush=True)

        consensus, keep, reliability, status = robust_face_consensus(embeddings, assoc_scores, cfg)
        tr.face = consensus
        tr.n_face = len(keep)
        tr.face_reliability = reliability
        tr.face_association_status = status
        if keep:
            kept_rows_for_stats = samples.iloc[keep] if len(samples) else samples
            om = pd.to_numeric(kept_rows_for_stats.get("face_person_owner_margin", np.nan), errors="coerce")
            finite_om = om[np.isfinite(om)] if hasattr(om, "__len__") else []
            tr.face_owner_margin = float(finite_om.min()) if len(finite_om) else 1.0
            dom = pd.to_numeric(kept_rows_for_stats.get("face_temporal_dominance", np.nan), errors="coerce")
            finite_dom = dom[np.isfinite(dom)] if hasattr(dom, "__len__") else []
            tr.face_temporal_dominance = float(finite_dom.min()) if len(finite_dom) else 1.0

        if keep:
            kept_audit = []
            for local_i in keep:
                if local_i >= len(local_audit_indices):
                    continue
                ai = local_audit_indices[local_i]
                audit_rows[ai]["used_for_consensus"] = True
                if consensus is not None:
                    audit_rows[ai]["consensus_similarity"] = float(
                        np.dot(embeddings[local_i], consensus)
                    )
                kept_audit.append(ai)
            if kept_audit:
                best_ai = max(
                    kept_audit,
                    key=lambda ai: (
                        float(audit_rows[ai].get("consensus_similarity", -1.0)),
                        float(audit_rows[ai].get("association_score", -1.0)),
                    ),
                )
                tr.face_best_image_path = str(audit_rows[best_ai]["target_face_image_path"])

    return tracklets, pd.DataFrame(audit_rows, columns=FACE_TARGET_AUDIT_COLUMNS)


def _temporal_score(gap: float, tau: float) -> float:
    return float(math.exp(-max(0.0, gap) / max(1e-6, tau)))


def _fusion(face_sim: float | None, body_sim: float | None, spatial_score: float | None, tscore: float, cfg: dict) -> float:
    fw = float(cfg.get("face_weight", 0.50)) if face_sim is not None else 0.0
    bw = float(cfg.get("body_weight", 0.30)) if body_sim is not None else 0.0
    sw = float(cfg.get("spatial_weight", 0.15)) if spatial_score is not None else 0.0
    tw = float(cfg.get("time_weight", 0.05))
    total = fw + bw + sw + tw
    if total <= 0:
        return 0.0
    value = tw * tscore
    if face_sim is not None:
        value += fw * max(-1.0, min(1.0, face_sim))
    if body_sim is not None:
        value += bw * max(-1.0, min(1.0, body_sim))
    if spatial_score is not None:
        value += sw * max(0.0, min(1.0, spatial_score))
    return float(value / total)


def _face_pair_trusted(pre: TrackletEvidence, post: TrackletEvidence, cfg: dict) -> bool:
    min_rel = float(cfg.get("face_min_reliability_for_auto", 0.58))
    min_samples = max(1, int(cfg.get("face_min_samples_for_auto", 2)))
    return (
        pre.face is not None and post.face is not None
        and pre.face_reliability >= min_rel and post.face_reliability >= min_rel
        and pre.n_face >= min_samples and post.n_face >= min_samples
        and float(getattr(pre, "face_owner_margin", 1.0)) >= float(cfg.get("face_min_owner_margin_for_auto", 0.16))
        and float(getattr(post, "face_owner_margin", 1.0)) >= float(cfg.get("face_min_owner_margin_for_auto", 0.16))
        and float(getattr(pre, "face_temporal_dominance", 1.0)) >= float(cfg.get("face_min_temporal_dominance_for_auto", 0.80))
        and float(getattr(post, "face_temporal_dominance", 1.0)) >= float(cfg.get("face_min_temporal_dominance_for_auto", 0.80))
    )



def _body_pair_stats(pre: TrackletEvidence, post: TrackletEvidence, cfg: dict) -> tuple[float | None, float, float, float, int]:
    body_sim = cosine_similarity(pre.body_end, post.body_start) if pre.body_end is not None and post.body_start is not None else None
    a = [x for x in getattr(pre, "body_end_samples", []) if x is not None]
    b = [x for x in getattr(post, "body_start_samples", []) if x is not None]
    if not a or not b:
        v = float(body_sim) if body_sim is not None else float("nan")
        return body_sim, v, v, 1.0 if body_sim is not None else 0.0, 1 if body_sim is not None else 0
    sims = np.asarray([cosine_similarity(x, y) for x in a for y in b], dtype=np.float32)
    sims = sims[np.isfinite(sims)]
    if sims.size == 0:
        return body_sim, float("nan"), float("nan"), 0.0, 0
    support_thr = float(cfg.get("body_pair_support_similarity", 0.84))
    return body_sim, float(np.median(sims)), float(np.min(sims)), float(np.mean(sims >= support_thr)), int(sims.size)


def _body_auto_consensus_ok(median: float, min_sim: float, support: float, count: int, cfg: dict, *, same_track: bool = False) -> bool:
    min_count = int(cfg.get("body_pair_min_count_for_auto", 4))
    med_key = "same_track_body_pair_median_min" if same_track else "body_pair_median_min_for_auto"
    med_thr = float(cfg.get(med_key, 0.92))
    min_thr = float(cfg.get("body_pair_min_similarity_for_auto", 0.76))
    support_thr = float(cfg.get("body_pair_min_support_for_auto", 0.80))
    if count < min_count:
        return bool(cfg.get("allow_aggregate_body_auto_without_samples", True)) and count > 0 and np.isfinite(median) and median >= med_thr
    return bool(np.isfinite(median) and median >= med_thr and np.isfinite(min_sim) and min_sim >= min_thr and support >= support_thr)

def _pair_evidence(pre: TrackletEvidence, post: TrackletEvidence, cfg: dict) -> dict | None:
    gap = float(post.start_sec - pre.end_sec)
    if gap < float(cfg.get("min_gap_sec", 0.5)) or gap > float(cfg.get("max_gap_sec", 120.0)):
        return None

    face_sim = cosine_similarity(pre.face, post.face) if pre.face is not None and post.face is not None else None
    body_sim, body_pair_median, body_pair_min, body_pair_support, body_pair_count = _body_pair_stats(pre, post, cfg)
    tscore = _temporal_score(gap, float(cfg.get("temporal_tau_sec", 45.0)))
    spatial_distance, spatial_scale_log, spatial_score = _spatial_pair_stats(pre, post, gap, cfg)
    fusion = _fusion(face_sim, body_sim, spatial_score, tscore, cfg)
    face_trusted = _face_pair_trusted(pre, post, cfg)
    body_purity_pair_ok = _body_purity_auto_ok(pre, post, cfg)

    pre_sid, post_sid = _clean_sid(pre.student_id), _clean_sid(post.student_id)
    reason = ""
    decision = "REJECT"
    label = "UNREVIEWED"
    review_status = "NOT_RETAINED"

    face_reject = float(cfg.get("face_reject_below", 0.20))
    face_auto = float(cfg.get("face_auto_same", 0.55))
    face_strong = float(cfg.get("face_strong_same", 0.62))
    body_auto = float(cfg.get("body_auto_same", 0.90))
    body_review = float(cfg.get("body_review_min", 0.76))
    face_review = float(cfg.get("face_review_min", 0.35))
    manual_min = float(cfg.get("manual_min_fusion", 0.52))
    max_body_gap = float(cfg.get("max_body_only_gap_sec", 12.0))
    # AUTO_SAME is a safety-critical decision. A face match is never sufficient on its own:
    # wrong face↔body ownership can still occur in crowded frames, so automatic merge requires
    # independent body ReID corroboration as a second modality.
    face_auto_body_min = float(cfg.get("face_auto_body_min", 0.84))
    face_body_conflict_below = float(cfg.get("face_body_conflict_below", 0.65))
    face_auto_max_gap = float(cfg.get("face_auto_max_gap_sec", 30.0))
    auto_fusion_min = float(cfg.get("auto_fusion_min", 0.68))
    body_only_auto = float(cfg.get("body_only_auto_same", max(body_auto, 0.95)))
    spatial_auto_min = float(cfg.get("spatial_auto_min", 0.45))
    spatial_body_only_min = float(cfg.get("spatial_body_only_auto_min", 0.72))
    spatial_short_gap_max_dist = float(cfg.get("spatial_short_gap_max_body_heights", 3.5))
    spatial_short_gap_sec = float(cfg.get("spatial_short_gap_sec", 5.0))
    spatial_contradiction = bool(np.isfinite(spatial_distance) and gap <= spatial_short_gap_sec and spatial_distance > spatial_short_gap_max_dist)

    if pre_sid and post_sid:
        if pre_sid == post_sid:
            decision, label, review_status, reason = (
                "AUTO_SAME", "SAME", "AUTO_FROM_REVIEWED_IDENTITY_MAP", "KNOWN_IDENTITY_SAME"
            )
        else:
            decision, label, review_status, reason = (
                "AUTO_DIFFERENT", "DIFFERENT", "AUTO_FROM_REVIEWED_IDENTITY_MAP", "KNOWN_IDENTITY_DIFFERENT"
            )
    elif int(pre.track_id) == int(post.track_id):
        # IMPORTANT: tracker IDs are continuity hints, not identity ground truth. A tracker can
        # ID-switch/reuse the same numeric ID after occlusion or crowding. Therefore SAME_TRACK
        # is allowed to AUTO_SAME only when independent ReID corroborates it.
        same_track_max_gap = float(cfg.get("same_track_max_auto_gap_sec", 3.0))
        same_track_body_auto = float(cfg.get("same_track_body_auto_same", 0.92))
        same_track_body_auto_no_face = float(cfg.get("same_track_body_auto_same_no_face", 0.95))
        same_track_body_reject = float(cfg.get("same_track_body_reject_below", 0.60))
        same_track_face_auto = float(cfg.get("same_track_face_auto_same", face_auto))

        if face_trusted and face_sim is not None and face_sim < face_reject:
            decision, label, review_status, reason = (
                "NEEDS_REVIEW", "UNREVIEWED", "NEEDS_MANUAL_REVIEW", "SAME_TRACK_FACE_CONFLICT"
            )
        elif body_sim is None:
            decision, label, review_status, reason = (
                "NEEDS_REVIEW", "UNREVIEWED", "NEEDS_MANUAL_REVIEW", "SAME_TRACK_NO_BODY_REID"
            )
        elif body_sim < same_track_body_reject:
            decision, label, review_status, reason = (
                "NEEDS_REVIEW", "UNREVIEWED", "NEEDS_MANUAL_REVIEW", "SAME_TRACK_BODY_CONFLICT"
            )
        elif gap > same_track_max_gap:
            decision, label, review_status, reason = (
                "NEEDS_REVIEW", "UNREVIEWED", "NEEDS_MANUAL_REVIEW", "SAME_TRACK_GAP_TOO_LONG"
            )
        elif spatial_contradiction:
            decision, label, review_status, reason = (
                "NEEDS_REVIEW", "UNREVIEWED", "NEEDS_MANUAL_REVIEW", "SPATIAL_CONTRADICTION"
            )
        elif not body_purity_pair_ok:
            decision, label, review_status, reason = (
                "NEEDS_REVIEW", "UNREVIEWED", "NEEDS_MANUAL_REVIEW", "BODY_PURITY_INSUFFICIENT"
            )
        elif face_trusted:
            if (face_sim is not None and face_sim >= same_track_face_auto and body_sim >= same_track_body_auto
                and _body_auto_consensus_ok(body_pair_median, body_pair_min, body_pair_support, body_pair_count, cfg, same_track=True)):
                decision, label, review_status, reason = (
                    "AUTO_SAME", "SAME", "AUTO_HIGH_CONFIDENCE", "SAME_TRACK_FACE_BODY_CORROBORATED"
                )
            else:
                decision, label, review_status, reason = (
                    "NEEDS_REVIEW", "UNREVIEWED", "NEEDS_MANUAL_REVIEW",
                    "SAME_TRACK_INSUFFICIENT_FACE_BODY_CORROBORATION"
                )
        elif (body_sim >= same_track_body_auto_no_face
              and _body_auto_consensus_ok(body_pair_median, body_pair_min, body_pair_support, body_pair_count, cfg, same_track=True)):
            decision, label, review_status, reason = (
                "AUTO_SAME", "SAME", "AUTO_HIGH_CONFIDENCE", "SAME_TRACK_BODY_CORROBORATED"
            )
        else:
            decision, label, review_status, reason = (
                "NEEDS_REVIEW", "UNREVIEWED", "NEEDS_MANUAL_REVIEW",
                "SAME_TRACK_INSUFFICIENT_BODY_CORROBORATION"
            )
    else:
        if spatial_contradiction:
            decision, label, review_status, reason = (
                "NEEDS_REVIEW", "UNREVIEWED", "NEEDS_MANUAL_REVIEW", "SPATIAL_CONTRADICTION"
            )
        elif face_trusted and face_sim is not None and face_sim < face_reject:
            reason = "FACE_CONTRADICTION"
        elif face_trusted and body_sim is not None and body_sim < face_body_conflict_below:
            # A high face score with body disagreement is exactly the signature of a possible
            # wrong face↔body assignment. Never auto-merge it; keep it visible for QC.
            decision, label, review_status, reason = (
                "NEEDS_REVIEW", "UNREVIEWED", "NEEDS_MANUAL_REVIEW", "FACE_BODY_CONFLICT"
            )
        elif face_trusted and face_sim is not None and (
            face_sim >= face_strong
            or (face_sim >= face_auto and body_sim is not None and body_sim >= face_auto_body_min)
        ):
            auto_body_ready = (
                body_sim is not None
                and body_sim >= face_auto_body_min
                and gap <= face_auto_max_gap
                and fusion >= auto_fusion_min
                and not spatial_contradiction
                and spatial_score >= spatial_auto_min
                and _body_auto_consensus_ok(body_pair_median, body_pair_min, body_pair_support, body_pair_count, cfg, same_track=False)
            )
            if auto_body_ready and body_purity_pair_ok:
                decision, label, review_status, reason = (
                    "AUTO_SAME", "SAME", "AUTO_HIGH_CONFIDENCE", "FACE_BODY_CROSS_MODAL_STRONG"
                )
            elif auto_body_ready and not body_purity_pair_ok:
                decision, label, review_status, reason = (
                    "NEEDS_REVIEW", "UNREVIEWED", "NEEDS_MANUAL_REVIEW", "BODY_PURITY_INSUFFICIENT"
                )
            else:
                decision, label, review_status, reason = (
                    "NEEDS_REVIEW", "UNREVIEWED", "NEEDS_MANUAL_REVIEW",
                    "FACE_STRONG_BODY_NOT_CORROBORATED"
                )
        elif (face_sim is None and body_sim is not None and body_sim >= body_only_auto and gap <= max_body_gap
              and not spatial_contradiction and spatial_score >= spatial_body_only_min
              and _body_auto_consensus_ok(body_pair_median, body_pair_min, body_pair_support, body_pair_count, cfg, same_track=False)):
            if body_purity_pair_ok:
                decision, label, review_status, reason = (
                    "AUTO_SAME", "SAME", "AUTO_HIGH_CONFIDENCE", "BODY_REID_VERY_STRONG_SHORT_GAP"
                )
            else:
                decision, label, review_status, reason = (
                    "NEEDS_REVIEW", "UNREVIEWED", "NEEDS_MANUAL_REVIEW", "BODY_PURITY_INSUFFICIENT"
                )
        elif face_sim is not None and not face_trusted:
            # Weak/single target-face evidence may support manual review, but can never auto-merge.
            if body_sim is not None and body_sim >= body_review and fusion >= manual_min:
                decision, label, review_status, reason = (
                    "NEEDS_REVIEW", "UNREVIEWED", "NEEDS_MANUAL_REVIEW", "FACE_EVIDENCE_NOT_TRUSTED_FOR_AUTO"
                )
            else:
                reason = "INSUFFICIENT_TRUSTED_IDENTITY_EVIDENCE"
        elif (
            (face_sim is not None and face_sim >= face_review and fusion >= manual_min)
            or (
                face_sim is None
                and body_sim is not None
                and body_sim >= body_review
                and gap <= float(cfg.get("max_manual_body_gap_sec", 60.0))
            )
        ):
            decision, label, review_status, reason = (
                "NEEDS_REVIEW", "UNREVIEWED", "NEEDS_MANUAL_REVIEW", "AMBIGUOUS_FACE_REID"
            )
        else:
            reason = "INSUFFICIENT_IDENTITY_EVIDENCE"

    # Optional session-scoped QC calibration. This is deliberately applied only after the base
    # resolver has chosen NEEDS_REVIEW; it never weakens contradiction/identity hard guards.
    if decision == "NEEDS_REVIEW":
        calibrated_reason = _calibrated_promotion_reason(
            pre, post, cfg, original_reason=reason, gap=gap, face_sim=face_sim, body_sim=body_sim,
            body_pair_median=body_pair_median, body_pair_min=body_pair_min,
            body_pair_support=body_pair_support, body_pair_count=body_pair_count, fusion=fusion,
            spatial_score=spatial_score, face_trusted=face_trusted,
            body_purity_pair_ok=body_purity_pair_ok, spatial_contradiction=spatial_contradiction,
        )
        if calibrated_reason is not None:
            decision, label, review_status, reason = (
                "AUTO_SAME", "SAME", "AUTO_HIGH_CONFIDENCE", calibrated_reason
            )

    return {
        "class_id": pre.class_id,
        "session_id": pre.session_id,
        "pre_tracklet": pre.tracklet_id,
        "post_tracklet": post.tracklet_id,
        "pre_track_id": int(pre.track_id),
        "post_track_id": int(post.track_id),
        "pre_student_id": pre_sid,
        "post_student_id": post_sid,
        "gap_sec": gap,
        "face_similarity": np.nan if face_sim is None else float(face_sim),
        "body_similarity": np.nan if body_sim is None else float(body_sim),
        "appearance_similarity": np.nan if body_sim is None else float(body_sim),
        "body_pair_median": body_pair_median,
        "body_pair_min": body_pair_min,
        "body_pair_support": body_pair_support,
        "body_pair_count": body_pair_count,
        "temporal_score": tscore,
        "fusion_score": fusion,
        "pre_face_reliability": float(pre.face_reliability),
        "post_face_reliability": float(post.face_reliability),
        "pre_face_samples": int(pre.n_face),
        "post_face_samples": int(post.n_face),
        "face_pair_trusted": bool(face_trusted),
        "pre_body_clean_ratio": float(pre.body_clean_ratio),
        "post_body_clean_ratio": float(post.body_clean_ratio),
        "pre_body_end_clean_ratio": float(pre.body_end_clean_ratio),
        "post_body_start_clean_ratio": float(post.body_start_clean_ratio),
        "pre_body_end_purity": float(pre.body_end_purity),
        "post_body_start_purity": float(post.body_start_purity),
        "pre_body_end_usable_samples": int(pre.body_end_usable_samples),
        "post_body_start_usable_samples": int(post.body_start_usable_samples),
        "pre_body_contaminated_samples": int(pre.body_contaminated_samples),
        "post_body_contaminated_samples": int(post.body_contaminated_samples),
        "body_purity_pair_ok": bool(body_purity_pair_ok),
        "spatial_distance": spatial_distance,
        "spatial_scale_log": spatial_scale_log,
        "spatial_score": spatial_score,
        "label": label,
        "decision": decision,
        "review_status": review_status,
        "decision_reason": reason,
    }


def build_reentry_candidates(tracklets: list[TrackletEvidence] | pd.DataFrame, cfg: dict) -> pd.DataFrame:
    """Generate a small, high-value re-entry set, ranked per post tracklet."""
    if isinstance(tracklets, pd.DataFrame):
        items = []
        for _, r in tracklets.iterrows():
            items.append(TrackletEvidence(
                class_id=str(r.class_id), session_id=str(r.session_id), tracklet_id=str(r.tracklet_id),
                track_id=int(r.track_id), student_id=_clean_sid(r.get("student_id", "")),
                start_sec=float(r.start_sec), end_sec=float(r.end_sec),
                start_frame=int(r.get("start_frame", 0)), end_frame=int(r.get("end_frame", 0)),
                body_start=r.get("body_start", r.get("start_hist", None)),
                body_end=r.get("body_end", r.get("end_hist", None)),
                face=r.get("face", None),
                n_face=int(r.get("n_face", 3 if r.get("face", None) is not None else 0)),
                face_reliability=float(r.get("face_reliability", 1.0 if r.get("face", None) is not None else 0.0)),
            ))
        tracklets = items
    if not tracklets:
        return pd.DataFrame(columns=CANDIDATE_COLUMNS)

    max_gap = float(cfg.get("max_gap_sec", 120.0))
    min_gap = float(cfg.get("min_gap_sec", 0.5))
    top_k = max(1, int(cfg.get("top_k_per_post_tracklet", 2)))
    ambiguity_margin = float(cfg.get("auto_same_margin", 0.05))

    by_session: dict[tuple[str, str], list[TrackletEvidence]] = {}
    for tr in tracklets:
        by_session.setdefault((tr.class_id, tr.session_id), []).append(tr)

    retained: list[dict] = []
    for _, sess_items in by_session.items():
        end_sorted = sorted(sess_items, key=lambda x: x.end_sec)
        end_times = [x.end_sec for x in end_sorted]
        for post in sorted(sess_items, key=lambda x: x.start_sec):
            lo = bisect_left(end_times, post.start_sec - max_gap)
            hi = bisect_right(end_times, post.start_sec - min_gap)
            evidences = []
            for pre in end_sorted[lo:hi]:
                if pre.tracklet_id == post.tracklet_id:
                    continue
                ev = _pair_evidence(pre, post, cfg)
                if ev is None or ev["decision"] == "REJECT":
                    continue
                evidences.append(ev)
            if not evidences:
                continue
            evidences.sort(key=lambda r: (r["fusion_score"], -r["gap_sec"]), reverse=True)

            if len(evidences) > 1:
                top, second = evidences[0], evidences[1]
                explicit = top["decision_reason"] in {"KNOWN_IDENTITY_SAME"}
                if (
                    top["decision"] == "AUTO_SAME"
                    and not explicit
                    and float(top["fusion_score"] - second["fusion_score"]) < ambiguity_margin
                ):
                    top["decision"] = "NEEDS_REVIEW"
                    top["label"] = "UNREVIEWED"
                    top["review_status"] = "NEEDS_MANUAL_REVIEW"
                    top["decision_reason"] = "AMBIGUOUS_TOP2_MARGIN"

            grounded = [e for e in evidences if e["review_status"] == "AUTO_FROM_REVIEWED_IDENTITY_MAP"]
            heuristic = [e for e in evidences if e["review_status"] != "AUTO_FROM_REVIEWED_IDENTITY_MAP"][:top_k]
            retained.extend(grounded)
            retained.extend(heuristic)

    out = pd.DataFrame(retained, columns=CANDIDATE_COLUMNS)
    if out.empty:
        return out
    out = out.drop_duplicates(["class_id", "session_id", "pre_tracklet", "post_tracklet"], keep="first")
    out = out.sort_values(
        ["class_id", "session_id", "post_tracklet", "fusion_score", "gap_sec"],
        ascending=[True, True, True, False, True],
    ).reset_index(drop=True)
    return out


class _UnionFind:
    def __init__(
        self,
        items: list[TrackletEvidence],
        cfg: dict,
        cannot_link: set[frozenset[str]] | None = None,
    ):
        self.items = {x.tracklet_id: x for x in items}
        self.parent = {x.tracklet_id: x.tracklet_id for x in items}
        self.members = {x.tracklet_id: {x.tracklet_id} for x in items}
        self.cfg = cfg
        self.tol = float(cfg.get("overlap_tolerance_sec", 0.15))
        self.cannot_link = cannot_link or set()
        self.last_conflict_reason = ""

    def find(self, x: str) -> str:
        p = self.parent[x]
        if p != x:
            self.parent[x] = self.find(p)
        return self.parent[x]

    def _cluster_face(self, ids: set[str]) -> tuple[np.ndarray | None, int]:
        min_rel = float(self.cfg.get("cluster_face_min_reliability", 0.55))
        xs = [self.items[x].face for x in ids if self.items[x].face is not None and self.items[x].face_reliability >= min_rel]
        return _mean_normalized(xs), len(xs)

    def _cluster_body(self, ids: set[str]) -> np.ndarray | None:
        xs = []
        for x in ids:
            tr = self.items[x]
            if tr.body_start is not None:
                xs.append(tr.body_start)
            if tr.body_end is not None:
                xs.append(tr.body_end)
        return _mean_normalized(xs)

    def _hard_compatible(self, ra: str, rb: str) -> bool:
        """Check constraints that even a human SAME decision must not silently violate."""
        self.last_conflict_reason = ""
        a_ids, b_ids = self.members[ra], self.members[rb]

        # Human/grounded DIFFERENT decisions are hard cannot-link constraints. This also
        # blocks a transitive A-C-B merge from undoing a reviewed A!=B decision.
        for ai in a_ids:
            for bi in b_ids:
                if frozenset((ai, bi)) in self.cannot_link:
                    self.last_conflict_reason = "CLUSTER_DIFFERENT_CONSTRAINT"
                    return False

        a_sid = {_clean_sid(self.items[x].student_id) for x in a_ids} - {""}
        b_sid = {_clean_sid(self.items[x].student_id) for x in b_ids} - {""}
        if a_sid and b_sid and a_sid != b_sid:
            self.last_conflict_reason = "CLUSTER_STUDENT_ID_CONFLICT"
            return False
        return True

    def _compatible(self, ra: str, rb: str) -> bool:
        if not self._hard_compatible(ra, rb):
            return False
        a_ids, b_ids = self.members[ra], self.members[rb]

        for ai in a_ids:
            a = self.items[ai]
            for bi in b_ids:
                b = self.items[bi]
                overlap = min(a.end_sec, b.end_sec) - max(a.start_sec, b.start_sec)
                if overlap > self.tol:
                    self.last_conflict_reason = "CLUSTER_TEMPORAL_OVERLAP"
                    return False

        af, an = self._cluster_face(a_ids)
        bf, bn = self._cluster_face(b_ids)
        if af is not None and bf is not None:
            fs = cosine_similarity(af, bf)
            if fs < float(self.cfg.get("cluster_face_reject_below", 0.28)):
                self.last_conflict_reason = "CLUSTER_FACE_CONTRADICTION"
                return False
            if an >= 2 and bn >= 2 and fs < float(self.cfg.get("cluster_face_min_similarity", 0.38)):
                self.last_conflict_reason = "CLUSTER_FACE_WEAK_INCONSISTENCY"
                return False

        ab, bb = self._cluster_body(a_ids), self._cluster_body(b_ids)
        if ab is not None and bb is not None:
            bs = cosine_similarity(ab, bb)
            if bs < float(self.cfg.get("cluster_body_reject_below", 0.45)):
                self.last_conflict_reason = "CLUSTER_BODY_CONTRADICTION"
                return False

        return True

    def _merge_roots(self, ra: str, rb: str) -> bool:
        keep, drop = sorted([ra, rb])[0], sorted([ra, rb])[1]
        self.parent[drop] = keep
        self.members[keep] |= self.members[drop]
        del self.members[drop]
        return True

    def union(self, a: str, b: str) -> bool:
        ra, rb = self.find(a), self.find(b)
        if ra == rb:
            return True
        if not self._compatible(ra, rb):
            return False
        return self._merge_roots(ra, rb)

    def union_reviewed_same(self, a: str, b: str) -> bool:
        """Apply an explicit human SAME decision as a must-link.

        Human review overrides heuristic face/body/temporal disagreement, because those are exactly
        the signals being corrected by QC. It still cannot override an explicit DIFFERENT constraint
        or conflicting known student IDs; those are hard contradictions and must be surfaced.
        """
        ra, rb = self.find(a), self.find(b)
        if ra == rb:
            return True
        if not self._hard_compatible(ra, rb):
            return False
        return self._merge_roots(ra, rb)


def build_stable_person_map(
    tracklets: list[TrackletEvidence], candidates: pd.DataFrame, cfg: dict,
    human_constraints: dict[tuple[str, str], str] | None = None,
) -> tuple[pd.DataFrame, pd.DataFrame]:
    c = candidates.copy()
    cannot_link: set[frozenset[str]] = set()
    human_constraints = human_constraints or {}
    valid_tracklet_ids = {x.tracklet_id for x in tracklets}
    for (a, b), label in human_constraints.items():
        if label == "DIFFERENT" and a in valid_tracklet_ids and b in valid_tracklet_ids:
            cannot_link.add(frozenset((a, b)))
    if not c.empty:
        grounded_different = (
            (c["label"].astype(str).str.upper() == "DIFFERENT")
            & c["review_status"].astype(str).str.upper().isin({
                "REVIEWED", "CONFIRMED", "APPROVED", "AUTO_FROM_REVIEWED_IDENTITY_MAP"
            })
        )
        for r in c[grounded_different].itertuples(index=False):
            cannot_link.add(frozenset((str(r.pre_tracklet), str(r.post_tracklet))))
    uf = _UnionFind(tracklets, cfg, cannot_link=cannot_link)
    if not c.empty:
        # Apply explicit human SAME first. These are must-link constraints and should not be silently
        # lost because a newer heuristic became more conservative. Human SAME can override heuristic
        # face/body/temporal disagreement, but never a human DIFFERENT or known Student-ID conflict.
        reviewed_same_mask = (
            (c["label"].astype(str) == "SAME")
            & c["review_status"].astype(str).isin({"REVIEWED", "CONFIRMED", "APPROVED"})
        )
        for idx, row in c[reviewed_same_mask].sort_values("fusion_score", ascending=False).iterrows():
            ok = uf.union_reviewed_same(str(row.pre_tracklet), str(row.post_tracklet))
            if not ok:
                raise RuntimeError(
                    "Human SAME constraint conflicts with a hard identity constraint for "
                    f"{row.pre_tracklet}->{row.post_tracklet}: "
                    f"{uf.last_conflict_reason or 'UNKNOWN_HARD_CONFLICT'}"
                )

        auto_same_mask = (
            (c["label"].astype(str) == "SAME")
            & c["review_status"].astype(str).isin({
                "AUTO_FROM_REVIEWED_IDENTITY_MAP", "AUTO_TRACK_CONTINUITY", "AUTO_HIGH_CONFIDENCE",
            })
        )
        for idx, row in c[auto_same_mask].sort_values("fusion_score", ascending=False).iterrows():
            ok = uf.union(str(row.pre_tracklet), str(row.post_tracklet))
            if not ok:
                c.at[idx, "decision"] = "NEEDS_REVIEW"
                c.at[idx, "label"] = "UNREVIEWED"
                c.at[idx, "review_status"] = "NEEDS_MANUAL_REVIEW"
                c.at[idx, "decision_reason"] = uf.last_conflict_reason or "CLUSTER_IDENTITY_CONFLICT"

    groups: dict[str, list[TrackletEvidence]] = {}
    for tr in tracklets:
        groups.setdefault(uf.find(tr.tracklet_id), []).append(tr)
    ordered = sorted(groups.values(), key=lambda g: (min(x.start_sec for x in g), min(x.tracklet_id for x in g)))

    rows = []
    for i, group in enumerate(ordered, start=1):
        spid = f"SP{i:05d}"
        sids = sorted({_clean_sid(x.student_id) for x in group} - {""})
        sid = sids[0] if len(sids) == 1 else ""
        group_ids = {x.tracklet_id for x in group}
        related = c[
            (c["label"].astype(str) == "SAME")
            & c["pre_tracklet"].astype(str).isin(group_ids)
            & c["post_tracklet"].astype(str).isin(group_ids)
        ] if not c.empty else pd.DataFrame()
        confidence = float(related["fusion_score"].mean()) if not related.empty else (1.0 if sid else 0.0)
        status = "IDENTIFIED" if sid else ("AUTO_RESOLVED" if len(group) > 1 else "SINGLETON")
        for tr in sorted(group, key=lambda x: x.start_sec):
            rows.append({
                "class_id": tr.class_id,
                "session_id": tr.session_id,
                "tracklet_id": tr.tracklet_id,
                "track_id": tr.track_id,
                "stable_person_id": spid,
                "student_id": sid,
                "start_sec": tr.start_sec,
                "end_sec": tr.end_sec,
                "resolution_status": status,
                "resolution_confidence": confidence,
                "face_association_status": tr.face_association_status,
                "face_reliability": tr.face_reliability,
                "face_samples": tr.n_face,
                "face_best_image_path": tr.face_best_image_path,
            })
    return pd.DataFrame(rows), c


def _apply_preserved_reviews(new_candidates: pd.DataFrame, old_path: Path) -> pd.DataFrame:
    """Preserve manually-reviewed rows that did NOT originate from the external QC CSV.

    HUMAN_QC_IMPORT decisions are deliberately excluded here because
    work/d5_manual_reviews/.../manual_reviews.csv is their source of truth. This prevents stale
    SAME decisions from an older QC export from surviving after the user replaces that review
    file with a newer QC pass.
    """
    if new_candidates.empty or not old_path.is_file():
        return new_candidates
    try:
        old = pd.read_csv(old_path, dtype=str, low_memory=False).fillna("")
    except Exception:
        return new_candidates
    if old.empty or not {"pre_tracklet", "post_tracklet", "review_status", "label"}.issubset(old.columns):
        return new_candidates
    reviewed = old[old["review_status"].astype(str).str.upper().isin({"REVIEWED", "CONFIRMED", "APPROVED"})].copy()
    if "decision_reason" in reviewed.columns:
        reviewed = reviewed[reviewed["decision_reason"].astype(str).str.upper().ne("HUMAN_QC_IMPORT")]
    if reviewed.empty:
        return new_candidates
    decisions = {
        (str(r.pre_tracklet), str(r.post_tracklet)): (
            str(r.label).upper(), str(r.review_status), str(getattr(r, "decision_reason", "MANUAL_REVIEW"))
        ) for r in reviewed.itertuples(index=False)
    }
    out = new_candidates.copy()
    for idx, row in out.iterrows():
        key = (str(row.pre_tracklet), str(row.post_tracklet))
        if key not in decisions:
            continue
        label, status, reason = decisions[key]
        if label in {"SAME", "DIFFERENT"}:
            out.at[idx, "label"] = label
            out.at[idx, "decision"] = "MANUAL_SAME" if label == "SAME" else "MANUAL_DIFFERENT"
            out.at[idx, "review_status"] = status
            out.at[idx, "decision_reason"] = reason or "PRESERVED_MANUAL_REVIEW"
    return out


def _load_human_qc_constraints(review_path: Path) -> tuple[dict[tuple[str, str], str], dict]:
    """Load external QC as authoritative SAME/DIFFERENT constraints.

    Blank/UNSURE rows are intentionally ignored. Contradictory duplicate decisions for the same
    pair are rejected instead of silently taking the last row.
    """
    stats = {
        "review_file": str(review_path), "valid_reviews": 0,
        "same_constraints": 0, "different_constraints": 0,
    }
    if not review_path.is_file():
        return {}, stats
    try:
        q = pd.read_csv(review_path, dtype=str, low_memory=False).fillna("")
    except Exception:
        return {}, stats
    need = {"pre_tracklet", "post_tracklet", "human_qc"}
    if q.empty or not need.issubset(q.columns):
        return {}, stats
    q["human_qc"] = q["human_qc"].astype(str).str.strip().str.upper()
    q = q[q["human_qc"].isin({"SAME", "DIFFERENT"})].copy()
    if q.empty:
        return {}, stats

    conflicts = (
        q.groupby(["pre_tracklet", "post_tracklet"])["human_qc"]
        .nunique()
    )
    conflicts = conflicts[conflicts > 1]
    if not conflicts.empty:
        pairs = [f"{a}->{b}" for a, b in conflicts.index.tolist()[:10]]
        raise RuntimeError("Conflicting human QC decisions for pair(s): " + ", ".join(pairs))

    q = q.drop_duplicates(["pre_tracklet", "post_tracklet"], keep="last")
    decisions = {
        (str(r.pre_tracklet), str(r.post_tracklet)): str(r.human_qc)
        for r in q.itertuples(index=False)
    }
    stats["valid_reviews"] = len(decisions)
    stats["same_constraints"] = sum(v == "SAME" for v in decisions.values())
    stats["different_constraints"] = sum(v == "DIFFERENT" for v in decisions.values())
    return decisions, stats

def _inject_missing_human_qc_pairs(
    tracklets: list[TrackletEvidence],
    candidates: pd.DataFrame,
    human_constraints: dict[tuple[str, str], str],
    cfg: dict,
) -> tuple[pd.DataFrame, int]:
    """Ensure reviewed SAME/DIFFERENT pairs remain auditable after heuristic rules change.

    Human QC may have been produced from an earlier candidate set. A safer future rule (for example,
    SAME_TRACK requiring independent ReID) can cause a previously reviewed pair to be REJECTed before
    top-k retention. We must not silently lose an explicit human SAME/DIFFERENT decision. Missing
    reviewed pairs are therefore re-injected as evidence rows and are overridden to MANUAL_* by
    `_apply_human_qc_reviews` immediately afterwards.

    This does not infer new labels. It only preserves pairs that already exist in the external human-QC
    source of truth.
    """
    if not human_constraints:
        return candidates, 0

    by_id = {x.tracklet_id: x for x in tracklets}
    existing: set[tuple[str, str]] = set()
    if not candidates.empty and {"pre_tracklet", "post_tracklet"}.issubset(candidates.columns):
        existing = set(zip(candidates["pre_tracklet"].astype(str), candidates["post_tracklet"].astype(str)))

    added: list[dict] = []
    # Widen only the evidence-row construction window. The row will become MANUAL_SAME/DIFFERENT;
    # automatic thresholds are not being relaxed.
    review_cfg = dict(cfg)
    review_cfg["min_gap_sec"] = -1.0e12
    review_cfg["max_gap_sec"] = 1.0e12

    for (pre_id, post_id), label in human_constraints.items():
        key = (str(pre_id), str(post_id))
        if key in existing or label not in {"SAME", "DIFFERENT"}:
            continue
        pre = by_id.get(key[0])
        post = by_id.get(key[1])
        if pre is None or post is None:
            continue
        if pre.class_id != post.class_id or pre.session_id != post.session_id:
            continue
        row = _pair_evidence(pre, post, review_cfg)
        if row is None:
            continue
        added.append(row)
        existing.add(key)

    if not added:
        return candidates, 0
    extra = pd.DataFrame(added, columns=CANDIDATE_COLUMNS)
    out = pd.concat([candidates, extra], ignore_index=True, sort=False) if not candidates.empty else extra
    out = out.drop_duplicates(["class_id", "session_id", "pre_tracklet", "post_tracklet"], keep="first")
    out = out.sort_values(
        ["class_id", "session_id", "post_tracklet", "fusion_score", "gap_sec"],
        ascending=[True, True, True, False, True],
    ).reset_index(drop=True)
    return out, len(added)


def _apply_human_qc_reviews(new_candidates: pd.DataFrame, review_path: Path) -> tuple[pd.DataFrame, dict]:
    decisions, base_stats = _load_human_qc_constraints(review_path)
    stats = {**base_stats, "applied": 0, "unmatched": 0}
    if new_candidates.empty or not decisions:
        stats["unmatched"] = len(decisions)
        return new_candidates, stats
    out = new_candidates.copy()
    seen = set()
    for idx, row in out.iterrows():
        key = (str(row.pre_tracklet), str(row.post_tracklet))
        label = decisions.get(key)
        if label is None:
            continue
        seen.add(key)
        out.at[idx, "label"] = label
        out.at[idx, "decision"] = "MANUAL_SAME" if label == "SAME" else "MANUAL_DIFFERENT"
        out.at[idx, "review_status"] = "REVIEWED"
        out.at[idx, "decision_reason"] = "HUMAN_QC_IMPORT"
    stats["applied"] = len(seen)
    stats["unmatched"] = max(0, len(decisions) - len(seen))
    return out, stats

def _save_embeddings(path: Path, tracklets: list[TrackletEvidence]) -> None:
    n = len(tracklets)
    body_dim = next((len(x.body_start) for x in tracklets if x.body_start is not None), 512)
    face_dim = next((len(x.face) for x in tracklets if x.face is not None), 512)
    body_start = np.zeros((n, body_dim), dtype=np.float32)
    body_end = np.zeros((n, body_dim), dtype=np.float32)
    face = np.zeros((n, face_dim), dtype=np.float32)
    body_start_valid = np.zeros(n, dtype=np.uint8)
    body_end_valid = np.zeros(n, dtype=np.uint8)
    face_valid = np.zeros(n, dtype=np.uint8)
    face_reliability = np.zeros(n, dtype=np.float32)
    face_samples = np.zeros(n, dtype=np.int32)
    for i, tr in enumerate(tracklets):
        if tr.body_start is not None:
            body_start[i, :len(tr.body_start)] = tr.body_start
            body_start_valid[i] = 1
        if tr.body_end is not None:
            body_end[i, :len(tr.body_end)] = tr.body_end
            body_end_valid[i] = 1
        if tr.face is not None:
            face[i, :len(tr.face)] = tr.face
            face_valid[i] = 1
        face_reliability[i] = float(tr.face_reliability)
        face_samples[i] = int(tr.n_face)
    np.savez_compressed(
        path,
        tracklet_id=np.asarray([x.tracklet_id for x in tracklets], dtype="U64"),
        body_start=body_start,
        body_end=body_end,
        face=face,
        body_start_valid=body_start_valid,
        body_end_valid=body_end_valid,
        face_valid=face_valid,
        face_reliability=face_reliability,
        face_samples=face_samples,
    )


def build_d5(
    d3_dir: str | Path,
    d4_dir: str | Path,
    output_root: str | Path,
    cfg: dict,
    project_root: str | Path | None = None,
) -> Path:
    d3_dir, d4_dir = Path(d3_dir), Path(d4_dir)
    root = Path(project_root).resolve() if project_root else Path.cwd().resolve()

    # Build everything in a temporary sibling; replace current D5 only after all checks/writes pass.
    body_meta = pd.read_csv(d4_dir / "metadata.csv", nrows=1)
    if body_meta.empty:
        raise RuntimeError(f"No D4 crops available in {d4_dir}")
    class_id, session_id = str(body_meta.iloc[0].class_id), str(body_meta.iloc[0].session_id)
    cfg, calibration_profile = _load_session_calibration(cfg, root, class_id, session_id)
    final_dir = Path(output_root) / "D5_REENTRY" / class_id / session_id
    tmp_dir = final_dir.with_name(final_dir.name + ".__building__")
    if tmp_dir.exists():
        shutil.rmtree(tmp_dir)
    tmp_dir.mkdir(parents=True, exist_ok=True)

    tracklets, face_audit = summarize_tracklets(
        d3_dir, d4_dir, cfg, root, tmp_dir / "face_targets"
    )
    if not tracklets:
        raise RuntimeError(f"No D4 crops available in {d4_dir}")

    candidates = build_reentry_candidates(tracklets, cfg)
    if bool(cfg.get("preserve_manual_reviews", True)):
        candidates = _apply_preserved_reviews(candidates, final_dir / "candidates.csv")
    review_path = root / "work" / "d5_manual_reviews" / class_id / session_id / "manual_reviews.csv"
    human_constraints, _ = _load_human_qc_constraints(review_path)
    candidates, injected_human_pairs = _inject_missing_human_qc_pairs(
        tracklets, candidates, human_constraints, cfg
    )
    candidates, human_qc_stats = _apply_human_qc_reviews(candidates, review_path)
    human_qc_stats["injected_missing_review_pairs"] = int(injected_human_pairs)
    stable_map, candidates = build_stable_person_map(
        tracklets, candidates, cfg, human_constraints=human_constraints
    )

    tracklet_rows = []
    for tr in tracklets:
        tracklet_rows.append({
            "class_id": tr.class_id,
            "session_id": tr.session_id,
            "tracklet_id": tr.tracklet_id,
            "track_id": tr.track_id,
            "student_id": tr.student_id,
            "start_sec": tr.start_sec,
            "end_sec": tr.end_sec,
            "start_frame": tr.start_frame,
            "end_frame": tr.end_frame,
            "body_start_images": tr.n_body_start,
            "body_end_images": tr.n_body_end,
            "face_samples": tr.n_face,
            "face_reliability": tr.face_reliability,
            "face_association_status": tr.face_association_status,
            "face_best_image_path": tr.face_best_image_path,
            "body_clean_ratio": tr.body_clean_ratio,
            "body_start_clean_ratio": tr.body_start_clean_ratio,
            "body_end_clean_ratio": tr.body_end_clean_ratio,
            "body_start_purity": tr.body_start_purity,
            "body_end_purity": tr.body_end_purity,
            "body_start_usable_samples": tr.body_start_usable_samples,
            "body_end_usable_samples": tr.body_end_usable_samples,
            "body_contaminated_samples": tr.body_contaminated_samples,
            "body_purity_samples": tr.body_purity_samples,
            "start_cx": tr.start_cx, "start_cy": tr.start_cy, "start_w": tr.start_w, "start_h": tr.start_h,
            "end_cx": tr.end_cx, "end_cy": tr.end_cy, "end_w": tr.end_w, "end_h": tr.end_h,
        })

    pd.DataFrame(tracklet_rows).to_csv(tmp_dir / "tracklets.csv", index=False)
    candidates.to_csv(tmp_dir / "candidates.csv", index=False)
    stable_map.to_csv(tmp_dir / "stable_person_map.csv", index=False)
    face_audit.to_csv(tmp_dir / "face_targets.csv", index=False)
    _save_embeddings(tmp_dir / "embeddings.npz", tracklets)

    face_status = face_audit["association_status"].value_counts().to_dict() if not face_audit.empty else {}
    d3_temporal_status: dict[str, int] = {}
    d3_temporal_ambiguous_tracklets = 0
    try:
        d3_guard = pd.read_csv(
            d3_dir / "metadata.csv",
            usecols=["tracklet_id", "face_temporal_status", "body_purity_status", "body_purity_score", "face_purity_status"],
            dtype={"tracklet_id": str},
            low_memory=False,
        )
        d3_temporal_status = {
            str(k): int(v) for k, v in d3_guard["face_temporal_status"].astype(str).value_counts().to_dict().items()
        }
        amb = d3_guard[d3_guard["face_temporal_status"].astype(str).eq("TEMPORAL_AMBIGUOUS_TRACKLET")]
        d3_temporal_ambiguous_tracklets = int(amb["tracklet_id"].nunique()) if not amb.empty else 0
    except Exception:
        d3_temporal_status = {}
        d3_temporal_ambiguous_tracklets = 0

    counts = {
        "tracklets": len(tracklets),
        "candidates_retained": int(len(candidates)),
        "auto_same": int((candidates.get("decision", pd.Series(dtype=str)) == "AUTO_SAME").sum()),
        "manual_same": int((candidates.get("decision", pd.Series(dtype=str)) == "MANUAL_SAME").sum()),
        "needs_manual_review": int((candidates.get("review_status", pd.Series(dtype=str)) == "NEEDS_MANUAL_REVIEW").sum()),
        "stable_persons": int(stable_map["stable_person_id"].nunique()) if not stable_map.empty else 0,
        "identified_stable_persons": int(stable_map.loc[stable_map["student_id"].astype(str).str.len() > 0, "stable_person_id"].nunique()) if not stable_map.empty else 0,
        "tracklets_with_validated_target_face": int(sum(x.face is not None for x in tracklets)),
        "tracklets_with_multi_sample_target_face": int(sum(x.n_face >= 2 for x in tracklets)),
        "tracklets_with_body_reid": int(sum(x.body_start is not None and x.body_end is not None for x in tracklets)),
        "target_face_samples_assigned": int(face_status.get("ASSIGNED", 0)),
        "target_face_samples_ambiguous": int(face_status.get("AMBIGUOUS_MULTIPLE_FACES", 0)),
        "d3_temporal_consistent_samples": int(d3_temporal_status.get("TEMPORAL_CONSISTENT", 0)),
        "d3_temporal_face_conflict_samples": int(d3_temporal_status.get("TEMPORAL_FACE_CONFLICT", 0)),
        "d3_temporal_ambiguous_samples": int(d3_temporal_status.get("TEMPORAL_AMBIGUOUS_TRACKLET", 0)),
        "d3_temporal_ambiguous_tracklets": int(d3_temporal_ambiguous_tracklets),
        "d3_cross_track_face_collision_samples": int(d3_temporal_status.get("TEMPORAL_CROSS_TRACK_COLLISION", 0)),
        "d3_body_crop_contaminated_samples": int(d3_guard["body_purity_status"].astype(str).str.upper().eq("BODY_CROP_CONTAMINATED").sum()) if "d3_guard" in locals() and "body_purity_status" in d3_guard.columns else 0,
        "d3_body_usable_samples": int(d3_guard["body_purity_status"].astype(str).str.upper().eq("USABLE").sum()) if "d3_guard" in locals() and "body_purity_status" in d3_guard.columns else 0,
        "d3_body_clean_samples": int(d3_guard["body_purity_status"].astype(str).str.upper().eq("CLEAN").sum()) if "d3_guard" in locals() and "body_purity_status" in d3_guard.columns else 0,
        "d3_face_purity_contaminated_samples": int(d3_guard["face_purity_status"].astype(str).str.upper().eq("FACE_CONTAMINATED").sum()) if "d3_guard" in locals() and "face_purity_status" in d3_guard.columns else 0,
        "tracklets_body_purity_auto_eligible": int(sum(_body_purity_auto_ok(x, x, cfg) for x in tracklets)),
        "body_purity_blocked_auto": int(candidates.get("decision_reason", pd.Series(dtype=str)).astype(str).eq("BODY_PURITY_INSUFFICIENT").sum()),
        "spatial_contradictions": int(candidates.get("decision_reason", pd.Series(dtype=str)).astype(str).eq("SPATIAL_CONTRADICTION").sum()),
        "cluster_conflicts": int(candidates["decision_reason"].astype(str).str.startswith("CLUSTER_").sum()) if not candidates.empty else 0,
        "same_track_auto_corroborated": int(candidates.get("decision_reason", pd.Series(dtype=str)).astype(str).isin({
            "SAME_TRACK_FACE_BODY_CORROBORATED", "SAME_TRACK_BODY_CORROBORATED"
        }).sum()),
        "same_track_needs_review": int(
            candidates.get("decision_reason", pd.Series(dtype=str)).astype(str).str.startswith("SAME_TRACK_").sum()
            - candidates.get("decision_reason", pd.Series(dtype=str)).astype(str).isin({
                "SAME_TRACK_FACE_BODY_CORROBORATED", "SAME_TRACK_BODY_CORROBORATED"
            }).sum()
        ),
        "cross_modal_auto_same": int(candidates.get("decision_reason", pd.Series(dtype=str)).astype(str).eq("FACE_BODY_CROSS_MODAL_STRONG").sum()),
        "qc_calibrated_auto_same": int(candidates.get("decision_reason", pd.Series(dtype=str)).astype(str).str.startswith("QC_CALIBRATED_PROMOTION_").sum()),
        "face_body_conflicts": int(candidates.get("decision_reason", pd.Series(dtype=str)).astype(str).eq("FACE_BODY_CONFLICT").sum()),
        "face_strong_body_not_corroborated": int(candidates.get("decision_reason", pd.Series(dtype=str)).astype(str).eq("FACE_STRONG_BODY_NOT_CORROBORATED").sum()),
        "manual_different": int((candidates.get("decision", pd.Series(dtype=str)).astype(str) == "MANUAL_DIFFERENT").sum()),
        "hard_different_constraints": int(human_qc_stats.get("different_constraints", 0)),
    }

    reid_weights = Path(str(cfg.get("reid_weights", "pretrained/reid/osnet_ain_x1_0_msmt17.pth")))
    if not reid_weights.is_absolute():
        reid_weights = root / reid_weights
    metadata = {
        "class_id": class_id,
        "session_id": session_id,
        "identity_resolver": "D3StrictOwnerFace+GradedFaceBodyPurity+TrackletTemporalFaceGuard+CrossTrackCollisionGuard(ArcFace)+EndpointBestSample+DualViewBodyReID(OSNet-AIN)+SpatialContinuity+CrossModalAutoGate+QCCalibratedPromotionGate+Temporal+ClusterConsistency",
        "face_source_for_d5": "Face/body purity are graded independently; D5 keeps strict D3 full-frame owner/temporal/collision guards, selects the best usable endpoint body samples, fuses full+central-torso OSNet views, and adds fixed-camera spatial continuity before AUTO_SAME",
        "reid_model_name": str(cfg.get("reid_model_name", "osnet_ain_x1_0")),
        "reid_weights": str(reid_weights.resolve()),
        "reid_weights_sha256": _sha256(reid_weights),
        "face_model_name": str(cfg.get("face_model_name", "buffalo_l")),
        "config": cfg,
        "effective_quality_gates": {
            "body_purity_min_sample_score": float(cfg.get("body_purity_min_sample_score", 0.55)),
            "body_endpoint_window_sec": float(cfg.get("body_endpoint_window_sec", 5.0)),
            "body_endpoint_min_usable_samples": int(cfg.get("body_endpoint_min_usable_samples", 2)),
            "body_endpoint_min_best_purity_for_auto": float(cfg.get("body_endpoint_min_best_purity_for_auto", 0.66)),
            "body_endpoint_min_mean_purity_for_auto": float(cfg.get("body_endpoint_min_mean_purity_for_auto", 0.58)),
            "spatial_auto_min": float(cfg.get("spatial_auto_min", 0.45)),
            "spatial_body_only_auto_min": float(cfg.get("spatial_body_only_auto_min", 0.72)),
            "spatial_short_gap_max_body_heights": float(cfg.get("spatial_short_gap_max_body_heights", 3.5)),
            "spatial_short_gap_sec": float(cfg.get("spatial_short_gap_sec", 5.0)),
            "spatial_tau_body_heights": float(cfg.get("spatial_tau_body_heights", 0.85)),
            "spatial_tau_gap_gain": float(cfg.get("spatial_tau_gap_gain", 1.15)),
            "spatial_weight": float(cfg.get("spatial_weight", 0.15)),
            "central_crop": [
                float(cfg.get("central_crop_x1", 0.14)), float(cfg.get("central_crop_y1", 0.18)),
                float(cfg.get("central_crop_x2", 0.86)), float(cfg.get("central_crop_y2", 0.82)),
            ],
        },
        "counts": counts,
        "target_face_status_counts": face_status,
        "d3_temporal_face_status_counts": d3_temporal_status,
        "human_qc": human_qc_stats,
        "calibration_profile": calibration_profile,
    }
    (tmp_dir / "summary.json").write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2, default=str), encoding="utf-8"
    )

    if final_dir.exists():
        shutil.rmtree(final_dir)
    tmp_dir.replace(final_dir)
    print("D5 COMPLETE:", json.dumps(counts, ensure_ascii=False), flush=True)
    return final_dir

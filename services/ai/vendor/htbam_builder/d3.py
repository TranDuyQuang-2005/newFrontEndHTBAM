from __future__ import annotations

from pathlib import Path
import math
import shutil

import cv2
import numpy as np
import pandas as pd

from .core import add_tracklet_ids, attach_identity, load_identity_map, validate_identity_temporal_consistency
from .ort_runtime import prepare_onnxruntime_for_providers, assert_insightface_cuda_active


class InsightFaceDetector:
    def __init__(self, model_name: str, providers: list[str]):
        # IMPORTANT: preload pip-installed CUDA/cuDNN DLLs *before* InsightFace creates
        # ONNX Runtime sessions. Otherwise ORT can expose CUDAExecutionProvider yet silently
        # fall back to CPU on Windows because cublas/cudnn DLLs are not on the DLL search path.
        prepare_onnxruntime_for_providers(providers)
        try:
            from insightface.app import FaceAnalysis
        except ImportError as e:
            raise RuntimeError(
                "Install insightface and onnxruntime/onnxruntime-gpu before building D3"
            ) from e
        self.app = FaceAnalysis(name=model_name, providers=providers)
        assert_insightface_cuda_active(self.app, providers)
        ctx_id = 0 if "CUDAExecutionProvider" in providers else -1
        self.app.prepare(ctx_id=ctx_id, det_size=(640, 640))

    def detect(self, frame_bgr: np.ndarray, upscale: float = 1.0) -> list[dict]:
        if upscale <= 0:
            raise ValueError("upscale must be > 0")
        work = frame_bgr
        if upscale != 1.0:
            work = cv2.resize(
                frame_bgr, None, fx=upscale, fy=upscale, interpolation=cv2.INTER_CUBIC
            )
        faces = self.app.get(work)
        out = []
        for face in faces:
            bbox = np.asarray(face.bbox, dtype=np.float32) / upscale
            kps = np.asarray(face.kps, dtype=np.float32) / upscale
            emb = getattr(face, "normed_embedding", None)
            if emb is None:
                emb = getattr(face, "embedding", None)
                if emb is not None:
                    emb = np.asarray(emb, dtype=np.float32).reshape(-1)
                    n = float(np.linalg.norm(emb))
                    emb = emb / n if n > 1e-12 else None
            elif emb is not None:
                emb = np.asarray(emb, dtype=np.float32).reshape(-1)
            out.append({
                "bbox": bbox,
                "kps": kps,
                "det_score": float(face.det_score),
                # Kept only in-memory during D3 build. It lets D3 reject a face that is
                # geometrically plausible for a body but temporally belongs to another identity.
                "embedding": emb,
            })
        return out


def face_inside_person(face_bbox: np.ndarray, person_bbox: np.ndarray) -> bool:
    fx1, fy1, fx2, fy2 = map(float, face_bbox)
    px1, py1, px2, py2 = map(float, person_bbox)
    cx, cy = (fx1 + fx2) / 2.0, (fy1 + fy2) / 2.0
    return px1 <= cx <= px2 and py1 <= cy <= py2


def _intersection_ratio(inner_bbox: np.ndarray, outer_bbox: np.ndarray) -> float:
    x1 = max(float(inner_bbox[0]), float(outer_bbox[0]))
    y1 = max(float(inner_bbox[1]), float(outer_bbox[1]))
    x2 = min(float(inner_bbox[2]), float(outer_bbox[2]))
    y2 = min(float(inner_bbox[3]), float(outer_bbox[3]))
    inter = max(0.0, x2 - x1) * max(0.0, y2 - y1)
    area = max(1e-6, float(inner_bbox[2] - inner_bbox[0]) * float(inner_bbox[3] - inner_bbox[1]))
    return float(inter / area)


def body_track_purity_diagnostics(tracks_frame: pd.DataFrame, cfg: dict | None = None) -> dict[int, dict]:
    """Graded crowd/owner quality for each tracker bbox.

    The previous hard ``overlap => contaminated`` gate destroyed re-entry recall in a crowded
    classroom because normal seated-person boxes overlap frequently.  This version separates:
      * body purity: how usable the target crop is for clothing/body ReID;
      * face purity: whether a foreign head can plausibly contaminate the target head region.

    It returns CLEAN / USABLE / CONTAMINATED *scores* instead of treating every overlap as a hard
    failure.  D5 is allowed to select only the best endpoint samples from a tracklet.
    """
    cfg = cfg or {}
    boxes: dict[int, np.ndarray] = {}
    for _, r in tracks_frame.iterrows():
        try:
            tid = int(r.track_id)
            b = np.asarray([r.x1, r.y1, r.x2, r.y2], dtype=np.float32)
        except Exception:
            continue
        if float(b[2] - b[0]) <= 1.0 or float(b[3] - b[1]) <= 1.0:
            continue
        boxes[tid] = b

    body_clean_min = float(cfg.get('body_purity_clean_score', 0.82))
    body_usable_min = float(cfg.get('body_purity_usable_score', 0.55))
    face_clean_min = float(cfg.get('face_purity_clean_score', 0.86))
    face_usable_min = float(cfg.get('face_purity_usable_score', 0.62))
    foreign_head_h = float(cfg.get('purity_foreign_head_height_ratio', 0.34))
    target_head_h = float(cfg.get('purity_target_head_height_ratio', 0.46))

    def area(b: np.ndarray) -> float:
        return max(1e-6, float(b[2]-b[0]) * float(b[3]-b[1]))

    def inter_area(a: np.ndarray, b: np.ndarray) -> float:
        x1, y1 = max(float(a[0]), float(b[0])), max(float(a[1]), float(b[1]))
        x2, y2 = min(float(a[2]), float(b[2])), min(float(a[3]), float(b[3]))
        return max(0.0, x2-x1) * max(0.0, y2-y1)

    out: dict[int, dict] = {}
    for tid, tb in boxes.items():
        tx1, ty1, tx2, ty2 = map(float, tb)
        tw, th = max(1e-6, tx2-tx1), max(1e-6, ty2-ty1)
        tarea = area(tb)
        head_zone = np.asarray([tx1, ty1, tx2, ty1 + target_head_h*th], dtype=np.float32)
        torso_zone = np.asarray([tx1, ty1 + 0.22*th, tx2, ty1 + 0.78*th], dtype=np.float32)
        lower_zone = np.asarray([tx1, ty1 + 0.72*th, tx2, ty2], dtype=np.float32)

        max_target_overlap = 0.0
        max_other_overlap = 0.0
        weighted_overlap = 0.0
        max_foreign_head_overlap = 0.0
        max_foreign_head_target = 0.0
        center_intruders = 0
        head_intruders = 0

        for oid, ob in boxes.items():
            if oid == tid:
                continue
            oa = area(ob)
            ia = inter_area(tb, ob)
            if ia <= 0.0:
                continue
            t_ratio = ia / tarea
            o_ratio = ia / oa
            max_target_overlap = max(max_target_overlap, t_ratio)
            max_other_overlap = max(max_other_overlap, o_ratio)

            # Region-weighted overlap: overlap near head/torso is more harmful than legs/edges.
            head_frac = inter_area(head_zone, ob) / max(area(head_zone), 1e-6)
            torso_frac = inter_area(torso_zone, ob) / max(area(torso_zone), 1e-6)
            lower_frac = inter_area(lower_zone, ob) / max(area(lower_zone), 1e-6)
            weighted_overlap = max(weighted_overlap, 0.48*head_frac + 0.42*torso_frac + 0.10*lower_frac)

            ox1, oy1, ox2, oy2 = map(float, ob)
            ow, oh = max(1e-6, ox2-ox1), max(1e-6, oy2-oy1)
            ocx, ocy = (ox1+ox2)/2.0, (oy1+oy2)/2.0
            if tx1 <= ocx <= tx2 and ty1 <= ocy <= ty2:
                center_intruders += 1

            foreign_head = np.asarray([
                ox1 + 0.08*ow, oy1,
                ox2 - 0.08*ow, oy1 + foreign_head_h*oh,
            ], dtype=np.float32)
            fha = area(foreign_head)
            fh_in_target = inter_area(foreign_head, tb) / fha
            fh_in_head = inter_area(foreign_head, head_zone) / fha
            max_foreign_head_overlap = max(max_foreign_head_overlap, fh_in_target)
            max_foreign_head_target = max(max_foreign_head_target, fh_in_head)
            fhcx, fhcy = (float(foreign_head[0])+float(foreign_head[2]))/2.0, (float(foreign_head[1])+float(foreign_head[3]))/2.0
            if float(head_zone[0]) <= fhcx <= float(head_zone[2]) and float(head_zone[1]) <= fhcy <= float(head_zone[3]):
                head_intruders += 1

        # Body score is graded. A small overlap is normal in classroom seating and should not
        # invalidate the whole tracklet. Foreign-head evidence affects body less than face.
        body_risk = (
            0.48 * weighted_overlap
            + 0.20 * max_target_overlap
            + 0.10 * max_other_overlap
            + 0.12 * min(1.0, max_foreign_head_overlap)
            + 0.05 * min(center_intruders, 2)
        )
        body_score = float(np.clip(1.0 - body_risk, 0.0, 1.0))

        # Face purity is independent. A neighbour head in the target head-zone is a strong risk,
        # even if the body crop is otherwise usable.
        face_risk = (
            0.62 * max_foreign_head_target
            + 0.18 * max_foreign_head_overlap
            + 0.20 * min(head_intruders, 1)
        )
        face_score = float(np.clip(1.0 - face_risk, 0.0, 1.0))

        if body_score >= body_clean_min:
            body_status = 'CLEAN'
        elif body_score >= body_usable_min:
            body_status = 'USABLE'
        else:
            body_status = 'BODY_CROP_CONTAMINATED'

        if face_score >= face_clean_min:
            face_status = 'FACE_CLEAN'
        elif face_score >= face_usable_min:
            face_status = 'FACE_USABLE'
        else:
            face_status = 'FACE_CONTAMINATED'

        out[tid] = {
            'body_purity_status': body_status,
            'body_purity_score': body_score,
            'face_purity_status': face_status,
            'face_purity_score': face_score,
            'body_purity_max_target_overlap': float(max_target_overlap),
            'body_purity_max_other_overlap': float(max_other_overlap),
            'body_purity_weighted_overlap': float(weighted_overlap),
            'body_purity_intruder_count': int(center_intruders),
            'body_purity_head_intruder_count': int(head_intruders),
            'body_purity_foreign_head_overlap': float(max_foreign_head_overlap),
            'body_purity_foreign_head_target_overlap': float(max_foreign_head_target),
        }
    return out

def face_person_geometry(face: dict, person_bbox: np.ndarray, cfg: dict | None = None) -> dict | None:
    """Score whether a detected face anatomically belongs to one person bbox.

    A face merely being *inside* an overlapping person box is not enough in a crowded
    classroom.  The center must be in a plausible head region of that person's box, most
    of the face must be contained, and the face size must be plausible.  This is deliberately
    conservative: ambiguous cases are rejected instead of contaminating identity embeddings.
    """
    cfg = cfg or {}
    fb = np.asarray(face["bbox"], dtype=np.float32)
    pb = np.asarray(person_bbox, dtype=np.float32)
    px1, py1, px2, py2 = map(float, pb)
    fx1, fy1, fx2, fy2 = map(float, fb)
    pw, ph = max(1e-6, px2 - px1), max(1e-6, py2 - py1)
    fw, fh = max(1e-6, fx2 - fx1), max(1e-6, fy2 - fy1)
    cx, cy = (fx1 + fx2) / 2.0, (fy1 + fy2) / 2.0
    rel_x = (cx - px1) / pw
    rel_y = (cy - py1) / ph
    contain = _intersection_ratio(fb, pb)
    size_ratio = min(fw / pw, fh / ph)
    det = float(face.get("det_score", 0.0))

    min_det = float(cfg.get("association_min_det_score", cfg.get("min_det_score", 0.50)))
    min_contain = float(cfg.get("association_min_face_containment", 0.80))
    min_x = float(cfg.get("association_min_center_x", 0.05))
    max_x = float(cfg.get("association_max_center_x", 0.95))
    max_y = float(cfg.get("association_max_center_y", 0.58))
    min_size_ratio = float(cfg.get("association_min_face_person_ratio", 0.035))
    max_size_ratio = float(cfg.get("association_max_face_person_ratio", 0.65))

    if det < min_det or contain < min_contain:
        return None
    if not (min_x <= rel_x <= max_x and 0.0 <= rel_y <= max_y):
        return None
    if not (min_size_ratio <= size_ratio <= max_size_ratio):
        return None

    # Expected classroom head location: upper part of the target person box.  Horizontal
    # centering matters, but less than vertical head location because seated/leaning people
    # are often off-center.  This strongly penalizes a neighbour's face entering near the
    # lower edge of the target body crop.
    x_score = max(0.0, 1.0 - abs(rel_x - 0.5) / 0.5)
    preferred_y = float(cfg.get("association_preferred_center_y", 0.22))
    y_sigma = max(1e-3, float(cfg.get("association_center_y_sigma", 0.22)))
    y_score = math.exp(-0.5 * ((rel_y - preferred_y) / y_sigma) ** 2)
    size_target = float(cfg.get("association_preferred_face_person_ratio", 0.16))
    size_sigma = max(1e-3, float(cfg.get("association_face_ratio_sigma", 0.12)))
    size_score = math.exp(-0.5 * ((size_ratio - size_target) / size_sigma) ** 2)

    score = (
        0.40 * det
        + 0.28 * y_score
        + 0.17 * x_score
        + 0.10 * contain
        + 0.05 * size_score
    )
    return {
        "score": float(score),
        "center_x_rel": float(rel_x),
        "center_y_rel": float(rel_y),
        "containment": float(contain),
        "face_person_ratio": float(size_ratio),
    }


def select_face_for_person(
    faces: list[dict], person_bbox: np.ndarray, cfg: dict | None = None
) -> tuple[dict | None, dict]:
    """Select one face for a person bbox, or reject as ambiguous/no-face.

    The returned diagnostics are designed to be persisted by D3/D5 so preview/QC can show
    *the exact face that was used*, rather than a face chosen only because it happened to be
    inside an overlapping body box.
    """
    cfg = cfg or {}
    scored: list[tuple[float, int, dict, dict]] = []
    for i, face in enumerate(faces):
        geom = face_person_geometry(face, person_bbox, cfg)
        if geom is not None:
            scored.append((float(geom["score"]), i, face, geom))
    scored.sort(key=lambda x: x[0], reverse=True)

    diag = {
        "n_faces_detected": int(len(faces)),
        "n_plausible_faces": int(len(scored)),
        "association_status": "NO_PLAUSIBLE_FACE",
        "association_score": np.nan,
        "association_runner_up_score": np.nan,
        "association_margin": np.nan,
    }
    if not scored:
        return None, diag

    top = scored[0]
    second_score = scored[1][0] if len(scored) > 1 else None
    margin = float(top[0] - second_score) if second_score is not None else float("inf")
    min_score = float(cfg.get("association_min_score", 0.52))
    min_margin = float(cfg.get("association_min_margin", 0.10))

    diag.update({
        "association_score": float(top[0]),
        "association_runner_up_score": np.nan if second_score is None else float(second_score),
        "association_margin": np.nan if second_score is None else margin,
    })
    if top[0] < min_score:
        diag["association_status"] = "LOW_ASSOCIATION_SCORE"
        return None, diag
    if second_score is not None and margin < min_margin:
        diag["association_status"] = "AMBIGUOUS_MULTIPLE_FACES"
        return None, diag

    selected = dict(top[2])
    selected["association_score"] = float(top[0])
    selected["association_margin"] = np.nan if second_score is None else margin
    selected.update({f"association_{k}": v for k, v in top[3].items() if k != "score"})
    diag["association_status"] = "ASSIGNED"
    return selected, diag


def assign_faces_to_tracks_with_diagnostics(
    faces: list[dict], tracks_frame: pd.DataFrame, cfg: dict | None = None
) -> tuple[dict[int, dict], dict[int, dict]]:
    """Global one-to-one face→track assignment with ambiguity rejection.

    Candidate pairs are scored using anatomical geometry inside each track's person bbox.
    A track or face with nearly tied alternatives is marked ambiguous instead of forcing a
    potentially wrong assignment.  This directly addresses the crowded-classroom failure mode
    "body A, face B".
    """
    cfg = cfg or {}
    track_boxes: dict[int, np.ndarray] = {}
    for _, row in tracks_frame.iterrows():
        track_boxes[int(row.track_id)] = np.asarray(
            [row.x1, row.y1, row.x2, row.y2], dtype=np.float32
        )

    pairs: list[dict] = []
    for fi, face in enumerate(faces):
        for tid, bbox in track_boxes.items():
            geom = face_person_geometry(face, bbox, cfg)
            if geom is None:
                continue
            pairs.append({"face_index": fi, "track_id": tid, "score": geom["score"], "geom": geom})

    diagnostics = {
        tid: {
            "assignment_status": "NO_PLAUSIBLE_FACE",
            "association_score": np.nan,
            "association_margin": np.nan,
            "n_plausible_faces": 0,
        }
        for tid in track_boxes
    }
    if not pairs:
        return {}, diagnostics

    min_score = float(cfg.get("association_min_score", 0.52))
    min_margin = float(cfg.get("association_min_margin", 0.10))

    by_track: dict[int, list[dict]] = {}
    by_face: dict[int, list[dict]] = {}
    for p in pairs:
        by_track.setdefault(int(p["track_id"]), []).append(p)
        by_face.setdefault(int(p["face_index"]), []).append(p)
    for values in by_track.values():
        values.sort(key=lambda x: float(x["score"]), reverse=True)
    for values in by_face.values():
        values.sort(key=lambda x: float(x["score"]), reverse=True)

    ambiguous_tracks: set[int] = set()
    ambiguous_faces: set[int] = set()
    for tid, vals in by_track.items():
        diagnostics[tid]["n_plausible_faces"] = len(vals)
        diagnostics[tid]["association_score"] = float(vals[0]["score"])
        if len(vals) > 1:
            margin = float(vals[0]["score"] - vals[1]["score"])
            diagnostics[tid]["association_margin"] = margin
            if margin < min_margin:
                ambiguous_tracks.add(tid)
                diagnostics[tid]["assignment_status"] = "AMBIGUOUS_MULTIPLE_FACES"
    face_owner_margin: dict[int, float] = {}
    for fi, vals in by_face.items():
        # This is different from the per-track face margin above: it measures whether THIS
        # face itself has one clear body owner among all overlapping person boxes. A face can
        # be the only plausible face for Track A yet still fit Track B almost as well. Persist
        # this owner margin so D5 can refuse low-exclusivity faces for automatic identity.
        if len(vals) > 1:
            margin = float(vals[0]["score"] - vals[1]["score"])
            face_owner_margin[fi] = margin
            if margin < float(cfg.get("association_min_owner_margin", min_margin)):
                ambiguous_faces.add(fi)
        else:
            face_owner_margin[fi] = float("inf")

    assigned: dict[int, dict] = {}
    used_faces: set[int] = set()
    for p in sorted(pairs, key=lambda x: float(x["score"]), reverse=True):
        tid, fi, score = int(p["track_id"]), int(p["face_index"]), float(p["score"])
        if score < min_score or tid in assigned or fi in used_faces:
            continue
        if tid in ambiguous_tracks or fi in ambiguous_faces:
            continue
        face = dict(faces[fi])
        face["association_score"] = score
        face["association_margin"] = diagnostics[tid].get("association_margin", np.nan)
        face["association_owner_margin"] = face_owner_margin.get(fi, float("inf"))
        face.update({f"association_{k}": v for k, v in p["geom"].items() if k != "score"})
        assigned[tid] = face
        used_faces.add(fi)
        diagnostics[tid]["assignment_status"] = "ASSIGNED"

    for tid in track_boxes:
        if tid not in assigned and diagnostics[tid]["assignment_status"] == "NO_PLAUSIBLE_FACE":
            if tid in by_track and by_track[tid]:
                diagnostics[tid]["assignment_status"] = "AMBIGUOUS_OR_CONFLICT"
    return assigned, diagnostics


def assign_faces_to_tracks(faces: list[dict], tracks_frame: pd.DataFrame, cfg: dict | None = None) -> dict[int, dict]:
    assigned, _ = assign_faces_to_tracks_with_diagnostics(faces, tracks_frame, cfg)
    return assigned


def _quality(face_img: np.ndarray) -> tuple[float, float]:
    gray = cv2.cvtColor(face_img, cv2.COLOR_BGR2GRAY)
    blur_var = float(cv2.Laplacian(gray, cv2.CV_64F).var())
    brightness = float(gray.mean())
    return blur_var, brightness


def _align_face(frame: np.ndarray, kps: np.ndarray, image_size: int = 112) -> np.ndarray:
    try:
        from insightface.utils import face_align
    except ImportError as e:
        raise RuntimeError("insightface is required for ArcFace-aligned crops") from e
    return face_align.norm_crop(frame, landmark=kps, image_size=image_size)



def _l2(v: np.ndarray | None) -> np.ndarray | None:
    if v is None:
        return None
    a = np.asarray(v, dtype=np.float32).reshape(-1)
    if not np.isfinite(a).all():
        return None
    n = float(np.linalg.norm(a))
    if n <= 1e-12:
        return None
    return a / n


def apply_tracklet_temporal_face_guard(rows: list[dict], cfg: dict | None = None) -> list[dict]:
    """Reject frame-level face↔body assignments that are identity-inconsistent over a tracklet.

    Geometry alone cannot fully solve crowded scenes: a neighbour's face can occasionally sit in
    the anatomically plausible head region of the target body.  D3 therefore performs a second,
    independent identity-consistency pass using the ArcFace embedding already produced by
    InsightFace on the ORIGINAL full frame.

    The guard is deliberately conservative:
      * it operates per contiguous ``tracklet_id`` (not numeric tracker ID), so ID reuse after an
        occlusion does not poison another tracklet;
      * with >=2 samples it keeps only a coherent dominant face cluster;
      * a split/ambiguous tracklet is rejected for identity use instead of guessing;
      * rejected crops remain on disk/metadata for audit, but D5 is forbidden from using them.
    """
    cfg = cfg or {}
    enabled = bool(cfg.get("temporal_face_guard_enabled", True))
    min_samples = max(2, int(cfg.get("temporal_face_min_samples", 2)))
    inlier_thr = float(cfg.get("temporal_face_inlier_similarity", 0.42))
    min_dominance = float(cfg.get("temporal_face_min_dominance", 0.67))
    min_cluster_mean = float(cfg.get("temporal_face_min_cluster_mean_similarity", 0.48))

    for r in rows:
        if str(r.get("face_purity_status", "UNKNOWN")).upper() == "FACE_CONTAMINATED":
            r["face_temporal_status"] = "FACE_PURITY_CONTAMINATED"
        elif str(r.get("face_assignment_status", "")).upper() != "ASSIGNED" or str(r.get("status", "")).upper() != "ACCEPT":
            r["face_temporal_status"] = "NOT_APPLICABLE"
        else:
            r["face_temporal_status"] = "PENDING"
        r["face_temporal_similarity"] = np.nan
        r["face_temporal_inlier_count"] = 0
        r["face_temporal_total_samples"] = 0
        r["face_temporal_dominance"] = np.nan

    if not enabled:
        for r in rows:
            if r.get("face_temporal_status") == "PENDING":
                r["face_temporal_status"] = "DISABLED"
        return rows

    groups: dict[str, list[int]] = {}
    for i, r in enumerate(rows):
        if r.get("face_temporal_status") != "PENDING":
            continue
        emb = _l2(r.get("_face_embedding"))
        if emb is None:
            r["face_temporal_status"] = "NO_EMBEDDING"
            continue
        r["_face_embedding"] = emb
        groups.setdefault(str(r.get("tracklet_id", "")), []).append(i)

    for tracklet_id, idxs in groups.items():
        n = len(idxs)
        for i in idxs:
            rows[i]["face_temporal_total_samples"] = n
        if n == 1:
            # One frame can still be useful for manual audit, but D5's auto-face gate already
            # requires multiple samples. Mark it explicitly instead of pretending it is verified.
            rows[idxs[0]]["face_temporal_status"] = "SINGLE_SAMPLE"
            rows[idxs[0]]["face_temporal_inlier_count"] = 1
            rows[idxs[0]]["face_temporal_dominance"] = 1.0
            continue

        mat = np.stack([rows[i]["_face_embedding"] for i in idxs]).astype(np.float32)
        sims = mat @ mat.T
        # Choose the medoid whose threshold-neighbourhood has the largest support, then the
        # strongest mean similarity. This is robust to one or a few neighbour-face contaminants.
        candidates = []
        for j in range(n):
            support = np.where(sims[j] >= inlier_thr)[0]
            mean_sim = float(np.mean(sims[j, support])) if len(support) else -1.0
            candidates.append((len(support), mean_sim, j, support))
        candidates.sort(key=lambda x: (x[0], x[1]), reverse=True)
        best_count, _, seed, support = candidates[0]
        dominance = float(best_count / n)
        cluster_mean = float(np.mean(sims[np.ix_(support, support)])) if len(support) else -1.0

        # A 50/50 or otherwise non-dominant split is exactly what an ID switch / wrong-face
        # contamination looks like. Reject the whole tracklet face evidence rather than pick one.
        if best_count < min_samples or dominance < min_dominance or cluster_mean < min_cluster_mean:
            for i in idxs:
                rows[i]["face_temporal_status"] = "TEMPORAL_AMBIGUOUS_TRACKLET"
                rows[i]["face_temporal_inlier_count"] = int(best_count)
                rows[i]["face_temporal_dominance"] = dominance
            continue

        proto = _l2(np.mean(mat[support], axis=0))
        if proto is None:
            for i in idxs:
                rows[i]["face_temporal_status"] = "TEMPORAL_AMBIGUOUS_TRACKLET"
            continue
        inlier_local = set(int(x) for x in support.tolist())
        for local_j, i in enumerate(idxs):
            sim = float(np.dot(mat[local_j], proto))
            rows[i]["face_temporal_similarity"] = sim
            rows[i]["face_temporal_inlier_count"] = int(best_count)
            rows[i]["face_temporal_dominance"] = dominance
            rows[i]["face_temporal_status"] = (
                "TEMPORAL_CONSISTENT" if local_j in inlier_local and sim >= inlier_thr
                else "TEMPORAL_FACE_CONFLICT"
            )

    return rows


def apply_cross_track_face_collision_guard(rows: list[dict], cfg: dict | None = None) -> list[dict]:
    """Invalidate face evidence when two simultaneously-active tracklets carry the same face identity.

    A per-tracklet temporal cluster can still be *consistently wrong*: e.g. Track A repeatedly steals
    neighbour B's face. If B's real track is also visible at the same time, both tracklets can end up
    with nearly identical ArcFace prototypes. One physical face cannot belong to two overlapping
    bodies simultaneously, so such evidence is unsafe for D5 and is kept only for audit.
    """
    cfg = cfg or {}
    if not bool(cfg.get("cross_track_face_collision_guard_enabled", True)):
        return rows
    sim_thr = float(cfg.get("cross_track_face_collision_similarity", 0.72))
    min_overlap = float(cfg.get("cross_track_face_collision_min_overlap_sec", 0.25))
    min_samples = max(2, int(cfg.get("cross_track_face_collision_min_samples", 2)))

    groups: dict[str, list[int]] = {}
    for i, r in enumerate(rows):
        if str(r.get("face_temporal_status", "")).upper() != "TEMPORAL_CONSISTENT":
            continue
        emb = _l2(r.get("_face_embedding"))
        if emb is None:
            continue
        r["_face_embedding"] = emb
        groups.setdefault(str(r.get("tracklet_id", "")), []).append(i)

    protos: dict[str, np.ndarray] = {}
    spans: dict[str, tuple[float, float]] = {}
    for tid, idxs in groups.items():
        if len(idxs) < min_samples:
            continue
        proto = _l2(np.mean(np.stack([rows[i]["_face_embedding"] for i in idxs]), axis=0))
        if proto is None:
            continue
        ts = [float(rows[i].get("timestamp_sec", 0.0)) for i in idxs]
        protos[tid] = proto
        spans[tid] = (min(ts), max(ts))

    bad: set[str] = set()
    tids = sorted(protos)
    for a_i in range(len(tids)):
        a = tids[a_i]
        a0, a1 = spans[a]
        for b in tids[a_i + 1:]:
            b0, b1 = spans[b]
            overlap = min(a1, b1) - max(a0, b0)
            if overlap < min_overlap:
                continue
            sim = float(np.dot(protos[a], protos[b]))
            if sim >= sim_thr:
                bad.add(a); bad.add(b)

    if bad:
        for r in rows:
            if str(r.get("tracklet_id", "")) in bad and str(r.get("face_temporal_status", "")).upper() == "TEMPORAL_CONSISTENT":
                r["face_temporal_status"] = "TEMPORAL_CROSS_TRACK_COLLISION"
    return rows


def build_d3(
    track_csv: str | Path,
    identity_map_path: str | Path,
    output_root: str | Path,
    cfg: dict,
) -> Path:
    tracks = pd.read_csv(track_csv)
    if tracks.empty:
        raise RuntimeError(f"No tracks in {track_csv}")
    identity_map = load_identity_map(identity_map_path)
    validate_identity_temporal_consistency(tracks, identity_map)
    tracks = attach_identity(tracks, identity_map)
    fps = float(tracks.iloc[0].fps)
    # Use the exact same contiguous-tracklet convention as D4. This prevents one numeric tracker
    # ID reused after an occlusion from sharing temporal face history with a different person.
    tracks = add_tracklet_ids(
        tracks, fps=fps, break_gap_sec=float(cfg.get("tracklet_break_gap_sec", 0.50))
    )
    class_id, session_id = str(tracks.iloc[0].class_id), str(tracks.iloc[0].session_id)
    video_path = Path(str(tracks.iloc[0].video_path))

    final_dir = Path(output_root) / "D3_FACE" / class_id / session_id
    tmp_dir = final_dir.with_name(final_dir.name + ".__building__")
    if tmp_dir.exists():
        shutil.rmtree(tmp_dir)
    img_root = tmp_dir / "images"
    img_root.mkdir(parents=True, exist_ok=True)

    detector = InsightFaceDetector(
        cfg.get("model_name", "buffalo_l"),
        list(cfg.get("providers", ["CPUExecutionProvider"])),
    )
    every_sec = float(cfg.get("sample_every_sec", 1.0))
    sampled_frames = set(
        range(0, int(tracks.frame_index.max()) + 1, max(1, int(round(fps * every_sec))))
    )
    by_frame = {
        int(k): g for k, g in tracks.groupby("frame_index") if int(k) in sampled_frames
    }

    cap = cv2.VideoCapture(str(video_path))
    if not cap.isOpened():
        raise RuntimeError(f"Cannot open video: {video_path}")

    rows = []
    frame_idx = 0
    try:
        while True:
            ok, frame = cap.read()
            if not ok:
                break
            if frame_idx not in by_frame:
                frame_idx += 1
                continue

            tdf = by_frame[frame_idx]
            purity_by_track = body_track_purity_diagnostics(tdf, cfg)
            faces = detector.detect(frame, upscale=float(cfg.get("upscale", 1.0)))
            assigned, diagnostics = assign_faces_to_tracks_with_diagnostics(faces, tdf, cfg)

            for _, tr in tdf.iterrows():
                tid = int(tr.track_id)
                face = assigned.get(tid)
                diag = diagnostics.get(tid, {})
                pdiag = purity_by_track.get(tid, {
                    "body_purity_status": "UNKNOWN", "body_purity_score": 0.0,
                    "face_purity_status": "UNKNOWN", "face_purity_score": 0.0,
                    "body_purity_max_target_overlap": np.nan, "body_purity_max_other_overlap": np.nan,
                    "body_purity_weighted_overlap": np.nan,
                    "body_purity_intruder_count": 0, "body_purity_head_intruder_count": 0,
                    "body_purity_foreign_head_overlap": np.nan,
                    "body_purity_foreign_head_target_overlap": np.nan,
                })
                base = {
                    "class_id": class_id,
                    "session_id": session_id,
                    "frame_index": frame_idx,
                    "timestamp_sec": float(tr.timestamp_sec),
                    "track_id": tid,
                    "tracklet_id": str(tr.tracklet_id),
                    "student_id": tr.student_id if pd.notna(tr.student_id) else "",
                    "identity_review_status": tr.identity_review_status,
                    "face_assignment_status": diag.get("assignment_status", "NO_PLAUSIBLE_FACE"),
                    "face_person_association_score": diag.get("association_score", np.nan),
                    "face_person_association_margin": diag.get("association_margin", np.nan),
                    "n_plausible_faces": diag.get("n_plausible_faces", 0),
                    "body_purity_status": pdiag.get("body_purity_status", "UNKNOWN"),
                    "body_purity_score": pdiag.get("body_purity_score", 0.0),
                    "face_purity_status": pdiag.get("face_purity_status", "UNKNOWN"),
                    "face_purity_score": pdiag.get("face_purity_score", 0.0),
                    "body_purity_max_target_overlap": pdiag.get("body_purity_max_target_overlap", np.nan),
                    "body_purity_max_other_overlap": pdiag.get("body_purity_max_other_overlap", np.nan),
                    "body_purity_weighted_overlap": pdiag.get("body_purity_weighted_overlap", np.nan),
                    "body_purity_intruder_count": pdiag.get("body_purity_intruder_count", 0),
                    "body_purity_head_intruder_count": pdiag.get("body_purity_head_intruder_count", 0),
                    "body_purity_foreign_head_overlap": pdiag.get("body_purity_foreign_head_overlap", np.nan),
                    "body_purity_foreign_head_target_overlap": pdiag.get("body_purity_foreign_head_target_overlap", np.nan),
                }
                if face is None:
                    rows.append({
                        **base,
                        "image_path": "",
                        "status": "NO_FACE",
                        "det_score": np.nan,
                        "blur_var": np.nan,
                        "brightness": np.nan,
                        "face_size_px": np.nan,
                        "face_x1": np.nan,
                        "face_y1": np.nan,
                        "face_x2": np.nan,
                        "face_y2": np.nan,
                        "face_center_x_rel": np.nan,
                        "face_center_y_rel": np.nan,
                        "face_person_owner_margin": np.nan,
                        "_face_embedding": None,
                    })
                    continue

                face_bbox = np.asarray(face["bbox"], dtype=np.float32)
                fw, fh = float(face_bbox[2] - face_bbox[0]), float(face_bbox[3] - face_bbox[1])
                if (
                    face["det_score"] < float(cfg.get("min_det_score", 0.5))
                    or min(fw, fh) < float(cfg.get("min_face_size_px", 28))
                ):
                    status = "REJECT_DETECTION"
                    img_rel = ""
                    blur_var = brightness = np.nan
                    face_size = int(min(fw, fh))
                else:
                    aligned = _align_face(frame, face["kps"], 112)
                    blur_var, brightness = _quality(aligned)
                    face_size = int(min(fw, fh))
                    accepted = (
                        blur_var >= float(cfg.get("min_blur_var", 35.0))
                        and brightness >= float(cfg.get("min_brightness", 30.0))
                        and brightness <= float(cfg.get("max_brightness", 225.0))
                    )
                    status = "ACCEPT" if accepted else "REJECT_QUALITY"
                    if accepted:
                        identity_label = (
                            str(tr.student_id)
                            if pd.notna(tr.student_id) and str(tr.student_id)
                            else f"TRACK_{tid:05d}"
                        )
                        label_state = (
                            "known"
                            if pd.notna(tr.student_id) and str(tr.student_id)
                            else "unassigned"
                        )
                        rel_dir = Path(label_state) / identity_label
                        (img_root / rel_dir).mkdir(parents=True, exist_ok=True)
                        filename = f"{class_id}_{session_id}_T{tid:05d}_F{frame_idx:08d}.jpg"
                        img_path = img_root / rel_dir / filename
                        cv2.imwrite(
                            str(img_path),
                            aligned,
                            [cv2.IMWRITE_JPEG_QUALITY, int(cfg.get("jpeg_quality", 95))],
                        )
                        img_rel = str(img_path.relative_to(tmp_dir))
                    else:
                        img_rel = ""

                rows.append({
                    **base,
                    "image_path": img_rel,
                    "status": status,
                    "det_score": float(face["det_score"]),
                    "blur_var": blur_var,
                    "brightness": brightness,
                    "face_size_px": face_size,
                    "face_x1": float(face_bbox[0]),
                    "face_y1": float(face_bbox[1]),
                    "face_x2": float(face_bbox[2]),
                    "face_y2": float(face_bbox[3]),
                    "face_center_x_rel": face.get("association_center_x_rel", np.nan),
                    "face_center_y_rel": face.get("association_center_y_rel", np.nan),
                    "face_person_owner_margin": face.get("association_owner_margin", np.nan),
                    "_face_embedding": face.get("embedding"),
                })
            frame_idx += 1
    finally:
        cap.release()

    rows = apply_tracklet_temporal_face_guard(rows, cfg)
    rows = apply_cross_track_face_collision_guard(rows, cfg)
    meta = pd.DataFrame(rows)
    # Embeddings are transient guard evidence; D5 computes its own ArcFace embeddings from the
    # aligned crops. Never serialize numpy vectors into metadata.csv.
    meta = meta.drop(columns=["_face_embedding"], errors="ignore")
    meta.to_csv(tmp_dir / "metadata.csv", index=False)

    # Replace old D3 only after a complete new D3 has been written.  This avoids leaving a
    # half-built face dataset if the process is interrupted.
    if final_dir.exists():
        shutil.rmtree(final_dir)
    tmp_dir.replace(final_dir)
    return final_dir

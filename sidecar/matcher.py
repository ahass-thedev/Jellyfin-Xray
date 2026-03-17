"""
matcher.py — GPU-accelerated face recognition using InsightFace ArcFace.

FaceMatcher:
  - Uses InsightFace buffalo_l model (RetinaFace detector + ArcFace embedder)
    running via ONNX Runtime with CUDA execution provider on the 5070 Ti.
  - Caches 512-dim ArcFace embeddings per actor reference image.
  - Matches frame faces against actor embeddings by cosine similarity.
"""

from __future__ import annotations

import base64
import hashlib
import io
import logging
import pickle
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

log = logging.getLogger(__name__)

# Cache version tag — bump this to invalidate old dlib/incompatible caches.
_CACHE_VERSION = "if1"

# InsightFace app singleton — initialised on first use.
_face_app = None


def _get_face_app():
    global _face_app
    if _face_app is None:
        from insightface.app import FaceAnalysis
        _face_app = FaceAnalysis(
            name="buffalo_l",
            allowed_modules=["detection", "recognition"],
            providers=["DmlExecutionProvider", "CUDAExecutionProvider", "CPUExecutionProvider"],
        )
        # ctx_id=0 → first GPU; det_size=(640,640) catches small faces well.
        _face_app.prepare(ctx_id=0, det_size=(640, 640))
        log.info("InsightFace buffalo_l loaded (DirectML/CUDA if available, else CPU fallback)")
    return _face_app


class FaceMatcher:
    def __init__(self, cache_dir: Path):
        self._cache_dir = cache_dir
        self._cache_dir.mkdir(parents=True, exist_ok=True)
        self._mem: dict[str, list] = {}

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    def match(
        self,
        frame: np.ndarray,          # RGB H×W×3
        actors: dict[str, str],     # name → base64-encoded reference image
        tolerance: float = 0.55,
        confidence_threshold: float = 0.05,
    ) -> list[str]:
        """
        Detect faces in frame and match against actor embeddings.

        tolerance maps to minimum cosine similarity as (1 − tolerance):
          tolerance=0.55  →  min_sim=0.45  (default, permissive)
          tolerance=0.30  →  min_sim=0.70  (strict)
        """
        app = _get_face_app()

        # InsightFace expects BGR
        frame_bgr = cv2.cvtColor(frame, cv2.COLOR_RGB2BGR)
        faces = app.get(frame_bgr)

        log.info("Frame %dx%d: %d face(s) detected", frame.shape[1], frame.shape[0], len(faces))

        if not faces:
            return []
        if len(faces) > 10:
            log.warning("Skipping frame — %d detections looks like noise", len(faces))
            return []

        # Build known encodings list scoped to this item's cast
        known_names: list[str] = []
        known_encodings: list[np.ndarray] = []
        no_enc = []

        for name, image_b64 in actors.items():
            encs = self._get_encoding(name, image_b64)
            if encs:
                for enc in encs:
                    known_names.append(name)
                    known_encodings.append(enc)
            else:
                no_enc.append(name)

        if no_enc:
            log.warning("No embedding for: %s", ", ".join(no_enc))
        if not known_encodings:
            log.warning("No usable embeddings for any actor — skipping frame")
            return []

        min_sim = 1.0 - tolerance
        matched: set[str] = set()

        for face in faces:
            if face.embedding is None:
                continue

            sims = np.array([_cosine_sim(face.embedding, k) for k in known_encodings])
            best_idx = int(np.argmax(sims))
            best_sim = float(sims[best_idx])
            confidence = _sim_to_confidence(best_sim, min_sim)

            log.info(
                "Best match: '%s' sim=%.3f conf=%.2f (min_sim=%.2f threshold=%.2f)",
                known_names[best_idx], best_sim, confidence, min_sim, confidence_threshold,
            )

            if best_sim < min_sim:
                continue
            if confidence < confidence_threshold:
                continue

            matched.add(known_names[best_idx])
            log.info("*** Matched '%s' sim=%.3f conf=%.2f", known_names[best_idx], best_sim, confidence)

        # Return in original billing order
        return [name for name in actors if name in matched]

    # ------------------------------------------------------------------
    # Embedding cache
    # ------------------------------------------------------------------

    def _get_encoding(self, name: str, image_b64: str) -> list[np.ndarray]:
        cache_key = _cache_key(image_b64)
        mem_key = f"{name}:{cache_key}"

        if mem_key in self._mem:
            return self._mem[mem_key]

        disk_path = self._cache_dir / f"{cache_key}.pkl"
        if disk_path.exists():
            try:
                with open(disk_path, "rb") as f:
                    encs = pickle.load(f)
                self._mem[mem_key] = encs
                return encs
            except Exception as e:
                log.warning("Corrupt cache for '%s', recomputing: %s", name, e)
                disk_path.unlink(missing_ok=True)

        encs = _compute_encoding(name, image_b64)
        if encs:
            with open(disk_path, "wb") as f:
                pickle.dump(encs, f)
        self._mem[mem_key] = encs
        return encs


# ------------------------------------------------------------------
# Module-level helpers
# ------------------------------------------------------------------

def _compute_encoding(name: str, image_b64: str) -> list[np.ndarray]:
    app = _get_face_app()
    try:
        img_bytes = base64.b64decode(image_b64)
        img_rgb = np.array(Image.open(io.BytesIO(img_bytes)).convert("RGB"))
        img_bgr = cv2.cvtColor(img_rgb, cv2.COLOR_RGB2BGR)
    except Exception as e:
        log.error("Failed to decode image for '%s': %s", name, e)
        return []

    faces = app.get(img_bgr)
    if not faces:
        log.warning("No faces detected in reference image for '%s'", name)
        return []

    # Pick the largest detected face (most likely the subject of a headshot)
    face = max(faces, key=lambda f: (f.bbox[2] - f.bbox[0]) * (f.bbox[3] - f.bbox[1]))
    if face.embedding is None:
        return []

    log.debug("Computed ArcFace embedding for '%s'", name)
    return [face.embedding]


def _cosine_sim(a: np.ndarray, b: np.ndarray) -> float:
    """Cosine similarity, normalising vectors explicitly to handle providers that skip L2 norm."""
    na = np.linalg.norm(a)
    nb = np.linalg.norm(b)
    if na == 0 or nb == 0:
        return 0.0
    return float(np.dot(a, b) / (na * nb))


def _sim_to_confidence(similarity: float, min_sim: float) -> float:
    """Map cosine similarity to 0–1 confidence relative to the minimum threshold."""
    if similarity <= min_sim:
        return 0.0
    return (similarity - min_sim) / (1.0 - min_sim)


def _cache_key(image_b64: str) -> str:
    """Cache key = SHA1 of image bytes + version tag. Version bump invalidates old caches."""
    return hashlib.sha1(image_b64.encode()).hexdigest()[:16] + f"_{_CACHE_VERSION}"

"""
matcher.py — face recognition logic for the X-Ray sidecar.

FaceMatcher:
  - Loads and caches face encodings per actor image (keyed by path + mtime)
  - Matches detected faces in a frame against a scoped set of actor encodings
  - Returns actor names whose faces appear in the frame, in billing order
"""

from __future__ import annotations

import base64
import hashlib
import io
import logging
import pickle
from pathlib import Path
from typing import Optional

import face_recognition
import numpy as np
from PIL import Image

log = logging.getLogger(__name__)


class FaceMatcher:
    def __init__(self, cache_dir: Path):
        self._cache_dir = cache_dir
        self._cache_dir.mkdir(parents=True, exist_ok=True)
        self._mem: dict[str, list] = {}  # in-memory LRU not needed at this scale

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    def match(
        self,
        frame: np.ndarray,
        actors: dict[str, str],  # name → image_b64
        tolerance: float = 0.55,
        confidence_threshold: float = 0.60,
    ) -> list[str]:
        """
        Detect faces in frame and match against scoped actor encodings.

        Args:
            frame: RGB numpy array (H×W×3).
            actors: Dict mapping actor name → base64-encoded reference image.
            tolerance: Maximum face distance to count as a match (lower = stricter).
            confidence_threshold: Minimum derived confidence to report a match.

        Returns:
            List of matched actor names, in the same order as `actors`.
        """
        locations = face_recognition.face_locations(frame, model="hog", number_of_times_to_upsample=1)
        log.info("Frame %dx%d: %d face(s) detected", frame.shape[1], frame.shape[0], len(locations))
        if not locations:
            return []

        # Sanity cap — more than 10 detections in a 320x180 tile is noise
        if len(locations) > 10:
            log.warning("Skipping frame — %d detections is almost certainly noise", len(locations))
            return []

        frame_encodings = face_recognition.face_encodings(frame, known_face_locations=locations)
        if not frame_encodings:
            return []

        # Build known encodings list, scoped to this item's cast
        known_names: list[str] = []
        known_encodings: list[np.ndarray] = []

        actors_with_no_encoding = []
        for name, image_b64 in actors.items():
            encs = self._get_encoding(name, image_b64)
            if encs:
                for enc in encs:
                    known_names.append(name)
                    known_encodings.append(enc)
            else:
                actors_with_no_encoding.append(name)

        if actors_with_no_encoding:
            log.warning("No encoding for: %s", ", ".join(actors_with_no_encoding))

        if not known_encodings:
            log.warning("No usable encodings for any actor — skipping frame")
            return []

        matched: set[str] = set()

        for face_enc in frame_encodings:
            distances = face_recognition.face_distance(known_encodings, face_enc)
            best_idx = int(np.argmin(distances))
            best_dist = float(distances[best_idx])
            confidence = _dist_to_confidence(best_dist, tolerance)
            log.info("Best match: '%s' dist=%.3f conf=%.2f (tolerance=%.2f threshold=%.2f)",
                     known_names[best_idx], best_dist, confidence, tolerance, confidence_threshold)

            if best_dist > tolerance:
                continue

            if confidence < confidence_threshold:
                continue

            matched.add(known_names[best_idx])
            log.info("*** Matched '%s' dist=%.3f conf=%.2f", known_names[best_idx], best_dist, confidence)

        # Return in original billing order
        return [name for name in actors if name in matched]

    # ------------------------------------------------------------------
    # Encoding cache
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
    try:
        img_bytes = base64.b64decode(image_b64)
        image = np.array(Image.open(io.BytesIO(img_bytes)).convert("RGB"))
    except Exception as e:
        log.error("Failed to decode image for '%s': %s", name, e)
        return []

    locations = face_recognition.face_locations(image, model="hog", number_of_times_to_upsample=2)
    if not locations:
        log.warning("No faces detected in reference image for '%s'", name)
        return []

    if len(locations) > 1:
        log.debug("Multiple faces in reference image for '%s', using largest", name)
        locations = [_largest_face(locations)]

    encodings = face_recognition.face_encodings(image, known_face_locations=locations)
    log.debug("Computed encoding for '%s'", name)
    return list(encodings)


def _largest_face(locations: list[tuple]) -> tuple:
    def area(loc):
        top, right, bottom, left = loc
        return (bottom - top) * (right - left)
    return max(locations, key=area)


def _dist_to_confidence(distance: float, tolerance: float = 0.55) -> float:
    """
    Map face distance to a 0–1 confidence score relative to tolerance.
    confidence=1.0 when distance=0 (identical), confidence=0.0 when distance>=tolerance.
    """
    if distance >= tolerance:
        return 0.0
    return 1.0 - (distance / tolerance)


def _cache_key(image_b64: str) -> str:
    """Cache key = SHA1 of the image bytes. Automatically invalidates if the image changes."""
    return hashlib.sha1(image_b64.encode()).hexdigest()[:16]

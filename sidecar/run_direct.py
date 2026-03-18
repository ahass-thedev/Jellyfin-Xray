#!/usr/bin/env python3
"""
run_direct.py — Direct video scan X-Ray processor (experimental).

Instead of using pre-generated trickplay sprites (low-res, sparse), this
reads the actual video files directly via OpenCV at full resolution, sampling
every --interval seconds. Yields much better face recognition accuracy at the
cost of longer processing time.

Does NOT require trickplay to be generated. Works on any media item that has
actors and a readable video file.

Output: {output_dir}/{itemId}.json
Format: {"42": ["Tom Hanks", "Robin Wright"], "130": ["Gary Sinise"]}

Usage:
    python run_direct.py \
        --appdata "\\\\10.10.3.18\\appdata\\jellyfin-stack\\jellyfin-config" \
        --data-root "\\\\10.10.3.18\\data" \
        --output-dir "./xray_output" \
        --interval 2 \
        --workers 2 \
        --skip-existing
"""

import argparse
import base64
import json
import logging
import os
import shutil
import sqlite3
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

from matcher import FaceMatcher, _get_face_app

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    stream=sys.stdout,
)
log = logging.getLogger("xray-direct")

_TYPE_MOVIE   = "MediaBrowser.Controller.Entities.Movies.Movie"
_TYPE_EPISODE = "MediaBrowser.Controller.Entities.TV.Episode"
_TYPE_PERSON  = "MediaBrowser.Controller.Entities.Person"


# ---------------------------------------------------------------------------
# Path remapping (same logic as run_batch.py)
# ---------------------------------------------------------------------------

def remap(path: str, appdata: str, data_root: str) -> str:
    if path.startswith("/config/"):
        return os.path.join(appdata, path[len("/config/"):])
    if path.startswith("/data/"):
        return os.path.join(data_root, path[len("/data/"):])
    return path


# ---------------------------------------------------------------------------
# Database helpers — no trickplay join required
# ---------------------------------------------------------------------------

def iter_media_items(conn: sqlite3.Connection):
    """Yield (item_id, name, path) for every movie/episode with a readable path."""
    cur = conn.execute(
        """
        SELECT b.Id, b.Name, b.Path
        FROM BaseItems b
        WHERE b.Type IN (?, ?)
          AND b.IsVirtualItem = 0
          AND b.Path IS NOT NULL
          AND b.Path != ''
        ORDER BY b.Name
        """,
        (_TYPE_MOVIE, _TYPE_EPISODE),
    )
    for row in cur:
        yield str(row[0]), str(row[1]), str(row[2])


def get_actors(conn: sqlite3.Connection, item_id: str) -> list[tuple[str, str]]:
    cur = conn.execute(
        """
        SELECT p.Name, pm.Role
        FROM Peoples p
        JOIN PeopleBaseItemMap pm ON p.Id = pm.PeopleId
        WHERE pm.ItemId = ? AND p.PersonType = 'Actor'
        ORDER BY pm.SortOrder ASC
        """,
        (item_id,),
    )
    return [(str(r[0]), str(r[1] or "")) for r in cur]


def get_actor_image_path(conn: sqlite3.Connection, name: str) -> str | None:
    row = conn.execute(
        """
        SELECT i.Path
        FROM BaseItems bi
        JOIN BaseItemImageInfos i ON bi.Id = i.ItemId
        WHERE bi.Type = ?
          AND bi.Name = ?
          AND i.ImageType = 0
        LIMIT 1
        """,
        (_TYPE_PERSON, name),
    ).fetchone()
    return str(row[0]) if row else None


def image_to_b64(path: str) -> str | None:
    try:
        with open(path, "rb") as f:
            return base64.b64encode(f.read()).decode()
    except Exception as e:
        log.debug("Could not read %s: %s", path, e)
        return None


# ---------------------------------------------------------------------------
# Pre-fetch — resolve all paths and actor images before spawning threads
# ---------------------------------------------------------------------------

def prefetch_items(conn, items, appdata, data_root, output_dir, skip_existing):
    prepared = []
    for item_id, item_name, db_media_path in items:
        guid_clean = item_id.replace("-", "").lower()
        out_path   = output_dir / f"{guid_clean}.json"

        if skip_existing and out_path.exists():
            log.info("SKIP  %s (already done)", item_name)
            continue

        media_path = remap(db_media_path, appdata, data_root)
        if not os.path.exists(media_path):
            log.warning("SKIP  %s — video not found: %s", item_name, media_path)
            continue

        raw_actors = get_actors(conn, item_id)
        if not raw_actors:
            log.warning("SKIP  %s — no actors in DB", item_name)
            continue

        actors: dict[str, str] = {}
        for name, _role in raw_actors:
            db_img = get_actor_image_path(conn, name)
            if not db_img:
                continue
            b64 = image_to_b64(remap(db_img, appdata, data_root))
            if b64:
                actors[name] = b64

        if not actors:
            log.warning("SKIP  %s — no actor images resolved", item_name)
            continue

        prepared.append({
            "item_id":    item_id,
            "item_name":  item_name,
            "out_path":   out_path,
            "media_path": media_path,
            "actors":     actors,
        })

    return prepared


# ---------------------------------------------------------------------------
# Core pipeline — one media item
# ---------------------------------------------------------------------------

def process_item(
    item: dict,
    matcher: FaceMatcher,
    interval: int,
    scale: float,
    tolerance: float,
    confidence: float,
    output_dir: Path,
) -> tuple[str, int]:
    """
    Open the video, sample a frame every `interval` seconds, run face
    recognition on each frame, write results to output_dir.

    scale: resize factor applied before recognition (1.0 = full res,
           0.5 = half res for speed). Values 0.5–1.0 are sensible.
    """
    name       = item["item_name"]
    media_path = item["media_path"]
    actors     = item["actors"]
    out_path   = item["out_path"]

    cap = cv2.VideoCapture(media_path)
    if not cap.isOpened():
        raise RuntimeError(f"Cannot open video: {media_path}")

    try:
        fps          = cap.get(cv2.CAP_PROP_FPS) or 25.0
        total_frames = cap.get(cv2.CAP_PROP_FRAME_COUNT)
        duration_s   = int(total_frames / fps) if fps > 0 else 0

        if duration_s == 0:
            log.warning("SKIP  %s — could not determine duration", name)
            return name, 0

        n_samples = max(1, duration_s // interval)
        log.info(
            "START %s  actors=%d  duration=%ds  interval=%ds  samples=%d  scale=%.1f",
            name, len(actors), duration_s, interval, n_samples, scale,
        )

        xray_data: dict[str, list[str]] = {}

        for t in range(0, duration_s, interval):
            cap.set(cv2.CAP_PROP_POS_MSEC, t * 1000)
            ret, frame_bgr = cap.read()
            if not ret:
                continue

            # Optionally downscale for speed while retaining much higher
            # quality than trickplay thumbnails.
            if scale != 1.0:
                h, w = frame_bgr.shape[:2]
                frame_bgr = cv2.resize(
                    frame_bgr,
                    (int(w * scale), int(h * scale)),
                    interpolation=cv2.INTER_AREA,
                )

            frame_rgb = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2RGB)

            matches = matcher.match(
                frame=frame_rgb,
                actors=actors,
                tolerance=tolerance,
                confidence_threshold=confidence,
            )
            if matches:
                xray_data[str(t)] = matches

    finally:
        cap.release()

    output_dir.mkdir(parents=True, exist_ok=True)
    out_path.write_text(
        json.dumps(xray_data, separators=(",", ":")), encoding="utf-8"
    )
    log.info("DONE  %s  -> %d timestamp entries", name, len(xray_data))
    return name, len(xray_data)


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(
        description="Direct video scan X-Ray — higher accuracy than trickplay approach."
    )
    parser.add_argument(
        "--appdata", required=True,
        help="Jellyfin config root (/config inside container).",
    )
    parser.add_argument(
        "--data-root", required=True,
        help="Jellyfin data/media root (/data inside container).",
    )
    parser.add_argument(
        "--interval", type=int, default=2,
        help="Sample every N seconds (default 2). Lower = more accurate, slower.",
    )
    parser.add_argument(
        "--scale", type=float, default=1.0,
        help="Frame resize factor before recognition (default 1.0 = full res). "
             "0.5 halves width/height for ~4x speedup with minor quality loss.",
    )
    parser.add_argument("--tolerance",  type=float, default=0.55)
    parser.add_argument("--confidence", type=float, default=0.05)
    parser.add_argument("--cache-dir",  default="./cache")
    parser.add_argument(
        "--output-dir", default=None,
        help="Override output dir (default: {appdata}/data/xray).",
    )
    parser.add_argument(
        "--workers", type=int, default=2,
        help="Parallel workers (default 2). Video I/O is a bottleneck so "
             "more than 4 workers rarely helps.",
    )
    parser.add_argument("--skip-existing", action="store_true")
    parser.add_argument(
        "--item", default=None,
        help="Process only this item GUID (for testing).",
    )
    args = parser.parse_args()

    appdata   = args.appdata.rstrip("/\\")
    data_root = args.data_root.rstrip("/\\")
    db_path   = os.path.join(appdata, "data", "jellyfin.db")
    out_dir   = Path(args.output_dir) if args.output_dir else Path(appdata) / "data" / "xray"

    if not os.path.exists(db_path):
        log.error("Database not found: %s", db_path)
        sys.exit(1)

    log.info("Config   : %s", appdata)
    log.info("Data     : %s", data_root)
    log.info("Database : %s", db_path)
    log.info("Output   : %s", out_dir)
    log.info("Interval : %ds", args.interval)
    log.info("Scale    : %.1f", args.scale)
    log.info("Workers  : %d", args.workers)

    tmp      = tempfile.mkdtemp()
    local_db = os.path.join(tmp, "jellyfin.db")
    log.info("Copying DB locally for WAL read...")
    shutil.copy2(db_path, local_db)
    for ext in ("-wal", "-shm"):
        src = db_path + ext
        if os.path.exists(src):
            shutil.copy2(src, local_db + ext)

    conn = sqlite3.connect(local_db)

    try:
        items = list(iter_media_items(conn))
        log.info("Media items in library: %d", len(items))

        if args.item:
            clean = args.item.replace("-", "").lower()
            items = [(g, n, p) for g, n, p in items
                     if g.replace("-", "").lower() == clean]
            if not items:
                log.error("Item %s not found in DB", args.item)
                sys.exit(1)

        log.info("Pre-fetching actor images...")
        prepared = prefetch_items(
            conn, items, appdata, data_root, out_dir, args.skip_existing
        )
        log.info("Items ready to process: %d", len(prepared))

        if not prepared:
            log.info("Nothing to do.")
            return

        log.info("Warming up InsightFace model...")
        _get_face_app()

        matcher = FaceMatcher(cache_dir=Path(args.cache_dir))

        done = failed = 0
        total = len(prepared)

        with ThreadPoolExecutor(max_workers=args.workers) as pool:
            futures = {
                pool.submit(
                    process_item, item, matcher,
                    args.interval, args.scale,
                    args.tolerance, args.confidence, out_dir,
                ): item["item_name"]
                for item in prepared
            }
            for i, future in enumerate(as_completed(futures), 1):
                name = futures[future]
                try:
                    _, n_ts = future.result()
                    done += 1
                    if (i % 5) == 0 or i == total:
                        log.info("Progress: %d/%d  done=%d  failed=%d",
                                 i, total, done, failed)
                except KeyboardInterrupt:
                    log.info("Interrupted")
                    pool.shutdown(wait=False, cancel_futures=True)
                    sys.exit(0)
                except Exception as e:
                    log.error("FAIL  %s: %s", name, e, exc_info=True)
                    failed += 1

        log.info("Finished — done=%d  failed=%d", done, failed)

    finally:
        conn.close()
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    main()

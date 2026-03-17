#!/usr/bin/env python3
"""
run_batch.py — Standalone X-Ray batch processor.

Reads Jellyfin's jellyfin.db directly (no HTTP API), runs InsightFace on every
trickplay frame using the local GPU, and writes X-Ray JSON results that the
plugin serves.

Output: {output_dir}/{itemId}.json
Format: {"42": ["Tom Hanks", "Robin Wright"], "130": ["Gary Sinise"]}

Usage (Unraid / Docker setup where paths inside the container differ from the host):
    python run_batch.py \
        --appdata "\\\\10.10.3.18\\appdata\\jellyfin-stack\\jellyfin-config" \
        --data-root "\\\\10.10.3.18\\data" \
        --output-dir "./xray_output" \
        --workers 4 \
        --skip-existing
"""

import argparse
import base64
import json
import logging
import os
import queue
import sqlite3
import sys
import threading
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

import numpy as np
from PIL import Image

from matcher import FaceMatcher, _get_face_app

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    stream=sys.stdout,
)
log = logging.getLogger("xray-batch")

_TYPE_MOVIE   = "MediaBrowser.Controller.Entities.Movies.Movie"
_TYPE_EPISODE = "MediaBrowser.Controller.Entities.TV.Episode"
_TYPE_PERSON  = "MediaBrowser.Controller.Entities.Person"


# ---------------------------------------------------------------------------
# Path remapping
# ---------------------------------------------------------------------------

def remap(path: str, appdata: str, data_root: str) -> str:
    if path.startswith("/config/"):
        return os.path.join(appdata, path[len("/config/"):])
    if path.startswith("/data/"):
        return os.path.join(data_root, path[len("/data/"):])
    return path


# ---------------------------------------------------------------------------
# Database helpers
# ---------------------------------------------------------------------------

def iter_media_items(conn: sqlite3.Connection):
    cur = conn.execute(
        """
        SELECT b.Id, b.Name, b.Path
        FROM BaseItems b
        JOIN TrickplayInfos t ON t.ItemId = b.Id
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


def get_trickplay_info(conn: sqlite3.Connection, item_id: str):
    row = conn.execute(
        """
        SELECT TileWidth, TileHeight, Interval
        FROM TrickplayInfos
        WHERE ItemId = ?
        ORDER BY Width DESC
        LIMIT 1
        """,
        (item_id,),
    ).fetchone()
    if row is None:
        return None
    cols, rows, interval_ms = int(row[0]), int(row[1]), int(row[2])
    return cols, rows, max(1, interval_ms // 1000)


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


# ---------------------------------------------------------------------------
# Trickplay sprite helpers
# ---------------------------------------------------------------------------

def find_trickplay_dir(media_path: str, cols: int, rows: int) -> str | None:
    media_dir  = os.path.dirname(media_path)
    media_name = os.path.splitext(os.path.basename(media_path))[0]
    tp_root    = os.path.join(media_dir, media_name + ".trickplay")

    if not os.path.isdir(tp_root):
        return None

    best_width, best_path = -1, None
    for entry in os.scandir(tp_root):
        if not entry.is_dir():
            continue
        dash = entry.name.find(" - ")
        if dash < 0:
            continue
        try:
            width = int(entry.name[:dash])
            grid  = entry.name[dash + 3:]
            xi    = grid.index("x")
            c, r  = int(grid[:xi]), int(grid[xi + 1:])
        except (ValueError, IndexError):
            continue
        if c == cols and r == rows and width > best_width:
            best_width, best_path = width, entry.path

    return best_path


def iter_sprite_frames(sprite_path: str, cols: int, rows: int,
                       base_second: int, interval: int):
    img = np.array(Image.open(sprite_path).convert("RGB"))
    h, w = img.shape[:2]
    th, tw = h // rows, w // cols
    for row in range(rows):
        for col in range(cols):
            ts = base_second + (row * cols + col) * interval
            y0, y1 = row * th, min((row + 1) * th, h)
            x0, x1 = col * tw, min((col + 1) * tw, w)
            yield ts, img[y0:y1, x0:x1]


# ---------------------------------------------------------------------------
# Image helpers
# ---------------------------------------------------------------------------

def image_to_b64(path: str) -> str | None:
    try:
        with open(path, "rb") as f:
            return base64.b64encode(f.read()).decode()
    except Exception as e:
        log.debug("Could not read %s: %s", path, e)
        return None


# ---------------------------------------------------------------------------
# Pre-fetch all item data from the DB (single-threaded, before parallelism)
# ---------------------------------------------------------------------------

def prefetch_items(conn, items, appdata, data_root, output_dir, skip_existing):
    """
    Build a list of ready-to-process dicts.  All DB and filesystem work
    (actor images, sprite dir scan) is done here so workers are pure compute.
    """
    prepared = []
    for item_id, item_name, db_media_path in items:
        guid_clean = item_id.replace("-", "").lower()
        out_path   = output_dir / f"{guid_clean}.json"

        if skip_existing and out_path.exists():
            log.info("SKIP  %s (already done)", item_name)
            continue

        tp = get_trickplay_info(conn, item_id)
        if tp is None:
            continue
        cols, rows, interval = tp

        media_path = remap(db_media_path, appdata, data_root)
        sprite_dir = find_trickplay_dir(media_path, cols, rows)
        if sprite_dir is None:
            log.warning("SKIP  %s — trickplay dir not found", item_name)
            continue

        sprite_files = sorted(
            (f for f in os.listdir(sprite_dir) if f.endswith(".jpg")),
            key=lambda f: int(os.path.splitext(f)[0]),
        )
        if not sprite_files:
            log.warning("SKIP  %s — no sprite files", item_name)
            continue

        raw_actors = get_actors(conn, item_id)
        if not raw_actors:
            log.warning("SKIP  %s — no actors", item_name)
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
            "sprite_dir": sprite_dir,
            "sprite_files": sprite_files,
            "cols":       cols,
            "rows":       rows,
            "interval":   interval,
            "actors":     actors,
        })

    return prepared


# ---------------------------------------------------------------------------
# Core pipeline — one media item (called from worker threads)
# ---------------------------------------------------------------------------

def process_prepared(item: dict, matcher: FaceMatcher,
                     tolerance: float, confidence: float,
                     output_dir: Path) -> tuple[str, int]:
    """
    Process one pre-fetched item.  Returns (item_name, n_timestamps).
    Raises on error so the caller can count failures.
    """
    name         = item["item_name"]
    sprite_dir   = item["sprite_dir"]
    sprite_files = item["sprite_files"]
    cols, rows   = item["cols"], item["rows"]
    interval     = item["interval"]
    actors       = item["actors"]
    out_path     = item["out_path"]

    tiles_per_sprite = cols * rows
    log.info("START %s  actors=%d  sprites=%d  grid=%dx%d",
             name, len(actors), len(sprite_files), cols, rows)

    xray_data: dict[str, list[str]] = {}

    for i, sf in enumerate(sprite_files):
        base_sec = i * tiles_per_sprite * interval
        for timestamp, frame_rgb in iter_sprite_frames(
                os.path.join(sprite_dir, sf), cols, rows, base_sec, interval):
            matches = matcher.match(
                frame=frame_rgb,
                actors=actors,
                tolerance=tolerance,
                confidence_threshold=confidence,
            )
            if matches:
                xray_data[str(timestamp)] = matches

    output_dir.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(xray_data, separators=(",", ":")), encoding="utf-8")
    log.info("DONE  %s  -> %d timestamp entries", name, len(xray_data))
    return name, len(xray_data)


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(
        description="Batch X-Ray — reads Jellyfin DB, runs InsightFace on GPU, writes results."
    )
    parser.add_argument(
        "--appdata", required=True,
        help="Jellyfin config root (/config inside container).",
    )
    parser.add_argument(
        "--data-root", required=True,
        help="Jellyfin data/media root (/data inside container).",
    )
    parser.add_argument("--tolerance",  type=float, default=0.55)
    parser.add_argument("--confidence", type=float, default=0.05)
    parser.add_argument("--cache-dir",  default="./cache")
    parser.add_argument("--output-dir", default=None,
                        help="Override output dir (default: {appdata}/data/xray).")
    parser.add_argument("--workers",    type=int, default=4,
                        help="Parallel worker threads (default 4). "
                             "Higher values saturate the GPU better.")
    parser.add_argument("--skip-existing", action="store_true")
    parser.add_argument("--item", default=None,
                        help="Process only this item GUID (for testing).")
    args = parser.parse_args()

    appdata   = args.appdata.rstrip("/\\")
    data_root = args.data_root.rstrip("/\\")
    db_path   = os.path.join(appdata, "data", "jellyfin.db")
    out_dir   = Path(args.output_dir) if args.output_dir else Path(appdata) / "data" / "xray"

    if not os.path.exists(db_path):
        log.error("Database not found: %s", db_path)
        sys.exit(1)

    log.info("Config  : %s", appdata)
    log.info("Data    : %s", data_root)
    log.info("Database: %s", db_path)
    log.info("Output  : %s", out_dir)
    log.info("Workers : %d", args.workers)

    import shutil, tempfile
    tmp = tempfile.mkdtemp()
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
        log.info("Items with trickplay: %d", len(items))

        if args.item:
            clean = args.item.replace("-", "").lower()
            items = [(g, n, p) for g, n, p in items
                     if g.replace("-", "").lower() == clean]
            if not items:
                log.error("Item %s not found", args.item)
                sys.exit(1)

        log.info("Pre-fetching item data (actor images, sprite dirs)...")
        prepared = prefetch_items(conn, items, appdata, data_root,
                                  out_dir, args.skip_existing)
        log.info("Items ready to process: %d", len(prepared))

        # Warm up InsightFace model before spawning threads so the lazy
        # singleton is initialised once in the main thread.
        log.info("Warming up InsightFace model...")
        _get_face_app()

        matcher = FaceMatcher(cache_dir=Path(args.cache_dir))

        done = skipped_pre = failed = 0
        skipped_pre = len(items) - len(prepared)
        total = len(prepared)

        with ThreadPoolExecutor(max_workers=args.workers) as pool:
            futures = {
                pool.submit(
                    process_prepared, item, matcher,
                    args.tolerance, args.confidence, out_dir
                ): item["item_name"]
                for item in prepared
            }
            for i, future in enumerate(as_completed(futures), 1):
                name = futures[future]
                try:
                    _, n_ts = future.result()
                    done += 1
                    if (i % 10) == 0 or i == total:
                        log.info("Progress: %d/%d  done=%d  failed=%d",
                                 i, total, done, failed)
                except KeyboardInterrupt:
                    log.info("Interrupted")
                    pool.shutdown(wait=False, cancel_futures=True)
                    sys.exit(0)
                except Exception as e:
                    log.error("FAIL  %s: %s", name, e, exc_info=True)
                    failed += 1

        log.info("Finished — done=%d  skipped=%d  failed=%d",
                 done, skipped_pre, failed)
    finally:
        conn.close()
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    main()

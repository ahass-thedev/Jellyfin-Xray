#!/usr/bin/env python3
"""
run_batch.py — Standalone X-Ray batch processor.

Reads Jellyfin's jellyfin.db directly (no HTTP API), runs InsightFace on every
trickplay frame using the local GPU, and writes X-Ray JSON results that the
plugin serves.

Output: {appdata}/data/xray/{itemId}.json
Format: {"42": ["Tom Hanks", "Robin Wright"], "130": ["Gary Sinise"]}

Usage (Unraid / Docker setup where paths inside the container differ from the host):
    python run_batch.py \
        --appdata "\\\\10.10.3.18\\appdata\\jellyfin-stack\\jellyfin-config" \
        --data-root "\\\\10.10.3.18\\data" \
        --skip-existing
"""

import argparse
import base64
import json
import logging
import os
import sqlite3
import sys
from pathlib import Path

import numpy as np
from PIL import Image

from matcher import FaceMatcher

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
# Container stores absolute paths like /data/... and /config/...
# We remap them to SMB/local equivalents at runtime.
# ---------------------------------------------------------------------------

def remap(path: str, appdata: str, data_root: str) -> str:
    """
    Remap container-internal paths to host-accessible paths.
      /config/... → {appdata}/...
      /data/...   → {data_root}/...
    """
    if path.startswith("/config/"):
        return os.path.join(appdata, path[len("/config/"):])
    if path.startswith("/data/"):
        return os.path.join(data_root, path[len("/data/"):])
    return path


# ---------------------------------------------------------------------------
# Database helpers
# ---------------------------------------------------------------------------

def iter_media_items(conn: sqlite3.Connection):
    """Yield (id, name, path) for all movies and episodes that have trickplay."""
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
    """Return [(name, role), ...] for actors in billing order."""
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
    """Return (cols, rows, interval_seconds) from TrickplayInfos, highest-width tier."""
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
    interval_sec = max(1, interval_ms // 1000)
    return cols, rows, interval_sec


def get_actor_image_path(conn: sqlite3.Connection, name: str) -> str | None:
    """Get the Primary image path for a person (stored in BaseItems)."""
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
    """Find the trickplay sprite directory next to the media file."""
    media_dir = os.path.dirname(media_path)
    media_name = os.path.splitext(os.path.basename(media_path))[0]
    tp_root = os.path.join(media_dir, media_name + ".trickplay")

    if not os.path.isdir(tp_root):
        return None

    # Pick the highest-width tier matching the DB cols/rows
    best_width = -1
    best_path = None
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
            c     = int(grid[:xi])
            r     = int(grid[xi + 1:])
        except (ValueError, IndexError):
            continue
        if c == cols and r == rows and width > best_width:
            best_width = width
            best_path  = entry.path

    return best_path


def iter_sprite_frames(sprite_path: str, cols: int, rows: int,
                       base_second: int, interval: int):
    """Yield (timestamp_seconds, rgb_ndarray) for every tile in a sprite sheet."""
    img = np.array(Image.open(sprite_path).convert("RGB"))
    h, w = img.shape[:2]
    th, tw = h // rows, w // cols

    for row in range(rows):
        for col in range(cols):
            ts  = base_second + (row * cols + col) * interval
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
# Core pipeline — one media item
# ---------------------------------------------------------------------------

def process_item(
    conn:       sqlite3.Connection,
    matcher:    FaceMatcher,
    item_id:    str,
    item_name:  str,
    media_path: str,        # host-remapped path
    output_dir: Path,
    cols:       int,
    rows:       int,
    interval:   int,
    appdata:    str,
    data_root:  str,
    tolerance:  float,
    confidence: float,
    skip_existing: bool,
) -> bool:
    guid_clean = item_id.replace("-", "").lower()
    out_path   = output_dir / f"{guid_clean}.json"

    if skip_existing and out_path.exists():
        log.info("SKIP  %s (already done)", item_name)
        return False

    # Actors + their images
    raw_actors = get_actors(conn, item_id)
    if not raw_actors:
        log.warning("SKIP  %s — no actors", item_name)
        return False

    actors: dict[str, str] = {}
    for name, _role in raw_actors:
        db_path = get_actor_image_path(conn, name)
        if not db_path:
            continue
        host_path = remap(db_path, appdata, data_root)
        b64 = image_to_b64(host_path)
        if b64:
            actors[name] = b64

    if not actors:
        log.warning("SKIP  %s — no actor images resolved", item_name)
        return False

    # Trickplay sprites
    sprite_dir = find_trickplay_dir(media_path, cols, rows)
    if sprite_dir is None:
        log.warning("SKIP  %s — trickplay dir not found at %s", item_name, media_path)
        return False

    sprite_files = sorted(
        (f for f in os.listdir(sprite_dir) if f.endswith(".jpg")),
        key=lambda f: int(os.path.splitext(f)[0]),
    )
    if not sprite_files:
        log.warning("SKIP  %s — no sprite files in %s", item_name, sprite_dir)
        return False

    tiles_per_sprite = cols * rows
    log.info("START %s  actors=%d  sprites=%d  grid=%dx%d",
             item_name, len(actors), len(sprite_files), cols, rows)

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
    log.info("DONE  %s  → %d timestamp entries", item_name, len(xray_data))
    return True


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(
        description="Batch X-Ray — reads Jellyfin DB, runs InsightFace on GPU, writes results."
    )
    parser.add_argument(
        "--appdata", required=True,
        help="Jellyfin config root (/config inside container). "
             r"E.g. \\10.10.3.18\appdata\jellyfin-stack\jellyfin-config",
    )
    parser.add_argument(
        "--data-root", required=True,
        help="Jellyfin data/media root (/data inside container). "
             r"E.g. \\10.10.3.18\data",
    )
    parser.add_argument("--tolerance",  type=float, default=0.55)
    parser.add_argument("--confidence", type=float, default=0.05)
    parser.add_argument("--cache-dir",  default="./cache")
    parser.add_argument("--skip-existing", action="store_true",
                        help="Skip items that already have an X-Ray JSON file")
    parser.add_argument("--item", default=None,
                        help="Process only this item GUID (for testing)")
    args = parser.parse_args()

    appdata   = args.appdata.rstrip("/\\")
    data_root = args.data_root.rstrip("/\\")
    db_path   = os.path.join(appdata, "data", "jellyfin.db")
    out_dir   = Path(appdata) / "data" / "xray"

    if not os.path.exists(db_path):
        log.error("Database not found: %s", db_path)
        sys.exit(1)

    log.info("Config  : %s", appdata)
    log.info("Data    : %s", data_root)
    log.info("Database: %s", db_path)
    log.info("Output  : %s", out_dir)

    # Copy DB locally to avoid WAL-over-SMB issues, then open read-only
    import shutil, tempfile
    tmp = tempfile.mkdtemp()
    local_db = os.path.join(tmp, "jellyfin.db")
    log.info("Copying DB to %s for WAL read...", tmp)
    shutil.copy2(db_path, local_db)
    for ext in ("-wal", "-shm"):
        src = db_path + ext
        if os.path.exists(src):
            shutil.copy2(src, local_db + ext)

    matcher = FaceMatcher(cache_dir=Path(args.cache_dir))
    conn    = sqlite3.connect(local_db)

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

        done = skipped = failed = 0
        for item_id, item_name, db_media_path in items:
            media_path = remap(db_media_path, appdata, data_root)
            tp = get_trickplay_info(conn, item_id)
            if tp is None:
                skipped += 1
                continue
            cols, rows, interval = tp
            try:
                ok = process_item(
                    conn, matcher,
                    item_id, item_name, media_path, out_dir,
                    cols, rows, interval,
                    appdata, data_root,
                    tolerance=args.tolerance,
                    confidence=args.confidence,
                    skip_existing=args.skip_existing,
                )
                if ok: done += 1
                else:  skipped += 1
            except KeyboardInterrupt:
                log.info("Interrupted — done=%d skipped=%d failed=%d", done, skipped, failed)
                sys.exit(0)
            except Exception as e:
                log.error("FAIL  %s: %s", item_name, e, exc_info=True)
                failed += 1

        log.info("Finished — done=%d  skipped=%d  failed=%d", done, skipped, failed)
    finally:
        conn.close()
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    main()

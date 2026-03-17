#!/usr/bin/env python3
"""
run_batch.py — Standalone X-Ray batch processor.

Reads Jellyfin's library.db directly (no HTTP API), runs InsightFace on every
trickplay frame, and writes X-Ray JSON data files that the plugin serves.

Output: {appdata}/data/xray/{itemId}.json
Format: {"42": ["Tom Hanks", "Robin Wright"], "130": ["Gary Sinise"]}

Usage:
    python run_batch.py --appdata "\\\\10.10.3.18\\appdata"
    python run_batch.py --appdata Z:\\JellyfinData --skip-existing
    python run_batch.py --appdata Z:\\JellyfinData --item <guid>
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

# Jellyfin item type names
_MOVIE   = "MediaBrowser.Controller.Entities.Movies.Movie"
_EPISODE = "MediaBrowser.Controller.Entities.TV.Episode"


# ---------------------------------------------------------------------------
# Database helpers
# ---------------------------------------------------------------------------

def iter_media_items(conn: sqlite3.Connection):
    """Yield (guid, name, path) for all movies and episodes."""
    cur = conn.execute(
        """
        SELECT guid, Name, Path
        FROM TypedBaseItems
        WHERE type IN (?, ?)
          AND IsVirtualItem = 0
          AND Path IS NOT NULL
          AND Path != ''
        ORDER BY Name
        """,
        (_MOVIE, _EPISODE),
    )
    for row in cur:
        yield str(row[0]), str(row[1]), str(row[2])


def get_actors(conn: sqlite3.Connection, item_guid: str) -> list[tuple[str, str]]:
    """Return [(name, role), ...] for actors in billing order."""
    cur = conn.execute(
        """
        SELECT Name, Role
        FROM People
        WHERE ItemId = ? AND PersonType = 'Actor'
        ORDER BY SortOrder ASC
        """,
        (item_guid,),
    )
    return [(str(r[0]), str(r[1] or "")) for r in cur]


# ---------------------------------------------------------------------------
# Actor image helpers
# ---------------------------------------------------------------------------

def find_actor_image(appdata: Path, name: str) -> Path | None:
    """
    Find a person's primary image in Jellyfin's standard metadata layout:
      {appdata}/metadata/People/{firstChar}/{name}/folder.jpg
    Also tries lowercase 'people' for case-sensitive filesystems.
    """
    if not name:
        return None
    first = name[0].upper()
    candidates = [
        appdata / "metadata" / "People" / first / name / "folder.jpg",
        appdata / "metadata" / "people" / first / name / "folder.jpg",
    ]
    for p in candidates:
        if p.exists():
            return p
    return None


def image_to_b64(path: Path) -> str | None:
    try:
        return base64.b64encode(path.read_bytes()).decode()
    except Exception as e:
        log.debug("Could not read image %s: %s", path, e)
        return None


# ---------------------------------------------------------------------------
# Trickplay helpers
# ---------------------------------------------------------------------------

def find_trickplay(media_path: str) -> tuple[str, int, int] | None:
    """
    Returns (sprite_directory, cols, rows) for the highest-resolution tier,
    matching Jellyfin's "{name}.trickplay/{width} - {cols}x{rows}/" layout.
    Returns None if no trickplay exists.
    """
    media_dir = os.path.dirname(media_path)
    media_name = os.path.splitext(os.path.basename(media_path))[0]
    tp_root = os.path.join(media_dir, media_name + ".trickplay")

    if not os.path.isdir(tp_root):
        return None

    best_width = -1
    best: tuple[str, int, int] | None = None

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
            cols  = int(grid[:xi])
            rows  = int(grid[xi + 1:])
        except (ValueError, IndexError):
            continue
        if width > best_width:
            best_width = width
            best = (entry.path, cols, rows)

    return best


def iter_sprite_frames(sprite_path: str, cols: int, rows: int,
                       base_second: int, interval: int):
    """Yield (timestamp_seconds, rgb_ndarray) for every tile in a sprite sheet."""
    img = np.array(Image.open(sprite_path).convert("RGB"))
    h, w = img.shape[:2]
    th, tw = h // rows, w // cols

    for row in range(rows):
        for col in range(cols):
            idx = row * cols + col
            ts  = base_second + idx * interval
            y0, y1 = row * th, min((row + 1) * th, h)
            x0, x1 = col * tw, min((col + 1) * tw, w)
            yield ts, img[y0:y1, x0:x1]


# ---------------------------------------------------------------------------
# Core pipeline — one media item
# ---------------------------------------------------------------------------

def process_item(
    conn:       sqlite3.Connection,
    matcher:    FaceMatcher,
    appdata:    Path,
    item_guid:  str,
    item_name:  str,
    media_path: str,
    output_dir: Path,
    interval:   int,
    tolerance:  float,
    confidence: float,
    skip_existing: bool,
) -> bool:
    # Output path uses no-dash lowercase GUID, matching XRayStore.FilePath()
    guid_clean = item_guid.replace("-", "").lower()
    out_path   = output_dir / f"{guid_clean}.json"

    if skip_existing and out_path.exists():
        log.info("SKIP  %s (already analysed)", item_name)
        return False

    # Actors + their images
    raw_actors = get_actors(conn, item_guid)
    if not raw_actors:
        log.warning("SKIP  %s — no actors in library", item_name)
        return False

    actors: dict[str, str] = {}     # name → image_b64
    for name, _role in raw_actors:
        img_path = find_actor_image(appdata, name)
        if img_path is None:
            continue
        b64 = image_to_b64(img_path)
        if b64:
            actors[name] = b64

    if not actors:
        log.warning("SKIP  %s — no actor images found in metadata", item_name)
        return False

    # Trickplay
    tp = find_trickplay(media_path)
    if tp is None:
        log.warning("SKIP  %s — no trickplay directory", item_name)
        return False

    sprite_dir, cols, rows = tp
    tiles_per_sprite = cols * rows

    sprite_files = sorted(
        (f for f in os.listdir(sprite_dir) if f.endswith(".jpg")),
        key=lambda f: int(os.path.splitext(f)[0]),
    )
    if not sprite_files:
        log.warning("SKIP  %s — empty trickplay directory", item_name)
        return False

    log.info("START %s  actors=%d  sprites=%d", item_name, len(actors), len(sprite_files))

    xray_data: dict[str, list[str]] = {}

    for i, sprite_file in enumerate(sprite_files):
        sprite_path = os.path.join(sprite_dir, sprite_file)
        base_second = i * tiles_per_sprite * interval

        for timestamp, frame_rgb in iter_sprite_frames(sprite_path, cols, rows, base_second, interval):
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
        description="Batch X-Ray analysis — reads Jellyfin DB, runs InsightFace, writes results."
    )
    parser.add_argument(
        "--appdata", required=True,
        help=(
            "Jellyfin data root (contains data/library.db and metadata/). "
            r"E.g. \\10.10.3.18\appdata  or  Z:\JellyfinData"
        ),
    )
    parser.add_argument(
        "--interval", type=int, default=10,
        help="Trickplay interval seconds — must match Jellyfin's setting (default: 10)",
    )
    parser.add_argument(
        "--tolerance", type=float, default=0.55,
        help="Face match tolerance 0–1, lower = stricter (default: 0.55)",
    )
    parser.add_argument(
        "--confidence", type=float, default=0.05,
        help="Min confidence above tolerance boundary (default: 0.05)",
    )
    parser.add_argument(
        "--cache-dir", default="./cache",
        help="Directory for face encoding cache (default: ./cache)",
    )
    parser.add_argument(
        "--skip-existing", action="store_true",
        help="Skip items that already have an X-Ray JSON file",
    )
    parser.add_argument(
        "--item", default=None,
        help="Process only this specific item GUID (useful for testing)",
    )
    args = parser.parse_args()

    appdata    = Path(args.appdata)
    db_path    = appdata / "data" / "library.db"
    output_dir = appdata / "data" / "xray"

    if not db_path.exists():
        log.error("Jellyfin database not found: %s", db_path)
        sys.exit(1)

    log.info("Appdata : %s", appdata)
    log.info("Database: %s", db_path)
    log.info("Output  : %s", output_dir)

    cache_path = Path(args.cache_dir)
    cache_path.mkdir(parents=True, exist_ok=True)
    matcher = FaceMatcher(cache_dir=cache_path)

    conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)  # read-only
    try:
        items = list(iter_media_items(conn))
        log.info("Library contains %d analysable items", len(items))

        if args.item:
            clean = args.item.replace("-", "").lower()
            items = [(g, n, p) for g, n, p in items
                     if g.replace("-", "").lower() == clean]
            if not items:
                log.error("Item %s not found in library", args.item)
                sys.exit(1)

        done = skipped = failed = 0
        for item_guid, item_name, media_path in items:
            try:
                ok = process_item(
                    conn, matcher, appdata,
                    item_guid, item_name, media_path,
                    output_dir=output_dir,
                    interval=args.interval,
                    tolerance=args.tolerance,
                    confidence=args.confidence,
                    skip_existing=args.skip_existing,
                )
                if ok:
                    done += 1
                else:
                    skipped += 1
            except KeyboardInterrupt:
                log.info("Interrupted — processed=%d skipped=%d failed=%d", done, skipped, failed)
                sys.exit(0)
            except Exception as e:
                log.error("FAIL  %s: %s", item_name, e, exc_info=True)
                failed += 1

        log.info("Finished — processed=%d  skipped=%d  failed=%d", done, skipped, failed)

    finally:
        conn.close()


if __name__ == "__main__":
    main()

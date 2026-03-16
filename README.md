# Jellyfin X-Ray Plugin

Shows actor cards in the Jellyfin video player as you watch, identifying which cast members are on screen — similar to Amazon Prime Video X-Ray.

## How it works

1. A **C# Jellyfin plugin** handles all Jellyfin integration: reading library metadata, scheduled tasks, the player overlay, and serving results.
2. A **Python sidecar** runs alongside Jellyfin and does the face recognition using the `face_recognition` library.

The plugin communicates with the sidecar over localhost HTTP. The sidecar has one job: given a video frame and a list of actor images, return which actors appear in the frame.

---

## Requirements

- Jellyfin 10.9+
- Python 3.10+ with `pip`
- `cmake` (required by `dlib`, which `face_recognition` depends on)
- Trickplay generation enabled in Jellyfin (Dashboard → Playback → Trickplay)

---

## Installation

### Step 1 — Install the plugin

1. In Jellyfin: **Dashboard → Plugins → Repositories → +**
2. Add repository URL:
   ```
   https://raw.githubusercontent.com/ahass-thedev/jellyfin-plugin-xray/main/manifest.json
   ```
3. **Dashboard → Plugins → Catalogue → X-Ray → Install**
4. Restart Jellyfin.

### Step 2 — Set up the Python sidecar

Install system dependencies:
```bash
# Debian / Ubuntu
sudo apt install cmake build-essential python3-dev

# macOS
brew install cmake
```

Clone the repo and install Python dependencies:
```bash
git clone https://github.com/ahass-thedev/jellyfin-plugin-xray
cd jellyfin-plugin-xray/sidecar
pip install -r requirements.txt
```

Copy the sidecar to the Jellyfin plugin data directory:
```bash
# Default Jellyfin data path — adjust if yours differs
sudo cp -r sidecar/ /var/lib/jellyfin/data/xray-sidecar/
```

### Step 3 — Run the sidecar

**Option A: Let the plugin manage it (recommended)**

The plugin will start `sidecar/main.py` automatically on Jellyfin startup.
No extra steps needed. Check plugin settings to set your Python path if needed.

**Option B: Run as a systemd service (more robust)**

```ini
# /etc/systemd/system/jellyfin-xray-sidecar.service
[Unit]
Description=Jellyfin X-Ray face recognition sidecar
After=network.target

[Service]
Type=simple
User=jellyfin
WorkingDirectory=/var/lib/jellyfin/data/xray-sidecar
ExecStart=/usr/bin/python3 main.py --cache-dir /var/lib/jellyfin/cache/xray-encodings
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now jellyfin-xray-sidecar
```

If using systemd, uncheck **Auto-start sidecar** in plugin settings.

### Step 4 — Analyse your library

1. In Jellyfin: **Dashboard → Scheduled Tasks → X-Ray: Analyse Library → Run now**
2. Analysis runs in the background. Large libraries take time — check the Jellyfin log for progress.
3. Once analysis is done for an item, actor cards will appear automatically during playback.

---

## Plugin settings

| Setting | Default | Description |
|---------|---------|-------------|
| Sidecar URL | `http://localhost:8756` | Address of the Python sidecar |
| Auto-start sidecar | On | Plugin manages sidecar process lifecycle |
| Python path | *(system)* | Path to python3 — leave blank to use PATH |
| Match tolerance | `0.55` | Lower = stricter, fewer false positives |
| Confidence threshold | `0.60` | Minimum confidence to show a match |
| Trickplay interval | `10` | Must match Jellyfin's trickplay setting |
| Overlay enabled | On | Show actor cards in the player |
| Max actors displayed | `4` | Maximum number of cards at once |

---

## Troubleshooting

**No actor cards appear during playback**
- Check that analysis has run: Dashboard → Scheduled Tasks → X-Ray: Analyse Library
- Check Jellyfin logs for `[X-Ray]` entries
- Check that trickplay is enabled and generated for the item (scrub the video — if thumbnails appear, trickplay is working)

**Sidecar unreachable**
- Plugin settings → Check sidecar button should show the status
- Check that port 8756 is not in use: `ss -tlnp | grep 8756`
- If using systemd: `journalctl -u jellyfin-xray-sidecar -f`

**Wrong actors / false positives**
- Lower Match tolerance (e.g. 0.50) and raise Confidence threshold (e.g. 0.65)
- Check that Jellyfin has good headshot images for actors (Dashboard → People)

**`dlib` fails to install**
- Make sure `cmake` is installed before `pip install face_recognition`
- On some systems: `pip install dlib --verbose` to see the full error

---

## Architecture

```
Jellyfin process
└── Jellyfin.Plugin.XRay (C#)
    ├── Plugin.cs              — entry point, DI registration, sidecar lifecycle
    ├── Configuration/         — typed config + dashboard settings page
    ├── ScheduledTasks/        — "Analyse Library" task (IScheduledTask)
    ├── Services/
    │   ├── MetadataService    — cast + trickplay paths via ILibraryManager
    │   ├── XRayService        — analysis orchestration
    │   ├── SidecarClient      — HTTP client to Python sidecar
    │   ├── SidecarManager     — starts/stops sidecar process
    │   └── XRayStore          — reads/writes per-item JSON
    ├── Api/
    │   └── XRayController     — GET /XRay/query, GET /XRay/overlay.js
    └── ClientScript/
        └── xray-overlay.js    — embedded JS injected into the player

Python sidecar (separate process)
└── main.py    — FastAPI app, POST /match endpoint
└── matcher.py — face_recognition logic + encoding cache
```

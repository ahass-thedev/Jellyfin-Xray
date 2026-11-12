# X-Ray for Jellyfin

Amazon-style X-Ray for movies and episodes: who is in the current scene, and which song is
playing. Everything runs inside Jellyfin as one plugin. There is no sidecar container.

## Install

1. In Jellyfin (12.3 or newer), go to Dashboard → Plugins → Repositories and add
   `https://raw.githubusercontent.com/ahass-thedev/Jellyfin-Xray/main/manifest.json`.
2. Install **X-Ray** from the catalogue, then restart Jellyfin. That's all: the player overlay
   works out of the box. If you already use the File Transformation plugin (Jellyfin Enhanced
   and others need it), X-Ray shares it instead of using its bundled copy.

It runs on Linux x64, Raspberry Pi 4/5 with a 64-bit OS (linux-arm64), Windows (x64 and ARM) and
Apple Silicon Macs. 32-bit Raspberry Pi OS and Intel Macs can show existing data, but can't analyse.
Analysis runs on the CPU by default: a film takes 10–20 minutes on a modern x64 server, and much
longer on a Pi. With an Intel GPU (Linux or Windows) or on Apple Silicon, set **Run face recognition
on** and **Run voice recognition on** to GPU for a large speed-up (see Settings).

**Which titles have X-Ray:** Dashboard → *X-Ray titles* (also linked from the settings page) lists
every movie and episode X-Ray covers: ready, to be refreshed, not done yet, in progress or failed,
with counts, a search box and an *Analyse now* button. Once the server shares X-Ray data it also
shows which titles were sent to the sharing service (or are waiting to be) and which were pulled
from it.

**Releasing:** bump `AssemblyVersion` in the csproj within the MR. When it merges to `main`, CI
publishes that version as a GitHub release and updates the manifest. Merges that don't bump the
version publish nothing new. The commit description becomes the changelog.

## How it works

**Faces.** For each title, frames are sampled (1 per second by default) through Jellyfin's own
ffmpeg. They are decoded on the GPU with VAAPI when available, otherwise on the CPU.

1. **Detect.** SCRFD finds faces and five landmarks per face.
2. **Track.** Faces are linked frame to frame within a shot by box overlap. A shot cut ends every track.
3. **Embed.** Only the 3 best crops of each track (sharp, frontal, large) go through ArcFace.
   This is what keeps CPU use low.
4. **Cluster.** Tracks are grouped into people. Two tracks visible in the same frame can never
   be one person (a cannot-link rule), which stops look-alikes from collapsing together.
5. **Name.** Each person cluster is compared with the credited cast's reference faces: the Jellyfin
   portrait, several TMDb portraits from different ages, and faces learned from other titles.
   - A cluster gets a name only when it clearly beats the runner-up.
   - A weaker match (an old film against a recent headshot) is accepted only with a wide lead.
   - Short or off-angle fragments join an already-named person on video-to-video similarity.
6. **Timeline.** Each person's tracks become scene intervals, with short gaps bridged.

**Scenes.** "In scene" means the scene, not the shot. Each sampled frame gets a 512-bin colour
fingerprint. Frames split into shots at hard changes, and a shot that links back to a shot from
the last 30 s (a similar set, or a similar set and the same people) continues that scene. Someone
seen for 2 s, or heard once, is shown for the whole scene, so cutaways in a conversation don't make
actors drop out and reappear. Scenes over 5 minutes use first-to-last appearance instead.

Off-angle faces are embedded too, just weighted lower. Dropping three-quarter views lost actors in
wide shots.

People the analyser sees often but cannot name are kept as **unknown faces**. An admin can name
them from the player. The assignment is re-applied on re-analysis and teaches the actor's
reference set.

**Voices.** This is how cartoon voice casts and costumed or off-screen characters (Buddy in *Buddy*)
appear.

1. **Speech.** Every single-speaker subtitle line with real words becomes a voiceprint, using WeSpeaker
   ResNet34 on the dialogue (centre) channel. Grunts like "Mmm! Unh!" are skipped.
2. **Voices.** Voiceprints are clustered into voices for the title.
3. **Naming.** Each voice is named automatically, with no human labelling, strongest evidence first:
   - **SDH speaker tags** (`[Buddy]`, `RICK:`).
   - **The voice gallery:** this actor's voice, learned from other titles in your library.
   - **Names said in the dialogue.** When someone says "Morty!", the voice that answers is probably
     Morty's. A voice that keeps saying "Morty" is probably not Morty. "I'm Rick" names the speaker.
   - **Live action:** whose face is on screen while the voice speaks.

   Only clear winners are named. Confident names teach the gallery, so a series gets better episode
   by episode.

Credits marked "(voice)" are shown as "Voice of …". The Cast tab also lists everyone credited, so a
cartoon always shows its voice cast. A title already analysed for faces only re-runs the voice pass.

Tested on *Buddy* (2026), with tags hidden:

- Buddy was named as Keegan-Michael Key.
- No wrong names.

Tested on a *Rick and Morty* episode with no speaker tags:

- Rick, Morty and Reese (Owen Wilson) were all named correctly.

**Music.**

- Songs in your Jellyfin music libraries are fingerprinted once with Chromaprint (via Jellyfin's
  ffmpeg). They are found inside a title's soundtrack by voting on 16-bit fingerprint halves,
  then verified by bit-error rate. This is robust to dialogue mixed on top.
- With an [AudD](https://audd.io) token, other songs are identified from short clips. Moments
  that text subtitles mark as music (♪) are tried first, and there is a per-title cap.

**When.** The *Analyse library* task (Dashboard → Scheduled Tasks → X-Ray) runs nightly, newest
titles first, with a 5-hour limit. Its progress counts the title being analysed, so the bar moves
during each title, not only between them. New movies and episodes are queued as soon as they are
added (once their metadata is in, at most 15 minutes later), ahead of the nightly backlog. A title
someone starts watching jumps ahead of both.

## Player

The overlay is added to jellyfin-web without touching files on disk, through
[File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation). If that
plugin is installed, X-Ray registers with it; if not, X-Ray uses a bundled copy of its middleware.

- **Controls visible:** a slim "In scene" column on the left.
- **Click it, or press X:** the full panel, with In scene, Cast and Music tabs. It pauses playback
  and resumes it when closed.
- **Person detail:** their scenes (click to jump, when direct playing), and their other titles in
  your library.
- **Admins:** name unknown faces, or remove a wrong name.

## API (for other clients)

All routes need a normal Jellyfin token (`Authorization: MediaBrowser Token="…"`).

| Method | Route | |
|---|---|---|
| GET | `/SceneXray/Items/{itemId}/Timeline` | `people[]` (personId, name, role, screenTimeSeconds, confidence, `intervals` as `[[start,end],…]` in seconds, imageUrl), `music[]` (start, end, title, artist, album, source, itemId), stats. 404 with the queue status when not analysed yet. |
| GET | `/SceneXray/Items/{itemId}/At?seconds=123.4` | Just who is on screen and what is playing at that moment. |
| GET | `/SceneXray/Items/{itemId}/Status` | `state`: ready, queued, running, failed or none, plus progress and queue position. |
| POST | `/SceneXray/Items/{itemId}/Analyze` | Admin. Queue (or re-queue) a title. |
| POST | `/SceneXray/Items/{itemId}/Unknown/{n}/Assign?personId=` | Admin. Name an unknown face. |
| DELETE | `/SceneXray/Items/{itemId}/People/{personId}` | Admin. Remove a wrong name (it stays removed on re-analysis). |
| GET | `/SceneXray/Summary` | Admin. Counts for the settings page. |
| GET | `/SceneXray/Titles?state=&sharing=&search=&startIndex=&limit=` | Admin. Which titles have X-Ray: counts by state (done, outdated, missing, running, queued, failed) and by sharing (sent, sending, received), then one page of titles. `state` filters (also `active` = running or queued); `sharing` is sent, sending, received or local. |

A native client only needs the Timeline call at playback start. After that it can look up the
current position locally.

## Data

Everything lives under `<jellyfin data>/scenexray/`:

- `models/`: the ONNX files, about 220 MB with the speaker model, downloaded on first use and
  SHA-256 checked.
- `timelines/<itemId>.json`
- `gallery/<recognizer>.json` and `gallery/voice-*.json`: the reference faces and voices.
- `music/`: the fingerprint catalog.
- `thumbs/`
- `share/` (only with sharing on): `install.json` (the membership token, mode 0600), the
  submission outbox, fingerprints waiting to be sent, and which submission came from which
  analysis.

Deleting a timeline makes the title analyse again.

The face models are InsightFace's `buffalo_l` / `buffalo_s` (non-commercial research licence),
fetched by your server and never redistributed by this plugin.

## Sharing (invited servers only)

X-Ray can share what it learns with a small, invite-only service, so that a title another
server already analysed needs no analysis here (design: `docs/shared-data-design.md`; the
service: `server/`).

- **Off by default.** Turning it on needs an invite. An invite is one line starting with
  `xray1.`, given out by the service's operator, and it registers up to three servers.
- **Both ways or not at all.** With sharing on, your server sends its results and receives other
  servers' results. With it off, it sends nothing and asks nothing.
- **Give to get.** Credit is counted in minutes of runtime.
  - Registering gives 600 minutes.
  - Every minute you analyse earns two minutes of other servers' analysis. Half of that is held
    until another friend's server agrees with your result.
  - Receiving a title costs its runtime once.
  - When credit runs out, titles are simply analysed locally again, which earns more.

**What is sent and what is not.** The full list is the toggle text in the settings and
[`docs/sharing-privacy-note.md`](docs/sharing-privacy-note.md). In short:
- **Sent:** TMDb/IMDb/TVDb ids, edition and runtime; the TMDb ids of the people found and when
  they appear; scenes; songs; short audio fingerprints; and, for faces X-Ray couldn't name, a
  face signature (numbers, not a picture).
- **Never sent:** pictures, voices, file names, users or watch history.

**How a title is shared.**
- **Before analysing a title,** X-Ray looks for it on the service. It asks with a 16-bit hash
  prefix of the title, so the service can't tell which title was meant.
- **Lining up.** X-Ray checks with audio fingerprints that its file lines up with a shared copy,
  across different intros, trimmed credits and PAL speed-up. Different cuts are refused, not
  misaligned.
- **When a copy lines up,** X-Ray fetches it instead of analysing.
- **Otherwise** it analyses locally and contributes the result in the background.
- **Data from a single server** is marked *unconfirmed*. Your server receives it only when
  someone is waiting to watch the title. New media and the nightly task analyse unconfirmed titles themselves,
  which earns credit and confirms them.
- **Received titles are your copy.** They are refreshed when the shared consensus improves.
  *Re-analyse received titles locally* replaces them with your own analysis.
- **Unknown faces.** Faces your server couldn't name can be named by other servers' results.
  Such a name teaches your local face gallery only once two independent servers back it.
- **Other cuts (optional).** *Use shared data from another cut of a title* lets a title someone
  starts watching borrow another cut's data for the scenes the two share, until it is analysed
  here.

**Leaving.**
- *Leave* deletes your membership, credit, title list and face signatures. Your past results stay
  as anonymous votes.
- *Leave and withdraw my contributions* deletes those results too.
- Servers silent for a year are deleted automatically.

The service's address is fixed (`SharingServerUrl` in the plugin's XML configuration, for
testing only). Running the service is described in [`server/README.md`](server/README.md) and,
on the homelab, in the proxmox repo's `docs/runbooks/xray-share.md`.

## Settings worth knowing

- **Models.**
  - Balanced (default): SCRFD-500M + ArcFace R50, the best accuracy for the CPU spent.
  - Accurate: SCRFD-10G, for small faces in wide shots.
  - Fast: MobileFaceNet.
- **Run face recognition on** CPU (default) or GPU. Face detection and recognition are most of
  the work, and they are many times faster on a GPU (on an Arc A380: detection about 20×,
  recognition about 50×; on an M-series Mac about 4× and 9×). The results match the CPU's.
  - **Intel GPUs** (Arc, Iris Xe, UHD) on Linux and Windows x64 use Intel's OpenVINO plugin for
    ONNX Runtime. It is about 70 MB, so it is downloaded from PyPI (checked by SHA-256) the first
    time GPU is chosen. In Docker the container needs the GPU (`--device /dev/dri`) and Intel's
    compute runtime, which the official Jellyfin image includes.
  - **Apple Silicon** uses Core ML, which is built in.
  - **Anything else** (NVIDIA, AMD, Raspberry Pi) stays on the CPU. So does any failure: the
    settings page says what runs the models and why. If Jellyfin itself stopped while a GPU was
    being set up, X-Ray stays on the CPU until you switch to CPU and back to GPU.
- **Run voice recognition on** CPU (default) or GPU, the same GPUs as faces, set on its own. On
  an Arc A380 a subtitle line takes about 12 ms instead of about 250 ms on 2 CPU threads (Core ML
  on an M-series Mac: 19 ms instead of 130 ms). A GPU runs the speaker model at fixed lengths, one
  per whole second up to the 8 s cap, and repeats each line's features up to its length: OpenVINO
  would otherwise recompile for every new length (1.4 s a line), and Core ML gets the model's
  pooling wrong when the length varies (every voiceprint came out NaN). The padding moves a
  voiceprint less than starting the line 50 ms later does (Breaking Bad S01E01, 281 lines:
  worst cosine 0.94 to the exact one, 2.2% of same/different-voice decisions flipped, against
  0.86 and 3.4% for the 50 ms shift). The first title after a restart spends about 20 s compiling
  the eight lengths on an A380 (cached on disk afterwards).
- **Music** has no GPU setting: it is matched by Chromaprint fingerprint (DSP and bit counting in
  ffmpeg and the plugin, no neural network), which takes seconds per title on the CPU.
- **CPU threads** (default 2): for face and voice recognition when they run on the CPU.
- **Naming threshold** / **low-confidence threshold** / **lead over runner-up**: raise them for
  fewer mistakes, lower them for more names.
- **TMDb API key**: optional. Empty means the key Jellyfin's TMDb provider already uses. Extra
  portraits matter most for films from decades ago.

## Build

```bash
dotnet test tests/Jellyfin.Plugin.SceneXray.Tests
./build.sh            # -> dist/X-Ray_<version>/ ready to copy into <jellyfin config>/plugins/
```

`tools/XrayHarness` runs the face pipeline on a local file with a folder of cast photos. It prints
per-cluster scores and timings, which is useful for tuning thresholds. A last argument of `gpu`
runs it through the same GPU setup as the plugin.

`tools/VoiceHarness` does the same for voices: 16 kHz mono PCM, an SRT file and a speaker model. It
scores the voices against SDH tags when the subtitles have them. `VoiceHarness bench <pcm> <model>
cpu|gpu` times the speaker model on the CPU and the chosen backend and compares their voiceprints;
`VoiceHarness fitsrt <pcm> <srt> <model>` measures what the GPU's fixed lengths do to real lines.

`tools/AlignHarness` checks whether a timeline made on one file can be applied to another file of
the same title (shared X-Ray data, `docs/shared-data-design.md` §3): `AlignHarness align <A> <B>`
fingerprints both through ffmpeg (`FFMPEG=` must point at one with Chromaprint, such as Jellyfin's),
fits B's time against A's anchors and prints the fit and the verdict.

## Later

`IFaceEngine` (`Vision/FaceModels.cs`) is the seam for offloading detection and embedding to a GPU
worker (for example the 3090) over HTTP. Nothing else would need to change.

## Licence

X-Ray is licensed under the GNU General Public License v3.0 (see `LICENSE`).

It includes code from [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
by IAmParadox27 (GPL-3.0), in `src/Jellyfin.Plugin.SceneXray/ThirdParty/FileTransformation/`. The
face and speaker models are downloaded at runtime and are not part of this repository:
- InsightFace buffalo models: non-commercial research licence.
- WeSpeaker ResNet34: trained on VoxCeleb.


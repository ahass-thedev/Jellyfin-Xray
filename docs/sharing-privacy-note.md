# X-Ray sharing: what is stored, and how to leave

*Sent with every invite (design decision D12). It is short on purpose. The full design is in
`docs/shared-data-design.md`.*

Your invite lets up to three of your Jellyfin servers join the X-Ray sharing service.

**The service:**
- It runs on my homelab, for friends only. Nobody without an invite can reach it.
- It has no ads or analytics, and no data is sold or passed on.
- It stays free: the face models it relies on are licensed for non-commercial use only.

## What your server sends

- **Which film or episode.** Its TMDb, IMDb or TVDb ids, the edition (for example "extended")
  and the runtime. Never a file name or path.
- **What X-Ray found in it:**
  - the TMDb ids of the people it recognised, and when they appear;
  - the scene boundaries;
  - the songs it identified.
- **The X-Ray version and settings** that produced the result.
- **Short audio fingerprints** (Chromaprint). They are used to line results up with other copies
  of the same title, and the audio cannot be rebuilt from them.
- **For faces X-Ray couldn't name:** a face signature. This is a list of 512 numbers that
  describes a face; it is not a picture. It is sent only for recurring faces in films and
  episodes, never from home videos or anything else without a TMDb or IMDb id.

## What it never sends

- Pictures of faces, or any image.
- Voice data.
- File names, paths or sizes.
- Users, or what anyone watched.
- IP addresses: the service itself doesn't log them. Cloudflare, which carries the traffic to
  the homelab, keeps its usual logs.

## What the service keeps, and for how long

| What | Kept until |
|---|---|
| Your membership and credit | You leave, or 12 months without contact |
| Which titles your server sent or received | You leave, or 12 months without contact |
| Your results (people, scenes, songs, fingerprints) | You choose *withdraw*. Otherwise, after you leave, they stay as anonymous votes with no link to you. |
| Face signatures | The moment the face is named (only the TMDb id is kept), 12 months unnamed, or when you leave, whichever comes first |
| Unused credit | 12 months |

**Face signatures are biometric data, so they get extra care:**
- No part of the service ever hands one out. It only ever answers with the TMDb id of a name.
- They are compared only with other unknown faces, to find the same actor in other titles.
- The database lives on my homelab, and its backups are encrypted.

The service knows which titles your server has. Sharing a result for a film can't work without
saying which film it is.

## How to stop

In Jellyfin, open **Dashboard → X-Ray → Sharing**.

- **Untick the box.** Everything stops at once. Your membership is kept, so ticking it again
  carries on where you left off.
- **Leave.** This deletes your membership, credit, your list of titles and every face signature
  your servers sent. Your past results stay as anonymous votes.
- **Leave and withdraw my contributions.** This does the same, and also deletes every result your
  servers contributed.

Titles your server already received stay on your server, because they are your copy.
**Re-analyse received titles locally** replaces them with your own analysis.

Questions, or want something deleted by hand? Just ask me.

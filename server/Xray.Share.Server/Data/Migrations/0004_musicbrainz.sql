-- Phase 5: MusicBrainz verification of single-voice music cues (design §4.4). A cue whose
-- recording id MusicBrainz confirms, with a matching title, is served from one install, with
-- MusicBrainz's own title and artist rather than the submitted text.
create table mb_recordings (
    mbid        uuid primary key,
    found       boolean not null,
    title       text,
    artist      text,
    checked_at  timestamptz not null default now()
);

-- Which submissions name which recording ids, so a verification requeues their consensus.
create table submission_mbids (
    submission_id  uuid not null references submissions (id) on delete cascade,
    mbid           uuid not null,
    primary key (submission_id, mbid)
);
create index submission_mbids_mbid on submission_mbids (mbid);

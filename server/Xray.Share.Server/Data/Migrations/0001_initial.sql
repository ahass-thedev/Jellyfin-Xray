-- Phase 1 schema (design §6). Applied once, in a transaction, by Db.MigrateAsync.

-- One invite per friend: a registration code (only its hash is kept) good for a few installs,
-- and the id of the Cloudflare Access service token handed out with it. The secret is printed
-- once in the invite line and never stored.
create table invites (
    id                uuid primary key default gen_random_uuid(),
    code_hash         bytea not null unique,
    access_client_id  text,
    note              text,
    max_installs      int not null,
    installs_used     int not null default 0,
    created_at        timestamptz not null default now(),
    revoked_at        timestamptz
);

-- An install is a bearer token (only its SHA-256 is kept) and nothing else about the server.
create table installs (
    id              uuid primary key default gen_random_uuid(),
    token_hash      bytea not null unique,
    invite_id       uuid not null references invites (id),
    plugin_version  text not null,
    created_at      timestamptz not null default now(),
    last_seen_at    timestamptz not null default now(),
    trust           double precision not null default 0.5,
    state           text not null default 'member'
);

-- Append-only credit entries, in minutes. held = the corroboration hold (design §1).
create table ledger (
    id             bigserial primary key,
    install_id     uuid not null references installs (id) on delete cascade,
    at             timestamptz not null default now(),
    kind           text not null,
    minutes        int not null,
    held           boolean not null default false,
    submission_id  uuid,
    variant_id     uuid
);
create index ledger_install on ledger (install_id);

create table titles (
    id            uuid primary key default gen_random_uuid(),
    key           text not null unique,
    key_hash      text not null,
    prefix        text not null,
    kind          text not null,
    created_at    timestamptz not null default now()
);
create index titles_prefix on titles (prefix);

-- A variant is one cut of a title with its own canonical time base: the full anchor set of the
-- contribution that created it (stride 1). Every ReferenceStride-th anchor is public.
create table variants (
    id            uuid primary key default gen_random_uuid(),
    title_id      uuid not null references titles (id) on delete cascade,
    edition       text not null,
    runtime       double precision not null,
    frame_rate    double precision,
    every         double precision not null,
    length        int not null,
    anchor_count  int not null,
    anchors       bytea not null,
    created_at    timestamptz not null default now()
);
create index variants_title on variants (title_id);

-- One row per received contribution. body = the gzipped DTO (unknown-face embeddings are never
-- kept here); canonical = its timeline mapped into the variant's time base, gzipped.
create table submissions (
    id               uuid primary key default gen_random_uuid(),
    install_id       uuid references installs (id) on delete set null,
    invite_id        uuid not null,
    variant_id       uuid not null references variants (id) on delete cascade,
    version          int not null,
    analyzer         text not null,
    model_pack       text not null,
    plugin_version   text not null,
    runtime          double precision not null,
    slope            double precision not null,
    "offset"         double precision not null,
    valid_from       double precision not null,
    valid_to         double precision not null,
    body             bytea not null,
    canonical        bytea not null,
    status           text not null,
    corroborated     boolean not null default false,
    earned           int not null default 0,
    held             int not null default 0,
    created_at       timestamptz not null default now()
);
create index submissions_variant on submissions (variant_id, version);
create index submissions_install on submissions (install_id);

create sequence consensus_revision;

create table consensus (
    variant_id   uuid not null references variants (id) on delete cascade,
    version      int not null,
    revision     bigint not null,
    tier         text not null,
    voices       int not null,
    body         bytea not null,
    body_hash    bytea not null,
    updated_at   timestamptz not null default now(),
    primary key (variant_id, version)
);
create index consensus_revision_idx on consensus (revision);

-- Which variants an install holds, for metering and copy-back detection (design §1).
create table holdings (
    install_id  uuid not null references installs (id) on delete cascade,
    variant_id  uuid not null references variants (id) on delete cascade,
    kind        text not null,
    at          timestamptz not null default now(),
    primary key (install_id, variant_id, kind)
);

-- Single-use possession-challenge nonces (only the hash is kept).
create table challenges (
    nonce_hash  bytea primary key,
    install_id  uuid not null references installs (id) on delete cascade,
    variant_id  uuid not null references variants (id) on delete cascade,
    created_at  timestamptz not null default now(),
    used_at     timestamptz
);

-- The work queue: consensus recomputation and the like, taken with FOR UPDATE SKIP LOCKED.
create table jobs (
    id          bigserial primary key,
    kind        text not null,
    ref         text not null,
    run_after   timestamptz not null default now(),
    attempts    int not null default 0,
    last_error  text,
    unique (kind, ref)
);

-- TMDb cast ids per title key, cached for 30 days. found = false caches "TMDb has no such title".
create table tmdb_credits (
    key         text primary key,
    people      int[] not null,
    found       boolean not null,
    fetched_at  timestamptz not null default now()
);

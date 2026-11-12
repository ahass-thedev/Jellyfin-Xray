-- Phase 2b: naming unknown faces together (design §2). Embeddings arrive one way and never
-- leave: no route returns one. An embedding is nulled when its face is named, after 12 months
-- unresolved, and the row goes when its install leaves.
create extension if not exists vector;

create sequence unknown_revision;

create table unknown_faces (
    id                 uuid primary key default gen_random_uuid(),
    submission_id      uuid not null references submissions (id) on delete cascade,
    install_id         uuid references installs (id) on delete cascade,
    variant_id         uuid not null references variants (id) on delete cascade,
    title_key          text not null,
    local_id           int not null,
    recognizer         text not null,
    embedding          halfvec(512),
    screen_time        double precision not null,
    -- [[start, end], …] in the variant's canonical time.
    intervals          jsonb not null,
    link_group         uuid,
    person             int,
    confidence         double precision,
    voices             int,
    rule               text,
    resolved_revision  bigint,
    resolved_at        timestamptz,
    created_at         timestamptz not null default now(),
    unique (submission_id, local_id)
);
create index unknown_faces_embedding on unknown_faces using hnsw (embedding halfvec_cosine_ops);
create index unknown_faces_install on unknown_faces (install_id, resolved_revision);
create index unknown_faces_variant on unknown_faces (variant_id);
create index unknown_faces_group on unknown_faces (link_group);

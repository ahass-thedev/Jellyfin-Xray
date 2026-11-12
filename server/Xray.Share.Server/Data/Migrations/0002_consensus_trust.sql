-- Phase 2: multi-voice consensus, agreement, trust and the corroboration hold (design §4).

alter table submissions
    add column agreement     double precision,
    add column outlier       boolean not null default false,
    -- pending: held credit not decided yet; released: an independent voice agreed; clawed: outvoted.
    add column credit_state  text not null default 'pending',
    -- Trust frozen when the install leaves, so its anonymous votes keep their weight (D7).
    add column anon_trust    double precision;

-- When a (variant, version) first reached `confirmed`: older versions are deleted 90 days later.
alter table consensus add column confirmed_at timestamptz;

-- When maintenance saw the install become old enough to vote, and requeued its variants.
alter table installs add column voting_since timestamptz;

create index submissions_install_status on submissions (install_id, status);

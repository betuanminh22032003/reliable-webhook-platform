CREATE TABLE IF NOT EXISTS schema_migrations
(
    version integer PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS webhook_endpoints
(
    id uuid PRIMARY KEY,
    destination_url text NOT NULL,
    secret text NOT NULL,
    enabled boolean NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS webhook_events
(
    id uuid PRIMARY KEY,
    event_type varchar(200) NOT NULL,
    payload jsonb NOT NULL,
    idempotency_key varchar(200),
    payload_hash char(64) NOT NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT uq_event_idempotency UNIQUE (idempotency_key)
);

CREATE TABLE IF NOT EXISTS deliveries
(
    id uuid PRIMARY KEY,
    event_id uuid NOT NULL REFERENCES webhook_events(id),
    endpoint_id uuid NOT NULL REFERENCES webhook_endpoints(id),
    status varchar(30) NOT NULL,
    attempt_count integer NOT NULL DEFAULT 0,
    max_attempts integer NOT NULL,
    next_attempt_at timestamptz NOT NULL,
    lease_token uuid,
    lease_expires_at timestamptz,
    completed_at timestamptz,
    last_status_code integer,
    last_error varchar(500),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (event_id, endpoint_id)
);

CREATE TABLE IF NOT EXISTS delivery_outbox
(
    delivery_id uuid PRIMARY KEY REFERENCES deliveries(id) ON DELETE CASCADE,
    available_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS delivery_attempts
(
    id uuid PRIMARY KEY,
    delivery_id uuid NOT NULL REFERENCES deliveries(id),
    attempt_number integer NOT NULL,
    attempted_at timestamptz NOT NULL,
    status_code integer,
    error varchar(500),
    duration_ms integer NOT NULL,
    UNIQUE (delivery_id, attempt_number)
);

CREATE INDEX IF NOT EXISTS ix_outbox_available
    ON delivery_outbox(available_at);

CREATE INDEX IF NOT EXISTS ix_deliveries_event
    ON deliveries(event_id);

CREATE INDEX IF NOT EXISTS ix_deliveries_claim
    ON deliveries(status, next_attempt_at, lease_expires_at);

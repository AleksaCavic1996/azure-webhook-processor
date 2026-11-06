-- Current state of each payment.
CREATE TABLE payments (
    payment_id      text        PRIMARY KEY,
    status          text        NOT NULL CHECK (status IN ('authorized', 'failed', 'captured')),
    captured_amount bigint      NOT NULL DEFAULT 0 CHECK (captured_amount >= 0),
    currency        char(3)     NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

-- One row per event ever applied. The primary key is what makes processing idempotent.
-- The foreign key is checked at commit because the event is inserted before the payment.
CREATE TABLE processed_events (
    event_id     text        PRIMARY KEY,
    payment_id   text        NOT NULL REFERENCES payments (payment_id) DEFERRABLE INITIALLY DEFERRED,
    event_type   text        NOT NULL,
    amount       bigint      NOT NULL,
    currency     char(3)     NOT NULL,
    occurred_at  timestamptz NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_processed_events_payment_id ON processed_events (payment_id);

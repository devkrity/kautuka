CREATE TABLE usage_schema (schema_id TEXT PRIMARY KEY, created_at TEXT NOT NULL);
INSERT INTO usage_schema(schema_id, created_at) VALUES ('usage-2026-07-15.1', CURRENT_TIMESTAMP);

CREATE TABLE runtime_run_usage (
    id                          TEXT PRIMARY KEY,
    usage_fact_version          INTEGER NOT NULL,
    run_id                      TEXT NOT NULL,
    session_id                  TEXT NULL,
    target_type                 TEXT NOT NULL,
    target_id                   TEXT NOT NULL,
    user_id                     TEXT NULL,
    username_snapshot           TEXT NULL,
    user_display_name_snapshot  TEXT NULL,
    agent_id                    TEXT NULL,
    agent_name_snapshot         TEXT NULL,
    model_id                    TEXT NULL,
    model_name_snapshot         TEXT NULL,
    provider_id                 TEXT NULL,
    provider_name_snapshot      TEXT NULL,
    run_status                  TEXT NOT NULL,
    source_channel              TEXT NULL,
    run_started_at              TEXT NOT NULL,
    first_model_output_at       TEXT NULL,
    run_ended_at                TEXT NOT NULL,
    total_duration_ms           INTEGER NOT NULL,
    first_token_latency_ms      INTEGER NULL,
    token_in                    INTEGER NULL,
    token_out                   INTEGER NULL,
    total_tokens                INTEGER NULL,
    provider_request_count      INTEGER NULL,
    model_turn_count            INTEGER NULL,
    assistant_turn_count        INTEGER NULL,
    tool_call_count             INTEGER NULL,
    cost_basis                  TEXT NULL,
    input_cost                  REAL NULL,
    output_cost                 REAL NULL,
    total_cost                  REAL NULL,
    cost_currency               TEXT NULL,
    pricing_source              TEXT NULL,
    usage_date                  TEXT NOT NULL,
    created_at                  TEXT NOT NULL
);

CREATE UNIQUE INDEX idx_runtime_run_usage_run
    ON runtime_run_usage(run_id);

CREATE INDEX idx_runtime_run_usage_user_started
    ON runtime_run_usage(user_id, run_started_at DESC);

CREATE INDEX idx_runtime_run_usage_model_started
    ON runtime_run_usage(model_id, run_started_at DESC);

CREATE INDEX idx_runtime_run_usage_provider_started
    ON runtime_run_usage(provider_id, run_started_at DESC);

CREATE INDEX idx_runtime_run_usage_session
    ON runtime_run_usage(session_id);

CREATE INDEX idx_runtime_run_usage_date
    ON runtime_run_usage(usage_date, run_started_at DESC);

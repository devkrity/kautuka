-- Canonical Gateway registry schema. Empty databases are created from this file.
-- Existing databases receive registered compatible upgrades, then exact DDL validation.
CREATE TABLE gateway_schema (
    schema_id TEXT PRIMARY KEY,
    created_at TEXT NOT NULL
);
INSERT INTO gateway_schema(schema_id, created_at) VALUES ('gateway-2026-10-10.1', CURRENT_TIMESTAMP);
CREATE TABLE providers (
            id           TEXT PRIMARY KEY,
            name         TEXT NOT NULL,
            type         TEXT NOT NULL,
            settings_json TEXT NOT NULL DEFAULT '{}',
            created_at    TEXT NOT NULL,
            updated_at    TEXT NOT NULL
        );
CREATE TABLE models (
            id           TEXT PRIMARY KEY,
            provider_id   TEXT NOT NULL,
            model_name    TEXT NOT NULL,
            settings_json TEXT NOT NULL DEFAULT '{}',
            capabilities_json TEXT NULL,
            created_at    TEXT NOT NULL,
            updated_at    TEXT NOT NULL
        );
CREATE INDEX idx_models_provider
            ON models(provider_id);
CREATE TABLE agents (
            id                    TEXT    PRIMARY KEY,
            scope                 TEXT    NOT NULL DEFAULT 'user',
            enabled               INTEGER NOT NULL DEFAULT 1,
            owner_user_id          TEXT    NOT NULL DEFAULT '',
            storage_locator_key    TEXT    NOT NULL DEFAULT '',
            created_at             TEXT    NOT NULL,
            updated_at             TEXT    NOT NULL
        );
CREATE INDEX idx_agents_scope_owner
            ON agents(scope, owner_user_id);
CREATE TABLE mcp_servers (
            id                            TEXT PRIMARY KEY,
            name                          TEXT NOT NULL,
            description                   TEXT NOT NULL DEFAULT '',
            icon_url                       TEXT NOT NULL DEFAULT '',
            preset_id                      TEXT NULL,
            transport                     TEXT NOT NULL DEFAULT 'http',
            http_url                       TEXT NULL,
            encrypted_env_json              TEXT NULL,
            env_keys_json                   TEXT NOT NULL DEFAULT '[]',
            requiresApproval              INTEGER NOT NULL DEFAULT 0,
            requiresApprovalFunctionsJson TEXT NULL,
            tool_count                     INTEGER NULL,
            tool_names_json                 TEXT NULL,
            scope                         TEXT NOT NULL DEFAULT 'system',
            owner_user_id                 TEXT NULL,
            enabled                       INTEGER NOT NULL DEFAULT 1,
            is_internal_system_connector  INTEGER NOT NULL DEFAULT 0,
            created_at                     TEXT NOT NULL,
            updated_at                     TEXT NOT NULL,
            bundle_path                    TEXT NULL,
            server_type                    TEXT NULL,
            container_image                TEXT NULL,
            container_command              TEXT NULL,
            container_args_json            TEXT NULL,
            container_sidecar_name         TEXT NULL,
            readiness_timeout_seconds      INTEGER NULL,
            jit_cooldown_seconds           INTEGER NULL,
            healthcheck_url                TEXT NULL,
            healthcheck_interval_milliseconds INTEGER NULL
        , runtime_artifact_type TEXT NULL, runtime_artifact_source_digest TEXT NULL, runtime_artifact_build_status TEXT NULL, mcp_protocol_version TEXT NULL);
CREATE TABLE skills (
            id                   TEXT PRIMARY KEY,
            name                 TEXT NOT NULL,
            description          TEXT NOT NULL DEFAULT '',
            import_timestamp     TEXT NOT NULL,
            package_hash         TEXT NOT NULL,
            status               TEXT NOT NULL DEFAULT 'ready',
            package_path         TEXT NOT NULL,
            has_resources        INTEGER NOT NULL DEFAULT 0,
            has_scripts          INTEGER NOT NULL DEFAULT 0,
            source_metadata_json TEXT NOT NULL DEFAULT '{}',
            scope                TEXT NOT NULL DEFAULT 'system',
            owner_user_id        TEXT NULL,
            created_at           TEXT NOT NULL,
            updated_at           TEXT NOT NULL
        , enabled INTEGER NOT NULL DEFAULT 1, env_keys_json TEXT NOT NULL DEFAULT '[]');
CREATE TABLE runtime_users (
            username             TEXT PRIMARY KEY,
            user_id               TEXT NOT NULL,
            role                 TEXT NOT NULL DEFAULT 'user',
            password             TEXT NOT NULL DEFAULT '',
            is_active             INTEGER NOT NULL DEFAULT 1,
            email                TEXT NULL,
            can_import_connectors INTEGER NOT NULL DEFAULT 0,
            can_import_skills     INTEGER NOT NULL DEFAULT 0,
            agent_ids_json         TEXT NOT NULL DEFAULT '[]',
            telegramuser_idsJson  TEXT NOT NULL DEFAULT '[]',
            telegram_bot_ids_json   TEXT NOT NULL DEFAULT '[]',
            agent_permissions_json TEXT NOT NULL DEFAULT '{}',
            created_at            TEXT NOT NULL,
            updated_at            TEXT NOT NULL
        , display_name TEXT NULL);
CREATE UNIQUE INDEX idx_runtime_users_user_id
            ON runtime_users(user_id);
CREATE TABLE runtime_channel_registrations (
            id            TEXT    PRIMARY KEY,
            channel_type   TEXT    NOT NULL,
            owneruser_id   TEXT    NULL,
            external_bot_id TEXT    NOT NULL DEFAULT '',
            is_active      INTEGER NOT NULL DEFAULT 1,
            settings_json  TEXT    NOT NULL DEFAULT '{}',
            created_at     TEXT    NOT NULL,
            updated_at     TEXT    NOT NULL
        );
CREATE TABLE runtime_channel_identity_links (
            id             TEXT PRIMARY KEY,
            registration_id TEXT NOT NULL,
            user_id         TEXT NOT NULL,
            externaluser_id TEXT NOT NULL,
            created_at      TEXT NOT NULL
        );
CREATE UNIQUE INDEX idx_runtime_channel_identity_links_lookup
            ON runtime_channel_identity_links(registration_id, externaluser_id);
CREATE TABLE runtime_channel_target_bindings (
            id             TEXT    PRIMARY KEY,
            registration_id TEXT    NOT NULL,
            target_type     TEXT    NOT NULL,
            target_id       TEXT    NOT NULL,
            is_default      INTEGER NOT NULL DEFAULT 0,
            created_at      TEXT    NOT NULL
        );
CREATE INDEX idx_runtime_channel_target_bindings_reg
            ON runtime_channel_target_bindings(registration_id);
CREATE TABLE runtime_channel_interactions (
            id                     TEXT PRIMARY KEY,
            registration_id         TEXT NOT NULL,
            user_id                 TEXT NULL,
            target_type             TEXT NULL,
            target_id               TEXT NULL,
            session_id              TEXT NULL,
            run_id                  TEXT NULL,
            chat_id                 TEXT NOT NULL,
            message_thread_id        TEXT NULL,
            prompt                 TEXT NOT NULL DEFAULT '',
            sender_display_name     TEXT NULL,
            options_json            TEXT NOT NULL DEFAULT '[]',
            status                 TEXT NOT NULL DEFAULT 'pending',
            created_at              TEXT NOT NULL,
            expires_at              TEXT NOT NULL,
            resolved_at             TEXT NULL,
            resolved_by_external_user TEXT NULL,
            resolution             TEXT NULL,
            approval_request_id      TEXT NULL,
            telegram_message_id      INTEGER NULL,
            provider_message_id      TEXT NULL
        );
CREATE INDEX idx_runtime_channel_interactions_reg
            ON runtime_channel_interactions(registration_id);
CREATE TABLE tool_approval_requests (
            id            TEXT PRIMARY KEY,
            session_id     TEXT,
            run_id         TEXT,
            agent_id       TEXT,
            user_id        TEXT,
            tool_call_id    TEXT NOT NULL,
            tool_name      TEXT NOT NULL,
            tool_type      TEXT NOT NULL DEFAULT 'mcp',
            mcp_server_id   TEXT,
            arguments_json TEXT,
            requested_at   TEXT NOT NULL,
            decision      TEXT,
            decided_by     TEXT,
            decided_at     TEXT,
            reason        TEXT,
            expires_at     TEXT,
            channel_tether_mode TEXT NOT NULL DEFAULT 'single'
        , checkpoint_id TEXT, resolution_source TEXT, outer_session_id TEXT, outer_agent_id TEXT, agent_definition_revision INTEGER, kind TEXT NOT NULL DEFAULT 'tool', workflow_id TEXT, instance_id TEXT, node_id TEXT);
CREATE INDEX idx_tool_approval_requests_session
            ON tool_approval_requests(session_id);
CREATE INDEX idx_tool_approval_requests_decision
            ON tool_approval_requests(decision);
CREATE INDEX idx_tool_approval_requests_user
            ON tool_approval_requests(user_id);
CREATE TABLE maf_sessions (
            session_id  TEXT    PRIMARY KEY,
            entityId   TEXT    NOT NULL,
            entityType TEXT    NOT NULL,
            stateJson  TEXT    NOT NULL,
            updated_at  INTEGER NOT NULL
        );
CREATE INDEX idx_maf_sessions_entity
            ON maf_sessions(entityId, entityType);
CREATE TABLE model_capability_profiles (
            profileKey         TEXT    PRIMARY KEY,
            provider_id         TEXT    NOT NULL,
            baseUrl            TEXT    NOT NULL,
            model_id            TEXT    NOT NULL,
            modelDigest        TEXT    NULL,
            probeSchemaVersion INTEGER NOT NULL,
            declaredJson       TEXT    NOT NULL DEFAULT '{}',
            probedJson         TEXT    NOT NULL DEFAULT '{}',
            observedJson       TEXT    NOT NULL DEFAULT '{}',
            probedAt           TEXT    NULL,
            created_at          TEXT    NOT NULL,
            updated_at          TEXT    NOT NULL
        );
CREATE INDEX idx_model_capability_profiles_model
            ON model_capability_profiles(provider_id, model_id);
CREATE TABLE provider_path_observations (
            pathKey                  TEXT    PRIMARY KEY,
            providerType             TEXT    NOT NULL,
            endpointKey              TEXT    NOT NULL,
            clientType               TEXT    NOT NULL,
            modelKey                 TEXT    NOT NULL,
            nativeToolSuccessCount   INTEGER NOT NULL DEFAULT 0,
            nativeToolFailureCount   INTEGER NOT NULL DEFAULT 0,
            strictJsonSuccessCount   INTEGER NOT NULL DEFAULT 0,
            relaxedJsonSuccessCount  INTEGER NOT NULL DEFAULT 0,
            jsonFailureCount         INTEGER NOT NULL DEFAULT 0,
            reasoningOnlyCount       INTEGER NOT NULL DEFAULT 0,
            visionSuccessCount       INTEGER NOT NULL DEFAULT 0,
            visionFailureCount       INTEGER NOT NULL DEFAULT 0,
            firstObservedAt          TEXT    NULL,
            lastObservedAt           TEXT    NOT NULL
        );
CREATE INDEX idx_provider_path_observations_provider
            ON provider_path_observations(providerType, endpointKey);
CREATE TABLE provider_path_effective_modes (
            pathKey       TEXT PRIMARY KEY,
            toolMode      TEXT NOT NULL DEFAULT 'Native',
            jsonMode      TEXT NOT NULL DEFAULT 'Relaxed',
            visionMode    TEXT NOT NULL DEFAULT 'Disabled',
            reasoningMode TEXT NOT NULL DEFAULT 'TextFirst',
            resolved_at    TEXT NOT NULL
        );
CREATE TABLE system_settings (
            key       TEXT PRIMARY KEY,
            value_json TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
CREATE INDEX idx_tool_approval_requests_tool_call
    ON tool_approval_requests(tool_call_id);
CREATE INDEX idx_tool_approval_requests_outer_session
    ON tool_approval_requests(outer_session_id);
CREATE INDEX idx_tool_approval_requests_stats_agent
    ON tool_approval_requests(agent_id, tool_name, decision, decided_at);
CREATE INDEX idx_tool_approval_requests_stats_mcp
    ON tool_approval_requests(mcp_server_id, tool_name, decision);
CREATE TABLE autopilot_schedules (
    id                        TEXT PRIMARY KEY,
    owner_user_id             TEXT NOT NULL,
    target_type               TEXT NOT NULL,
    target_id                 TEXT NOT NULL,
    cron_expression           TEXT NOT NULL,
    timezone_id               TEXT NOT NULL,
    mode                      TEXT NOT NULL,
    requires_pre_run_approval INTEGER NOT NULL DEFAULT 0,
    schedule_prompt_context   TEXT NOT NULL DEFAULT '',
    workspace_file_paths      TEXT NOT NULL DEFAULT '[]',
    notify_ui_inbox           INTEGER NOT NULL DEFAULT 1,
    notify_telegram           INTEGER NOT NULL DEFAULT 0,
    pre_run_approval_expiry_policy TEXT NOT NULL DEFAULT 'next_occurrence',
    pre_run_approval_timeout_seconds INTEGER NULL,
    overlap_policy            TEXT NOT NULL DEFAULT 'skip_and_notify',
    is_paused                 INTEGER NOT NULL DEFAULT 0,
    created_at                TEXT NOT NULL,
    updated_at                TEXT NOT NULL,
    next_due_at               TEXT NULL,
    last_evaluated_at         TEXT NULL,
    lease_owner               TEXT NULL,
    lease_expires_at          TEXT NULL
);
CREATE INDEX idx_autopilot_schedules_owner
    ON autopilot_schedules(owner_user_id);
CREATE INDEX idx_autopilot_schedules_due
    ON autopilot_schedules(next_due_at);
CREATE INDEX idx_autopilot_schedules_owner_target
    ON autopilot_schedules(owner_user_id, target_type, target_id);
CREATE TABLE autopilot_runs (
    id                          TEXT PRIMARY KEY,
    schedule_id                 TEXT NOT NULL,
    owner_user_id               TEXT NOT NULL,
    target_type                 TEXT NOT NULL,
    target_id                   TEXT NOT NULL,
    status                      TEXT NOT NULL,
    trigger_type                TEXT NOT NULL,
    scheduled_for               TEXT NOT NULL,
    session_id                  TEXT NULL,
    runtime_run_id              TEXT NULL,
    pre_run_approval_request_id TEXT NULL,
    input_json                  TEXT NULL,
    output_json                 TEXT NULL,
    error                       TEXT NULL,
    skip_reason                 TEXT NULL,
    blocking_run_id             TEXT NULL,
    blocking_run_status_snapshot TEXT NULL,
    blocking_run_state_category TEXT NULL,
    blocked_at                  TEXT NULL,
    created_at                  TEXT NOT NULL,
    updated_at                  TEXT NOT NULL,
    started_at                  TEXT NULL,
    ended_at                    TEXT NULL
);
CREATE INDEX idx_autopilot_runs_schedule_created
    ON autopilot_runs(schedule_id, created_at DESC);
CREATE INDEX idx_autopilot_runs_owner_created
    ON autopilot_runs(owner_user_id, created_at DESC);
CREATE INDEX idx_autopilot_runs_status
    ON autopilot_runs(status);
CREATE UNIQUE INDEX idx_autopilot_runs_one_non_terminal
    ON autopilot_runs(schedule_id)
    WHERE status IN ('scheduled', 'awaiting_pre_run_approval', 'running', 'waiting_for_in_run_approval');
CREATE TABLE autopilot_suppressions (
    id             TEXT PRIMARY KEY,
    schedule_id    TEXT NOT NULL,
    suppressed_at  TEXT NOT NULL,
    reason         TEXT NOT NULL,
    existing_run_id TEXT NULL,
    note           TEXT NULL
);
CREATE INDEX idx_autopilot_suppressions_schedule
    ON autopilot_suppressions(schedule_id, suppressed_at DESC);
CREATE TABLE autopilot_notifications (
    id               TEXT PRIMARY KEY,
    scheduled_run_id TEXT NOT NULL,
    owner_user_id    TEXT NOT NULL DEFAULT '',
    channel_type     TEXT NOT NULL,
    destination      TEXT NOT NULL,
    status           TEXT NOT NULL,
    payload_json     TEXT NULL,
    created_at       TEXT NOT NULL,
    sent_at          TEXT NULL,
    error            TEXT NULL,
    is_read          INTEGER NOT NULL DEFAULT 0,
    read_at          TEXT NULL
);
CREATE INDEX idx_autopilot_notifications_run
    ON autopilot_notifications(scheduled_run_id, created_at DESC);
CREATE INDEX idx_autopilot_notifications_owner
    ON autopilot_notifications(owner_user_id, created_at DESC);
CREATE TABLE conversation_assets (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    owner_user_id TEXT NULL,
    file_name TEXT NOT NULL,
    content_type TEXT NOT NULL,
    size_bytes INTEGER NOT NULL,
    origin TEXT NOT NULL,
    disposition TEXT NOT NULL,
    storage_path TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE INDEX idx_conversation_assets_session_id ON conversation_assets(session_id);
CREATE TABLE conversation_asset_upload_staging (
    asset_id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    storage_path TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE TABLE message_asset_refs (
    asset_id TEXT NOT NULL,
    session_id TEXT NOT NULL,
    message_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (asset_id, message_id)
);
CREATE INDEX idx_message_asset_refs_session_message ON message_asset_refs(session_id, message_id);
CREATE TABLE parsed_artifacts (
    asset_id TEXT NOT NULL,
    session_id TEXT NOT NULL,
    status INTEGER NOT NULL,
    parser_name TEXT NOT NULL,
    parser_version TEXT NOT NULL,
    normalized_markdown TEXT NULL,
    metadata_json TEXT NULL,
    warning TEXT NULL,
    error TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (asset_id, session_id)
);
CREATE INDEX idx_parsed_artifacts_session_id ON parsed_artifacts(session_id);
CREATE INDEX idx_parsed_artifacts_status ON parsed_artifacts(status);
CREATE TABLE agent_tool_invocations (
    invocation_id TEXT PRIMARY KEY,
    parent_run_id TEXT NOT NULL,
    parent_session_id TEXT NOT NULL,
    parent_tool_call_id TEXT NOT NULL,
    parent_tool_name TEXT NOT NULL,
    parent_continuation_run_id TEXT NULL,
    child_run_id TEXT NOT NULL,
    child_session_id TEXT NOT NULL,
    child_agent_id TEXT NOT NULL,
    root_run_id TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('waiting_on_child', 'parent_completion_pending', 'completing_parent', 'completed', 'failed')),
    completion_lease_owner TEXT NULL,
    completion_lease_expires_at TEXT NULL,
    failure_code TEXT NULL,
    child_result_json TEXT NULL,
    child_outcome TEXT NULL,
    child_completed_at TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    completed_at TEXT NULL,
    UNIQUE(parent_run_id, parent_tool_call_id),
    CHECK ((state = 'completing_parent' AND completion_lease_owner IS NOT NULL AND completion_lease_expires_at IS NOT NULL)
        OR (state <> 'completing_parent' AND completion_lease_owner IS NULL AND completion_lease_expires_at IS NULL)),
    CHECK ((state = 'completed' AND completed_at IS NOT NULL) OR (state <> 'completed' AND completed_at IS NULL)),
    CHECK ((state = 'failed' AND failure_code IS NOT NULL) OR state <> 'failed')
);
CREATE TABLE agent_tool_invocation_links (
    approval_request_id TEXT PRIMARY KEY,
    invocation_id TEXT NOT NULL,
    caller_session_id TEXT NOT NULL,
    caller_continuation_run_id TEXT NULL,
    caller_tool_call_id TEXT NOT NULL,
    caller_tool_name TEXT NOT NULL,
    child_session_id TEXT NOT NULL,
    caller_run_id TEXT NOT NULL,
    child_run_id TEXT NOT NULL,
    child_agent_id TEXT NOT NULL,
    root_run_id TEXT NOT NULL,
    canonical_event_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY(invocation_id) REFERENCES agent_tool_invocations(invocation_id) ON DELETE CASCADE,
    FOREIGN KEY(approval_request_id) REFERENCES tool_approval_requests(id) ON DELETE CASCADE
);
CREATE INDEX idx_agent_tool_invocations_child_session
    ON agent_tool_invocations(child_session_id);
CREATE INDEX idx_agent_tool_invocations_root_run
    ON agent_tool_invocations(root_run_id);
CREATE INDEX idx_agent_tool_invocations_state
    ON agent_tool_invocations(state, completion_lease_expires_at);
CREATE INDEX idx_agent_tool_invocation_links_invocation
    ON agent_tool_invocation_links(invocation_id);
CREATE INDEX idx_agent_tool_invocation_links_caller_session
    ON agent_tool_invocation_links(caller_session_id);
CREATE INDEX idx_agent_tool_invocation_links_child_session
    ON agent_tool_invocation_links(child_session_id);
CREATE INDEX idx_agent_tool_invocation_links_caller_tool_call
    ON agent_tool_invocation_links(caller_tool_call_id);
CREATE INDEX idx_agent_tool_invocation_links_root_run
    ON agent_tool_invocation_links(root_run_id);
CREATE TABLE run_wait_states (
    wait_id                TEXT PRIMARY KEY,
    continuation_run_id    TEXT NOT NULL,
    run_id                 TEXT NULL,
    target_type            TEXT NULL,
    target_id              TEXT NULL,
    session_id             TEXT NULL,
    agent_id               TEXT NULL,
    user_id                TEXT NULL,
    wait_reason            TEXT NOT NULL,
    correlation_id         TEXT NULL,
    session_state_json     TEXT NOT NULL,
    behavior_json          TEXT NOT NULL,
    artifact_manifest_json TEXT NOT NULL,
    tool_call_id           TEXT NULL,
    tool_name              TEXT NULL,
    approval_request_id    TEXT NULL,
    child_session_id       TEXT NULL,
    parent_run_id          TEXT NULL,
    child_run_id           TEXT NULL,
    root_run_id            TEXT NOT NULL,
    trace_parent           TEXT NULL,
    trace_state            TEXT NULL,
    canonical_event_id     TEXT NOT NULL,
    paused_at              TEXT NOT NULL,
    status                 TEXT NOT NULL CHECK (status IN ('ready', 'executing', 'paused_again', 'completed', 'failed')),
    attempt_count          INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    lease_owner            TEXT NULL,
    lease_expires_at       TEXT NULL,
    completed_at           TEXT NULL,
    failure_code           TEXT NULL,
    CHECK ((status = 'executing') = (lease_owner IS NOT NULL AND lease_expires_at IS NOT NULL)),
    CHECK ((status IN ('completed', 'failed')) = (completed_at IS NOT NULL))
);
CREATE UNIQUE INDEX idx_run_wait_states_active_correlation
    ON run_wait_states(correlation_id)
    WHERE correlation_id IS NOT NULL AND status IN ('ready', 'executing');
CREATE INDEX idx_run_wait_states_continuation_active
    ON run_wait_states(continuation_run_id, status);
CREATE INDEX idx_run_wait_states_session_active
    ON run_wait_states(session_id, status);
CREATE INDEX idx_run_wait_states_session_run
    ON run_wait_states(session_id, run_id);
CREATE INDEX idx_run_wait_states_tool_call
    ON run_wait_states(tool_call_id);
CREATE TABLE provider_type_catalog (
    type TEXT PRIMARY KEY,
    friendly_name TEXT NOT NULL,
    default_base_url TEXT,
    default_http_timeout_seconds INTEGER,
    requires_api_key INTEGER NOT NULL,
    requires_base_url INTEGER NOT NULL,
    supports_http_timeout_seconds INTEGER NOT NULL,
    is_builtin INTEGER NOT NULL,
    seed_version INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE config_snapshots (
    revision        INTEGER PRIMARY KEY AUTOINCREMENT,
    content_hash    TEXT NOT NULL,
    payload_json    TEXT NOT NULL,
    captured_at     TEXT NOT NULL
);
CREATE UNIQUE INDEX idx_config_snapshots_hash
    ON config_snapshots(content_hash);
CREATE TABLE auth_refresh_sessions (
    token_hash TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    revoked_at TEXT NULL,
    rotated_from_hash TEXT NULL
);
CREATE INDEX idx_auth_refresh_sessions_user ON auth_refresh_sessions(user_id);
CREATE INDEX idx_auth_refresh_sessions_expiry ON auth_refresh_sessions(expires_at);
CREATE TABLE auth_user_session_generations (
    user_id TEXT PRIMARY KEY,
    generation INTEGER NOT NULL CHECK (generation >= 0)
);
CREATE TABLE approval_continuation_jobs (
    request_id        TEXT PRIMARY KEY,
    session_id        TEXT NOT NULL,
    target_type       TEXT NOT NULL,
    target_id         TEXT NOT NULL,
    user_id           TEXT NULL,
    approved          INTEGER NOT NULL CHECK (approved IN (0, 1)),
    resolution_source INTEGER NOT NULL,
    reason            TEXT NULL,
    decided_by_user_id TEXT NULL,
    stop_run          INTEGER NOT NULL CHECK (stop_run IN (0, 1)),
    status            TEXT NOT NULL CHECK (status IN ('pending', 'leased', 'completed', 'dead_letter')),
    attempt_count     INTEGER NOT NULL DEFAULT 0,
    available_at      TEXT NOT NULL,
    lease_owner       TEXT NULL,
    lease_expires_at  TEXT NULL,
    last_error        TEXT NULL,
    created_at        TEXT NOT NULL,
    updated_at        TEXT NOT NULL,
    FOREIGN KEY (request_id) REFERENCES tool_approval_requests(id) ON DELETE CASCADE
);
CREATE INDEX ix_approval_continuation_jobs_claim
    ON approval_continuation_jobs(status, available_at, lease_expires_at);
CREATE TABLE approval_resolution_events (
    event_id             TEXT PRIMARY KEY,
    request_id           TEXT NOT NULL UNIQUE,
    payload_json         TEXT NOT NULL,
    agent_id             TEXT NOT NULL,
    session_id           TEXT NOT NULL,
    user_id              TEXT NOT NULL,
    run_id               TEXT NOT NULL,
    root_run_id          TEXT NOT NULL,
    parent_run_id        TEXT NULL,
    parent_session_id    TEXT NULL,
    parent_tool_call_id  TEXT NULL,
    continuation_run_id  TEXT NULL,
    trace_id             TEXT NULL,
    tool_call_id         TEXT NULL,
    checkpoint_id        TEXT NULL,
    status               TEXT NOT NULL CHECK (status IN ('pending', 'leased', 'published', 'dead_letter')),
    available_at         TEXT NOT NULL,
    lease_owner          TEXT NULL,
    lease_expires_at     TEXT NULL,
    published_at         TEXT NULL,
    attempt_count        INTEGER NOT NULL DEFAULT 0,
    last_error           TEXT NULL,
    created_at           TEXT NOT NULL,
    updated_at           TEXT NOT NULL,
    FOREIGN KEY (request_id) REFERENCES tool_approval_requests(id) ON DELETE CASCADE
);
CREATE INDEX ix_approval_resolution_events_claim
    ON approval_resolution_events(status, available_at, lease_expires_at, created_at);
CREATE TABLE execution_quarantine (
    quarantine_id TEXT PRIMARY KEY,
    entity_type TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    reason_code TEXT NOT NULL,
    safe_diagnostic TEXT NOT NULL CHECK (length(safe_diagnostic) <= 1024),
    status TEXT NOT NULL CHECK (status IN ('open', 'retry_requested', 'terminated', 'resolved')),
    retry_count INTEGER NOT NULL DEFAULT 0 CHECK (retry_count >= 0),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    resolved_at TEXT NULL,
    audit_published_at TEXT NULL,
    CHECK ((status IN ('terminated', 'resolved') AND resolved_at IS NOT NULL)
        OR (status IN ('open', 'retry_requested') AND resolved_at IS NULL))
);
CREATE UNIQUE INDEX idx_execution_quarantine_active_entity
    ON execution_quarantine(entity_type, entity_id)
    WHERE status IN ('open', 'retry_requested');
CREATE INDEX idx_execution_quarantine_status
    ON execution_quarantine(status, created_at);
CREATE TABLE memory_extraction_jobs (
    id TEXT PRIMARY KEY,
    target_type TEXT NOT NULL,
    target_id TEXT NOT NULL,
    session_id TEXT NOT NULL,
    user_message TEXT NOT NULL,
    assistant_message TEXT NOT NULL,
    status TEXT NOT NULL CHECK(status IN ('pending','leased','completed','failed')),
    attempt_count INTEGER NOT NULL,
    next_attempt_at TEXT NOT NULL,
    lease_owner TEXT NULL,
    lease_expires_at TEXT NULL,
    last_error TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX idx_memory_extraction_jobs_due
    ON memory_extraction_jobs(status,next_attempt_at,lease_expires_at);
CREATE TABLE usage_staging (
    id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL UNIQUE,
    payload_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    last_error TEXT NULL
);
CREATE INDEX idx_usage_staging_created ON usage_staging(created_at,id);
CREATE TABLE workflow_definitions (
    id TEXT PRIMARY KEY,
    owner_user_id TEXT NOT NULL,
    name TEXT NOT NULL,
    governed INTEGER NOT NULL DEFAULT 0,
    revision INTEGER NOT NULL DEFAULT 1,
    definition_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX idx_workflow_definitions_owner
    ON workflow_definitions(owner_user_id, updated_at DESC);
CREATE TABLE workflow_instances (
    id TEXT PRIMARY KEY,
    definition_id TEXT NOT NULL,
    definition_revision INTEGER NOT NULL,
    owner_user_id TEXT NOT NULL,
    correlation_key TEXT NULL,
    status TEXT NOT NULL CHECK (status IN ('running', 'paused', 'finished', 'error', 'cancelled')),
    stop_requested INTEGER NOT NULL DEFAULT 0,
    current_node_id TEXT NULL,
    envelope_json TEXT NOT NULL,
    envelope_hash TEXT NOT NULL,
    envelope_revision INTEGER NOT NULL DEFAULT 0,
    snapshot_json TEXT NOT NULL,
    maf_checkpoint_json TEXT NULL,
    failure_code TEXT NULL,
    error TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    started_at TEXT NULL,
    ended_at TEXT NULL,
    FOREIGN KEY (definition_id) REFERENCES workflow_definitions(id)
);
CREATE INDEX idx_workflow_instances_definition_created
    ON workflow_instances(definition_id, created_at DESC);
CREATE INDEX idx_workflow_instances_owner_created
    ON workflow_instances(owner_user_id, created_at DESC);
CREATE UNIQUE INDEX idx_workflow_instances_overlap
    ON workflow_instances(definition_id, correlation_key)
    WHERE correlation_key IS NOT NULL AND status IN ('running', 'paused');
CREATE TABLE workflow_step_attempts (
    id TEXT PRIMARY KEY,
    instance_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    attempt INTEGER NOT NULL,
    run_id TEXT NOT NULL,
    session_id TEXT NULL,
    status TEXT NOT NULL CHECK (status IN ('pending', 'running', 'finished', 'failed', 'cancelled')),
    failure_code TEXT NULL,
    started_at TEXT NULL,
    ended_at TEXT NULL,
    created_at TEXT NOT NULL,
    FOREIGN KEY (instance_id) REFERENCES workflow_instances(id) ON DELETE CASCADE,
    UNIQUE(instance_id, run_id),
    UNIQUE(instance_id, node_id, attempt)
);
CREATE INDEX idx_workflow_step_attempts_instance
    ON workflow_step_attempts(instance_id, created_at);
CREATE TABLE workflow_envelope_revisions (
    instance_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    envelope_hash TEXT NOT NULL,
    envelope_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (instance_id, revision),
    FOREIGN KEY (instance_id) REFERENCES workflow_instances(id) ON DELETE CASCADE
);
CREATE TABLE workflow_artifacts (
    id TEXT PRIMARY KEY,
    instance_id TEXT NOT NULL,
    sha256 TEXT NOT NULL,
    name TEXT NOT NULL,
    storage_path TEXT NOT NULL,
    created_at TEXT NOT NULL,
    FOREIGN KEY (instance_id) REFERENCES workflow_instances(id) ON DELETE CASCADE,
    UNIQUE(instance_id, sha256)
);
CREATE INDEX idx_workflow_artifacts_instance
    ON workflow_artifacts(instance_id);
CREATE TABLE workflow_narad_events (
    event_id TEXT PRIMARY KEY,
    instance_id TEXT NOT NULL,
    session_sequence INTEGER NOT NULL,
    workflow_sequence INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    record_class TEXT NOT NULL,
    event_json TEXT NOT NULL,
    occurred_at TEXT NOT NULL,
    UNIQUE(instance_id, session_sequence),
    FOREIGN KEY (instance_id) REFERENCES workflow_instances(id) ON DELETE CASCADE
);
CREATE INDEX idx_workflow_narad_events_instance_sequence
    ON workflow_narad_events(instance_id, session_sequence);
CREATE TABLE external_logins (
    provider TEXT NOT NULL,
    subject TEXT NOT NULL,
    user_id TEXT NOT NULL,
    email TEXT NOT NULL,
    created_at TEXT NOT NULL,
    issuer TEXT,
    tenant_id TEXT,
    object_id TEXT,
    PRIMARY KEY (provider, subject)
);
CREATE TABLE external_login_invites (
    code_hash TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE TABLE runtime_user_password_flags (
    user_id TEXT PRIMARY KEY,
    has_password INTEGER NOT NULL
);

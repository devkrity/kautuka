CREATE TABLE agent_store_schema (schema_id TEXT PRIMARY KEY, created_at TEXT NOT NULL);
INSERT INTO agent_store_schema(schema_id, created_at) VALUES ('agent-store-2026-09-21.1', CURRENT_TIMESTAMP);

CREATE TABLE agent_definition_versions (
                version                  INTEGER PRIMARY KEY,
                agent_id                 TEXT NOT NULL,
                scope                    TEXT NOT NULL,
                enabled                  INTEGER NOT NULL,
                name                     TEXT NOT NULL,
                model_id                 TEXT NOT NULL,
                system_prompt            TEXT NOT NULL,
                super_exec_mode          TEXT NOT NULL,
                session_json             TEXT NOT NULL,
                sandbox_json             TEXT NOT NULL,
                mcp_bindings_json        TEXT NOT NULL DEFAULT '[]',
                skill_attachments_json   TEXT NOT NULL,
                persona_json             TEXT NOT NULL,
                approval_config_json     TEXT NOT NULL,
                tool_exposure_json       TEXT NOT NULL,
                created_at               TEXT NOT NULL
            );

            CREATE TABLE agent_definition_settings (
                version      INTEGER NOT NULL,
                setting_id   TEXT NOT NULL,
                setting_name TEXT NOT NULL,
                value_json   TEXT NOT NULL,
                PRIMARY KEY (version, setting_id),
                FOREIGN KEY (version) REFERENCES agent_definition_versions(version) ON DELETE CASCADE
            );

            CREATE TABLE agent_definition_current (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                version      INTEGER NOT NULL,
                activated_at TEXT NOT NULL,
                FOREIGN KEY (version) REFERENCES agent_definition_versions(version) ON DELETE CASCADE
            );

            CREATE TABLE runtime_sessions (
                id             TEXT    PRIMARY KEY,
                target_type    TEXT    NOT NULL,
                target_id      TEXT    NOT NULL,
                owner_user_id  TEXT    NOT NULL DEFAULT '',
                title          TEXT    NULL,
                compacted      INTEGER NOT NULL DEFAULT 0,
                compact_summary TEXT   NULL,
                estimated_context_tokens INTEGER NULL,
                raw_transcript_estimated_tokens INTEGER NULL,
                last_estimated_at TEXT NULL,
                last_compacted_at TEXT NULL,
                last_compaction_reason TEXT NULL,
                archived_at TEXT NULL,
                title_user_set INTEGER NOT NULL DEFAULT 0,
                originator TEXT NULL,
                created_at     TEXT    NOT NULL,
                updated_at     TEXT    NOT NULL
            );

            CREATE INDEX idx_agent_runtime_sessions_updated
                ON runtime_sessions(updated_at DESC);

            CREATE TABLE runtime_session_messages (
                session_id  TEXT    NOT NULL,
                sequence    INTEGER NOT NULL CHECK (sequence > 0),
                message_id  TEXT    NOT NULL,
                message_json TEXT   NOT NULL,
                PRIMARY KEY (session_id, sequence),
                UNIQUE (session_id, message_id),
                FOREIGN KEY (session_id) REFERENCES runtime_sessions(id) ON DELETE CASCADE
            );

            CREATE TABLE runtime_runs (
                id            TEXT PRIMARY KEY,
                user_id       TEXT NOT NULL,
                target_type   TEXT NOT NULL,
                target_id     TEXT NOT NULL,
                session_id    TEXT NULL,
                interaction_id TEXT NULL,
                status        TEXT NOT NULL DEFAULT 'pending',
                run_json      TEXT NOT NULL DEFAULT '{}',
                created_at    TEXT NOT NULL
            );

            CREATE INDEX idx_agent_runtime_runs_created
                ON runtime_runs(created_at DESC);

            CREATE TABLE runtime_memory_facts (
                id            TEXT PRIMARY KEY,
                target_type   TEXT NOT NULL,
                target_id     TEXT NOT NULL,
                owner_user_id TEXT NOT NULL DEFAULT '',
                title         TEXT NOT NULL,
                content       TEXT NOT NULL,
                category      TEXT NOT NULL,
                source        TEXT NOT NULL,
                confidence    REAL NOT NULL DEFAULT 1.0,
                session_id    TEXT NULL,
                created_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL
            );

            CREATE INDEX idx_agent_runtime_memory_facts_updated
                ON runtime_memory_facts(updated_at DESC);

            CREATE TABLE continuation_checkpoints (
                id                      TEXT PRIMARY KEY,
                continuation_run_id     TEXT NOT NULL,
                checkpoint_index        INTEGER NOT NULL,
                session_id              TEXT NOT NULL,
                agent_id                TEXT NOT NULL,
                user_id                 TEXT,
                run_id                  TEXT NOT NULL,
                boundary_type           TEXT NOT NULL,
                session_state_json      TEXT NOT NULL,
                effective_behavior_json TEXT NOT NULL DEFAULT '',
                artifact_manifest_json  TEXT NOT NULL DEFAULT '[]',
                resume_token_hash       TEXT NOT NULL UNIQUE,
                created_at              TEXT NOT NULL
            );

            CREATE UNIQUE INDEX idx_agent_continuation_checkpoints_run_index
                ON continuation_checkpoints(continuation_run_id, checkpoint_index);

            CREATE INDEX idx_agent_continuation_checkpoints_session
                ON continuation_checkpoints(session_id);

            CREATE INDEX idx_agent_continuation_checkpoints_run
                ON continuation_checkpoints(continuation_run_id);

            CREATE INDEX idx_agent_runtime_runs_session_run
                ON runtime_runs(session_id, id);

            CREATE TABLE narad_runtime_journal (
                event_id               TEXT    PRIMARY KEY,
                protocol_version       TEXT    NOT NULL,
                event_type             TEXT    NOT NULL,
                session_id             TEXT    NOT NULL,
                session_sequence       INTEGER NOT NULL,
                root_run_id            TEXT    NULL,
                run_id                 TEXT    NULL,
                sequence               INTEGER NULL,
                workflow_id            TEXT    NULL,
                workflow_sequence      INTEGER NULL,
                record_class           TEXT    NOT NULL,
                event_json             TEXT    NOT NULL,
                occurred_at            TEXT    NOT NULL,
                CHECK (length(event_id) > 0),
                CHECK (length(protocol_version) > 0),
                CHECK (length(event_type) > 0),
                CHECK (length(session_id) > 0),
                CHECK (session_sequence > 0),
                CHECK (sequence IS NULL OR sequence > 0),
                CHECK (workflow_sequence IS NULL OR workflow_sequence > 0),
                CHECK (record_class = 'authority'),
                CHECK (length(event_json) > 0),
                CHECK (length(occurred_at) > 0),
                CHECK (
                    (root_run_id IS NULL AND run_id IS NULL AND sequence IS NULL)
                    OR (root_run_id IS NOT NULL AND run_id IS NOT NULL AND sequence IS NOT NULL)
                ),
                CHECK (
                    (workflow_id IS NULL AND workflow_sequence IS NULL)
                    OR (workflow_id IS NOT NULL AND workflow_sequence IS NOT NULL)
                ),
                UNIQUE (session_id, session_sequence)
            );

            CREATE INDEX idx_narad_runtime_journal_session_sequence
                ON narad_runtime_journal(session_id, session_sequence);

            -- Canonical run-tree rows only. AAAT parent-session projections copy sequence into
            -- event_json/columns but set sourceEventId and must not consume this uniqueness lane.
            CREATE UNIQUE INDEX idx_narad_runtime_journal_canonical_root_sequence
                ON narad_runtime_journal(root_run_id, sequence)
                WHERE json_extract(event_json, '$.sourceEventId') IS NULL;

            CREATE INDEX idx_narad_runtime_journal_root_sequence
                ON narad_runtime_journal(root_run_id, sequence);

            CREATE INDEX idx_narad_runtime_journal_root_run
                ON narad_runtime_journal(root_run_id, run_id);

            CREATE TABLE runtime_event_sequences (
                sequence_scope TEXT NOT NULL,
                sequence_key   TEXT NOT NULL,
                next_sequence  INTEGER NOT NULL CHECK (next_sequence > 0),
                PRIMARY KEY (sequence_scope, sequence_key)
            );

            CREATE TABLE runtime_event_outbox (
                event_id                    TEXT PRIMARY KEY,
                source_storage_locator_key  TEXT NOT NULL,
                published_at                TEXT NULL,
                mirrored_at                 TEXT NULL,
                attempt_count               INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
                last_error                  TEXT NULL,
                available_at                TEXT NOT NULL,
                lease_owner                 TEXT NULL,
                lease_expires_at            TEXT NULL,
                dead_lettered_at            TEXT NULL,
                dismissed_at                TEXT NULL,
                FOREIGN KEY (event_id) REFERENCES narad_runtime_journal(event_id) ON DELETE CASCADE
            );

            CREATE INDEX idx_runtime_event_outbox_pending
                ON runtime_event_outbox(available_at, lease_expires_at, event_id)
                WHERE (published_at IS NULL OR mirrored_at IS NULL)
                  AND dead_lettered_at IS NULL
                  AND dismissed_at IS NULL;

            CREATE INDEX idx_runtime_event_outbox_dead_letter
                ON runtime_event_outbox(dead_lettered_at, event_id)
                WHERE dead_lettered_at IS NOT NULL AND dismissed_at IS NULL;

            CREATE TABLE runtime_memory_vector_metadata (
                singleton_id    INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                sqlite_vec_path TEXT NOT NULL,
                vec_version     TEXT NOT NULL,
                validated_at    TEXT NOT NULL
            );

            CREATE TABLE runtime_memory_embeddings (
                embedding_id  INTEGER PRIMARY KEY,
                fact_id       TEXT NOT NULL UNIQUE,
                target_type   TEXT NOT NULL,
                target_id     TEXT NOT NULL,
                model_key     TEXT NOT NULL,
                dimensions    INTEGER NOT NULL,
                source_text   TEXT NOT NULL,
                created_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL
            );

            CREATE INDEX idx_runtime_memory_embeddings_target
                ON runtime_memory_embeddings(target_type, target_id, updated_at DESC);

            CREATE INDEX idx_runtime_memory_embeddings_model
                ON runtime_memory_embeddings(model_key, dimensions, updated_at DESC);

            CREATE TABLE agent_tool_surface_current (
                singleton_id   INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                fingerprint    TEXT    NOT NULL,
                catalogue_json TEXT    NOT NULL,
                built_at       TEXT    NOT NULL,
                CHECK (length(fingerprint) > 0),
                CHECK (length(catalogue_json) > 0),
                CHECK (length(built_at) > 0)
            );

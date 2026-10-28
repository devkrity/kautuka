using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Protos;

using Microsoft.Data.Sqlite;

namespace Kautuka.ChannelHost.Hosting;

[SuppressMessage("Performance", "CA1812", Justification = "Constructed through dependency injection in Program.cs")]
internal sealed class ChannelAttachmentStore(ChannelHostDatabase database, ChannelRuntimeCredentialStore? runtimeCredentials = null)
{
    private const string telegramProviderKey = "telegram";
    private const string attachmentSchemaVersion = "telegram/v1";
    private const string capabilityVersion = "telegram/v1";
    private readonly Lock sync = new();
    private readonly Dictionary<string, ChannelAttachmentState> attachments = new(StringComparer.Ordinal);
    private readonly ChannelRuntimeCredentialStore runtimeCredentials = runtimeCredentials ?? new ChannelRuntimeCredentialStore();

    public GetAttachmentsResponse GetAttachments(string? ownerUserId, bool includeInactive)
    {
        List<AttachmentSummary> attachments;
        lock (sync)
        {
            EnsureLoadedLocked();
            attachments = [.. this.attachments.Values
                .Where(item => (includeInactive || item.Status == AttachmentStatus.Active)
                    && (string.IsNullOrWhiteSpace(ownerUserId) || string.Equals(item.OwnerUserId, ownerUserId, StringComparison.Ordinal)))
                .OrderBy(static item => item.DisplayName, StringComparer.Ordinal)
                .Select(ToSummary)];
        }

        GetAttachmentsResponse response = new();
        response.Attachments.AddRange(attachments);
        return response;
    }

    public GetAttachmentResponse? GetAttachment(string attachmentId)
    {
        lock (sync)
        {
            EnsureLoadedLocked();
            if (!attachments.TryGetValue(attachmentId, out ChannelAttachmentState? state))
            {
                return null;
            }

            GetAttachmentResponse response = new()
            {
                Attachment = ToSummary(state),
            };
            response.Settings.AddRange(ToSettingValues(state.Settings));
            response.CredentialReferences.AddRange(state.CredentialReferences.Select(static item => new AttachmentCredentialReference
            {
                FieldKey = item.Key,
                CredentialKey = item.Value?.GetValue<string>() ?? string.Empty,
            }));
            return response;
        }
    }

    public AttachmentSummary? GetAttachmentSummary(string attachmentId)
    {
        lock (sync)
        {
            EnsureLoadedLocked();
            return attachments.TryGetValue(attachmentId, out ChannelAttachmentState? state)
                ? ToSummary(state)
                : null;
        }
    }

    public string? GetAttachmentOwner(string attachmentId)
    {
        lock (sync)
        {
            EnsureLoadedLocked();
            return attachments.TryGetValue(attachmentId, out ChannelAttachmentState? state) ? state.OwnerUserId : null;
        }
    }

    public string? GetAttachmentProvider(string attachmentId)
    {
        lock (sync)
        {
            EnsureLoadedLocked();
            return attachments.TryGetValue(attachmentId, out ChannelAttachmentState? state) ? state.ProviderKey : null;
        }
    }

    public string? GetAttachmentTenant(string attachmentId)
    {
        lock (sync)
        {
            EnsureLoadedLocked();
            return attachments.TryGetValue(attachmentId, out ChannelAttachmentState? state)
                ? ReadString(state.Settings, "tenant_id") is { Length: > 0 } tenant ? tenant : "default"
                : null;
        }
    }

    public bool IsConversationBound(string attachmentId, string conversationId)
    {
        EnsureSchema();
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM channel_attachment_conversations WHERE attachment_id=$attachment AND conversation_id=$conversation";
        command.Parameters.AddWithValue("$attachment", attachmentId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        return (long)(command.ExecuteScalar() ?? 0L) > 0;
    }

    public UpsertAttachmentResponse UpsertAttachment(UpsertAttachmentRequest request)
    {
        ValidateUpsert(request);
        if (request.Settings.Any(static item => item.FieldKey is "bot_token" or "app_token" or "signing_secret"))
            throw new InvalidOperationException("Secret attachment settings must be stored as CredentialsHost references.");
        JsonObject settings = ToSettingsObject(request.Settings);
        string tenantId = string.IsNullOrWhiteSpace(request.Context?.TenantId) ? "default" : request.Context.TenantId;
        settings["tenant_id"] = tenantId;
        JsonObject credentialReferences = new(request.CredentialReferences
            .Where(static item => !string.IsNullOrWhiteSpace(item.FieldKey) && !string.IsNullOrWhiteSpace(item.CredentialKey))
            .Select(static item => KeyValuePair.Create<string, JsonNode?>(item.FieldKey, item.CredentialKey)));
        string ownerUserId = ReadString(settings, "owner_user_id");
        string displayName = ReadString(settings, "display_name");
        string providerKey = string.IsNullOrWhiteSpace(request.ProviderKey) ? telegramProviderKey : request.ProviderKey;
        string now = DateTimeOffset.UtcNow.ToString("O");

        lock (sync)
        {
            EnsureLoadedLocked();
            attachments.TryGetValue(request.AttachmentId, out ChannelAttachmentState? existing);
            if (existing is not null && string.Equals(providerKey, telegramProviderKey, StringComparison.Ordinal))
            {
                bool sameDestination = string.Equals(ReadString(existing.Settings, "channel_device_id"), ReadString(settings, "channel_device_id"), StringComparison.Ordinal);
                // Topic checkpoints belong to ChannelHost. A stale Gateway settings save must
                // not roll them back; changing the destination invalidates their scope.
                foreach (string key in new[] { "target_threads", "target_thread_labels", "target_thread_session_titles", "retired_target_threads", "telegram_bot_id", "telegram_bot_username" })
                {
                    if (sameDestination && existing.Settings.TryGetPropertyValue(key, out JsonNode? value)) settings[key] = value?.DeepClone();
                    else settings.Remove(key);
                }
            }
            else if (existing is not null && string.Equals(providerKey, "slack", StringComparison.Ordinal))
            {
                // Slack identity is learned and owned by ChannelHost. A Gateway settings
                // upsert must never erase the workspace/app claim or bot identity.
                foreach (string key in new[] { "team_id", "app_id", "bot_user_id", "bot_id" })
                    if (string.IsNullOrWhiteSpace(ReadString(settings, key)) && existing.Settings.TryGetPropertyValue(key, out JsonNode? value))
                        settings[key] = value?.DeepClone();
            }
            ChannelAttachmentState state = new(
                request.AttachmentId,
                providerKey,
                ownerUserId,
                string.IsNullOrWhiteSpace(displayName) ? existing?.DisplayName ?? request.AttachmentId : displayName,
                // Save & Connect (upsert) must reactivate INACTIVE registrations. Do not require
                // recover_requires_validation — delivery status can overwrite that marker while still inactive.
                existing is null
                    || existing.Status is AttachmentStatus.Active or AttachmentStatus.Inactive
                    ? AttachmentStatus.Active
                    : existing.Status,
                string.IsNullOrWhiteSpace(request.ExpectedSchemaVersion) ? existing?.SchemaVersion ?? attachmentSchemaVersion : request.ExpectedSchemaVersion,
                existing?.CapabilityVersion ?? capabilityVersion,
                settings,
                credentialReferences,
                existing?.LastProvisionedAtUtc ?? string.Empty,
                existing?.LastDeliveryStatus ?? string.Empty,
                now);
            try
            {
                PersistTransitionLocked(state, existing?.Status ?? AttachmentStatus.Unspecified, state.Status,
                    existing is null ? "create_activate" : "validate_update", request.Context?.RequestId);
            }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067 && providerKey == "slack")
            {
                return new UpsertAttachmentResponse
                {
                    ValidationStatus = ValidationStatus.Rejected,
                    ValidationIssues = { new ValidationIssue
                    {
                        FieldKey = "team_id",
                        Code = "slack_workspace_in_use",
                        Message = "This Slack workspace is already connected to another Kautuka member. For now each workspace can be connected by one member.",
                    } },
                };
            }
            attachments[request.AttachmentId] = state;
            BindConfiguredConversation(state.AttachmentId, ReadString(settings, "conversation_id"), "configured");
            // Provider setup uses channel_device_id as the canonical outbound destination.
            // Admit only that persisted, validated setting; never infer authority from message payload JSON.
            BindConfiguredConversation(state.AttachmentId, ReadString(settings, "channel_device_id"), "configured_destination");

            return new UpsertAttachmentResponse
            {
                Attachment = ToSummary(state),
                ValidationStatus = ValidationStatus.Accepted,
            };
        }
    }

    public static void ValidateUpsert(UpsertAttachmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (AttachmentSettingValue setting in request.Settings.Where(static item =>
                     item.FieldKey is "conversation_id" or "channel_device_id"))
            ValidateConfiguredConversationValue(setting.StringValue);
    }

    public bool DeactivateAttachment(string attachmentId, string? operationId = null)
    {
        lock (sync)
        {
            EnsureLoadedLocked();
            if (!attachments.TryGetValue(attachmentId, out ChannelAttachmentState? state))
            {
                return false;
            }

            if (state.Status is AttachmentStatus.Inactive or AttachmentStatus.Deleted) return true;
            ChannelAttachmentState updated = state with { Status = AttachmentStatus.Inactive, UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") };
            PersistTransitionLocked(updated, state.Status, updated.Status, "deactivate", operationId);
            attachments[attachmentId] = updated;
            return true;
        }
    }

    public bool SuspendAttachment(string attachmentId, string reason, string operationId)
        => TransitionAttachment(attachmentId, AttachmentStatus.Suspended, "suspend:" + reason, operationId,
            [AttachmentStatus.Active, AttachmentStatus.Provisioning, AttachmentStatus.Error]);

    public bool RecoverAttachment(string attachmentId, string operationId)
        => TransitionAttachment(attachmentId, AttachmentStatus.Inactive, "recover_requires_validation", operationId,
            [AttachmentStatus.Suspended, AttachmentStatus.Error, AttachmentStatus.RecoveryRequired, AttachmentStatus.Deleted]);

    public bool DeleteAttachment(string attachmentId, string operationId)
        => TransitionAttachment(attachmentId, AttachmentStatus.Deleted, "soft_delete", operationId,
            [AttachmentStatus.Inactive, AttachmentStatus.Suspended, AttachmentStatus.RecoveryRequired]);

    private bool TransitionAttachment(
        string attachmentId,
        AttachmentStatus target,
        string reason,
        string operationId,
        AttachmentStatus[] allowedFrom)
    {
        lock (sync)
        {
            EnsureLoadedLocked();
            if (!attachments.TryGetValue(attachmentId, out ChannelAttachmentState? state)) return false;
            if (state.Status == target) return true;
            if (!allowedFrom.Contains(state.Status)) return false;
            ChannelAttachmentState updated = state with
            {
                Status = target,
                LastDeliveryStatus = reason,
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            };
            PersistTransitionLocked(updated, state.Status, target, reason, operationId);
            attachments[attachmentId] = updated;
            if (target is AttachmentStatus.Suspended or AttachmentStatus.Inactive or AttachmentStatus.Deleted)
                runtimeCredentials.Remove(attachmentId);
            return true;
        }
    }

    public bool TryGetActiveAttachment(string attachmentId, out ChannelAttachmentRecord? attachment)
    {
        lock (sync)
        {
            EnsureLoadedLocked();
            if (!attachments.TryGetValue(attachmentId, out ChannelAttachmentState? state)
                || state.Status != AttachmentStatus.Active)
            {
                attachment = null;
                return false;
            }

            attachment = new ChannelAttachmentRecord(
                state.AttachmentId,
                state.ProviderKey,
                state.OwnerUserId,
                state.DisplayName,
                state.SchemaVersion,
                state.CapabilityVersion,
                ParseJsonElement(state.Settings));
            return true;
        }
    }

    public IReadOnlyList<ChannelAttachmentRecord> ListActiveAttachments(string providerKey, string? ownerUserId = null)
    {
        lock (sync)
        {
            EnsureLoadedLocked();
            return [.. attachments.Values
                .Where(item => item.Status == AttachmentStatus.Active
                    && string.Equals(item.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(ownerUserId) || string.Equals(item.OwnerUserId, ownerUserId, StringComparison.Ordinal)))
                .Select(item => new ChannelAttachmentRecord(
                    item.AttachmentId,
                    item.ProviderKey,
                    item.OwnerUserId,
                    item.DisplayName,
                    item.SchemaVersion,
                    item.CapabilityVersion,
                    ParseJsonElement(item.Settings)))];
        }
    }

    public bool TryGetTelegramRegistration(string attachmentId, out TelegramAttachmentRegistration? registration)
        => TryGetTelegramRegistration(attachmentId, allowProvisioning: false, out registration);

    public bool TryGetTelegramRegistrationForProvisioning(string attachmentId, out TelegramAttachmentRegistration? registration)
        => TryGetTelegramRegistration(attachmentId, allowProvisioning: true, out registration);

    private bool TryGetTelegramRegistration(
        string attachmentId,
        bool allowProvisioning,
        out TelegramAttachmentRegistration? registration)
    {
        lock (sync)
        {
            EnsureLoadedLocked();
            if (!attachments.TryGetValue(attachmentId, out ChannelAttachmentState? state)
                || (state.Status != AttachmentStatus.Active
                    && (!allowProvisioning || state.Status != AttachmentStatus.Provisioning))
                || !string.Equals(state.ProviderKey, telegramProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                registration = null;
                return false;
            }

            string botToken = runtimeCredentials.Get(state.AttachmentId, "bot_token");
            if (string.IsNullOrWhiteSpace(botToken))
            {
                registration = null;
                return false;
            }

            int pollTimeoutSeconds = ReadInt(state.Settings, "poll_timeout_seconds", 25);
            registration = new TelegramAttachmentRegistration(
                state.AttachmentId,
                state.OwnerUserId,
                botToken,
                Math.Clamp(pollTimeoutSeconds, 5, 25),
                ParseJsonElement(state.Settings));
            return true;
        }
    }

    public ChannelAttachmentRecord? GetAttachmentForProvisioning(string attachmentId, string providerKey)
    {
        lock (sync)
        {
            EnsureLoadedLocked();
            if (!attachments.TryGetValue(attachmentId, out ChannelAttachmentState? state)
                || state.Status is not (AttachmentStatus.Active or AttachmentStatus.Provisioning)
                || !string.Equals(state.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new ChannelAttachmentRecord(
                state.AttachmentId,
                state.ProviderKey,
                state.OwnerUserId,
                state.DisplayName,
                state.SchemaVersion,
                state.CapabilityVersion,
                ParseJsonElement(state.Settings));
        }
    }

    public IReadOnlyList<TelegramAttachmentRegistration> ListActiveTelegramRegistrations(string? ownerUserId = null)
    {
        lock (sync)
        {
            EnsureLoadedLocked();
            return [.. attachments.Values
                .Where(item => item.Status == AttachmentStatus.Active
                    && string.Equals(item.ProviderKey, telegramProviderKey, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(ownerUserId) || string.Equals(item.OwnerUserId, ownerUserId, StringComparison.Ordinal)))
                .Select(item =>
                {
                    string botToken = runtimeCredentials.Get(item.AttachmentId, "bot_token");
                    if (string.IsNullOrWhiteSpace(botToken))
                    {
                        return null;
                    }

                    return new TelegramAttachmentRegistration(
                        item.AttachmentId,
                        item.OwnerUserId,
                        botToken,
                        Math.Clamp(ReadInt(item.Settings, "poll_timeout_seconds", 25), 5, 25),
                        ParseJsonElement(item.Settings));
                })
                .Where(static item => item is not null)!];
        }
    }

    public void ApplySettingsPatch(string attachmentId, JsonObject patch)
    {
        lock (sync)
        {
            EnsureLoadedLocked();
            if (!attachments.TryGetValue(attachmentId, out ChannelAttachmentState? state))
            {
                return;
            }

            JsonObject merged = CloneJsonObject(state.Settings);
            foreach (KeyValuePair<string, JsonNode?> entry in patch)
            {
                merged[entry.Key] = entry.Value?.DeepClone();
            }

            ChannelAttachmentState updated = state with { Settings = merged, UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") };
            PersistAttachmentLocked(updated);
            attachments[attachmentId] = updated;
        }
    }

    public bool TryClaimSlackIdentity(string attachmentId, JsonObject patch, out string? conflict)
    {
        conflict = null;
        lock (sync)
        {
            EnsureLoadedLocked();
            if (!attachments.TryGetValue(attachmentId, out ChannelAttachmentState? state)
                || !string.Equals(state.ProviderKey, "slack", StringComparison.OrdinalIgnoreCase))
                return false;

            JsonObject merged = CloneJsonObject(state.Settings);
            foreach (KeyValuePair<string, JsonNode?> entry in patch)
                merged[entry.Key] = entry.Value?.DeepClone();
            ChannelAttachmentState updated = state with { Settings = merged, UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") };
            EnsureSchema();
            using SqliteConnection connection = database.OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            try
            {
                PersistAttachmentLocked(updated, connection, transaction);
                transaction.Commit();
                attachments[attachmentId] = updated;
                return true;
            }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
            {
                conflict = "slack_workspace_in_use";
                return false;
            }
        }
    }

    public void MarkProvisioned(string attachmentId, string detail, string operationId)
    {
        lock (sync)
        {
            EnsureLoadedLocked();
            if (attachments.TryGetValue(attachmentId, out ChannelAttachmentState? state))
            {
                ChannelAttachmentState updated = state with
                {
                    Status = AttachmentStatus.Active,
                    LastProvisionedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                    LastDeliveryStatus = detail,
                    UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                };
                PersistTransitionLocked(updated, state.Status, updated.Status, "provision_succeeded:" + detail, operationId);
                attachments[attachmentId] = updated;
            }
        }
    }

    public bool MarkProvisioningStarted(string attachmentId, string operationId)
        => TransitionAttachment(attachmentId, AttachmentStatus.Provisioning, "provisioning_started", operationId,
            [AttachmentStatus.Active, AttachmentStatus.Inactive, AttachmentStatus.Error]);

    public bool MarkProvisioningFailed(string attachmentId, string detail, string operationId)
        => TransitionAttachment(attachmentId, AttachmentStatus.RecoveryRequired, "provisioning_failed:" + detail, operationId,
            [AttachmentStatus.Active, AttachmentStatus.Provisioning, AttachmentStatus.Error]);

    public void MarkDeliveryStatus(string attachmentId, string detail)
    {
        lock (sync)
        {
            EnsureLoadedLocked();
            if (attachments.TryGetValue(attachmentId, out ChannelAttachmentState? state)
                && state.Status == AttachmentStatus.Active)
            {
                ChannelAttachmentState updated = state with { LastDeliveryStatus = detail, UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") };
                attachments[attachmentId] = updated;
                PersistAttachmentLocked(updated);
            }
        }
    }

    public ChannelStreamDelivery? GetStreamDelivery(string attachmentId, string streamId)
    {
        EnsureSchema();
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT transport_message_id, last_sequence, is_final FROM runtime_channel_streams WHERE attachment_id = $attachmentId AND stream_id = $streamId";
        command.Parameters.AddWithValue("$attachmentId", attachmentId);
        command.Parameters.AddWithValue("$streamId", streamId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? new ChannelStreamDelivery(reader.GetString(0), reader.GetInt64(1), reader.GetBoolean(2)) : null;
    }

    public void RecordStreamDelivery(string attachmentId, string streamId, string transportMessageId, long sequence, bool isFinal)
    {
        EnsureSchema();
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO runtime_channel_streams (attachment_id, stream_id, transport_message_id, last_sequence, is_final, updated_at_utc)
            VALUES ($attachmentId, $streamId, $transportMessageId, $sequence, $isFinal, $updatedAt)
            ON CONFLICT(attachment_id, stream_id) DO UPDATE SET
                transport_message_id = excluded.transport_message_id,
                last_sequence = MAX(runtime_channel_streams.last_sequence, excluded.last_sequence),
                is_final = runtime_channel_streams.is_final OR excluded.is_final,
                updated_at_utc = excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$attachmentId", attachmentId);
        command.Parameters.AddWithValue("$streamId", streamId);
        command.Parameters.AddWithValue("$transportMessageId", transportMessageId);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$isFinal", isFinal);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        _ = command.ExecuteNonQuery();
    }

    private void EnsureLoadedLocked()
    {
        if (attachments.Count > 0)
        {
            return;
        }

        EnsureSchema();
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT attachment_id, provider_key, owner_user_id, display_name, status, schema_version, capability_version, settings_json, credential_references_json, last_provisioned_at_utc, last_delivery_status, updated_at_utc FROM runtime_channel_attachments";

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            ChannelAttachmentState state = new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ParseStatus(reader.GetString(4)),
                reader.GetString(5),
                reader.GetString(6),
                ParseJsonObject(reader.GetString(7)),
                ParseJsonObject(reader.GetString(8)),
                reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                reader.IsDBNull(11) ? string.Empty : reader.GetString(11));
            attachments[state.AttachmentId] = state;
        }
    }

    private void PersistAttachmentLocked(ChannelAttachmentState state)
    {
        EnsureSchema();
        using SqliteConnection connection = database.OpenConnection();
        PersistAttachmentLocked(state, connection, null);
    }

    private static void PersistAttachmentLocked(ChannelAttachmentState state, SqliteConnection connection, SqliteTransaction? transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO runtime_channel_attachments (
                attachment_id, provider_key, owner_user_id, display_name, status,
                schema_version, capability_version, settings_json,
                credential_references_json,
                last_provisioned_at_utc, last_delivery_status, updated_at_utc)
            VALUES (
                $attachmentId, $providerKey, $ownerUserId, $displayName, $status,
                $schemaVersion, $capabilityVersion, $settingsJson,
                $credentialReferencesJson,
                $lastProvisionedAtUtc, $lastDeliveryStatus, $updatedAtUtc)
            ON CONFLICT(attachment_id) DO UPDATE SET
                provider_key = excluded.provider_key,
                owner_user_id = excluded.owner_user_id,
                display_name = excluded.display_name,
                status = excluded.status,
                schema_version = excluded.schema_version,
                capability_version = excluded.capability_version,
                settings_json = excluded.settings_json,
                credential_references_json = excluded.credential_references_json,
                last_provisioned_at_utc = excluded.last_provisioned_at_utc,
                last_delivery_status = excluded.last_delivery_status,
                updated_at_utc = excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$attachmentId", state.AttachmentId);
        command.Parameters.AddWithValue("$providerKey", state.ProviderKey);
        command.Parameters.AddWithValue("$ownerUserId", state.OwnerUserId);
        command.Parameters.AddWithValue("$displayName", state.DisplayName);
        command.Parameters.AddWithValue("$status", state.Status.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("$schemaVersion", state.SchemaVersion);
        command.Parameters.AddWithValue("$capabilityVersion", state.CapabilityVersion);
        command.Parameters.AddWithValue("$settingsJson", state.Settings.ToJsonString());
        command.Parameters.AddWithValue("$credentialReferencesJson", state.CredentialReferences.ToJsonString());
        command.Parameters.AddWithValue("$lastProvisionedAtUtc", string.IsNullOrWhiteSpace(state.LastProvisionedAtUtc) ? DBNull.Value : state.LastProvisionedAtUtc);
        command.Parameters.AddWithValue("$lastDeliveryStatus", string.IsNullOrWhiteSpace(state.LastDeliveryStatus) ? DBNull.Value : state.LastDeliveryStatus);
        command.Parameters.AddWithValue("$updatedAtUtc", state.UpdatedAtUtc);
        _ = command.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using SqliteConnection connection = database.OpenConnection();
        using (SqliteCommand inspect = connection.CreateCommand())
        {
            inspect.CommandText = "SELECT COUNT(*) FROM pragma_table_info('runtime_channel_attachments') WHERE name='credential_references_json'";
            long currentSchema = (long)(inspect.ExecuteScalar() ?? 0L);
            using SqliteCommand exists = connection.CreateCommand();
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='runtime_channel_attachments'";
            bool legacyTableExists = (long)(exists.ExecuteScalar() ?? 0L) > 0;
            if (legacyTableExists && currentSchema == 0)
            {
                using SqliteCommand scrub = connection.CreateCommand();
                scrub.CommandText = "DROP TABLE runtime_channel_attachments";
                _ = scrub.ExecuteNonQuery();
            }
        }
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS runtime_channel_attachments (
                attachment_id TEXT PRIMARY KEY,
                provider_key TEXT NOT NULL,
                owner_user_id TEXT NOT NULL,
                display_name TEXT NOT NULL,
                status TEXT NOT NULL,
                schema_version TEXT NOT NULL,
                capability_version TEXT NOT NULL,
                settings_json TEXT NOT NULL DEFAULT '{}',
                credential_references_json TEXT NOT NULL DEFAULT '{}',
                last_provisioned_at_utc TEXT NULL,
                last_delivery_status TEXT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_runtime_channel_attachments_owner ON runtime_channel_attachments(owner_user_id);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_runtime_channel_slack_team_active
                ON runtime_channel_attachments(json_extract(settings_json, '$.team_id'))
                WHERE provider_key = 'slack' AND status NOT IN ('INACTIVE','DELETED') AND COALESCE(json_extract(settings_json, '$.team_id'), '') <> '';
            CREATE UNIQUE INDEX IF NOT EXISTS idx_runtime_channel_slack_app_active
                ON runtime_channel_attachments(json_extract(settings_json, '$.app_id'))
                WHERE provider_key = 'slack' AND status NOT IN ('INACTIVE','DELETED') AND COALESCE(json_extract(settings_json, '$.app_id'), '') <> '';
            CREATE TABLE IF NOT EXISTS runtime_channel_streams (
                attachment_id TEXT NOT NULL,
                stream_id TEXT NOT NULL,
                transport_message_id TEXT NOT NULL,
                last_sequence INTEGER NOT NULL,
                is_final INTEGER NOT NULL DEFAULT 0,
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY (attachment_id, stream_id),
                FOREIGN KEY (attachment_id) REFERENCES runtime_channel_attachments(attachment_id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS channel_registration_lifecycle_audit(
                audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
                operation_id TEXT NOT NULL,
                attachment_id TEXT NOT NULL,
                owner_user_id TEXT NOT NULL,
                provider_key TEXT NOT NULL,
                from_status TEXT NOT NULL,
                to_status TEXT NOT NULL,
                reason TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                UNIQUE(operation_id,attachment_id,to_status));
            CREATE INDEX IF NOT EXISTS idx_channel_registration_lifecycle_attachment
                ON channel_registration_lifecycle_audit(attachment_id,audit_id);
            CREATE TABLE IF NOT EXISTS channel_attachment_conversations(
                attachment_id TEXT NOT NULL,
                conversation_id TEXT NOT NULL,
                binding_source TEXT NOT NULL,
                observed_at_utc TEXT NOT NULL,
                PRIMARY KEY(attachment_id,conversation_id));
            """;
        _ = command.ExecuteNonQuery();
    }


    private void BindConversation(string attachmentId, string conversationId, string source)
    {
        EnsureSchema();
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO channel_attachment_conversations(attachment_id,conversation_id,binding_source,observed_at_utc) VALUES($attachment,$conversation,$source,$now)";
        command.Parameters.AddWithValue("$attachment", attachmentId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        _ = command.ExecuteNonQuery();
    }

    private void BindConfiguredConversation(string attachmentId, string conversationId, string source)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return;
        ValidateConfiguredConversationValue(conversationId);
        BindConversation(attachmentId, conversationId, source);
    }

    private static void ValidateConfiguredConversationValue(string conversationId)
    {
        if (!string.IsNullOrWhiteSpace(conversationId)
            && (conversationId.Length > 256 || conversationId.Any(char.IsControl)))
            throw new InvalidOperationException("configured_conversation_id_invalid");
    }

    private void PersistTransitionLocked(
        ChannelAttachmentState state,
        AttachmentStatus from,
        AttachmentStatus to,
        string reason,
        string? operationId)
    {
        EnsureSchema();
        using SqliteConnection connection = database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        PersistAttachmentLocked(state, connection, transaction);
        RecordLifecycleLocked(state, from, to, reason, operationId, connection, transaction);
        transaction.Commit();
    }

    private static void RecordLifecycleLocked(
        ChannelAttachmentState state,
        AttachmentStatus from,
        AttachmentStatus to,
        string reason,
        string? operationId,
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO channel_registration_lifecycle_audit(
                operation_id,attachment_id,owner_user_id,provider_key,from_status,to_status,reason,occurred_at_utc)
            VALUES($operation,$attachment,$owner,$provider,$from,$to,$reason,$occurred)
            ON CONFLICT(operation_id,attachment_id,to_status) DO NOTHING
            """;
        command.Parameters.AddWithValue("$operation", string.IsNullOrWhiteSpace(operationId) ? Guid.NewGuid().ToString("N") : operationId);
        command.Parameters.AddWithValue("$attachment", state.AttachmentId);
        command.Parameters.AddWithValue("$owner", state.OwnerUserId);
        command.Parameters.AddWithValue("$provider", state.ProviderKey);
        command.Parameters.AddWithValue("$from", from.ToString());
        command.Parameters.AddWithValue("$to", to.ToString());
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$occurred", DateTimeOffset.UtcNow.ToString("O"));
        _ = command.ExecuteNonQuery();
    }

    private static AttachmentSummary ToSummary(ChannelAttachmentState state) => new()
    {
        AttachmentId = state.AttachmentId,
        DisplayName = state.DisplayName,
        ProviderKey = state.ProviderKey,
        Status = state.Status,
        SchemaVersion = state.SchemaVersion,
        CapabilityVersion = state.CapabilityVersion,
        ProviderInstanceDisplayName = state.DisplayName,
        LastProvisionedAtUtc = state.LastProvisionedAtUtc,
        LastDeliveryStatus = state.LastDeliveryStatus,
    };

    private static AttachmentStatus ParseStatus(string value)
        => Enum.TryParse<AttachmentStatus>(value, ignoreCase: true, out AttachmentStatus status)
            ? status
            : AttachmentStatus.Active;

    private static JsonObject ToSettingsObject(IEnumerable<AttachmentSettingValue> settings)
    {
        JsonObject result = new();
        foreach (AttachmentSettingValue setting in settings)
        {
            if (!string.IsNullOrWhiteSpace(setting.JsonValue))
            {
                result[setting.FieldKey] = JsonNode.Parse(setting.JsonValue);
            }
            else if (setting.MultiValues.Count > 0)
            {
                result[setting.FieldKey] = new JsonArray([.. setting.MultiValues.Select(static item => (JsonNode?)item)]);
            }
            else if (!string.IsNullOrWhiteSpace(setting.StringValue))
            {
                result[setting.FieldKey] = setting.StringValue;
            }
            else if (Math.Abs(setting.NumberValue) > double.Epsilon)
            {
                result[setting.FieldKey] = setting.NumberValue;
            }
            else
            {
                result[setting.FieldKey] = setting.BoolValue;
            }
        }

        RemoveInvalidOptionalString(result, "channel_device_id");
        RemoveInvalidOptionalString(result, "conversation_id");

        return result;
    }

    private static void RemoveInvalidOptionalString(JsonObject settings, string fieldKey)
    {
        if (settings[fieldKey] is not JsonValue value
            || !value.TryGetValue<string>(out string? text)
            || string.IsNullOrWhiteSpace(text))
        {
            settings.Remove(fieldKey);
        }
    }

    public static IEnumerable<AttachmentSettingValue> ToSettingValues(JsonObject settings)
    {
        foreach (KeyValuePair<string, JsonNode?> entry in settings)
        {
            if (entry.Value is null)
            {
                continue;
            }

            AttachmentSettingValue value = new() { FieldKey = entry.Key };
            switch (entry.Value)
            {
                case JsonArray array:
                    foreach (JsonNode? item in array)
                    {
                        value.MultiValues.Add(item?.ToString() ?? string.Empty);
                    }
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<string>(out string? stringValue):
                    if (string.IsNullOrWhiteSpace(stringValue))
                    {
                        continue;
                    }
                    value.StringValue = stringValue;
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<bool>(out bool boolValue):
                    value.BoolValue = boolValue;
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<int>(out int intValue):
                    value.NumberValue = intValue;
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<long>(out long longValue):
                    value.NumberValue = longValue;
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<double>(out double doubleValue):
                    value.NumberValue = doubleValue;
                    break;
                default:
                    value.JsonValue = entry.Value.ToJsonString();
                    break;
            }

            yield return value;
        }
    }

    private static JsonObject ParseJsonObject(string json)
        => JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? [];

    private static JsonObject CloneJsonObject(JsonObject settings)
        => ParseJsonObject(settings.ToJsonString());

    private static JsonElement ParseJsonElement(JsonObject settings)
    {
        using JsonDocument document = JsonDocument.Parse(settings.ToJsonString());
        return document.RootElement.Clone();
    }

    private static string ReadString(JsonObject settings, string propertyName)
        => settings[propertyName] is JsonValue value
            && value.TryGetValue<string>(out string? text)
                ? text.Trim()
                : string.Empty;

    private static int ReadInt(JsonObject settings, string propertyName, int fallback)
        => settings[propertyName] is JsonNode node && int.TryParse(node.ToJsonString().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;

    private sealed record ChannelAttachmentState(
        string AttachmentId,
        string ProviderKey,
        string OwnerUserId,
        string DisplayName,
        AttachmentStatus Status,
        string SchemaVersion,
        string CapabilityVersion,
        JsonObject Settings,
        JsonObject CredentialReferences,
        string LastProvisionedAtUtc,
        string LastDeliveryStatus,
        string UpdatedAtUtc);
}

internal sealed record TelegramAttachmentRegistration(
    string AttachmentId,
    string OwnerUserId,
    string BotToken,
    int PollTimeoutSeconds,
    JsonElement Settings);

internal sealed record ChannelAttachmentRecord(
    string AttachmentId,
    string ProviderKey,
    string OwnerUserId,
    string DisplayName,
    string SchemaVersion,
    string CapabilityVersion,
    JsonElement Settings);

internal sealed record ChannelStreamDelivery(string TransportMessageId, long LastSequence, bool IsFinal);

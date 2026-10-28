using System.Text.Json;

using Dapper;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.State;

internal sealed partial class GatewayState
{
    internal void UpsertChannelInteraction(GatewayChannelInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        EnsureChannelInteractionsLoaded();

        lock (channelInteractionLoadLock)
        {
            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();
            connection.Execute("PRAGMA busy_timeout=5000;");
            using var transaction = connection.BeginTransaction();

            string optionsJson = JsonSerializer.Serialize(interaction.Options.ToArray(), KautukaJsonContext.Default.GatewayChannelInteractionOptionArray);

            connection.Execute(
                $"""
                INSERT INTO runtime_channel_interactions (id, registration_id, user_id, target_type, target_id, session_id, run_id, chat_id, message_thread_id, prompt, sender_display_name, options_json, status, created_at, expires_at, resolved_at, resolved_by_external_user, resolution, approval_request_id, interaction_group_id, {channelMessageIdColumn}, {channelTransportMessageIdColumn})
                VALUES (@id, @registrationId, @userId, @targetType, @targetId, @sessionId, @runId, @chatId, @messageThreadId, @prompt, @senderDisplayName, @optionsJson, @status, @createdAt, @expiresAt, @resolvedAt, @resolvedByExternalUser, @resolution, @approvalRequestId, @interactionGroupId, @ChannelMessageId, @TransportMessageId)
                ON CONFLICT(id) DO UPDATE SET
                    registration_id=excluded.registration_id,
                    user_id=excluded.user_id,
                    target_type=excluded.target_type,
                    target_id=excluded.target_id,
                    session_id=excluded.session_id,
                    run_id=excluded.run_id,
                    chat_id=excluded.chat_id,
                    message_thread_id=excluded.message_thread_id,
                    prompt=excluded.prompt,
                    sender_display_name=excluded.sender_display_name,
                    options_json=excluded.options_json,
                    status=excluded.status,
                    created_at=excluded.created_at,
                    expires_at=excluded.expires_at,
                    resolved_at=excluded.resolved_at,
                    resolved_by_external_user=excluded.resolved_by_external_user,
                    resolution=excluded.resolution,
                    approval_request_id=excluded.approval_request_id,
                    interaction_group_id=excluded.interaction_group_id,
                    {channelMessageIdColumn}=excluded.{channelMessageIdColumn},
                    {channelTransportMessageIdColumn}=excluded.{channelTransportMessageIdColumn}
                """,
                new
                {
                    id = interaction.Id,
                    registrationId = interaction.RegistrationId,
                    userId = (object?)interaction.UserId,
                    targetType = (object?)interaction.TargetType,
                    targetId = (object?)interaction.TargetId,
                    sessionId = (object?)interaction.SessionId,
                    runId = (object?)interaction.RunId,
                    chatId = interaction.ChatId,
                    messageThreadId = (object?)interaction.MessageThreadId,
                    prompt = interaction.Prompt,
                    senderDisplayName = (object?)interaction.SenderDisplayName,
                    optionsJson,
                    status = interaction.Status,
                    createdAt = interaction.CreatedAt,
                    expiresAt = interaction.ExpiresAt,
                    resolvedAt = (object?)interaction.ResolvedAt,
                    resolvedByExternalUser = (object?)interaction.ResolvedByExternalUserId,
                    resolution = (object?)interaction.Resolution,
                    approvalRequestId = (object?)interaction.ApprovalRequestId,
                    interactionGroupId = (object?)interaction.InteractionGroupId,
                    ChannelMessageId = (object?)interaction.SourceMessageId,
                    TransportMessageId = (object?)interaction.SourceTransportMessageId,
                },
                transaction: transaction);

            transaction.Commit();
            channelInteractions[interaction.Id] = interaction;
        }
    }
}

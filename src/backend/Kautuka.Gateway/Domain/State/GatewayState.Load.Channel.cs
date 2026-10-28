using Dapper;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.State;

internal sealed partial class GatewayState
{
    private static void LoadChannelInteractions(SqliteConnection connection, GatewayState state)
    {
        state.channelInteractions.Clear();

        foreach (DbChannelInteraction row in connection.Query<DbChannelInteraction>(
            $"""
            SELECT id AS Id, registration_id AS RegistrationId, user_id AS UserId,
                   target_type AS TargetType, target_id AS TargetId, session_id AS SessionId,
                   run_id AS RunId, chat_id AS ChatId, message_thread_id AS MessageThreadId,
                     prompt AS Prompt, sender_display_name AS SenderDisplayName, options_json AS OptionsJson, status AS Status,
                   created_at AS CreatedAt, expires_at AS ExpiresAt, resolved_at AS ResolvedAt,
                   resolved_by_external_user AS ResolvedByExternalUser, resolution AS Resolution,
                   approval_request_id AS ApprovalRequestId, interaction_group_id AS InteractionGroupId,
                   {channelMessageIdColumn} AS ChannelMessageId,
                   {channelTransportMessageIdColumn} AS TransportMessageId
            FROM runtime_channel_interactions
            """))
        {
            var options = DeserializeInteractionOptions(row.OptionsJson);
            state.channelInteractions[row.Id] = new GatewayChannelInteraction(
                Id: row.Id,
                RegistrationId: row.RegistrationId,
                UserId: row.UserId,
                TargetType: row.TargetType,
                TargetId: row.TargetId,
                SessionId: row.SessionId,
                RunId: row.RunId,
                ChatId: row.ChatId,
                MessageThreadId: row.MessageThreadId,
                Prompt: row.Prompt,
                SenderDisplayName: row.SenderDisplayName,
                Options: options,
                Status: row.Status,
                CreatedAt: row.CreatedAt,
                ExpiresAt: row.ExpiresAt,
                ResolvedAt: row.ResolvedAt,
                ResolvedByExternalUserId: row.ResolvedByExternalUser,
                Resolution: row.Resolution,
                ApprovalRequestId: row.ApprovalRequestId,
                SourceMessageId: row.ChannelMessageId,
                SourceTransportMessageId: row.TransportMessageId,
                InteractionGroupId: row.InteractionGroupId
                    ?? (string.IsNullOrWhiteSpace(row.ApprovalRequestId) ? null : $"approval:{row.ApprovalRequestId}"));
        }
    }
}

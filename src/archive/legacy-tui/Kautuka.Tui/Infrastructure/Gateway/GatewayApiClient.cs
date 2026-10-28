// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Infrastructure/Gateway/GatewayApiClient.cs | Namespace: Kautuka.Tui.Infrastructure.Gateway
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Kautuka.Tui.Common;

namespace Kautuka.Tui.Infrastructure.Gateway;

public sealed class GatewayApiClient(HttpClient httpClient) : IDisposable
{
    private readonly HttpClient httpClient = httpClient;

    public void Dispose() => httpClient.Dispose();

    public void SetAccessToken(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    public Task<Result<AuthLoginResponse>> LoginAsync(string username, string password, CancellationToken cancellationToken)
        => PostAsync("/api/auth/login", new AuthLoginRequest(username, password), GatewayJsonContext.Default.AuthLoginRequest, GatewayJsonContext.Default.AuthLoginResponse, cancellationToken);

    public Task<Result<GatewayUserPayload>> GetMeAsync(CancellationToken cancellationToken)
        => GetAsync("/api/auth/me", GatewayJsonContext.Default.GatewayUserPayload, cancellationToken);

    public Task<Result<GatewayAutopilotSchedule[]>> GetAutopilotSchedulesAsync(CancellationToken cancellationToken)
        => GetAsync("/api/autopilot/schedules", GatewayJsonContext.Default.GatewayAutopilotScheduleArray, cancellationToken);

    public Task<Result<GatewayScheduledRun[]>> GetAutopilotRunsAsync(CancellationToken cancellationToken)
        => GetAsync("/api/autopilot/runs", GatewayJsonContext.Default.GatewayScheduledRunArray, cancellationToken);

    public Task<Result<GatewayAutopilotNotification[]>> GetAutopilotInboxAsync(CancellationToken cancellationToken)
        => GetAsync("/api/autopilot/inbox", GatewayJsonContext.Default.GatewayAutopilotNotificationArray, cancellationToken);

    public Task<Result<GatewayProviderTypesResponse>> GetProviderTypesAsync(CancellationToken cancellationToken)
        => GetAsync("/api/settings/provider-types", GatewayJsonContext.Default.GatewayProviderTypesResponse, cancellationToken);

    public Task<Result<GatewayProvidersResponse>> GetProvidersAsync(CancellationToken cancellationToken)
        => GetAsync("/api/settings/providers", GatewayJsonContext.Default.GatewayProvidersResponse, cancellationToken);

    public Task<Result<GatewayProviderResponse>> SaveProviderAsync(GatewayProviderSaveRequest request, CancellationToken cancellationToken)
        => PostAsync(
            "/api/settings/providers/save",
            request,
            GatewayJsonContext.Default.GatewayProviderSaveRequest,
            GatewayJsonContext.Default.GatewayProviderResponse,
            cancellationToken);

    public Task<Result<GatewayModelsResponse>> GetModelsAsync(CancellationToken cancellationToken)
        => GetAsync("/api/settings/models", GatewayJsonContext.Default.GatewayModelsResponse, cancellationToken);

    public Task<Result<GatewayModelResponse>> SaveModelAsync(GatewayModelSaveRequest request, CancellationToken cancellationToken)
        => PostAsync(
            "/api/settings/models/save",
            request,
            GatewayJsonContext.Default.GatewayModelSaveRequest,
            GatewayJsonContext.Default.GatewayModelResponse,
            cancellationToken);

    public Task<Result<GatewayDiscoveredModelsResponse>> DiscoverModelsAsync(string providerId, CancellationToken cancellationToken)
        => PostAsync(
            "/api/settings/models/discover",
            new GatewayModelDiscoveryRequest(providerId),
            GatewayJsonContext.Default.GatewayModelDiscoveryRequest,
            GatewayJsonContext.Default.GatewayDiscoveredModelsResponse,
            cancellationToken);

    public Task<Result<GatewayModelCapabilitiesResponse>> ProbeModelAsync(string modelId, CancellationToken cancellationToken)
        => PostAsync(
            $"/api/settings/models/{Uri.EscapeDataString(modelId)}/probe",
            new GatewayEmptyRequest(),
            GatewayJsonContext.Default.GatewayEmptyRequest,
            GatewayJsonContext.Default.GatewayModelCapabilitiesResponse,
            cancellationToken);

    public Task<Result<GatewayConnectorsResponse>> GetConnectorsAsync(CancellationToken cancellationToken)
        => GetAsync("/api/connectors", GatewayJsonContext.Default.GatewayConnectorsResponse, cancellationToken);

    public Task<Result<GatewayRuntimeContainersResponse>> GetRuntimeContainersAsync(CancellationToken cancellationToken)
        => GetAsync("/api/runtime/diagnostics/manager/containers", GatewayJsonContext.Default.GatewayRuntimeContainersResponse, cancellationToken);

    public Task<Result<GatewayPkgsProxyCacheStatsResponse>> GetPkgsProxyCacheStatsAsync(CancellationToken cancellationToken)
        => GetAsync("/api/runtime/diagnostics/pkgs-proxy/stats", GatewayJsonContext.Default.GatewayPkgsProxyCacheStatsResponse, cancellationToken);

    public Task<Result<GatewayRuntimeUpdateOperationItem[]>> GetUpdateHistoryAsync(CancellationToken cancellationToken)
        => GetAsync("/api/runtime/update-history", GatewayJsonContext.Default.GatewayRuntimeUpdateOperationItemArray, cancellationToken);

    public Task<Result<GatewayApplianceHealthItem>> GetApplianceHealthAsync(CancellationToken cancellationToken)
        => GetAsync("/api/runtime/appliance-health", GatewayJsonContext.Default.GatewayApplianceHealthItem, cancellationToken);

    public Task<Result<GatewayConnectorListItem>> GetConnectorAsync(string connectorId, CancellationToken cancellationToken)
        => GetAsync($"/api/connectors/{Uri.EscapeDataString(connectorId)}", GatewayJsonContext.Default.GatewayConnectorListItem, cancellationToken);

    public Task<Result<GatewayAgentsResponse>> GetAgentsAsync(CancellationToken cancellationToken)
        => GetAsync("/api/settings/agents", GatewayJsonContext.Default.GatewayAgentsResponse, cancellationToken);

    public Task<Result<GatewayAgentResponse>> SaveAgentAsync(string agentId, GatewayAgentUpsertRequest request, CancellationToken cancellationToken)
        => PutAsync(
            $"/api/settings/agents/{Uri.EscapeDataString(agentId)}",
            request,
            GatewayJsonContext.Default.GatewayAgentUpsertRequest,
            GatewayJsonContext.Default.GatewayAgentResponse,
            cancellationToken);

    public Task<Result<GatewayBooleanSuccessResponse>> DeleteAgentAsync(string agentId, CancellationToken cancellationToken)
        => DeleteAsync($"/api/settings/agents/{Uri.EscapeDataString(agentId)}", GatewayJsonContext.Default.GatewayBooleanSuccessResponse, cancellationToken);

    public Task<Result<GatewaySession>> CreateSessionAsync(string targetType, string targetId, CancellationToken cancellationToken)
        => PostAsync(
            "/api/sessions",
            new GatewaySessionCreateRequest(targetType, targetId),
            GatewayJsonContext.Default.GatewaySessionCreateRequest,
            GatewayJsonContext.Default.GatewaySession,
            cancellationToken);

    public Task<Result<GatewaySessionsResponse>> GetSessionsForAgentAsync(string agentId, CancellationToken cancellationToken)
        => GetAsync($"/api/sessions/agents/{Uri.EscapeDataString(agentId)}", GatewayJsonContext.Default.GatewaySessionsResponse, cancellationToken);

    public Task<Result<GatewaySession>> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
        => GetAsync($"/api/sessions/{Uri.EscapeDataString(sessionId)}", GatewayJsonContext.Default.GatewaySession, cancellationToken);

    public Task<Result<GatewayMemoriesResponse>> GetMemoriesForAgentAsync(string agentId, CancellationToken cancellationToken)
        => GetAsync($"/api/memories/agents/{Uri.EscapeDataString(agentId)}", GatewayJsonContext.Default.GatewayMemoriesResponse, cancellationToken);

    public Task<Result<GatewayUsersResponse>> GetUsersAsync(CancellationToken cancellationToken)
        => GetAsync("/api/settings/users", GatewayJsonContext.Default.GatewayUsersResponse, cancellationToken);

    public Task<Result<GatewayOrganizationSettingsResponse>> GetOrganizationAsync(CancellationToken cancellationToken)
        => GetAsync("/api/settings/organization", GatewayJsonContext.Default.GatewayOrganizationSettingsResponse, cancellationToken);

    public Task<Result<GatewayOrganizationSettingsResponse>> SaveOrganizationAsync(GatewayOrganizationSettingsPayload request, CancellationToken cancellationToken)
        => PutAsync(
            "/api/settings/organization",
            request,
            GatewayJsonContext.Default.GatewayOrganizationSettingsPayload,
            GatewayJsonContext.Default.GatewayOrganizationSettingsResponse,
            cancellationToken);

    public Task<Result<GatewaySystemSettingsResponse>> GetSystemSettingsAsync(CancellationToken cancellationToken)
        => GetAsync("/api/settings/system", GatewayJsonContext.Default.GatewaySystemSettingsResponse, cancellationToken);

    public Task<Result<GatewaySystemSettingsResponse>> SaveSystemSettingsAsync(GatewaySystemSettingsResponse request, CancellationToken cancellationToken)
        => PutAsync(
            "/api/settings/system",
            request,
            GatewayJsonContext.Default.GatewaySystemSettingsResponse,
            GatewayJsonContext.Default.GatewaySystemSettingsResponse,
            cancellationToken);

    public Task<Result<GatewayProviderPathPolicySettingsResponse>> GetProviderPathPolicySettingsAsync(CancellationToken cancellationToken)
        => GetAsync("/api/settings/provider-path-policy", GatewayJsonContext.Default.GatewayProviderPathPolicySettingsResponse, cancellationToken);

    public Task<Result<GatewayProviderPathPolicySettingsResponse>> SaveProviderPathPolicySettingsAsync(GatewayProviderPathPolicySettingsRequest request, CancellationToken cancellationToken)
        => PutAsync(
            "/api/settings/provider-path-policy",
            request,
            GatewayJsonContext.Default.GatewayProviderPathPolicySettingsRequest,
            GatewayJsonContext.Default.GatewayProviderPathPolicySettingsResponse,
            cancellationToken);

    public Task<Result<GatewayMemorySettingsResponse>> GetMemorySettingsAsync(CancellationToken cancellationToken)
        => GetAsync("/api/settings/memory", GatewayJsonContext.Default.GatewayMemorySettingsResponse, cancellationToken);

    public Task<Result<GatewayMemorySettingsResponse>> SaveMemorySettingsAsync(GatewayMemorySettingsRequest request, CancellationToken cancellationToken)
        => PutAsync(
            "/api/settings/memory",
            request,
            GatewayJsonContext.Default.GatewayMemorySettingsRequest,
            GatewayJsonContext.Default.GatewayMemorySettingsResponse,
            cancellationToken);

    public Task<Result<GatewayPersonaDefinitionsResponse>> GetPersonaDefinitionsAsync(CancellationToken cancellationToken)
        => GetAsync("/api/settings/persona/definitions", GatewayJsonContext.Default.GatewayPersonaDefinitionsResponse, cancellationToken);

    public Task<Result<GatewayPersonaDefinitionsResponse>> SavePersonaDefinitionsAsync(GatewayPersonaDefinitionsRequest request, CancellationToken cancellationToken)
        => PutAsync(
            "/api/settings/persona/definitions",
            request,
            GatewayJsonContext.Default.GatewayPersonaDefinitionsRequest,
            GatewayJsonContext.Default.GatewayPersonaDefinitionsResponse,
            cancellationToken);

    public Task<Result<GatewayUserResponse>> InviteUserAsync(string username, string password, string role, string? email, CancellationToken cancellationToken)
        => PostAsync(
            "/api/settings/users/invite",
            new GatewayUserInviteRequest(username, password, role, email),
            GatewayJsonContext.Default.GatewayUserInviteRequest,
            GatewayJsonContext.Default.GatewayUserResponse,
            cancellationToken);

    public Task<Result<GatewayDeletedResponse>> DeleteConnectorAsync(string connectorId, CancellationToken cancellationToken)
        => DeleteAsync($"/api/connectors/{Uri.EscapeDataString(connectorId)}", GatewayJsonContext.Default.GatewayDeletedResponse, cancellationToken);

    public Task<Result<GatewayBooleanSuccessResponse>> DeleteProviderAsync(string providerId, CancellationToken cancellationToken)
        => DeleteAsync($"/api/settings/providers/{Uri.EscapeDataString(providerId)}", GatewayJsonContext.Default.GatewayBooleanSuccessResponse, cancellationToken);

    public Task<Result<GatewayBooleanSuccessResponse>> DeleteModelAsync(string modelId, CancellationToken cancellationToken)
        => DeleteAsync($"/api/settings/models/{Uri.EscapeDataString(modelId)}", GatewayJsonContext.Default.GatewayBooleanSuccessResponse, cancellationToken);

    public Task<Result<GatewayBooleanSuccessResponse>> SetUserRoleAsync(string userId, string role, CancellationToken cancellationToken)
        => PutAsync(
            $"/api/settings/users/{Uri.EscapeDataString(userId)}/role",
            new GatewayUserRoleUpdateRequest(string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase)),
            GatewayJsonContext.Default.GatewayUserRoleUpdateRequest,
            GatewayJsonContext.Default.GatewayBooleanSuccessResponse,
            cancellationToken);

    public Task<Result<GatewayBooleanSuccessResponse>> SetUserActiveAsync(string userId, bool isActive, CancellationToken cancellationToken)
        => PutAsync(
            $"/api/settings/users/{Uri.EscapeDataString(userId)}/active",
            new GatewayUserActivationUpdateRequest(isActive),
            GatewayJsonContext.Default.GatewayUserActivationUpdateRequest,
            GatewayJsonContext.Default.GatewayBooleanSuccessResponse,
            cancellationToken);

    public Task<Result<GatewayBooleanSuccessResponse>> SetUserImportPermissionsAsync(string userId, bool canImportConnectors, bool canImportSkills, CancellationToken cancellationToken)
        => PutAsync(
            $"/api/settings/users/{Uri.EscapeDataString(userId)}/import-permissions",
            new GatewayUserImportPermissionsUpdateRequest(canImportConnectors, canImportSkills),
            GatewayJsonContext.Default.GatewayUserImportPermissionsUpdateRequest,
            GatewayJsonContext.Default.GatewayBooleanSuccessResponse,
            cancellationToken);

    public Task<Result<GatewayNaradCapabilitiesResponse>> GetNaradCapabilitiesAsync(
        string agentId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        string path =
            $"/api/narad/v1/capabilities?agentId={Uri.EscapeDataString(agentId)}&sessionId={Uri.EscapeDataString(sessionId)}";
        return GetAsync(path, GatewayJsonContext.Default.GatewayNaradCapabilitiesResponse, cancellationToken);
    }

    public Task<Result<GatewayNaradIntentResponse>> SendNaradMessageAsync(
        string agentId,
        string sessionId,
        string message,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return PostAsync(
            $"/api/narad/v1/agents/{Uri.EscapeDataString(agentId)}/sessions/{Uri.EscapeDataString(sessionId)}/messages",
            new GatewayNaradMessageIntentRequest(message, expectedRevision),
            GatewayJsonContext.Default.GatewayNaradMessageIntentRequest,
            GatewayJsonContext.Default.GatewayNaradIntentResponse,
            cancellationToken);
    }

    public async Task<Result> RunAgentChatAsync(
        string agentId,
        string sessionId,
        string message,
        long? expectedRevision,
        Func<GatewayNaradAuthorityEvent, CancellationToken, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(onEvent);

        Result<GatewayNaradIntentResponse> intentResult = await SendNaradMessageAsync(
            agentId,
            sessionId,
            message,
            expectedRevision,
            cancellationToken).ConfigureAwait(false);
        if (!intentResult.IsSuccess || intentResult.Value is null)
        {
            return Results.Failure(intentResult.ErrorMessage ?? "Failed to send Narad message intent.");
        }

        if (!string.Equals(intentResult.Value.Status, "accepted", StringComparison.Ordinal))
        {
            return Results.Failure(intentResult.Value.Detail ?? $"Narad message intent returned '{intentResult.Value.Status}'.");
        }

        long streamCursor = expectedRevision ?? 0;
        return await StreamNaradSessionAsync(
            agentId,
            sessionId,
            streamCursor,
            onEvent,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> StreamNaradSessionAsync(
        string agentId,
        string sessionId,
        long afterSessionSequence,
        Func<GatewayNaradAuthorityEvent, CancellationToken, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(onEvent);

        try
        {
            using HttpClient streamingClient = CreateStreamingClient();
            string path =
                $"/api/narad/v1/sessions/{Uri.EscapeDataString(sessionId)}/stream?agentId={Uri.EscapeDataString(agentId)}&afterSessionSequence={Math.Max(0, afterSessionSequence)}&hydrate=true";
            using HttpRequestMessage httpRequest = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using HttpResponseMessage response = await streamingClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Stream errorStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (errorStream.ConfigureAwait(false))
                {
                    string message = await TryReadErrorAsync(errorStream, cancellationToken).ConfigureAwait(false)
                        ?? $"Gateway Narad stream failed with status {(int)response.StatusCode} {response.ReasonPhrase}.";
                    return Results.Failure(message);
                }
            }

            Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (contentStream.ConfigureAwait(false))
            {
                using StreamReader reader = new(contentStream);
                string? dataLine = null;
                bool terminalSeen = false;

                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (line.Length == 0)
                    {
                        if (dataLine is not null)
                        {
                            GatewayNaradAuthorityEvent? authorityEvent = NaradAuthorityEventParser.ParseSseData(dataLine);
                            dataLine = null;
                            if (authorityEvent is null || !NaradAuthorityEventParser.ShouldSurfaceToChat(authorityEvent.Type))
                            {
                                continue;
                            }

                            await onEvent(authorityEvent, cancellationToken).ConfigureAwait(false);
                            if (NaradAuthorityEventParser.IsTerminalRunEvent(authorityEvent.Type))
                            {
                                terminalSeen = true;
                                break;
                            }
                        }

                        continue;
                    }

                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        dataLine = line.Length > 5 ? line[5..].TrimStart() : string.Empty;
                    }
                }

                if (!terminalSeen && dataLine is not null)
                {
                    GatewayNaradAuthorityEvent? authorityEvent = NaradAuthorityEventParser.ParseSseData(dataLine);
                    if (authorityEvent is not null && NaradAuthorityEventParser.ShouldSurfaceToChat(authorityEvent.Type))
                    {
                        await onEvent(authorityEvent, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            return Results.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.Success();
        }
        catch (JsonException)
        {
            return Results.Failure("Gateway Narad stream returned invalid event data.");
        }
    }

    private async Task<Result<TResponse>> GetAsync<TResponse>(string path, JsonTypeInfo<TResponse> responseTypeInfo, CancellationToken cancellationToken)
        where TResponse : class
    {
        using HttpResponseMessage response = await httpClient.GetAsync(new Uri(path, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(response, responseTypeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<TResponse>> DeleteAsync<TResponse>(string path, JsonTypeInfo<TResponse> responseTypeInfo, CancellationToken cancellationToken)
        where TResponse : class
    {
        using HttpResponseMessage response = await httpClient.DeleteAsync(new Uri(path, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(response, responseTypeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<TResponse>> PostAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class
    {
        using HttpRequestMessage httpRequest = CreateJsonRequest(HttpMethod.Post, path, request, requestTypeInfo);
        using HttpResponseMessage response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(response, responseTypeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<TResponse>> PutAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class
    {
        using HttpRequestMessage httpRequest = CreateJsonRequest(HttpMethod.Put, path, request, requestTypeInfo);
        using HttpResponseMessage response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(response, responseTypeInfo, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateJsonRequest<TRequest>(HttpMethod method, string path, TRequest request, JsonTypeInfo<TRequest> requestTypeInfo)
        where TRequest : class
    {
        string json = JsonSerializer.Serialize(request, requestTypeInfo);
        return new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static async Task<Result<TResponse>> ReadResponseAsync<TResponse>(
        HttpResponseMessage response,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (contentStream.ConfigureAwait(false))
        {
            if (response.IsSuccessStatusCode)
            {
                TResponse? payload = await JsonSerializer.DeserializeAsync(contentStream, responseTypeInfo, cancellationToken).ConfigureAwait(false);
                return payload is null
                    ? Results.Failure<TResponse>("Received an empty response payload.")
                    : Results.Success(payload);
            }

            string message = await TryReadErrorAsync(contentStream, cancellationToken).ConfigureAwait(false)
                ?? $"Gateway request failed with status {(int)response.StatusCode} {response.ReasonPhrase}.";

            return Results.Failure<TResponse>(message);
        }
    }

    private static async Task<string?> TryReadErrorAsync(Stream contentStream, CancellationToken cancellationToken)
    {
        GatewayErrorResponse? error = await JsonSerializer.DeserializeAsync(contentStream, GatewayJsonContext.Default.GatewayErrorResponse, cancellationToken).ConfigureAwait(false);
        return error?.Error;
    }

    private HttpClient CreateStreamingClient()
    {
        HttpClient streamingClient = new()
        {
            BaseAddress = httpClient.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };

        if (httpClient.DefaultRequestHeaders.Authorization is AuthenticationHeaderValue authorization)
        {
            streamingClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(authorization.Scheme, authorization.Parameter);
        }

        return streamingClient;
    }
}

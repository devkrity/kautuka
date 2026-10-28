using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Kautuka.Control.Core.Auth;

namespace Kautuka.Control.Core.Api;

/// <summary>
/// Thin HttpClient wrapper that surfaces ApiError instead of throwing.
/// Reuse a single instance per application lifetime.
/// </summary>
public sealed class ApiClient
{
    private readonly HttpClient http;

    public ApiClient(HttpClient http) => this.http = http;

    public void SetToken(string? token)
    {
        http.DefaultRequestHeaders.Authorization = token is null
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    // ── GET ──────────────────────────────────────────────────────────────────

    public async Task<ApiResult<T>> GetAsync<T>(string path, JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        return await SendAsync(req, typeInfo, ct).ConfigureAwait(false);
    }

    // ── POST ─────────────────────────────────────────────────────────────────

    public async Task<ApiResult<T>> PostAsync<TBody, T>(string path, TBody body,
        JsonTypeInfo<TBody> bodyTypeInfo, JsonTypeInfo<T> resultTypeInfo,
        CancellationToken ct = default)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(body, bodyTypeInfo);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = content,
        };
        return await SendAsync(req, resultTypeInfo, ct).ConfigureAwait(false);
    }

    public async Task<ApiResult<string?>> PostEmptyAsync(string path,
        CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsync(new Uri(path, UriKind.Relative), content: null, ct)
                .ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return ApiResults.Err<string?>(new UnauthorizedApiError());
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return ApiResults.Err<string?>(new NotFoundApiError());
            return resp.IsSuccessStatusCode
                ? ApiResults.Ok<string?>(null)
                : ApiResults.Err<string?>(new UnexpectedApiError((int)resp.StatusCode,
                    await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)));
        }
        catch (HttpRequestException ex) { return ApiResults.Err<string?>(new TransportApiError(ex)); }
        catch (TaskCanceledException ex) { return ApiResults.Err<string?>(new TransportApiError(ex)); }
        catch (InvalidOperationException ex) { return ApiResults.Err<string?>(new TransportApiError(ex)); }
    }

    // ── DELETE ────────────────────────────────────────────────────────────────

    public async Task<ApiResult<string?>> DeleteAsync(string path,
        CancellationToken ct = default)
    {
        try
        {
            var resp = await http.DeleteAsync(new Uri(path, UriKind.Relative), ct)
                .ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return ApiResults.Err<string?>(new UnauthorizedApiError());
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return ApiResults.Err<string?>(new NotFoundApiError());
            return resp.IsSuccessStatusCode
                ? ApiResults.Ok<string?>(null)
                : ApiResults.Err<string?>(new UnexpectedApiError((int)resp.StatusCode,
                    await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)));
        }
        catch (HttpRequestException ex) { return ApiResults.Err<string?>(new TransportApiError(ex)); }
        catch (TaskCanceledException ex) { return ApiResults.Err<string?>(new TransportApiError(ex)); }
        catch (InvalidOperationException ex) { return ApiResults.Err<string?>(new TransportApiError(ex)); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<ApiResult<T>> SendAsync<T>(HttpRequestMessage req,
        JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        try
        {
            var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return ApiResults.Err<T>(new UnauthorizedApiError());
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return ApiResults.Err<T>(new NotFoundApiError());
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ApiResults.Err<T>(new UnexpectedApiError((int)resp.StatusCode, body));
            }
            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var value = await JsonSerializer.DeserializeAsync(stream, typeInfo, ct)
                             .ConfigureAwait(false);
            return value is null
                ? ApiResults.Err<T>(new UnexpectedApiError(200, "Empty response body"))
                : ApiResults.Ok(value);
        }
        catch (HttpRequestException ex) { return ApiResults.Err<T>(new TransportApiError(ex)); }
        catch (TaskCanceledException ex) { return ApiResults.Err<T>(new TransportApiError(ex)); }
        catch (JsonException ex) { return ApiResults.Err<T>(new TransportApiError(ex)); }
        catch (NotSupportedException ex) { return ApiResults.Err<T>(new TransportApiError(ex)); }
        catch (InvalidOperationException ex) { return ApiResults.Err<T>(new TransportApiError(ex)); }
    }

    // ── Typed convenience methods ─────────────────────────────────────────────

    public async Task<ApiResult<AgentSummary[]>> GetAgentsAsync(CancellationToken ct = default)
    {
        var r = await GetAsync("/api/settings/agents", ApiJsonContext.Default.AgentsResponse, ct)
            .ConfigureAwait(false);
        return r.IsOk ? ApiResults.Ok(r.Value.Agents.ToArray()) : ApiResults.Err<AgentSummary[]>(r.Error);
    }

    public async Task<ApiResult<UserSummary[]>> GetUsersAsync(CancellationToken ct = default)
    {
        var r = await GetAsync("/api/settings/users", ApiJsonContext.Default.UsersResponse, ct)
            .ConfigureAwait(false);
        return r.IsOk ? ApiResults.Ok(r.Value.Users.ToArray()) : ApiResults.Err<UserSummary[]>(r.Error);
    }

    public async Task<ApiResult<SessionSummary[]>> GetSessionsAsync(CancellationToken ct = default)
    {
        var r = await GetAsync("/api/sessions/recent", ApiJsonContext.Default.SessionsResponse, ct)
            .ConfigureAwait(false);
        return r.IsOk ? ApiResults.Ok(r.Value.Sessions.ToArray()) : ApiResults.Err<SessionSummary[]>(r.Error);
    }

    public Task<ApiResult<ApprovalSummary[]>> GetApprovalsAsync(CancellationToken ct = default) =>
        GetAsync("/api/approvals/pending", ApiJsonContext.Default.ApprovalSummaryArray, ct);

    public async Task<ApiResult<ProviderSummary[]>> GetProvidersAsync(CancellationToken ct = default)
    {
        var r = await GetAsync("/api/settings/providers", ApiJsonContext.Default.ProvidersResponse, ct)
            .ConfigureAwait(false);
        return r.IsOk ? ApiResults.Ok(r.Value.Providers.ToArray()) : ApiResults.Err<ProviderSummary[]>(r.Error);
    }

    public async Task<ApiResult<ModelSummary[]>> GetModelsAsync(CancellationToken ct = default)
    {
        var r = await GetAsync("/api/settings/models", ApiJsonContext.Default.ModelsResponse, ct)
            .ConfigureAwait(false);
        return r.IsOk ? ApiResults.Ok(r.Value.Models.ToArray()) : ApiResults.Err<ModelSummary[]>(r.Error);
    }

    public Task<ApiResult<AuthMe>> GetMeAsync(CancellationToken ct = default) =>
        GetAsync("/api/auth/me", ApiJsonContext.Default.AuthMe, ct);

    public Task<ApiResult<SystemSettings>> GetSettingsAsync(CancellationToken ct = default) =>
        GetAsync("/api/settings/system", ApiJsonContext.Default.SystemSettings, ct);

    public Task<ApiResult<LoginResponse>> LoginAsync(LoginRequest req,
        CancellationToken ct = default) =>
        PostAsync("/api/auth/login", req,
            ApiJsonContext.Default.LoginRequest,
            ApiJsonContext.Default.LoginResponse, ct);
}

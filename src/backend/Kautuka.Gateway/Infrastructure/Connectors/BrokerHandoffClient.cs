using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Kautuka.Gateway.Domain.Connectors;

namespace Kautuka.Gateway.Infrastructure.Connectors;

internal sealed record BrokerHandoffStart(string Provider, string Purpose, string State, string Challenge,
    string Nonce, string GatewayOrigin, string? ConnectionId = null, string? DpopJkt = null,
    long? ExpectedGeneration = null, string? MicrosoftAccountType = null);
internal sealed record BrokerMailResult(string Provider, string RefreshToken, long Generation)
{
    public override string ToString() => $"BrokerMailResult {{ Provider = {Provider}, RefreshToken = [redacted], Generation = {Generation} }}";
}
internal sealed record BrokerMailRefreshResult(string AccessToken, string RefreshToken, string TokenType, long IssuedAt, long? ExpiresAt)
{
    public override string ToString() => $"BrokerMailRefreshResult {{ AccessToken = [redacted], RefreshToken = [redacted], TokenType = {TokenType}, ExpiresAt = {ExpiresAt} }}";
}
internal sealed class BrokerMailRequestException : HttpRequestException
{
    public BrokerMailRequestException() { }
    public BrokerMailRequestException(string message) : base(message) { }
    public BrokerMailRequestException(string message, Exception innerException) : base(message, innerException) { }
    public BrokerMailRequestException(HttpStatusCode status, TimeSpan? retryAfter, string? admissionDenial = null, string? mailError = null)
        : base("The broker could not complete the mail request.", null, status) { RetryAfter = retryAfter; AdmissionDenial = admissionDenial; MailError = mailError; }
    public TimeSpan? RetryAfter { get; }
    public string? AdmissionDenial { get; }
    public string? MailError { get; }
}

internal sealed class BrokerHandoffClient(
    BrokerInstallationIdentityProvider identities,
    BrokerInstallationRegistration registration,
    HttpClient client) : IDisposable
{
    private async Task<BrokerInstallationIdentity> GetIdentityAsync(Uri origin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identities);
        return await registration.RegisterAsync(origin, ct).ConfigureAwait(false);
    }
    public async Task<string> StartAsync(Uri origin, BrokerHandoffStart start, CancellationToken cancellationToken)
    {
        BrokerInstallationIdentity identity = await GetIdentityAsync(origin, cancellationToken).ConfigureAwait(false);
        byte[] body = WriteBody(writer =>
        {
            writer.WriteString("id", identity.Id);
            writer.WriteString("provider", start.Provider);
            writer.WriteString("purpose", start.Purpose);
            writer.WriteString("state", start.State);
            writer.WriteString("challenge", start.Challenge);
            writer.WriteString("nonce", start.Nonce);
            writer.WriteString("gatewayOrigin", start.GatewayOrigin);
            if (start.ConnectionId is not null) writer.WriteString("connectionId", start.ConnectionId);
            if (start.DpopJkt is not null) writer.WriteString("dpopJkt", start.DpopJkt);
            if (start.ExpectedGeneration is not null) writer.WriteNumber("expectedGeneration", start.ExpectedGeneration.Value);
            if (start.MicrosoftAccountType is not null) writer.WriteString("microsoftAccountType", start.MicrosoftAccountType);
        });
        using HttpResponseMessage response = await SendAsync(origin, "/installation/flow", identity, body, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("The broker could not start sign-in.", null, response.StatusCode);
        using JsonDocument json = await ReadJsonAsync(response, 4096, cancellationToken).ConfigureAwait(false);
        string? flowId = json.RootElement.TryGetProperty("flowId", out JsonElement value) ? value.GetString() : null;
        if (!IsId(flowId)) throw new InvalidDataException("The broker returned an invalid sign-in flow.");
        return flowId!;
    }

    public async Task<string?> ExchangeSocialAsync(Uri origin, string flowId, string verifier, CancellationToken cancellationToken)
    {
        if (!IsId(flowId) || string.IsNullOrWhiteSpace(verifier) || verifier.Length is < 43 or > 128)
            throw new ArgumentException("The sign-in flow is invalid.");
        BrokerInstallationIdentity identity = await GetIdentityAsync(origin, cancellationToken).ConfigureAwait(false);
        byte[] body = WriteBody(writer =>
        {
            writer.WriteString("id", identity.Id);
            writer.WriteString("flowId", flowId);
            writer.WriteString("verifier", verifier);
        });
        using HttpResponseMessage response = await SendAsync(origin, "/installation/social-exchange", identity, body, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted) return null;
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("The broker could not complete sign-in.", null, response.StatusCode);
        using JsonDocument json = await ReadJsonAsync(response, 24000, cancellationToken).ConfigureAwait(false);
        string? token = json.RootElement.TryGetProperty("id_token", out JsonElement value) ? value.GetString() : null;
        if (token is not { Length: >= 20 and <= 20000 }) throw new InvalidDataException("The broker returned an invalid sign-in result.");
        return token;
    }

    public async Task BindBrowserAsync(Uri origin, string flowId, string nonce, CancellationToken cancellationToken)
    {
        if (!IsId(flowId) || !IsId(nonce)) throw new ArgumentException("The browser confirmation is invalid.");
        BrokerInstallationIdentity identity = await GetIdentityAsync(origin, cancellationToken).ConfigureAwait(false);
        byte[] body = WriteBody(writer =>
        {
            writer.WriteString("id", identity.Id);
            writer.WriteString("flowId", flowId);
            writer.WriteString("nonce", nonce);
        });
        using HttpResponseMessage response = await SendAsync(origin, "/installation/bind-browser", identity, body, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent)
            throw new HttpRequestException("The broker could not confirm this browser.", null, response.StatusCode);
    }

    public async Task<BrokerMailResult?> ExchangeMailAsync(Uri origin, string expectedProvider, string flowId, string verifier, string mailKey, CancellationToken ct)
    {
        if (expectedProvider is not ("google" or "microsoft") || !IsId(flowId) || verifier.Length is < 43 or > 128) throw new ArgumentException("Invalid mail flow.");
        BrokerInstallationIdentity identity = await GetIdentityAsync(origin, ct).ConfigureAwait(false);
        byte[] body = WriteBody(w => { w.WriteString("id", identity.Id); w.WriteString("flowId", flowId); w.WriteString("verifier", verifier); });
        using HttpResponseMessage response = await SendAsync(origin, "/installation/mail-exchange", identity, body, ct, mailKey).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted) return null;
        if (response.StatusCode != HttpStatusCode.OK) throw MailError(response);
        using JsonDocument json = await ReadJsonAsync(response, 24000, ct).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The broker returned an invalid mail result.");
        string? provider = ReadString(json.RootElement, "provider");
        string? token = ReadString(json.RootElement, "refreshToken");
        if (provider != expectedProvider || token is not { Length: > 20 and <= 12000 } || !token.StartsWith("kmf1.", StringComparison.Ordinal)
            || !json.RootElement.TryGetProperty("generation", out JsonElement generation) || generation.ValueKind != JsonValueKind.Number
            || !generation.TryGetInt64(out long number) || number is < 1 or > 9007199254740991L)
            throw new InvalidDataException("The broker returned an invalid mail result.");
        return new BrokerMailResult(provider, token, number);
    }

    public async Task AcknowledgeMailAsync(Uri origin, string flowId, string verifier, string mailKey, CancellationToken ct)
    {
        if (!IsId(flowId) || verifier.Length is < 43 or > 128) throw new ArgumentException("Invalid mail flow.");
        BrokerInstallationIdentity identity = await GetIdentityAsync(origin, ct).ConfigureAwait(false);
        byte[] body = WriteBody(w => { w.WriteString("id", identity.Id); w.WriteString("flowId", flowId); w.WriteString("verifier", verifier); });
        using HttpResponseMessage response = await SendAsync(origin, "/installation/mail-ack", identity, body, ct, mailKey).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent) throw MailError(response);
    }

    public async Task<bool> IsMailActiveAsync(Uri origin, string provider, string connectionId, long generation, string flowId, string mailKey, CancellationToken ct)
    {
        ValidateMailBinding(provider, connectionId, generation, flowId);
        BrokerInstallationIdentity identity = await GetIdentityAsync(origin, ct).ConfigureAwait(false);
        byte[] body = WriteBody(w => WriteMailBinding(w, identity.Id, provider, connectionId, generation, flowId));
        using HttpResponseMessage response = await SendAsync(origin, "/installation/mail-status", identity, body, ct, mailKey).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw MailError(response);
        using JsonDocument json = await ReadJsonAsync(response, 1024, ct).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("active", out JsonElement active) || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("The broker returned an invalid connection status.");
        return active.GetBoolean();
    }

    public async Task<BrokerMailRefreshResult?> RefreshMailAsync(Uri origin, string provider, string connectionId, long generation,
        string flowId, string operationId, string refreshToken, string mailKey, CancellationToken ct)
    {
        ValidateMailBinding(provider, connectionId, generation, flowId);
        if (!IsId(operationId) || refreshToken is not { Length: > 20 and <= 12000 } || !refreshToken.StartsWith("kmf1.", StringComparison.Ordinal))
            throw new ArgumentException("Invalid mail refresh operation.");
        BrokerInstallationIdentity identity = await GetIdentityAsync(origin, ct).ConfigureAwait(false);
        byte[] body = WriteBody(w => { WriteMailBinding(w, identity.Id, provider, connectionId, generation, flowId); w.WriteString("operationId", operationId); w.WriteString("refreshToken", refreshToken); });
        using HttpResponseMessage response = await SendAsync(origin, "/installation/mail-refresh", identity, body, ct, mailKey).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted) return null;
        if (response.StatusCode != HttpStatusCode.OK) throw MailError(response);
        using JsonDocument json = await ReadJsonAsync(response, 40000, ct).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The broker returned an invalid mail refresh result.");
        string? access = ReadString(json.RootElement, "accessToken"), refresh = ReadString(json.RootElement, "refreshToken"), type = ReadString(json.RootElement, "tokenType");
        if (access is not { Length: > 0 and <= 20000 } || refresh is not { Length: > 20 and <= 12000 } || !refresh.StartsWith("kmf1.", StringComparison.Ordinal)
            || type is not { Length: > 0 and <= 40 } || !json.RootElement.TryGetProperty("issuedAt", out JsonElement issued)
            || issued.ValueKind != JsonValueKind.Number || !issued.TryGetInt64(out long issuedAt) || issuedAt is < 0 or > 253402300799999L
            || !json.RootElement.TryGetProperty("expiresAt", out JsonElement expires)
            || expires.ValueKind != JsonValueKind.Null && (expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt64(out long milliseconds)
                || milliseconds is < -62135596800000L or > 253402300799999L)) throw new InvalidDataException("The broker returned an invalid mail refresh result.");
        return new BrokerMailRefreshResult(access, refresh, type, issuedAt, expires.ValueKind == JsonValueKind.Null ? null : expires.GetInt64());
    }

    public async Task AcknowledgeMailRefreshAsync(Uri origin, string provider, string connectionId, long generation, string flowId,
        string operationId, string mailKey, CancellationToken ct)
    {
        ValidateMailBinding(provider, connectionId, generation, flowId);
        if (!IsId(operationId)) throw new ArgumentException("Invalid mail refresh operation.");
        BrokerInstallationIdentity identity = await GetIdentityAsync(origin, ct).ConfigureAwait(false);
        byte[] body = WriteBody(w => { WriteMailBinding(w, identity.Id, provider, connectionId, generation, flowId); w.WriteString("operationId", operationId); });
        using HttpResponseMessage response = await SendAsync(origin, "/installation/mail-refresh-ack", identity, body, ct, mailKey).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent) throw MailError(response);
    }

    public async Task CancelMailAsync(Uri origin, string flowId, string mailKey, CancellationToken ct)
    {
        if (!IsId(flowId)) throw new ArgumentException("Invalid mail flow.");
        BrokerInstallationIdentity identity = await GetIdentityAsync(origin, ct).ConfigureAwait(false);
        byte[] body = WriteBody(w => { w.WriteString("id", identity.Id); w.WriteString("flowId", flowId); });
        using HttpResponseMessage response = await SendAsync(origin, "/installation/mail-cancel", identity, body, ct, mailKey).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent) throw MailError(response);
    }

    public async Task DisconnectMailAsync(Uri origin, BrokerMailCredential credential, CancellationToken ct)
    {
        if (!string.Equals(origin.GetLeftPart(UriPartial.Authority), credential.BrokerOrigin, StringComparison.Ordinal))
            throw new InvalidOperationException("This mail connection belongs to a different authentication broker.");
        ValidateMailBinding(credential.Provider, credential.ConnectionId, credential.Generation, credential.FlowId);
        BrokerInstallationIdentity identity = await GetIdentityAsync(origin, ct).ConfigureAwait(false);
        byte[] body = WriteBody(w => WriteMailBinding(w, identity.Id, credential.Provider, credential.ConnectionId, credential.Generation, credential.FlowId));
        using HttpResponseMessage response = await SendAsync(origin, "/installation/mail-disconnect", identity, body, ct, credential.DpopPrivateJwk).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent) throw MailError(response);
    }

    private static void ValidateMailBinding(string provider, string connectionId, long generation, string flowId)
    {
        if (provider is not ("google" or "microsoft") || !IsId(connectionId) || generation is < 1 or > 9007199254740991L || !IsId(flowId)) throw new ArgumentException("Invalid mail connection.");
    }

    private static void WriteMailBinding(Utf8JsonWriter w, string id, string provider, string connectionId, long generation, string flowId)
    {
        w.WriteString("id", id); w.WriteString("provider", provider); w.WriteString("connectionId", connectionId);
        w.WriteNumber("generation", generation); w.WriteString("flowId", flowId);
    }

    private static string? ReadString(JsonElement value, string name)
        => value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static BrokerMailRequestException MailError(HttpResponseMessage response)
    {
        string? admissionDenial = null;
        if (response.Headers.TryGetValues("Kautuka-Admission-Denied", out IEnumerable<string>? values))
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests && values.Any(value => value == "new_account_limit")) admissionDenial = "new_account_limit";
            else if (response.StatusCode == HttpStatusCode.Conflict && values.Any(value => value == "active_account_limit")) admissionDenial = "active_account_limit";
        }
        TimeSpan? delay = response.Headers.RetryAfter?.Delta;
        if (delay is null && response.Headers.RetryAfter?.Date is DateTimeOffset date) delay = date - DateTimeOffset.UtcNow;
        if (delay is not null) delay = TimeSpan.FromSeconds(Math.Clamp(delay.Value.TotalSeconds, 0, admissionDenial == "new_account_limit" ? 31536000 : 3600));
        string? mailError = response.StatusCode == HttpStatusCode.Forbidden
            && response.Headers.TryGetValues("Kautuka-Mail-Error", out IEnumerable<string>? errors)
            && errors.Any(value => value == "insufficient_scope") ? "insufficient_scope" : null;
        return new BrokerMailRequestException(response.StatusCode, delay, admissionDenial, mailError);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri origin, string path, BrokerInstallationIdentity identity, byte[] body, CancellationToken ct, string? mailKey = null)
    {
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps || origin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            throw new ArgumentException("The broker must be an HTTPS origin.", nameof(origin));
        string url = origin.GetLeftPart(UriPartial.Authority) + path;
        async Task<HttpResponseMessage> SendOnceAsync(BrokerInstallationIdentity key, string? nonce)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("Kautuka-Installation-Proof", ConnectorDpop.CreateInstallationProof(key.PrivateJwk, "POST", url, body));
            if (mailKey is not null) request.Headers.Add("DPoP", ConnectorDpop.CreateProof(mailKey, nonce, authorizationCode: null, "POST", url));
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        async Task<HttpResponseMessage> SendWithNonceAsync(BrokerInstallationIdentity key)
        {
            HttpResponseMessage initial = await SendOnceAsync(key, null).ConfigureAwait(false);
            if (mailKey is null || initial.StatusCode != HttpStatusCode.BadRequest
                || !initial.Headers.TryGetValues("DPoP-Nonce", out IEnumerable<string>? values)) return initial;
            string[] nonces = [.. values];
            if (nonces.Length != 1 || !IsId(nonces[0])) return initial;
            try
            {
                using JsonDocument challenge = await ReadJsonAsync(initial, 1024, ct).ConfigureAwait(false);
                if (challenge.RootElement.ValueKind != JsonValueKind.Object
                    || !challenge.RootElement.TryGetProperty("error", out JsonElement error)
                    || error.ValueKind != JsonValueKind.String || error.GetString() != "use_dpop_nonce") return initial;
            }
            catch (JsonException) { return initial; }
            catch (InvalidDataException) { return initial; }
            catch { initial.Dispose(); throw; }
            initial.Dispose();
            return await SendOnceAsync(key, nonces[0]).ConfigureAwait(false);
        }
        HttpResponseMessage response = await SendWithNonceAsync(identity).ConfigureAwait(false);
        if (BrokerInstallationSuspendedException.IsSuspended(response))
        {
            response.Dispose();
            throw new BrokerInstallationSuspendedException();
        }
        if (response.StatusCode == HttpStatusCode.Forbidden
            && response.Headers.TryGetValues("Kautuka-Mail-Error", out IEnumerable<string>? errors)
            && errors.Any(value => value == "insufficient_scope")) return response;
        if (response.StatusCode != HttpStatusCode.Forbidden) return response;
        BrokerInstallationIdentity current;
        try { current = await registration.RegisterAsync(origin, ct).ConfigureAwait(false); }
        catch { response.Dispose(); throw; }
        if (current.Id != identity.Id || ConnectorDpop.Thumbprint(current.PrivateJwk) == ConnectorDpop.Thumbprint(identity.PrivateJwk)) return response;
        response.Dispose();
        HttpResponseMessage retry = await SendWithNonceAsync(current).ConfigureAwait(false);
        if (BrokerInstallationSuspendedException.IsSuspended(retry))
        {
            retry.Dispose();
            throw new BrokerInstallationSuspendedException();
        }
        return retry;
    }

    private static byte[] WriteBody(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, int maximumLength, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        byte[] buffer = new byte[maximumLength + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), deadline.Token).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        if (total > maximumLength) throw new InvalidDataException("The broker response is too large.");
        return JsonDocument.Parse(buffer.AsMemory(0, total));
    }

    private static bool IsId(string? value)
        => value is { Length: 43 } && value.All(static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');

    public void Dispose() => client.Dispose();
}

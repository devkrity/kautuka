using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Kautuka.Telemetry.DependencyInjection;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed class OpenObserveIngestionTokenDisabledException : InvalidOperationException
{
    public OpenObserveIngestionTokenDisabledException() { }
    public OpenObserveIngestionTokenDisabledException(string message) : base(message) { }
    public OpenObserveIngestionTokenDisabledException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Creates and retrieves the named ingestion-only credential through the Manager's host UDS route.</summary>
public static class OpenObserveIngestionTokenProvisioner
{
    private static readonly Uri tokenEndpoint = new("http://kautuka-telemetry.internal/api/default/ingestion-tokens");

    public static async Task<string> EnsureAsync(string socketPath, string rootPassword, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPassword);

        using HttpClient client = ManagedTelemetryHttpTransport.CreateClient(socketPath);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(OpenObserveCredentialStore.RootUserEmail + ":" + rootPassword)));

        using HttpResponseMessage listResponse = await client.GetAsync(tokenEndpoint, cancellationToken).ConfigureAwait(false);
        if (listResponse.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("The persisted OpenObserve root credential does not match collector data. Preserve collector data and repair the credential; it was not replaced.");
        await EnsureSuccessAsync(listResponse, "Read OpenObserve ingestion tokens").ConfigureAwait(false);

        string? existing = await FindTokenAsync(listResponse, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return CreateExporterAuthorizationHeader(existing);

        using var createContent = new StringContent(
            "{\"name\":\"" + OpenObserveCredentialStore.IngestionTokenName + "\"}",
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage createResponse = await client.PostAsync(tokenEndpoint, createContent, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(createResponse, "Create OpenObserve ingestion token").ConfigureAwait(false);
        using JsonDocument created = await JsonDocument.ParseAsync(
            await createResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!created.RootElement.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("token", out JsonElement token)
            || token.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(token.GetString()))
            throw new InvalidOperationException("OpenObserve did not return the newly created ingestion token.");

        return CreateExporterAuthorizationHeader(token.GetString()!);
    }

    private static async Task<string?> FindTokenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using JsonDocument document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("OpenObserve returned an invalid ingestion token list.");

        string? found = null;
        foreach (JsonElement item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out JsonElement name)
                || name.ValueKind != JsonValueKind.String
                || !string.Equals(name.GetString(), OpenObserveCredentialStore.IngestionTokenName, StringComparison.Ordinal))
                continue;
            if (item.TryGetProperty("enabled", out JsonElement enabled) && enabled.ValueKind == JsonValueKind.False)
                throw new OpenObserveIngestionTokenDisabledException("The managed OpenObserve ingestion token is disabled; enable or replace it without deleting collector data.");
            if (!item.TryGetProperty("token", out JsonElement token)
                || token.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(token.GetString()))
                throw new InvalidOperationException("OpenObserve did not return the managed ingestion token.");
            if (found is not null)
                throw new InvalidOperationException("OpenObserve contains duplicate managed ingestion tokens; repair the token inventory without deleting collector data.");
            found = token.GetString();
        }

        return found;
    }

    private static Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        string detail = response.StatusCode == HttpStatusCode.Unauthorized
            ? "OpenObserve rejected Manager authentication. Preserve collector data and repair the Manager credential."
            : $"OpenObserve returned HTTP {(int)response.StatusCode} while attempting to {operation}.";
        return Task.FromException(new InvalidOperationException(detail));
    }

    private static string CreateExporterAuthorizationHeader(string token)
        => OpenObserveCredentialStore.CreateBasicAuthorizationHeader(OpenObserveCredentialStore.IngestionTokenName, token);
}

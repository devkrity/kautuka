using System.Text;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Manager.Protos;

namespace Kautuka.Gateway.Infrastructure.Agents;

// This context is built only from authorized Gateway state, never MCP tool arguments.
internal sealed record RemoteMcpRequestContext(
    Uri Endpoint,
    string ConnectorId,
    string UserId,
    bool IncludeSystemScope,
    IReadOnlyList<RemoteMcpCredential> Credentials,
    IReadOnlyList<RemoteMcpHeader> Headers,
    Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>> Resolve,
    Func<CancellationToken, Task>? Revalidate = null,
    Func<RemoteMcpOperationLease>? CreateLease = null,
    string? CredentialNamespace = null,
    string? ProtocolVersion = null,
    bool PreferIpv4 = false,
    bool RequiresCredentialReentry = false,
    Func<Uri, string?, CancellationToken, Task<RemoteConnectorNetworkResponse>>? CheckNetwork = null,
    string? PinnedIpv4 = null,
    bool LegacyAllowPrivateDestination = false,
    RemoteMcpNetworkRouteState? NetworkRouteState = null);

internal sealed class RemoteMcpConfigurationException : HttpRequestException
{
    public RemoteMcpConfigurationException() { }
    public RemoteMcpConfigurationException(string message) : base(message) { }
    public RemoteMcpConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed class RemoteMcpRequestHandler(RemoteMcpRequestContext context, HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    private readonly RemoteMcpOperationLease? lease = context.CreateLease?.Invoke();
    private readonly RemoteMcpNetworkRouteState networkRouteState = context.NetworkRouteState ?? new(context.LegacyAllowPrivateDestination);

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => SendAsync(request, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var linked = lease is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Token);
        cancellationToken = linked?.Token ?? cancellationToken;
        RemoteMcpConfigurationValidator.ValidateEndpoint(context.Endpoint);
        RemoteMcpConfigurationValidator.ValidateEndpoint(request.RequestUri);
        if (context.RequiresCredentialReentry)
            throw new RemoteMcpConfigurationException("This remote connector used the previous credential setup. Enter its key again in connector settings before using it.");
        Uri? destination = request.RequestUri;
        if (destination is null || !destination.IsAbsoluteUri || !string.IsNullOrEmpty(destination.UserInfo)
            || !string.Equals(destination.Scheme, context.Endpoint.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(destination.IdnHost, context.Endpoint.IdnHost, StringComparison.OrdinalIgnoreCase)
            || destination.Port != context.Endpoint.Port)
            throw new HttpRequestException("Remote MCP destination is outside the authorized origin.");

        await CheckNetworkAsync(destination, cancellationToken).ConfigureAwait(false);

        RemoteMcpConfigurationValidator.Validate("http", context.Credentials, context.Headers);
        var needed = context.Headers.Where(h => h.CredentialKey is not null).Select(h => h.CredentialKey!).ToHashSet(StringComparer.Ordinal);
        foreach (RemoteMcpCredential credential in context.Credentials.Where(c => c.Required)) needed.Add(credential.Key);
        string[] keys = [.. needed.Select(key => ConnectorCredentialKeys.BuildRemoteCredentialKey(context.CredentialNamespace ?? throw new InvalidOperationException("Connector credential namespace is missing."), context.Endpoint, key))];
        IReadOnlyDictionary<string, string> resolved;
        try
        {
            resolved = keys.Length == 0 ? new Dictionary<string, string>(StringComparer.Ordinal) : await context.Resolve(keys, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HttpRequestException("Remote MCP credential resolution failed.");
        }

        foreach (RemoteMcpCredential credential in context.Credentials.Where(c => c.Required))
        {
            string key = ConnectorCredentialKeys.BuildRemoteCredentialKey(context.CredentialNamespace ?? throw new InvalidOperationException("Connector credential namespace is missing."), context.Endpoint, credential.Key);
            if (!resolved.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
                throw new RemoteMcpConfigurationException($"Required credential '{credential.Key}' is not configured.");
        }

        var injected = new List<string>();
        var screeningSecrets = new List<string>();
        int aggregate = 0;
        try
        {
            foreach (RemoteMcpHeader header in context.Headers)
            {
                string? value = header.Value;
                if (header.CredentialKey is not null)
                {
                    string storageKey = ConnectorCredentialKeys.BuildRemoteCredentialKey(context.CredentialNamespace ?? throw new InvalidOperationException("Connector credential namespace is missing."), context.Endpoint, header.CredentialKey);
                    if (!resolved.TryGetValue(storageKey, out string? secret) || string.IsNullOrWhiteSpace(secret))
                    {
                        RemoteMcpCredential declaration = context.Credentials.First(c => string.Equals(c.Key, header.CredentialKey, StringComparison.Ordinal));
                        if (declaration.Required) throw new RemoteMcpConfigurationException($"Required credential for header '{header.Name}' is not configured.");
                        continue;
                    }
                    value = string.Concat(header.Prefix, secret);
                    screeningSecrets.Add(secret);
                }
                if (value is null || value.Any(c => c == '\r' || c == '\n' || (char.IsControl(c) && c != '\t')))
                    throw new RemoteMcpConfigurationException($"Header '{header.Name}' contains invalid characters.");
                int valueBytes = Encoding.UTF8.GetByteCount(value);
                if (valueBytes > 8192) throw new RemoteMcpConfigurationException($"Header '{header.Name}' exceeds 8192 bytes.");
                aggregate += Encoding.UTF8.GetByteCount(header.Name) + valueBytes;
                if (aggregate > 32768) throw new RemoteMcpConfigurationException("Connector headers exceed 32768 bytes.");
                if (request.Headers.Contains(header.Name) || !request.Headers.TryAddWithoutValidation(header.Name, value))
                    throw new RemoteMcpConfigurationException($"Header '{header.Name}' could not be added to the request.");
                injected.Add(header.Name);
            }
            if (context.Revalidate is not null) await context.Revalidate(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw RemoteMcpFailureMessages.AsSafeException(ex, context.PreferIpv4 && !HasConfiguredCredential(resolved));
            }
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                response.Dispose();
                throw new HttpRequestException("Remote MCP server returned a redirect, which is not allowed.");
            }
            string? providerFailure = (int)response.StatusCode switch
            {
                401 => "Remote MCP authentication was rejected (HTTP 401). Check the configured credentials.",
                403 when context.Endpoint.IdnHost.Equals("mcp.exa.ai", StringComparison.OrdinalIgnoreCase)
                    && !HasConfiguredCredential(resolved)
                    => "Exa free access was refused on this network. Add an Exa API key or use a network with direct IPv4 access.",
                403 => "Remote MCP access was denied (HTTP 403). Check credentials and the provider's access restrictions.",
                429 => "Remote MCP usage limit was reached (HTTP 429). Check the provider's quota before retrying.",
                _ => null,
            };
            if (providerFailure is not null)
            {
                response.Dispose();
                throw new RemoteMcpConfigurationException(providerFailure);
            }
            RemoteMcpResponseScreening.Apply(response, screeningSecrets);
            if (lease is not null) response.Content = new RemoteMcpLeaseContent(response.Content, lease.Token);
            return response;
        }
        finally
        {
            foreach (string name in injected) request.Headers.Remove(name);
        }
    }

    private async Task CheckNetworkAsync(Uri destination, CancellationToken cancellationToken)
    {
        if (context.CheckNetwork is null)
            throw new RemoteMcpConfigurationException("Remote MCP network authorization is unavailable. No request was sent.");

        RemoteConnectorNetworkResponse response;
        try
        {
            response = await context.CheckNetwork(destination, context.PinnedIpv4, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            throw new RemoteMcpConfigurationException("Remote MCP network authorization is unavailable. No request was sent.", exception);
        }

        if (!response.Enabled)
        {
            if (string.Equals(response.Decision, "allowed", StringComparison.Ordinal))
            {
                string? legacyFailure = ProbeNetworkPolicy.GetDestinationFailure(context.Endpoint.AbsoluteUri, context.LegacyAllowPrivateDestination);
                if (legacyFailure is not null) throw new RemoteMcpConfigurationException(legacyFailure);
                networkRouteState.Accept(response);
                return;
            }
            throw new RemoteMcpConfigurationException("Remote MCP network authorization is unavailable. No request was sent.");
        }

        if (!string.Equals(response.Decision, "allowed", StringComparison.Ordinal))
        {
            string message = string.IsNullOrWhiteSpace(response.Message)
                ? "Remote MCP network access is not authorized. No request was sent."
                : response.Message;
            throw new RemoteMcpConfigurationException(message);
        }

        networkRouteState.Accept(response);
    }

    private bool HasConfiguredCredential(IReadOnlyDictionary<string, string> resolved)
        => context.Headers.Any(header => header.CredentialKey is not null
            && resolved.TryGetValue(ConnectorCredentialKeys.BuildRemoteCredentialKey(
                context.CredentialNamespace ?? string.Empty, context.Endpoint, header.CredentialKey), out string? value)
            && !string.IsNullOrWhiteSpace(value));

    protected override void Dispose(bool disposing)
    {
        if (disposing) lease?.Dispose();
        base.Dispose(disposing);
    }

}

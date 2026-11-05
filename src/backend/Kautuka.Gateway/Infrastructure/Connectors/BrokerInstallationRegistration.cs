using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Kautuka.Gateway.Domain.Connectors;

namespace Kautuka.Gateway.Infrastructure.Connectors;

internal sealed class BrokerInstallationRegistration(BrokerInstallationIdentityProvider identities, HttpClient client) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? registeredOrigin;
    private string? registeredIdentity;

    public async Task<BrokerInstallationIdentity> RegisterAsync(Uri brokerOrigin, CancellationToken cancellationToken)
    {
        if (!brokerOrigin.IsAbsoluteUri || brokerOrigin.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(brokerOrigin.UserInfo) || !string.IsNullOrEmpty(brokerOrigin.Query)
            || !string.IsNullOrEmpty(brokerOrigin.Fragment) || brokerOrigin.AbsolutePath != "/")
            throw new ArgumentException("The broker must be an HTTPS origin.", nameof(brokerOrigin));
        identities.ValidateBrokerOrigin(brokerOrigin);

        string url = brokerOrigin.GetLeftPart(UriPartial.Authority) + "/installation/register";
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string origin = brokerOrigin.GetLeftPart(UriPartial.Authority);
            await identities.RotateAsync(origin, (oldKey, newKey, operation, ct) => SendRotationAsync(origin, oldKey, newKey, operation, ct), create: false, cancellationToken).ConfigureAwait(false);
            BrokerInstallationIdentity identity = await identities.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
            string identityKey = identity.Id + ":" + ConnectorDpop.Thumbprint(identity.PrivateJwk);
            if (string.Equals(registeredOrigin, url, StringComparison.Ordinal)
                && string.Equals(registeredIdentity, identityKey, StringComparison.Ordinal)) return identity;
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("id", identity.Id);
                writer.WriteEndObject();
            }
            byte[] body = buffer.ToArray();
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("Kautuka-Installation-Proof", ConnectorDpop.CreateInstallationProof(identity.PrivateJwk, "POST", url, body));
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NoContent)
                throw new HttpRequestException("The broker could not register this installation.", null, response.StatusCode);
            registeredOrigin = url;
            registeredIdentity = identityKey;
            return identity;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RotateAsync(Uri brokerOrigin, CancellationToken cancellationToken)
    {
        if (!brokerOrigin.IsAbsoluteUri || brokerOrigin.Scheme != Uri.UriSchemeHttps || brokerOrigin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(brokerOrigin.UserInfo) || !string.IsNullOrEmpty(brokerOrigin.Query) || !string.IsNullOrEmpty(brokerOrigin.Fragment))
            throw new ArgumentException("The broker must be an HTTPS origin.", nameof(brokerOrigin));
        identities.ValidateBrokerOrigin(brokerOrigin);
        string origin = brokerOrigin.GetLeftPart(UriPartial.Authority);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (registeredOrigin != origin + "/installation/register") throw new InvalidOperationException("Register the installation before rotating its key.");
            await identities.RotateAsync(origin, (oldKey, newKey, operation, ct) => SendRotationAsync(origin, oldKey, newKey, operation, ct), create: true, cancellationToken).ConfigureAwait(false);
            registeredOrigin = null;
            registeredIdentity = null;
        }
        finally { gate.Release(); }
    }

    private async Task SendRotationAsync(string origin, BrokerInstallationIdentity previous, BrokerInstallationIdentity next, string operation, CancellationToken ct)
    {
        // The same canonical request bytes are covered by both signatures.
        string url = origin + "/installation/rotate";
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) { writer.WriteStartObject(); writer.WriteString("id", previous.Id); writer.WriteString("operationId", operation); writer.WriteString("replacementThumbprint", ConnectorDpop.Thumbprint(next.PrivateJwk)); writer.WriteEndObject(); }
        byte[] body = buffer.ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("Kautuka-Installation-Proof", ConnectorDpop.CreateInstallationProof(previous.PrivateJwk, "POST", url, body));
        request.Headers.Add("Kautuka-Replacement-Proof", ConnectorDpop.CreateInstallationProof(next.PrivateJwk, "POST", url, body));
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent) throw new HttpRequestException("The broker could not rotate this installation key.", null, response.StatusCode);
    }

    public async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { registeredOrigin = null; registeredIdentity = null; }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        gate.Dispose();
        client.Dispose();
    }
}

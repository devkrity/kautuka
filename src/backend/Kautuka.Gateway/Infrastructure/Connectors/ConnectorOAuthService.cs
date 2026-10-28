using Kautuka.Gateway.Domain.Connectors;

namespace Kautuka.Gateway.Infrastructure.Connectors;

internal sealed class ConnectorOAuthService(
    ConnectorAuthCatalog catalog,
    IConnectorRefreshTokenStore tokens,
    ConnectorAuthorizationClient client,
    ConnectorOAuthSessionStore sessions,
    ConnectorAuthEndpoint? authEndpoint = null)
{
    private readonly object beginAttemptsLock = new();
    private readonly Dictionary<string, (DateTimeOffset WindowStart, int Count)> beginAttempts = new(StringComparer.Ordinal);
    private readonly ConnectorAuthEndpoint endpoint = authEndpoint ?? ConnectorAuthEndpoint.Local;
    public ConnectorOAuthBeginResponse Begin(string userId, string connectorId, string? origin)
    {
        if (!AllowBegin(userId))
        {
            return new ConnectorOAuthBeginResponse("rejected", "Too many sign-in attempts. Wait a few minutes and try again.", null);
        }

        ConnectorAuthProfile? profile = catalog.Find(connectorId);
        if (profile is null)
        {
            return new ConnectorOAuthBeginResponse("missing", "This connector does not use sign-in.", null);
        }

        if (endpoint.IsMisconfigured)
        {
            return new ConnectorOAuthBeginResponse("not_configured", "Sign-in is not configured on this appliance yet.", null);
        }

        if (!profile.IsReady(endpoint))
        {
            return new ConnectorOAuthBeginResponse("not_configured", profile.ConfigurationMessage, null);
        }

        if (!ConnectorAuthProfile.TryNormalizeReturnOrigin(origin, out string returnOrigin))
        {
            return new ConnectorOAuthBeginResponse("rejected", "Sign-in can only return to this Kautuka address.", null);
        }

        string redirectUri;
        if (endpoint.IsHosted)
        {
            redirectUri = endpoint.ProviderCallback;
        }
        else if (!ConnectorAuthEndpoint.TryLoopbackCallback(returnOrigin, out redirectUri))
        {
            return new ConnectorOAuthBeginResponse("rejected", "Open Kautuka at 127.0.0.1 to sign in from this checkout.", null);
        }

        string verifier = ConnectorAuthProfile.CreateCodeVerifier();
        string state = ConnectorAuthProfile.CreateState();
        string challenge = ConnectorAuthProfile.CreateCodeChallenge(verifier);
        if (!sessions.Save(state, new ConnectorOAuthSession
        {
            UserId = userId,
            ConnectorId = profile.Id,
            CodeVerifier = verifier,
            RedirectUri = redirectUri,
            ReturnOrigin = returnOrigin,
            DpopPrivateJwk = UsesDpop(profile) ? ConnectorDpop.CreatePrivateJwk() : null,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        }))
        {
            return new ConnectorOAuthBeginResponse("rejected", "Sign-in is temporarily busy. Try again shortly.", null);
        }
        string startUrl = endpoint.IsHosted
            ? profile.BuildHostedStartUrl(endpoint.Host!, returnOrigin, state, challenge)
            : profile.BuildAuthorizeUrl(state, challenge, redirectUri);
        return new ConnectorOAuthBeginResponse(
            "redirect",
            "Continue in the browser to sign in.",
            startUrl);
    }

    private bool AllowBegin(string userId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (beginAttemptsLock)
        {
            if (beginAttempts.Count >= 4096)
            {
                foreach (string expired in beginAttempts
                    .Where(pair => now - pair.Value.WindowStart >= TimeSpan.FromMinutes(5))
                    .Select(pair => pair.Key)
                    .ToArray())
                {
                    _ = beginAttempts.Remove(expired);
                }
            }

            if (!beginAttempts.TryGetValue(userId, out (DateTimeOffset WindowStart, int Count) prior))
            {
                if (beginAttempts.Count >= 4096)
                {
                    return false;
                }

                beginAttempts[userId] = (now, 1);
                return true;
            }

            var updated = now - prior.WindowStart >= TimeSpan.FromMinutes(5)
                ? (now, 1)
                : (prior.WindowStart, prior.Count + 1);
            beginAttempts[userId] = updated;
            return updated.Item2 <= 10;
        }
    }

    public async Task<ConnectorOAuthCompleteResponse> CompleteAsync(
        string userId,
        string? code,
        string? state,
        CancellationToken cancellationToken,
        string? brokerTicket = null)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return new ConnectorOAuthCompleteResponse("rejected", "Microsoft did not return a sign-in code.");
        }

        if (!sessions.TryTake(state, out ConnectorOAuthSession? session) || session is null)
        {
            return new ConnectorOAuthCompleteResponse("rejected", "This sign-in session has expired. Start again from the Connectors page.");
        }

        if (!string.Equals(session.UserId, userId, StringComparison.Ordinal) || session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return new ConnectorOAuthCompleteResponse("rejected", "This sign-in session has expired. Start again from the Connectors page.");
        }

        ConnectorAuthProfile? profile = catalog.Find(session.ConnectorId);
        if (profile is null || !profile.IsReady(endpoint))
        {
            return new ConnectorOAuthCompleteResponse("not_configured", profile?.ConfigurationMessage ?? ConnectorAuthProfile.NotConfiguredMessage);
        }

        bool brokerExchange = UsesBroker(profile);
        if (string.IsNullOrWhiteSpace(code) || brokerExchange && string.IsNullOrWhiteSpace(brokerTicket))
        {
            return new ConnectorOAuthCompleteResponse("rejected", "The sign-in response was not accepted.");
        }

        ConnectorTokenResult exchanged = await ExchangeCodeAsync(
            profile,
            code!,
            session,
            cancellationToken,
            brokerTicket,
            session.ReturnOrigin).ConfigureAwait(false);
        if (!exchanged.Success || string.IsNullOrWhiteSpace(exchanged.RefreshToken))
        {
            return new ConnectorOAuthCompleteResponse("rejected", exchanged.Error ?? "The sign-in was not accepted.");
        }

        string exchangedRefreshToken = exchanged.RefreshToken!;
        if (brokerExchange && !IsBrokerRefreshEnvelope(exchangedRefreshToken))
        {
            return new ConnectorOAuthCompleteResponse("rejected", "The sign-in response was not accepted.");
        }

        await tokens.SetAsync(userId, profile.RefreshCredentialKey, exchangedRefreshToken, cancellationToken).ConfigureAwait(false);
        if (UsesDpop(profile) && !string.IsNullOrWhiteSpace(session.DpopPrivateJwk) && !string.IsNullOrWhiteSpace(profile.DpopKeyCredentialKey))
        {
            await tokens.SetAsync(userId, profile.DpopKeyCredentialKey, session.DpopPrivateJwk, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(exchanged.DpopNonce) && !string.IsNullOrWhiteSpace(profile.DpopNonceCredentialKey))
        {
            await tokens.SetAsync(userId, profile.DpopNonceCredentialKey, exchanged.DpopNonce, cancellationToken).ConfigureAwait(false);
        }

        return new ConnectorOAuthCompleteResponse("connected", "Signed in.");
    }

    public async Task<ConnectorOAuthCatalogResponse> ListStatusAsync(string userId, CancellationToken cancellationToken)
    {
        var items = new List<ConnectorOAuthStatusItem>(catalog.Profiles.Count);
        foreach (ConnectorAuthProfile profile in catalog.Profiles)
        {
            items.Add(await BuildStatusAsync(userId, profile, cancellationToken).ConfigureAwait(false));
        }

        return new ConnectorOAuthCatalogResponse([.. items]);
    }

    public async Task<ConnectorOAuthDisconnectResponse> DisconnectAsync(
        string userId,
        string connectorId,
        CancellationToken cancellationToken)
    {
        ConnectorAuthProfile? profile = catalog.Find(connectorId);
        if (profile is null)
        {
            return new ConnectorOAuthDisconnectResponse(false, "This connector does not use sign-in.", null);
        }

        string? currentRefreshToken = await tokens.GetAsync(userId, profile.RefreshCredentialKey, cancellationToken).ConfigureAwait(false);
        bool legacyHostedCredential = UsesBroker(profile)
            && !string.IsNullOrWhiteSpace(currentRefreshToken)
            && !IsBrokerRefreshEnvelope(currentRefreshToken);
        try
        {
            if (!legacyHostedCredential && !await RevokeBrokerKeyAsync(profile, userId, cancellationToken).ConfigureAwait(false))
            {
                return new ConnectorOAuthDisconnectResponse(
                    false,
                    "The mailbox is still signed in because the sign-in key could not be revoked.",
                    profile.RevokeUrl);
            }
        }
        catch (HttpRequestException)
        {
            return new ConnectorOAuthDisconnectResponse(
                false,
                "The mailbox is still signed in because the sign-in key could not be revoked.",
                profile.RevokeUrl);
        }

        await tokens.DeleteAsync(userId, profile.RefreshCredentialKey, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(profile.DpopKeyCredentialKey))
        {
            await tokens.DeleteAsync(userId, profile.DpopKeyCredentialKey, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(profile.DpopNonceCredentialKey))
        {
            await tokens.DeleteAsync(userId, profile.DpopNonceCredentialKey, cancellationToken).ConfigureAwait(false);
        }

        return new ConnectorOAuthDisconnectResponse(
            true,
            legacyHostedCredential
                ? "Removed this saved sign-in. Remove Kautuka on the permissions page to revoke its existing access."
                : "Disconnected. You can also remove Kautuka on the permissions page.",
            profile.RevokeUrl);
    }

    public async Task<Dictionary<string, string>> PrepareAsync(
        string connectorId,
        string? userId,
        Dictionary<string, string> environment,
        bool exchangeAccessToken,
        CancellationToken cancellationToken)
    {
        ConnectorAuthProfile? profile = catalog.Find(connectorId);
        if (profile is null)
        {
            return environment;
        }

        environment.TryGetValue(profile.RefreshCredentialKey, out string? refreshToken);
        RemoveHeldCredentials(profile, environment);
        if (!exchangeAccessToken)
        {
            return environment;
        }

        if (string.IsNullOrWhiteSpace(refreshToken) && !string.IsNullOrWhiteSpace(userId))
        {
            refreshToken = await tokens.GetAsync(userId, profile.RefreshCredentialKey, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException(ConnectorAuthProfile.SignInRequiredMessage);
        }

        if (UsesBroker(profile) && !IsBrokerRefreshEnvelope(refreshToken))
        {
            throw new InvalidOperationException("This mailbox sign-in needs to be renewed. Reconnect it from the Connectors page.");
        }

        if (!profile.IsReady(endpoint))
        {
            throw new InvalidOperationException(profile.ConfigurationMessage);
        }

        ConnectorTokenResult refreshed = await RefreshAsync(profile, userId, refreshToken, cancellationToken).ConfigureAwait(false);
        if (!refreshed.Success || string.IsNullOrWhiteSpace(refreshed.AccessToken))
        {
            throw new InvalidOperationException(refreshed.Error ?? ConnectorAuthProfile.SignInRequiredMessage);
        }

        if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken))
        {
            await tokens.SetAsync(userId, profile.RefreshCredentialKey, refreshed.RefreshToken, cancellationToken).ConfigureAwait(false);
        }

        environment[profile.AccessTokenEnvironmentKey] = refreshed.AccessToken;
        return environment;
    }

    public void RemoveRefreshTokens(string? connectorId, IDictionary<string, string> environment)
    {
        ConnectorAuthProfile? profile = catalog.Find(connectorId);
        if (profile is not null)
        {
            RemoveHeldCredentials(profile, environment);
        }
    }

    public bool IsRefreshCredential(string? connectorId, string key)
    {
        ConnectorAuthProfile? profile = catalog.Find(connectorId);
        return profile is not null && string.Equals(profile.RefreshCredentialKey, key, StringComparison.Ordinal);
    }

    private async Task<ConnectorTokenResult> ExchangeCodeAsync(
        ConnectorAuthProfile profile,
        string code,
        ConnectorOAuthSession session,
        CancellationToken cancellationToken,
        string? brokerTicket = null,
        string? returnOrigin = null)
    {
        string? proof = ProofFor(profile, session.DpopPrivateJwk, nonce: null, code);
        if (UsesDpop(profile) && proof is null)
        {
            return new ConnectorTokenResult(false, null, null, ConnectorAuthProfile.SignInRequiredMessage);
        }

        string redirectUri = session.RedirectUri ?? profile.RedirectUri;
        ConnectorTokenResult exchanged = await client.ExchangeCodeAsync(
            profile,
            code,
            session.CodeVerifier,
            proof,
            redirectUri,
            TokenUrl(profile),
            IncludeClientCredentials(profile),
            brokerTicket,
            returnOrigin,
            cancellationToken).ConfigureAwait(false);
        if (exchanged.UseDpopNonce && !string.IsNullOrWhiteSpace(exchanged.DpopNonce))
        {
            proof = ProofFor(profile, session.DpopPrivateJwk, exchanged.DpopNonce, code);
            exchanged = await client.ExchangeCodeAsync(
                profile,
                code,
                session.CodeVerifier,
                proof,
                redirectUri,
                TokenUrl(profile),
                IncludeClientCredentials(profile),
                brokerTicket,
                returnOrigin,
                cancellationToken).ConfigureAwait(false);
        }

        return exchanged;
    }

    private async Task<ConnectorTokenResult> RefreshAsync(
        ConnectorAuthProfile profile,
        string userId,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        string? privateJwk = null;
        string? nonce = null;
        if (UsesDpop(profile))
        {
            if (string.IsNullOrWhiteSpace(profile.DpopKeyCredentialKey))
            {
                return new ConnectorTokenResult(false, null, null, ConnectorAuthProfile.SignInRequiredMessage);
            }

            privateJwk = await tokens.GetAsync(userId, profile.DpopKeyCredentialKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(privateJwk))
            {
                return new ConnectorTokenResult(false, null, null, ConnectorAuthProfile.SignInRequiredMessage);
            }

            if (!string.IsNullOrWhiteSpace(profile.DpopNonceCredentialKey))
            {
                nonce = await tokens.GetAsync(userId, profile.DpopNonceCredentialKey, cancellationToken).ConfigureAwait(false);
            }
        }

        string? proof = ProofFor(profile, privateJwk, nonce, authorizationCode: null);
        ConnectorTokenResult refreshed = await client.RefreshAsync(
            profile,
            refreshToken,
            proof,
            TokenUrl(profile),
            IncludeClientCredentials(profile),
            cancellationToken).ConfigureAwait(false);
        if (refreshed.UseDpopNonce && !string.IsNullOrWhiteSpace(refreshed.DpopNonce))
        {
            if (!string.IsNullOrWhiteSpace(profile.DpopNonceCredentialKey))
            {
                await tokens.SetAsync(userId, profile.DpopNonceCredentialKey, refreshed.DpopNonce, cancellationToken).ConfigureAwait(false);
            }

            proof = ProofFor(profile, privateJwk, refreshed.DpopNonce, authorizationCode: null);
            refreshed = await client.RefreshAsync(
                profile,
                refreshToken,
                proof,
                TokenUrl(profile),
                IncludeClientCredentials(profile),
                cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(refreshed.DpopNonce) && !string.IsNullOrWhiteSpace(profile.DpopNonceCredentialKey))
        {
            await tokens.SetAsync(userId, profile.DpopNonceCredentialKey, refreshed.DpopNonce, cancellationToken).ConfigureAwait(false);
        }

        if (UsesBroker(profile)
            && refreshed.Success
            && !string.IsNullOrWhiteSpace(refreshed.RefreshToken)
            && !IsBrokerRefreshEnvelope(refreshed.RefreshToken))
        {
            return new ConnectorTokenResult(false, null, null, ConnectorAuthProfile.SignInRequiredMessage);
        }

        return refreshed;
    }

    private async Task<bool> RevokeBrokerKeyAsync(
        ConnectorAuthProfile profile,
        string userId,
        CancellationToken cancellationToken)
    {
        if (!UsesDpop(profile) || string.IsNullOrWhiteSpace(profile.DpopKeyCredentialKey) || endpoint.Host is null)
        {
            return true;
        }

        string? privateJwk = await tokens.GetAsync(userId, profile.DpopKeyCredentialKey, cancellationToken).ConfigureAwait(false);
        string? refreshToken = await tokens.GetAsync(userId, profile.RefreshCredentialKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(privateJwk) || string.IsNullOrWhiteSpace(refreshToken))
        {
            return string.IsNullOrWhiteSpace(privateJwk) && string.IsNullOrWhiteSpace(refreshToken);
        }

        if (!IsBrokerRefreshEnvelope(refreshToken))
        {
            return false;
        }

        string? route = ConnectorAuthEndpoint.KeyRoute(profile.Id);
        string? url = route is null
            ? null
            : ConnectorDpop.KeyRevokeUrl(endpoint.Host, route, ConnectorDpop.Thumbprint(privateJwk));
        if (url is null)
        {
            return false;
        }

        string? nonce = string.IsNullOrWhiteSpace(profile.DpopNonceCredentialKey)
            ? null
            : await tokens.GetAsync(userId, profile.DpopNonceCredentialKey, cancellationToken).ConfigureAwait(false);
        string proof = ConnectorDpop.CreateProof(privateJwk, nonce, authorizationCode: null, "DELETE", url);
        ConnectorDpopRevokeResult revoked = await client.RevokeDpopKeyAsync(url, refreshToken, proof, cancellationToken).ConfigureAwait(false);
        if (revoked.UseDpopNonce && !string.IsNullOrWhiteSpace(revoked.DpopNonce))
        {
            if (!string.IsNullOrWhiteSpace(profile.DpopNonceCredentialKey))
            {
                await tokens.SetAsync(userId, profile.DpopNonceCredentialKey, revoked.DpopNonce, cancellationToken).ConfigureAwait(false);
            }

            proof = ConnectorDpop.CreateProof(privateJwk, revoked.DpopNonce, authorizationCode: null, "DELETE", url);
            revoked = await client.RevokeDpopKeyAsync(url, refreshToken, proof, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(revoked.DpopNonce) && !string.IsNullOrWhiteSpace(profile.DpopNonceCredentialKey))
        {
            await tokens.SetAsync(userId, profile.DpopNonceCredentialKey, revoked.DpopNonce, cancellationToken).ConfigureAwait(false);
        }

        return revoked.Success;
    }

    private bool UsesBroker(ConnectorAuthProfile profile)
        => endpoint.BrokerUrl(profile.Id) is not null;

    private bool UsesDpop(ConnectorAuthProfile profile)
        => UsesBroker(profile) && profile.UseDpop;

    private string TokenUrl(ConnectorAuthProfile profile)
        => endpoint.BrokerUrl(profile.Id) ?? profile.TokenUrl;

    private bool IncludeClientCredentials(ConnectorAuthProfile profile)
        => !UsesBroker(profile);

    private string? ProofFor(ConnectorAuthProfile profile, string? privateJwk, string? nonce, string? authorizationCode)
    {
        if (!UsesDpop(profile))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(privateJwk))
        {
            return null;
        }

        string htu = UsesBroker(profile) ? TokenUrl(profile) : ConnectorDpop.TokenEndpoint;
        return ConnectorDpop.CreateProof(privateJwk, nonce, authorizationCode, "POST", htu);
    }

    private static bool IsBrokerRefreshEnvelope(string token)
    {
        string[] parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 4
            || !string.Equals(parts[0], "krt1", StringComparison.Ordinal)
            || parts[1].Length is 0 or > 64
            || parts[2].Length != 16
            || parts[3].Length < 22
            || parts[3].Length % 4 == 1)
        {
            return false;
        }

        return parts.Skip(1).All(static part => part.All(static c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }

    private static void RemoveHeldCredentials(ConnectorAuthProfile profile, IDictionary<string, string> environment)
    {
        environment.Remove(profile.RefreshCredentialKey);
        environment.Remove(profile.AccessTokenEnvironmentKey);
        if (!string.IsNullOrWhiteSpace(profile.DpopKeyCredentialKey))
        {
            environment.Remove(profile.DpopKeyCredentialKey);
        }

        if (!string.IsNullOrWhiteSpace(profile.DpopNonceCredentialKey))
        {
            environment.Remove(profile.DpopNonceCredentialKey);
        }
    }

    private async Task<ConnectorOAuthStatusItem> BuildStatusAsync(
        string userId,
        ConnectorAuthProfile profile,
        CancellationToken cancellationToken)
    {
        bool configured = profile.IsReady(endpoint);
        string? refresh = await tokens.GetAsync(userId, profile.RefreshCredentialKey, cancellationToken).ConfigureAwait(false);
        bool legacyHostedCredential = endpoint.IsHosted
            && !string.IsNullOrWhiteSpace(refresh)
            && !IsBrokerRefreshEnvelope(refresh);
        bool connected = !string.IsNullOrWhiteSpace(refresh) && !legacyHostedCredential;
        string message = connected
            ? "Signed in. Sending starts as Ask me first, and you can change that on the agent."
            : legacyHostedCredential
                ? "Reconnect this mailbox to continue using it."
            : configured
                ? endpoint.IsHosted
                    ? "Sign in once. Kautuka keeps the mailbox connection. You do not register an app."
                    : "Sign in with the OAuth app configured for this checkout."
                : profile.ConfigurationMessage;
        return new ConnectorOAuthStatusItem(
            profile.Id,
            connected,
            !string.IsNullOrWhiteSpace(refresh),
            configured,
            message,
            profile.SignInLabel,
            profile.RevokeUrl);
    }
}

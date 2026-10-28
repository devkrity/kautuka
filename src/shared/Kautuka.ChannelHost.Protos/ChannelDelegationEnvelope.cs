using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Google.Protobuf;

using Kautuka.Protos;

namespace Kautuka.ChannelHost.Protos;

public static class ChannelDelegationEnvelope
{
    public const string AuthorizationVersion = "kautuka-channel-auth/v1";
    public const string ContractVersion = "kautuka-channelhost/v1";
    public const string Issuer = "kautuka-gateway";
    public const string Audience = "kautuka-channel-gateway";
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(10);

    public static void Sign(
        IMessage request,
        ScopedRequestContext context,
        string operation,
        X509Certificate2 signingCertificate,
        DateTimeOffset now,
        string issuer = Issuer,
        string audience = Audience)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(signingCertificate);

        context.ActorCapabilities.Clear();
        if (string.IsNullOrWhiteSpace(context.TenantId)) context.TenantId = "default";
        context.AuthorizationVersion = AuthorizationVersion;
        context.ContractVersion = ContractVersion;
        context.DelegationIssuer = issuer;
        context.DelegationAudience = audience;
        context.DelegatedOperation = operation;
        context.DelegationIssuedAtUtc = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        context.DelegationExpiresAtUtc = now.Add(Lifetime).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        context.DelegationTokenId = Guid.NewGuid().ToString("N");
        context.DelegationKeyId = NormalizeThumbprint(signingCertificate.Thumbprint);
        context.DelegationSignature = ByteString.Empty;
        context.RequestDigest = string.Empty;
        context.RequestDigest = ComputeRequestDigest(request, context);

        using ECDsa? key = signingCertificate.GetECDsaPrivateKey();
        if (key is null)
            throw new InvalidOperationException("The Channel Gateway delegation certificate does not contain an ECDSA private key.");
        context.DelegationSignature = ByteString.CopyFrom(key.SignData(
            BuildSigningPayload(context),
            HashAlgorithmName.SHA256));
    }

    public static ChannelDelegationValidationResult Validate(
        IMessage request,
        ScopedRequestContext? context,
        string expectedOperation,
        X509Certificate2 signingCertificate,
        DateTimeOffset now,
        string expectedIssuer = Issuer,
        string expectedAudience = Audience)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(signingCertificate);
        if (context is null)
            return ChannelDelegationValidationResult.Deny("delegation_missing");
        if (context.ActorCapabilities.Count != 0)
            return ChannelDelegationValidationResult.Deny("client_capabilities_forbidden");
        if (!string.Equals(context.AuthorizationVersion, AuthorizationVersion, StringComparison.Ordinal)
            || !string.Equals(context.ContractVersion, ContractVersion, StringComparison.Ordinal))
            return ChannelDelegationValidationResult.Deny("security_contract_mismatch");
        if (!string.Equals(context.DelegationIssuer, expectedIssuer, StringComparison.Ordinal)
            || !string.Equals(context.DelegationAudience, expectedAudience, StringComparison.Ordinal))
            return ChannelDelegationValidationResult.Deny("delegation_issuer_or_audience_invalid");
        if (!string.Equals(context.DelegatedOperation, expectedOperation, StringComparison.Ordinal))
            return ChannelDelegationValidationResult.Deny("delegated_operation_invalid");
        if (string.IsNullOrWhiteSpace(context.RequestId)
            || string.IsNullOrWhiteSpace(context.ActorUserId)
            || string.IsNullOrWhiteSpace(context.DelegationTokenId)
            || context.DelegationSignature.IsEmpty)
            return ChannelDelegationValidationResult.Deny("delegation_claims_incomplete");
        if (!DateTimeOffset.TryParse(context.DelegationIssuedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset issued)
            || !DateTimeOffset.TryParse(context.DelegationExpiresAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset expires)
            || issued > now.Add(ClockSkew)
            || expires < now.Subtract(ClockSkew)
            || expires <= issued
            || expires - issued > Lifetime + ClockSkew)
            return ChannelDelegationValidationResult.Deny("delegation_expired_or_not_yet_valid");
        if (!string.Equals(context.DelegationKeyId, NormalizeThumbprint(signingCertificate.Thumbprint), StringComparison.Ordinal))
            return ChannelDelegationValidationResult.Deny("delegation_key_invalid");

        string suppliedDigest = context.RequestDigest;
        string calculatedDigest = ComputeRequestDigest(request, context);
        byte[] suppliedDigestBytes = Encoding.ASCII.GetBytes(suppliedDigest);
        byte[] calculatedDigestBytes = Encoding.ASCII.GetBytes(calculatedDigest);
        if (suppliedDigestBytes.Length != calculatedDigestBytes.Length
            || !CryptographicOperations.FixedTimeEquals(suppliedDigestBytes, calculatedDigestBytes))
            return ChannelDelegationValidationResult.Deny("request_digest_invalid");

        using ECDsa? key = signingCertificate.GetECDsaPublicKey();
        bool valid = key is not null && key.VerifyData(
            BuildSigningPayload(context),
            context.DelegationSignature.Span,
            HashAlgorithmName.SHA256);
        return valid
            ? ChannelDelegationValidationResult.Allow(calculatedDigest)
            : ChannelDelegationValidationResult.Deny("delegation_signature_invalid");
    }

    public static string ComputeRequestDigest(IMessage request, ScopedRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        string digest = context.RequestDigest;
        ByteString signature = context.DelegationSignature;
        string authorizationVersion = context.AuthorizationVersion;
        string issuer = context.DelegationIssuer;
        string audience = context.DelegationAudience;
        string operation = context.DelegatedOperation;
        string issued = context.DelegationIssuedAtUtc;
        string expires = context.DelegationExpiresAtUtc;
        string token = context.DelegationTokenId;
        string key = context.DelegationKeyId;
        string requestId = context.RequestId;
        string submittedAt = context.SubmittedAtUtc;
        string gatewaySessionId = context.GatewaySessionId;
        try
        {
            // These claims remain covered by BuildSigningPayload, but they are correlation/
            // freshness metadata rather than the durable business effect. Excluding them
            // lets a caller reconstruct the same idempotent mutation after a restart.
            context.RequestId = string.Empty;
            context.SubmittedAtUtc = string.Empty;
            context.GatewaySessionId = string.Empty;
            context.RequestDigest = string.Empty;
            context.DelegationSignature = ByteString.Empty;
            context.AuthorizationVersion = string.Empty;
            context.DelegationIssuer = string.Empty;
            context.DelegationAudience = string.Empty;
            context.DelegatedOperation = string.Empty;
            context.DelegationIssuedAtUtc = string.Empty;
            context.DelegationExpiresAtUtc = string.Empty;
            context.DelegationTokenId = string.Empty;
            context.DelegationKeyId = string.Empty;
            return Convert.ToHexStringLower(SHA256.HashData(request.ToByteArray()));
        }
        finally
        {
            context.RequestDigest = digest;
            context.DelegationSignature = signature;
            context.AuthorizationVersion = authorizationVersion;
            context.DelegationIssuer = issuer;
            context.DelegationAudience = audience;
            context.DelegatedOperation = operation;
            context.DelegationIssuedAtUtc = issued;
            context.DelegationExpiresAtUtc = expires;
            context.DelegationTokenId = token;
            context.DelegationKeyId = key;
            context.RequestId = requestId;
            context.SubmittedAtUtc = submittedAt;
            context.GatewaySessionId = gatewaySessionId;
        }
    }

    private static byte[] BuildSigningPayload(ScopedRequestContext context) => Encoding.UTF8.GetBytes(string.Join('\n',
        context.AuthorizationVersion,
        context.ContractVersion,
        context.DelegationIssuer,
        context.DelegationAudience,
        context.DelegatedOperation,
        context.ActorUserId,
        context.TenantId,
        context.GatewaySessionId,
        context.RequestId,
        context.DelegationIssuedAtUtc,
        context.DelegationExpiresAtUtc,
        context.DelegationTokenId,
        context.RequestDigest,
        context.DelegationKeyId));

    private static string NormalizeThumbprint(string? thumbprint) =>
        (thumbprint ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}

public readonly record struct ChannelDelegationValidationResult(bool Succeeded, string Digest, string DenialReason)
{
    public static ChannelDelegationValidationResult Allow(string digest) => new(true, digest, string.Empty);
    public static ChannelDelegationValidationResult Deny(string reason) => new(false, string.Empty, reason);
}

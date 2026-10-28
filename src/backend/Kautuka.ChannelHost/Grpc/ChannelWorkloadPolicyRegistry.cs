using System.Security.Cryptography.X509Certificates;

using Kautuka.Telemetry.Security;

namespace Kautuka.ChannelHost.Grpc;

internal sealed class ChannelWorkloadPolicyRegistry(
    X509Certificate2 trustRoot,
    IReadOnlyList<ChannelWorkloadPolicy> policies)
{
    private long transportDenials;
    public long TransportDenialCount => Interlocked.Read(ref transportDenials);
    public ChannelWorkloadPolicy DefaultPolicy { get; } = policies[0];
    private readonly Dictionary<string, ChannelWorkloadPolicy> byThumbprint = policies
        .SelectMany(policy => policy.CertificateThumbprints.Select(thumbprint => KeyValuePair.Create(Normalize(thumbprint), policy)))
        .ToDictionary(StringComparer.OrdinalIgnoreCase);

    public ChannelWorkloadPolicy? Resolve(X509Certificate2 certificate)
    {
        if (!byThumbprint.TryGetValue(Normalize(certificate.Thumbprint), out ChannelWorkloadPolicy? policy)) return null;
        return LocalCaMtlsBootstrap.ValidateExactClientCert(
            certificate, trustRoot, policy.CertificateCommonName, policy.WorkloadIdentityUri)
            ? policy
            : null;
    }

    public bool ValidateTransport(X509Certificate2 certificate)
    {
        bool allowed = Resolve(certificate) is not null;
        if (!allowed) _ = Interlocked.Increment(ref transportDenials);
        return allowed;
    }

    public ChannelWorkloadPolicy RequireAuthorized(X509Certificate2 certificate, string method, string operation)
    {
        ChannelWorkloadPolicy? policy = Resolve(certificate);
        if (policy is null || !policy.AllowedMethods.Contains(method) || !policy.AllowedOperations.Contains(operation))
            throw new UnauthorizedAccessException("workload_policy_denied");
        return policy;
    }

    public static ChannelWorkloadPolicyRegistry Create(
        X509Certificate2 trustRoot,
        ChannelWorkloadPolicy gatewayPolicy,
        IConfiguration configuration)
    {
        var policies = new List<ChannelWorkloadPolicy> { gatewayPolicy };
        foreach (IConfigurationSection section in configuration.GetSection("Kautuka:ChannelGateway:ExternalWorkloads").GetChildren())
        {
            if (!section.GetValue("Enabled", false)) continue;
            string certificatePath = section["DelegationPublicCertificatePath"] ?? string.Empty;
            string identityUri = section["WorkloadIdentityUri"] ?? string.Empty;
            if (!Uri.TryCreate(identityUri, UriKind.Absolute, out Uri? uri) || !File.Exists(certificatePath))
                throw new InvalidOperationException($"External Channel workload '{section.Key}' has an invalid identity or delegation certificate.");
            policies.Add(new ChannelWorkloadPolicy(
                section.Key,
                section.GetSection("CertificateThumbprints").Get<string[]>()?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [],
                section["CertificateCommonName"] ?? string.Empty,
                uri,
                section.GetSection("AllowedMethods").Get<string[]>()?.ToHashSet(StringComparer.Ordinal) ?? [],
                section.GetSection("AllowedOperations").Get<string[]>()?.ToHashSet(StringComparer.Ordinal) ?? [],
                section["DelegationIssuer"] ?? string.Empty,
                section["DelegationAudience"] ?? "kautuka-channel-gateway",
                [X509CertificateLoader.LoadCertificateFromFile(certificatePath)],
                section.GetSection("AllowedOwnerUserIds").Get<string[]>()?.ToHashSet(StringComparer.Ordinal) ?? [],
                OwnerScopeWildcard: false,
                AllowCredentialOperations: false,
                AllowAdministrativeOperations: false,
                AllowCanonicalIngress: false));
        }
        return new ChannelWorkloadPolicyRegistry(trustRoot, policies);
    }

    private static string Normalize(string? thumbprint) =>
        (thumbprint ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}

internal sealed record ChannelWorkloadPolicy(
    string WorkloadId,
    IReadOnlySet<string> CertificateThumbprints,
    string CertificateCommonName,
    Uri WorkloadIdentityUri,
    IReadOnlySet<string> AllowedMethods,
    IReadOnlySet<string> AllowedOperations,
    string DelegationIssuer,
    string DelegationAudience,
    IReadOnlyList<X509Certificate2> DelegationVerificationKeys,
    IReadOnlySet<string> AllowedOwnerUserIds,
    bool OwnerScopeWildcard,
    bool AllowCredentialOperations,
    bool AllowAdministrativeOperations,
    bool AllowCanonicalIngress);

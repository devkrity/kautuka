using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;

namespace Kautuka.Telemetry.Security;

/// <summary>
/// Installer-only provisioning for the Channel Gateway trust boundary. Runtime
/// services receive disjoint inventories and never receive the bootstrap CA key.
/// </summary>
public static class ChannelSecurityMaterialProvisioner
{
    public const string ServerPfx = "channel-server.pfx";
    public const string GatewayClientPfx = "gateway-channel-client.pfx";
    public const string GatewayDelegationPfx = "gateway-channel-delegation.pfx";
    public const string CaCertificate = "channel-ca.cer";
    public const string GatewayClientCertificate = "gateway-channel-client.cer";
    public const string GatewayDelegationCertificate = "gateway-channel-delegation.cer";
    public const string NextSuffix = ".next";
    public const string PreviousSuffix = ".previous";
    public const string RotationManifest = "channel-security-rotation.json";
    public const string AuthorityKey = "channel-ca.key";
    public static readonly TimeSpan MaximumRotationOverlap = TimeSpan.FromHours(24);

    public static void Provision(ChannelSecurityMaterialDirectories directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ValidateDistinctRoots(directories);
        Directory.CreateDirectory(directories.GatewayPrivateRoot);
        Directory.CreateDirectory(directories.GatewayTrustRoot);
        Directory.CreateDirectory(directories.ChannelPrivateRoot);
        Directory.CreateDirectory(directories.ChannelTrustRoot);
        RestrictDirectory(directories.GatewayPrivateRoot);
        RestrictDirectory(directories.ChannelPrivateRoot);
        if (!string.IsNullOrWhiteSpace(directories.AuthorityRoot))
        {
            Directory.CreateDirectory(directories.AuthorityRoot);
            RestrictDirectory(directories.AuthorityRoot);
        }

        string staging = Path.Combine(Path.GetTempPath(), "kautuka-channel-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        RestrictDirectory(staging);
        try
        {
            var (server, ca) = LocalCaMtlsBootstrap.EnsureAndLoad(staging, "Channel", ".gateway-client.pfx");
            using (server)
            using (ca)
            using (X509Certificate2 delegation = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
                staging, "Channel", ".gateway-delegation.pfx", ".gateway-delegation.cer"))
            using (X509Certificate2 client = X509CertificateLoader.LoadPkcs12FromFile(
                Path.Combine(staging, ".gateway-client.pfx"), null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet))
            {
                WritePrivate(Path.Combine(directories.ChannelPrivateRoot, ServerPfx), server.Export(X509ContentType.Pkcs12));
                WritePrivate(Path.Combine(directories.GatewayPrivateRoot, GatewayClientPfx),
                    File.ReadAllBytes(Path.Combine(staging, ".gateway-client.pfx")));
                WritePrivate(Path.Combine(directories.GatewayPrivateRoot, GatewayDelegationPfx),
                    File.ReadAllBytes(Path.Combine(staging, ".gateway-delegation.pfx")));
                WritePublic(Path.Combine(directories.GatewayTrustRoot, CaCertificate), ca.Export(X509ContentType.Cert));
                WritePublic(Path.Combine(directories.ChannelTrustRoot, CaCertificate), ca.Export(X509ContentType.Cert));
                WritePublic(Path.Combine(directories.ChannelTrustRoot, GatewayClientCertificate), client.Export(X509ContentType.Cert));
                WritePublic(Path.Combine(directories.ChannelTrustRoot, GatewayDelegationCertificate), delegation.Export(X509ContentType.Cert));
                if (!string.IsNullOrWhiteSpace(directories.AuthorityRoot))
                {
                    WritePrivate(Path.Combine(directories.AuthorityRoot, AuthorityKey), File.ReadAllBytes(Path.Combine(staging, ".ca.key")));
                    WritePublic(Path.Combine(directories.AuthorityRoot, CaCertificate), ca.Export(X509ContentType.Cert));
                }
            }
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }

        ValidateRuntimeInventories(directories);
        ValidateAuthorityInventory(directories);
    }

    public static void ValidateRuntimeInventories(ChannelSecurityMaterialDirectories directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ValidateDistinctRoots(directories);
        RequirePrivateCertificate(Path.Combine(directories.ChannelPrivateRoot, ServerPfx));
        RequirePrivateCertificate(Path.Combine(directories.GatewayPrivateRoot, GatewayClientPfx));
        RequirePrivateCertificate(Path.Combine(directories.GatewayPrivateRoot, GatewayDelegationPfx));
        RequirePublicOnly(Path.Combine(directories.GatewayTrustRoot, CaCertificate));
        RequirePublicOnly(Path.Combine(directories.ChannelTrustRoot, CaCertificate));
        RequirePublicOnly(Path.Combine(directories.ChannelTrustRoot, GatewayClientCertificate));
        RequirePublicOnly(Path.Combine(directories.ChannelTrustRoot, GatewayDelegationCertificate));

        if (Directory.EnumerateFiles(directories.ChannelPrivateRoot).Any(path =>
                Path.GetFileName(path).Contains("gateway", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("channel_runtime_contains_gateway_private_material");
        if (Directory.EnumerateFiles(directories.GatewayPrivateRoot).Any(path =>
                Path.GetFileName(path).Contains("server", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Contains("ca.key", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("gateway_runtime_contains_channel_or_ca_private_material");
        if (Directory.EnumerateFiles(directories.ChannelTrustRoot).Concat(Directory.EnumerateFiles(directories.GatewayTrustRoot))
            .Any(static path => !IsAllowedPublicTrustFile(path)))
        {
            if (Directory.EnumerateFiles(directories.ChannelTrustRoot).Concat(Directory.EnumerateFiles(directories.GatewayTrustRoot))
                .Any(static path => !IsAllowedPublicTrustFile(path)))
                throw new InvalidOperationException("runtime_trust_inventory_contains_private_material");
        }
        ValidateGatewayRuntimeInventory(directories.GatewayPrivateRoot, directories.GatewayTrustRoot);
        ValidateChannelRuntimeInventory(directories.ChannelPrivateRoot, directories.ChannelTrustRoot);
        ValidateCrossRootSlot(directories, NextSuffix);
        ValidateCrossRootSlot(directories, PreviousSuffix);
    }

    public static void ValidateGatewayRuntimeInventory(string privateRoot, string trustRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustRoot);
        RequireDistinct(privateRoot, trustRoot);
        RequirePrivateDirectoryPermissions(privateRoot);
        RequirePrivateCertificate(Path.Combine(privateRoot, GatewayClientPfx));
        RequirePrivateCertificate(Path.Combine(privateRoot, GatewayDelegationPfx));
        RequirePrivateFilePermissions(Path.Combine(privateRoot, GatewayClientPfx));
        RequirePrivateFilePermissions(Path.Combine(privateRoot, GatewayDelegationPfx));
        RequirePublicOnly(Path.Combine(trustRoot, CaCertificate));
        ValidateOptionalPair(privateRoot, GatewayClientPfx + NextSuffix, GatewayDelegationPfx + NextSuffix, requirePrivate: true);
        ValidateOptionalPair(privateRoot, GatewayClientPfx + PreviousSuffix, GatewayDelegationPfx + PreviousSuffix, requirePrivate: true);
        RejectUnexpectedFiles(privateRoot,
            GatewayClientPfx, GatewayDelegationPfx,
            GatewayClientPfx + NextSuffix, GatewayDelegationPfx + NextSuffix,
            GatewayClientPfx + PreviousSuffix, GatewayDelegationPfx + PreviousSuffix);
        RejectUnexpectedFiles(trustRoot, CaCertificate);
    }

    public static void ValidateChannelRuntimeInventory(string privateRoot, string trustRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustRoot);
        RequireDistinct(privateRoot, trustRoot);
        RequirePrivateDirectoryPermissions(privateRoot);
        RequirePrivateCertificate(Path.Combine(privateRoot, ServerPfx));
        RequirePrivateFilePermissions(Path.Combine(privateRoot, ServerPfx));
        RequirePublicOnly(Path.Combine(trustRoot, CaCertificate));
        RequirePublicOnly(Path.Combine(trustRoot, GatewayClientCertificate));
        RequirePublicOnly(Path.Combine(trustRoot, GatewayDelegationCertificate));
        ValidateOptionalPair(trustRoot, GatewayClientCertificate + NextSuffix, GatewayDelegationCertificate + NextSuffix, requirePrivate: false);
        ValidateOptionalPair(trustRoot, GatewayClientCertificate + PreviousSuffix, GatewayDelegationCertificate + PreviousSuffix, requirePrivate: false);
        RejectUnexpectedFiles(privateRoot, ServerPfx);
        RejectUnexpectedFiles(trustRoot,
            CaCertificate, GatewayClientCertificate, GatewayDelegationCertificate,
            GatewayClientCertificate + NextSuffix, GatewayDelegationCertificate + NextSuffix,
            GatewayClientCertificate + PreviousSuffix, GatewayDelegationCertificate + PreviousSuffix,
            RotationManifest);
    }

    public static void StageRotation(
        ChannelSecurityMaterialDirectories directories,
        string nextClientPfx,
        string nextDelegationPfx)
    {
        ValidateRuntimeInventories(directories);
        if (File.Exists(Path.Combine(directories.GatewayPrivateRoot, GatewayClientPfx + NextSuffix))
            || File.Exists(Path.Combine(directories.GatewayPrivateRoot, GatewayClientPfx + PreviousSuffix)))
            throw new InvalidOperationException("channel_security_rotation_not_clean");
        using X509Certificate2 ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directories.ChannelTrustRoot, CaCertificate));
        using X509Certificate2 client = X509CertificateLoader.LoadPkcs12FromFile(nextClientPfx, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        using X509Certificate2 delegation = X509CertificateLoader.LoadPkcs12FromFile(nextDelegationPfx, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        RequireRotationCertificate(client, ca, "Kautuka Gateway Client", LocalCaMtlsBootstrap.GatewayChannelWorkloadIdentity);
        RequireRotationCertificate(delegation, ca, "Kautuka Gateway Channel Delegation", new Uri("spiffe://kautuka.local/delegation/gateway/channel"));
        EnsureDistinctFromCurrent(directories, client, delegation);
        WritePrivateAtomic(Path.Combine(directories.GatewayPrivateRoot, GatewayClientPfx + NextSuffix), File.ReadAllBytes(nextClientPfx));
        WritePrivateAtomic(Path.Combine(directories.GatewayPrivateRoot, GatewayDelegationPfx + NextSuffix), File.ReadAllBytes(nextDelegationPfx));
        WritePublicAtomic(Path.Combine(directories.ChannelTrustRoot, GatewayClientCertificate + NextSuffix), client.Export(X509ContentType.Cert));
        WritePublicAtomic(Path.Combine(directories.ChannelTrustRoot, GatewayDelegationCertificate + NextSuffix), delegation.Export(X509ContentType.Cert));
        ValidateRuntimeInventories(directories);
    }

    public static void StageGeneratedRotation(ChannelSecurityMaterialDirectories directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        if (string.IsNullOrWhiteSpace(directories.AuthorityRoot))
            throw new InvalidOperationException("channel_security_rotation_authority_required");
        ValidateAuthorityInventory(directories);
        string authorityCa = Path.Combine(directories.AuthorityRoot, ".ca.cer");
        string authorityKey = Path.Combine(directories.AuthorityRoot, ".ca.key");
        File.Copy(Path.Combine(directories.AuthorityRoot, CaCertificate), authorityCa, overwrite: true);
        File.Copy(Path.Combine(directories.AuthorityRoot, AuthorityKey), authorityKey, overwrite: true);
        string client = Path.Combine(directories.AuthorityRoot, ".rotation-client.pfx");
        string delegation = Path.Combine(directories.AuthorityRoot, ".rotation-delegation.pfx");
        string delegationPublic = Path.Combine(directories.AuthorityRoot, ".rotation-delegation.cer");
        try
        {
            using (LocalCaMtlsBootstrap.RenewGatewayClientCertificate(directories.AuthorityRoot, "Channel", ".rotation-client.pfx")) { }
            using (LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(directories.AuthorityRoot, "Channel", ".rotation-delegation.pfx", ".rotation-delegation.cer")) { }
            StageRotation(directories, client, delegation);
        }
        finally
        {
            File.Delete(client);
            File.Delete(delegation);
            File.Delete(delegationPublic);
            File.Delete(authorityCa);
            File.Delete(authorityKey);
        }
    }

    public static ChannelSecurityRotationState ActivateStagedRotation(
        ChannelSecurityMaterialDirectories directories,
        DateTimeOffset now,
        TimeSpan overlap)
    {
        if (overlap <= TimeSpan.Zero || overlap > MaximumRotationOverlap)
            throw new InvalidOperationException("channel_security_rotation_overlap_invalid");
        ValidateRuntimeInventories(directories);
        RequireCompleteSlot(directories, NextSuffix);
        PromotePair(directories.GatewayPrivateRoot, GatewayClientPfx, GatewayDelegationPfx);
        try
        {
            PromotePair(directories.ChannelTrustRoot, GatewayClientCertificate, GatewayDelegationCertificate);
        }
        catch
        {
            RollbackPromotedPair(directories.GatewayPrivateRoot, GatewayClientPfx, GatewayDelegationPfx);
            throw;
        }
        ChannelSecurityRotationState state = ReadCurrentState(directories, now.Add(overlap));
        WriteManifestAtomic(Path.Combine(directories.ChannelTrustRoot, RotationManifest), state);
        ValidateRuntimeInventories(directories);
        return state;
    }

    public static void RollbackRotation(ChannelSecurityMaterialDirectories directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        RequireCompleteSlot(directories, PreviousSuffix);
        RollbackPromotedPair(directories.GatewayPrivateRoot, GatewayClientPfx, GatewayDelegationPfx);
        RollbackPromotedPair(directories.ChannelTrustRoot, GatewayClientCertificate, GatewayDelegationCertificate);
        File.Delete(Path.Combine(directories.ChannelTrustRoot, RotationManifest));
        ValidateRuntimeInventories(directories);
    }

    public static void FinalizeRotation(ChannelSecurityMaterialDirectories directories, DateTimeOffset now, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ChannelSecurityRotationState? state = ReadRotationState(directories.ChannelTrustRoot);
        if (state is null) return;
        if (!force && now < state.PreviousAcceptedUntilUtc)
            throw new InvalidOperationException("channel_security_rotation_overlap_active");
        DeleteSlot(directories, PreviousSuffix);
        File.Delete(Path.Combine(directories.ChannelTrustRoot, RotationManifest));
        ValidateRuntimeInventories(directories);
    }

    public static ChannelSecurityRotationState? ReadRotationState(string channelTrustRoot)
    {
        string path = Path.Combine(channelTrustRoot, RotationManifest);
        if (!File.Exists(path)) return null;
        Dictionary<string, string> values = File.ReadAllLines(path)
            .Select(static line => line.Split('=', 2))
            .Where(static parts => parts.Length == 2)
            .ToDictionary(static parts => parts[0], static parts => parts[1], StringComparer.Ordinal);
        if (!values.TryGetValue("version", out string? version) || version != "1"
            || !values.TryGetValue("phase", out string? phase)
            || !values.TryGetValue("previousAcceptedUntilUtc", out string? until)
            || !DateTimeOffset.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset acceptedUntil)
            || !values.TryGetValue("currentClient", out string? currentClient)
            || !values.TryGetValue("previousClient", out string? previousClient)
            || !values.TryGetValue("currentDelegation", out string? currentDelegation)
            || !values.TryGetValue("previousDelegation", out string? previousDelegation))
            throw new InvalidOperationException("channel_security_rotation_manifest_invalid");
        return new ChannelSecurityRotationState(1, phase, acceptedUntil, currentClient, previousClient, currentDelegation, previousDelegation);
    }

    private static void ValidateDistinctRoots(ChannelSecurityMaterialDirectories directories)
    {
        var roots = new List<string>
        {
            Path.GetFullPath(directories.GatewayPrivateRoot).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(directories.GatewayTrustRoot).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(directories.ChannelPrivateRoot).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(directories.ChannelTrustRoot).TrimEnd(Path.DirectorySeparatorChar),
        };
        if (!string.IsNullOrWhiteSpace(directories.AuthorityRoot))
            roots.Add(Path.GetFullPath(directories.AuthorityRoot).TrimEnd(Path.DirectorySeparatorChar));
        if (roots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != roots.Count)
            throw new InvalidOperationException("channel_security_material_roots_must_be_distinct");
    }

    private static void ValidateAuthorityInventory(ChannelSecurityMaterialDirectories directories)
    {
        if (string.IsNullOrWhiteSpace(directories.AuthorityRoot)) return;
        RequirePrivateDirectoryPermissions(directories.AuthorityRoot);
        string keyPath = Path.Combine(directories.AuthorityRoot, AuthorityKey);
        if (!File.Exists(keyPath) || new FileInfo(keyPath).Length == 0)
            throw new InvalidOperationException("channel_security_rotation_authority_missing");
        RequirePrivateFilePermissions(keyPath);
        RequirePublicOnly(Path.Combine(directories.AuthorityRoot, CaCertificate));
        RejectUnexpectedFiles(directories.AuthorityRoot, AuthorityKey, CaCertificate);
    }

    private static void RequireDistinct(string first, string second)
    {
        if (string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("channel_security_material_roots_must_be_distinct");
    }

    private static void RejectUnexpectedFiles(string root, params string[] allowed)
    {
        if (!Directory.Exists(root)) throw new InvalidOperationException("channel_security_material_root_missing");
        if (Directory.EnumerateFiles(root).Any(path => !allowed.Contains(Path.GetFileName(path))))
            throw new InvalidOperationException("channel_security_material_inventory_contains_unexpected_file");
    }

    private static void ValidateOptionalPair(string root, string first, string second, bool requirePrivate)
    {
        bool firstExists = File.Exists(Path.Combine(root, first));
        bool secondExists = File.Exists(Path.Combine(root, second));
        if (firstExists != secondExists) throw new InvalidOperationException("channel_security_rotation_slot_incomplete");
        if (!firstExists) return;
        if (requirePrivate)
        {
            RequirePrivateCertificate(Path.Combine(root, first));
            RequirePrivateCertificate(Path.Combine(root, second));
            RequirePrivateFilePermissions(Path.Combine(root, first));
            RequirePrivateFilePermissions(Path.Combine(root, second));
        }
        else
        {
            RequirePublicOnly(Path.Combine(root, first));
            RequirePublicOnly(Path.Combine(root, second));
        }
    }

    private static bool IsAllowedPublicTrustFile(string path)
    {
        string name = Path.GetFileName(path);
        return string.Equals(Path.GetExtension(name), ".cer", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".cer" + NextSuffix, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".cer" + PreviousSuffix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, RotationManifest, StringComparison.Ordinal);
    }

    private static void ValidateCrossRootSlot(ChannelSecurityMaterialDirectories directories, string suffix)
    {
        bool privateSlot = File.Exists(Path.Combine(directories.GatewayPrivateRoot, GatewayClientPfx + suffix));
        bool trustSlot = File.Exists(Path.Combine(directories.ChannelTrustRoot, GatewayClientCertificate + suffix));
        if (privateSlot != trustSlot) throw new InvalidOperationException("channel_security_rotation_cross_root_slot_incomplete");
        if (!privateSlot) return;
        using X509Certificate2 clientPrivate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(directories.GatewayPrivateRoot, GatewayClientPfx + suffix), null);
        using X509Certificate2 clientPublic = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directories.ChannelTrustRoot, GatewayClientCertificate + suffix));
        using X509Certificate2 delegationPrivate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(directories.GatewayPrivateRoot, GatewayDelegationPfx + suffix), null);
        using X509Certificate2 delegationPublic = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directories.ChannelTrustRoot, GatewayDelegationCertificate + suffix));
        if (!string.Equals(clientPrivate.Thumbprint, clientPublic.Thumbprint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(delegationPrivate.Thumbprint, delegationPublic.Thumbprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("channel_security_rotation_public_private_mismatch");
    }

    private static void RequireRotationCertificate(X509Certificate2 certificate, X509Certificate2 ca, string subject, Uri identity)
    {
        if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(1)
            || !LocalCaMtlsBootstrap.ValidateClientCert(certificate, ca)
            || !string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, false), subject, StringComparison.Ordinal)
            || !certificate.Extensions.OfType<X509Extension>().Any(extension =>
                extension.Oid?.Value == "2.5.29.17" && extension.Format(false).Contains(identity.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("channel_security_rotation_certificate_invalid");
    }

    private static void EnsureDistinctFromCurrent(ChannelSecurityMaterialDirectories directories, X509Certificate2 client, X509Certificate2 delegation)
    {
        using X509Certificate2 currentClient = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(directories.GatewayPrivateRoot, GatewayClientPfx), null);
        using X509Certificate2 currentDelegation = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(directories.GatewayPrivateRoot, GatewayDelegationPfx), null);
        if (string.Equals(currentClient.Thumbprint, client.Thumbprint, StringComparison.OrdinalIgnoreCase)
            || string.Equals(currentDelegation.Thumbprint, delegation.Thumbprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("channel_security_rotation_identity_unchanged");
    }

    private static void RequireCompleteSlot(ChannelSecurityMaterialDirectories directories, string suffix)
    {
        ValidateRuntimeInventories(directories);
        if (!File.Exists(Path.Combine(directories.GatewayPrivateRoot, GatewayClientPfx + suffix))
            || !File.Exists(Path.Combine(directories.ChannelTrustRoot, GatewayClientCertificate + suffix)))
            throw new InvalidOperationException("channel_security_rotation_slot_missing");
    }

    private static void PromotePair(string root, string first, string second)
    {
        PromoteFile(Path.Combine(root, first));
        try { PromoteFile(Path.Combine(root, second)); }
        catch { RollbackPromotedFile(Path.Combine(root, first)); throw; }
    }

    private static void PromoteFile(string current)
    {
        File.Move(current, current + PreviousSuffix, overwrite: false);
        try { File.Move(current + NextSuffix, current, overwrite: false); }
        catch { File.Move(current + PreviousSuffix, current); throw; }
    }

    private static void RollbackPromotedPair(string root, string first, string second)
    {
        RollbackPromotedFile(Path.Combine(root, second));
        RollbackPromotedFile(Path.Combine(root, first));
    }

    private static void RollbackPromotedFile(string current)
    {
        if (!File.Exists(current + PreviousSuffix)) return;
        if (File.Exists(current)) File.Move(current, current + NextSuffix, overwrite: true);
        File.Move(current + PreviousSuffix, current, overwrite: true);
    }

    private static ChannelSecurityRotationState ReadCurrentState(ChannelSecurityMaterialDirectories directories, DateTimeOffset acceptedUntil)
    {
        using X509Certificate2 client = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directories.ChannelTrustRoot, GatewayClientCertificate));
        using X509Certificate2 delegation = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directories.ChannelTrustRoot, GatewayDelegationCertificate));
        using X509Certificate2 previousClient = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directories.ChannelTrustRoot, GatewayClientCertificate + PreviousSuffix));
        using X509Certificate2 previousDelegation = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directories.ChannelTrustRoot, GatewayDelegationCertificate + PreviousSuffix));
        return new ChannelSecurityRotationState(1, "ACTIVE", acceptedUntil,
            client.Thumbprint, previousClient.Thumbprint, delegation.Thumbprint, previousDelegation.Thumbprint);
    }

    private static void DeleteSlot(ChannelSecurityMaterialDirectories directories, string suffix)
    {
        foreach (string path in new[]
        {
            Path.Combine(directories.GatewayPrivateRoot, GatewayClientPfx + suffix),
            Path.Combine(directories.GatewayPrivateRoot, GatewayDelegationPfx + suffix),
            Path.Combine(directories.ChannelTrustRoot, GatewayClientCertificate + suffix),
            Path.Combine(directories.ChannelTrustRoot, GatewayDelegationCertificate + suffix),
        }) File.Delete(path);
    }

    private static void WriteManifestAtomic(string path, ChannelSecurityRotationState state)
        => WritePublicAtomic(path, Encoding.UTF8.GetBytes(string.Join('\n',
            "version=1",
            "phase=" + state.Phase,
            "previousAcceptedUntilUtc=" + state.PreviousAcceptedUntilUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            "currentClient=" + state.CurrentClientThumbprint,
            "previousClient=" + state.PreviousClientThumbprint,
            "currentDelegation=" + state.CurrentDelegationThumbprint,
            "previousDelegation=" + state.PreviousDelegationThumbprint)));

    private static void WritePrivateAtomic(string path, byte[] value)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WritePrivate(temporary, value);
            File.Move(temporary, path, overwrite: false);
        }
        finally { File.Delete(temporary); }
    }

    private static void WritePublicAtomic(string path, byte[] value)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WritePublic(temporary, value);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static void RequirePrivateDirectoryPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictDirectory(path);
            return;
        }
        UnixFileMode mode = File.GetUnixFileMode(path);
        UnixFileMode forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & forbidden) != 0) throw new InvalidOperationException("channel_private_material_directory_permissions_too_broad");
    }

    private static void RequirePrivateFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictPrivateFile(path);
            return;
        }
        UnixFileMode mode = File.GetUnixFileMode(path);
        UnixFileMode forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & forbidden) != 0) throw new InvalidOperationException("channel_private_material_file_permissions_too_broad");
    }

    private static void RequirePrivateCertificate(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("channel_private_security_material_missing");
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null);
        if (!certificate.HasPrivateKey) throw new InvalidOperationException("channel_private_security_material_has_no_key");
    }

    private static void RequirePublicOnly(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("channel_public_security_material_missing");
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificateFromFile(path);
        if (certificate.HasPrivateKey) throw new InvalidOperationException("channel_public_security_material_contains_private_key");
    }

    private static void WritePrivate(string path, byte[] value)
    {
        File.WriteAllBytes(path, value);
        RestrictPrivateFile(path);
    }

    private static void WritePublic(string path, byte[] value) => File.WriteAllBytes(path, value);

    private static void RestrictPrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.Read | FileSystemRights.Write,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.Modify | FileSystemRights.ReadAndExecute | FileSystemRights.DeleteSubdirectoriesAndFiles,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

public sealed record ChannelSecurityMaterialDirectories(
    string GatewayPrivateRoot,
    string GatewayTrustRoot,
    string ChannelPrivateRoot,
    string ChannelTrustRoot,
    string? AuthorityRoot = null);

public sealed record ChannelSecurityRotationState(
    int Version,
    string Phase,
    DateTimeOffset PreviousAcceptedUntilUtc,
    string CurrentClientThumbprint,
    string PreviousClientThumbprint,
    string CurrentDelegationThumbprint,
    string PreviousDelegationThumbprint);

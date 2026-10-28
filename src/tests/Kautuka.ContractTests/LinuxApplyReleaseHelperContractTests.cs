namespace Kautuka.ContractTests;

public sealed class LinuxApplyReleaseHelperContractTests
{
    [Fact]
    public void ApplyReleaseHelperDoesNotRestartUpdateService()
    {
        string projectRoot = FindProjectRoot();
        string helperPath = Path.Combine(projectRoot, "scripts", "kautuka-apply-release.sh");

        Assert.True(File.Exists(helperPath), $"Expected apply-release helper to exist: {helperPath}");
        string source = File.ReadAllText(helperPath);

        Assert.Contains(
            "system_units=(kautuka-credentialshost.service kautuka-channelhost.service kautuka-gateway.service)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("systemctl stop kautuka-gateway.service", source, StringComparison.Ordinal);
        Assert.Contains("systemctl stop kautuka-channelhost.service", source, StringComparison.Ordinal);
        Assert.Contains("systemctl stop kautuka-credentialshost.service", source, StringComparison.Ordinal);
        Assert.Contains(
            "user_units=(kautuka-manager.service kautuka-webhost.service)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "user_units=(kautuka-manager.service kautuka-updateservice.service kautuka-webhost.service)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("KAUTUKA_APPLY_DETACHED", source, StringComparison.Ordinal);
        Assert.Contains("systemd-run", source, StringComparison.Ordinal);
        Assert.Contains("artifact must be under /var/lib/kautuka/updateService/updates/", source, StringComparison.Ordinal);
        Assert.Contains("current.next", source, StringComparison.Ordinal);
        Assert.Contains("activation failed; restoring current pointers", source, StringComparison.Ordinal);
        Assert.Contains("activation failed after revert", source, StringComparison.Ordinal);
        Assert.Contains("current pointer mismatch", source, StringComparison.Ordinal);
        Assert.Contains("current_manager_version", source, StringComparison.Ordinal);
        Assert.Contains("kautuka-apply.rollback", source, StringComparison.Ordinal);
        Assert.Contains("heal_incomplete_apply_if_needed", source, StringComparison.Ordinal);
        Assert.Contains("trap '' HUP TERM INT", source, StringComparison.Ordinal);
        Assert.Contains("service_passwd=\"$(getent passwd \"$service_user\")\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("| awk -F: '{ print $6 }'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsInstallerPageWarnsThatItIsUnsigned()
    {
        string projectRoot = FindProjectRoot();
        string pagePath = Path.Combine(projectRoot, "src", "installer", "Kautuka.Installer.Windows", "wwwroot", "index.html");

        Assert.True(File.Exists(pagePath), $"Expected Windows installer page to exist: {pagePath}");
        string source = File.ReadAllText(pagePath);

        Assert.Contains("This Windows installer is not Authenticode-signed.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows residency after reboot is experimental.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplianceBakeUsesTheHostUnionAndNumericOwnership()
    {
        string projectRoot = FindProjectRoot();
        string bakePath = Path.Combine(projectRoot, "scripts", "build-release-assets.sh");

        Assert.True(File.Exists(bakePath), $"Expected bake script to exist: {bakePath}");
        string source = File.ReadAllText(bakePath);

        Assert.Contains("\"schemaVersion\": 2,", source, StringComparison.Ordinal);
        Assert.Contains("install_system_channel_service gateway", source, StringComparison.Ordinal);
        Assert.Contains("stage_oci_image_archives", source, StringComparison.Ordinal);
        Assert.Contains("runtime-images.json", source, StringComparison.Ordinal);
        Assert.Contains("visible_root/oci-images", source, StringComparison.Ordinal);
        Assert.Contains("install_system_channel_service channelhost", source, StringComparison.Ordinal);
        Assert.Contains("rootfs_uid kautuka-credentials", source, StringComparison.Ordinal);
        Assert.Contains("kautuka ALL=(root) NOPASSWD: /usr/local/lib/kautuka/apply-release", source, StringComparison.Ordinal);
        Assert.Contains("rootfs_gid kautuka-channelhost", source, StringComparison.Ordinal);
        Assert.Contains("rootfs_gid kautuka-gateway", source, StringComparison.Ordinal);
        Assert.Contains("chown_tree_excluding \"$visible_root\" \"$kautuka_uid:$kautuka_gid\" \"$channel_security_root\"", source, StringComparison.Ordinal);
        Assert.Contains("-o -exec chown -h \"$owner\" {} +", source, StringComparison.Ordinal);
        Assert.Contains("local containers_root=\"$root/.local/share/containers\"", source, StringComparison.Ordinal);
        Assert.Contains("local credentials_state_root=\"$root/credentialsHost\"", source, StringComparison.Ordinal);
        Assert.Contains("\"$rootfs_dir/var/lib/kautuka/credentialsHost/vault\"", source, StringComparison.Ordinal);
        Assert.Contains("sudo chown -R \"0:$channelhost_gid\"", source, StringComparison.Ordinal);
        Assert.Contains("sudo chown -R \"0:$gateway_gid\"", source, StringComparison.Ordinal);
        Assert.Contains("sudo chown \"0:$kautuka_gid\" \"$rootfs_dir/var/lib/kautuka\" \"$rootfs_dir/var/lib/kautuka/logs\"", source, StringComparison.Ordinal);
        Assert.Contains("sudo chmod 0771 \"$rootfs_dir/var/lib/kautuka\" \"$rootfs_dir/var/lib/kautuka/logs\"", source, StringComparison.Ordinal);
        Assert.Contains("sudo mkdir -p \"$rootfs_dir/var/lib/kautuka/mcpb-bundles\"", source, StringComparison.Ordinal);
        Assert.Contains("sudo chown -R \"$gateway_uid:$kautuka_gid\" \"$rootfs_dir/var/lib/kautuka/mcpb-bundles\"", source, StringComparison.Ordinal);
        Assert.Contains("sudo chmod 2750 \"$rootfs_dir/var/lib/kautuka/mcpb-bundles\"", source, StringComparison.Ordinal);
        Assert.Contains("harden_rootfs_sudo_bits \"$rootfs_dir\"", source, StringComparison.Ordinal);
        Assert.Contains("require_rootfs_owner_mode \"$channel_security_root/channel-private\"", source, StringComparison.Ordinal);
        Assert.Contains("appliance channel-private owner must be", source, StringComparison.Ordinal);
        Assert.Contains("if [[ \"$service_key\" != 'updateservice' ]]; then", source, StringComparison.Ordinal);
        Assert.Contains("Prefer the repo helper over any helper embedded in the platform tarball", source, StringComparison.Ordinal);
        Assert.Contains("source_root/scripts/kautuka-apply-release.sh", source, StringComparison.Ordinal);
        Assert.DoesNotContain("install_host_service gateway", source, StringComparison.Ordinal);
        Assert.DoesNotContain("install_host_service channelhost", source, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo chown -R \"$credential_user:$credential_user\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo chown -R \"$kautuka_uid:$kautuka_gid\" \"$install_root\" \"$visible_root\" \"$internal_state_root\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"scope\": \"user\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WslBaseRootfsCreatesKautukaAsASystemAccount()
    {
        string projectRoot = FindProjectRoot();
        string rootfsPath = Path.Combine(projectRoot, "scripts", "build-wsl-rootfs.sh");
        Assert.True(File.Exists(rootfsPath), $"Expected WSL rootfs builder to exist: {rootfsPath}");
        string source = File.ReadAllText(rootfsPath);
        Assert.Contains(
            "useradd --system --user-group --create-home --home-dir /var/lib/kautuka --shell /usr/sbin/nologin kautuka",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "useradd --user-group --create-home --home-dir /var/lib/kautuka --shell /usr/sbin/nologin kautuka",
            source,
            StringComparison.Ordinal);
        Assert.Contains("for sudo_bin in /usr/bin/sudo /usr/sbin/sudo; do", source, StringComparison.Ordinal);
        Assert.Contains("  curl \\\n  python3\n", source, StringComparison.Ordinal);
        Assert.Contains("tar --numeric-owner --xattrs --acls -czf /work/out/rootfs.tar.gz -C \"$rootfs_dir\" .", source, StringComparison.Ordinal);
        Assert.Contains("base rootfs sudo owner must be 0/0", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "chown root:root /etc/sudo.conf /usr/sbin/sudo /usr/bin/newuidmap /usr/bin/newgidmap || true",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseManifestGeneratorEmbedsRuntimeImages()
    {
        string projectRoot = FindProjectRoot();
        string generatorPath = Path.Combine(projectRoot, "scripts", "generate-release-manifest.mjs");
        Assert.True(File.Exists(generatorPath), $"Expected release manifest generator to exist: {generatorPath}");
        string source = File.ReadAllText(generatorPath);
        Assert.Contains("loadRuntimeImages", source, StringComparison.Ordinal);
        Assert.Contains("runtimeImages", source, StringComparison.Ordinal);
        Assert.Contains("runtime-images", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxInstallerGeneratorStripsCarriageReturns()
    {
        string projectRoot = FindProjectRoot();
        string generatorPath = Path.Combine(projectRoot, "scripts", "generate-linux-installer.sh");
        Assert.True(File.Exists(generatorPath), $"Expected installer generator to exist: {generatorPath}");
        string source = File.ReadAllText(generatorPath);
        Assert.Contains("| tr -d '\\r' > \"$output_path\"", source, StringComparison.Ordinal);
        Assert.Contains("--preview-manifest-url", source, StringComparison.Ordinal);
        Assert.Contains("--stable-manifest-url", source, StringComparison.Ordinal);
        Assert.Contains("KAUTUKA_DEFAULT_PREVIEW_MANIFEST_URL", source, StringComparison.Ordinal);
        Assert.Contains("KAUTUKA_DEFAULT_STABLE_MANIFEST_URL", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubReleaseLanePublishesVersionedAssetsAndSeparateChannelPointers()
    {
        string projectRoot = FindProjectRoot();
        string buildWorkflow = File.ReadAllText(Path.Combine(projectRoot, ".github", "workflows", "release-build-platform-assets.yml"));
        string publishWorkflow = File.ReadAllText(Path.Combine(projectRoot, ".github", "workflows", "release-publish-assets.yml"));
        string pipeline = File.ReadAllText(Path.Combine(projectRoot, ".github", "workflows", "release-pipeline.yml"));
        string publishScript = File.ReadAllText(Path.Combine(projectRoot, "scripts", "publish-release-feed.sh"));

        Assert.Contains("https://github.com/devkrity/kautuka", pipeline, StringComparison.Ordinal);
        Assert.Contains("https://kautuka.store/catalog/manifest", pipeline, StringComparison.Ordinal);
        Assert.Contains("--manifest-url \"$channel_manifest_url\"", buildWorkflow, StringComparison.Ordinal);
        Assert.Contains("https://raw.githubusercontent.com/devkrity/kautuka-release-feed-preview/main/manifest", buildWorkflow, StringComparison.Ordinal);
        Assert.Contains("https://raw.githubusercontent.com/devkrity/kautuka-release-feed-stable/main/manifest", buildWorkflow, StringComparison.Ordinal);
        Assert.Contains("scripts/publish-release-feed.sh", publishWorkflow, StringComparison.Ordinal);
        Assert.Contains("gh release upload", publishWorkflow, StringComparison.Ordinal);
        Assert.Contains("verify-release-assets.mjs", publishWorkflow, StringComparison.Ordinal);
        Assert.Contains(".immutable", publishWorkflow, StringComparison.Ordinal);
        Assert.Contains("devkrity/kautuka-release-feed-${CHANNEL}", publishWorkflow, StringComparison.Ordinal);
        Assert.Contains("catalog-feed/catalog", publishWorkflow, StringComparison.Ordinal);
        Assert.Contains("releases/download", buildWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--delete", publishScript, StringComparison.Ordinal);
        Assert.Contains("catalog/*", publishScript, StringComparison.Ordinal);
        Assert.Contains("RELEASE_FEED_S3_ENDPOINT", publishScript, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxNetworkInstallerVerifiesSignedManifestAndArchiveBeforePersistentMutation()
    {
        string projectRoot = FindProjectRoot();
        string installer = File.ReadAllText(Path.Combine(projectRoot, "scripts", "kautuka-install-linux.sh.in"));
        string generator = File.ReadAllText(Path.Combine(projectRoot, "scripts", "generate-linux-installer.sh"));
        string verifier = File.ReadAllText(Path.Combine(projectRoot, "scripts", "verify-linux-release.sh"));
        string regression = File.ReadAllText(Path.Combine(projectRoot, "scripts", "test-linux-installer-security.sh"));
        string verifiedRunner = File.ReadAllText(Path.Combine(projectRoot, "scripts", "install-verified-linux.sh"));

        int verificationIndex = installer.IndexOf("verify_linux_release \\", StringComparison.Ordinal);
        int accountMutationIndex = installer.IndexOf("ensure_service_account \"$service_user\"", StringComparison.Ordinal);
        int packageMutationIndex = installer.LastIndexOf("    ensure_dotnet_host_libraries", StringComparison.Ordinal);
        Assert.True(verificationIndex >= 0, "Network install must call the signed release verifier.");
        Assert.True(accountMutationIndex > verificationIndex, "Account creation must follow release verification.");
        Assert.True(packageMutationIndex > verificationIndex, "System package installation must follow release verification.");
        Assert.Contains("cat \"$release_verifier_source\"", generator, StringComparison.Ordinal);
        Assert.Contains("openssl dgst -sha256 -verify", verifier, StringComparison.Ordinal);
        Assert.Contains("sha256sum", verifier, StringComparison.Ordinal);
        Assert.Contains("tampered-manifest", regression, StringComparison.Ordinal);
        Assert.Contains("unsigned-manifest", regression, StringComparison.Ordinal);
        Assert.Contains("wrong-digest", regression, StringComparison.Ordinal);
        Assert.Contains("mismatched-version", regression, StringComparison.Ordinal);
        Assert.Contains("mismatched-channel", regression, StringComparison.Ordinal);
        Assert.Contains("mismatched-tag", regression, StringComparison.Ordinal);
        Assert.Contains("mismatched-artifact-url", regression, StringComparison.Ordinal);
        Assert.Contains("gh attestation verify", verifiedRunner, StringComparison.Ordinal);
        Assert.Contains("exec sudo bash", verifiedRunner, StringComparison.Ordinal);
        Assert.Contains("substituted Linux installer", regression, StringComparison.Ordinal);
    }

    private static string FindProjectRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "scripts", "kautuka-apply-release.sh")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Kautuka repository root.");
    }
}

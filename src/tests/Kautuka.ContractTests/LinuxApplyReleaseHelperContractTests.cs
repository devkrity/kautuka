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
            "user_units=(kautuka-manager.service)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "user_units=(kautuka-manager.service kautuka-updateservice.service kautuka-webhost.service)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("KAUTUKA_APPLY_DETACHED", source, StringComparison.Ordinal);
        Assert.Contains("systemd-run", source, StringComparison.Ordinal);
        Assert.Contains("artifact must be under /var/lib/kautuka/updateService/updates/", source, StringComparison.Ordinal);
        Assert.Contains("# Kautuka-Apply-Protocol: 5", source, StringComparison.Ordinal);
        Assert.Contains("--capabilities", source, StringComparison.Ordinal);
        Assert.Contains("safe-state-recovery-v5", source, StringComparison.Ordinal);
        Assert.Contains("assert_maintenance_webhost", source, StringComparison.Ordinal);
        Assert.Contains("--recover", source, StringComparison.Ordinal);
        Assert.Contains("--boot-recover", source, StringComparison.Ordinal);
        Assert.Contains("'committed', 'recovered'", source, StringComparison.Ordinal);
        Assert.Contains("assert_database_writers_absent", source, StringComparison.Ordinal);
        Assert.Contains("assert_database_writer_lock_ready", source, StringComparison.Ordinal);
        Assert.Contains("flock -x -w 30 8", source, StringComparison.Ordinal);
        Assert.Contains("acquire_database_writer_exclusion", source, StringComparison.Ordinal);
        Assert.Contains("assert_service_executable", source, StringComparison.Ordinal);
        Assert.Contains("ExecStart overrides are unsupported", source, StringComparison.Ordinal);
        Assert.Contains("must launch only the supported Kautuka executable", source, StringComparison.Ordinal);
        Assert.Contains("is unresolved; recover or complete it before starting another update", source, StringComparison.Ordinal);
        Assert.Contains("v?(0|[1-9]", source, StringComparison.Ordinal);
        Assert.Contains("--complete-recovery", source, StringComparison.Ordinal);
        Assert.Contains("python3 \"$recovery_tool\" capture", source, StringComparison.Ordinal);
        Assert.Contains("python3 \"$recovery_tool\" restore", source, StringComparison.Ordinal);
        Assert.Contains("wait_for_manager_desired_state", source, StringComparison.Ordinal);
        Assert.Contains("manager-desired-state.json", source, StringComparison.Ordinal);
        Assert.Contains("ensure_maintenance_fence_from_journal", source, StringComparison.Ordinal);
        Assert.Contains("set_operation_phase committed", source, StringComparison.Ordinal);
        Assert.Contains("committed updates are repaired by rolling forward", source, StringComparison.Ordinal);
        Assert.Contains("last-known-good", source, StringComparison.Ordinal);
        Assert.Contains("target final startup failed; restoring the pre-update state", source, StringComparison.Ordinal);
        Assert.Contains("current pointer mismatch", source, StringComparison.Ordinal);
        Assert.Contains("legacy recovery marker requires administrator review", source, StringComparison.Ordinal);
        Assert.Contains("--unix-socket /run/kautuka/gateway/gateway.sock http://localhost/api/runtime/system-health", source, StringComparison.Ordinal);
        Assert.Contains("completed|aborted)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1:20000/health", source, StringComparison.Ordinal);
        Assert.Contains("ensure_selinux_runtime_exec_policy() {", source, StringComparison.Ordinal);
        Assert.Contains("semanage fcontext -a -t bin_t \"$rule\"", source, StringComparison.Ordinal);
        Assert.Contains("tar --no-same-owner --no-same-permissions --no-overwrite-dir -xzf", source, StringComparison.Ordinal);
        Assert.Contains("check_staging_space \"$artifact_path\"", source, StringComparison.Ordinal);
        Assert.Contains("cleanup_staging", source, StringComparison.Ordinal);
        Assert.Contains("service_passwd=\"$(getent passwd \"$service_user\")\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("| awk -F: '{ print $6 }'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsInstallerPageDoesNotRepeatDownloadWarnings()
    {
        string projectRoot = FindProjectRoot();
        string pagePath = Path.Combine(projectRoot, "src", "installer", "Kautuka.Installer.Windows", "wwwroot", "index.html");

        Assert.True(File.Exists(pagePath), $"Expected Windows installer page to exist: {pagePath}");
        string source = File.ReadAllText(pagePath);

        Assert.DoesNotContain("This Windows installer is not Authenticode-signed.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows residency after reboot is experimental.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxPackagesAndWslRootfsInstallRecoveryBeforeApplicationServices()
    {
        string projectRoot = FindProjectRoot();
        string releaseBuilder = File.ReadAllText(Path.Combine(projectRoot, "scripts", "build-release-assets.sh"));
        string installer = File.ReadAllText(Path.Combine(projectRoot, "scripts", "kautuka-install-linux.sh.in"));
        string recoveryUnit = File.ReadAllText(Path.Combine(projectRoot, "scripts", "kautuka-update-recovery.service"));
        string writerLease = File.ReadAllText(Path.Combine(projectRoot, "src", "shared", "Kautuka.Hosting", "Hosting", "KautukaDatabaseWriterLease.cs"));
        string channelHost = File.ReadAllText(Path.Combine(projectRoot, "src", "backend", "Kautuka.ChannelHost", "Program.cs"));

        Assert.Contains("kautuka-update-recovery.py", releaseBuilder, StringComparison.Ordinal);
        Assert.Contains("kautuka-update-recovery.service", releaseBuilder, StringComparison.Ordinal);
        Assert.Contains("Requires=kautuka-update-recovery.service", releaseBuilder, StringComparison.Ordinal);
        Assert.Contains("f /run/kautuka-db-writer.lock 0644 root root -", installer + releaseBuilder, StringComparison.Ordinal);
        Assert.Contains("exclusive ? LockExclusive : LockShared", writerLease, StringComparison.Ordinal);
        Assert.Contains("AcquireForMode", channelHost, StringComparison.Ordinal);
        Assert.Contains("exclusive: provisioningChannelSecurity", channelHost, StringComparison.Ordinal);
        Assert.Contains("Kautuka__Mode=prod \"$channelhost_executable\" --provision-channel-security", installer, StringComparison.Ordinal);
        Assert.Contains("env -u Kautuka__Mode", releaseBuilder, StringComparison.Ordinal);
        Assert.Contains("Requires=systemd-tmpfiles-setup.service", recoveryUnit + installer, StringComparison.Ordinal);
        Assert.Contains("After=kautuka-update-recovery.service", releaseBuilder, StringComparison.Ordinal);
        Assert.Contains("10-kautuka-recovery.conf", releaseBuilder, StringComparison.Ordinal);
        Assert.Contains("kautuka-update-recovery", installer, StringComparison.Ordinal);
        Assert.Contains("kautuka-update-recovery.service", installer, StringComparison.Ordinal);
        Assert.Contains("safe update recovery support", installer, StringComparison.Ordinal);
        Assert.Contains("systemctl enable kautuka-update-recovery.service", installer, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/usr/local/lib/kautuka/apply-release --boot-recover", installer, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/usr/local/lib/kautuka/apply-release --boot-recover", recoveryUnit, StringComparison.Ordinal);
        Assert.Contains("Environment=Kautuka__Mode=prod", releaseBuilder, StringComparison.Ordinal);
        Assert.Contains("Environment=Kautuka__ContainerStateRoot=/var/lib/kautuka", releaseBuilder, StringComparison.Ordinal);
        Assert.Contains("Environment=Kautuka__Mode=prod", installer, StringComparison.Ordinal);
        Assert.Contains("Environment=Kautuka__ContainerStateRoot=/var/lib/kautuka", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemdBootRecoveryTestUsesRealUnitsWithoutTheDisposableHelperBypass()
    {
        string projectRoot = FindProjectRoot();
        string recoveryTest = File.ReadAllText(Path.Combine(projectRoot, "scripts", "test-apply-release-systemd-reboot.sh"));

        Assert.Contains("prepare|verify1|verify2|prepare-committed|verify-committed", recoveryTest, StringComparison.Ordinal);
        Assert.Contains("systemd-test-no-userbus.sh", recoveryTest, StringComparison.Ordinal);
        Assert.Contains("--complete-recovery --version \"$source_version\"", recoveryTest, StringComparison.Ordinal);
        Assert.Contains("v0.1.20-preview.17", recoveryTest, StringComparison.Ordinal);
        Assert.Contains("--boot-recover", recoveryTest, StringComparison.Ordinal);
        Assert.Contains("Kautuka database writer/runtime process remains before restore", recoveryTest, StringComparison.Ordinal);
        Assert.Contains("--unix-socket /run/kautuka/gateway/gateway.sock http://localhost/health", recoveryTest, StringComparison.Ordinal);
        Assert.Contains("write-after-recovery", recoveryTest, StringComparison.Ordinal);
        Assert.DoesNotContain("KAUTUKA_DISPOSABLE_TEST", recoveryTest, StringComparison.Ordinal);
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
    public void LinuxInstallerGeneratorRetainsChannelAndCatalogOptionsWithoutFeedDefaults()
    {
        string projectRoot = FindProjectRoot();
        string generatorPath = Path.Combine(projectRoot, "scripts", "generate-linux-installer.sh");
        Assert.True(File.Exists(generatorPath), $"Expected installer generator to exist: {generatorPath}");
        string source = File.ReadAllText(generatorPath);
        Assert.Contains("| tr -d '\\r' > \"$output_path\"", source, StringComparison.Ordinal);
        Assert.Contains("--release-channel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("raw.githubusercontent.com/devkrity/kautuka-release-feed", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubReleaseLanePublishesVersionedAssetsWithoutChannelPointers()
    {
        string projectRoot = FindProjectRoot();
        string buildWorkflow = File.ReadAllText(Path.Combine(projectRoot, ".github", "workflows", "release-build-platform-assets.yml"));
        string publishWorkflow = File.ReadAllText(Path.Combine(projectRoot, ".github", "workflows", "release-publish-assets.yml"));
        string pipeline = File.ReadAllText(Path.Combine(projectRoot, ".github", "workflows", "release-pipeline.yml"));

        Assert.DoesNotContain("catalog_manifest_url", pipeline, StringComparison.Ordinal);
        Assert.Contains("https://github.com/${GITHUB_REPOSITORY}/releases/download/${RELEASE_TAG}", buildWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("release-feed-", buildWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--preview-manifest-url", buildWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--stable-manifest-url", buildWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallerReleaseManifestUrl", buildWorkflow, StringComparison.Ordinal);
        Assert.Contains("gh release upload", publishWorkflow, StringComparison.Ordinal);
        Assert.Contains("verify-release-assets.mjs", publishWorkflow, StringComparison.Ordinal);
        Assert.Contains(".immutable", publishWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("release-channel", publishWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("release_feed_deploy_key", publishWorkflow, StringComparison.Ordinal);
        Assert.Contains("releases/download", buildWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("publish-release-feed.sh", publishWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("RELEASE_FEED_S3_", publishWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("S3 catalog", publishWorkflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("manifest-host-bundle", buildWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Stage catalog artifacts", buildWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog-manifest-url", buildWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog_manifest_url", buildWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("catalogManifestUrl", File.ReadAllText(Path.Combine(projectRoot, "scripts", "kautuka-install-linux.sh.in")), StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsBootstrapDiscoversNativeReleaseAndValidatesExactTagAssets()
    {
        string projectRoot = FindProjectRoot();
        string argumentParser = File.ReadAllText(Path.Combine(projectRoot, "src", "installer", "Kautuka.Installer.Windows", "Hosting", "Program.Args.cs"));
        string installer = File.ReadAllText(Path.Combine(projectRoot, "src", "installer", "Kautuka.Installer.Windows", "Installation", "WindowsApplianceInstaller.Release.cs"));

        Assert.DoesNotContain("CreateChannelPointerUrl", argumentParser, StringComparison.Ordinal);
        Assert.Contains("ValidateManifestMatchesChannel", installer, StringComparison.Ordinal);
        Assert.Contains("expectedSignatureUrl", installer, StringComparison.Ordinal);
        Assert.Contains("expectedRootfsUrl", installer, StringComparison.Ordinal);
        Assert.Contains("expectedLinuxUrl", installer, StringComparison.Ordinal);
        Assert.Contains("api.github.com/repos/", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DiscoverLatestReleaseTagAsync", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("release-channel", installer, StringComparison.OrdinalIgnoreCase);
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

        int verificationIndex = installer.IndexOf("verify_linux_release \"${verification_args[@]}\"", StringComparison.Ordinal);
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
        Assert.Contains("mismatched-source-sha", regression, StringComparison.Ordinal);
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

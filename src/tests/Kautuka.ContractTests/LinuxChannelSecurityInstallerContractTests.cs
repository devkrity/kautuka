namespace Kautuka.ContractTests;

public sealed class LinuxChannelSecurityInstallerContractTests
{
    [Fact]
    public void ProductionInstallerPreservesTheChannelProcessAndKeyBoundary()
    {
        string projectRoot = FindProjectRoot();
        string installerPath = Path.Combine(projectRoot, "scripts", "kautuka-install-linux.sh.in");

        Assert.True(File.Exists(installerPath), $"Expected installer template to exist: {installerPath}");
        string source = File.ReadAllText(installerPath);

        Assert.Contains("ensure_isolated_service_account kautuka-gateway kautuka-gateway", source, StringComparison.Ordinal);
        Assert.Contains("ensure_isolated_service_account kautuka-channelhost kautuka-channelhost", source, StringComparison.Ordinal);
        Assert.Contains("ensure_isolated_service_account kautuka-credentials kautuka-credentials", source, StringComparison.Ordinal);
        Assert.Contains("socket directory owner $owner does not exist yet", source, StringComparison.Ordinal);
        Assert.DoesNotContain("systemd-tmpfiles --create \"$tmpfiles_path\" >/dev/null 2>&1 || true", source, StringComparison.Ordinal);

        int credentialsAccount = source.IndexOf("ensure_isolated_service_account kautuka-credentials kautuka-credentials", StringComparison.Ordinal);
        int socketDirs = source.IndexOf("install_socket_runtime_dirs \"$service_user\"", StringComparison.Ordinal);
        Assert.True(credentialsAccount >= 0 && credentialsAccount < socketDirs, "CredentialsHost account must exist before socket tmpfiles are written.");
        Assert.Contains("/etc/systemd/system/${unit_name}", source, StringComparison.Ordinal);
        Assert.Contains("User=${runtime_user}", source, StringComparison.Ordinal);
        Assert.Contains("Requires=kautuka-channelhost.service", source, StringComparison.Ordinal);
        Assert.Contains("supplementary_groups='SupplementaryGroups=kautuka kautuka-credentials-client'", source, StringComparison.Ordinal);
        Assert.Contains("kautuka-channelhost must not belong to the broad appliance group", source, StringComparison.Ordinal);
        Assert.DoesNotContain("network-online.target kautuka-channelhost.service kautuka-credentialshost.service", source, StringComparison.Ordinal);
        Assert.Contains("--provision-channel-security", source, StringComparison.Ordinal);

        Assert.Contains("gateway-channel-client.pfx", source, StringComparison.Ordinal);
        Assert.Contains("gateway-channel-delegation.pfx", source, StringComparison.Ordinal);
        Assert.Contains("channel-server.pfx", source, StringComparison.Ordinal);
        Assert.Contains("chmod 0700", source, StringComparison.Ordinal);
        Assert.Contains("chmod 0600", source, StringComparison.Ordinal);
        Assert.Contains("validate_exact_material_inventory", source, StringComparison.Ordinal);
        Assert.Contains("validate_material_owner_mode", source, StringComparison.Ordinal);
        Assert.Contains("validate_isolated_channel_runtime_access", source, StringComparison.Ordinal);
        Assert.Contains("Gateway can read ChannelHost private material", source, StringComparison.Ordinal);
        Assert.Contains("ChannelHost can read Gateway private material", source, StringComparison.Ordinal);
        Assert.Contains("'root:kautuka:771'", source, StringComparison.Ordinal);

        Assert.Contains("chown_tree_excluding \"$internal_state_root\" \"$service_user:$service_group\" \"$channel_security_root\"", source, StringComparison.Ordinal);
        Assert.Contains("-o -exec chown -h \"$owner\" {} +", source, StringComparison.Ordinal);
        Assert.Contains("local containers_root=\"$root/.local/share/containers\"", source, StringComparison.Ordinal);
        Assert.Contains("local credentials_state_root=\"$root/credentialsHost\"", source, StringComparison.Ordinal);
        Assert.Contains("-o -path \"$credentials_state_root\" -o -path \"$credentials_state_root/*\"", source, StringComparison.Ordinal);
        Assert.Contains("-o -exec chgrp -h \"$group\" {} +", source, StringComparison.Ordinal);
        Assert.Contains("validate_channel_security_material_permissions \"$internal_state_root\"", source, StringComparison.Ordinal);
        Assert.Contains("Group=${credential_group}", source, StringComparison.Ordinal);
        Assert.Contains("chmod 2750 \"$credentials_root\" \"$credentials_root/transport\" \"$credentials_state_root\" \"$credentials_state_root/transport\"", source, StringComparison.Ordinal);
        Assert.Contains("chown \"$credential_user:$credential_group\" \"$credentials_root\" \"$credentials_root/transport\" \"$credentials_state_root\" \"$credentials_state_root/transport\"", source, StringComparison.Ordinal);
        Assert.Contains("mkdir -p \"$version_root\" \"$log_dir\" \"$credentials_root/vault\" \"$credentials_root/transport\" \"$credentials_state_root/vault\" \"$credentials_state_root/transport\"", source, StringComparison.Ordinal);
        Assert.Contains("chown -R \"$credential_user:$credential_user\" \"$install_root\" \"$credentials_root/vault\" \"$credentials_state_root/vault\" \"$credentials_root/transport\" \"$credentials_state_root/transport\" \"$log_dir\"", source, StringComparison.Ordinal);
        Assert.Contains("chmod 0700 \"$credentials_root/vault\" \"$credentials_state_root/vault\" \"$log_dir\"", source, StringComparison.Ordinal);
        Assert.Contains("validate_material_owner_mode \"$internal_state_root/credentialsHost/vault\" 'kautuka-credentials:kautuka-credentials:700'", source, StringComparison.Ordinal);
        Assert.Contains("Gateway can read CredentialsHost vault", source, StringComparison.Ordinal);
        Assert.Contains("ReadWritePaths=${credentials_root} ${credentials_state_root} ${log_dir}", source, StringComparison.Ordinal);
        Assert.Contains("prepare_mcpb_bundle_share", source, StringComparison.Ordinal);
        Assert.Contains("chown -R \"kautuka-gateway:$service_group\" \"$bundle_root\"", source, StringComparison.Ordinal);
        Assert.Contains("skipping empty update-source.json; UpdateService stays unprovisioned", source, StringComparison.Ordinal);
        Assert.Contains("wait_for_web_ui \"$service_user\"", source, StringComparison.Ordinal);
        Assert.Contains("ensure_dotnet_host_libraries", source, StringComparison.Ordinal);
        Assert.Contains("libicu is required to run Kautuka host services", source, StringComparison.Ordinal);
        Assert.Contains("dnf install -y libicu", source, StringComparison.Ordinal);
        Assert.Contains("cd \"$HOME\" || exit; exec \"$@\"", source, StringComparison.Ordinal);
        Assert.Contains("this installer requires Bash", source, StringComparison.Ordinal);
        Assert.Contains("ensure_rootless_idmap_helpers", source, StringComparison.Ordinal);
        Assert.Contains("kautuka-root-rshared.service", source, StringComparison.Ordinal);
        Assert.Contains("cap_setuid=ep", source, StringComparison.Ordinal);
        Assert.Contains("sed -i 's/\\r$//' /usr/local/lib/kautuka/apply-release", source, StringComparison.Ordinal);
        Assert.Contains("no_new_privileges=''", source, StringComparison.Ordinal);
        Assert.Contains("KillMode=process", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n", source, StringComparison.Ordinal);
        Assert.DoesNotContain("install_host_service_user gateway Gateway", source, StringComparison.Ordinal);
        Assert.DoesNotContain("install_host_service_user channelhost ChannelHost", source, StringComparison.Ordinal);

        int managerStart = source.LastIndexOf("start kautuka-manager.service", StringComparison.Ordinal);
        int channelHostStart = source.LastIndexOf("systemctl start kautuka-channelhost.service", StringComparison.Ordinal);
        int gatewayStart = source.LastIndexOf("systemctl start kautuka-gateway.service", StringComparison.Ordinal);
        Assert.True(managerStart >= 0 && managerStart < channelHostStart);
        Assert.True(channelHostStart < gatewayStart);
    }

    private static string FindProjectRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "scripts", "kautuka-install-linux.sh.in")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Kautuka repository root.");
    }
}

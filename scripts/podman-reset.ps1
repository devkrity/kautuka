# Kill the proxies that are hanging
Stop-Process -Name "win-sshproxy" -Force -ErrorAction SilentlyContinue

# Also kill the Podman Desktop frontend if it's acting up
Stop-Process -Name "Podman Desktop" -Force -ErrorAction SilentlyContinue

# Reset the WSL VM state
wsl --terminate podman-machine-default
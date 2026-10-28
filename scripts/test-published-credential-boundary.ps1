param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath
)

$ErrorActionPreference = 'Stop'
$stateRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("kautuka-credential-process-" + [guid]::NewGuid().ToString('N'))
$stdout = Join-Path $stateRoot 'stdout.log'
$stderr = Join-Path $stateRoot 'stderr.log'
New-Item -ItemType Directory -Path $stateRoot | Out-Null
$env:Kautuka__ContainerStateRoot = $stateRoot
# This smoke test asserts the credential boundary - published mTLS material, server
# identity, vault init - none of which depend on the transport. Services default to a
# Unix socket, which PowerShell cannot dial without a ConnectCallback, so opt this run
# back onto TCP rather than reimplementing the handshake here.
$env:Kautuka__Transport = 'tcp'
$process = $null
try {
    if (-not (Test-Path -LiteralPath $ExecutablePath)) {
        throw "CredentialsHost executable was not found: $ExecutablePath"
    }

    # PowerShell scriptblocks cannot run on HttpClient TLS worker threads (no Runspace).
    # Compile a plain CLR callback once per process.
    if (-not ('Kautuka.CredentialBoundarySmoke.Tls' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Kautuka.CredentialBoundarySmoke
{
    public static class Tls
    {
        public static bool AcceptCredentialsServer(
            HttpRequestMessage request,
            X509Certificate2 certificate,
            X509Chain chain,
            SslPolicyErrors errors)
        {
            return certificate is not null
                && certificate.GetNameInfo(X509NameType.SimpleName, false) == "Credentials Server";
        }
    }
}
'@
    }

    $process = Start-Process -FilePath $ExecutablePath -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    # Matches KautukaHostPathPolicy.ResolveServiceStateRoot(..., "credentialsHost").
    $transportRoot = Join-Path $stateRoot 'credentialsHost/transport'
    $pfx = Join-Path $transportRoot '.credentials-client.pfx'
    $ca = Join-Path $transportRoot '.ca.cer'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((! (Test-Path $pfx) -or ! (Test-Path $ca)) -and [DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "CredentialsHost exited early: $(Get-Content $stderr -Raw)" }
        Start-Sleep -Milliseconds 100
    }
    if (!(Test-Path $pfx) -or !(Test-Path $ca)) { throw 'CredentialsHost did not publish mTLS material.' }

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $clientCertificate = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile($pfx, $null)
    [void]$handler.ClientCertificates.Add($clientCertificate)
    $handler.ServerCertificateCustomValidationCallback = [Kautuka.CredentialBoundarySmoke.Tls]::AcceptCredentialsServer
    $client = [System.Net.Http.HttpClient]::new($handler)
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, 'https://localhost:20002/health')
    $request.Version = [Version]::new(2, 0)
    $request.VersionPolicy = [System.Net.Http.HttpVersionPolicy]::RequestVersionExact
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    if (!$response.IsSuccessStatusCode) { throw "Published CredentialsHost health failed: $($response.StatusCode)" }
    if (!(Test-Path (Join-Path $stateRoot 'credentialsHost/vault/kautuka.credentials.db'))) { throw 'Published CredentialsHost did not initialize its SQLite vault.' }
}
catch {
    Write-Error $_
    exit 1
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force }
    Remove-Item Env:Kautuka__ContainerStateRoot -ErrorAction SilentlyContinue
    Remove-Item Env:Kautuka__Transport -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stateRoot -Recurse -Force -ErrorAction SilentlyContinue
}

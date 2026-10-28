#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generates self-signed mTLS certificates for local Gateway ↔ ChannelHost gRPC development.

.DESCRIPTION
    Creates a minimal CA, a Gateway server cert, and a ChannelHost client cert.
    Outputs PFX files and a CA PEM that match the default config keys:
        Gateway:  ChannelGrpc:ServerCertPath / ChannelGrpc:ClientCaCertPath
        ChannelHost: GatewayGrpc:ClientCertPath / GatewayGrpc:ServerCaCertPath

    Requires: openssl in PATH (ships with Git for Windows and most Unix platforms).

.EXAMPLE
    .\scripts\gen-dev-certs.ps1
    .\scripts\gen-dev-certs.ps1 -OutDir certs\dev -ValidDays 3650
#>

param(
    [string] $OutDir    = "certs\dev",
    [int]    $ValidDays = 3650
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command openssl -ErrorAction SilentlyContinue)) {
    Write-Error "openssl not found in PATH. Install Git for Windows or OpenSSL."
}

$out = Join-Path $PSScriptRoot "..\$OutDir"
New-Item -ItemType Directory -Force -Path $out | Out-Null
Write-Host "Generating certs in: $(Resolve-Path $out)"

$caKey  = Join-Path $out "ca.key"
$caCert = Join-Path $out "ca.pem"
$srvKey = Join-Path $out "server.key"
$srvCsr = Join-Path $out "server.csr"
$srvPfx = Join-Path $out "server.pfx"
$cliKey = Join-Path $out "client.key"
$cliCsr = Join-Path $out "client.csr"
$cliPfx = Join-Path $out "client.pfx"

# ----- CA -----
Write-Host "Generating CA..."
openssl genrsa -out $caKey 4096 2>&1 | Out-Null
openssl req -new -x509 -days $ValidDays -key $caKey -out $caCert `
    -subj "/CN=Kautuka Dev CA/O=Kautuka/C=US" 2>&1 | Out-Null

# ----- Gateway server cert (SAN localhost + 127.0.0.1) -----
Write-Host "Generating Gateway server cert..."
openssl genrsa -out $srvKey 2048 2>&1 | Out-Null
openssl req -new -key $srvKey -out $srvCsr `
    -subj "/CN=kautuka-gateway/O=Kautuka/C=US" 2>&1 | Out-Null

$srvExt = @"
[req_ext]
subjectAltName = @alt_names
[alt_names]
DNS.1 = localhost
IP.1  = 127.0.0.1
"@
$extFile = Join-Path $out "server_ext.cnf"
$srvExt | Set-Content $extFile -Encoding UTF8

openssl x509 -req -days $ValidDays -in $srvCsr -CA $caCert -CAkey $caKey `
    -CAcreateserial -out (Join-Path $out "server.crt") `
    -extfile $extFile -extensions req_ext 2>&1 | Out-Null

openssl pkcs12 -export -out $srvPfx `
    -inkey $srvKey -in (Join-Path $out "server.crt") -certfile $caCert `
    -passout pass:dev 2>&1 | Out-Null

# ----- ChannelHost client cert -----
Write-Host "Generating ChannelHost client cert..."
openssl genrsa -out $cliKey 2048 2>&1 | Out-Null
openssl req -new -key $cliKey -out $cliCsr `
    -subj "/CN=kautuka-channelhost/O=Kautuka/C=US" 2>&1 | Out-Null

openssl x509 -req -days $ValidDays -in $cliCsr -CA $caCert -CAkey $caKey `
    -CAcreateserial -out (Join-Path $out "client.crt") 2>&1 | Out-Null

openssl pkcs12 -export -out $cliPfx `
    -inkey $cliKey -in (Join-Path $out "client.crt") -certfile $caCert `
    -passout pass:dev 2>&1 | Out-Null

# ----- Output summary -----
Write-Host "`nDone. Add to appsettings.Development.json:"
Write-Host ""
Write-Host "  Gateway:"
Write-Host "    `"ChannelGrpc`": {"
Write-Host "      `"ServerCertPath`": `"$srvPfx`","
Write-Host "      `"ServerCertPassword`": `"dev`","
Write-Host "      `"ClientCaCertPath`": `"$caCert`""
Write-Host "    }"
Write-Host ""
Write-Host "  ChannelHost:"
Write-Host "    `"GatewayGrpc`": {"
Write-Host "      `"Url`": `"https://localhost:8099`","
Write-Host "      `"ClientCertPath`": `"$cliPfx`","
Write-Host "      `"ClientCertPassword`": `"dev`","
Write-Host "      `"ServerCaCertPath`": `"$caCert`""
Write-Host "    }"

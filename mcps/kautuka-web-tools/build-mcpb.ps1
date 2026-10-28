#!/usr/bin/env pwsh
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '../..'))

if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
    Write-Error "npx is required to build MCPB bundles with the reference CLI"
    exit 1
}

$pyprojectPath = Join-Path $scriptDir 'pyproject.toml'
if (-not (Test-Path -LiteralPath $pyprojectPath)) {
    Write-Error "Could not find pyproject.toml at $pyprojectPath"
    exit 1
}

$versionLine = Get-Content -LiteralPath $pyprojectPath |
    Where-Object { $_ -match '^\s*version\s*=\s*"([^"]+)"\s*$' } |
    Select-Object -First 1

if (-not $versionLine) {
    Write-Error "Could not determine package version from pyproject.toml"
    exit 1
}

$version = ([regex]::Match($versionLine, '^\s*version\s*=\s*"([^"]+)"\s*$')).Groups[1].Value
if ([string]::IsNullOrWhiteSpace($version)) {
    Write-Error "Could not determine package version from pyproject.toml"
    exit 1
}

$outputDir = Join-Path $repoRoot 'artifacts/mcpb'
$outputFile = Join-Path $outputDir "kautuka-web-tools-$version.mcpb"

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

if (Test-Path -LiteralPath $outputFile) {
    Remove-Item -LiteralPath $outputFile -Force
}

& npx -y @anthropic-ai/mcpb validate $scriptDir
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& npx -y @anthropic-ai/mcpb pack $scriptDir $outputFile
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $outputFile)) {
    Write-Error "Failed to create MCPB archive at $outputFile"
    exit 1
}

Write-Output $outputFile

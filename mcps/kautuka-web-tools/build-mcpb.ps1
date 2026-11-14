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

$tempFile = "$outputFile.$([guid]::NewGuid().ToString('N')).tmp.mcpb"
$backupFile = "$outputFile.$([guid]::NewGuid().ToString('N')).bak"
try {
    & npx -y @anthropic-ai/mcpb validate $scriptDir
    if ($LASTEXITCODE -ne 0) { throw "MCPB validation failed (exit $LASTEXITCODE)" }

    & npx -y @anthropic-ai/mcpb pack $scriptDir $tempFile
    if ($LASTEXITCODE -ne 0) { throw "MCPB pack failed (exit $LASTEXITCODE)" }
    if (-not (Test-Path -LiteralPath $tempFile) -or (Get-Item -LiteralPath $tempFile).Length -lt 4) { throw "Failed to create MCPB archive at $tempFile" }
    & node (Join-Path $repoRoot 'scripts/validate-mcpb-archive.mjs') $tempFile
    if ($LASTEXITCODE -ne 0) { throw "MCPB ZIP integrity validation failed (exit $LASTEXITCODE)" }

    if (Test-Path -LiteralPath $outputFile) {
        [System.IO.File]::Replace($tempFile, $outputFile, $backupFile)
        if (Test-Path -LiteralPath $backupFile) { Remove-Item -LiteralPath $backupFile -Force }
    } else {
        [System.IO.File]::Move($tempFile, $outputFile)
    }
} finally {
    if (Test-Path -LiteralPath $tempFile) { Remove-Item -LiteralPath $tempFile -Force }
    if (Test-Path -LiteralPath $backupFile) { Remove-Item -LiteralPath $backupFile -Force }
}

Write-Output $outputFile

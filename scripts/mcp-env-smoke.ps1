param(
    [string]$ContainerName,
    [string]$UserName = 'Kautuka'
)

$ErrorActionPreference = 'Stop'

function Invoke-Podman {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & podman @Arguments
    return $LASTEXITCODE
}

if ([string]::IsNullOrWhiteSpace($ContainerName)) {
    $containers = & podman ps --format '{{.Names}}'
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to list running containers."
    }

    $match = $containers | Where-Object { $_ -match '^kautuka-agent-' } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($match)) {
        throw "No running kautuka-agent-* container found."
    }

    $ContainerName = $match
}

Write-Host "Using container : $ContainerName"
Write-Host "Injecting env    : USERname=$UserName"

$execArgs = @(
    'exec',
    '--env', "USERname=$UserName",
    $ContainerName,
    'sh',
    '-lc',
    'echo hello $USERname'
)

$output = & podman @execArgs 2>&1
$exitCode = $LASTEXITCODE

Write-Host '--- command output ---'
$output | ForEach-Object { Write-Host $_ }
Write-Host '----------------------'
Write-Host "Exit code: $exitCode"

if ($exitCode -ne 0) {
    throw "Smoke test failed with exit code $exitCode."
}

$expected = "hello $UserName"
if (-not ($output -join "`n").Contains($expected)) {
    throw "Smoke test did not produce expected output '$expected'."
}

Write-Host "Smoke test passed: $expected"

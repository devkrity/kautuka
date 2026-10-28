param(
    [Parameter(Mandatory = $true)]
    [string]$Image,
    [int]$Iterations = 25
)

$ErrorActionPreference = 'Stop'

if ($env:KAUTUKA_RUN_PODMAN_STRESS -ne '1') {
    throw 'Live Podman stress is gated. Set KAUTUKA_RUN_PODMAN_STRESS=1 explicitly.'
}

if ($Iterations -lt 1 -or $Iterations -gt 500) {
    throw 'Iterations must be between 1 and 500.'
}

& podman info --format json | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Podman is unavailable.'
}

$name = 'kautuka-manager-crash-harness'
$expectedFingerprint = ''
try {
    for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
        $oldFingerprint = "old-$iteration"
        $expectedFingerprint = "replacement-$iteration"

        & podman run -d --replace --name $name `
            --label kautuka.managed=manager `
            --label kautuka.role=agent `
            --label "kautuka.desired-version=$iteration" `
            --label "kautuka.spec-fingerprint=$oldFingerprint" `
            $Image sh -lc 'while true; do sleep 1; done' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Initial container start failed at iteration $iteration." }

        # Model an abrupt container/runtime loss during an accepted desired state.
        & podman kill $name | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Crash injection failed at iteration $iteration." }

        # This is the exact replace path used by Manager after inspect observes stopped/drifted state.
        & podman run -d --replace --name $name `
            --label kautuka.managed=manager `
            --label kautuka.role=agent `
            --label "kautuka.desired-version=$iteration" `
            --label "kautuka.spec-fingerprint=$expectedFingerprint" `
            $Image sh -lc 'while true; do sleep 1; done' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Replacement failed at iteration $iteration." }

        $observed = & podman inspect --format '{{.State.Status}}|{{index .Config.Labels "kautuka.desired-version"}}|{{index .Config.Labels "kautuka.spec-fingerprint"}}' $name
        if ($LASTEXITCODE -ne 0) { throw "Replacement observation failed at iteration $iteration." }
        if ($observed.Trim() -ne "running|$iteration|$expectedFingerprint") {
            throw "Replacement did not converge at iteration $iteration. Observed '$observed'."
        }

        $count = @(& podman ps -a --filter "name=^${name}$" --format '{{.Names}}').Count
        if ($count -ne 1) { throw "Expected exactly one managed container, observed $count at iteration $iteration." }
    }

    Write-Host "Manager Podman crash/replace stress passed: $Iterations iterations; final fingerprint $expectedFingerprint."
}
finally {
    & podman rm -f $name 2>$null | Out-Null
}

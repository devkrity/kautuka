[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)

    Write-Host "==> $Message"
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$preCommitPath = Join-Path $repoRoot ".husky\pre-commit"
$huskyShimPath = Join-Path $repoRoot ".husky\_\husky.sh"

Push-Location $repoRoot
try {
    Write-Step "Restoring repository-local .NET tools"
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool restore failed with exit code $LASTEXITCODE."
    }

    Write-Step "Installing the Husky Git hooks"
    dotnet husky install
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet husky install failed with exit code $LASTEXITCODE."
    }

    $hooksPath = (git config --local --get core.hooksPath).Trim()
    if ($LASTEXITCODE -ne 0 -or $hooksPath -ne ".husky") {
        throw "Expected local git core.hooksPath to be '.husky', but found '$hooksPath'."
    }

    if (-not (Test-Path -LiteralPath $preCommitPath -PathType Leaf)) {
        throw "Expected pre-commit hook was not found at '$preCommitPath'."
    }

    if (-not (Test-Path -LiteralPath $huskyShimPath -PathType Leaf)) {
        throw "Expected generated Husky shim was not found at '$huskyShimPath'."
    }

    Write-Host "Husky pre-commit hook is installed and active for this clone."
}
finally {
    Pop-Location
}

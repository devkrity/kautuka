param(
  [Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments
)
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$Check=$false; $SkipInstall=$false; $Reset=$false; $Yes=$false; $Help=$false; $Repos='kautuka-auth,narad'
for ($i=0; $i -lt $Arguments.Count; $i++) {
  switch -Exact ($Arguments[$i]) {
    '--check' { $Check=$true }
    '--skip-install' { $SkipInstall=$true }
    '--reset' { $Reset=$true }
    '--yes' { $Yes=$true }
    '--help' { $Help=$true }
    '-h' { $Help=$true }
    '--repos' { $i++; if ($i -ge $Arguments.Count) { throw '--repos requires a comma-separated repository list.' }; $Repos=$Arguments[$i] }
    default { throw "Unknown option: $($Arguments[$i])" }
  }
}
if ($Help) { Write-Host 'Usage: ./setup_the_world.ps1 [--check] [--skip-install] [--repos kautuka-auth,narad] [--reset] [--yes]'; exit 0 }
$Package = Get-Content -LiteralPath (Join-Path $Root 'package.json') -Raw | ConvertFrom-Json
$Sdk = (Get-Content -LiteralPath (Join-Path $Root 'global.json') -Raw | ConvertFrom-Json).sdk.version
$SdkRollForward = (Get-Content -LiteralPath (Join-Path $Root 'global.json') -Raw | ConvertFrom-Json).sdk.rollForward
$Node = (Get-Content -LiteralPath (Join-Path $Root '.nvmrc') -Raw).Trim()
$Pnpm = ($Package.packageManager -replace '^pnpm@','')
$Python = (Get-Content -LiteralPath (Join-Path $Root '.python-version') -Raw).Trim()
$Rows = [System.Collections.Generic.List[object]]::new()
function Probe([string]$Tool,[string]$Expected,[string]$Command,[string[]]$CommandArgs) {
  $found = '<missing>'
  try { $out = & $Command @CommandArgs 2>$null; if ($LASTEXITCODE -eq 0 -and $out) { $found = [string]($out | Select-Object -First 1); $found = $found.Trim() } } catch {}
  $ok = $found -eq $Expected -or ($Expected -eq 'present' -and $found -ne '<missing>')
  $script:Rows.Add([pscustomobject]@{Tool=$Tool;Expected=$Expected;Found=$found;Status=$(if($ok){'OK'}else{'MISSING / WRONG'})})
  return $ok
}
function ProbeDotnet {
  $found='<missing>'; try {$out=& dotnet --version 2>$null; if($LASTEXITCODE -eq 0 -and $out){$found=([string]$out).Trim()}} catch {}
  $ok=$false; try {$actualVersion=[version]$found; $requiredVersion=[version]$Sdk; $ok=($actualVersion -eq $requiredVersion) -or ($SdkRollForward -eq 'latestFeature' -and $actualVersion.Major -eq $requiredVersion.Major -and $actualVersion.Minor -eq $requiredVersion.Minor -and $actualVersion -ge $requiredVersion)} catch {}
  $expected=$Sdk; if($SdkRollForward -eq 'latestFeature'){$expected+=' (latestFeature)'}
  $Rows.Add([pscustomobject]@{Tool='.NET SDK';Expected=$expected;Found=$found;Status=$(if($ok){'OK'}else{'MISSING / WRONG'})})
  return $ok
}
if ($Reset) {
  if ($Check) { throw '--check cannot be combined with --reset.' }
  $Candidates = @((Join-Path $Root 'node_modules'),(Join-Path $Root 'src/frontend/node_modules'),(Join-Path $Root 'src/frontend/web/node_modules'),(Join-Path $Root 'mcps/kautuka-web-tools/.venv'),(Join-Path $Root 'mcps/kautuka-web-tools/.pytest_cache'))
  foreach ($Name in @('bin','obj','.venv','__pycache__','.pytest_cache')) {
    $Candidates += @(Get-ChildItem -LiteralPath $Root -Directory -Recurse -Filter $Name -ErrorAction SilentlyContinue | Where-Object FullName -notmatch '\\node_modules\\' | ForEach-Object FullName)
  }
  $Candidates = @($Candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Sort-Object -Unique)
  $Targets = @(); $SkippedTargets = @()
  foreach ($Candidate in $Candidates) {
    $Relative = $Candidate.Substring($Root.Length).TrimStart([char[]]@('\','/')) -replace '\\','/'
    & git -C $Root check-ignore -q -- $Relative 2>$null
    if ($LASTEXITCODE -ne 0) { $SkippedTargets += "$Candidate (not ignored by Git)"; continue }
    $Tracked = @(& git -C $Root ls-files -- $Relative)
    if ($Tracked.Count -gt 0) { $SkippedTargets += "$Candidate (contains tracked files)"; continue }
    $Targets += $Candidate
  }
  $DatabaseFiles = @()
  foreach ($Name in @('kautuka.db','kautuka.db-wal','kautuka.db-shm','kautuka-usage.db','kautuka-usage.db-wal','kautuka-usage.db-shm')) { $Candidate=Join-Path (Join-Path $HOME '.kautuka/runtime') $Name; if(Test-Path -LiteralPath $Candidate -PathType Leaf){$DatabaseFiles += $Candidate} }
  Write-Host 'Reset will delete these Git-ignored build/dependency/cache directories with no tracked files:' -ForegroundColor Yellow
  if($Targets.Count){$Targets | ForEach-Object { Write-Host "  $_" }}else{Write-Host '  <none found>'}
  Write-Host 'Reset will skip these directories because they are not ignored or contain tracked files:' -ForegroundColor Yellow
  if($SkippedTargets.Count){$SkippedTargets | ForEach-Object {Write-Host "  $_"}}else{Write-Host '  <none found>'}
  Write-Host 'Reset will also delete these Kautuka development database files:' -ForegroundColor Yellow
  if($DatabaseFiles.Count){$DatabaseFiles | ForEach-Object {Write-Host "  $_"}}else{Write-Host '  <none found>'}
  if (-not $Yes -and (Read-Host 'Continue? [y/N]') -notmatch '^(y|yes)$') { Write-Host 'Reset cancelled.'; exit 1 }
  $RootPrefix = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  foreach ($Target in $Targets) { $Resolved = (Resolve-Path -LiteralPath $Target).Path; if (-not $Resolved.StartsWith($RootPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to delete outside repository: $Resolved" }; Remove-Item -LiteralPath $Resolved -Recurse -Force }
  foreach ($File in $DatabaseFiles) { if ([IO.Path]::GetFileName($File) -notin @('kautuka.db','kautuka.db-wal','kautuka.db-shm','kautuka-usage.db','kautuka-usage.db-wal','kautuka-usage.db-shm') -or (Split-Path $File -Parent) -ne (Join-Path $HOME '.kautuka/runtime')) { throw "Refusing unexpected data file: $File" }; Remove-Item -LiteralPath $File -Force }
}
$DotnetOk = ProbeDotnet
$NodeOk = Probe 'Node.js' "v$Node" 'node' @('--version')
$PnpmOk = Probe 'pnpm (Corepack)' $Pnpm 'pnpm' @('--version')
$UvOk = Probe 'uv' 'present' 'uv' @('--version')
$PyFound = '<missing>'; try { $PyPath=(& uv python find $Python 2>$null | Select-Object -First 1).Trim(); if($PyPath){$PyFound=(& $PyPath --version 2>$null | Select-Object -First 1).Trim()} } catch {}; $Rows.Add([pscustomobject]@{Tool='Python (uv)';Expected="Python $Python";Found=$PyFound;Status=$(if($PyFound -like "Python $Python*"){'OK'}else{'MISSING / WRONG'})})
$PodmanOk = Probe 'Podman' 'present' 'podman' @('--version')
$GitOk = Probe 'Git' 'present' 'git' @('--version')
$Ext = Test-Path -LiteralPath (Join-Path $Root '.vscode/extensions.json'); $Rows.Add([pscustomobject]@{Tool='VS Code extensions';Expected='manifest present';Found=$(if($Ext){'present'}else{'<missing>'});Status=$(if($Ext){'OK'}else{'MISSING / WRONG'})})
$Missing = @($Rows | Where-Object Status -ne 'OK')
if (-not $Check -and -not $SkipInstall) {
  if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw 'winget is required for automatic Windows tool installation. Install prerequisites manually, then rerun.' }
  if (-not $DotnetOk) { winget install --id Microsoft.DotNet.SDK.10 --exact --version $Sdk --force --accept-package-agreements --accept-source-agreements }
  if (-not $NodeOk) { winget install --id OpenJS.NodeJS --exact --version $Node --force --accept-package-agreements --accept-source-agreements }
  if (-not $PnpmOk) { if (-not (Get-Command corepack -ErrorAction SilentlyContinue)) { npm install --global corepack }; corepack enable; corepack prepare "pnpm@$Pnpm" --activate }
  if (-not $UvOk) { winget install --id astral-sh.uv --exact --accept-package-agreements --accept-source-agreements }
  if (-not $PodmanOk) { winget install --id RedHat.Podman --exact --accept-package-agreements --accept-source-agreements }
  $env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
  if (-not (Get-Command corepack -ErrorAction SilentlyContinue)) { $CorepackRoot=Join-Path $env:LOCALAPPDATA 'Kautuka/corepack'; npm install --prefix $CorepackRoot corepack; if($LASTEXITCODE){throw 'Could not install Corepack'}; $env:Path=(Join-Path $CorepackRoot 'node_modules/.bin')+';'+$env:Path }
  $CorepackBin=Join-Path $env:LOCALAPPDATA 'Kautuka/bin'; New-Item -ItemType Directory -Path $CorepackBin -Force | Out-Null; corepack enable --install-directory $CorepackBin; if($LASTEXITCODE){throw 'Corepack could not enable shims'}; $env:Path=$CorepackBin+';'+$env:Path; corepack prepare "pnpm@$Pnpm" --activate; if($LASTEXITCODE){throw "Corepack could not activate pnpm $Pnpm"}
  if ($PyFound -notlike "Python $Python*") { uv python install $Python; if($LASTEXITCODE){throw "uv could not provision Python $Python"} }
}
if (-not $Check) {
  if ($SkipInstall -and $Missing.Count) { throw "--skip-install: install required tools first: $(($Missing.Tool) -join ', ')" }
  $Rows.Clear()
  $null=ProbeDotnet
  $null=Probe 'Node.js' "v$Node" 'node' @('--version')
  $null=Probe 'pnpm (Corepack)' $Pnpm 'pnpm' @('--version')
  $null=Probe 'uv' 'present' 'uv' @('--version')
  $PyFound='<missing>'; try { $PyPath=(& uv python find $Python 2>$null | Select-Object -First 1).Trim(); if($PyPath){$PyFound=(& $PyPath --version 2>$null | Select-Object -First 1).Trim()} } catch {}
  $Rows.Add([pscustomobject]@{Tool='Python (uv)';Expected="Python $Python";Found=$PyFound;Status=$(if($PyFound -like "Python $Python*"){'OK'}else{'MISSING / WRONG'})})
  $null=Probe 'Podman' 'present' 'podman' @('--version')
  $null=Probe 'Git' 'present' 'git' @('--version')
  $Rows.Add([pscustomobject]@{Tool='VS Code extensions';Expected='manifest present';Found='present';Status='OK'})
  $Missing=@($Rows | Where-Object Status -ne 'OK')
  if ($Missing.Count) { throw "Required tools still missing or wrong: $(($Missing.Tool) -join ', ')" }
  $CreatedRepos=[System.Collections.Generic.List[string]]::new()
  foreach ($Repo in ($Repos -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
    if ($Repo -notin @('kautuka-auth','narad')) { throw "Unsupported repo: $Repo" }
    $Dest = Join-Path (Split-Path $Root -Parent) $Repo; $Url = "https://github.com/devkrity/$Repo.git"
    if (Test-Path -LiteralPath $Dest) { if (-not (Test-Path -LiteralPath (Join-Path $Dest '.git'))) { throw "Path exists but is not a Git repository; leaving it untouched: $Dest" }; git -C $Dest fetch --all --prune; if ($LASTEXITCODE) { throw "Fetch failed: $Repo" } } else { git clone $Url $Dest; if ($LASTEXITCODE) { throw "Clone failed: $Repo" }; $CreatedRepos.Add($Repo) }
  }
  foreach($Repo in $CreatedRepos){$Dest=Join-Path (Split-Path $Root -Parent) $Repo; $SiblingPackage=Get-Content -LiteralPath (Join-Path $Dest 'package.json') -Raw | ConvertFrom-Json; if($Repo -eq 'kautuka-auth'){if($SiblingPackage.scripts.setup){npm --prefix $Dest run setup}else{npm --prefix $Dest ci}; if($LASTEXITCODE){throw 'kautuka-auth setup failed'}} elseif($Repo -eq 'narad'){if($SiblingPackage.scripts.setup){pnpm --dir $Dest run setup}else{pnpm --dir $Dest install --frozen-lockfile}; if($LASTEXITCODE){throw 'Narad setup failed'}}}
  Push-Location $Root
  try {
    dotnet restore src/Kautuka.slnx; if ($LASTEXITCODE) { throw 'dotnet restore failed' }
    pnpm install; if ($LASTEXITCODE) { throw 'pnpm install failed' }
    uv sync --project mcps/kautuka-web-tools; if ($LASTEXITCODE) { throw 'uv sync failed' }
    pnpm --dir src/frontend/web build; if ($LASTEXITCODE) { throw 'Frontend build failed' }
    dotnet build src/Kautuka.slnx --no-restore; if ($LASTEXITCODE) { throw 'dotnet build failed' }
    git config --local core.hooksPath .husky; if ($LASTEXITCODE) { throw 'Git hook path setup failed' }
    foreach($Example in @((Join-Path $Root '.env.example'),(Join-Path $Root 'src/backend/Kautuka.Gateway/.env.example'),(Join-Path (Split-Path $Root -Parent) 'kautuka-auth/.dev.vars.example'))){ if(Test-Path -LiteralPath $Example){$Target=$Example -replace '\.example$','.local'; if($Example -like '*\.dev.vars.example'){$Target=$Example -replace '\.example$',''}; if(-not (Test-Path -LiteralPath $Target)){Copy-Item -LiteralPath $Example -Destination $Target}} }
  } finally { Pop-Location }
  $AuthVars=Join-Path (Split-Path $Root -Parent) 'kautuka-auth/.dev.vars'
  if(Test-Path -LiteralPath $AuthVars){Write-Host 'Review blank local auth settings as needed: GOOGLE_CLIENT_ID/SECRET, MICROSOFT_CLIENT_ID/SECRET, GOOGLE_LOGIN_CLIENT_ID/SECRET, MICROSOFT_LOGIN_CLIENT_ID/SECRET, AUTH_TICKET_HMAC_KEY, REFRESH_TOKEN_KEYS, REFRESH_TOKEN_ACTIVE_KID.'}
  Write-Host 'Kautuka provider credentials are entered in the first-run settings UI; setup never retrieves them.'
  if((Get-Command code -ErrorAction SilentlyContinue) -and (Read-Host 'Install the VS Code recommended extensions now? [y/N]') -match '^(y|yes)$'){ $Recommendations=(Get-Content (Join-Path $Root '.vscode/extensions.json') -Raw | ConvertFrom-Json).recommendations; foreach($Extension in $Recommendations){code --install-extension $Extension; if($LASTEXITCODE){throw "VS Code extension install failed for $Extension"}} }
  Write-Host 'Tip: use git commit -s for DCO sign-off.'
}
Write-Host "`nDoctor summary" -ForegroundColor Cyan; $Rows | Format-Table -AutoSize
if ($Missing.Count) { Write-Host 'Install missing prerequisites. Windows: use winget or the official installer, then rerun.' -ForegroundColor Yellow; exit 1 }
if (-not $Check) { Write-Host 'Next: Open in VS Code and press F5 (Launch Kautuka), or run npm run launch.' }

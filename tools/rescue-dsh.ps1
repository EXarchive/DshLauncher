#requires -Version 7.0
<#
  rescue-dsh.ps1 -- a self-contained rescue harness for a broken DSH install.

  WHY THIS EXISTS
  ---------------
  The launcher's job is repairing DSH, so it must not depend on the DSH install
  it repairs. If the machine's install (the global @deepseek-ai/dsh tree or the
  shared @deepseek-ai core layer under %DSH_HOME%\profiles) is corrupted, this
  script still boots an agent and still repairs session logs, because it runs a
  SNAPSHOT of the harness from <LAUNCHER>\rescue\ and never imports anything
  from the machine's install.

  COMMANDS
  --------
    create   Snapshot the DSH package tree + the whole dependency closure (which
             contains the shared @deepseek-ai core layer) into rescue\dsh\, set up
             the isolated rescue home, and write rescue\manifest.json.
    verify   Re-check every snapshot path, node, the rescue home links, and boot
             the rescue profile. Non-zero exit names the missing piece.
    run      Boot the rescue headless profile FROM THE SNAPSHOT and forward the
             remaining arguments as the task. This is the "main DSH will not
             start" path. Quote the task: run "fix my session logs". Under
             `pwsh -File` a literal "--" separator is eaten by PowerShell's own
             parser and breaks parameter binding; it is only accepted (and
             ignored) when the script is called with & or pwsh -Command.
    repair   Run tools\session-manager.mjs FROM THE SNAPSHOT: scan, then repair
             every damaged session. -DryRun changes nothing on disk.
    status   Print what the snapshot contains and whether it looks usable.

  LAYOUT (see rescue\README.md for why)
  -------------------------------------
    rescue\dsh\dsh\             the @deepseek-ai/dsh CLI package (copy)
    rescue\dsh\node_modules\    the dependency closure, incl. @deepseek-ai\*
    rescue\profile\             the rescue profile (package.json + patch layer)
    rescue\home\                isolated rescue $DSH_HOME
    rescue\home\profiles\rescue junction -> rescue\profile
    rescue\tools\               copies of the session tools the repair path uses
    rescue\manifest.json        what was snapshotted, and how big it is

  SAFETY RULES THIS SCRIPT OBEYS
  ------------------------------
    * Nothing under %DSH_HOME%\profiles is ever written to. The real home is
      only ever READ (settings.yaml / .credentials.yaml) or repaired through
      session-manager.mjs, which only touches session logs.
    * Nothing under the machine's node_global install is ever written to.
    * No port is bound, no DSH process is started or killed: the only processes
      this script starts are short-lived node boot tests and one-shot headless
      tasks it started itself, and it waits for them to exit.
    * node.exe is always an absolute path. PATH is never consulted.
    * Every failure is loud, names the missing piece, and exits non-zero.
#>

[CmdletBinding()]
param(
    # create | verify | run | repair | status
    [Parameter(Position = 0)]
    [string]$Command = '',

    # Zero side effects. For create it prints the plan only; for repair it asks
    # session-manager.mjs to compute without writing.
    [switch]$DryRun,

    # The harness home to READ config from and to REPAIR session logs in.
    # Defaults to $env:DSH_HOME, then %USERPROFILE%\.dsh.
    [string]$DshHome = '',

    # Absolute path of the node binary to use. Defaults to the path this machine
    # is set up with; PATH is never used as a fallback.
    [string]$NodeExe = '',

    # Copy .credentials.yaml into the isolated rescue home so the rescue AGENT can
    # reach a model. OFF by default: repair needs no credentials, and a second
    # copy of the API key is a second place it can leak from.
    [switch]$WithCredentials,

    # Everything else, forwarded to `run` as the task text.
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Rest = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Fixed, absolute facts about this machine. These are the DEFAULTS the manifest
# records; nothing here is resolved through PATH.
# ---------------------------------------------------------------------------
$DefaultNodeExe = 'D:\Program Files\nodejs\node.exe'
$CliSource      = 'D:\Program Files\nodejs\node_global\node_modules\@deepseek-ai\dsh'
$CoreSource     = Join-Path $CliSource 'node_modules'

$ScriptDir     = Split-Path -Parent $PSCommandPath
$Root          = Split-Path -Parent $ScriptDir
$RescueDir     = Join-Path $Root 'rescue'
$RescueDsh     = Join-Path $RescueDir 'dsh'
$SnapCli       = Join-Path $RescueDsh 'dsh'
$SnapModules   = Join-Path $RescueDsh 'node_modules'
$SnapProfile   = Join-Path $RescueDir 'profile'
$SnapHome      = Join-Path $RescueDir 'home'
$SnapTools     = Join-Path $RescueDir 'tools'
$ManifestPath  = Join-Path $RescueDir 'manifest.json'
$ProfileName   = 'rescue'
$BinJs         = Join-Path $SnapCli 'lib\bin.js'

# The two session tools the repair path runs. They are the launcher's own logic,
# so the rescue keeps its own copy and stays independent of the repo state.
$ToolFiles     = @('dsh-session-log.mjs', 'session-manager.mjs')
# Non-secret configuration that lets the rescue AGENT think. READ-ONLY source;
# the copy lives in the isolated rescue home.
#
# .credentials.yaml is DELIBERATELY NOT copied by default.
# ---------------------------------------------------------------------------
# The rescue's primary job is repairing session logs, which needs no model route
# at all: `verify` and `repair` work fully without credentials. Copying the API
# key would add a second byte-identical copy of the secret on disk for a feature
# most rescue runs never use, and the rescue home is a plain directory inside the
# repo. So credentials are opt-in via -WithCredentials, and `run` says exactly
# what to do when they are absent.
$ConfigFiles   = @('settings.yaml')
$SecretFiles   = @('.credentials.yaml')

# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------
function Write-Step([string]$Message) {
    Write-Host "[rescue] $Message" -ForegroundColor Cyan
}

function Write-Info([string]$Message) {
    Write-Host "         $Message"
}

function Fail([string]$Message, [string]$Hint) {
    Write-Host ''
    Write-Host "[rescue] FAILED: $Message" -ForegroundColor Red
    if ($Hint) { Write-Host "[rescue] $Hint" -ForegroundColor Yellow }
    Write-Host ''
    exit 1
}

function Format-Bytes([long]$Bytes) {
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    return ('{0:N0} KB' -f ($Bytes / 1KB))
}

# Returns @{ Files = n; Bytes = m }. Get-ChildItem does not follow reparse
# points, so this counts real files in the tree and nothing through a junction.
function Measure-Tree([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @{ Files = 0; Bytes = [long]0 } }
    $sum = Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum
    if ($null -eq $sum.Sum) { return @{ Files = 0; Bytes = [long]0 } }
    return @{ Files = [int]$sum.Count; Bytes = [long]$sum.Sum }
}

# Same measurement, but skipping the named top-level directories, so a dry run
# reports what would actually be copied (the CLI copy excludes node_modules).
function Measure-TreeExcluding([string]$Path, [string[]]$ExcludeDirs) {
    if (-not $ExcludeDirs -or $ExcludeDirs.Count -eq 0) { return (Measure-Tree $Path) }
    if (-not (Test-Path -LiteralPath $Path)) { return @{ Files = 0; Bytes = [long]0 } }
    $files = 0; $bytes = [long]0
    foreach ($child in Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue) {
        if ($ExcludeDirs -contains $child.Name) { continue }
        if ($child.PSIsContainer) {
            $m = Measure-Tree $child.FullName
            $files += $m.Files; $bytes += $m.Bytes
        } else {
            $files += 1; $bytes += [long]$child.Length
        }
    }
    return @{ Files = $files; Bytes = $bytes }
}

# Resolve the absolute node binary. Fails loud; never falls back to PATH.
function Resolve-Node([string]$Requested) {
    $candidate = if ($Requested) { $Requested } else { $DefaultNodeExe }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        Fail "node.exe not found at the configured absolute path: $candidate" `
             "Pass -NodeExe <absolute path to node.exe>. The rescue never uses PATH."
    }
    return (Resolve-Path -LiteralPath $candidate).ProviderPath
}

function Get-NodeVersion([string]$Node) {
    $v = & $Node --version 2>&1
    if ($LASTEXITCODE -ne 0) { return 'unknown' }
    return ($v | Select-Object -First 1).ToString().Trim()
}

function Get-PackageVersion([string]$PackageJsonPath) {
    if (-not (Test-Path -LiteralPath $PackageJsonPath)) { return $null }
    try {
        $j = Get-Content -LiteralPath $PackageJsonPath -Raw | ConvertFrom-Json
        return [pscustomobject]@{ Name = $j.name; Version = $j.version }
    } catch {
        return $null
    }
}

function Resolve-Robocopy {
    $sys = if ($env:SystemRoot) { $env:SystemRoot } else { 'C:\Windows' }
    $candidate = Join-Path $sys 'System32\robocopy.exe'
    if (Test-Path -LiteralPath $candidate) { return $candidate }
    return $null
}

# Mirror one source tree into the snapshot with robocopy when available;
# otherwise fall back to Copy-Item -Recurse. Returns bytes/files copied.
function Copy-Tree([string]$Source, [string]$Destination, [string[]]$ExcludeDirs) {
    if (-not (Test-Path -LiteralPath $Source)) {
        Fail "snapshot source is missing: $Source" `
             "The machine's DSH install is not where it is expected; the rescue cannot be created from it."
    }
    if ($DryRun) {
        $m = Measure-TreeExcluding $Source $ExcludeDirs
        Write-Info "DRY RUN: would copy $Source -> $Destination ($($m.Files) files, $(Format-Bytes $m.Bytes))"
        return $m
    }

    $robocopy = Resolve-Robocopy
    if ($robocopy) {
        $roboArgs = @($Source, $Destination, '/MIR', '/MT:16', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
        if ($ExcludeDirs) { $roboArgs += '/XD'; $roboArgs += $ExcludeDirs }
        Write-Info "robocopy $Source -> $Destination"
        & $robocopy @roboArgs | Out-Null
        $code = $LASTEXITCODE
        # robocopy: 0-7 are success (bit flags for copied/extra/mismatch), >=8 is a real failure.
        if ($code -ge 8) {
            Fail "robocopy failed with exit code $code while copying $Source" `
                 "Check that the destination is writable and the source is readable."
        }
    } else {
        Write-Info "robocopy unavailable; Copy-Item -Recurse $Source -> $Destination"
        New-Item -ItemType Directory -Force -Path $Destination | Out-Null
        $cargs = @{ LiteralPath = (Join-Path $Source '*'); Destination = $Destination; Recurse = $true; Force = $true }
        Copy-Item @cargs
        if ($ExcludeDirs) {
            foreach ($d in $ExcludeDirs) {
                $stale = Join-Path $Destination $d
                if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Recurse -Force }
            }
        }
    }
    return (Measure-Tree $Destination)
}

# Collect name -> version for every package.json one level down in the core
# layer (including @scope/package). This is the version evidence the manifest
# records for the snapshotted core.
function Get-CoreVersions([string]$ModulesDir) {
    $map = [ordered]@{}
    if (-not (Test-Path -LiteralPath $ModulesDir)) { return $map }
    foreach ($entry in Get-ChildItem -LiteralPath $ModulesDir -Directory -Force -ErrorAction SilentlyContinue) {
        if ($entry.Name -like '@*') {
            foreach ($scoped in Get-ChildItem -LiteralPath $entry.FullName -Directory -Force -ErrorAction SilentlyContinue) {
                $pkg = Get-PackageVersion (Join-Path $scoped.FullName 'package.json')
                if ($pkg -and $pkg.Version) { $map["$($entry.Name)/$($scoped.Name)"] = $pkg.Version }
            }
        } elseif ($entry.Name -ne '.bin') {
            $pkg = Get-PackageVersion (Join-Path $entry.FullName 'package.json')
            if ($pkg -and $pkg.Version) { $map[$entry.Name] = $pkg.Version }
        }
    }
    return $map
}

function Get-RealDshHome {
    if ($DshHome) { return $DshHome }
    if ($env:DSH_HOME) { return $env:DSH_HOME }
    return (Join-Path $env:USERPROFILE '.dsh')
}

# ---------------------------------------------------------------------------
# The rescue home: an isolated $DSH_HOME so the rescue never writes into the
# real one, plus the one junction that lets `--profile <name>` find a profile
# that physically lives at rescue\profile.
#
# `dsh --profile` only accepts a NAME (dsh-app-boot's resolveProfileDir rejects
# any name containing "/" or "\"), and a profile is always looked up at
# <$DSH_HOME>\profiles\<name>. So the profile directory must be reachable under
# the rescue home by name. A directory junction gives that for free, needs no
# privilege, and leaves rescue\profile as the single physical copy.
# ---------------------------------------------------------------------------
function Ensure-RescueHome {
    $profilesDir = Join-Path $SnapHome 'profiles'
    $linkPath = Join-Path $profilesDir $ProfileName
    $target = (Resolve-Path -LiteralPath $SnapProfile).ProviderPath

    New-Item -ItemType Directory -Force -Path $profilesDir | Out-Null

    $item = Get-Item -LiteralPath $linkPath -Force -ErrorAction SilentlyContinue
    if ($item) {
        if ($item.LinkType -eq 'Junction' -and $item.Target -and ($item.Target | Select-Object -First 1) -eq $target) {
            return $linkPath
        }
        Write-Info "replacing stale profile entry $linkPath"
        if ($item.LinkType) {
            # Remove the link itself, never the target.
            [System.IO.Directory]::Delete($linkPath, $false)
        } else {
            Remove-Item -LiteralPath $linkPath -Recurse -Force
        }
    }
    New-Item -ItemType Junction -Path $linkPath -Target $target | Out-Null
    Write-Info "linked $linkPath -> $target"
    return $linkPath
}

function Ensure-RescueConfig {
    $realHome = Get-RealDshHome
    $copied = @()
    $wanted = @($ConfigFiles)
    if ($WithCredentials) { $wanted += $SecretFiles }
    foreach ($name in $wanted) {
        $src = Join-Path $realHome $name
        $dst = Join-Path $SnapHome $name
        if ((Test-Path -LiteralPath $src) -and -not (Test-Path -LiteralPath $dst)) {
            Copy-Item -LiteralPath $src -Destination $dst -Force
            $copied += $name
        }
    }
    if (-not $WithCredentials) {
        $secret = Join-Path $SnapHome '.credentials.yaml'
        if (Test-Path -LiteralPath $secret) {
            # Opt-out is the default, so an earlier -WithCredentials run must not
            # leave the key behind when the operator later decides against it.
            Remove-Item -LiteralPath $secret -Force
            Write-Info 'removed .credentials.yaml from the rescue home (default: no secret copy)'
        }
    }
    return $copied
}

function Read-Manifest {
    if (-not (Test-Path -LiteralPath $ManifestPath)) {
        Fail "no snapshot manifest at $ManifestPath" `
             "Run: pwsh -File tools\rescue-dsh.ps1 create"
    }
    return (Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json)
}

# ---------------------------------------------------------------------------
# create
# ---------------------------------------------------------------------------
function Invoke-Create {
    $node = Resolve-Node $NodeExe
    $realHome = Get-RealDshHome
    Write-Step "create: snapshotting the DSH install into $RescueDsh"
    Write-Info "node:      $node ($(Get-NodeVersion $node))"
    Write-Info "real home: $realHome"
    if ($DryRun) { Write-Info 'DRY RUN: nothing will be written.' }

    if (-not (Test-Path -LiteralPath $CliSource)) {
        Fail "DSH CLI package not found at $CliSource" `
             "Point the script at the install by editing `$CliSource, or pass -DshHome only if the home moved."
    }

    Write-Step "1/5 copying the DSH CLI package (node_modules excluded on purpose)"
    Write-Info "source: $CliSource"
    $cliStats = Copy-Tree -Source $CliSource -Destination $SnapCli -ExcludeDirs @('node_modules')

    Write-Step "2/5 copying the dependency closure (this holds the @deepseek-ai core layer)"
    Write-Info "source: $CoreSource"
    $coreStats = Copy-Tree -Source $CoreSource -Destination $SnapModules -ExcludeDirs @()

    Write-Step "3/5 copying the session tools the repair path runs"
    $toolStats = @{ Files = 0; Bytes = [long]0 }
    if (-not $DryRun) { New-Item -ItemType Directory -Force -Path $SnapTools | Out-Null }
    foreach ($name in $ToolFiles) {
        $src = Join-Path $ScriptDir $name
        if (-not (Test-Path -LiteralPath $src)) {
            Write-Info "WARNING: $src not found; the repair command will not be available in the snapshot"
            continue
        }
        if (-not $DryRun) { Copy-Item -LiteralPath $src -Destination (Join-Path $SnapTools $name) -Force }
    }
    if (-not $DryRun) { $toolStats = Measure-Tree $SnapTools }

    if ($DryRun) {
        $totalFiles = $cliStats.Files + $coreStats.Files
        $totalBytes = $cliStats.Bytes + $coreStats.Bytes
        Write-Step "DRY RUN summary: $totalFiles files, $(Format-Bytes $totalBytes); nothing was written."
        return
    }

    Write-Step "4/5 preparing the isolated rescue home ($SnapHome)"
    $link = Ensure-RescueHome
    # @() around the call: PowerShell unrolls a returned empty array into $null.
    $copied = @(Ensure-RescueConfig)
    if ($copied.Count -gt 0) { Write-Info "copied config into the rescue home: $($copied -join ', ')" }
    else { Write-Info 'rescue home config already present' }

    Write-Step "5/5 writing the manifest"
    $cliPkg = Get-PackageVersion (Join-Path $SnapCli 'package.json')
    $coreVersions = Get-CoreVersions $SnapModules
    $profileRaw = Get-Content -LiteralPath (Join-Path $SnapProfile 'package.json') -Raw | ConvertFrom-Json
    $profileBundles = @($profileRaw.dsh.profile.bundles)

    $manifest = [ordered]@{
        schema      = 1
        createdAt   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        launcher    = $Root
        node        = [ordered]@{ path = $node; version = (Get-NodeVersion $node) }
        realDshHome = $realHome
        entry       = [ordered]@{ node = $node; bin = $BinJs; profile = $ProfileName }
        profile     = [ordered]@{
            dir      = $SnapProfile
            name     = $ProfileName
            manifest = (Join-Path $SnapProfile 'package.json')
            patch    = (Join-Path $SnapProfile 'cordis.patch.yml')
            bundles  = $profileBundles
        }
        home        = [ordered]@{
            path          = $SnapHome
            profilesLink  = $link
            configCopied  = @($copied)
            configSources = @($ConfigFiles | ForEach-Object { Join-Path $realHome $_ })
        }
        trees       = @(
            [ordered]@{
                name         = 'cli'
                snapshotPath = $SnapCli
                sourcePath   = $CliSource
                files        = $cliStats.Files
                bytes        = $cliStats.Bytes
                packageName  = $cliPkg.Name
                version      = $cliPkg.Version
            },
            [ordered]@{
                name          = 'core'
                snapshotPath  = $SnapModules
                sourcePath    = $CoreSource
                sourceMirror  = (Join-Path $realHome 'profiles\node_modules')
                files         = $coreStats.Files
                bytes         = $coreStats.Bytes
                packageCount  = ($coreVersions.Keys).Count
            },
            [ordered]@{
                name         = 'tools'
                snapshotPath = $SnapTools
                sourcePath   = $ScriptDir
                files        = $toolStats.Files
                bytes        = $toolStats.Bytes
            }
        )
        coreVersions = $coreVersions
    }

    ($manifest | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $ManifestPath -Encoding utf8NoBOM

    $totalFiles = $cliStats.Files + $coreStats.Files + $toolStats.Files
    $totalBytes = $cliStats.Bytes + $coreStats.Bytes + $toolStats.Bytes
    Write-Host ''
    Write-Step "create: done"
    Write-Info "cli    : $($cliStats.Files) files, $(Format-Bytes $cliStats.Bytes)  (dsh $($cliPkg.Version))"
    Write-Info "core   : $($coreStats.Files) files, $(Format-Bytes $coreStats.Bytes)  ($(($coreVersions.Keys).Count) packages)"
    Write-Info "tools  : $($toolStats.Files) files, $(Format-Bytes $toolStats.Bytes)"
    Write-Info "TOTAL  : $totalFiles files, $(Format-Bytes $totalBytes)"
    Write-Info "manifest: $ManifestPath"
    if ($totalBytes -gt 2GB) {
        Write-Info 'NOTE: the snapshot exceeds 2 GB; it was mirrored with robocopy /MIR, and the number above is measured, not estimated.'
    }
    Write-Info "next: pwsh -File tools\rescue-dsh.ps1 verify"
}

# ---------------------------------------------------------------------------
# verify
# ---------------------------------------------------------------------------
function Invoke-Verify {
    Write-Step 'verify: checking the snapshot is complete and can boot'
    $node = Resolve-Node $NodeExe
    Write-Info "node: $node ($(Get-NodeVersion $node))"

    $manifest = Read-Manifest
    Write-Info "snapshot created: $($manifest.createdAt)"

    $problems = @()
    foreach ($tree in $manifest.trees) {
        $p = $tree.snapshotPath
        if (-not (Test-Path -LiteralPath $p)) {
            $problems += "snapshot tree '$($tree.name)' is missing: $p"
        } else {
            Write-Info ("tree {0,-6} OK  {1}" -f $tree.name, $p)
        }
    }

    if (-not (Test-Path -LiteralPath $BinJs -PathType Leaf)) {
        $problems += "DSH CLI entry point is missing in the snapshot: $BinJs"
    } else {
        Write-Info "entry    OK  $BinJs"
    }

    foreach ($f in @((Join-Path $SnapProfile 'package.json'), (Join-Path $SnapProfile 'cordis.patch.yml'))) {
        if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { $problems += "rescue profile file is missing: $f" }
    }

    # The bundles the profile names must resolve from the snapshot's own core
    # layer, not from the machine install.
    foreach ($bundle in @('dsh-base', 'dsh-headless')) {
        $dir = Join-Path $SnapModules "@deepseek-ai\$bundle"
        if (-not (Test-Path -LiteralPath (Join-Path $dir 'package.json'))) {
            $problems += "core bundle @deepseek-ai/$bundle is missing from the snapshot: $dir"
        } else {
            Write-Info "bundle   OK  @deepseek-ai/$bundle"
        }
    }

    if ($problems.Count -gt 0) {
        Write-Host ''
        foreach ($p in $problems) { Write-Host "  - $p" -ForegroundColor Red }
        Fail "the snapshot is incomplete ($($problems.Count) problem(s))" 'Re-run: pwsh -File tools\rescue-dsh.ps1 create'
    }

    # Rebuild the rescue home links (cheap, idempotent) so a moved rescue tree
    # still resolves its profile by name.
    $link = Ensure-RescueHome
    $item = Get-Item -LiteralPath $link -Force
    if ($item.LinkType -ne 'Junction') { Fail "rescue home profile entry is not a junction: $link" 'Delete it and re-run create.' }
    Write-Info "home     OK  $link -> $($item.Target | Select-Object -First 1)"

    Write-Step 'verify: boot test (dsh --profile rescue --help)'
    $old = $env:DSH_HOME
    try {
        $env:DSH_HOME = $SnapHome
        $out = & $node $BinJs --profile $ProfileName --help 2>&1
        $code = $LASTEXITCODE
    } finally {
        $env:DSH_HOME = $old
    }
    if ($out) { $out | ForEach-Object { Write-Info $_.ToString() } }
    if ($code -ne 0) {
        Fail "the rescue profile failed to boot from the snapshot (exit $code)" `
             "The snapshot's own core layer cannot compose the headless profile. Re-run create, or inspect the output above."
    }
    Write-Host ''
    Write-Step 'verify: OK -- the snapshot is complete and boots without the machine install.'
}

# ---------------------------------------------------------------------------
# run
# ---------------------------------------------------------------------------
function Invoke-Run([string[]]$TaskArgs) {
    Write-Step 'run: booting the rescue profile from the snapshot'
    $node = Resolve-Node $NodeExe
    if (-not (Test-Path -LiteralPath $BinJs -PathType Leaf)) {
        Fail "the snapshot has no DSH entry point at $BinJs" 'Run: pwsh -File tools\rescue-dsh.ps1 create'
    }
    if ($TaskArgs.Count -eq 0) {
        Fail 'run needs a task' 'Usage: pwsh -File tools\rescue-dsh.ps1 run -- "your task"'
    }

    $link = Ensure-RescueHome
    Write-Info "snapshot : $RescueDsh"
    Write-Info "rescue home: $SnapHome"
    Write-Info "workspace  : $((Get-Location).ProviderPath)"
    Write-Info "task       : $($TaskArgs -join ' ')"
    Write-Host ''

    $old = $env:DSH_HOME
    try {
        $env:DSH_HOME = $SnapHome
        & $node $BinJs --profile $ProfileName @TaskArgs
        $code = $LASTEXITCODE
    } finally {
        $env:DSH_HOME = $old
    }
    Write-Host ''
    Write-Step "run: exit $code"
    exit $code
}

# ---------------------------------------------------------------------------
# repair
# ---------------------------------------------------------------------------
function Invoke-Repair {
    $node = Resolve-Node $NodeExe
    $manager = Join-Path $SnapTools 'session-manager.mjs'
    if (-not (Test-Path -LiteralPath $manager -PathType Leaf)) {
        Fail "the snapshot has no session-manager.mjs at $manager" 'Run: pwsh -File tools\rescue-dsh.ps1 create'
    }
    $realHome = Get-RealDshHome
    Write-Step 'repair: session repair from the snapshot'
    Write-Info "manager : $manager"
    Write-Info "repairing home: $realHome  (the REAL harness home; sessions only)"
    if ($DryRun) { Write-Info 'DRY RUN: no session log will be changed.' }
    Write-Host ''

    $old = $env:DSH_HOME
    try {
        $env:DSH_HOME = $realHome

        Write-Step 'scan'
        $scanJson = & $node $manager scan
        if ($LASTEXITCODE -ne 0) { Fail "session-manager.mjs scan failed (exit $LASTEXITCODE)" 'The scan reports on the real session store; check its output above.' }
        $scan = $scanJson | ConvertFrom-Json
        Write-Info ("scanned  : {0}" -f $scan.totals.scanned)
        Write-Info ("ok       : {0}" -f $scan.totals.ok)
        Write-Info ("damaged  : {0}" -f $scan.totals.damaged)
        Write-Info ("unreadable: {0}" -f $scan.totals.unreadable)
        Write-Info ("missing  : {0}" -f $scan.totals.missing)

        $damagedIds = @($scan.damaged | Where-Object { $_.health -eq 'damaged' } | ForEach-Object { $_.id })
        if ($damagedIds.Count -eq 0) {
            Write-Host ''
            Write-Step 'repair: nothing to repair -- every log has a clean trailer.'
            return
        }
        Write-Info "damaged ids: $($damagedIds.Count) -> $($damagedIds -join ', ')"

        Write-Host ''
        Write-Step 'repair'
        # session-manager.mjs has no `--select damaged` selector (its SELECTORS
        # list is subagent|probe|imported|system|unlisted|archived|empty|all), so
        # the damaged set is derived from `scan` and passed as explicit --id
        # values. Should that selector ever appear, this call starts using it.
        $selectSupported = $false
        try {
            $probe = & $node $manager select --select damaged
            $probeObj = $probe | ConvertFrom-Json
            if ($probeObj.ok -eq $true) { $selectSupported = $true }
        } catch { $selectSupported = $false }

        $repairArgs = @($manager, 'repair')
        if ($selectSupported) {
            Write-Info 'using: repair --select damaged'
            $repairArgs += @('--select', 'damaged')
        } else {
            Write-Info "using: repair --id <$($damagedIds.Count) damaged session(s)>"
            $repairArgs += @('--id') + $damagedIds
        }
        if ($DryRun) { $repairArgs += '--dry-run' }

        $repairJson = & $node @repairArgs
        $repairCode = $LASTEXITCODE
        if ($repairCode -ne 0) { Fail "session-manager.mjs repair failed (exit $repairCode)" 'Nothing was changed unless a repair reported success.' }
        $repair = $repairJson | ConvertFrom-Json
        if ($DryRun) {
            Write-Info "DRY RUN: would repair $($repair.wouldRepair) session(s). Nothing was written."
        } else {
            foreach ($r in $repair.results) {
                $state = if ($r.repaired) { 'repaired' } else { "skipped ($($r.reason))" }
                Write-Info ("{0}: {1}" -f $r.id, $state)
            }
        }
    } finally {
        $env:DSH_HOME = $old
    }
    Write-Host ''
    Write-Step 'repair: done'
}

# ---------------------------------------------------------------------------
# status
# ---------------------------------------------------------------------------
function Invoke-Status {
    Write-Step 'status'
    if (-not (Test-Path -LiteralPath $ManifestPath)) {
        Write-Host ''
        Write-Info "NO SNAPSHOT: $ManifestPath does not exist."
        Write-Info 'The rescue is not usable yet. Run: pwsh -File tools\rescue-dsh.ps1 create'
        exit 2
    }
    $manifest = Read-Manifest
    Write-Info "launcher        : $($manifest.launcher)"
    Write-Info "snapshot created: $($manifest.createdAt)"
    Write-Info "node            : $($manifest.node.path) ($($manifest.node.version))"
    Write-Info "real dsh home   : $($manifest.realDshHome)"
    Write-Info "rescue home     : $($manifest.home.path)"
    Write-Info "profile         : $($manifest.profile.dir) (name: $($manifest.profile.name))"
    Write-Host ''
    Write-Host '  tree                    files          bytes   version/packages'
    Write-Host '  -------------------------------------------------------------------'
    $totalFiles = 0; $totalBytes = [long]0
    foreach ($tree in $manifest.trees) {
        $m = Measure-Tree $tree.snapshotPath
        $totalFiles += $m.Files; $totalBytes += $m.Bytes
        $note = if ($tree.name -eq 'cli') { "dsh $($tree.version)" }
                elseif ($tree.name -eq 'core') { "$($tree.packageCount) packages" }
                else { '' }
        Write-Host ('  {0,-22} {1,7}  {2,14}   {3}' -f $tree.name, $m.Files, (Format-Bytes $m.Bytes), $note)
    }
    Write-Host ('  {0,-22} {1,7}  {2,14}' -f 'TOTAL', $totalFiles, (Format-Bytes $totalBytes))
    Write-Host ''

    $usable = $true
    $reasons = @()
    if (-not (Test-Path -LiteralPath $BinJs -PathType Leaf)) { $usable = $false; $reasons += "no entry point: $BinJs" }
    if (-not (Test-Path -LiteralPath (Join-Path $SnapModules '@deepseek-ai\dsh-base\package.json'))) { $usable = $false; $reasons += 'core layer missing @deepseek-ai/dsh-base' }
    if (-not (Test-Path -LiteralPath (Join-Path $SnapModules '@deepseek-ai\dsh-headless\package.json'))) { $usable = $false; $reasons += 'core layer missing @deepseek-ai/dsh-headless' }
    $link = Join-Path $SnapHome "profiles\$ProfileName"
    $linkItem = Get-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
    if (-not $linkItem -or $linkItem.LinkType -ne 'Junction') { $usable = $false; $reasons += "profile is not reachable by name: $link" }
    try { [void](Resolve-Node $NodeExe) } catch { $usable = $false; $reasons += 'node.exe is not at the configured absolute path' }

    if ($usable) {
        Write-Step 'status: USABLE -- the rescue can boot without the machine install.'
        Write-Info 'Run `verify` for the boot test; run `run "<task>"` to use it.'
    } else {
        Write-Step 'status: NOT USABLE'
        foreach ($r in $reasons) { Write-Info "- $r" }
        Write-Info 'Run: pwsh -File tools\rescue-dsh.ps1 create'
        exit 2
    }
}

# ---------------------------------------------------------------------------
# dispatch
# ---------------------------------------------------------------------------
# `pwsh -File script.ps1 run -- "task"` reaches us with a literal "--" separator
# in Rest; drop it. Dry-run is also detected inside Rest so the flag works in
# any position and under any PowerShell host.
$rest = @($Rest)
if ($rest.Count -gt 0 -and $rest[0] -eq '--') { $rest = @($rest | Select-Object -Skip 1) }
$dryTokens = @('-DryRun', '-dryrun', '--dry-run', '--dryrun')
if (-not $DryRun -and ($rest | Where-Object { $dryTokens -contains $_ })) { $DryRun = $true }

switch ($Command.ToLowerInvariant()) {
    'create' { Invoke-Create }
    'verify' { Invoke-Verify }
    'run'    { Invoke-Run -TaskArgs @($rest | Where-Object { $dryTokens -notcontains $_ }) }
    'repair' { Invoke-Repair }
    'status' { Invoke-Status }
    default {
        Fail "unknown command '$Command'" @'
Usage: pwsh -File tools\rescue-dsh.ps1 <command> [-DryRun] [-DshHome <path>] [-NodeExe <path>]

  create                 snapshot the DSH tree + core layer into rescue\dsh\
  verify                 check the snapshot and boot the rescue profile
  run "<task>"           answer one task from the snapshot
  repair [-DryRun]       scan and repair damaged session logs (real home)
  status                 print snapshot contents and usability

  The task is passed as arguments, so quote it. Under `pwsh -File` a literal
  "--" separator is consumed by PowerShell itself and will fail parameter
  binding -- do not use it. With & or pwsh -Command, "--" is accepted and
  ignored: & .\tools\rescue-dsh.ps1 run -- "task text"
'@
    }
}

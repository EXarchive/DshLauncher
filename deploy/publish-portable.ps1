# publish-portable.ps1
#
# Produce the single-file, self-contained, portable launcher in one command.
#
#   pwsh -File deploy\publish-portable.ps1
#   pwsh -File deploy\publish-portable.ps1 -RebuildPacks   # force feed regeneration
#
# Output: dist-selfcontained\DshLauncher.exe — EXACTLY ONE file, ~134 MB,
# bundling the .NET runtime + WPF + ASP.NET Core + the rescue snapshot. Runs on
# any Windows x64 machine with NO .NET runtime installed.
#
# WHY THIS ISN'T JUST `dotnet publish`
# -----------------------------------
# `-p:SelfContained=true` needs three runtime packs PLUS the apphost pack. None
# of them are on this offline machine and none can be downloaded (nuget.org is
# blocked by the policy proxy). .runtimepacks\build-packs.ps1 synthesizes all
# four from the installed shared frameworks / SDK packs, and this script
# publishes against that local feed.
#
# The apphost pack is easy to overlook but mandatory for PublishSingleFile: it
# carries singlefilehost.exe. Without it, restore dies with
#   NU1101: 找不到包 Microsoft.NETCore.App.Host.win-x64
#
# SAFE SWAP: the publish goes to a staging directory and is only moved into
# dist-selfcontained AFTER it verifiably produced one file. A failed build
# therefore never destroys the previous good executable.

param(
    [switch]$RebuildPacks   # regenerate the offline feed from the installed .NET first
)

$ErrorActionPreference = 'Stop'
$Root       = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Feed       = Join-Path $Root '.runtimepacks\feed'
$PackScript = Join-Path $Root '.runtimepacks\build-packs.ps1'
$VerFile    = Join-Path $Root '.runtimepacks\version.txt'

$packIds = @(
    'Microsoft.NETCore.App.Runtime.win-x64',
    'Microsoft.WindowsDesktop.App.Runtime.win-x64',
    'Microsoft.AspNetCore.App.Runtime.win-x64',
    'Microsoft.NETCore.App.Host.win-x64'
)

function Get-FeedVersion {
    if (Test-Path $VerFile) { return (Get-Content $VerFile -Raw).Trim() }
    return ''
}

# ── 1. make sure the feed is complete for the version we are about to pin ───
$version   = Get-FeedVersion
$needPacks = $RebuildPacks.IsPresent -or [string]::IsNullOrWhiteSpace($version)

if (-not $needPacks) {
    foreach ($id in $packIds) {
        if (-not (Test-Path (Join-Path $Feed "$id.$version.nupkg"))) { $needPacks = $true; break }
    }
}

if ($needPacks) {
    if (-not (Test-Path $PackScript)) { throw "offline feed generator missing: $PackScript" }
    Write-Host '[portable] synthesizing the offline runtime + apphost feed ...'
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $PackScript
    if ($LASTEXITCODE -ne 0) { throw 'offline feed generation failed' }

    $version = Get-FeedVersion
    if ([string]::IsNullOrWhiteSpace($version)) { throw "feed generator did not write $VerFile" }
    foreach ($id in $packIds) {
        if (-not (Test-Path (Join-Path $Feed "$id.$version.nupkg"))) {
            throw "feed incomplete after generation: $id.$version.nupkg is missing"
        }
    }
}

Write-Host "[portable] .NET runtime $version, feed = $Feed"

# Sweep copies parked by earlier swaps. A copy that was still locked at the time
# (a DshLauncher was running) becomes deletable as soon as that instance exits.
Get-ChildItem $env:TEMP -Filter 'DshLauncher.stale.*.exe' -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $_.FullName)) { Write-Host "[portable] swept parked copy $($_.Name)" }
}

# ── 2. clean publish into staging (never touches the current deliverable) ───
$stage = Join-Path $Root 'dist-staging'
$out   = Join-Path $Root 'dist-selfcontained'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

# Build intermediates must go: the pack classification is cached in deps.json, so
# a reclassification (e.g. a file moving native->lib) only takes effect on a
# clean publish. (Learned the hard way.)
Remove-Item (Join-Path $Root 'src\obj'), (Join-Path $Root 'src\bin') -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "[portable] publishing self-contained single-file (staging) ..."
dotnet publish (Join-Path $Root 'src\DshLauncher.csproj') -c Release -o $stage `
    -p:SelfContained=true `
    -p:RuntimeFrameworkVersion=$version `
    -p:RestoreSources=$Feed

if ($LASTEXITCODE -ne 0) {
    throw "publish failed (exit $LASTEXITCODE) - the previous $out was left untouched"
}

# ── 3. verify the staging result before swapping it in ─────────────────────
$stagedExe = Join-Path $stage 'DshLauncher.exe'
if (-not (Test-Path $stagedExe)) { throw "publish produced no DshLauncher.exe in $stage" }

$files = @(Get-ChildItem $stage -File)
if ($files.Count -ne 1) {
    $names = ($files | ForEach-Object { $_.Name }) -join ', '
    throw "expected exactly ONE file, got $($files.Count): $names"
}

$size = (Get-Item $stagedExe).Length

# The icon must be IN the artifact, not merely referenced by the project. The csproj
# already guarantees src\DshLauncher.ico exists (EnsureAppIcon) and ApplicationIcon
# embeds it, but this asserts the end result so a future csproj edit cannot silently
# ship an icon-less exe. Runs under Windows PowerShell because System.Drawing has no
# usable GDI+ binding under pwsh 7.6 on this machine.
$iconIco = Join-Path $Root 'src\DshLauncher.ico'
$iconCheck = Join-Path $Root 'deploy\check-icon.ps1'
if ((Test-Path $iconCheck) -and (Test-Path $iconIco)) {
    $ps51 = Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $ps51 -NoProfile -ExecutionPolicy Bypass -File $iconCheck -Exe $stagedExe -Ico $iconIco
    if ($LASTEXITCODE -ne 0) { throw 'published exe carries no icon - see the message above' }
} else {
    Write-Host '[portable] WARNING: icon check skipped (check-icon.ps1 or DshLauncher.ico missing)'
}

# ── 4. swap into place ─────────────────────────────────────────────────────
# A RUNNING DshLauncher.exe cannot be deleted or overwritten - the loader holds
# it without FILE_SHARE_WRITE - but it CAN be renamed, which is what every
# updater on Windows does. So: park the previous exe in TEMP, then move the new
# one in. The deliverable directory keeps its "exactly one file" invariant even
# while the launcher is up, and the running instance is not disturbed (its image
# is already mapped; only the on-disk name changes).
New-Item -ItemType Directory -Force -Path $out | Out-Null
$liveExe = Join-Path $out 'DshLauncher.exe'

if (Test-Path $liveExe) {
    try {
        # 首选：直接删掉旧 exe，不留任何备份。
        Remove-Item $liveExe -Force -ErrorAction Stop
        Write-Host '[portable] 旧 exe 已删除'
    } catch {
        # 删除失败 = 该 exe 正在运行。运行中的文件无法删除，只能改名（改名只动目录项，
        # 不碰正在映射的文件），改名后目标路径就空出来了。
        $parked = Join-Path $env:TEMP ("DshLauncher.stale.{0}.exe" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
        try {
            Move-Item $liveExe $parked -Force -ErrorAction Stop
            Write-Host "[portable] 旧 exe 正在运行无法删除，已改名到 $parked（进程退出后清理）"
            Remove-Item $parked -Force -ErrorAction SilentlyContinue
        } catch {
            throw "cannot replace ${liveExe}: $($_.Exception.Message)`nClose the running DshLauncher, then re-run."
        }
    }
}

Move-Item $stagedExe $liveExe -Force
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

$final = @(Get-ChildItem $out -File)
if ($final.Count -ne 1) {
    $names = ($final | ForEach-Object { $_.Name }) -join ', '
    throw "deliverable directory should hold exactly one file, found $($final.Count): $names"
}

Write-Host "[portable] $liveExe"
Write-Host ("[portable] {0:N1} MB, 1 file — self-contained, portable, .NET {1}" -f ((Get-Item $liveExe).Length / 1MB), $version)

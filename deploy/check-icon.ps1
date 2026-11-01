# check-icon.ps1 - assert that a built exe actually carries OUR icon.
#
# Deliberately ASCII-only: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI,
# so any non-ASCII text in this file would corrupt the script.
#
# Why not [Icon]::ExtractAssociatedIcon? It returns the generic application icon
# when the file has none, so "non-null" proves nothing. Instead we pull a byte
# slice out of the 256px entry of src\DshLauncher.ico and look for that exact
# slice inside the exe - the PE stores icon images verbatim, so this is precise.
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Ico
)

if (-not (Test-Path $Exe)) { Write-Error "exe not found: $Exe"; exit 2 }
if (-not (Test-Path $Ico)) { Write-Error "ico not found: $Ico"; exit 2 }

$icoBytes = [System.IO.File]::ReadAllBytes($Ico)

# ICO header: reserved(2) type(2) count(2); then 16-byte entries.
$count = [BitConverter]::ToUInt16($icoBytes, 4)
if ($count -lt 1) { Write-Error "ico has no entries: $Ico"; exit 2 }

# Pick the entry with the largest payload (our 256px PNG).
$bestOff = 0; $bestLen = 0
for ($i = 0; $i -lt $count; $i++) {
    $o = 6 + 16 * $i
    $len = [BitConverter]::ToUInt32($icoBytes, $o + 8)
    $off = [BitConverter]::ToUInt32($icoBytes, $o + 12)
    if ($len -gt $bestLen) { $bestLen = $len; $bestOff = $off }
}

# Take 48 bytes of that entry as a fingerprint (PNG signature + IHDR start).
$fpLen = [Math]::Min(48, [int]$bestLen)
$fp = New-Object byte[] $fpLen
[Array]::Copy($icoBytes, $bestOff, $fp, 0, $fpLen)

# PNG entries start with the 8-byte PNG signature; verify we picked one of those.
$isPng = ($fp[0] -eq 0x89 -and $fp[1] -eq 0x50 -and $fp[2] -eq 0x4E -and $fp[3] -eq 0x47)
if (-not $isPng) { Write-Error "largest ico entry is not a PNG; cannot fingerprint"; exit 2 }

# Search the exe for the fingerprint.
$exeBytes = [System.IO.File]::ReadAllBytes($Exe)
$found = -1
$limit = $exeBytes.Length - $fpLen
for ($i = 0; $i -le $limit; $i++) {
    if ($exeBytes[$i] -ne $fp[0]) { continue }
    $ok = $true
    for ($j = 1; $j -lt $fpLen; $j++) {
        if ($exeBytes[$i + $j] -ne $fp[$j]) { $ok = $false; break }
    }
    if ($ok) { $found = $i; break }
}

if ($found -lt 0) {
    Write-Error "exe does NOT contain the 256px icon image from $Ico"
    exit 1
}

Write-Output ("[icon] OK - icon image found in exe at offset {0} ({1} entries in ico)" -f $found, $count)
exit 0

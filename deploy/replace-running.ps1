# replace-running.ps1 — 把新构建换到目标位置：能删就直接删，只有正在运行删不掉时才改名。
#
# 规则：
#   1. 目标 exe 没在运行 → 直接删除，再把新文件复制过去（不留任何备份）。
#   2. 目标 exe 正在运行 → Windows 不允许删除运行中的文件，只能改名（改名只动目录项、
#      不碰正在映射的文件，运行中的进程不受影响），然后把新文件复制到原路径。
#      改出来的 .old 会在下一次替换时优先清理。
#
# 用法（在仓库根目录）：
#   pwsh -File deploy\replace-running.ps1 -Source .\dist-selfcontained\DshLauncher.exe -Target "C:\Users\Administrator\Desktop\DshLauncher.exe"
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Target
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Source)) { throw "源文件不存在: $Source" }

$name = Split-Path $Target -Leaf
$old = "$Target.old"

$running = Get-CimInstance Win32_Process -Filter "Name='$name'" |
    Where-Object { $_.ExecutablePath -eq $Target } |
    Select-Object -First 1

if (Test-Path $Target) {
    if ($running) {
        # 运行中：删不掉，只能改名。
        # 目标名必须是唯一的：上一轮的 .old 可能仍被那个运行中的进程占用，
        # 这时既删不掉、也不能 Move-Item -Force 覆盖（会报"文件已存在"）。
        # 所以先试固定 .old；删不掉就换成带时间戳的唯一名字，绝不因此中断替换。
        if (Test-Path $old) {
            try {
                Remove-Item $old -Force -ErrorAction Stop
            } catch {
                $old = "$Target.old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
                Write-Host "[replace] 上一份 .old 仍被占用，改用唯一名字: $old"
            }
        }
        Write-Host "[replace] 目标正在运行 (PID $($running.ProcessId))，无法删除，改用改名绕过文件锁…"
        Move-Item $Target $old -Force
        Write-Host "[replace] 运行中的旧文件已改名 → $old（进程退出后清理）"
    } else {
        # 没在运行：直接删除，不留备份。
        Remove-Item $Target -Force
        Write-Host "[replace] 旧文件已直接删除 → $Target"
    }
}

Copy-Item $Source $Target -Force
Write-Host "[replace] 完成 → $Target"
if ($running) {
    Write-Host "[replace] 下一步：关掉当前运行的旧版，双击 $(Split-Path $Target -Leaf) 即用新版"
} else {
    Write-Host "[replace] 直接双击 $(Split-Path $Target -Leaf) 即用新版"
}

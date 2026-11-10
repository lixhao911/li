# =====================================================================
#  项目备份脚本
#
#  用途：每次修改代码/文档前，把整个项目复制一份带时间戳的快照，
#        出问题可整体回滚。
#
#  用法（在项目根目录）：
#     powershell -NoProfile -ExecutionPolicy Bypass -File .\backup.ps1
#     powershell -NoProfile -ExecutionPolicy Bypass -File .\backup.ps1 -Note "改界面前"
#     powershell -NoProfile -ExecutionPolicy Bypass -File .\backup.ps1 -Keep 30
#
#  备份位置：<项目根>\.backup\yyyyMMdd_HHmmss[_备注]\
#  排除目录：bin、obj、.backup、.vs、.git（生成物/版本库不占空间）
#  清理策略：默认只保留最近 20 份，更早的自动删除
# =====================================================================

param(
    [int]$Keep = 20,          # 保留最近多少份备份
    [string]$Note = ""        # 可选备注，会写进备份目录名和 manifest
)

$ErrorActionPreference = "Stop"

$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$backupRoot = Join-Path $root ".backup"

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$suffix = if ([string]::IsNullOrWhiteSpace($Note)) { "" } else { "_" + ($Note -replace '[\\/:*?"<>|\s]', '-') }
$dest = Join-Path $backupRoot ($stamp + $suffix)

New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

# robocopy：/E 连空目录一起复制；/XD 排除目录；/NFL /NDL /NJH /NJS 精简输出
$exclude = @("bin", "obj", ".backup", ".vs", ".git")
$rcArgs = @($root, $dest, "/E", "/XD") + $exclude + @("/NFL", "/NDL", "/NJH", "/NJS", "/R:1", "/W:1")

& robocopy @rcArgs | Out-Null
$code = $LASTEXITCODE
if ($code -ge 8) {
    Write-Host "[失败] robocopy 返回 $code，备份未完成。" -ForegroundColor Red
    exit 1
}

# 写一份 manifest，方便日后查找
$files = Get-ChildItem -Path $dest -Recurse -File
$totalKb = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1KB), 1)
$manifest = @(
    ("备份时间: " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss")),
    ("备注: " + $Note),
    ("源目录: " + $root),
    ("文件数: " + $files.Count),
    ("总大小: " + $totalKb + " KB")
) -join "`r`n"
[System.IO.File]::WriteAllText((Join-Path $dest "MANIFEST.txt"), $manifest,
    (New-Object System.Text.UTF8Encoding($true)))

# 只保留最近 $Keep 份
$old = Get-ChildItem -Path $backupRoot -Directory |
       Sort-Object Name -Descending |
       Select-Object -Skip $Keep
foreach ($d in $old) {
    Remove-Item -Path $d.FullName -Recurse -Force
}

Write-Host "[完成] 已备份到 $dest" -ForegroundColor Green
Write-Host ("       文件 {0} 个，{1} KB；当前保留 {2} 份。" -f
    $files.Count,
    [math]::Round((($files | Measure-Object Length -Sum).Sum / 1KB), 1),
    (Get-ChildItem -Path $backupRoot -Directory).Count)

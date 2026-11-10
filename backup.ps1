# =====================================================================
#  项目备份脚本（zip 压缩包版）
#
#  用途：每次修改代码/文档前，把整个项目打包成 zip 快照，出问题可整体回滚。
#
#  用法（在项目根目录）：
#     powershell -NoProfile -ExecutionPolicy Bypass -File .\backup.ps1
#     powershell -NoProfile -ExecutionPolicy Bypass -File .\backup.ps1 -Note "改界面前"
#     powershell -NoProfile -ExecutionPolicy Bypass -File .\backup.ps1 -Keep 30
#
#  输出：<项目根>\.backup\yyyyMMdd_HHmmss[_备注].zip
#  内容：全部源码与文档，排除 bin、obj、.backup、.vs、.git
#  清理：默认只保留最近 20 个 zip，更早的自动删除
#
#  还原：把 zip 解压后覆盖回项目根目录即可。
# =====================================================================

param(
    [int]$Keep = 20,          # 保留最近多少个 zip
    [string]$Note = ""        # 可选备注，会写进压缩包名和 MANIFEST.txt
)

$ErrorActionPreference = "Stop"

$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$backupRoot = Join-Path $root ".backup"

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$suffix = if ([string]::IsNullOrWhiteSpace($Note)) { "" } else { "_" + ($Note -replace '[\\/:*?"<>|\s]', '-') }
$zipPath = Join-Path $backupRoot ($stamp + $suffix + ".zip")
$staging = Join-Path $env:TEMP ("3dprint_backup_" + $stamp)

New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

try {
    # 1) 先复制到临时目录（robocopy 负责按目录排除）
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    $exclude = @("bin", "obj", ".backup", ".vs", ".git")
    $rcArgs = @($root, $staging, "/E", "/XD") + $exclude + @("/NFL", "/NDL", "/NJH", "/NJS", "/R:1", "/W:1")

    & robocopy @rcArgs | Out-Null
    $code = $LASTEXITCODE
    if ($code -ge 8) {
        Write-Host "[失败] robocopy 返回 $code，备份未完成。" -ForegroundColor Red
        exit 1
    }

    # 2) 写 manifest，方便日后查找
    $files = Get-ChildItem -Path $staging -Recurse -File
    $totalKb = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1KB), 1)
    $manifest = @(
        ("备份时间: " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss")),
        ("备注: " + $Note),
        ("源目录: " + $root),
        ("文件数: " + $files.Count),
        ("总大小: " + $totalKb + " KB")
    ) -join "`r`n"
    [System.IO.File]::WriteAllText((Join-Path $staging "MANIFEST.txt"), $manifest,
        (New-Object System.Text.UTF8Encoding($true)))

    # 3) 打包成 zip（用 ZipFile 而非 Compress-Archive：更快、中文名不乱码）
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $staging, $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)   # false = 不把 staging 目录名当成顶层文件夹
}
finally {
    # 4) 无论成功失败都清理临时目录
    if (Test-Path $staging) { Remove-Item -Path $staging -Recurse -Force -ErrorAction SilentlyContinue }
}

# 5) 只保留最近 $Keep 个 zip
$old = Get-ChildItem -Path $backupRoot -Filter "*.zip" -File |
       Sort-Object LastWriteTime -Descending |
       Select-Object -Skip $Keep
foreach ($f in $old) {
    Remove-Item -Path $f.FullName -Force
}

$zipKb = [math]::Round(((Get-Item $zipPath).Length / 1KB), 1)
Write-Host "[完成] 已打包 $zipPath" -ForegroundColor Green
Write-Host ("       压缩包 {0} KB（源 {1} 个文件，{2} KB）；当前保留 {3} 个 zip。" -f
    $zipKb,
    $files.Count,
    $totalKb,
    (Get-ChildItem -Path $backupRoot -Filter "*.zip" -File).Count)

<# 创建 Worker 仓库副本(AGENTS.md「派活规则」的 robocopy 命令封装:排除 bin/obj/.git/.vs/artifacts,重试 1 次、等待 1 秒,不会因 .vs 里 IDE 锁文件挂起)。目标已存在且非空时拒绝(上个任务的残留被静默合并后,会随「拷回主仓」把旧改动一并带回;确要复用加 -Force)。用法: scripts/new-copy.ps1 D:/FORGE/workN [-Force] #>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Destination,
    [switch]$Force
)

$src = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dst = [System.IO.Path]::GetFullPath($Destination)
if (-not $Force -and (Test-Path -LiteralPath $dst)) {
    $existing = @(Get-ChildItem -LiteralPath $dst -Force -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        Write-Host "[new-copy] 目标已存在且非空: $dst —— 残留会被静默合并并随拷回带旧改动;先删除它或换新的 workN 目录,确要合并复用加 -Force。" -ForegroundColor Red
        exit 3
    }
}
robocopy $src $dst /E /XD bin obj .git .vs artifacts /R:1 /W:1 /NFL /NDL /NJH /NJS
if ($LASTEXITCODE -ge 8) {
    Write-Host "[new-copy] robocopy 失败,退出码 $LASTEXITCODE,副本不可用,报告阻塞。" -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "[new-copy] 副本就绪: $dst(robocopy 码 $LASTEXITCODE,<8 即成功)"
exit 0

<#
生成 updates.json(schema 1,契约冻结于 src/Forge.App/SPEC.md「自动更新」)。
逐个产物计算 SHA-256,按 RID 登记 url / sha256 / sizeBytes,供 Forge.App 的自动更新检查与「安装并重启」使用。

本地/发布流程调用:
  pwsh scripts/make-update-manifest.ps1 -ArtifactsDir dist -Version 1.2.0 -Channel stable -Output updates.json
发布时传入 -BaseUrl(真实 release 资产根)、-Notes(发布说明)与 -MergeFrom(上一份 updates.json)。
契约与 App 侧消费口径以 src/Forge.App/SPEC.md「自动更新」为唯一权威;按 RID 打包与上传的 workflow 尚未接入(见该 SPEC 未决),所以目前只有手工发版会跑这个脚本。

产物命名:Forge-<version>-<rid>.zip(Windows)或 Forge-<version>-<rid>.tar.gz(Linux/macOS)。
找不到任何匹配产物即失败,不静默产出空 manifest。

渠道合并(-MergeFrom):一份 manifest 必须能回答每个渠道。beta 发布若只写 beta 键,
stable 用户的默认地址(滚动 manifest tag 上的 updates.json)就查不到自己的渠道。故发布时先取上一份 manifest,
把它的渠道原样带入,再用本次发布的渠道覆盖自己那一键 —— stable/beta 两键共存。
-MergeFrom 指定的文件不存在或无法解析即失败(调用方负责在"上一份不存在"时不传该参数),
不让一次断掉的合并悄悄产出一份缺渠道的 manifest。

同一 RID 出现两个产物(如同时有 .zip 与 .tar.gz)即失败:后者会覆盖前者,而 manifest 无法表达歧义。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArtifactsDir,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidateSet('stable', 'beta')][string]$Channel,
    [string]$BaseUrl = '',
    [string]$Notes = '',
    [string]$ReleasedAt = '',
    [string]$MergeFrom = '',
    [string]$Output = 'updates.json'
)

$ErrorActionPreference = 'Stop'
$artifacts = (Resolve-Path -LiteralPath $ArtifactsDir).Path

if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    $BaseUrl = "https://github.com/zfcflower/Forge/releases/download/v$Version"
}

if ([string]::IsNullOrWhiteSpace($ReleasedAt)) {
    $ReleasedAt = [DateTimeOffset]::UtcNow.ToString('o')
}

$prefix = "Forge-$Version-"
$files = @(Get-ChildItem -LiteralPath $artifacts -File | Where-Object { $_.Name.StartsWith($prefix, [System.StringComparison]::Ordinal) })
if ($files.Count -eq 0) {
    throw "no packages matching '$prefix*' under $artifacts"
}

$packages = [ordered]@{}
$sourceOf = @{}
foreach ($file in ($files | Sort-Object Name)) {
    $rest = $file.Name.Substring($prefix.Length)
    $rid = $null
    if ($rest.EndsWith('.tar.gz', [System.StringComparison]::Ordinal)) {
        $rid = $rest.Substring(0, $rest.Length - '.tar.gz'.Length)
    }
    elseif ($rest.EndsWith('.zip', [System.StringComparison]::Ordinal)) {
        $rid = $rest.Substring(0, $rest.Length - '.zip'.Length)
    }

    if ([string]::IsNullOrWhiteSpace($rid)) {
        continue
    }

    if ($packages.Contains($rid)) {
        throw "two packages map to RID '$rid': '$($sourceOf[$rid])' and '$($file.Name)'"
    }

    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $sourceOf[$rid] = $file.Name
    $packages[$rid] = [ordered]@{
        url       = "$BaseUrl/$($file.Name)"
        sha256    = $hash
        sizeBytes = [long]$file.Length
    }
}

if ($packages.Count -eq 0) {
    throw "no packages with a recognized RID suffix (.zip / .tar.gz) under $artifacts"
}

# 上一份 manifest 的渠道先原样带入,再由本次发布覆盖自己那一键。
$channels = [ordered]@{}
if (-not [string]::IsNullOrWhiteSpace($MergeFrom)) {
    $merged = Resolve-Path -LiteralPath $MergeFrom -ErrorAction SilentlyContinue
    if (-not $merged) {
        throw "the manifest to merge from does not exist: $MergeFrom"
    }

    $previous = Get-Content -LiteralPath $merged.Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $previous -or $null -eq $previous.channels) {
        throw "the manifest to merge from has no channels object: $MergeFrom"
    }

    foreach ($property in $previous.channels.PSObject.Properties) {
        $channels[$property.Name] = $property.Value
    }
}

$channels[$Channel] = [ordered]@{
    version    = $Version
    releasedAt = $ReleasedAt
    notes      = $Notes
    packages   = $packages
}

$manifest = [ordered]@{
    schema   = 1
    channels = $channels
}

$json = $manifest | ConvertTo-Json -Depth 12
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$target = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path (Get-Location) $Output }
[System.IO.File]::WriteAllText($target, $json, $utf8NoBom)
Write-Host "wrote $target ($($packages.Count) package(s), channel '$Channel', version '$Version', channels: $($channels.Keys -join ','))"

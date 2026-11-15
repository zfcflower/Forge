<#
Forge 测试执行入口 —— 测试范围治理的唯一执行面。
规则来源:testing.md(id forge-testing)与 AGENTS.md「测试分层与执行」。
主仓(有 .git):scripts/forge-test.ps1 -Auto   按 git 改动推导落点过滤器(只跑与改动
  严格相关的用例),改了 .cs/.axaml 自动追加源码扫描门槛,失败先重建一次再判红
  (治旧 bin 假红),收据写 artifacts/testruns/ 并打印,须粘贴进完工报告。
副本(无 .git):按派活文本给的过滤器显式执行,例如
  scripts/forge-test.ps1 -Level L1 -Project Forge.App.Tests -Ns Workbench [-Gates]
L3 全量必须显式 --why merge|release|interface|infra|cross-module,缺省直接拒绝。

细粒度选择器(自己知道要跑哪几条时用它;给了选择器就不用 -Ns,也不必给 -Level——过滤器运行按 L1 算):
  指定测试类    -Project Forge.Core.Tests -Class WorkflowEngineTests
  指定测试方法  -Project Forge.Core.Tests -Method Forge.Core.Tests.WorkflowEngineTests.ExecuteAsync
  指定 Trait    -Project Forge.Core.Tests -Trait Category=Unit
  多个值用逗号(OR 关系);-Class/-Method/-Trait 可混用,拼成一条 --filter。可加 -Gates 附源码扫描门槛
  (目标项目不是 Forge.App.Tests 时门槛另开一条运行)。选择器与 -Auto、-Ns、-Level L2/L3 互斥
  (它们都是"全量/另一个范围"的声明);过滤器 0 命中判红,不判绿。

并行:
  -Parallel                多个测试项目并发跑(每个项目一个 dotnet test 进程)
  -Parallelism <1-16>      并发进程数上限;缺省 = min(运行数, CPU/2 向下取整)
  -MaxParallelThreads <n>  单个项目内 xunit 并行线程上限(临时 .runsettings,经 --settings 传入)
  首跑失败的项目回到串行路径再跑一次(仍失败才 Rebuild 重跑),并行不会掩掉“旧 bin 假红”。
#>
[CmdletBinding()]
param(
    [switch]$Auto,
    [ValidateSet('L1', 'L2', 'L3')][string]$Level,
    [string]$Project,
    [string]$Ns,
    [switch]$Gates,
    [ValidateSet('merge', 'release', 'interface', 'infra', 'cross-module')][string]$Why,
    [switch]$SkipBuild,
    [switch]$ListOnly,

    # ---- 细粒度选择器(收窄到具体用例;给了它们就不必再给 -Ns) ----
    # -Class:类名或其一段(FullyQualifiedName~),多个用逗号,OR 关系
    [string[]]$Class,
    # -Method:完全限定方法名(FullyQualifiedName=),大小写与命名空间必须精确,多个用逗号,OR 关系
    [string[]]$Method,
    # -Trait:VSTest trait 值,写成 Name=Value(如 Category=Unit),多个用逗号,OR 关系;xunit 的 [Trait("Category","Unit")] 即为 Category=Unit
    [string[]]$Trait,

    # ---- 并行 ----
    # -Parallel:把本次选中的多个测试项目并发跑(每个项目一个 dotnet test 进程)
    [switch]$Parallel,
    # -Parallelism:并发进程数上限,默认取"运行数"与"CPU 数/2 向下取整"的较小者(至少 1)
    [ValidateRange(1, 16)][int]$Parallelism = 0,
    # -MaxParallelThreads:单个项目内 xunit 的并行线程数上限;0=不动(xunit 默认按 CPU);写临时 .runsettings 经 --settings 传入
    [ValidateRange(0, 64)][int]$MaxParallelThreads = 0
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }  # dotnet 输出是 UTF-8;zh-CN 控制台默认 GBK,不设会乱码且解析不到摘要
$GatesFilter = 'FullyQualifiedName~Localization|FullyQualifiedName~MinorRegressionTests|FullyQualifiedName~KeyRegistry|FullyQualifiedName~StringTable|FullyQualifiedName~NoHardcodedChinese'

function Write-Fail([string]$Message) {
    Write-Host "[forge-test] $Message" -ForegroundColor Red
    exit 2
}

if ($env:FORGE_RUN -eq '1') {
    Write-Fail "本 shell 设了 FORGE_RUN=1(测试 shell 不得设,否则测试项目会引用 artifacts/run 的 Forge.App)。Remove-Item Env:FORGE_RUN 后重跑。"
}

if ($Auto -and $Level) { Write-Fail "-Auto 与 -Level 二选一:-Auto 自己推导落点。" }

# ---- 细粒度选择器:给了 -Class/-Method/-Trait 就必须指定一个 -Project ----
$Selectors = @()
foreach ($c in @($Class)) {
    if ([string]::IsNullOrWhiteSpace($c)) { continue }
    foreach ($part in ($c -split ',')) {
        $v = $part.Trim()
        if ($v) { $Selectors += "FullyQualifiedName~$v" }
    }
}
foreach ($m in @($Method)) {
    if ([string]::IsNullOrWhiteSpace($m)) { continue }
    foreach ($part in ($m -split ',')) {
        $v = $part.Trim()
        # 精确匹配:用户写全名就原样,写 "." 开头的相对名也要求全名,不做模糊猜测
        if ($v) { $Selectors += "FullyQualifiedName=$v" }
    }
}
foreach ($t in @($Trait)) {
    if ([string]::IsNullOrWhiteSpace($t)) { continue }
    foreach ($part in ($t -split ',')) {
        $v = $part.Trim()
        if (-not $v) { continue }
        if ($v -notmatch '=') { Write-Fail "-Trait 需要写成 Name=Value,例如 -Trait Category=Unit;收到 '$v'" }
        $Selectors += $v
    }
}
$HasSelectors = $Selectors.Count -gt 0

# 给了选择器但解析为空(`-Class " "` / `-Class ,`)是错误输入,不能静默落回 -Auto
if (-not $HasSelectors -and ([bool]$Class -or [bool]$Method -or [bool]$Trait)) {
    Write-Fail "-Class/-Method/-Trait 解析后为空:给了参数但没有可用的值。"
}

if ($HasSelectors) {
    if ($Auto) {
        # 选择器是“我自己知道要跑哪几条”的声明,和落点推导冲突(推导会追加别的项目/门槛)
        Write-Fail "-Class/-Method/-Trait 与 -Auto 不能合用:选择器本身就是范围声明。去掉 -Auto(选择器默认按 L1 跑)。"
    }
    if (-not $Project) {
        Write-Fail "-Class/-Method/-Trait 需要 -Project <测试项目> 指明在哪个项目里跑,例如 -Project Forge.Core.Tests -Class WorkflowEngineTests。"
    }
    if ($Level -in @('L2', 'L3')) {
        # 选择器跑的是子集:L2 的合约定是项目全量,L3 是全仓全量,两者与“只要这几条”矛盾
        Write-Fail "-Class/-Method/-Trait 与 -Level $Level 互斥:L2 是项目全量、L3 是全仓全量,而选择器只要子集。去掉 -Level(选择器按 L1 跑)或去掉选择器。"
    }

    # 过滤运行就是 L1(AGENTS.md §4.3 的定义);不给 -Level 时默认到这里,而不是落到 -Auto 推导
    $Level = 'L1'
}

if (-not $Auto -and -not $Level) { $Auto = $true }

# ---- 落点映射(scripts/test-map.json,最长前缀匹配;改表须与 testing.md 落点表同步) ----
# Prefix=小写匹配键、Rule=原始大小写(收据/推导行展示用,保证可 grep 匹配真实路径);l1 的 ns 支持字符串或数组
$Map = Get-Content -Raw (Join-Path $RepoRoot 'scripts/test-map.json') | ConvertFrom-Json
$L3Rules = @($Map.l3 | ForEach-Object { @{ Prefix = ([string]$_.prefix).ToLowerInvariant(); Rule = [string]$_.prefix; Why = [string]$_.why } })
$L1Rules = @($Map.l1 | ForEach-Object { @{ Prefix = ([string]$_.prefix).ToLowerInvariant(); Rule = [string]$_.prefix; Ns = @($_.ns); GatesOnly = [bool]$_.gatesOnly } } | Sort-Object { $_.Prefix.Length } -Descending)
$L2Rules = @($Map.l2 | ForEach-Object { @{ Prefix = ([string]$_.prefix).ToLowerInvariant(); Rule = [string]$_.prefix; Project = [string]$_.project } })

# 映射表自检:前缀失配(写错/模块改名)会让规则永不命中、静默降级假绿——l3 失配直接拒绝执行,l1/l2 只警告
function Test-MapPrefix([string]$Prefix) {
    $p = $Prefix.TrimEnd('/')
    $full = Join-Path $RepoRoot ($p -replace '/', '\')
    if ($Prefix.EndsWith('/')) { return (Test-Path -LiteralPath $full) }
    if (Test-Path -LiteralPath $full) { return $true }
    return (Test-Path -LiteralPath (Split-Path $full -Parent))
}
foreach ($r in $L3Rules) {
    if (-not (Test-MapPrefix $r.Rule)) { Write-Fail "scripts/test-map.json 的 l3 前缀 '$($r.Rule)' 在仓库中不存在(写错或模块已改名);为防静默失配假绿,拒绝执行——先修表再跑。" }
}
$badMap = @((@($L1Rules) + @($L2Rules)) | Where-Object { -not (Test-MapPrefix $_.Rule) })
if ($badMap.Count -gt 0) {
    Write-Host "[forge-test] 警告:落点表前缀失配(仍继续,但这些规则永不命中):$(($badMap | ForEach-Object { $_.Rule }) -join ', ')" -ForegroundColor Yellow
}

function Get-ChangedFiles {
    if (-not (Test-Path (Join-Path $RepoRoot '.git'))) { return $null }
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $source = 'worktree'
    try {
        $tracked = @(git -C $RepoRoot diff --name-only HEAD 2>$null)
        $untracked = @(git -C $RepoRoot ls-files --others --exclude-standard 2>$null)
        $all = @($tracked) + @($untracked)
        $all = @($all | Where-Object { $_ })
        if ($all.Count -eq 0) {
            # 工作区干净:改用最近一次提交的范围(已提交的工作也该有落点可测)。
            # 单提交仓 HEAD~1 不存在,git stderr 在 EAP=Continue + 2>$null 下被吞掉,all 为空走下方 Write-Fail。
            $all = @(git -C $RepoRoot diff --name-only HEAD~1 HEAD 2>$null)
            $source = 'HEAD~1'
            if ($all.Count -gt 0) { Write-Host "[forge-test] 工作区无未提交改动,改用最近一次提交的范围。" -ForegroundColor Yellow }
        }
    }
    finally { $ErrorActionPreference = $prev }
    return , @{ Files = $all; Source = $source }
}

function Get-Classification([string]$File) {
    $f = ($File -replace '\\', '/').ToLowerInvariant()   # 匹配用小写副本
    $fOrig = ($File -replace '\\', '/')                    # 取值保留原大小写(项目名/命名空间)
    # 文档/spec 一律无测试落点(testing.md「文档、spec、.md -> 不跑测试"),先于任何目录前缀判,否则 src/*/SPEC.md 会被当产品改动触发全量
    if ($f -match '\.(md|txt|svg|png|jpe?g|gif|ico|webp|license)$') { return @{ Kind = 'doc'; Rule = '' } }
    foreach ($r in $L3Rules) { if ($f.StartsWith($r.Prefix)) { return @{ Kind = 'L3'; Why = $r.Why; Rule = $r.Rule } } }
    foreach ($r in $L1Rules) { if ($f.StartsWith($r.Prefix)) { return @{ Kind = 'L1'; Ns = @($r.Ns); GatesOnly = $r.GatesOnly; Rule = $r.Rule } } }
    foreach ($r in $L2Rules) { if ($f.StartsWith($r.Prefix)) { return @{ Kind = 'L2'; Project = $r.Project; Rule = $r.Rule } } }
    if ($f.StartsWith('tests/')) {
        $parts = $fOrig.Split('/')
        if ($parts.Count -ge 4) { return @{ Kind = 'L1'; Ns = ($parts[1] + '.' + $parts[2]); Rule = 'tests/<项目>/<命名空间>/' } }
        if ($parts.Count -ge 3) { return @{ Kind = 'L2'; Project = $parts[1]; Rule = 'tests/<项目>/' } }
    }
    return @{ Kind = 'none'; Rule = '' }
}

# ---- 1. 定界 ----
$Runs = New-Object System.Collections.Generic.List[object]
$Derivation = New-Object System.Collections.Generic.List[string]
$PlanLevel = $Level
$PlanWhy = $Why
$GatesNeeded = $false
$ChangedCount = $null
$Files = $null

if ($Auto) {
    $chg = Get-ChangedFiles
    if ($null -eq $chg) {
        Write-Fail "未发现 .git(这是副本):不要用 -Auto。按派活文本给的过滤器显式执行,例如 scripts/forge-test.ps1 -Level L1 -Project Forge.App.Tests -Ns Workbench"
    }
    if ($chg.Source -eq 'HEAD~1') {
        $Derivation.Add("工作区无未提交改动,范围=最近一次提交(HEAD~1..HEAD);更早的提交不在本收据内")
    }
    $Files = @($chg.Files)
    if ($Files.Count -eq 0) { Write-Fail "git 无任何改动(也未提交过),没有可测落点;若改动未保存,先保存再跑。" }
    $ChangedCount = $Files.Count
    $nsByProject = @{}
    $fullProjects = New-Object System.Collections.Generic.List[string]
    $groups = @{}
    $unmappedCode = 0

    foreach ($file in $Files) {
        $c = Get-Classification $file
        $f = ($file -replace '\\', '/').ToLowerInvariant()
        # 门菶触发:.cs/.axaml(AGENTS.md)与 .resx(testing.md「Resources 文案」行)都是会被源码扫描门槛覆盖的源文件
        if ($f -match '\.(cs|axaml|resx)$' -and ($f.StartsWith('src/') -or $f.StartsWith('tests/'))) { $GatesNeeded = $true }

        $label = switch ($c.Kind) {
            'L3'   { "L3(落点 $($c.Rule) why=$($c.Why))" }
            'L1'   { if ($c.GatesOnly) { "L1 源码扫描门槛(落点 $($c.Rule))" } else { "L1 $(($c.Ns -join '|'))(落点 $($c.Rule))" } }
            'L2'   { "L2 $($c.Project)(落点 $($c.Rule))" }
            'doc'  { "无测试落点(文档/spec):testing.md 规定不跑测试" }
            default { "未命中落点表(工具/未映射路径,无专属测试落点)" }
        }
        $key = "$($c.Kind)|$($c.Rule)|$($label)"
        if (-not $groups.ContainsKey($key)) { $groups[$key] = 0 }
        $groups[$key]++

        switch ($c.Kind) {
            'L3' {
                $PlanLevel = 'L3'
                if (-not $PlanWhy) { $PlanWhy = $c.Why }
            }
            'L1' {
                if ($c.GatesOnly) { $GatesNeeded = $true }
                else {
                    foreach ($ns in $c.Ns) {
                        $proj = $ns.Substring(0, $ns.LastIndexOf('.'))
                        if (-not $nsByProject.ContainsKey($proj)) { $nsByProject[$proj] = New-Object System.Collections.Generic.List[string] }
                        $nsByProject[$proj].Add($ns)
                    }
                }
            }
            'L2' { if ($c.Project -and -not $fullProjects.Contains($c.Project)) { $fullProjects.Add($c.Project) } }
            default { if ($c.Kind -eq 'none' -and ($f.StartsWith('src/') -or $f.StartsWith('tests/'))) { $unmappedCode++ } }
        }
    }
    foreach ($k in $groups.Keys) {
        $label = ($k -split '\|', 3)[2]
        $Derivation.Add("$label x $($groups[$k])")
    }
    if ($unmappedCode -gt 0 -and $PlanLevel -ne 'L3') {
        $Derivation.Add("警告: $unmappedCode 个 src/tests 改动未命中落点表(如 ExtensionHost/PluginWorker 等未映射模块),它们只被构建与源码扫描门槛覆盖;需要跨进程保护时显式 -Level L3 --why")
    }

    if ($PlanLevel -eq 'L3') {
        $Derivation.Add("命中 L3 落点 -> 13 个测试项目全量(why 自动=$PlanWhy)")
    }
    else {
        foreach ($proj in $fullProjects) { $Runs.Add(@{ Project = $proj; Filter = ''; Reason = 'L2 落点(项目全量)' }) }
        foreach ($proj in $nsByProject.Keys) {
            if ($fullProjects -contains $proj) { continue }
            $segs = @($nsByProject[$proj] | Sort-Object -Unique | ForEach-Object { "FullyQualifiedName~$_" })
            $Runs.Add(@{ Project = $proj; Filter = ($segs -join '|'); Reason = 'L1 落点' })
        }
    }
}
else {
    if ($Level -eq 'L3' -and -not $Why) {
        Write-Fail @"
拒绝:L3(全量)必须带 --why。
L3 触发条件(testing.md):合并/发布;Abstractions/Plugin.Sdk/Ipc/存储 schema/AOT 等公共协议与核心基础设施变化;L1/L2 疑似跨模块回归;用户明确要求。
全量会把与本次改动无关的偶发失败混进来,判不出真伪——不是保险,是稀释信号。
成本参考:Forge.App.Tests 全量 16m25s(串行),13 个项目全量约 30 分钟起。
确属触发:scripts/forge-test.ps1 -Level L3 --why merge|release|interface|infra|cross-module
"@
    }
    # 细粒度选择器:它就是范围声明,一个项目一条过滤器;不再叠加项目全量
    if ($HasSelectors) {
        # -Ns 与选择器都是范围声明,同时给含义不明(不论 -Level 写不写)
        if ($Ns) { Write-Fail "-Ns 与 -Class/-Method/-Trait 选一个:两者都是范围声明,同时给会让过滤器含义歧义。" }
        if (-not (Test-Path (Join-Path $RepoRoot "tests/$Project"))) { Write-Fail "tests/$Project 不存在;项目名取 tests/ 下的目录,例如 Forge.Core.Tests。" }
        $f = ($Selectors -join '|')
        $Runs.Add(@{ Project = $Project; Filter = $f; Reason = '细粒度选择器' })
        # 源码扫描门槛的五个类只存在于 Forge.App.Tests:目标项目不是它时另开一条运行,
        # 不能把门槛过滤器 OR 进别的项目(那会是 0 命中的假覆盖)。
        if ($Gates) {
            if ($Project -eq 'Forge.App.Tests') {
                $Runs[0].Filter = $f + '|' + $GatesFilter
            }
            else {
                $Runs.Add(@{ Project = 'Forge.App.Tests'; Filter = $GatesFilter; Reason = '显式源码扫描门槛' })
            }
        }

        $Derivation.Add("选择器: $($Runs[0].Filter)")
    }
    elseif ($Level -eq 'L1') {
        if (-not $Project -or -not $Ns) { Write-Fail "L1 需要 -Project <测试项目> 与 -Ns <命名空间段>,或者 -Project 加 -Class/-Method/-Trait 选择器(副本无 .git,不能用 -Auto)。示例:scripts/forge-test.ps1 -Level L1 -Project Forge.App.Tests -Ns Workbench" }
        if (-not (Test-Path (Join-Path $RepoRoot "tests/$Project"))) { Write-Fail "tests/$Project 不存在;项目名取 tests/ 下的目录,例如 Forge.App.Tests。" }
        $segs = @()
        foreach ($n in ($Ns -split ',')) {
            $s = $n.Trim()
            if (-not $s) { continue }
            if ($s.StartsWith($Project + '.')) { $segs += "FullyQualifiedName~$s" } else { $segs += "FullyQualifiedName~$Project.$s" }
        }
        if ($segs.Count -eq 0) { Write-Fail "-Ns 解析后为空。" }
        if ($Gates) { $segs += ($GatesFilter -split '\|') }
        $Runs.Add(@{ Project = $Project; Filter = ($segs -join '|'); Reason = '显式 L1' })
    }
    elseif ($Level -eq 'L2') {
        if (-not $Project) { Write-Fail "L2 需要 -Project <测试项目>,例如 -Project Forge.Core.Tests。" }
        if (-not (Test-Path (Join-Path $RepoRoot "tests/$Project"))) { Write-Fail "tests/$Project 不存在;项目名取 tests/ 下的目录,例如 Forge.Core.Tests。" }
        $Runs.Add(@{ Project = $Project; Filter = ''; Reason = '显式 L2(项目全量)' })
        if ($Gates -and $Project -ne 'Forge.App.Tests') { $Runs.Add(@{ Project = 'Forge.App.Tests'; Filter = $GatesFilter; Reason = '显式源码扫描门槛' }) }
    }
}

# 源码扫描门槛合并:App.Tests 已有带过滤器的运行就并入,否则单开一条
if ($Auto -and $PlanLevel -ne 'L3' -and $GatesNeeded) {
    $appRun = @($Runs | Where-Object { $_.Project -eq 'Forge.App.Tests' -and $_.Filter })
    if ($appRun.Count -gt 0) {
        $appRun[0].Filter = $appRun[0].Filter + '|' + $GatesFilter
        $appRun[0].Reason = $appRun[0].Reason + '+源码扫描门槛'
    }
    elseif (-not ($Runs | Where-Object { $_.Project -eq 'Forge.App.Tests' })) {
        $Runs.Add(@{ Project = 'Forge.App.Tests'; Filter = $GatesFilter; Reason = '源码扫描门槛(.cs/.axaml 改动)' })
    }
}

# 收据的 level 从实际 Runs 反推(命中 L3 时已置;无落点为 none;带过滤器的运行= L1,项目全量= L2,两者都有= L1+L2)
if (-not $PlanLevel) {
    $hasFull = [bool]($Runs | Where-Object { -not $_.Filter })
    $hasNs = [bool]($Runs | Where-Object { $_.Filter })
    if ($hasFull -and $hasNs) { $PlanLevel = 'L1+L2' }
    elseif ($hasFull) { $PlanLevel = 'L2' }
    elseif ($hasNs) { $PlanLevel = 'L1' }
}

# L3:13 个测试项目全量
if ($PlanLevel -eq 'L3') {
    $Runs.Clear()
    foreach ($d in (Get-ChildItem (Join-Path $RepoRoot 'tests') -Directory | Sort-Object Name)) {
        if (Get-ChildItem $d.FullName -Filter '*.csproj' -File -ErrorAction SilentlyContinue) {
            $Runs.Add(@{ Project = $d.Name; Filter = ''; Reason = 'L3 全量' })
        }
    }
}

if ($ListOnly) {
    Write-Host "[forge-test] 计划(level=$(if ($PlanLevel) { $PlanLevel } else { 'none' })$(if ($PlanWhy) { " why=$PlanWhy" } else { '' })):"
    if ($Derivation.Count -gt 0) { foreach ($d in $Derivation) { Write-Host "  推导: $d" } }
    if ($Runs.Count -eq 0) { Write-Host "  无测试落点(文档/spec/工具等非测试落点改动)-> 不跑测试。" }
    foreach ($r in $Runs) {
        $cmd = "dotnet test tests/$($r.Project) --no-build"
        if ($r.Filter) { $cmd += " --filter `"$($r.Filter)`"" }
        if ($MaxParallelThreads -gt 0) { $cmd += " --settings artifacts/testruns/forge-test.runsettings" }
        if ($PlanLevel -eq 'L3') { $cmd += " --logger `"trx;LogFileName=$($r.Project).trx`" --results-directory artifacts/testresults/$($r.Project) --blame-hang --blame-hang-timeout 10min --blame-crash" }
        Write-Host "  [计划] $cmd"
    }
    if ($Parallel -and $Runs.Count -gt 1) {
        $d = $Parallelism
        if ($d -le 0) { $d = [math]::Max(1, [math]::Min($Runs.Count, [int][math]::Floor([Environment]::ProcessorCount / 2))) }
        Write-Host "  [计划] 并行: 并发上限 $([math]::Min($d, $Runs.Count))($($Runs.Count) 个项目)"
    }
    exit 0
}

# ---- 2. 执行 ----
function Invoke-Dotnet([string[]]$ArgList) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $text = (& dotnet $ArgList 2>&1 | Out-String -Width 4096)
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $prev }
    Write-Host $text
    return @{ Exit = $code; Text = $text }
}

function Get-Counts([string]$Text) {
    # 过滤器 0 命中时 dotnet test 退出码是 0 且**不打印摘要行**,只打一句 "No test matches the given testcase filter"。
    # 不把它译成 0/0/0 就会被当成“退出码 0 = 绿”,于是类名手误、Trait 写错都变成假绿(0 用例本该判红)。
    if ($Text -match 'No test matches the given testcase filter') {
        return @{ Failed = 0; Passed = 0; Skipped = 0 }
    }

    # 摘要可能是英文(VSTest 英文资源)或 zh-CN 本地化("失败: 0，通过: 110，已跳过: 0",全角逗号);取最后一条匹配(最终摘要)
    $patterns = @(
        'Failed:\s*(\d+)\s*[,，]\s*Passed:\s*(\d+)\s*[,，]\s*Skipped:\s*(\d+)',
        '失败:\s*(\d+)\s*[,，]\s*通过:\s*(\d+)\s*[,，]\s*已跳过:\s*(\d+)'
    )
    foreach ($pat in $patterns) {
        $ms = [regex]::Matches($Text, $pat)
        if ($ms.Count -gt 0) {
            $m = $ms[$ms.Count - 1]
            return @{ Failed = [int]$m.Groups[1].Value; Passed = [int]$m.Groups[2].Value; Skipped = [int]$m.Groups[3].Value }
        }
    }
    return $null
}

function Invoke-TestOnce([string[]]$ArgList) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $res = Invoke-Dotnet $ArgList
    $sw.Stop()
    return @{ Res = $res; Counts = (Get-Counts $res.Text); Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) }
}

# 并行跑一批项目:每个项目一个 dotnet test 进程,输出各自落文件(不交错),跑完再收计数。
# 并发上限 Degree;首跑失败的项目交给串行重跑路径(重建 → 重跑),那是唯一能治旧 bin 假红的路。
# 同步等待整批结束,不用 Start-Job(需序列化参数、看不到本脚本的函数)。
function Invoke-ParallelRuns {
    param($Items, [int]$Degree, $Receipt)

    $dir = Join-Path $RepoRoot 'artifacts/testruns/parallel'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    $pending = [System.Collections.Generic.Queue[object]]::new()
    foreach ($item in $Items) { $pending.Enqueue($item) }
    $running = New-Object System.Collections.Generic.List[object]
    $records = @{}

    while ($pending.Count -gt 0 -or $running.Count -gt 0) {
        while ($running.Count -lt $Degree -and $pending.Count -gt 0) {
            $run = $pending.Dequeue()
            $argList = New-TestArgs $run
            # Start-Process 的 -ArgumentList 不替我们加引号:带空白的值(含空格的工作区路径、--settings 的绝对路径)
            # 会被拆成两个 argv,而 & dotnet 的串行路径不会。手动包一层引号。
            $quotedArgs = @($argList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
            $tag = ($run.Project + '-' + ([guid]::NewGuid().ToString('N').Substring(0, 6)))
            $outFile = Join-Path $dir "$tag.out"
            $errFile = Join-Path $dir "$tag.err"
            Write-Host "[forge-test] 启动 tests/$($run.Project)$(if ($run.Filter) { '  filter: ' + $run.Filter })" -ForegroundColor Cyan
            $psi = @{
                FilePath               = 'dotnet'
                ArgumentList           = $quotedArgs
                NoNewWindow            = $true
                PassThru               = $true
                RedirectStandardOutput = $outFile
                RedirectStandardError  = $errFile
            }
            $proc = Start-Process @psi
            $running.Add([pscustomobject]@{ Run = $run; Proc = $proc; Out = $outFile; Err = $errFile; Args = $argList; Started = [DateTime]::UtcNow })
        }

        $finished = @($running | Where-Object { $_.Proc.HasExited })
        if ($finished.Count -eq 0) { Start-Sleep -Milliseconds 250; continue }

        foreach ($f in $finished) {
            [void]$running.Remove($f)
            $text = ''
            foreach ($file in @($f.Out, $f.Err)) {
                if (Test-Path -LiteralPath $file) {
                    # 重定向出来的文件是 dotnet 的 UTF-8 字节,而 Windows PowerShell 5.1 的 Get-Content 默认按 ANSI/GBK 解,
                    # 不指定编码会让中文摘要变成乱码 -> 计数解析不出来 -> 收据缺用例数。
                    try { $text += (Get-Content -Raw -Encoding UTF8 -LiteralPath $file) + "`n" } catch { }
                    Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
                }
            }

            Write-Host "`n===== tests/$($f.Run.Project)$(if ($f.Run.Filter) { '  filter: ' + $f.Run.Filter }) =====" -ForegroundColor Cyan
            Write-Host $text

            $seconds = [math]::Round(([DateTime]::UtcNow - $f.Started).TotalSeconds, 1)
            $counts = Get-Counts $text
            $exit = $f.Proc.ExitCode
            $records[$f.Run.Project] = [ordered]@{
                project = $f.Run.Project
                filter  = $f.Run.Filter
                reason  = $f.Run.Reason
                command = ($f.Args -join ' ')
                exit    = $exit
                passed  = $(if ($counts) { $counts.Passed } else { $null })
                failed  = $(if ($counts) { $counts.Failed } else { $null })
                skipped = $(if ($counts) { $counts.Skipped } else { $null })
                retried = $false
                seconds = $seconds
            }
        }
    }

    # 首跑失败的项目回到串行路径:重建 → 重跑一次(治旧 bin 假红),结果覆盖并行那次
    $anyFailed = $false
    foreach ($run in $Items) {
        $rec = $records[$run.Project]
        if ($null -eq $rec) { continue }
        $zeroCounts = ($null -ne $rec.passed) -and (($rec.passed + $rec.failed + $rec.skipped) -eq 0)
        $isFail = ($rec.exit -ne 0) -or ($null -ne $rec.failed -and $rec.failed -gt 0) -or $zeroCounts
        if ($isFail) {
            Write-Host "[forge-test] tests/$($run.Project) 并行首跑失败 -> 回到串行重跑(再失败才重建重跑)..." -ForegroundColor Yellow
            $out = Invoke-Run $run
            $Receipt.Add($out.record)
            if ($out.failed) { $anyFailed = $true }
        }
        else {
            if ($null -eq $rec.passed) { Write-Host "[forge-test] 警告: tests/$($run.Project) 未能解析用例计数,以退出码 $($rec.exit) 为准。" -ForegroundColor Yellow }
            $Receipt.Add($rec)
        }
    }

    return $anyFailed
}

$totalSw = [Diagnostics.Stopwatch]::StartNew()
$buildSeconds = $null
Push-Location $RepoRoot
try {
    if ($Runs.Count -eq 0) {
        Write-Host "[forge-test] 改动 $ChangedCount 个文件,无测试落点(文档/spec/工具等非测试落点)-> 不跑测试、不构建。" -ForegroundColor Cyan
    }
    elseif (-not $SkipBuild) {
        Write-Host "[forge-test] dotnet build Forge.slnx -c Debug ..."
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $b = Invoke-Dotnet @('build', 'Forge.slnx', '-c', 'Debug')
        $sw.Stop()
        $buildSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        if ($b.Exit -ne 0) {
            # 构建失败也必须落收据(build.failed=true):「无收据的全量视为未完成测试义务」,评审须能区分“构建失败”与“没跑”
            $ts = Get-Date -Format 'yyyyMMdd-HHmmss'
            $receiptDir = Join-Path $RepoRoot 'artifacts/testruns'
            New-Item -ItemType Directory -Force -Path $receiptDir | Out-Null
            $lvlVal = $PlanLevel
            if (-not $lvlVal) { $lvlVal = 'none' }
            [ordered]@{
                timestamp          = $ts
                level              = $lvlVal
                why                = $PlanWhy
                auto               = [bool]$Auto
                selectors          = @($Selectors)
                parallel           = @{ enabled = [bool]$Parallel; degree = $(if ($Parallel -and $Parallelism -gt 0) { $Parallelism } elseif ($Parallel) { 'auto' } else { 0 }) }
                maxParallelThreads = $MaxParallelThreads
                changedFiles       = $ChangedCount
                derivation         = $Derivation.ToArray()
                build              = @{ failed = $true; skipped = $false; seconds = $buildSeconds }
                runs               = @()
                totals             = @{ passed = 0; failed = 0; skipped = 0; countsKnown = $true }
                wallClockSeconds   = [math]::Round($totalSw.Elapsed.TotalSeconds, 1)
            } | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $receiptDir "forge-test-$ts.json")
            Write-Host "收据文件: artifacts/testruns/forge-test-$ts.json(build.failed=true)"
            Write-Fail "构建失败(退出码 $($b.Exit)),测试未执行;失败收据已落盘。"
        }
    }

    $receiptRuns = New-Object System.Collections.Generic.List[object]
    $anyFailed = $false

    # 单项目内 xunit 并行线程数:0 = 不动(xunit 自己按 CPU 数)。写成临时 .runsettings,经 --settings 传入一个项目一份
    # (不碰各项目自己的 xunit 配置);文件放在 artifacts/,跑完就删。
    $runSettingsPath = $null
    if ($MaxParallelThreads -gt 0 -and $Runs.Count -gt 0) {
        $rsDir = Join-Path $RepoRoot 'artifacts/testruns'
        New-Item -ItemType Directory -Force -Path $rsDir | Out-Null
        $runSettingsPath = Join-Path $rsDir 'forge-test.runsettings'
        @"
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <xUnit>
    <MaxParallelThreads>$MaxParallelThreads</MaxParallelThreads>
  </xUnit>
</RunSettings>
"@ | Set-Content -Encoding UTF8 $runSettingsPath
    }

    function New-TestArgs($Run) {
        $argList = @('test', "tests/$($Run.Project)", '--no-build')
        if ($Run.Filter) { $argList += @('--filter', $Run.Filter) }
        if ($runSettingsPath) { $argList += @('--settings', $runSettingsPath) }
        if ($PlanLevel -eq 'L3') {
            $argList += @('--logger', "trx;LogFileName=$($Run.Project).trx")
            $argList += @('--results-directory', "artifacts/testresults/$($Run.Project)")
            $argList += @('--blame-hang', '--blame-hang-timeout', '10min', '--blame-crash')
        }
        return $argList
    }

    # 一个项目一次运行的全部步骤:跑 → 失败先重建再重跑(治旧 bin 假红)→ 出记录。
    function Invoke-Run($Run) {
        $argList = New-TestArgs $Run
        Write-Host "`n[forge-test] tests/$($Run.Project)$(if ($Run.Filter) { '  filter: ' + $Run.Filter })" -ForegroundColor Cyan
        $t = Invoke-TestOnce $argList
        $counts = $t.Counts
        $retried = $false

        $isFail = ($t.Res.Exit -ne 0) -or (($counts -and $counts.Failed -gt 0))
        if (-not $isFail -and $counts -and (($counts.Passed + $counts.Failed + $counts.Skipped) -eq 0)) { $isFail = $true }

        if ($isFail -and ($counts -eq $null -or $counts.Failed -gt 0 -or $t.Res.Exit -ne 0)) {
            Write-Host "[forge-test] tests/$($Run.Project) 首跑失败 -> 先 Rebuild 该项目再重跑一次(治旧 bin 假红)..." -ForegroundColor Yellow
            # 必须 -t:Rebuild：普通增量 build 在“源码改回原样/时间戳比产物旧”时不会重编,治不了这一类假红。
            $rb = Invoke-Dotnet @('build', "tests/$($Run.Project)", '-c', 'Debug', '-t:Rebuild')
            if ($rb.Exit -eq 0) {
                $t = Invoke-TestOnce $argList
                $counts = $t.Counts
                $retried = $true
            }
            else { Write-Host "[forge-test] 重建也失败,按首跑结果判红。" -ForegroundColor Yellow }
        }

        $finalFail = ($t.Res.Exit -ne 0) -or (($counts -and $counts.Failed -gt 0)) -or ($counts -and (($counts.Passed + $counts.Failed + $counts.Skipped) -eq 0))
        if (-not $counts) { Write-Host "[forge-test] 警告: tests/$($Run.Project) 未能解析用例计数(输出格式变化?),以退出码 $($t.Res.Exit) 为准。" -ForegroundColor Yellow }

        $p = $null; $f = $null; $s = $null
        if ($counts) { $p = $counts.Passed; $f = $counts.Failed; $s = $counts.Skipped }
        return [ordered]@{
            record = [ordered]@{
                project = $Run.Project
                filter  = $Run.Filter
                reason  = $Run.Reason
                command = ($argList -join ' ')
                exit    = $t.Res.Exit
                passed  = $p
                failed  = $f
                skipped = $s
                retried = $retried
                seconds = $t.Seconds
            }
            failed  = $finalFail
        }
    }

    if ($Parallel -and $Runs.Count -gt 1) {
        $degree = $Parallelism
        if ($degree -le 0) {
            $cpu = [Environment]::ProcessorCount
            $degree = [math]::Max(1, [math]::Min($Runs.Count, [int][math]::Floor($cpu / 2)))
        }

        $degree = [math]::Min($degree, $Runs.Count)
        Write-Host "[forge-test] 并行执行:$($Runs.Count) 个项目,并发上限 $degree。" -ForegroundColor Cyan
        $anyFailed = Invoke-ParallelRuns -Items $Runs -Degree $degree -Receipt $receiptRuns
    }
    else {
        foreach ($r in $Runs) {
            $out = Invoke-Run $r
            $receiptRuns.Add($out.record)
            if ($out.failed) { $anyFailed = $true }
        }
    }

    if ($runSettingsPath -and (Test-Path $runSettingsPath)) { Remove-Item -LiteralPath $runSettingsPath -Force -ErrorAction SilentlyContinue }

    # ---- 3. 收据 ----
    $ts = Get-Date -Format 'yyyyMMdd-HHmmss'
    $receiptDir = Join-Path $RepoRoot 'artifacts/testruns'
    New-Item -ItemType Directory -Force -Path $receiptDir | Out-Null
    $receiptPath = Join-Path $receiptDir "forge-test-$ts.json"
    $sumPassed = 0; $sumFailed = 0; $sumSkipped = 0; $known = $true
    foreach ($rr in $receiptRuns) {
        if ($null -ne $rr.passed) { $sumPassed += $rr.passed; $sumFailed += $rr.failed; $sumSkipped += $rr.skipped } else { $known = $false }
    }
    $levelVal = $PlanLevel
    if (-not $levelVal) { $levelVal = 'none' }
    $receipt = [ordered]@{
        timestamp        = $ts
        level            = $levelVal
        why              = $PlanWhy
        auto             = [bool]$Auto
        selectors        = @($Selectors)
        parallel         = @{ enabled = [bool]$Parallel; degree = $(if ($Parallel -and $Parallelism -gt 0) { $Parallelism } elseif ($Parallel) { 'auto' } else { 0 }) }
        maxParallelThreads = $MaxParallelThreads
        changedFiles     = $ChangedCount
        derivation       = $Derivation.ToArray()
        build            = @{ failed = $false; skipped = [bool]$SkipBuild; seconds = $buildSeconds }
        runs             = $receiptRuns.ToArray()
        totals           = @{ passed = $sumPassed; failed = $sumFailed; skipped = $sumSkipped; countsKnown = $known }
        wallClockSeconds = [math]::Round($totalSw.Elapsed.TotalSeconds, 1)
    }
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $receiptPath

    Write-Host "`n== Forge 测试收据(粘贴进完工报告)==" -ForegroundColor Green
    Write-Host "level: $(if ($PlanLevel) { $PlanLevel } else { 'none' })$(if ($PlanLevel -eq 'L3') { "(why=$PlanWhy)" } else { '' })$(if ($Auto) { ' auto' } else { '' })$(if ($Parallel) { " parallel($(if ($Parallelism -gt 0) { $Parallelism } else { 'auto' }))" } else { '' })$(if ($MaxParallelThreads -gt 0) { " maxThreads=$MaxParallelThreads" } else { '' })  改动文件: $ChangedCount"
    if ($Derivation.Count -gt 0) { foreach ($d in $Derivation) { Write-Host "推导: $d" } }
    if ($buildSeconds) { Write-Host "构建: $buildSeconds s$(if ($SkipBuild) { '(跳过)' } else { '' })" }
    foreach ($rr in $receiptRuns) {
        $countTxt = $(if ($null -ne $rr.passed) { "passed $($rr.passed) / failed $($rr.failed) / skipped $($rr.skipped)" } else { '计数未解析' })
        Write-Host "运行: tests/$($rr.project)$(if ($rr.filter) { "  filter: $($rr.filter)" })"
        Write-Host "      $countTxt$(if ($rr.retried) { '(重建后重跑)' } else { '' }) - $($rr.seconds) s  exit=$($rr.exit)"
    }
    if ($receiptRuns.Count -eq 0) { Write-Host "运行: (无,无测试落点)" }
    Write-Host "收据文件: artifacts/testruns/forge-test-$ts.json"
    if ($anyFailed) { Write-Host "结果: FAILED" -ForegroundColor Red; exit 1 }
    Write-Host "结果: OK" -ForegroundColor Green
    exit 0
}
finally { Pop-Location }

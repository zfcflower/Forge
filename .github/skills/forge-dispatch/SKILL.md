---
name: forge-dispatch
description: >-
  Use when the main Agent dispatches a Worker to a repository copy, or reviews
  the Worker's delivery — fills the dispatch text with copy creation, expected
  L1 test filter, skill paths, and receipt requirements per AGENTS.md.
---

# Forge 派活模板

主 Agent 派 Worker 时按此模板填(规则源:`AGENTS.md`「派活规则」/「测试分层与执行」、`testing.md`):

1. **副本**:`scripts/new-copy.ps1 D:/FORGE/work<N>`(或 AGENTS.md 的 robocopy 原命令);Worker 只在自己的副本内干活、搜索排除 `bin`/`obj`、不碰其他路径与 `*.csproj`/`Directory.Build.props`/`Forge.slnx`、不执行 git 命令、禁止在 `D:/FORGE` 根下递归搜索。
2. **任务**:目标、归属路径(完工后主 Agent 只从该路径拷回主仓)、验收标准。
3. **测试**:写明预期过滤器——`scripts/forge-test.ps1 -Level L1 -Project <测试项目> -Ns <命名空间>`(副本无 `.git`,不能用 `-Auto`);改了 `.cs`/`.axaml` 加 `-Gates`;确需全量时 `-Level L3 --why <理由>`,缺 `--why` 会被拒绝。
4. **约束**:适用的 `.github/skills/<name>/SKILL.md` 路径逐条列出(Worker 不会自己找);涉及性能或分配时同时列 `blazing-fast-dotnet`。
5. **收货**:完工报告必须附 `forge-test.ps1` 收据(命令、过滤器推导、用例数、墙钟);缺收据、或测试范围与落点不匹配(无 `--why` 的全量),评审即打回。

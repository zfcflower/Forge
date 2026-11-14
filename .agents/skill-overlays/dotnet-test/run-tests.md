---
core: dotnet-test/run-tests
binding-revision: "1"
mode: extend
---

# Forge 仓库绑定(dotnet-test/run-tests)

本仓库的测试执行一律经 `scripts/forge-test.ps1`,不手拼 `dotnet test`:

- 主仓(有 `.git`):`scripts/forge-test.ps1 -Auto`——按 git 改动推导落点过滤器(映射 `scripts/test-map.json`)并自动追加源码扫描门槛,只跑与改动严格相关的用例。
- 副本(无 `.git`):按派活文本给的过滤器显式执行,如 `scripts/forge-test.ps1 -Level L1 -Project Forge.App.Tests -Ns Workbench [-Gates] [-SkipBuild]`。
- `-Level L2 -Project <测试项目>` 跑模块全量;`-Level L3` 必须带 `--why merge|release|interface|infra|cross-module`,缺省直接拒绝。
- 禁止对 `Forge.slnx` 或多项目做无 `--why` 的全量;失败让脚本自动重建一次再判红;收据(`artifacts/testruns/*.json` 与控制台清单)粘贴进完工报告。

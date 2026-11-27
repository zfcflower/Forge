# 托管上下文与确定性折叠（用户可读版）

> 本文是 `src/Forge.Core/SPEC.md`「托管上下文与确定性折叠」的用户向说明：模型到底看到多少历史、为什么会被折叠、原始内容去哪了、怎么找回来。实现与本文同源（`ManagedContext` / `AgentLoop.FoldAsync` / `SqliteTurnLedgerStore`），代码改动时同步本文。

**一句话**：模型的历史区不是原始对话流水，而是**每轮一条 entry** 的累加；历史过长时，最旧的 entry 被换成一段**指针块**——全程零模型调用、零摘要，原文一条不删，随时能按指针查回来。

---

## 一、模型看到的历史是什么样

一次请求的形状（与旧版相比只变历史区）：

```

[稳定前缀]  # System → # 全局/项目 Agent → # Role
[工具定义]  固定段 → 条件段 → 外部段（字节稳定）
[图例]      [managed context] …turn: <n> is the ledger turn number…   ← 每请求一次，请求的第一条（不在视图里）
[历史区]    ← 每回合开头由 turn_ledger 重建；下面这些块字节稳定
    [compacted: turns 1–30 · seq 1–18640 · 30 turns]      ← 折叠块（若有）
    retrieve: read_context(from_turn=1, to_turn=30)
    originals: read_history(from_seq=1, to_seq=18640)

    user:      修复登录跳转                                ← 一轮 = 两条消息：任务（user）
    assistant: 修好了：401 现在会回跳到原页面。      ← 就是模型那一轮的回复，原样，不改写、不加标题
               <!-- evidence turn=128 read=src/Auth/Login.cs wrote=src/Auth/Guard.cs spec=forge-core seq=4201-4207 -->
                                                        ← harness 的收据行：轮号、读过的文件、改过的文件、spec、消息范围
[本回合交换] user → assistant(+tool_calls) → 每个工具结果 → 插话/steering   ← 逐字完整
[承载]      (harness context follows)                                    ← 仅当有动态尾，固定 assistant 一行
[动态尾]    <forge-context>…</forge-context>                              ← 每请求临时，不落库（今天只载 nudge）
```

要点：

- **轮内不受影响**：本回合的每一次请求都带着本回合的真实交换（工具结果全在），不裁剪、不收据、不摘要。托管化只替换**已结束轮**的呈现。
- **轮末吸收**：一轮结束，它的原始交换留在 `messages`（证据），进入累加上下文的只有那两条消息。
- **一条 entry 是两条消息**：任务发 `user`、报告发 `assistant`。这样历史区在**消息边界**上一段段可复用，而不是被拼成一整块（网关按消息块比对缓存，相邻同 role 会合并；实测同一网关下把 entry 拆开，跨轮首请求的缓存读取从 0% 升到 84%）。唯一重写历史中段的是折叠。

---

## 二、什么时候折叠、折多少

| 时点 | 用哪条线 | 做什么 |
|---|---|---|
| 回合的第一个请求前 | 触发线 τ（默认 0.85·U） | 折叠最旧的已结算轮，直到 ≤ 目标线 T（默认 0.5·U） |
| 回合内后续请求 | 紧急线 ε（默认 0.95·U） | 同上，只为避免撞窗口 |
| 回合内被 provider 判超长 | 无 | 加大折叠（降到 1）后重发一次；仍不够则按原错误失败 |
| `/compact` | 不看线 | 折叠到只剩最新 3 轮 entry |

- **永远保留最新 3 轮**不折叠（`ManagedContext.MinRecentEntries`），最近的来龙去脉不需要查。
- **只折已结算的轮**：正在进行的那一轮没有 entry 可折。
- **折叠区间只增不减**：`turn_ledger.folded` 是单调标记，所以同一份数据永远折出同一个形状，重开标签与同一进程看到的历史一致。
- **没有第二级**：旧算法里"裁工具输出 → 模型总结成六段摘要"的两级都已删除。压缩不再调模型、不产生任何新文本。

---

## 三、原文去哪了、怎么查

| 你要什么 | 用什么 | 参数从哪来 |
|---|---|---|
| 某几轮的 **entry 原文**（托管上下文本身） | `read_context` | 折叠块的 `retrieve:` 行直接给出 `from_turn`/`to_turn` |
| 按关键词找哪一轮说过什么 | `search_context` | 查 `turn_ledger` 的 FTS 索引（task + 结论 + unresolved + next） |
| 某轮的**原始消息与工具输出** | `read_history` | entry 的 `seq:` 行、或折叠块的 `originals:` 行 |
| 单条超长工具输出（>32k 被转存） | `read_artifact` | 工具结果里的 artifact id |

链子连起来是这样：

```
折叠块  --retrieve-->  read_context(from_turn, to_turn)  -->  那几轮的 entry 原文
entry   --seq-->       read_history(from_seq, to_seq)     -->  原始消息（user/assistant/tool）
工具结果 --artifact id--> read_artifact(id, offset, len)    -->  超长输出的全文
```

**为什么可以放心折**：折叠不删任何东西，只改变"这一次请求带多少历史"。折叠块里三样东西保证不失忆——轮区间（模型按轮思考）、seq 区间（不可变指针）、`open:` 列表（折叠后唯一天然会丢、又直接影响决策的信息）。

---

## 四、和旧算法的区别（一眼看清）

| | 旧（已删除） | 现在 |
|---|---|---|
| 历史区内容 | 原始消息全量 | 每轮两条消息（任务 user + 报告 assistant） |
| 压缩手段 | ①裁旧工具输出成收据 ②模型总结成六段摘要 | 只折叠 entry 成指针块 |
| 是否调模型 | 摘要级要调一次（还会分批） | **永不** |
| 失败形态 | 摘要被拒 → 整次压缩失败、白烧一次请求 | 折叠是本地 SQLite UPDATE + 重渲染，无外部依赖 |
| 原文去向 | `summaries` + 原文 | 原文不动；`summaries` 只被旧会话读 |
| 找回方式 | `read_history` / `search_history` | 新增 `read_context` / `search_context`；旧的两个仍在（证据级） |

---

## 五、代价与边界

- **一轮一个问题**：现在模型读历史是按轮的 entry，不是逐条消息。需要过程细节时必须显式 `read_history`——这是设计意图（先看结论再决定要不要回原文），但意味着"随手翻旧消息"要两次调用。
- **每轮一次重建**：回合开始时一次 `turn_ledger.ListAsync` + 渲染（毫秒级，已计入 `agentloop.dynamic` 的 833 B/请求预算）。换来的是"同进程与重开标签看到同一份历史"。
- **旧会话**：功能上线前的库没有 ledger 行，继续走 `summaries` 摘要视图 + 原始尾部；新会话不再写摘要。
- **规则**：折叠块不聚合任何内容，只留范围与两个指针——聚合"做完了什么"会变成变相摘要，是这条设计明确拒绝的东西。

---
id: forge-io
type: module-design
status: draft
title: Forge.IO
parent: forge-architecture
---

# Forge.IO

主程序的文件与进程底座,以及 Agent 的文件/搜索/shell 工具。只依赖 BCL 与 `Forge.Abstractions`(仅 `ITool` 契约;Abstractions 零依赖,无环),AOT 兼容、分析器 0 警告。所有“覆盖已有文件”的写入统一走这里,不再各处自写“临时文件再改名”。

## 边界

- **路径**(`Paths/`):`WorkspacePath` 是工作区路径身份与包含检查的唯一实现,`Forge.Workspace` 的 `WorkspacePaths` 委托到这里;`PathGate` 串行同一路径的读-改-写。
- **文件**(`Files/`):`FileWalker` 惰性 DFS 遍历;`TextFileReader` 有界窗口读;`LineEndings` 行尾归一(写入与匹配共用);`TextFileWriter` 原子整写与追加。
- **进程**(`Process/`):`ProcessRunner` 有界字节捕获的进程执行;`ShellResolver` 平台 shell 解析;`CommandLine` 引用感知切分与 direct/script 判定。
  - **实时输出尾部**:`ProcessRequest.OnOutput`(空则零开销)收到 stdout/stderr 按到达顺序合并后的最新尾部快照(≤ `OutputTailChars`,默认 16 KiB),至多每 `OutputInterval`(默认 250 ms)一次,间隔内到达的输出由一次性定时器补报;每路一个 UTF-8 `Decoder`;`ProcessOutputTail` 在持锁时调用监听者,所以快照严格有序、不并发,监听者必须不阻塞、抛异常被吞;进程退出时未报的尾部丢弃(结果含全部输出)。
- **搜索**(`Search/`):`GlobMatcher`、`GitIgnore`(完整 gitignore 语义)、`GrepEngine`(托管)、`RipgrepBackend`(可选加速)。
- **工具**(`Tools/`):`read`/`write`/`edit`/`ls`/`find`/`grep`/`copy`/`move`/`delete`/`bash` 十个 `ITool`、`FileTools` 聚合、`FileOps`(复制/删除的树遍历与路径规则)、`ToolOptions`、`ToolBase`。
- `AtomicFile`:无锁、可用于用户工作区文件。写临时文件、落盘、改名覆盖;读取带 `FileShare.ReadWrite | Delete`。对外两组入口:整份写入 `WriteAllBytes`/`WriteAllBytesAsync`,以及只做“发布”的 `ReplaceFile`/`ReplaceFileAsync`(给已经流式写好并落盘的临时文件用)。
- `AtomicJsonStore<T>` + `FileLock`:仅用于本程序自有、多进程共享的小状态文件(设置等)。每次更新在跨进程锁内重读最新值再应用修改,避免后写覆盖先写。用户工作区文件不可加锁(其他程序不遵守)。
- 审批记录不使用本组件,改用 SQLite。
- **不管**:审批与权限交互(App/Core)、Git 与 PR(Workspace)、进程隔离与沙箱。`WorkspacePath` 只挡意外与明显越界,而且只管写(读走 `ResolveReadable`,不设界),它也不是安全沙箱:能在工作区内建链接并抢在检查后替换的调用方仍可逃逸(TOCTOU)。

## 关键决策

### 原子写与跨进程状态文件

- **覆盖用改名覆盖,不用 `File.Replace`。** Windows 上 `File.Move(overwrite)` 在目标有任何打开句柄时被拒绝(访问被拒绝),`File.Replace` 写者不受阻但让读者约 12% 打开失败(文件名瞬时不存在)。前者读者零失败,故选前者并重试。
- **只重试确认的占用竞争:** Windows、`UnauthorizedAccessException` 或共享/锁冲突类 `IOException`(HResult 低 16 位 32、33),且目标仍存在、不是目录、不带 `ReadOnly`。其余错误立即抛出。
- **确定性失败不重试:** 目标明明是 `ReadOnly` 文件、目标是目录、锁路径是目录或 `ReadOnly` 文件时,等待不会改变结果,因此立即原样抛出原异常,不消耗预算(改造前这些情况会占满整个 5 秒预算/锁超时)。
- **只读目标在改名前拒绝,不靠改名失败:** `ReadOnly` 不再只是失败后的重试分类条件。Unix 上目标目录可写时 `rename(2)` 能覆盖只读文件、并把权限换成临时文件的,所以发布前统一检查目标:Windows 看 `FileAttributes.ReadOnly`;Unix 另加两条(模式没有任何写位、或以写权限打开目标被拒),三条任一命中即抛 `UnauthorizedAccessException`(消息含路径),删临时文件,目标字节与属性原样保留,不重试。两个入口与异步变体共用同一段发布逻辑,因此 `WriteAllBytes`、`WriteAllBytesAsync`、`ReplaceFile`、`ReplaceFileAsync` 行为一致。
- **成功替换保留目标权限:** Unix 上发布前把目标的 `File.GetUnixFileMode` 设到临时文件,换名后文件权限(含可执行位)不变,而不是变成临时文件的 0644;非 Unix 跳过,设置失败忽略(权限不是发布的契约条件)。
- **重试节奏:** 先 `Thread.Yield()` 约 10 毫秒再 `Sleep(1)`,总预算默认 5 秒,可取消,耗尽重抛原始异常并清理临时文件。
- **取消的响应上限:** 同步与异步的重试循环都在**每次尝试之前**检查令牌,等待本身可取消(同步 `WaitHandle.WaitOne(1)`,异步 `Task.Delay(1, token)`)⇒ 从取消到抛出的延迟上限是“**一次改名尝试 + 1 毫秒**”,异步再加一次续体调度(12 个竞争任务同进程时实测 ≤ 16 毫秒)。单次尝试是一次 `File.Move` 加一次属性查询,它和临时文件的创建/写入/`fsync` 一样是文件系统调用:杀毒软件与慢盘可让单次调用停数百毫秒到数秒(托管 Windows runner 上实测过单次小文件写入 0.67 秒、整条写入 6.9 秒),产品不为这类单次调用担保固定毫秒数。写临时文件的创建/写入/`fsync` 本身不可取消,取消落在这一段时调用在其后第一次令牌检查处抛出;写完数据后、`fsync` 前再检查一次令牌,取消落在写入与落盘之间时不再付出一次落盘的代价。
- **读者永不把瞬时缺失当作“无数据”。** 读取到不存在时做有界复查;只有确认不存在才返回 null。损坏的 JSON 抛 `InvalidDataException`,绝不当作空状态(否则下一次更新会覆盖全部数据)。
- **已知局限:** 读者长时间持有句柄(如连续读取)时重试无法保证写入成功,预算耗尽会明确失败而不是静默。

### 工具面

- **只给十个工具,名字对齐提示词。** `read`/`write`/`edit`/`ls`/`find`/`grep`/`copy`/`move`/`delete`/`bash` 的名字与 `Forge.Core.Prompts.BuiltInAgents` 的白名单逐字一致(Scout 与 Plan 用前面四个,Worker 再加 `edit`/`write`/`copy`/`move`/`delete`/`bash`;Reviewer 是前面四个加只读的 `git_diff`,它不属本模块,见 forge-core),所以工具落地不需要改提示词与白名单。`copy`/`move`/`delete` 是独立工具而不是 `bash` 一行命令:进程启动比文件操作本身贵得多(基准:600 文件复制 94 ms vs 进程 306 ms,删除 21 ms vs 112 ms),且工作区边界与 `PathGate` 看不到 shell 命令里的路径。
- **文件操作留在进程内。** 读、写、改、列、找、搜全部是原生实现,不搭 `cat`/`sed`/`find` 进程;只有真的需要外部程序时才创建子进程,`grep` 的 ripgrep 后端只是可选的加速,不是前提。
- **常量 schema。** 每个工具的 `InputSchemaJson` 是 `const string`,参数由 `JsonDocument` 读取(无反射、无源生成以外的序列化);两次请求字节相同,提示词缓存不被无意义地打散。参数错误一律转成 `IsError` 结果,只有取消向外抛。
- **可选字符串的空白等于省略。** 可选参数按“空白 = 没给”读,取各自 schema 里写的默认值:`path`(`grep`/`ls`/`find`,经 `ToolJson.OptionalPath`)、`workdir`、`glob`、`pattern`、`git_diff` 的 `base`、`models` 的 `provider`。依据:模型常把未使用的可选字符串填成 `""` 而不是省略它,而把空串当输入错误会让三个只读遍历工具在那类模型手里 100% 失败,报错还只是 `WorkspacePath.Resolve` 那句 .NET 参数守卫的原话(点名内部形参、不给下一步)。`WorkspacePath.Resolve` 自己的空白守卫保持不动——它挡的是调用方漏传,不是工具输入。
- **审批看副作用与边界。** `read`/`ls`/`find`/`grep` 是 `ReadOnly`(策略的只读规则直接放行),`write`/`edit`/`copy`/`move`/`delete` 是 `Write`,`bash` 是 `Execute`。**四个只读工具没有工作区边界**(走 `ResolveReadable`,见「路径」),所以它们不实现 `IToolCallScope`。**文件五件与 `bash` 这六个写/执行工具实现 `IToolCallScope`**:`CallScope` 只看参数的字面拼写(绝对路径落在工作区外、`~`、`..` 逃逸、带 glob 的取第一个通配符前的目录、`bash` 另看 `workdir` 与命令里非开关的词),答 `OutsideWorkspace` 时策略出卡;它**不碰盘**,工作区内链接指向域外由执行时的包含性检查拒绝(错误,不是卡)。治理只能越过边界的执行:只有 `ToolContext.OutOfWorkspaceAllowed`(被允许的域外调用)才把 `allowOutside` 传给 `WorkspacePath.Resolve`/`FileOps.ResolveTarget`/`ResolveEntry`/`PathPatterns.Expand`/`BashTool` 的 `workdir`。**这一步的代价实测**(`tools/Forge.Bench` 的 `approval.scope.*`,i9-14900HX/Win11、Release、n=20):边界判定本身 `approval.scope.write.1KiB` p50 0.001 ms / 352 B、`write.1MiB` 0.109 ms / 352 B(`JsonDocument` 的缓冲池复用,托管分配与 payload 无关)、`approval.scope.bash` 0.002 ms / 1,168 B;1 MiB 写本身(原子写 + fsync)是 3.2–3.8 ms,因此新增部分约 3%,1 KiB 写可忽略。只读调用不计算边界,所以没有 read 行。**W14 起**:`read`/`ls`/`find`/`grep` 进 `run_code` 的脚本注册表,依据就是同一个 `ReadOnly` 判定(主程序按 `SideEffect == ReadOnly` 挑候选,不认名字);`write`/`edit`/`copy`/`move`/`delete`/`bash` 仍永不进,脚本既调不到也看不到。用户在 `tools` 分区关掉其中某个工具后,脚本面也同步去掉它(`Tools.List()` 不列、调用按未知名拒绝)。

### 读与写

- **`read` 有界且按字节算。** 默认窗口 2000 行 / 256 KiB(硬上限 20000 行 / 1 MiB)。行窗口走流式跳过与截取:读 100 行不会把 100 MiB 读进内存(本机实测三种大小的分配都在 14–16 KB)。行号右对齐,宽度按本次窗口最后一行算;表头只在**整读**时写 `<path>: N lines`(N 就是文件行数);带 `offset` 或被 `limit`/`max_bytes` 截断时改写 `<path>: lines 600–729`,`offset` 越过末尾时写 `<path>: the file ends before line N`。文件总行数不扫全文件(与“不得整体读入”冲突),所以窗口读从不声称它是多少——把窗口行数印成文件行数会被读成文件长度(生产库里 75 次截断读都带着那种表头)。单次返回另有 32,000 字符上限(等于循环的 spill 阈值,`read` 不会被转成 artifact):按整行截断,尾部给出已显示的行范围和续读的 `offset`;首行本身超过上限时只截这一行。`read`、`find`、`ls`、`grep` 都实现 `IBoundedOutputTool`,循环不再对它们的结果做 spill。
- **`read` 的整形参数(W1)。** `paths[]`(≤64,一次读多文件)、`pattern`(字面、大小写敏感的行过滤器)+ `context`(0–5,超出截图并自报)、`max_chars`(1000–32000,整份答案的预算)。规则:①`path` 与 `paths` 只能二选一,两者都缺或多都报错;②**单 `path` 且无 `pattern` 时输出与改造前逐字节相同**(热路径与既有断言都不受影响),`max_chars` 只改那个上限;③多文件/`pattern` 模式下每个文件一块,块头写 `<path>: N matching lines`(pattern 模式;窗口被截断时补 `in lines A–B`)或沿用 `<path>: N lines` / `<path>: lines A–B`,每行 `N→text`,非连续片段之间用一行 `…` 隔开;④读不到的文件**就地写一行**(`<path>: does not exist` / `is a directory` / 二进制),其余文件照常返回;全部失败才是 `IsError`;⑤预算用尽时在该文件内截断并写 `… (cut at line N by max_chars; continue that file with offset=N+1)`,其后未读到的文件汇总成 `… (K more files not returned: a, b, c; raise max_chars, read them in a smaller group, or narrow with pattern)`。`pattern` 只过滤调用方点名的文件,**不做仓库发现**:发现文件仍是 `grep` 的职责。每个被读文件在读之前照旧 stat 并记入版本账本。
- **编码策略明确。** 嗅探 BOM(UTF-8 / UTF-16LE / UTF-16BE);无 BOM 时按 UTF-8 **严格**解码,且只严格校验前 8 KiB(此后按宽松解码,因为半行截断本就会切在多字节字符中间)。首 8 KiB 含 `0x00` 或严格解码失败的,判为二进制/非 UTF-8 并报错(带字节数),不返回乱码。
- **`edit` 要求唯一匹配。** 0 次出现、以及多于 1 次又没给 `replace_all`,都返回错误并给出文件行数与前两处行号:猜一处是最坏的结果。匹配在归一 LF 之后做(见下一条行尾契约)。读-改-写整段持同一把 `PathGate` 租约,写回走 `AtomicFile`。
- **行尾只有一个标准:LF。** `read` 交给模型的是 LF 视图(`TextFileReader` 删掉 CRLF 的 `\r` 再按 `\n` 连接;裸 `\r` 不裁也不当换行),所以 `edit` 的匹配也在同一空间里做:`LineEndings.Normalize` 把正文与 `old_string` 的 `\r\n`、裸 `\r` 都归一成 `\n` 再找,命中后整份以 LF 写回;`TextFileWriter`(`write`/`edit` 唯一的写入路径)对 content 做同一次归一,没有调用方能把 `\r` 漏进文件。
  - 不跟文件历史、不按平台、也不读 `.gitattributes`/`.editorconfig`:无探测、无分支,写出的字节只取决于内容,跨平台一致——一致性优先于“保持既有 CRLF 文件不动”是明确的取舍。
  - 归一不需要偏移映射(换行集合不变),所以行号、出现次数、`replace_all` 与原文逐一对得上;没有 `\r` 的文件付一次向量化 `IndexOf` 就返回原实例,零分配。
  - **代价写明**:被工具碰过的 CRLF 文件整份变 LF(git 里存 LF 的仓库看不出差异,真存 CRLF 的仓库会看到整份 diff);`append` 只归一自己追加的那段、不重写已有前缀(重写就把 O(1) 追加变成整份读+整份写);故意造 CRLF 文件得走 `bash` 重定向;CR-only(旧 Mac)文件上 `edit` 的行号与 `read` 的行数不一致——`read` 按 `\n` 切,整个文件是 1 行,而 `edit` 把裸 `\r` 当断行,错误里报的是后者。
  - **为什么不能只做字节精确匹配**:`read` 早就在删 `\r`,模型手里根本没有 `\r`,于是 CRLF 文件上的跨行 `old_string` 恒不命中(生产里就是 `no match for old_string`),那不是模型写错了 old_string,而是工具对这类文件结构性不可用。
  - **一个护栏**:`old_string` 若全由换行组成且带 `\r`(典型是单个 `\r` 想“剥 CR”,这在 LF 标准下已不成立),直接报错而不是当成“匹配每个换行”去执行——否则 `replace_all` + 空 `new_string` 会把整份文件的换行删光。
  - **2026-11-24 实测**(`tools/Forge.Bench`,i9-14900HX/Win11、Release;**只覆盖无 `\r` 的快路径,归一的 rent+copy 分支未测**,而 bench 的 payload 本来就是 LF):`write.atomic.1KiB` 2.258 → 2.32–2.69 ms、`write.atomic.1MiB` 3.549 → 3.20–3.76 ms(3 次运行区间;fsync 主导,单次抖动 ±15%),分配与 GC 不变(5,832 / 2,355 B/op,0/0/0);`ledger.write.4KiB` 同一 run 内的有/无账本对照 +1.2% → +2.0%,仍远低于 1.25x 门槛,而绝对值 46.3 → 64.5 ms 的漂移由同 run 基线同步抵消(45.0 → 62.3 ms)。
- **`edit` 零命中要说为什么。** 在 `no match for old_string in '<path>' (N lines); nothing changed.` 之后最多再加一行:某行与 payload 行完全相同 → `line N matches an old_string line on its own; the surrounding lines are what differ.`;共享前缀 → `closest line N differs at character K: "…" vs "…"`(或 `your line ends at character K; the file continues "…"`);payload 里出现字面 `\` + `r`/`n` 时点名“这是两个字符,不是换行”。一次遍历、无索引,真没共同字符就只说原话。依据:生产里 10 次 `no match` 让模型在“内容写错”和“行尾不对”之间猜了四轮。**文件在你读过之后被改过也要说(2026-11-24)。** 零命中时若账本里本 agent 对该路径的记录与磁盘现状(`FileVersion`:长度 + mtime)不一致,在诊断行之后再补一句 `the file changed after your read (A → B bytes); read it again and edit against what is there now.`——账本只记 `read` 过的文件,没有记录就只说原话。依据:并行 Agent/外部编辑改掉一段巨型单行段落后,`no match` 会把“过时文本”读成“内容写错”,而这是唯一能直接给出下一步动作的诊断(生产里 `src/Forge.Workspace/SPEC.md` 就是这样撞上的)。
- **`edit` 会把 UTF-16 文件写回 UTF-8。** 这是已知偏差:保留原编码要多一套编码探测与回写路径,而仓库里的文本文件几乎全是 UTF-8。已在工具文档与这里写明,不静默。
- **`write` 覆盖检查。** 防止 Agent 用过期的内容整体覆盖别人(其他 Agent、用户、命令)的改动。依据是调用方 Agent 的读取记录 `ToolContext.FileVersions`(见 forge-core;null 时不检查):`路径 → (长度, LastWriteTimeUtc)`。规则:
  - `read`(含 `offset`/`limit` 的部分读取)在读**之前** stat 并记录;读的过程中被改则记下的是旧版本,之后的覆盖被拒,偏安全。
  - `write` 覆盖**已存在**的文件:在同一把 `PathGate` 租约内 stat,本 Agent 无记录或记录与磁盘不符即返回错误、不写(两种措辞:“覆盖前请先 read”与“在你上次读取后已被修改,请重新 read”);写成功后记录新状态。新建文件不检查,写后记录。
  - `edit` 不检查(LF 归一后的精确匹配本身就是乐观并发);写后**仅当编辑前的磁盘状态与本 Agent 的记录一致**才更新记录,否则保留旧记录——否则 Agent 在 v1 读、别人改成 v2、它 `edit` 成 v3 后记成 v3,再用记忆中的 v1 整体 `write` 就会通过检查。`append` 同 `edit`:不检查,记录按同一条件更新。
  - `copy`/`move`/`delete` 的覆盖目标不纳入。
  - 局限:同一时间刻度内长度相同的两次修改比对不出(NTFS 100 ns;FAT、部分网络盘精度粗会漏判)。记录**不设上限、不淘汰**(只增;淘汰会让读过的文件突然要求重读,而这层检查的错误方向本就该偏安全),量级由基准实测给出。
  - `read`/`write` 是热路径,每次多一次 stat 与一次字典操作。**2026-11-19 实测**(`tools/Forge.Bench` 的 `ledger.*`,i9-14900HX / Win11 / Release,每样本 20 次工具调用、跨 10 次运行取中位):`read` 4 KiB 净增 +0.7~1%(≈+3.7 µs、+96 B/次)、`read` 1 MiB 测不出(与对照同量级);覆盖已存在文件的 `write` 净增 +2.1~2.9%(≈+60 µs、+200 B/次,相对原子写+fsync 本身可接受);账本 **276 B/条目**(表 36 B + 105 字符路径 240 B),1 万个不同文件 ≈ 2.6 MiB——这是「不设上限、不淘汰」的量化依据。工具描述补“覆盖已存在文件前须先 read”,工具定义变化使提示词缓存失效一次。
- **`copy`/`move`/`delete` 的契约。** 三者都是 `Write`、路径必须落在工作区内(目标沿用 `write` 的“最深已存在祖先”解析,末段是链接则跟随后再判界;被允许的域外调用除外,见「工具面」)。**三者要能完整替代 bash 的 `cp`/`mv`/`rm`**:`from`/`path` 收单个路径、glob 模式或它们的数组(`PathPatterns`:`* ? [..] ** {a,b}`,以点开头的名字只被同样以点开头的段匹配,`.git` 永不匹配也不进入,匹配到另一匹配之下的项被上层覆盖而去重,模式无匹配报错,不跟随链接,`copy` 不收链接、`move`/`delete` 收链接本身)。单个非模式源时 `to` 是**确切的新路径**;多个源、含模式、或 `to` 以 `/` 结尾时 `to` 是**拷/移进去的目录**(各用自己的名字,重名报错)。所有源与目标先全部校验、再动手(任一不合规整个调用不执行),闸按固定序一次取全。缺的父目录自动建;目标已存在默认报错,`overwrite` 才替换文件/合并进目录(`move` 永不替换目录)。源的最后一段不跟随链接:`delete`/`move` 作用于链接本身,删链接不碰目标;`copy` 目录时树里的链接与 `.git` 项跳过并计数。工作区根与 `.git`(含其下)永不可作 `delete`/`move` 的目标——`.git` 这一条对域外路径同样成立(按完整路径的每一段判定),`copy` 也不碰 `.git`;目录不能复制/移入自身。`delete` 非空目录必须 `recursive`(写错路径只损失一次报错,不损失一棵树),只读文件先去只读属性再删。路径闸只锁顶层路径(`move` 按固定序锁源与目标,防反向对调死锁),不锁树内文件。树遍历每目录只读一次(`FileSystemEnumerable` 直接带出类型与链接位,不额外 stat);文件数 ≥64 时用至多 `min(核数,8)` 线程并行复制/删除(目录先建好、最深目录最后删),失败取首个线程的原异常(不让 `AggregateException` 漏出工具边界)。
- **追加不承诺原子。** `write` 默认整写(同目录临时文件 + `fsync` + 改名,失败不留半文件);`append` 是普通追加,并发追加的字节顺序由 OS 决定。

### 遍历、忽略与搜索

- **遍历不递归、不 LINQ、不整树收集。** `FileWalker` 是显式栈 DFS,只分类当前一层(`FileSystemEnumerable` 从枚举数据里直接拿“是目录/是链接”,不给每项多一次 stat),目录项先于文件项(不排序也读得下去),永不进入 `.git`,单个目录的权限错误跳过而不是让整次遍历失败。
- **不跟随链接。** 目录链接会被列出但不进入,文件链接带着 `IsLink` 上报;`find`/`grep` 跳过 `IsLink` 项,`ls` 用 `l <path>` 单列一指。依据有两条:ripgrep 默认也不跟随链接(两后端才能给同一集合),以及一个检入仓库的链接不应让 `grep` 读到工作区外的文件。显式给 `read`/`write` 的路径跟随后再判界:`write`/`edit` 由 `WorkspacePath.Resolve` 逐段解析守住工作区,`read` 不设界(见「路径」)。
- **忽略规则是完整的 gitignore 语义:** `#` 注释、`\` 转义、被转义的行尾空格、`!` 否定、前导 `/` 锚定、含 `/` 的模式相对其 `.gitignore` 所在目录、目录结尾 `/`、`**`;根 `.git/info/exclude` 参与。被忽略的目录不进入,因此目录内的 `!` 无法重新包含——与 git 一致。不读全局 `core.excludesFile`(推迟,需读 git config)。
- **隐藏项默认可搜。** `.github`、`.editorconfig` 必须搜得到,所以不套用 rg 的“跳过隐藏项”默认。
- **忽略规则作用于起点之下,起点本身照搜。** `grep`/`find` 共用 `SearchFilter`:`.gitignore`(父先子后的目录压栈)加目录名列表 `ToolOptions.IgnoredDirectories`(设置页可编辑,默认 `bin`、`obj`、`.vs`、`node_modules`、`target`、`.gradle`、`.venv`、`__pycache__` 等 19 个,不含 `build`/`dist`/`out`),只过滤起点之下的条目:搜 `src` 剪掉 `src/**/obj`,而 `path=obj` 仍搜 `obj`;名字不区分大小写,空列表不剪。`ls` 不用过滤器——列目录就是回答“这里有什么”。代价写明:用 `find`/`grep` 看构建产物必须点名那个目录。`.git` 与本层硬跳过的项不受这条影响。
- **两个搜索后端必须给同一集合。** 托管后端是默认实现;`RipgrepPath` 非空时用 rg,并以 `--no-ignore-vcs --no-ignore-parent --no-ignore-global --no-ignore-dot --no-ignore-exclude --hidden` 关掉它自己的忽略判断,每条结果再经**同一个** `SearchFilter`(含祖先语义)过滤,rg 另收 `--glob !<name>/` 让它自己不扫这些目录(名字含 glob 元字符时只做结果过滤)。等价性由 fixture 仓库的 parity 测试固定(本机 rg 15.2.0,含“NUL 在探针窗之外”的二进制用例)。托管后端到达 `limit` 后**不停止**,继续只计数不存行,`GrepResult.TotalMatches` 是真实命中行数(代价:截断的查询也要扫完全部文件;本仓库 1306 个文件实测约 15 ms);rg 后端在 `limit` 处停止,`TotalMatches` 为 null,输出写“至少 limit+1”。大小写敏感、无上下文行的字面量在 UTF-8 字节上整缓冲区向量化搜索,只解码命中的行(与逐行字符串路径的结果逐条相等,本仓库 8 个查询已对比),读文件用同步 `RandomAccess`。已知不等价:rg 的 globset 不支持 `[!...]` 取反字符类;`limit` 的截断位置两后端不同(集合一致、顺序不保证);`FilesSkippedTooLarge` 在 rg 侧拿不到时保持 0;`FilesSkippedBinary` 在 rg 侧只能统计“NUL 之前产生过输出”的二进制文件——没命中的二进制文件 rg 不发 `end` 事件,所以那个数字会偏小(托管侧统计所有被跳过的)。命中集合与 `path:line` 文本仍然逐条相等。
- **整形后的实测(W1,`tools/Forge.Bench` 的 `shaping.*`,i9-14900HX/Win11/Release,n=20)。** 一次 `read{paths:20,max_chars:32000}` 5.57 ms vs 20 次单文件 `read` 10.85 ms(0.51×),返回 **32,174 字符** vs 约 16.6 万;一次 `read{paths:3,pattern,context:2}` 1.19 ms vs 3 次整文件读 1.45 ms(0.82×),**1,127 字符** vs 约 2.5 万;一次 `grep{max_per_file:3,total_chars:4000}` 1.35 ms vs 未整形 1.51 ms(0.90×),**4,109 字符** vs 32,000(工具自身上限)。工具侧成本都在毫秒级,差别在进上下文的字符数。

- **`grep` 空结果要能判定。** 无命中时输出固定为 `no matches` + `scanned N file(s)`(0 个时改写成“路径空或全被 .gitignore 排除,用 ls 核对”)、`regex` 未开而 pattern 带正则语法时的一行提示(保守判定:`|`、首 `^`/尾 `$`、`(?`、`.*`、`[^`、`\s`/`\d`/`\w`/`\b`/`\\` 这类只有正则才这么读的转义;单独的 `.` `*` `+` `[` 不触发),以及 pattern 含**大写字母**且 `case_sensitive` 为 true 时的一句 `case_sensitive` 口径提示。命中路径不加这些行。`FilesScanned` 是后端各自的读数(托管侧是打开的字节流数,rg 侧取 `stats.searches`),**不承诺两侧数值相等**——承诺相等的是命中集合。
- **`find`/`ls` 的空结果与截断同样要能判定。** `find` 的否定结果带走过的条目数:一个都没走到时写 `no entries: the directory is empty or excluded by .gitignore — verify with ls`,走了 N 个但 glob 不匹配时写 `no entry matched '<glob>' (walked N entries)`;两个工具的 limit 截断都补 `: raise limit or narrow pattern`。裸 `(empty)` 分不清“glob 没匹配”和“目录被剪空”,这正是 grep 那次修掉的同一个坑。
- **命中行按路径与行号排序输出。** 后端并发跑文件,`GrepResult.Lines` 仍是完成顺序(内存不变),但 `grep` 拼装文本时按 `path`(平台比较)+ 行号排一次。理由是实测:一次命中跨 152 个文件时,未排序的结果就是乱序,模型看不出哪个文件里有什么,于是它又发一次 grep——生产上“grep 之后的下一个调用”中 52% 仍是 grep,其中 39/41 换了 pattern。排序让一次调用就能从上往下读;两后端的输出顺序也因此一致(集合与顺序都不再依赖调度)。
- **`grep` 的整形参数(W1)。** `files_only` 只要文件名单(`<path>: N matching lines`,无行文本);`max_per_file`(1–10000)按文件封顶匹配行,被丢弃匹配的上下文行随之丢弃、保留匹配的上下文照旧保留(上下文先暂存,匹配被接受才释放,不会留下孤立片段);`total_chars`(1000–32000)是整份答案的预算,裁剪顺序**按文件行数从多到少**(同样行数取路径靠后的),所以密集文件吃不掉全部预算。两种截断各自自报:`… (max_per_file=N: K matching line(s) left out)` 与 `… (total_chars=N: K more line(s) left out; raise total_chars or narrow with glob)`。`files_only` 也受 `total_chars` 限制并自报未列出的文件数。未给这三个参数时输出与改造前逐字节相同。
- **数值参数两种策略,取舍标准是“越界会不会改变答案含义”。** 只影响呈现的参数**截到边界并在结果里写明**(`context` 超过 5 就按 5 搜并输出 `context 10 requested, capped at 5`;`bash` 的 `timeout_seconds` 同样截到 1–600);影响“看到了多少东西”的参数**越界即报错**并写明收到的值与合法区间(`limit`、`depth`、`read` 的 `offset`/`limit`/`max_bytes`)。理由:把 10 悄悄当 5 用会让模型以为自己看到了 10 行上下文,后续推理建立在错误前提上;而把 `limit 2000` 悄悄当 1000 用会让它以为这就是全部命中。`context` 曾经属于报错那一侧,生产上因此白烧 6 轮(每次模型改成 5 重试)。
- **二进制口径两边一致。** 第一个 NUL 落在**前 8 KiB**内的文件整体跳过(不产生任何命中);NUL 在探针之外时,只保留 NUL **之前**的命中并停在那儿(托管引擎把扫描截在 NUL,rg 在 `end.binary_offset` 处停止,并丢弃 `absolute_offset >= binary_offset` 的条目)。两种情形都计入 `FilesSkippedBinary`。没有这条,同一查询换后端就会给出不同结果。
- **rg 只是加速,失败就退回。** 未配置、启动失败、退出码 ≥2、输出超过采集上限而没到 `limit`,都回退托管实现并在结果尾部写出原因——宁可慢,也不给模型半截结果。退出码 1 是“没有匹配”,不是错误。
- **并发有界。** 托管 grep 用容量 2×并发 的有界 Channel,消费者固定 `clamp(SearchConcurrency, 1, 8)`;字面量用 `MemoryExtensions.IndexOf`,正则用 `CultureInvariant` + 2 秒 `MatchTimeout`(超时的文件丢弃并列入跳过表)。不为每个文件创建 Task,不对 IO 套 `Task.Run`。

### 进程执行

- **参数永远是向量。** `ArgumentList`,绝不 `string.Join` 拼命令行。
- **Direct 优先。** `CommandLine.TryParseSimple` 引用感知切分,只要出现 `| & ; < > ( ) $ ` * ? [ ] { } ~ # !`、换行、前导 `VAR=` 或未闭合引号就走脚本模式;否则直接启动 `argv[0]`(交给 OS 做 PATH 解析)。`\` 算转义而不算 shell 触发符,否则 Windows 上 `dotnet build src\a.csproj` 会被误判。判定保守:误判成脚本只是慢一点,误判成 direct 才是错的。
- **输出按字节有上限,且上限内继续读。** 从 `StandardOutput.BaseStream`/`StandardError.BaseStream` 读原始字节,`ArrayPool` 复用缓冲,每流独立上限(默认 512 KiB);达到上限后继续读取并**丢弃**,否则子进程会因管道写满而永久阻塞。结束只解码一次(UTF-8,剥 BOM),截断位与真实字节数一起回报。
- **输出解码:UTF-8 优先,非法序列切到系统 OEM 代码页(仅 Windows)。** 跨平台工具(git、dotnet)写 UTF-8,Windows 原生控制台程序(ping、ipconfig、dir)写 OEM 代码页(中文系统 936),固定按 UTF-8 解码会乱码。`ProcessTextDecoding` 对实时尾部(逐流有状态解码器)与最终捕获(整块)采用同一规则:合法 UTF-8 保持 UTF-8,首个非法序列起整流改按 OEM 代码页;上限截断造成的末尾半个字符不算非法。非 Windows 无备用代码页,行为不变(非法字节替换为 U+FFFD)。不改写命令文本,不改 Shell 编码,因此不影响 BashPolicy 分析。已知限制:一条流内 UTF-8 与 OEM 混写时,首个 OEM 字节之后全按 OEM 解码;未做前后基准。
- **stdin 重定向后立即关闭。** 等输入的命令应当立刻拿到 EOF 并退出,而不是挂到超时(这是 Agent 的非交互调用该有的样子)。
- **超时与取消杀整棵树。** `Kill(entireProcessTree: true)` → 有界等待退出(10 s)→ 有界等待泵(3 s,再 1 s 取消读)。取消在清理完成后抛 `OperationCanceledException`;超时返回 `TimedOut` 并带回已捕获输出。进程树清理不是沙箱:后代自己 `setsid` 就能脱离。
- **退出经句柄等待观察,信号随结果回报。** 等待用 `Process.SafeHandle.WaitForExitAsync` 而不是 `Process.WaitForExitAsync`:只有 `ProcessExitStatus` 能区分“被信号杀死”和“自己退出成 137”。`ProcessResult.Signal` 只在操作系统报告时非空:Windows 永不报告(被杀的子进程只有退出码),`PosixSignal` 也只能表示它列出的那 11 个信号，**崩溃信号(SIGSEGV/SIGABRT/SIGBUS)无法命名**,那类死亡仍只有 128+信号 的退出码。`bash` 的退出行只在 `Signal` 非空时补写信号名。
- **启动失败是结果不是异常。** `ProcessRunner.StartFailedExitCode`(-1)加 stderr 里的原因,由 `bash` 呈现为错误结果;模型侧看到的应当是“没能启动”,而不是一次循环中断。
- **`bash` 的描述写明会被接管的命令与谁解析命令串。** 工具描述把下一段那份接管清单写成一行,与 `BashPolicy.BuildFileOwners` 同源:拒绝本身已点名正确工具,但模型只有撞上才知道会被拒(生产库里 19 次拒绝,其中 7 次是 PowerShell 的 `Get-ChildItem`)。描述首句后另写明方言:Windows 上是 `pwsh`(缺它时 `cmd.exe`),Unix 上是 `bash`/`sh`,**与工具名无关**,并要求多行或带嵌套引号的命令先 `write` 成文件再跑——方言原本只在执行结果的 `script (pwsh)` 里出现,模型动手前就写出了 POSIX 风格的 `ls` 和被 PowerShell 吃掉的引号(真实会话里 `python -c "…"` 连撞两次 SyntaxError)。
- **`bash` 只做没有专用工具的事(`BashPolicy`)。** 判据只有一条:**与内置工具重复的才拒绝**,没有工具覆盖的一律放行;没有工具就没有能力,bash 不是后门。
  - 文件读写搜索(`cat/head/tail`、`ls/dir`、`find`、`grep/rg/findstr`、`tee`/`Tee-Object`、`sed -i` 及 PowerShell 同义命令)指向 `read/ls/find/grep/write/edit`,复制/移动/删除(`cp`/`mv`/`rm`、`Copy-Item`/`Move-Item`/`Remove-Item` 及 cmd 同义命令)指向 `copy/move/delete`。但调用者没有对应工具、含 `$VAR`/命令替换这类工具不做的 shell 展开、由管道供数(`… | xargs rm`)或路径在工作区外(`~`、`../x`、别处绝对路径)时不是重复,放行——那里是审批卡的事(`bash` 自己的边界扫描已经为这条路径出过卡),不是这一层的拒绝。
  - **只过滤管道输入、不带文件操作数、不递归**的 `grep/head/tail/findstr/Select-String`(如 `dotnet build | grep error`)不是文件操作,放行——文件工具看不到进程输出。
  - 联网(`curl/wget/Invoke-WebRequest`)指向 `web_fetch`:只放行目标**每一个**位置参数都是回环地址的调用(选项的取值不算目标;测自己起的服务)。
  - `git` 看调用者的工具面(`ToolContext.Tools`,不看角色):不持有任何 `git_*` 工具就整体拒绝(内置子 Agent 都是);持有时,工具已覆盖的子命令(`status/log/diff/fetch`、`switch/checkout`、改动型 `branch`、`pull/push/rebase/merge`)指向对应工具;其余没有工具覆盖的子命令里,只读的(`show/blame/rev-parse/…`)任一 git 工具即放行,改变仓库的(`add/commit/stash/reset/restore/cherry-pick/…`)要求持有写工具(`git_branch` 或 `git_sync`)。
  - `gh pr`、`gh run`、`gh workflow`、`gh api`(能到达同样的端点)只看 `pr_*` 工具:没有任何 `pr_*` 就严格拒绝,持有任一 `pr_*` 则整个 `gh` 全部放行(包括合并与 `gh repo sync`,持有者自担);没有 `pr_*` 时 `gh repo sync` 按 git 规则、`gh` 其余命令不限制。拒绝是错误结果,命令不启动。
  - **解析。** `BashCommandAnalyzer` 按 `; | && || ( ) { }`、换行与反引号切分并记下“来自管道”,剥掉 `env/sudo -u x/timeout 5/xargs/nice/time` 等前缀(含其带值选项),看进 `$(…)` 与反引号(双引号里也看)和一层 `sh -c`/`pwsh -Command`/`cmd /c`。
  - **这是护栏不是沙箱**:脚本文件、`make`、`npm run` 内部再调的命令看不到;`>` 重定向写文件不拦(构建命令常用)。
- **非廉价命令按工作区串行(`ExecutionGate`)。** 构建与测试会抢 `obj/`、`bin/`、测试宿主,并行只会互相失败,所以同一工作区(规范化后的根目录,进程内共享)同一时刻只跑一条;只有 `echo/pwd/cd/whoami/hostname/date/which/where/true/false/printenv`、只读的 `git`(`show/blame/rev-parse/…`;`stash/reset/restore/clean` 会改源码,要排队)与 `dotnet --version` 一类明确廉价的命令免排队,无法识别的命令一律排队(分类只用来放宽)。等锁时通过 `ToolContext.YieldSlot` 交还并行名额,结束后取回(取回用**整轮**的取消令牌,不用这次调用的:只取消一次排队中的调用不会让 Agent 永久少占名额;取回本身要等池子有空位,这段时间工作区锁仍被这条命令占着,不会死锁——其余等锁者都已交还名额);等锁不计入 `timeout_seconds`(超时从拿到锁后算);结果里写“queued: waited Ns for <持有者>”;最长等 10 分钟,超时返回“工作区繁忙”且不运行;取消立即退出并释放。
- **重复的噪声行折叠后再给模型。** 连续 ≥5 行“只有数字不同”的输出(MSBuild 的 `MSB3026` 每重试一行,一条被占用的 dll 能刷上百行)折成首行 + `[×N more lines, same message]`,头部标 `, repeated lines folded`。字节数与截断标记仍是命令真实写出的量;非连续、不足 5 行、有任何非数字差异都原样保留。
- **构建前先探自己的输出(`BuildLockProbe`)。** `dotnet build|publish|test` 带项目路径时,对 `<项目>/bin` 里该项目自己的 `*.dll`（每个输出目录至多 64 个）试开 `FileShare.None`:被占用就在启动前返回 `bash: not started: '<file>' is held open by another process…`,省掉 MSBuild 的 10×1 s 重试（生产里是 77 s 与 119 KB 日志）。解析不出项目、`bin` 不存在、或失败不是共享冲突(只读、权限)都不拦。
- **环境最小化。** 继承父进程后移除仓库定位变量(`GIT_DIR`/`GIT_WORK_TREE`/… 与 `GitCli` 同一份清单),`bash` 再设 `GIT_TERMINAL_PROMPT=0`、`GIT_PAGER=cat`、`PAGER=cat`、`TERM=dumb`、`NO_COLOR=1`;不覆盖 `LC_ALL`/`LANG`。
- **不复用进程。** 每次调用一个进程;不做常驻 shell 或进程池(没有实测收益前不引入,见规范 P2)。

### 路径

- **`ResolveRealPath` 对不存在的尾段原样追加**(不返回最深的已存在祖先):`a/b/c.txt` 全不存在时结果是 `.../a/b/c.txt`,否则 `write` 建新目录会写到错误的位置。这是改造前 `WorkspacePaths` 就有的缺陷,已在此修正。
- **`Resolve(root, requested)` 是工具路径校验的唯一入口:** `GetFullPath` → 逐段解析已存在组件的链接 → 结果必须等于 root 或在 root 之下(Windows 不区分大小写);越界抛 `WorkspacePathException`,工具转成错误结果。两处例外:读走 `ResolveReadable`(域外按 `GetFullPath` + `ResolveRealPath` 取);写侧的 `allowOutside` 只对**已被用户允许的域外调用**关掉包含性检查(链接照旧解析)。工具看到的根也必须是真实路径(`ToolBase.Root` 做 `ResolveRealPath`):`Resolve` 返回真实路径,混用两种拼写会让受守卫的工具在工作区本身经过链接时全部失败(macOS 的 `/var`、Windows 的 junction、subst 盘)。逐段解析让 junction/符号链接无法用来跨出工作区;但它挡不住 TOCTOU(见“边界”)。
- **无边界的一侧是读,不是写。** 四个只读工具(`read`/`ls`/`find`/`grep`)的起点都走 `WorkspacePath.ResolveReadable`:工作区内的相对路径仍走 `Resolve`(`read` 与 `edit` 的版本账本是同一个键),工作区外的路径按 `GetFullPath` + `ResolveRealPath` 解析。域外时报告的路径是**绝对路径**(`read` 本来如此;`ls`/`find` 每行、`grep` 的 `path:line` 都带绝对路径),因为这些名字要能直接交给 `read`/`edit` ——相对拼写会被当成相对工作区解析、读到别处去。`ls`/`find`/`grep` 的起点在域外时用它自己的目录作锚(`FileOps.ReadAnchor`):`.gitignore` 与构建目录名只看那棵树,报告路径再冠上该目录(`grep` 的单文件起点用它的父目录)。代价写明:读取等于进程权限,模型可以读任意可读文件(包缓存里的 `skills/*/SKILL.md` 就是这类)。写入侧(`write`/`edit`/`copy`/`move`/`delete`)的边界自 2026-11-24 起由审批治理:域外目标弹一张卡,允许(或记住)后工具才带着 `allowOutside` 真的写出去;工作区根与 `.git` 不可删/移的守卫不变。
- **逐段解析的开销:** 每段只做一次目录项读取(存在性与是否链接同一次得到;内部段先按目录探测),不再是 `File.Exists`+`Directory.Exists`+链接探测。调用方已持有真实根时用 `Resolve(root, requested, rootIsReal: true)`:根不再重走,位于根下的路径只解析根以下的段;不在根下仍整条解析。`copy`/`move`/`delete` 的单文件三连调用实测 9.9 ms → 2.6 ms(bench `copy.single`,基线为裸 `File.*` 0.9 ms)。
- **同一个路径的写串行。** `PathGate` 用引用计数门,条目归零即移除,长会话不会积累百万条目;跨会话共享一个实例,因为两个会话可能指向同一个目录。

### 回收站服务(`Trash/`)

`ITrashService.MoveToTrashAsync(path)`,`TrashService.CreateDefault()` 按平台选实现。

- **Windows 走 shell 回收站。** `Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(..., UIOption.AllDialogs, SendToRecycleBin)`(`Task.Run` 包装,用户在系统对话框取消抛 `OperationCanceledException`,视为失败)。shell 只在有回收站的位置回收,别处直接永久删除,而压掉对话框(`OnlyErrorDialogs`)时这种删除是静默的,所以两道防线:UNC 路径、卷 GUID 路径与非固定盘(网络、可移动、光驱、RAM、类型读不出)先拒绝、什么都不动,路径上(含该项自身)的符号链接或联接点按其目标所在的卷同样判定(超过 16 跳按环拒绝);对话框保持开启,回收站放不下(超出配额、该盘关了回收站)时 shell 会弹“永久删除?”,用户取消即失败。回收确认框跟随用户的回收站设置(默认不弹)。对话框的所有者由该库自己取 `Process.MainWindowHandle`(即 Forge 主窗口,已从 `Microsoft.VisualBasic.Core` 的 IL 确认),所以开在 Forge 之上。
- **`subst` 盘。** 它没有自己的回收站、却报为固定盘:先读 `%SystemRoot%\System32\subst.exe` 的映射列表(`X:\: => C:\folder`,按 OEM 代码页解码;运行失败或超时 5 秒按无映射处理),把路径换成它映射的真实目录再判定与回收,进真实盘的回收站;换出来的路径不存在而原路径存在(映射读错)时不换,仍由对话框兜底。
- **Linux** 自实现 XDG trash(`$XDG_DATA_HOME/Trash/{files,info}` + `.trashinfo`,缺省 `~/.local/share`,重名加后缀);**macOS** 移到 `~/.Trash`,重名加后缀,跨卷失败如实报错、不退化为删除。路径不存在时抛异常,调用方先判存在。
- 以上都不用 P/Invoke(P/Invoke 只允许在 Host 的 `WorkerProcessOwnership`,见 `architecture.md`)。不对仓库根检出或项目根调用(由 App 的目录归属判定保证)。

### 单实例锁

`SingleInstanceLock.TryAcquire(dataRoot)` 在数据根(按需创建)独占打开 `forge.instance.lock`,成功返回持有到进程结束的 `IDisposable`,已被占用返回 `null`;进程崩溃由 OS 释放句柄,不是“存在即锁”的标记文件。锁文件只放数据根,不放用户工作区。

## 实现约定

上面“关键决策”已定;以下是实现时不得不自行确定、契约里没写死的细节,以本文件为准。

- **临时文件**：与目标同目录，名字 `<文件名>.<guid:N>.tmp`，`FileMode.CreateNew` + `FileShare.None`；写完 `Flush(flushToDisk: true)` 再改名，目标目录不存在则创建。
- **发布已写好的临时文件**：`ReplaceFile(tempPath, destinationPath, options, token)`/`ReplaceFileAsync` 接受调用方写好并 `Flush(flushToDisk: true)` 的临时文件，其余契约（预算解析、重试策略、失败或取消时删 `tempPath`、目标目录按需创建）与 `WriteAllBytes` 一致；非正预算在动任何文件前抛 `ArgumentOutOfRangeException`。编辑器的流式保存因此不必自己 `File.Move`。
- **只读探测的顺序**：发布前、预算计时之前，目标存在且不是目录时依次检查 `File.GetAttributes` 带 `ReadOnly`、（Unix）`File.GetUnixFileMode` 的 `UserWrite|GroupWrite|OtherWrite` 全为 0、（Unix）`new FileStream(dest, Open, Write, FileShare.ReadWrite|Delete)` 立刻 dispose 被拒（与 `File.OpenWrite` 会拒绝的集合一致）；命中即抛带路径的 `UnauthorizedAccessException`，临时文件由既有 `catch` 删除，第（3）条抛其他 `IOException` 不算只读。
- **权限保留**：目标存在且非只读时先 `File.SetUnixFileMode(temporary, File.GetUnixFileMode(destination))` 再改名；读写失败（`IOException`/`UnauthorizedAccessException`/`NotSupportedException`）忽略，Windows 跳过。
- **首次发布走不覆盖的改名**：目标此刻不存在时用 `File.Move(tmp, path, overwrite: false)`；因目标被他人创建而失败（`IOException`，HResult 低 16 位 183 / 80 / 17 且目标现在存在）时回退到覆盖重试循环，不依赖 `File.Exists` 的原子性。
- **重试等待**：前 10 毫秒 `Thread.Yield()`，之后 `Sleep(1)`（可取消时 `WaitHandle.WaitOne(1)`，取消在下一轮开头抛出）；预算从改名开始计时，不含写临时文件与 `fsync`；`Write`/`WriteAsync` 之后、`Flush(flushToDisk: true)` 之前再检查一次令牌。异步路径的 `fsync` 与改名仍是同步的（BCL 无异步 fsync），等待用 `Task.Delay(1, token)`。
- **读取的缺失复查**：`FileNotFoundException`/`DirectoryNotFoundException` 最多复查 16 次（10 毫秒自旋窗口，实测一次缺失读取约 0.15 毫秒），仍缺失返回 `null`；替换竞争类失败（Windows 的访问被拒绝/共享冲突且文件仍在、不是目录、不带 `ReadOnly`）按同一预算重试，耗尽重抛原异常；存在的空文件返回空数组。
- **锁竞争的平台判定**：Windows 认 `IOException`（HResult 低 16 位 32、33）与 `UnauthorizedAccessException`；Unix 认 `EWOULDBLOCK`（Linux 11、macOS/BSD 35）。其他 `IOException`/权限错误不重试；锁路径是目录或带 `ReadOnly` 的文件同样不重试，开锁失败的原始 `UnauthorizedAccessException` 立即上抛。退避从 5 毫秒起翻倍到 100 毫秒封顶，每次在 `[d/2, d]` 内抖动，单次等待不超过剩余预算。
- **锁文件**：`OpenOrCreate` + `ReadWrite` + `FileShare.None`，目录不存在则创建；OS 在进程死亡时释放；本类型永不删除锁文件，也不做“过期锁”清理。
- **`AtomicJsonStore` 的路径与内容**：`Path` 是 `Path.GetFullPath` 之后的值，锁文件为 `Path + ".lock"`；文件缺失或长度为 0 ⇒ `new T()`，非空内容反序列化抛 `JsonException` 或得到 `null` ⇒ `InvalidDataException`（消息含路径）。`Update` 在锁内 `Read` → `mutate` → `SerializeToUtf8Bytes` → `AtomicFile.WriteAllBytes`，`mutate` 返回 `null` 视为错误（`InvalidOperationException`）不写盘；`Stamp` 用 `FileInfo`，文件缺失或路径是目录返回 `null`。
- **构建与分析器**：`IsAotCompatible` 打开，AOT/裁剪分析器零告警；序列化只走调用方提供的 `JsonTypeInfo<T>`。

### 工具实现的常量

上限集中在 `ToolOptions` 里,每个值都有硬上限(偏好只能在硬上限内移动默认值,不能撑开):读 256 KiB / 2000 行(上限 1 MiB / 20000 行);`edit` 整文件上限 8 MiB(硬上限 32 MiB);`bash` 每流 512 KiB(硬上限 4 MiB)、默认超时 120 s(1–600)、全局并发 8;`grep` 1000 条命中(硬上限 10000)、单文件 8 MiB、并发 `min(核数, 8)`;`find` 1000 条(硬上限 10000);`ls` 10000 条;遍历深度上限 64。

读路径的探测窗 8 KiB、读块 64 KiB;进程的退出等待 10 s、泵等待 3 s + 1 s;rg 的 stdout 采集上限按 512 B/命中 缩放并夹在 1–16 MiB。这些都是安全默认,不是性能最优值。

## 验证

- Windows 10/11(`D:/FORGE/work37/harness`,throwaway 控制台):4 读者 + 1 写者、100 KB 负载、200 次改写 ×5 轮(1000 次改写)零读者错误/零半文件/零写失败,写延迟 p50 4.0–4.9 毫秒、p95 7.1–8.0 毫秒;异步路径 100 次改写同样全零。
- 并发读者压测(在 2 核 CPU 限制下复现过写失败):读者在两次读取之间让出 1 毫秒。零间隙地连续重开同一路径会让 Windows 的“改名覆盖”在整个预算内都拿不到目标——那是“已知局限”里的场景,不是本测试要固定的契约;让出后仍能验证真正的不变量:改写进行中读者永远读不到缺失、半文件或混合内容。
- 句柄长期持有:预算 300 毫秒 ⇒ 315 毫秒后抛出原始 `UnauthorizedAccessException`(HResult 0x0005),目标字节不变,无 `.tmp` 残留;目标为目录 ⇒ 2 毫秒内失败不重试。
- 取消(100 毫秒后触发,读者全程持有目标):只计重试循环(`ReplaceFile`/`ReplaceFileAsync`,临时文件事先写好并落盘)时,从触发取消到 `OperationCanceledException` 的净延迟 ≤ 41 毫秒(20 次;Windows 11,2 核 affinity + 4 个并发 `Flush(true)` 写线程 + 2 个自旋线程)。同一条件下整条 `WriteAllBytes` 的取消延迟还包含不可取消的临时文件创建/写入/`fsync`,因此测试只对重试循环设毫秒级上限(5 秒,单次调用被杀毒软件卡住时不误报),对整条路径只断言“在预算之内抛出、且不是预算耗尽”。
- 确定性失败(Windows 11,`dotnet test` 实测):目标为 `ReadOnly` 文件 ⇒ 6 毫秒(异步 98 毫秒)抛原 `UnauthorizedAccessException`,改造前 5007 毫秒;锁路径为目录 ⇒ 约 1 毫秒抛原 `UnauthorizedAccessException`,改造前 2012 毫秒后抛 `TimeoutException`;锁路径为 `ReadOnly` 文件 ⇒ 约 1 毫秒,改造前 2005 毫秒;目标为目录 ⇒ 约 6 毫秒抛 `IOException`(0xB7),原本就立即。
- 只读目标不再被覆盖(两个平台同一断言):`File.SetAttributes(ReadOnly)`(Unix 另设模式 0444)后 `WriteAllBytes`/`WriteAllBytesAsync`/`ReplaceFile`/`ReplaceFileAsync` 都抛 `UnauthorizedAccessException`,耗时 < 预算的一半,目标字节、属性、Unix 模式不变,无 `*.tmp` 残留;`SpecGraph.UpdateFrontmatterAsync` 对只读 spec 文件同样失败且文件字节不变。成功替换后目标 Unix 模式(含 `0640` 这类非默认模式)保持不变。
- 多进程状态文件:同进程 8 个 store 实例 ×100 次 `Update` + 1 个 `dotnet` 子进程 ×100 次 ⇒ 计数 900/900;持有锁的进程被 kill 后锁在约 4 毫秒内可用。
- 更新序列的等待上限按本机实测成本标定,不写死 30 秒:先在同一目录用同一 store 串行跑 6 次 `Update` 取中位数作为单次持久更新成本,上限 = `clamp(次数 × 成本 × 8 + 子进程启动余量, 30 秒, 2 分钟)`。依据:更新被跨进程锁串行,每次更新 = 一次锁开/关 + 一次读 + 一次临时文件创建/写入/`fsync` + 一次改名,总时长就是次数乘单次成本;托管 Windows runner 上 800 次实测 32.8 秒(同一次运行里 900 次那条只用 17.0 秒),固定 30 秒必然偶发超时。
- 退避不构成锁护送:本机 2 核 affinity 下 8 线程 ×100 次 `Update` 为 3.70–5.27 秒;把退避换成“每 1 毫秒轮询同一把锁”的等效实现为 3.57–4.48 秒,不睡眠的自旋版本反而更慢(5.63–5.88 秒)。时间由串行的持久写成本决定,不是握手延迟。
- Linux(Ubuntu 24.04 / WSL2,ext4)同一套 harness 全绿:`rename(2)` 语义下持有句柄的读者不阻塞写入,`EWOULDBLOCK` = 11 的锁竞争判定与超时有效。macOS 未实测(按 BSD 的 `EWOULDBLOCK` = 35 列入竞争判定)。
- Windows `PublishAot=true` + `TrimmerSingleWarn=false` + `TreatWarningsAsErrors=true` 发布零告警,原生二进制可正常读写与加锁。

### 文件、进程与搜索(本机 win-x64,Release)

- `read` 只读 100 行的分配:1 KiB → 16392 B、1 MiB → 16400 B、100 MiB → 14072 B,不随文件大小增长(整读 100 MiB 对照 756 ms、≥100 MiB 分配)。
- `ProcessRunner` 输出 10 MiB、上限 512 KiB:退出码 0、`StdoutBytes` = 10485760、截断为真、分配 402 KB、WorkingSet +4.6 MB;100 MiB 输出 WorkingSet 增量 2.1 MB,内存与输出大小无关。
- 超时 2 s 杀树:子进程与其 `ping` 孙进程均消失;取消(100 ms)后 176 ms 抛 `OperationCanceledException` 且无残留。
- 信号命名只有 POSIX 有:win-x64 上超时杀树后 `Signal` 为空而 `EndReason` 仍为 `TimedOut`;`SIGKILL` 那条断言写进了 `ProcessRunnerTests`,只能在 CI 的 ubuntu 作业上确认,不得先当成已验证。
- 等待只等进程本身不等管道 EOF:孙进程继承重定向 stdout 并持续持有管道时,`Process.WaitForExitAsync` 与 `SafeHandle.WaitForExitAsync` 都在 25–41 ms 返回,改成句柄等待因此不改变“孙进程霸占管道”场景的分类。
- 相对裸 `Process.Start` + `BaseStream` 排空的额外开销:`dotnet --info` 20 次 p50 差值 2.3–10.1 ms(0.9–3.7 %);`cmd /c echo` 落在噪声带内(-2.6…+0.8 ms)。
- 托管 grep 与 rg(`15.2.0`)在 fixture 仓库上 11 组查询的 `path:line` 集合逐条相等;`build/`、`drop.log`、`.git`、二进制、`work/*` + `!work/keep.txt` 两侧都被同样处理。
- 10 万文件(100×1000)的目录:`find`(limit 10000)19 ms / 分配 3.6 MiB;全树无命中遍历 136 ms / 峰值工作集 70 MiB。
- 待跑:Linux/macOS 由 CI 的三 OS 测试矩阵冒烟(`.github/workflows/ci.yml` 已接线,首跑通过前仍算未验证);(ShellResolver 的 `/bin/bash`→`/bin/sh`、进程组杀树、rg parity 均只在本机 Windows 验过)。

### 基准(`tools/Forge.Bench`,win-x64 32 逻辑核,Release,两次全量 18/18 验收通过)

基线与目标场景在同一循环内交替测量、取配对中位差,所以比值稳定而绝对值噪声可达 1.5–2×。

| 场景 | 实测 |
|---|---|
| `read` 100 MiB 全读 | 59.8 ms(1.5–1.6 GB/s);100 MiB 取 100 行 0.071 ms(≈14k 次/秒) |
| `read` 1 MiB 取 100 行 | 0.119 ms vs BCL 0.089 ms(窗口读定开销 +16–31 µs) |
| `write` 1 KiB 原子写 | 2.67 ms vs 直写 0.40 ms(差距全在 `fsync`,是耐久性代价) |
| `write` 1 MiB 原子写 | 4.34 ms vs 直写 5.53 ms(比 BCL 快 21%) |
| `File.Copy` 100 MiB | 31.7 ms(3.09 GB/s) |
| `copy` 600 文件目录树(4 KiB/个) | 94–99 ms,对手写 `File.Copy` 循环 0.42×、对 `cp -r`/`xcopy` 进程 0.31×(顺序版 201 ms,并行 2.2× 加速) |
| `delete` 600 文件目录树 | 21–24 ms,对 `Directory.Delete(recursive)` 0.47×、对 `rm -rf`/`rmdir /s /q` 进程 0.21×(顺序版 35 ms) |
| `FileWalker` 1k/10k/100k | 0.79 / 9.19 / 56.9 ms(100k 为 1.8 M 项/秒);比 `EnumerateFileSystemEntries` 慢 1.7–2.2×,因为它同时给出全路径、相对路径与目录/链接分类 |
| 托管 `grep` 10k 文件(14 MB) | 命中 114 ms / 无命中 121 ms(≈85k 文件/秒);rg 103–105 ms,即慢 11–15%,命中数 420/420 相同 |
| `ProcessRunner` 短命令 | p50 19.6 ms,直接 `Process.Start` 19.9 ms(启动开销主导) |
| `ProcessRunner` 长命令 | p50 216.3 ms,直接 219.7 ms(配对差 −1.7 ms / −0.8%) |
| 100 MiB 子进程输出 | 69.8 ms(1.40 GB/s)、分配 1.39 MB、工作集增量 < 6 MiB |
| 并发 | 4×1 MiB 并行读 0.786 ms(聚合 4.97 GB/s);1/2/4/8 并发进程 p50 22.6/22.5/32.4/45.4 ms,0 失败 |

结论:大文件读吞吐远高于 BCL 基线(1012–1545%)、`ProcessRunner` 相对直接 Process 的额外开销 −0.8…−0.9 %、100 MiB 输出内存与输出大小无关,四项验收全部达成。

## 未决

- 全局 `core.excludesFile` 与 `.gitmodules` 未参与忽略判定(需要读 git config)。
- `edit` 把 UTF-16 文件写回 UTF-8;保留原编码未做。
- 行尾只认 LF:被工具编辑或重写过的 CRLF/CR 文件整份变 LF,`write` 也无法产出 CRLF 内容(需 `bash` 重定向)。想按仓库声明(`.gitattributes` 的 `eol=`、`.editorconfig` 的 `end_of_line`)改写 content 未做——那是一套 glob 与多级优先级的子系统。
- ripgrep 不随应用分发,只接受偏好里给出的路径;是否按 RID 打包待基准数据决定(当前托管实现只比 rg 慢 11–15%)。
- `GitCli`/`GhCli`/`ScreenshotShell` 仍各自实现进程执行与捕获,尚未改为复用 `ProcessRunner`(它们有各自的 head/tail 与逐行回调契约,单独立项)。
- `bash` 的输出只在结束时一次性返回,不流式进 UI;`ls` 的截断发生在遍历顺序上而不是排序之后(保留的是遍历顺序的前 N 条,再排序)。
- 已知分配成本(基准实测):`FileWalker` 约 353 B/文件(全路径 + 相对路径 + 分类);托管 `grep` 一次 1 万文件扫描分配约 82 MB、gen0 92 次,主因是每个文件 `rent + Encoding.UTF8.GetString` 整文件成串。两者都不影响内存上界(逐文件释放),但值得在需要时改成按行跨字节解码与复用相对路径缓冲。
- 回收站服务:过期清理、容量上限、网络盘导出兜底与 jsonl 镜像联动均推迟。

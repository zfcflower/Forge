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
- **文件**(`Files/`):`FileWalker` 惰性 DFS 遍历;`TextFileReader` 有界窗口读;`TextFileWriter` 原子整写与追加。
- **进程**(`Process/`):`ProcessRunner` 有界字节捕获的进程执行;`ShellResolver` 平台 shell 解析;`CommandLine` 引用感知切分与 direct/script 判定。
  - **实时输出尾部**:`ProcessRequest.OnOutput`(空则零开销)收到 stdout/stderr 按到达顺序合并后的最新尾部快照(≤ `OutputTailChars`,默认 16 KiB),至多每 `OutputInterval`(默认 250 ms)一次,间隔内到达的输出由一次性定时器补报;每路一个 UTF-8 `Decoder`;`ProcessOutputTail` 在持锁时调用监听者,所以快照严格有序、不并发,监听者必须不阻塞、抛异常被吞;进程退出时未报的尾部丢弃(结果含全部输出)。
- **搜索**(`Search/`):`GlobMatcher`、`GitIgnore`(完整 gitignore 语义)、`GrepEngine`(托管)、`RipgrepBackend`(可选加速)。
- **工具**(`Tools/`):`read`/`write`/`edit`/`ls`/`find`/`grep`/`bash` 七个 `ITool`、`FileTools` 聚合、`ToolOptions`、`ToolBase`。
- `AtomicFile`:无锁、可用于用户工作区文件。写临时文件、落盘、改名覆盖;读取带 `FileShare.ReadWrite | Delete`。对外两组入口:整份写入 `WriteAllBytes`/`WriteAllBytesAsync`,以及只做“发布”的 `ReplaceFile`/`ReplaceFileAsync`(给已经流式写好并落盘的临时文件用)。
- `AtomicJsonStore<T>` + `FileLock`:仅用于本程序自有、多进程共享的小状态文件(设置等)。每次更新在跨进程锁内重读最新值再应用修改,避免后写覆盖先写。用户工作区文件不可加锁(其他程序不遵守)。
- 审批记录不使用本组件,改用 SQLite。
- **不管**:审批与权限交互(App/Core)、Git 与 PR(Workspace)、进程隔离与沙箱。`WorkspacePath` 只挡意外与明显越界,不是安全沙箱:能在工作区内建链接并抢在检查后替换的调用方仍可逃逸(TOCTOU)。

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

- **只给七个工具,名字对齐提示词。** `read`/`write`/`edit`/`ls`/`find`/`grep`/`bash` 的名字与 `Forge.Core.Prompts.BuiltInAgents` 的白名单逐字一致(Scout 与 Plan 用前面四个,Worker 再加 `edit`/`write`/`bash`;Reviewer 是前面四个加只读的 `git_diff`,它不属本模块,见 forge-core),所以工具落地不需要改提示词与白名单。不提供 `copy`/`move`/`delete` 独立工具:它们是 `bash` 的一行命令,而工具数量本身就是模型的选择成本。
- **文件操作留在进程内。** 读、写、改、列、找、搜全部是原生实现,不搭 `cat`/`sed`/`find` 进程;只有真的需要外部程序时才创建子进程,`grep` 的 ripgrep 后端只是可选的加速,不是前提。
- **常量 schema。** 每个工具的 `InputSchemaJson` 是 `const string`,参数由 `JsonDocument` 读取(无反射、无源生成以外的序列化);两次请求字节相同,提示词缓存不被无意义地打散。参数错误一律转成 `IsError` 结果,只有取消向外抛。
- **审批只看副作用。** `read`/`ls`/`find`/`grep` 是 `ReadOnly`(走 `DefaultApprovalPolicy` 的自动放行),`write`/`edit` 是 `Write`,`bash` 是 `Execute`。**W14 起**:`read`/`ls`/`find`/`grep` 进 `run_code` 的脚本注册表,依据就是同一个 `ReadOnly` 判定(主程序按 `SideEffect == ReadOnly` 挑候选,不认名字);`write`/`edit`/`bash` 仍永不进,脚本既调不到也看不到。用户在 `tools` 分区关掉其中某个工具后,脚本面也同步去掉它(`Tools.List()` 不列、调用按未知名拒绝)。

### 读与写

- **`read` 有界且按字节算。** 默认窗口 2000 行 / 256 KiB(硬上限 20000 行 / 1 MiB)。行窗口走流式跳过与截取:读 100 行不会把 100 MiB 读进内存(本机实测三种大小的分配都在 14–16 KB)。行号右对齐,宽度按本次窗口最后一行算;`<path>: N lines` 里的 N 是**本次窗口行数**而不是文件总行数——总行数要扫全文件,与“不得整体读入”直接冲突。
- **编码策略明确。** 嗅探 BOM(UTF-8 / UTF-16LE / UTF-16BE);无 BOM 时按 UTF-8 **严格**解码,且只严格校验前 8 KiB(此后按宽松解码,因为半行截断本就会切在多字节字符中间)。首 8 KiB 含 `0x00` 或严格解码失败的,判为二进制/非 UTF-8 并报错(带字节数),不返回乱码。
- **`edit` 要求唯一匹配。** 0 次出现、以及多于 1 次又没给 `replace_all`,都返回错误并给出文件行数与前两处行号:猜一处是最坏的结果。读-改-写整段持同一把 `PathGate` 租约,写回走 `AtomicFile`。
- **`edit` 会把 UTF-16 文件写回 UTF-8。** 这是已知偏差:保留原编码要多一套编码探测与回写路径,而仓库里的文本文件几乎全是 UTF-8。已在工具文档与这里写明,不静默。
- **追加不承诺原子。** `write` 默认整写(同目录临时文件 + `fsync` + 改名,失败不留半文件);`append` 是普通追加,并发追加的字节顺序由 OS 决定。

### 遍历、忽略与搜索

- **遍历不递归、不 LINQ、不整树收集。** `FileWalker` 是显式栈 DFS,只分类当前一层(`FileSystemEnumerable` 从枚举数据里直接拿“是目录/是链接”,不给每项多一次 stat),目录项先于文件项(不排序也读得下去),永不进入 `.git`,单个目录的权限错误跳过而不是让整次遍历失败。
- **不跟随链接。** 目录链接会被列出但不进入,文件链接带着 `IsLink` 上报;`find`/`grep` 跳过 `IsLink` 项,`ls` 用 `l <path>` 单列一指。依据有两条:ripgrep 默认也不跟随链接(两后端才能给同一集合),以及一个检入仓库的链接不应让 `grep` 读到工作区外的文件。显式给 `read`/`write` 的路径仍会跟随链接,但由 `WorkspacePath.Resolve` 的逐段解析守住工作区边界。
- **忽略规则是完整的 gitignore 语义:** `#` 注释、`\` 转义、被转义的行尾空格、`!` 否定、前导 `/` 锚定、含 `/` 的模式相对其 `.gitignore` 所在目录、目录结尾 `/`、`**`;规则按目录压栈(父先子后、同层后写覆盖先写),根 `.git/info/exclude` 参与。被忽略的目录不进入,因此目录内的 `!` 无法重新包含——与 git 一致。不读全局 `core.excludesFile`(推迟,需读 git config)。
- **隐藏项默认可搜。** `.github`、`.editorconfig` 必须搜得到,所以不套用 rg 的“跳过隐藏项”默认;`ls` 对自己显式列出的目录**不**应用忽略规则(用户已经指名要它),`find`/`grep` 应用全部规则。
- **两个搜索后端必须给同一集合。** 托管后端是默认实现;`RipgrepPath` 非空时用 rg,并以 `--no-ignore-vcs --no-ignore-parent --no-ignore-global --no-ignore-dot --no-ignore-exclude --hidden` 关掉它自己的忽略判断,每条结果再经**同一个** `GitIgnore`(含祖先语义)过滤。等价性由 fixture 仓库的 parity 测试固定(本机 rg 15.2.0,含“NUL 在探针窗之外”的二进制用例)。已知不等价:rg 的 globset 不支持 `[!...]` 取反字符类;`limit` 的截断位置两后端不同(集合一致、顺序不保证);`FilesSkippedTooLarge` 在 rg 侧拿不到时保持 0;`FilesSkippedBinary` 在 rg 侧只能统计“NUL 之前产生过输出”的二进制文件——没命中的二进制文件 rg 不发 `end` 事件,所以那个数字会偏小(托管侧统计所有被跳过的)。命中集合与 `path:line` 文本仍然逐条相等。
- **二进制口径两边一致。** 第一个 NUL 落在**前 8 KiB**内的文件整体跳过(不产生任何命中);NUL 在探针之外时,只保留 NUL **之前**的命中并停在那儿(托管引擎把扫描截在 NUL,rg 在 `end.binary_offset` 处停止,并丢弃 `absolute_offset >= binary_offset` 的条目)。两种情形都计入 `FilesSkippedBinary`。没有这条,同一查询换后端就会给出不同结果。
- **rg 只是加速,失败就退回。** 未配置、启动失败、退出码 ≥2、输出超过采集上限而没到 `limit`,都回退托管实现并在结果尾部写出原因——宁可慢,也不给模型半截结果。退出码 1 是“没有匹配”,不是错误。
- **并发有界。** 托管 grep 用容量 2×并发 的有界 Channel,消费者固定 `clamp(SearchConcurrency, 1, 8)`;字面量用 `MemoryExtensions.IndexOf`,正则用 `CultureInvariant` + 2 秒 `MatchTimeout`(超时的文件丢弃并列入跳过表)。不为每个文件创建 Task,不对 IO 套 `Task.Run`。

### 进程执行

- **参数永远是向量。** `ArgumentList`,绝不 `string.Join` 拼命令行。
- **Direct 优先。** `CommandLine.TryParseSimple` 引用感知切分,只要出现 `| & ; < > ( ) $ ` * ? [ ] { } ~ # !`、换行、前导 `VAR=` 或未闭合引号就走脚本模式;否则直接启动 `argv[0]`(交给 OS 做 PATH 解析)。`\` 算转义而不算 shell 触发符,否则 Windows 上 `dotnet build src\a.csproj` 会被误判。判定保守:误判成脚本只是慢一点,误判成 direct 才是错的。
- **输出按字节有上限,且上限内继续读。** 从 `StandardOutput.BaseStream`/`StandardError.BaseStream` 读原始字节,`ArrayPool` 复用缓冲,每流独立上限(默认 512 KiB);达到上限后继续读取并**丢弃**,否则子进程会因管道写满而永久阻塞。结束只解码一次(UTF-8,剥 BOM),截断位与真实字节数一起回报。
- **stdin 重定向后立即关闭。** 等输入的命令应当立刻拿到 EOF 并退出,而不是挂到超时(这是 Agent 的非交互调用该有的样子)。
- **超时与取消杀整棵树。** `Kill(entireProcessTree: true)` → 有界等待退出(10 s)→ 有界等待泵(3 s,再 1 s 取消读)。取消在清理完成后抛 `OperationCanceledException`;超时返回 `TimedOut` 并带回已捕获输出。进程树清理不是沙箱:后代自己 `setsid` 就能脱离。
- **启动失败是结果不是异常。** `ProcessRunner.StartFailedExitCode`(-1)加 stderr 里的原因,由 `bash` 呈现为错误结果;模型侧看到的应当是“没能启动”,而不是一次循环中断。
- **环境最小化。** 继承父进程后移除仓库定位变量(`GIT_DIR`/`GIT_WORK_TREE`/… 与 `GitCli` 同一份清单),`bash` 再设 `GIT_TERMINAL_PROMPT=0`、`GIT_PAGER=cat`、`PAGER=cat`、`TERM=dumb`、`NO_COLOR=1`;不覆盖 `LC_ALL`/`LANG`。
- **不复用进程。** 每次调用一个进程;不做常驻 shell 或进程池(没有实测收益前不引入,见规范 P2)。

### 路径

- **`ResolveRealPath` 对不存在的尾段原样追加**(不返回最深的已存在祖先):`a/b/c.txt` 全不存在时结果是 `.../a/b/c.txt`,否则 `write` 建新目录会写到错误的位置。这是改造前 `WorkspacePaths` 就有的缺陷,已在此修正。
- **`Resolve(root, requested)` 是唯一的入口:** `GetFullPath` → 逐段解析已存在组件的链接 → 结果必须等于 root 或在 root 之下(Windows 不区分大小写);越界抛 `WorkspacePathException`,工具转成错误结果。工具看到的根也必须是真实路径(`ToolBase.Root` 做 `ResolveRealPath`):`Resolve` 返回真实路径,混用两种拼写会让 `read`/`write`/`edit` 在工作区本身经过链接时全部失败(macOS 的 `/var`、Windows 的 junction、subst 盘)。逐段解析让 junction/符号链接无法用来跨出工作区;但它挡不住 TOCTOU(见“边界”)。
- **同一个路径的写串行。** `PathGate` 用引用计数门,条目归零即移除,长会话不会积累百万条目;跨会话共享一个实例,因为两个会话可能指向同一个目录。

### 回收站服务(`Trash/`)

`ITrashService.MoveToTrashAsync(path)`,`TrashService.CreateDefault()` 按平台选实现:Windows `Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(..., UIOption.AllDialogs, SendToRecycleBin)`(`Task.Run` 包装,用户在系统对话框取消抛 `OperationCanceledException`,视为失败)。shell 只在有回收站的位置回收,别处直接永久删除,而压掉对话框(`OnlyErrorDialogs`)时这种删除是静默的,所以两道防线:UNC 路径、卷 GUID 路径与非固定盘(网络、可移动、光驱、RAM、类型读不出)先拒绝、什么都不动,路径上(含该项自身)的符号链接或联接点按其目标所在的卷同样判定(超过 16 跳按环拒绝);对话框保持开启,回收站放不下(超出配额、该盘关了回收站)时 shell 会弹“永久删除?”,用户取消即失败。回收确认框跟随用户的回收站设置(默认不弹)。对话框的所有者由该库自己取 `Process.MainWindowHandle`(即 Forge 主窗口,已从 `Microsoft.VisualBasic.Core` 的 IL 确认),所以开在 Forge 之上。`subst` 盘没有自己的回收站、却报为固定盘:先读 `%SystemRoot%\System32\subst.exe` 的映射列表(`X:\: => C:\folder`,按 OEM 代码页解码;运行失败或超时 5 秒按无映射处理),把路径换成它映射的真实目录再判定与回收,进真实盘的回收站;换出来的路径不存在而原路径存在(映射读错)时不换,仍由对话框兜底。以上都不用 P/Invoke(P/Invoke 只允许在 Host 的 `WorkerProcessOwnership`,见 `architecture.md`)。Linux 自实现 XDG trash(`$XDG_DATA_HOME/Trash/{files,info}` + `.trashinfo`,缺省 `~/.local/share`,重名加后缀);macOS 移到 `~/.Trash`,重名加后缀,跨卷失败如实报错、不退化为删除。路径不存在时抛异常,调用方先判存在。零 P/Invoke。不对仓库根检出或项目根调用(由 App 的目录归属判定保证)。

### 单实例锁

`SingleInstanceLock.TryAcquire(dataRoot)` 在数据根(按需创建)独占打开 `forge.instance.lock`,成功返回持有到进程结束的 `IDisposable`,已被占用返回 `null`;进程崩溃由 OS 释放句柄,不是“存在即锁”的标记文件。锁文件只放数据根,不放用户工作区。

## 实现约定

上面“关键决策”已定;以下是实现时不得不自行确定、契约里没写死的细节,以本文件为准。

- **临时文件**:与目标同目录,名字 `<文件名>.<guid:N>.tmp`,`FileMode.CreateNew` + `FileShare.None`;写完 `Flush(flushToDisk: true)` 再改名。目标目录不存在则创建。
- **发布已写好的临时文件**:`ReplaceFile(tempPath, destinationPath, options, token)` / `ReplaceFileAsync` 由调用方提供已写完并已 `Flush(flushToDisk: true)` 的临时文件,其余契约与 `WriteAllBytes` 完全一致——预算解析与校验(非正预算在动任何文件前抛 `ArgumentOutOfRangeException`)、同一重试策略、失败或取消时删除 `tempPath`、目标保持原内容、目标目录不存在则创建。`WriteAllBytes`/`WriteAllBytesAsync` 写临时文件成功后走的就是这条路径;流式写入者(编辑器保存)因此不必自己 `File.Move`。
- **只读目标的探测细节**:发布函数开头、估算预算计时之前检查目标是否存在(目标不存在或本身是目录时跳过,目录失败仍由改名自己抛)。判定顺序:(1) `File.GetAttributes` 带 `ReadOnly`;(2) 仅 Unix: `File.GetUnixFileMode` 的 `UserWrite|GroupWrite|OtherWrite` 全为 0;(3) 仅 Unix: `new FileStream(dest, Open, Write, FileShare.ReadWrite|Delete)` 立刻 dispose,抛 `UnauthorizedAccessException` 则算只读。第(2)(3)条合起来覆盖 `File.SetAttributes(ReadOnly)` 的 chmod 语义、以 root 运行以及 ACL 拒绝,并保持与 `File.OpenWrite` 会拒绝的集合一致;(3) 抛其他 `IOException`(如另一进程正持有)不当只读,交给改名与既有重试策略。命中后抛新建的 `UnauthorizedAccessException`(消息含路径),临时文件由既有 `catch` 删除:同步与异步、`WriteAllBytes` 与 `ReplaceFile` 一致。
- **权限保留的实现**:同一次发布前的检查里,目标存在且非只读时先 `File.SetUnixFileMode(temporary, File.GetUnixFileMode(destination))` 再改名;读取或设置失败(`IOException`/`UnauthorizedAccessException`/`NotSupportedException`)忽略,Windows 直接跳过。
- **首次发布走不覆盖的改名**:目标此刻不存在时用 `File.Move(tmp, path, overwrite: false)`;若因目标在此期间被别人创建而失败(`IOException`,HResult 低 16 位 183 / 80 / 17 且目标现在存在),回退到覆盖重试循环。不依赖 `File.Exists` 判断的原子性,回退由真实异常触发。
- **重试等待**:前 10 毫秒 `Thread.Yield()`;之后 `Sleep(1)`(令牌可取消时改为 `WaitHandle.WaitOne(1)`,取消在下一轮循环开头立即抛出)。预算从**改名开始时**计时,不包含写临时文件与 `fsync`。写临时文件的 `Write`/`WriteAsync` 之后、`Flush(flushToDisk: true)` 之前检查一次令牌,使取消不必再付一次落盘。
- **异步写入**:写临时文件用异步 IO,`fsync` 与改名是同步的(BCL 无异步 fsync);重试等待为 `Task.Delay(1, token)`。取消/失败同样删除临时文件。
- **读取的缺失复查**:`FileNotFoundException` / `DirectoryNotFoundException` 最多复查 16 次(10 毫秒自旋窗口内,实测一次缺失读取约 0.15 毫秒),仍缺失才返回 `null`;耗尽预算时若最后一次仍是“缺失”也返回 `null`(异常本身就证明名字不在)。替换竞争类失败(Windows 的访问被拒绝/共享冲突,且文件仍在、不是目录、不带 `ReadOnly`)按同一预算重试,耗尽重抛原异常。存在的空文件返回空数组。
- **锁竞争的平台判定**:Windows 认 `IOException`(HResult 低 16 位 32、33)与 `UnauthorizedAccessException`;Unix 认 `EWOULDBLOCK`(Linux 11、macOS/BSD 35,即 .NET 把 `FileShare.None` 映射到的 advisory lock 冲突)。其他 `IOException`/权限错误不重试;锁路径是目录或带 `ReadOnly` 的文件时同样不重试,开锁失败的原始 `UnauthorizedAccessException` 立即上抛(而不是等超时后包成 `TimeoutException`)。退避从 5 毫秒起翻倍到 100 毫秒封顶,每次在 `[d/2, d]` 内抖动,单次等待不超过剩余预算,因此超时最多超出约一次退避。
- **锁文件**:`OpenOrCreate` + `ReadWrite` + `FileShare.None`,目录不存在则创建;由 OS 在进程死亡时释放;本类型永不删除锁文件,也不做“过期锁”清理。
- **`AtomicJsonStore` 的路径与内容**:`Path` 是 `Path.GetFullPath` 之后的值,锁文件为 `Path + ".lock"`。文件缺失或长度为 0 ⇒ `new T()`;非空内容反序列化抛 `JsonException` 或得到 `null` ⇒ `InvalidDataException`(消息含路径)。`Update` 在锁内 `Read` → `mutate` → `SerializeToUtf8Bytes` → `AtomicFile.WriteAllBytes`;`mutate` 返回 `null` 视为错误(`InvalidOperationException`),不写盘。`Stamp` 用 `FileInfo`,文件缺失(或路径是目录)返回 `null`。
- **构建与分析器**:`IsAotCompatible` 打开,AOT/裁剪分析器零告警;序列化只走调用方提供的 `JsonTypeInfo<T>`。

### 工具实现的常量

上限集中在 `ToolOptions` 里,每个值都有硬上限(偏好只能在硬上限内移动默认值,不能撑开):读 256 KiB / 2000 行(上限 1 MiB / 20000 行);`edit` 整文件上限 8 MiB(硬上限 32 MiB);`bash` 每流 512 KiB(硬上限 4 MiB)、默认超时 120 s(1–600)、全局并发 8;`grep` 1000 条命中、单文件 8 MiB、并发 `min(核数, 8)`;`find` 1000 条;`ls` 10000 条;遍历深度上限 64。

读路径的探测窗 8 KiB、读块 64 KiB;进程的退出等待 10 s、泵等待 3 s + 1 s;rg 的 stdout 采集上限按 512 B/命中 缩放并夹在 1–16 MiB。这些都是安全默认,不是性能最优值。

## 验证

- Windows 10/11(`D:/FORGE/work37/harness`,throwaway 控制台):4 读者 + 1 写者、100 KB 负载、200 次改写 ×5 轮(1000 次改写)零读者错误/零半文件/零写失败,写延迟 p50 4.0–4.9 毫秒、p95 7.1–8.0 毫秒;异步路径 100 次改写同样全零。
- 并发读者压测(`AtomicFileWriteTests`,在 2 核 CPU 限制下复现过写失败):读者在两次读取之间让出 1 毫秒。零间隙地连续重开同一路径会让 Windows 的“改名覆盖”在整个预算内都拿不到目标——那是“已知局限”里的场景,不是本测试要固定的契约;让出后仍能验证真正的不变量:改写进行中读者永远读不到缺失、半文件或混合内容。
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
- 相对裸 `Process.Start` + `BaseStream` 排空的额外开销:`dotnet --info` 20 次 p50 差值 2.3–10.1 ms(0.9–3.7 %);`cmd /c echo` 落在噪声带内(-2.6…+0.8 ms)。
- 托管 grep 与 rg(`15.2.0`)在 fixture 仓库上 11 组查询的 `path:line` 集合逐条相等;`build/`、`drop.log`、`.git`、二进制、`work/*` + `!work/keep.txt` 两侧都被同样处理。
- 10 万文件(100×1000)的目录:`find`(limit 10000)19 ms / 分配 3.6 MiB;全树无命中遍历 136 ms / 峰值工作集 70 MiB。
- 待跑:Linux/macOS 由 CI 冒烟(ShellResolver 的 `/bin/bash`→`/bin/sh`、进程组杀树、rg parity 均只在本机 Windows 验过)。

### 基准(`tools/Forge.Bench`,win-x64 32 逻辑核,Release,两次全量 18/18 验收通过)

基线与目标场景在同一循环内交替测量、取配对中位差,所以比值稳定而绝对值噪声可达 1.5–2×。

| 场景 | 实测 |
|---|---|
| `read` 100 MiB 全读 | 59.8 ms(1.5–1.6 GB/s);100 MiB 取 100 行 0.071 ms(≈14k 次/秒) |
| `read` 1 MiB 取 100 行 | 0.119 ms vs BCL 0.089 ms(窗口读定开销 +16–31 µs) |
| `write` 1 KiB 原子写 | 2.67 ms vs 直写 0.40 ms(差距全在 `fsync`,是耐久性代价) |
| `write` 1 MiB 原子写 | 4.34 ms vs 直写 5.53 ms(比 BCL 快 21%) |
| `File.Copy` 100 MiB | 31.7 ms(3.09 GB/s) |
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
- ripgrep 不随应用分发,只接受偏好里给出的路径;是否按 RID 打包待基准数据决定(当前托管实现只比 rg 慢 11–15%)。
- `GitCli`/`GhCli`/`ScreenshotShell` 仍各自实现进程执行与捕获,尚未改为复用 `ProcessRunner`(它们有各自的 head/tail 与逐行回调契约,单独立项)。
- `bash` 的输出只在结束时一次性返回,不流式进 UI;`ls` 的截断发生在遍历顺序上而不是排序之后。
- 已知分配成本(基准实测):`FileWalker` 约 353 B/文件(全路径 + 相对路径 + 分类);托管 `grep` 一次 1 万文件扫描分配约 82 MB、gen0 92 次,主因是每个文件 `rent + Encoding.UTF8.GetString` 整文件成串。两者都不影响内存上界(逐文件释放),但值得在需要时改成按行跨字节解码与复用相对路径缓冲。
- 回收站服务:过期清理、容量上限、网络盘导出兖底与 jsonl 镜像联动均推迟。

---
id: forge-scripting
type: module-design
status: draft
title: Forge.Scripting
parent: forge-architecture
---

# Forge.Scripting

Extension Host 的动态 C# 插件编译/缓存/加载层:Roslyn 编译、源码哈希缓存、版本化产物、可卸载 AssemblyLoadContext。JIT 侧项目(不 AOT),只依赖 BCL + Roslyn + `Forge.Plugin.Sdk`。

## 边界

- 插件是普通类库(`IPlugin` 入口),不用 CSharpScript;不提供插件发现、注册、manifest 读取(属 ExtensionHost)。
- 不做语法白名单/安全分析:权限由 `forge-approval` 审批与 Worker 进程隔离承担,ALC 只负责加载与卸载。

## 关键决策

**两级哈希。** `SourceHash` 始终由编译器从归一化源码(LF、去 BOM、去单尾换行)计算,供审批;调用方无法设置或替换它。需要携带已知/已批准哈希时用独立字段 `ExpectedSourceHash`:它只做校验,与计算值不一致则该次编译以 `FORGE0004` 失败,而结果里的 `SourceHash` 仍是计算值。`CacheKey` = 归一化源码摘要 + `ExpectedSourceHash`(有无与取值)+ 程序集名 + 契约版本 + 编译设置(含 `Usings` 的 null 与空列表之别)+ 引用集(含无法解析的引用),列表按条数与长度分帧,避免 `[System, P]` 与 `[System]` + 引用 `P` 撞键;期望哈希计入 key,使校验失败的结果不会顶替合法请求的缓存条目。确定性编译(同请求 → 同字节镜像)。

**版本化产物。** `<base>/<pluginId>/<version>/`,原子写,已存在版本绝不覆盖;防路径穿越。

**可卸载 ALC。** 共享集只有 TPA、`Forge.Plugin.Sdk` 与显式配置的共享程序集;宿主任意已加载(非 TPA)程序集不因“已加载”而被共享,因此插件自带的同名依赖仍绑定插件目录里的副本。路径模式下插件依赖经 `AssemblyDependencyResolver` + 平铺目录探测;`BeginUnload` 与 `WaitForCollectionAsync` 分离,弱引用确认回收,并且只在已卸载上下文的依赖文件句柄确实释放后才返回 `true`(超时未确认返回 `false`)。

**已验证镜像模式。** `PluginLoadContext.FromVerifiedImages` 接受“程序集简单名 → 已验证字节”的集合:该模式下所有托管依赖只从内存 `LoadFromStream`,不探测插件目录,也不读 `.deps.json`(既不建 resolver 也不回退路径),因此依赖在任意时刻(包括工具调用中途)首次被解析都只可能是被校验过的那份字节。非共享且未提供的依赖以 `FileNotFoundException` 指名失败,不回退到磁盘。**本地库在该模式下根本不装载**:`LoadUnmanagedDll` 不返回零(返回零等于交回默认探测,会去插件目录或操作系统里找),而是抛 `DllNotFoundException` 指名该库并说明已验证插件不支持本地库——因为插件程序集里的 `DllImport` 先经它自己的 ALC 解析,异常即到此为止,默认探测不可达(有测试用操作系统本来就能解析的绝对路径库固定住“不回退”);插件目录里同名文件在不在、内容是什么、扩展名是什么都不影响结论(有测试固定住 present/replaced/deleted 三种情况抛出同一异常)。集合里的镜像必须是托管程序集,判定用 `ManagedImage`(`PEReader`:`HasMetadata` 且有 assembly manifest),非托管 `.dll` 在构造上下文时即被拒绝并指名;空名、重名或 `null` 字节同属调用方错误。字节与上下文同生命周期:卸载时集合引用被清空,上下文被回收后不残留任何镜像数组。默认的按路径解析仍然保留给既有调用方(该模式仍把不可解析的名字返回零交给默认探测)。

**本地二进制识别。** `ManagedImage.ReadNativeBinaryFormat` 只看文件头而与扩展名无关,返回 ELF(`\x7fELF`)、Mach-O(32/64 位与字节序反转的四种 magic)、Mach-O universal/fat(fat 与 fat64)或无 CLI 头的 PE,数据文件返回 `null`。Worker 在装载前用它拒收本地二进制,扩展名不构成放行依据。

## 未决

- `CompileAsync` 单飞时,首个调用者的取消令牌连累并发等待者。
- 失败的编译保留在缓存;依赖变化时的失效由 ExtensionHost 负责(`Evict`/`Clear`)。

using System.Runtime.CompilerServices;
using Forge.Abstractions;

namespace Forge.App.Localization;

/// <summary>
/// The provider feature's string table: the <c>provider</c> settings section, the four status sentences a row can show, the
/// add/edit dialog, the model metadata window and the sign-in flows. Keys are namespaced with <c>providers.</c> (the
/// shell's older single-provider status uses <c>provider.</c>) and every key is registered from <see cref="Initialize"/>.
/// </summary>
internal static class ProviderStrings
{
    // ---------------------------------------------------------------- section
    public const string SectionTitleKey = "providers.section.title";

    public const string BannerNeedProviderKey = "providers.banner.needProvider";

    public const string SectionBuiltInKey = "providers.section.builtIn";

    public const string SectionBuiltInHintKey = "providers.section.builtInHint";

    public const string LoginButtonKey = "providers.row.login";

    public const string ApiKeyButtonKey = "providers.row.apiKey";

    public const string RowDeleteArmedKey = "providers.row.deleteArmed";

    public const string SectionConnectedKey = "providers.section.connected";

    public const string SectionAddServerKey = "providers.section.addServer";

    public const string SectionCustomTitleKey = "providers.section.custom";

    public const string SectionCustomHintKey = "providers.section.customHint";

    public const string SectionRefreshKey = "providers.section.refresh";

    public const string MetadataCapabilitiesKey = "providers.metadata.capabilities";

    public const string CapabilityTextKey = "providers.metadata.capability.text";

    public const string CapabilityImageKey = "providers.metadata.capability.image";

    public const string CapabilityPdfKey = "providers.metadata.capability.pdf";

    public const string CapabilityAudioKey = "providers.metadata.capability.audio";

    public const string CapabilityVideoKey = "providers.metadata.capability.video";

    public const string CapabilityReasoningKey = "providers.metadata.capability.reasoning";

    public const string CapabilityReasoningUnknownStepsKey = "providers.metadata.capability.reasoningUnknownSteps";

    public const string DefaultContextWindowKey = "providers.defaults.contextWindow";

    public const string DefaultContextWindowHintKey = "providers.defaults.contextWindowHint";

    public const string DefaultContextWindowInvalidKey = "providers.defaults.contextWindowInvalid";

    // ---------------------------------------------------------------- built-in agent defaults
    public const string AgentsTitleKey = "providers.agents.title";

    public const string AgentsHintKey = "providers.agents.hint";

    public const string AgentsNotSetKey = "providers.agents.notSet";

    public const string AgentsMainKey = "providers.agents.main";

    public const string AgentsScoutKey = "providers.agents.scout";

    public const string AgentsWorkerKey = "providers.agents.worker";

    public const string AgentsPlanKey = "providers.agents.plan";

    public const string AgentsReviewerKey = "providers.agents.reviewer";

    public const string AgentsModelKey = "providers.agents.model";

    public const string AgentsThinkingKey = "providers.agents.thinking";

    public const string AgentsSaveFailedKey = "providers.agents.saveFailed";

    // ---------------------------------------------------------------- load / store
    public const string SettingsMissingKey = "providers.store.settingsMissing";

    public const string SettingsUnreadableKey = "providers.store.settingsUnreadable";

    public const string SectionMissingKey = "providers.store.sectionMissing";

    public const string InvalidKey = "providers.store.invalid";

    public const string ReadyKey = "providers.store.ready";

    // ---------------------------------------------------------------- row status
    public const string ReadyEnvironmentKey = "providers.status.environment";

    public const string StoredKeyReadyKey = "providers.status.storedKey";

    public const string NeedsKeyVariableKey = "providers.status.needsKeyVariable";

    public const string NeedsKeyNoVariableKey = "providers.status.needsKey";

    public const string ReadyClaudeKey = "providers.status.claude";

    public const string NeedsClaudeSignInKey = "providers.status.claudeSignIn";

    public const string ReadyCopilotKey = "providers.status.copilot";

    public const string NeedsCopilotSignInKey = "providers.status.copilotSignIn";

    public const string ReadyKimiKey = "providers.status.kimi";

    public const string NeedsKimiSignInKey = "providers.status.kimiSignIn";

    public const string ReadyXAiKey = "providers.status.xai";

    public const string NeedsXAiSignInKey = "providers.status.xaiSignIn";

    public const string NeedsMetaSignInKey = "providers.status.metaSignIn";

    public const string ReadyMetaKey = "providers.status.meta";

    public const string NeedsChatGptSignInKey = "providers.status.chatGptSignIn";

    public const string ReadyChatGptKey = "providers.status.chatGpt";

    public const string ErrorNoBaseUrlKey = "providers.status.noBaseUrl";

    public const string ErrorNoCredentialKey = "providers.status.noCredential";

    public const string ManagedKey = "providers.status.managed";

    public const string NoModelForTestKey = "providers.status.noModelForTest";

    public const string SaveFailedKey = "providers.store.saveFailed";

    // ---------------------------------------------------------------- row
    public const string RowModelsKey = "providers.row.models";

    public const string RowModelsNoneKey = "providers.row.modelsNone";

    public const string RowEditKey = "providers.row.edit";

    public const string RowDefaultKey = "providers.row.default";

    public const string RowMakeDefaultKey = "providers.row.makeDefault";

    public const string RowDeleteKey = "providers.row.delete";

    public const string RowStatusReadyKey = "providers.row.statusReady";

    public const string RowStatusNeedsKeyKey = "providers.row.statusNeedsKey";

    public const string RowStatusNeedsSignInKey = "providers.row.statusNeedsSignIn";

    public const string RowStatusErrorKey = "providers.row.statusError";

    // ---------------------------------------------------------------- editor
    public const string EditorNewKey = "providers.editor.new";

    public const string EditorEditKey = "providers.editor.edit";

    public const string EditorNameKey = "providers.editor.name";

    public const string EditorBaseUrlKey = "providers.editor.baseUrl";

    public const string EditorSecretKey = "providers.editor.secret";

    public const string EditorKeyAsSecretKey = "providers.editor.keyAsSecret";

    public const string EditorKeyVariableKey = "providers.editor.keyVariable";

    public const string EditorKeyHintSecretKey = "providers.editor.keyHintSecret";

    public const string EditorKeyHintVariableKey = "providers.editor.keyHintVariable";

    public const string EditorTestKey = "providers.editor.test";

    public const string EditorTestingKey = "providers.editor.testing";

    public const string EditorTestPassedKey = "providers.editor.testPassed";

    public const string EditorTestPassedNoListKey = "providers.editor.testPassedNoList";

    public const string EditorNeedsVariableKey = "providers.editor.needsVariable";

    public const string LoginTitleKey = "providers.login.title";

    public const string LoginClaudeNotInstalledKey = "providers.login.claudeNotInstalled";

    public const string LoginClaudeStepsKey = "providers.login.claudeSteps";

    public const string LoginClaudeInstallKey = "providers.login.claudeInstall";

    public const string LoginClaudeRecheckKey = "providers.login.claudeRecheck";

    public const string LoginClaudeUseKey = "providers.login.claudeUse";

    public const string LoginCopilotStartKey = "providers.login.copilotStart";

    public const string LoginKimiStartKey = "providers.login.kimiStart";

    public const string LoginKimiHintKey = "providers.login.kimiHint";

    public const string LoginKimiCodeKey = "providers.login.kimiCode";

    public const string LoginXAiStartKey = "providers.login.xaiStart";

    public const string LoginXAiHintKey = "providers.login.xaiHint";

    public const string LoginXAiCodeKey = "providers.login.xaiCode";

    public const string LoginOpenRouterStartKey = "providers.login.openRouterStart";

    public const string LoginOpenRouterHintKey = "providers.login.openRouterHint";

    public const string LoginOpenRouterWaitingKey = "providers.login.openRouterWaiting";

    public const string LoginOpenRouterTimeoutKey = "providers.login.openRouterTimeout";

    public const string LoginOpenRouterNoCodeKey = "providers.login.openRouterNoCode";

    public const string LoginMetaStartKey = "providers.login.metaStart";

    public const string LoginMetaHintKey = "providers.login.metaHint";

    public const string LoginMetaCodeKey = "providers.login.metaCode";

    public const string LoginMetaEnablingKey = "providers.login.metaEnabling";

    public const string LoginChatGptStartKey = "providers.login.chatGptStart";

    public const string LoginChatGptHintKey = "providers.login.chatGptHint";

    public const string LoginChatGptCodeKey = "providers.login.chatGptCode";

    public const string EditorSecretStoredKey = "providers.editor.secretStored";

    public const string EditorModelsFetchedKey = "providers.editor.modelsFetched";

    public const string EditorSaveKey = "providers.editor.save";

    public const string EditorCancelKey = "providers.editor.cancel";

    public const string EditorSignedInKey = "providers.editor.signedIn";

    public const string EditorCopilotHintKey = "providers.editor.copilotHint";

    public const string EditorDeviceCodeKey = "providers.editor.deviceCode";

    public const string EditorDeviceCodeHintKey = "providers.editor.deviceCodeHint";

    public const string EditorDeviceCodeWaitingKey = "providers.editor.deviceCodeWaiting";

    public const string EditorNeedsNameKey = "providers.editor.needsName";

    public const string EditorNeedsBaseUrlKey = "providers.editor.needsBaseUrl";

    // ---------------------------------------------------------------- model metadata window
    public const string MetadataTitleKey = "providers.meta.title";

    public const string MetadataIntroKey = "providers.meta.intro";

    public const string MetadataModelKey = "providers.meta.model";

    public const string MetadataContextKey = "providers.meta.context";

    public const string MetadataInputKey = "providers.meta.input";

    public const string MetadataOutputKey = "providers.meta.output";

    public const string MetadataCacheReadKey = "providers.meta.cacheRead";

    public const string MetadataCacheWriteKey = "providers.meta.cacheWrite";

    public const string MetadataPerMillionKey = "providers.meta.perMillion";

    public const string MetadataSaveKey = "providers.meta.save";

    public const string MetadataSavedKey = "providers.meta.saved";

    public const string MetadataRefreshKey = "providers.meta.refresh";

    public const string MetadataResetKey = "providers.meta.reset";

    public const string MetadataCloseKey = "providers.meta.close";

    public const string MetadataEmptyKey = "providers.meta.empty";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    internal static void Register()
    {
        Loc.Register(SectionTitleKey, "提供商", "Providers");
        Loc.Register(BannerNeedProviderKey, "至少连接一个提供商才能运行 agent", "Connect at least one provider to run an agent");
        Loc.Register(SectionConnectedKey, "已连接", "Connected");
        Loc.Register(SectionAddServerKey, "添加提供商", "Add provider");
        Loc.Register(SectionCustomTitleKey, "自定义提供商", "Custom providers");
        Loc.Register(
            SectionCustomHintKey,
            "任何兼容 OpenAI 协议的提供商或者服务器",
            "Any provider or server that speaks the OpenAI protocol");
        Loc.Register(SectionBuiltInKey, "内置提供商", "Built-in providers");
        Loc.Register(
            SectionBuiltInHintKey,
            "可使用您现有的 Claude、ChatGPT 或 Copilot 套餐，无需 API 密钥。",
            "Use your existing Claude, ChatGPT or Copilot plan — no API key needed.");
        Loc.Register(LoginButtonKey, "登录", "Sign in");
        Loc.Register(ApiKeyButtonKey, "API 密钥", "API key");
        Loc.Register(RowDeleteArmedKey, "再次点击删除", "Click again to delete");
        Loc.Register(SectionRefreshKey, "刷新", "Refresh");
        Loc.Register(MetadataCapabilitiesKey, "能力", "Capabilities");
        Loc.Register(CapabilityTextKey, "文本", "text");
        Loc.Register(CapabilityImageKey, "图像", "image");
        Loc.Register(CapabilityPdfKey, "PDF", "PDF");
        Loc.Register(CapabilityAudioKey, "音频", "audio");
        Loc.Register(CapabilityVideoKey, "视频", "video");
        Loc.Register(CapabilityReasoningKey, "推理 {0} 档", "reasoning × {0}");
        Loc.Register(CapabilityReasoningUnknownStepsKey, "推理", "reasoning");
        Loc.Register(DefaultContextWindowKey, "默认上下文窗口", "Default context window");
        Loc.Register(
            DefaultContextWindowHintKey,
            "数字 = 固定 tokens；百分比（如 50%） = 按各模型自身窗口的比例，模型窗口未知时不生效；留空 = 用各模型的目录值。它只作用于没有单独设置的模型。",
            "A number is a fixed token count; a percentage such as 50% takes that share of each model's own window and does nothing while that window is unknown; empty uses each model's catalog value. It only applies to models without their own setting.");
        Loc.Register(
            DefaultContextWindowInvalidKey,
            "默认上下文窗口必须是正整数、1% 到 1000% 的百分比，或留空表示使用模型目录值。",
            "The default context window must be a positive whole number, a percentage from 1% to 1000%, or empty to use the catalog value.");

        Loc.Register(AgentsTitleKey, "内置 Agent 默认", "Built-in agent defaults");
        Loc.Register(
            AgentsHintKey,
            "未设置时继承 Worker 的默认；Worker 也没有时，使用第一个已连接提供商的第一个模型，思考力度为「关闭」。",
            "An unset row inherits the Worker's defaults; when the Worker is unset too, the first model of the first connected provider runs at Off.");
        Loc.Register(AgentsNotSetKey, "未设置（继承）", "Not set (inherit)");
        Loc.Register(AgentsMainKey, "主 Agent", "Main");
        Loc.Register(AgentsScoutKey, "Scout", "Scout");
        Loc.Register(AgentsWorkerKey, "Worker", "Worker");
        Loc.Register(AgentsPlanKey, "Plan", "Plan");
        Loc.Register(AgentsReviewerKey, "Reviewer", "Reviewer");
        Loc.Register(AgentsModelKey, "模型", "Model");
        Loc.Register(AgentsThinkingKey, "思考力度", "Thinking");
        Loc.Register(AgentsSaveFailedKey, "无法写入 settings.json：{0}", "Could not write settings.json: {0}");

        Loc.Register(SettingsMissingKey, "找不到 settings.json", "settings.json was not found");
        Loc.Register(SettingsUnreadableKey, "settings.json 无法读取：{0}", "settings.json cannot be read: {0}");
        Loc.Register(SectionMissingKey, "还没有配置任何提供商", "No provider is configured yet");
        Loc.Register(InvalidKey, "提供商配置无效：{0}", "The provider configuration is invalid: {0}");
        Loc.Register(ReadyKey, "已就绪：{0}", "Ready: {0}");

        Loc.Register(ReadyEnvironmentKey, "环境变量 key · {0} · {1}", "Environment key · {0} · {1}");
        Loc.Register(StoredKeyReadyKey, "已保存的密钥 · {0}", "Stored key · {0}");
        Loc.Register(NeedsKeyVariableKey, "缺少密钥 · {0}", "No key · {0}");
        Loc.Register(NeedsKeyNoVariableKey, "缺少密钥", "No key");
        Loc.Register(ReadyClaudeKey, "已登录 Claude Code", "Signed in to Claude Code");
        Loc.Register(
            NeedsClaudeSignInKey,
            "未登录：请先在本机运行 claude 登录",
            "Not signed in: sign in with the local claude CLI first");
        Loc.Register(ReadyCopilotKey, "已登录 GitHub", "Signed in to GitHub");
        Loc.Register(NeedsCopilotSignInKey, "未登录 GitHub", "Not signed in to GitHub");
        Loc.Register(ReadyKimiKey, "已登录 Kimi For Coding", "Signed in to Kimi For Coding");
        Loc.Register(NeedsKimiSignInKey, "未登录 Kimi For Coding", "Not signed in to Kimi For Coding");
        Loc.Register(ReadyXAiKey, "已登录 xAI", "Signed in to xAI");
        Loc.Register(NeedsXAiSignInKey, "未登录 xAI", "Not signed in to xAI");
        Loc.Register(NeedsMetaSignInKey, "未登录 Meta 订阅", "Not signed in to the Meta subscription");
        Loc.Register(ReadyMetaKey, "已登录 Meta 订阅", "Signed in to the Meta subscription");
        Loc.Register(NeedsChatGptSignInKey, "未登录 ChatGPT 订阅", "Not signed in to the ChatGPT subscription");
        Loc.Register(ReadyChatGptKey, "已登录 ChatGPT 订阅", "Signed in to the ChatGPT subscription");
        Loc.Register(ErrorNoBaseUrlKey, "缺少 Base URL", "No base URL");
        Loc.Register(ErrorNoCredentialKey, "没有选择认证方式", "No authentication method");
        Loc.Register(ManagedKey, "托管中", "ManagedKey");
        Loc.Register(NoModelForTestKey, "没有可用于测试的模型", "No model to test with");
        Loc.Register(SaveFailedKey, "无法保存提供商配置，未做任何改动", "Could not save the provider settings; nothing was changed");

        Loc.Register(RowModelsKey, "{0} 个模型", "{0} models");
        Loc.Register(RowModelsNoneKey, "0 个模型", "0 models");
        Loc.Register(RowEditKey, "编辑提供商", "Edit provider");
        Loc.Register(RowDefaultKey, "默认", "Default");
        Loc.Register(RowMakeDefaultKey, "设为默认", "Set as default");
        Loc.Register(RowDeleteKey, "删除提供商", "Delete provider");
        Loc.Register(RowStatusReadyKey, "已连接", "Connected");
        Loc.Register(RowStatusNeedsKeyKey, "缺少密钥", "Missing key");
        Loc.Register(RowStatusNeedsSignInKey, "待登录", "Sign-in required");
        Loc.Register(RowStatusErrorKey, "配置有误", "Configuration error");

        Loc.Register(EditorNewKey, "添加提供商", "Add provider");
        Loc.Register(EditorEditKey, "编辑提供商", "Edit provider");
        Loc.Register(EditorNameKey, "名称", "Name");
        Loc.Register(EditorBaseUrlKey, "Base URL", "Base URL");
        Loc.Register(EditorSecretKey, "密钥", "Key");
        Loc.Register(EditorSecretStoredKey, "已保存，留空则不修改", "Stored — leave empty to keep it");
        Loc.Register(EditorKeyAsSecretKey, "以密钥粘贴（不勾选则填写环境变量名）", "Paste the key itself (untick to enter an environment variable name)");
        Loc.Register(EditorKeyVariableKey, "环境变量名", "Environment variable name");
        Loc.Register(
            EditorKeyHintSecretKey,
            "这里是密钥本身，加密保存在本机，不写入 settings.json。",
            "The key itself, stored encrypted on this machine and never written to settings.json.");
        Loc.Register(
            EditorKeyHintVariableKey,
            "这里是环境变量名，运行时从该变量读取密钥。",
            "The name of an environment variable; the key is read from it at run time.");
        Loc.Register(EditorTestKey, "测试连接", "Test connection");
        Loc.Register(EditorTestingKey, "测试中…", "Testing…");
        Loc.Register(EditorTestPassedKey, "连接成功，已获取 {0} 个模型", "Connected — {0} models fetched");
        Loc.Register(EditorTestPassedNoListKey, "连接成功", "Connected");
        Loc.Register(EditorNeedsVariableKey, "请填写环境变量名", "Enter the environment variable name");
        Loc.Register(LoginTitleKey, "登录 {0}", "Sign in to {0}");
        Loc.Register(
            LoginClaudeNotInstalledKey,
            "没有检测到 Claude Code（本机 claude 命令）",
            "Claude Code (the local claude command) was not found");
        Loc.Register(LoginClaudeStepsKey, "1. 运行安装命令（PowerShell）：irm https://claude.ai/install.ps1 | iex\n2. 打开新的终端，运行 claude 并完成登录\n3. 回到这里点击「重新检查」",
            "1. Run the installer in PowerShell: irm https://claude.ai/install.ps1 | iex\n2. Open a new terminal, run claude and sign in\n3. Come back here and click Re-check");
        Loc.Register(LoginClaudeInstallKey, "安装 Claude Code", "Install Claude Code");
        Loc.Register(LoginClaudeRecheckKey, "重新检查", "Re-check");
        Loc.Register(LoginClaudeUseKey, "使用此登录", "Use this sign-in");
        Loc.Register(LoginCopilotStartKey, "开始设备码登录", "Start device-code sign-in");
        Loc.Register(LoginKimiStartKey, "开始登录", "Start sign-in");
        Loc.Register(
            LoginKimiHintKey,
            "用 Kimi For Coding 设备码登录，登录后直接使用该套餐；本机不保存密码。",
            "Signs in to Kimi For Coding with a device code and uses that plan directly; no password is stored on this machine.");
        Loc.Register(LoginKimiCodeKey, "在 Kimi 输入代码 {0}", "Enter the code {0} at Kimi");
        Loc.Register(LoginXAiStartKey, "开始登录", "Start sign-in");
        Loc.Register(
            LoginXAiHintKey,
            "用 xAI 账号的设备码登录，登录后直接使用该订阅；本机不保存密码。",
            "Signs in with an xAI account's device code and uses that subscription directly; no password is stored on this machine.");
        Loc.Register(LoginXAiCodeKey, "在 xAI 输入代码 {0}", "Enter the code {0} at xAI");
        Loc.Register(LoginOpenRouterStartKey, "在浏览器中登录", "Sign in in the browser");
        Loc.Register(
            LoginOpenRouterHintKey,
            "在浏览器里用 OpenRouter 账号完成授权；完成后本机只保存一个 API 密钥。",
            "Authorizes OpenRouter in the browser; only an API key is stored on this machine afterwards.");
        Loc.Register(
            LoginOpenRouterWaitingKey,
            "已在浏览器打开 OpenRouter，等待你完成授权…",
            "OpenRouter is open in the browser; waiting for you to finish signing in…");
        Loc.Register(
            LoginOpenRouterTimeoutKey,
            "OpenRouter 登录在 5 分钟后放弃，请重新登录。",
            "The OpenRouter sign-in gave up after five minutes. Please start it again.");
        Loc.Register(
            LoginOpenRouterNoCodeKey,
            "OpenRouter 没有返回授权码，请重新登录。",
            "OpenRouter returned no authorization code. Please start it again.");
        Loc.Register(LoginMetaStartKey, "开始登录", "Start sign-in");
        Loc.Register(
            LoginMetaHintKey,
            "用 Meta 账号的设备码登录，登录后为这台机器开通一个 API 密钥；本机不保存密码。",
            "Signs in with a Meta account's device code and enables an API key for this machine; no password is stored on this machine.");
        Loc.Register(LoginMetaCodeKey, "在 Meta 输入代码 {0}", "Enter the code {0} at Meta");
        Loc.Register(LoginMetaEnablingKey, "正在开通访问…", "Enabling access…");
        Loc.Register(LoginChatGptStartKey, "开始登录", "Start sign-in");
        Loc.Register(
            LoginChatGptHintKey,
            "用 ChatGPT 订阅的设备码登录，登录后直接使用该订阅；本机不保存密码。",
            "Signs in with a ChatGPT plan's device code and uses that plan directly; no password is stored on this machine.");
        Loc.Register(LoginChatGptCodeKey, "在 ChatGPT 输入代码 {0}", "Enter the code {0} at ChatGPT");
        Loc.Register(EditorModelsFetchedKey, "已获取 {0} 个模型", "Fetched {0} models");
        Loc.Register(EditorSaveKey, "保存", "Save");
        Loc.Register(EditorCancelKey, "取消", "Cancel");
        Loc.Register(EditorSignedInKey, "已登录", "Signed in");
        Loc.Register(
            EditorCopilotHintKey,
            "用 GitHub 设备码登录 Copilot；这是非官方客户端用法，可能随 GitHub 变化。",
            "Signs in to Copilot with GitHub's device code; an unofficial client, so GitHub may change it.");
        Loc.Register(EditorDeviceCodeKey, "在 GitHub 输入代码 {0}", "Enter the code {0} on GitHub");
        Loc.Register(
            EditorDeviceCodeHintKey,
            "浏览器会打开一次；授权后这里会自动变成已登录。",
            "The browser opens once; this switches to signed in after you authorize.");
        Loc.Register(EditorDeviceCodeWaitingKey, "等待授权…", "Waiting for authorization…");
        Loc.Register(EditorNeedsNameKey, "名称不能为空", "The name cannot be empty");
        Loc.Register(EditorNeedsBaseUrlKey, "Base URL 必须是绝对地址", "The base URL must be absolute");

        Loc.Register(MetadataTitleKey, "模型 · {0}", "Models · {0}");
        Loc.Register(
            MetadataIntroKey,
            "上下文窗口按 token 计，单价按每百万 token 的美元计；留空表示未知，不按 0 计算。",
            "Context window is in tokens and prices are US dollars per million tokens; empty means unknown, not zero.");
        Loc.Register(MetadataModelKey, "模型", "Model");
        Loc.Register(MetadataContextKey, "上下文窗口", "Context window");
        Loc.Register(MetadataInputKey, "输入", "Input");
        Loc.Register(MetadataOutputKey, "输出", "Output");
        Loc.Register(MetadataCacheReadKey, "缓存读", "Cache read");
        Loc.Register(MetadataCacheWriteKey, "缓存写", "Cache write");
        Loc.Register(MetadataPerMillionKey, "美元 / 百万 token", "USD / million tokens");
        Loc.Register(MetadataSaveKey, "保存", "Save");
        Loc.Register(MetadataSavedKey, "已保存", "Saved");
        Loc.Register(MetadataRefreshKey, "从服务端刷新模型", "Refresh models from the server");
        Loc.Register(MetadataResetKey, "恢复默认", "Restore defaults");
        Loc.Register(MetadataCloseKey, "关闭", "Close");
        Loc.Register(MetadataEmptyKey, "这个提供商还没有模型；先在编辑界面刷新一次。",
            "This provider has no models yet; refresh once in the editor.");
    }

    // ---------------------------------------------------------------- accessors used outside this file
    public static Localization.LocText StoreReady(string summary) => new(ReadyKey, summary);

    public static Localization.LocText SettingsMissingText() => new(SettingsMissingKey);

    public static Localization.LocText SettingsUnreadableText(string message) => new(SettingsUnreadableKey, message);

    public static Localization.LocText SectionMissingText() => new(SectionMissingKey);

    public static Localization.LocText InvalidText(string message) => new(InvalidKey, message);

    public static Localization.LocText ReadyEnvironmentText(string variable) => new(ReadyEnvironmentKey, variable, ManagedText);

    public static Localization.LocText StoredKeyReadyText() => new(StoredKeyReadyKey, ManagedText);

    public static Localization.LocText NeedsKeyVariableText(string variable) => new(NeedsKeyVariableKey, variable);

    public static Localization.LocText NeedsKeyNoVariableText() => new(NeedsKeyNoVariableKey);


    public static Localization.LocText NoBaseUrlText() => new(ErrorNoBaseUrlKey);

    public static Localization.LocText NoCredentialText() => new(ErrorNoCredentialKey);

    public static Localization.LocText NoModelForTestText() => new(NoModelForTestKey);

    public static Localization.LocText SaveFailedText() => new(SaveFailedKey);

    public static Localization.LocText ClaudeSignedInText() => new(ReadyClaudeKey);

    public static Localization.LocText ClaudeSignInRequiredText() => new(NeedsClaudeSignInKey);

    public static Localization.LocText CopilotSignedInText() => new(ReadyCopilotKey);

    public static Localization.LocText CopilotSignInRequiredText() => new(NeedsCopilotSignInKey);

    public static Localization.LocText KimiSignedInText() => new(ReadyKimiKey);

    public static Localization.LocText KimiSignInRequiredText() => new(NeedsKimiSignInKey);

    public static Localization.LocText XAiSignedInText() => new(ReadyXAiKey);

    public static Localization.LocText XAiSignInRequiredText() => new(NeedsXAiSignInKey);

    public static Localization.LocText OpenRouterTimedOutText() => new(LoginOpenRouterTimeoutKey);

    public static Localization.LocText OpenRouterNoCodeText() => new(LoginOpenRouterNoCodeKey);

    public static Localization.LocText MetaSignInRequiredText() => new(NeedsMetaSignInKey);

    public static Localization.LocText MetaSignedInText() => new(ReadyMetaKey);

    public static Localization.LocText ChatGptSignInRequiredText() => new(NeedsChatGptSignInKey);

    public static Localization.LocText ChatGptSignedInText() => new(ReadyChatGptKey);

    public static string ManagedText => Localization.Loc.Instance[ManagedKey];

    public static string MetadataCapabilities => Localization.Loc.Instance[MetadataCapabilitiesKey];

    /// <summary>
    /// The read-only capability summary of one model ("文本·图像 · 推理 4 档"). Empty when no source described the model,
    /// which is the normal state for a model nobody has priced yet; the dialog then shows nothing instead of "unknown".
    /// </summary>
    public static string CapabilitiesText(ModelCapabilities? capabilities)
    {
        if (capabilities is not { IsKnown: true })
        {
            return string.Empty;
        }

        var parts = new List<string>(2);
        if (capabilities.Inputs is { } inputs)
        {
            var names = new List<string>(4);
            foreach (var (flag, key) in new[]
                     {
                         (ModelInputs.Text, CapabilityTextKey),
                         (ModelInputs.Image, CapabilityImageKey),
                         (ModelInputs.Pdf, CapabilityPdfKey),
                         (ModelInputs.Audio, CapabilityAudioKey),
                         (ModelInputs.Video, CapabilityVideoKey),
                     })
            {
                if ((inputs & flag) != 0)
                {
                    names.Add(Localization.Loc.Instance[key]);
                }
            }

            if (names.Count > 0)
            {
                parts.Add(string.Join("·", names));
            }
        }

        if (capabilities.ReasoningSteps is { Count: > 0 } steps)
        {
            parts.Add(Localization.Loc.Instance.Format(
                CapabilityReasoningKey,
                (steps.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        else if (capabilities.Reasoning == true)
        {
            parts.Add(Localization.Loc.Instance[CapabilityReasoningUnknownStepsKey]);
        }

        return string.Join(" · ", parts);
    }
}

using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The provider feature's string table: the <c>provider</c> settings section, the four status sentences a row can show, the
/// add/edit dialog, the model metadata window and the two sign-in flows. Keys are namespaced with <c>providers.</c> (the
/// shell's older single-provider status uses <c>provider.</c>) and every key is registered from <see cref="Initialize"/>.
/// </summary>
internal static class ProviderStrings
{
    // ---------------------------------------------------------------- section
    public const string SectionTitleKey = "providers.section.title";

    public const string SectionIntroKey = "providers.section.intro";

    public const string SectionConnectedKey = "providers.section.connected";

    public const string SectionAddServerKey = "providers.section.addServer";

    public const string SectionCustomTitleKey = "providers.section.custom";

    public const string SectionCustomHintKey = "providers.section.customHint";

    public const string SectionRefreshKey = "providers.section.refresh";

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

    public const string ErrorNoBaseUrlKey = "providers.status.noBaseUrl";

    public const string ErrorNoCredentialKey = "providers.status.noCredential";

    public const string ManagedKey = "providers.status.managed";

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

    public const string EditorPresetKey = "providers.editor.preset";

    public const string EditorNameKey = "providers.editor.name";

    public const string EditorBaseUrlKey = "providers.editor.baseUrl";

    public const string EditorProtocolKey = "providers.editor.protocol";

    public const string EditorAuthKey = "providers.editor.auth";

    public const string EditorAuthApiKeyKey = "providers.editor.authApiKey";

    public const string EditorAuthClaudeKey = "providers.editor.authClaude";

    public const string EditorAuthCopilotKey = "providers.editor.authCopilot";

    public const string EditorEnvironmentVariableKey = "providers.editor.envVar";

    public const string EditorSecretKey = "providers.editor.secret";

    public const string EditorSecretPlaceholderKey = "providers.editor.secretPlaceholder";

    public const string EditorSecretStoredKey = "providers.editor.secretStored";

    public const string EditorSecretClearKey = "providers.editor.secretClear";

    public const string EditorModelsKey = "providers.editor.models";

    public const string EditorModelsFetchKey = "providers.editor.modelsFetch";

    public const string EditorModelsFetchedKey = "providers.editor.modelsFetched";

    public const string EditorModelAddKey = "providers.editor.modelAdd";

    public const string EditorDefaultModelKey = "providers.editor.defaultModel";

    public const string EditorThinkingKey = "providers.editor.thinking";

    public const string EditorMaxTokensKey = "providers.editor.maxTokens";

    public const string EditorSaveKey = "providers.editor.save";

    public const string EditorCancelKey = "providers.editor.cancel";

    public const string EditorDeleteKey = "providers.editor.delete";

    public const string EditorDeleteConfirmKey = "providers.editor.deleteConfirm";

    public const string EditorSignInKey = "providers.editor.signIn";

    public const string EditorSignOutKey = "providers.editor.signOut";

    public const string EditorSignedInKey = "providers.editor.signedIn";

    public const string EditorClaudeHintKey = "providers.editor.claudeHint";

    public const string EditorCopilotHintKey = "providers.editor.copilotHint";

    public const string EditorDeviceCodeKey = "providers.editor.deviceCode";

    public const string EditorDeviceCodeHintKey = "providers.editor.deviceCodeHint";

    public const string EditorDeviceCodeWaitingKey = "providers.editor.deviceCodeWaiting";

    public const string EditorOpenBrowserKey = "providers.editor.openBrowser";

    public const string EditorCopyCodeKey = "providers.editor.copyCode";

    public const string EditorNeedsNameKey = "providers.editor.needsName";

    public const string EditorNeedsBaseUrlKey = "providers.editor.needsBaseUrl";

    public const string EditorNeedsModelKey = "providers.editor.needsModel";

    public const string EditorSignedInClaudeKey = "providers.editor.signedInClaude";

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

    public const string MetadataUnknownKey = "providers.meta.unknown";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    internal static void Register()
    {
        Loc.Register(SectionTitleKey, "提供方", "Providers");
        Loc.Register(
            SectionIntroKey,
            "至少连接一个提供方才能运行 agent，可以是订阅或 API 密钥。",
            "Connect at least one provider to run an agent — a subscription or an API key.");
        Loc.Register(SectionConnectedKey, "已连接", "Connected");
        Loc.Register(SectionAddServerKey, "添加服务器", "Add server");
        Loc.Register(SectionCustomTitleKey, "自定义服务器", "Custom server");
        Loc.Register(
            SectionCustomHintKey,
            "公司大模型网关或本机上的模型——任何兼容 OpenAI 的服务器。",
            "A company gateway or a model on this machine — any OpenAI-compatible server.");
        Loc.Register(SectionRefreshKey, "刷新", "Refresh");

        Loc.Register(SettingsMissingKey, "找不到 settings.json", "settings.json was not found");
        Loc.Register(SettingsUnreadableKey, "settings.json 无法读取：{0}", "settings.json cannot be read: {0}");
        Loc.Register(SectionMissingKey, "还没有配置任何提供方", "No provider is configured yet");
        Loc.Register(InvalidKey, "提供方配置无效：{0}", "The provider configuration is invalid: {0}");
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
        Loc.Register(ErrorNoBaseUrlKey, "缺少 Base URL", "No base URL");
        Loc.Register(ErrorNoCredentialKey, "没有选择认证方式", "No authentication method");
        Loc.Register(ManagedKey, "托管中", "ManagedKey");

        Loc.Register(RowModelsKey, "{0} 个模型", "{0} models");
        Loc.Register(RowModelsNoneKey, "0 个模型", "0 models");
        Loc.Register(RowEditKey, "编辑提供方", "Edit provider");
        Loc.Register(RowDefaultKey, "默认", "Default");
        Loc.Register(RowMakeDefaultKey, "设为默认", "Set as default");
        Loc.Register(RowDeleteKey, "删除提供方", "Delete provider");
        Loc.Register(RowStatusReadyKey, "已连接", "Connected");
        Loc.Register(RowStatusNeedsKeyKey, "缺少密钥", "Missing key");
        Loc.Register(RowStatusNeedsSignInKey, "待登录", "Sign-in required");
        Loc.Register(RowStatusErrorKey, "配置有误", "Configuration error");

        Loc.Register(EditorNewKey, "添加提供方", "Add provider");
        Loc.Register(EditorEditKey, "编辑提供方", "Edit provider");
        Loc.Register(EditorPresetKey, "预设", "Preset");
        Loc.Register(EditorNameKey, "名称", "Name");
        Loc.Register(EditorBaseUrlKey, "Base URL", "Base URL");
        Loc.Register(EditorProtocolKey, "协议", "Protocol");
        Loc.Register(EditorAuthKey, "认证方式", "Authentication");
        Loc.Register(EditorAuthApiKeyKey, "API 密钥", "API key");
        Loc.Register(EditorAuthClaudeKey, "Claude 订阅登录", "Claude subscription");
        Loc.Register(EditorAuthCopilotKey, "GitHub Copilot 登录", "GitHub Copilot sign-in");
        Loc.Register(EditorEnvironmentVariableKey, "环境变量", "Environment variable");
        Loc.Register(EditorSecretKey, "密钥", "Key");
        Loc.Register(EditorSecretPlaceholderKey, "粘贴密钥（保存在加密密钥库，不写入 settings.json）",
            "Paste the key (kept in the encrypted store, never in settings.json)");
        Loc.Register(EditorSecretStoredKey, "已保存，留空则不修改", "Stored — leave empty to keep it");
        Loc.Register(EditorSecretClearKey, "清除密钥", "Clear key");
        Loc.Register(EditorModelsKey, "模型", "Models");
        Loc.Register(EditorModelsFetchKey, "从服务端刷新", "Refresh from server");
        Loc.Register(EditorModelsFetchedKey, "已获取 {0} 个模型", "Fetched {0} models");
        Loc.Register(EditorModelAddKey, "添加模型 id", "Add a model id");
        Loc.Register(EditorDefaultModelKey, "默认模型", "Default model");
        Loc.Register(EditorThinkingKey, "思考强度", "Thinking effort");
        Loc.Register(EditorMaxTokensKey, "最大输出 token", "Max output tokens");
        Loc.Register(EditorSaveKey, "保存", "Save");
        Loc.Register(EditorCancelKey, "取消", "Cancel");
        Loc.Register(EditorDeleteKey, "删除", "Delete");
        Loc.Register(EditorDeleteConfirmKey, "再次点击以删除这个提供方", "Click again to delete this provider");
        Loc.Register(EditorSignInKey, "登录", "Sign in");
        Loc.Register(EditorSignOutKey, "退出登录", "Sign out");
        Loc.Register(EditorSignedInKey, "已登录", "Signed in");
        Loc.Register(
            EditorClaudeHintKey,
            "复用本机 claude 已登录的订阅额度；需要先运行 claude 完成登录。",
            "Uses the subscription the local claude CLI is signed in to; sign in with claude first.");
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
        Loc.Register(EditorOpenBrowserKey, "打开页面", "Open page");
        Loc.Register(EditorCopyCodeKey, "复制代码", "Copy code");
        Loc.Register(EditorNeedsNameKey, "名称不能为空", "The name cannot be empty");
        Loc.Register(EditorNeedsBaseUrlKey, "Base URL 必须是绝对地址", "The base URL must be absolute");
        Loc.Register(EditorNeedsModelKey, "至少需要一个模型", "At least one model is required");
        Loc.Register(EditorSignedInClaudeKey, "已登录 Claude Code", "Signed in to Claude Code");

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
        Loc.Register(MetadataEmptyKey, "这个提供方还没有模型；先在编辑界面刷新一次。",
            "This provider has no models yet; refresh once in the editor.");
        Loc.Register(MetadataUnknownKey, "未知", "Unknown");
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

    public static Localization.LocText ClaudeSignedInText() => new(ReadyClaudeKey);

    public static Localization.LocText ClaudeSignInRequiredText() => new(NeedsClaudeSignInKey);

    public static Localization.LocText CopilotSignedInText() => new(ReadyCopilotKey);

    public static Localization.LocText CopilotSignInRequiredText() => new(NeedsCopilotSignInKey);

    public static string ManagedText => Localization.Loc.Instance[ManagedKey];

    /// <summary>Short name of a protocol, as the editor shows it.</summary>
    public static string ProtocolText(string protocolId) => protocolId == "anthropic"
        ? "Anthropic Messages"
        : "OpenAI Chat Completions";
}

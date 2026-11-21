using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>The <c>web</c> settings section (web_fetch / web_search); keys are namespaced <c>web.*</c>.</summary>
internal static class WebStrings
{
    public const string SectionTitle = "web.section";
    public const string Hint = "web.hint";
    public const string BackendLabel = "web.backend";
    public const string BackendNone = "web.backend.none";
    public const string ApiKeyLabel = "web.apiKey";
    public const string ApiKeyPlaceholder = "web.apiKey.placeholder";
    public const string ApiKeyStored = "web.apiKey.stored";
    public const string ApiKeyOptional = "web.apiKey.optional";
    public const string ApiKeySharedNote = "web.apiKey.shared";
    public const string SearxngUrlLabel = "web.searxngUrl";
    public const string AllowPrivateLabel = "web.allowPrivate";
    public const string AllowPrivateHint = "web.allowPrivate.hint";
    public const string SaveButton = "web.save";
    public const string TestButton = "web.test";
    public const string StatusSaved = "web.status.saved";
    public const string StatusSaveFailed = "web.status.saveFailed";
    public const string StatusTesting = "web.status.testing";
    public const string StatusTestOk = "web.status.testOk";
    public const string StatusTestFailed = "web.status.testFailed";
    public const string StatusNotConfigured = "web.status.notConfigured";

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(SectionTitle, "Web 工具", "Web tools");
        Loc.Register(
            Hint,
            "web_fetch 始终可用;web_search 默认走 Exa(无需密钥)。下拉里带绿点的后端现在就能用。两个工具都会联网,每次调用默认需要你批准。API 密钥保存在加密密钥库,不写入 settings.json。",
            "web_fetch is always available; web_search defaults to Exa, which needs no key. An entry with a green dot can search right now. Both tools use the network and ask for approval by default. API keys go to the encrypted secret store, never to settings.json.");
        Loc.Register(BackendLabel, "搜索后端", "Search backend");
        Loc.Register(BackendNone, "未配置", "Not configured");
        Loc.Register(ApiKeyLabel, "API 密钥", "API key");
        Loc.Register(ApiKeyPlaceholder, "粘贴新密钥后点击保存", "Paste a new key, then Save");
        Loc.Register(ApiKeyStored, "已保存密钥;输入新值可替换", "A key is stored; enter a new value to replace it");
        Loc.Register(
            ApiKeyOptional,
            "可留空:留空时使用 Exa 公共 MCP(匿名、无需密钥,限额较低)。",
            "Optional: leave it empty to use Exa's public MCP (anonymous, no key, lower limits).");
        Loc.Register(
            ApiKeySharedNote,
            "DeepSeek 已经在你配置的 DeepSeek 模型提供商里填过密钥时,这里可以留空。",
            "Leave this empty when the DeepSeek model provider you configured already carries the key.");
        Loc.Register(SearxngUrlLabel, "SearXNG 地址", "SearXNG URL");
        Loc.Register(AllowPrivateLabel, "允许访问内网和本机地址", "Allow private-network and loopback addresses");
        Loc.Register(
            AllowPrivateHint,
            "关闭时 web_fetch 拒绝回环、链路本地和内网地址,防止被诱导访问内部服务。",
            "When off, web_fetch refuses loopback, link-local and private addresses so it cannot be steered at internal services.");
        Loc.Register(SaveButton, "保存", "Save");
        Loc.Register(TestButton, "测试搜索", "Test search");
        Loc.Register(StatusSaved, "已保存", "Saved");
        Loc.Register(StatusSaveFailed, "保存失败", "Save failed");
        Loc.Register(StatusTesting, "正在测试…", "Testing...");
        Loc.Register(StatusTestOk, "搜索成功,返回结果数", "Search works, results returned");
        Loc.Register(StatusTestFailed, "搜索失败", "Search failed");
        Loc.Register(StatusNotConfigured, "请先选择后端并填写密钥或地址", "Choose a backend and enter its key or URL first");
    }
}

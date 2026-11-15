using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The update checker's table (wave W10b): the <c>updates</c> settings section (manifest URL, frequency, channel, current
/// version and the check-now action) plus every readable status and failure the service reports. Keys: <c>updates.*</c>.
/// <para>Only wording lives here. The offered version and the byte count are <see cref="LocText"/> arguments (the byte count
/// pre-formatted by <see cref="TextFormat.Number"/>, never by the caller); a <see cref="LocText.Literal"/> carries an
/// exception message from the network or the file system, which is not translated.</para>
/// </summary>
internal static class UpdatesStrings
{
    // ---------------------------------------------------------------- the `updates` settings section
    public const string SettingsTitleKey = "updates.settings.title";
    public const string SettingsHintKey = "updates.settings.hint";
    public const string SettingsManifestUrlKey = "updates.settings.manifestUrl";
    public const string SettingsManifestUrlHintKey = "updates.settings.manifestUrlHint";
    public const string SettingsFrequencyKey = "updates.settings.frequency";
    public const string SettingsFrequencyDailyKey = "updates.settings.frequencyDaily";
    public const string SettingsFrequencyWeeklyKey = "updates.settings.frequencyWeekly";
    public const string SettingsFrequencyNeverKey = "updates.settings.frequencyNever";
    public const string SettingsChannelKey = "updates.settings.channel";
    public const string SettingsChannelStableKey = "updates.settings.channelStable";
    public const string SettingsChannelBetaKey = "updates.settings.channelBeta";
    public const string SettingsCurrentVersionKey = "updates.settings.currentVersion";
    public const string SettingsCheckNowKey = "updates.settings.checkNow";
    public const string SettingsCancelKey = "updates.settings.cancel";
    public const string SettingsOpenDownloadLocationKey = "updates.settings.openDownloadLocation";
    public const string SettingsLastCheckKey = "updates.settings.lastCheck";
    public const string SettingsLastCheckNeverKey = "updates.settings.lastCheckNever";
    public const string SettingsCheckingKey = "updates.settings.checking";
    public const string SettingsProgressKey = "updates.settings.progress";
    public const string SettingsInstallKey = "updates.settings.install";
    public const string SettingsInstallHintKey = "updates.settings.installHint";

    // ---------------------------------------------------------------- install and restart (wave W10c)
    public const string InstallConfirmTitleKey = "updates.install.confirmTitle";
    public const string InstallConfirmMessageKey = "updates.install.confirmMessage";
    public const string InstallConfirmActionKey = "updates.install.confirmAction";
    public const string InstallStartingKey = "updates.install.starting";
    public const string InstallStartedKey = "updates.install.started";
    public const string InstallErrorNothingStagedKey = "updates.install.errorNothingStaged";
    public const string InstallErrorBusyKey = "updates.install.errorBusy";
    public const string InstallErrorVersionKey = "updates.install.errorVersion";
    public const string InstallErrorPackageMissingKey = "updates.install.errorPackageMissing";
    public const string InstallErrorUnpackKey = "updates.install.errorUnpack";
    public const string InstallErrorNoExecutableKey = "updates.install.errorNoExecutable";
    public const string InstallErrorRefusedKey = "updates.install.errorRefused";
    public const string InstallErrorStartKey = "updates.install.errorStart";
    public const string InstallErrorFailedKey = "updates.install.errorFailed";

    // ---------------------------------------------------------------- outcomes and failures
    public const string ResultUpToDateKey = "updates.result.upToDate";
    public const string ResultAvailableKey = "updates.result.available";
    public const string ResultDevBuildKey = "updates.result.devBuild";
    public const string ResultCancelledKey = "updates.result.cancelled";
    public const string ResultChannelMissingKey = "updates.result.channelMissing";

    public const string ErrorManifestUrlEmptyKey = "updates.error.manifestUrlEmpty";
    public const string ErrorManifestUrlInvalidKey = "updates.error.manifestUrlInvalid";
    public const string ErrorCheckBusyKey = "updates.error.checkBusy";
    public const string ErrorCheckFailedKey = "updates.error.checkFailed";
    public const string ErrorEmptyPackageKey = "updates.error.emptyPackage";
    public const string ErrorManifestTimeoutKey = "updates.error.manifestTimeout";
    public const string ErrorManifestHttpKey = "updates.error.manifestHttp";
    public const string ErrorManifestTooLargeKey = "updates.error.manifestTooLarge";
    public const string ErrorBadManifestKey = "updates.error.badManifest";
    public const string ErrorBadVersionKey = "updates.error.badVersion";
    public const string ErrorNoPackageKey = "updates.error.noPackage";
    public const string ErrorNetworkKey = "updates.error.network";
    public const string ErrorDownloadTimeoutKey = "updates.error.downloadTimeout";
    public const string ErrorDownloadHttpKey = "updates.error.downloadHttp";
    public const string ErrorDownloadTooLargeKey = "updates.error.downloadTooLarge";
    public const string ErrorDownloadFailedKey = "updates.error.downloadFailed";
    public const string ErrorSizeMismatchKey = "updates.error.sizeMismatch";
    public const string ErrorHashMismatchKey = "updates.error.hashMismatch";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    public static string SettingsTitle => Loc.Instance[SettingsTitleKey];

    public static string SettingsHint => Loc.Instance[SettingsHintKey];

    public static string SettingsManifestUrl => Loc.Instance[SettingsManifestUrlKey];

    public static string SettingsManifestUrlHint => Loc.Instance[SettingsManifestUrlHintKey];

    public static string SettingsFrequency => Loc.Instance[SettingsFrequencyKey];

    public static string SettingsFrequencyDaily => Loc.Instance[SettingsFrequencyDailyKey];

    public static string SettingsFrequencyWeekly => Loc.Instance[SettingsFrequencyWeeklyKey];

    public static string SettingsFrequencyNever => Loc.Instance[SettingsFrequencyNeverKey];

    public static string SettingsChannel => Loc.Instance[SettingsChannelKey];

    public static string SettingsChannelStable => Loc.Instance[SettingsChannelStableKey];

    public static string SettingsChannelBeta => Loc.Instance[SettingsChannelBetaKey];

    public static string SettingsCurrentVersion => Loc.Instance[SettingsCurrentVersionKey];

    public static string SettingsCheckNow => Loc.Instance[SettingsCheckNowKey];

    public static string SettingsCancel => Loc.Instance[SettingsCancelKey];

    public static string SettingsOpenDownloadLocation => Loc.Instance[SettingsOpenDownloadLocationKey];

    public static string SettingsLastCheck => Loc.Instance[SettingsLastCheckKey];

    public static string SettingsLastCheckNever => Loc.Instance[SettingsLastCheckNeverKey];

    public static string SettingsChecking => Loc.Instance[SettingsCheckingKey];

    public static LocText SettingsProgress(long bytesRead, long? totalBytes) =>
        new(SettingsProgressKey, TextFormat.Number(bytesRead), TextFormat.Number(totalBytes));

    public static string SettingsInstall => Loc.Instance[SettingsInstallKey];

    public static string SettingsInstallHint => Loc.Instance[SettingsInstallHintKey];

    public static string InstallConfirmTitle => Loc.Instance[InstallConfirmTitleKey];

    public static LocText InstallConfirmMessage(string version) => new(InstallConfirmMessageKey, version);

    public static string InstallConfirmAction => Loc.Instance[InstallConfirmActionKey];

    public static LocText InstallStarting => new(InstallStartingKey);

    public static LocText InstallStarted(string version) => new(InstallStartedKey, version);

    public static LocText InstallErrorNothingStaged => new(InstallErrorNothingStagedKey);
    public static LocText InstallErrorBusy => new(InstallErrorBusyKey);

    public static LocText InstallErrorVersion(string version) => new(InstallErrorVersionKey, version);

    public static LocText InstallErrorPackageMissing(string path) => new(InstallErrorPackageMissingKey, LocText.Literal(path));

    public static LocText InstallErrorUnpack(string detail) => new(InstallErrorUnpackKey, LocText.Literal(detail));

    public static LocText InstallErrorNoExecutable(string name) => new(InstallErrorNoExecutableKey, name);

    public static LocText InstallErrorRefused(string detail) => new(InstallErrorRefusedKey, LocText.Literal(detail));

    public static LocText InstallErrorStart(string path) => new(InstallErrorStartKey, LocText.Literal(path));

    public static LocText InstallErrorFailed(string detail) => new(InstallErrorFailedKey, LocText.Literal(detail));

    public static LocText ResultUpToDate(string version) => new(ResultUpToDateKey, version);

    public static LocText ResultAvailable(string version, long sizeBytes) =>
        new(ResultAvailableKey, version, TextFormat.Number(sizeBytes));

    public static LocText ResultDevBuild(string version) => new(ResultDevBuildKey, version);

    public static LocText ResultCancelled => new(ResultCancelledKey);

    public static LocText ResultChannelMissing(string channel) => new(ResultChannelMissingKey, channel);

    public static LocText ErrorManifestUrlEmpty => new(ErrorManifestUrlEmptyKey);

    public static LocText ErrorManifestUrlInvalid(string url) => new(ErrorManifestUrlInvalidKey, LocText.Literal(url));

    public static LocText ErrorCheckBusy => new(ErrorCheckBusyKey);

    public static LocText ErrorCheckFailed(string detail) => new(ErrorCheckFailedKey, LocText.Literal(detail));

    public static LocText ErrorEmptyPackage => new(ErrorEmptyPackageKey);

    public static LocText ErrorManifestTimeout => new(ErrorManifestTimeoutKey);

    public static LocText ErrorManifestHttp(int status) => new(ErrorManifestHttpKey, status);

    public static LocText ErrorManifestTooLarge => new(ErrorManifestTooLargeKey);

    public static LocText ErrorBadManifest(string detail) => new(ErrorBadManifestKey, LocText.Literal(detail));

    public static LocText ErrorBadVersion(string version) => new(ErrorBadVersionKey, version);

    public static LocText ErrorNoPackage(string version, string rid) => new(ErrorNoPackageKey, version, rid);

    public static LocText ErrorNetwork(string detail) => new(ErrorNetworkKey, LocText.Literal(detail));

    public static LocText ErrorDownloadTimeout => new(ErrorDownloadTimeoutKey);

    public static LocText ErrorDownloadHttp(int status) => new(ErrorDownloadHttpKey, status);

    public static LocText ErrorDownloadTooLarge(long sizeBytes) =>
        new(ErrorDownloadTooLargeKey, TextFormat.Number(sizeBytes));

    public static LocText ErrorDownloadFailed(string detail) => new(ErrorDownloadFailedKey, LocText.Literal(detail));

    public static LocText ErrorSizeMismatch(long actual, long declared) =>
        new(ErrorSizeMismatchKey, TextFormat.Number(actual), TextFormat.Number(declared));

    public static LocText ErrorHashMismatch => new(ErrorHashMismatchKey);

    internal static void Register()
    {
        Loc.Register(SettingsTitleKey, "更新", "Updates");
        Loc.Register(
            SettingsHintKey,
            "自动检查按所选频率在后台运行，只在有新版时下载并通过 SHA-256 校验后暂存到本机；你可以手动打开下载位置，也可以下载完成后直接「安装并重启」。开发构建只支持手动检查。",
            "Automatic checks run in the background on the chosen schedule; a newer version is downloaded, verified with SHA-256 and staged locally, and you can either open the download location or use \"Install and restart\" once it is staged. Development builds only check manually.");
        Loc.Register(SettingsManifestUrlKey, "更新清单地址", "Update manifest URL");
        Loc.Register(
            SettingsManifestUrlHintKey,
            "一个 JSON 清单，描述每个渠道的最新版本与安装包。留空会关闭更新检查。改动对下一次检查生效。",
            "A JSON manifest describing the latest version and packages of each channel. Leave it empty to disable update checks. A change applies to the next check.");
        Loc.Register(SettingsFrequencyKey, "自动检查", "Check automatically");
        Loc.Register(SettingsFrequencyDailyKey, "每日", "Daily");
        Loc.Register(SettingsFrequencyWeeklyKey, "每周", "Weekly");
        Loc.Register(SettingsFrequencyNeverKey, "从不", "Never");
        Loc.Register(SettingsChannelKey, "渠道", "Channel");
        Loc.Register(SettingsChannelStableKey, "稳定版", "Stable");
        Loc.Register(SettingsChannelBetaKey, "测试版", "Beta");
        Loc.Register(SettingsCurrentVersionKey, "当前版本", "Current version");
        Loc.Register(SettingsCheckNowKey, "立即检查", "Check now");
        Loc.Register(SettingsCancelKey, "取消", "Cancel");
        Loc.Register(SettingsOpenDownloadLocationKey, "打开下载位置", "Open download location");
        Loc.Register(SettingsLastCheckKey, "上次检查", "Last check");
        Loc.Register(SettingsLastCheckNeverKey, "尚未检查", "Not checked yet");
        Loc.Register(SettingsCheckingKey, "正在检查…", "Checking…");
        Loc.Register(
            SettingsProgressKey,
            "已下载 {0} / {1} 字节",
            "Downloaded {0} / {1} bytes");
        Loc.Register(SettingsInstallKey, "安装并重启", "Install and restart");
        Loc.Register(
            SettingsInstallHintKey,
            "安装会在退出 Forge 后替换当前安装目录。若新版本启动后没有确认，更新器会自动回滚并重新打开旧版本，过程记录在本机数据目录的 install.log。",
            "Installing replaces the current install directory after Forge exits. If the new version never confirms its startup, the updater rolls back and reopens the old one; the steps are written to install.log in the local data directory.");

        Loc.Register(InstallConfirmTitleKey, "安装并重启", "Install and restart");
        Loc.Register(
            InstallConfirmMessageKey,
            "将关闭 Forge，把当前安装目录替换为已下载并校验的 {0}，然后重新启动。扩展宿主等子进程会一并关闭。继续？",
            "Forge closes, replaces its install directory with the downloaded and verified {0}, and starts again. The extension host and other child processes close with it. Continue?");
        Loc.Register(InstallConfirmActionKey, "安装并重启", "Install and restart");
        Loc.Register(InstallStartingKey, "正在准备安装…", "Preparing the install…");
        Loc.Register(
            InstallStartedKey,
            "更新器已启动，Forge 正在关闭以安装 {0}。",
            "The updater is running; Forge is closing to install {0}.");
        Loc.Register(InstallErrorNothingStagedKey, "没有已下载并校验的安装包。", "No downloaded and verified package is staged.");
        Loc.Register(InstallErrorBusyKey, "正在检查更新,请稍候再试。", "An update check is in progress; try again in a moment.");
        Loc.Register(InstallErrorVersionKey, "版本无法用作文件名：{0}", "The version cannot be used as a file name: {0}");
        Loc.Register(InstallErrorPackageMissingKey, "暂存的安装包已不在：{0}", "The staged package is gone: {0}");
        Loc.Register(InstallErrorUnpackKey, "解包失败：{0}", "The package could not be unpacked: {0}");
        Loc.Register(
            InstallErrorNoExecutableKey,
            "安装包里没有 {0}。",
            "The package does not hold {0}.");
        Loc.Register(InstallErrorRefusedKey, "安装请求被拒绝：{0}", "The install request was refused: {0}");
        Loc.Register(InstallErrorStartKey, "更新器启动失败：{0}", "The updater could not be started: {0}");
        Loc.Register(InstallErrorFailedKey, "安装失败：{0}", "The install failed: {0}");

        Loc.Register(ResultUpToDateKey, "已是最新版本（当前 {0}）。", "You are on the latest version ({0}).");
        Loc.Register(
            ResultAvailableKey,
            "发现新版本 {0}（{1} 字节）。已下载并通过校验，可以「安装并重启」，也可以打开下载位置手动安装。",
            "Version {0} ({1} bytes) is available and has been downloaded and verified. You can install and restart, or open the download location to install it by hand.");
        Loc.Register(
            ResultDevBuildKey,
            "开发构建（{0}）只支持手动检查。",
            "Development builds ({0}) only check for updates manually.");
        Loc.Register(ResultCancelledKey, "已取消。", "Cancelled.");
        Loc.Register(
            ResultChannelMissingKey,
            "更新清单里没有 {0} 渠道，已按最新版本处理。",
            "The update manifest has no {0} channel; there is nothing newer to install.");

        Loc.Register(ErrorManifestUrlEmptyKey, "未设置更新清单地址。", "No update manifest URL is set.");
        Loc.Register(
            ErrorManifestUrlInvalidKey,
            "更新清单地址不可用（需要以 http:// 或 https:// 开头的绝对地址）：{0}",
            "The update manifest URL is unusable (it must be an absolute http:// or https:// address): {0}");
        Loc.Register(ErrorCheckBusyKey, "上一次更新检查还在进行，请稍后再试。", "The previous update check is still running; try again in a moment.");
        Loc.Register(ErrorCheckFailedKey, "更新检查失败：{0}", "The update check failed: {0}");
        Loc.Register(ErrorEmptyPackageKey, "下载到的安装包是空的。", "The downloaded package is empty.");
        Loc.Register(ErrorManifestTimeoutKey, "下载更新清单超时。", "The update manifest request timed out.");
        Loc.Register(ErrorManifestHttpKey, "更新清单请求失败：HTTP {0}。", "The update manifest request failed: HTTP {0}.");
        Loc.Register(ErrorManifestTooLargeKey, "更新清单过大。", "The update manifest is too large.");
        Loc.Register(ErrorBadManifestKey, "更新清单无法读取：{0}", "The update manifest could not be read: {0}");
        Loc.Register(ErrorBadVersionKey, "版本无法解析：{0}", "Version could not be read: {0}");
        Loc.Register(ErrorNoPackageKey, "版本 {0} 没有提供 {1} 的安装包。", "Version {0} has no package for {1}.");
        Loc.Register(ErrorNetworkKey, "网络请求失败：{0}", "Network request failed: {0}");
        Loc.Register(ErrorDownloadTimeoutKey, "下载超时。", "The download timed out.");
        Loc.Register(ErrorDownloadHttpKey, "下载失败：HTTP {0}。", "The download failed: HTTP {0}.");
        Loc.Register(ErrorDownloadTooLargeKey, "下载超过声明的 {0} 字节。", "The download exceeds the declared {0} bytes.");
        Loc.Register(ErrorDownloadFailedKey, "下载失败：{0}", "The download failed: {0}");
        Loc.Register(
            ErrorSizeMismatchKey,
            "下载大小不符（实际 {0}，声明 {1}）。",
            "The download size does not match (got {0}, expected {1}).");
        Loc.Register(ErrorHashMismatchKey, "下载内容校验失败（SHA-256 不匹配）。", "The download failed verification (SHA-256 mismatch).");
    }
}

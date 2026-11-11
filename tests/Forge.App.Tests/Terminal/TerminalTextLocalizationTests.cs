using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Terminal;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Pins the <c>terminal.*</c> string table (forge-app spec, "偏好、文案与设置分区"): every key carries both a Chinese
/// and an English text, and a language switch surfaces the matching text on tab titles and status lines.
///
/// Headless note: this class switches <see cref="Loc"/> language, so it runs its single switching test in an exclusive
/// session with no window alive, collects dead windows first, and restores the previous language before returning —
/// the documented precaution against notifying a closed window from another session's thread.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TerminalTextLocalizationTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Every_terminal_key_has_chinese_and_english_and_switches_cleanly()
    {
        // Let windows of earlier tests be collected before the process-global language switch.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var session = SharedUi.Session;
        var report = await TerminalUi.RunTestAsync(
                session,
                async () =>
                {
                    var previous = Loc.Instance.Language;
                    try
                    {
                        Loc.Instance.Language = UiLanguage.Chinese;
                        var chinese = ReadAll();
                        var chineseTitle = await ReadTitleAsync();
                        var chineseStarting = Loc.Instance[TerminalStrings.StatusStarting];
                        var chineseExited = Loc.Instance.Format(TerminalStrings.StatusExited, 3);

                        Loc.Instance.Language = UiLanguage.English;
                        var english = ReadAll();
                        var englishTitle = await ReadTitleAsync();
                        var englishStarting = Loc.Instance[TerminalStrings.StatusStarting];
                        var englishExited = Loc.Instance.Format(TerminalStrings.StatusExited, 3);

                        return (chinese, english, chineseTitle, englishTitle, chineseStarting, englishStarting, chineseExited, englishExited);
                    }
                    finally
                    {
                        Loc.Instance.Language = previous;
                    }

                    static Dictionary<string, string> ReadAll()
                    {
                        var table = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var key in Loc.Keys.Where(static key => key.StartsWith("terminal.", StringComparison.Ordinal)))
                        {
                            table[key] = Loc.Instance[key];
                        }

                        return table;
                    }

                    static async Task<string> ReadTitleAsync()
                    {
                        var viewModel = new TerminalViewModel(
                            "s1",
                            1,
                            host: null,
                            new TerminalStartRequest("s1", "C:\\work", new TerminalSize(80, 24)),
                            new AppPreferences());
                        try
                        {
                            return viewModel.TabTitle;
                        }
                        finally
                        {
                            await viewModel.DisposeAsync();
                        }
                    }
                },
                Budget);

            Assert.NotEmpty(report.chinese);
            Assert.Equal(
                report.chinese.Keys.OrderBy(static key => key),
                report.english.Keys.OrderBy(static key => key));
            foreach (var (key, text) in report.chinese)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), "missing Chinese text for " + key);
                Assert.NotEqual("[" + key + "]", text);
            }

            foreach (var (key, text) in report.english)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), "missing English text for " + key);
                Assert.NotEqual("[" + key + "]", text);
            }

            // Spot-checks that the switch really lands in each language, not just in non-bracket text.
            Assert.Equal("终端 1", report.chineseTitle);
            Assert.Equal("Terminal 1", report.englishTitle);
            Assert.Equal("正在启动终端…", report.chineseStarting);
            Assert.Equal("Starting the terminal…", report.englishStarting);
            Assert.Contains("退出码", report.chineseExited);
            Assert.Contains("exit code", report.englishExited);
    }
}

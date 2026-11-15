using System.Text;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// The bounded append log's edges: the exact 128 KiB boundary (a line that lands on it is not truncated, the one after it
/// is), concurrent appends all survive without interleaving, and a message that carries newlines, quotes and non-ASCII
/// characters is written verbatim as UTF-8. The log is written by a one-shot updater, so the bounds matter for the
/// post-mortem of a failed swap, not for throughput.
/// </summary>
public sealed class UpdateApplyLogTests
{
    // Each timestamp is a fixed 23 characters ("yyyy-MM-dd HH:mm:ss.fff"); a line is 25 bytes plus its message. A
    // 39-character message therefore makes a 64-byte line, and 2048 of them are exactly 128 * 1024 bytes.
    private const int MessageLength = 39;

    private static string Message(int index) =>
        "m" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 34);

    [Fact]
    public void A_log_that_lands_exactly_on_the_bound_is_not_truncated()
    {
        var root = TestTempFiles.NewDirectory("forge-apply-log-bound-");
        try
        {
            var log = Path.Combine(root, UpdateApplyPaths.InstallLogFileName);
            for (var i = 0; i < 2048; i++)
            {
                UpdateInstallLog.Append(log, Message(i));
            }

            Assert.Equal(UpdateInstallLog.MaxBytes, new FileInfo(log).Length);
            var text = File.ReadAllText(log);
            Assert.DoesNotContain("(log truncated)", text, StringComparison.Ordinal);
            Assert.Contains(Message(0), text, StringComparison.Ordinal);
            Assert.Contains(Message(2047), text, StringComparison.Ordinal);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public void The_bound_is_crossed_only_by_the_byte_that_exceeds_it()
    {
        var root = TestTempFiles.NewDirectory("forge-apply-log-over-");
        try
        {
            var log = Path.Combine(root, UpdateApplyPaths.InstallLogFileName);
            for (var i = 0; i < 2049; i++)
            {
                UpdateInstallLog.Append(log, Message(i));
            }

            Assert.True(new FileInfo(log).Length <= UpdateInstallLog.MaxBytes, "the log grew past its bound");
            var text = File.ReadAllText(log);
            Assert.Contains("(log truncated)", text, StringComparison.Ordinal);
            Assert.Contains(Message(2048), text, StringComparison.Ordinal);
            Assert.DoesNotContain(Message(0), text, StringComparison.Ordinal);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public void Concurrent_appends_are_serialized_and_none_are_lost()
    {
        var root = TestTempFiles.NewDirectory("forge-apply-log-race-");
        try
        {
            var log = Path.Combine(root, UpdateApplyPaths.InstallLogFileName);
            Parallel.For(0, 1000, i => UpdateInstallLog.Append(log, "line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            var text = File.ReadAllText(log);
            for (var i = 0; i < 1000; i++)
            {
                Assert.Contains("line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("(log truncated)", text, StringComparison.Ordinal);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public void A_message_with_newlines_quotes_and_non_ascii_characters_is_written_verbatim_as_utf8()
    {
        var root = TestTempFiles.NewDirectory("forge-apply-log-chars-");
        try
        {
            var log = Path.Combine(root, UpdateApplyPaths.InstallLogFileName);
            UpdateInstallLog.Append(log, "install 'C:\\Program Files\\Forge' 版本 1.2.0\nnext <&> 行");

            var text = File.ReadAllText(log);
            Assert.Contains("C:\\Program Files\\Forge", text, StringComparison.Ordinal);
            Assert.Contains("版本 1.2.0", text, StringComparison.Ordinal);
            Assert.Contains("next <&> 行", text, StringComparison.Ordinal);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }
}

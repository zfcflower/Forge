using Xunit;

namespace Forge.Scripting.Tests;

/// <summary>Non-cooperative loops run in a disposable child process the parent always reaps.</summary>
public sealed class ScriptCodeRunnerTerminationTests
{
    [Fact]
    public void Synchronous_loop_hits_timeout_and_is_abandoned_for_termination()
    {
        var line = RunChildAndReadResult(ScriptCodeChildProcess.TimeoutLoopMode);

        Assert.Contains("IsError=True", line, StringComparison.Ordinal);
        Assert.Contains("RequiresTermination=True", line, StringComparison.Ordinal);
        Assert.Contains("Calls=0", line, StringComparison.Ordinal);
        Assert.Contains("abandoned", line, StringComparison.Ordinal);
        Assert.Contains("must be terminated", line, StringComparison.Ordinal);
        Assert.Contains("0.5 s", line, StringComparison.Ordinal);
        Assert.DoesNotContain("The script was stopped after", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Synchronous_loop_ignores_external_cancel_and_is_abandoned()
    {
        var line = RunChildAndReadResult(ScriptCodeChildProcess.CancelLoopMode);

        Assert.Contains("IsError=True", line, StringComparison.Ordinal);
        Assert.Contains("RequiresTermination=True", line, StringComparison.Ordinal);
        // Calls=1 proves the script started before abandonment.
        Assert.Contains("Calls=1", line, StringComparison.Ordinal);
        Assert.Contains("cancelled", line, StringComparison.Ordinal);
        Assert.Contains("abandoned", line, StringComparison.Ordinal);
        Assert.Contains("must be terminated", line, StringComparison.Ordinal);
        Assert.DoesNotContain("cancelled and the script stopped", line, StringComparison.Ordinal);
    }

    private static string RunChildAndReadResult(string mode)
    {
        using var child = ScriptCodeChildProcess.Start(mode);
        string? line;
        bool exited;
        try
        {
            line = ScriptCodeChildProcess.WaitForLine(child, TimeSpan.FromSeconds(60));
        }
        finally
        {
            // Always reap the whole tree: the abandoned loop is still executing in the child.
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            exited = child.WaitForExit(TimeSpan.FromSeconds(30));
        }

        Assert.True(exited, "the abandoned child process exited after the whole process tree was killed");
        Assert.False(string.IsNullOrEmpty(line), "the child never reported its abandoned run; it may have hung instead of abandoning");
        return line!;
    }
}

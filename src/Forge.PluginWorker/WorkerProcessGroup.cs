using System.Runtime.InteropServices;

namespace Forge.PluginWorker;

/// <summary>
/// Puts the worker into a process group of its own so that the host can kill the worker together with every process it
/// started by signalling the group, without needing the worker to still be alive. All P/Invoke of the worker lives in
/// this class; nothing here is unsafe and it does nothing on Windows.
/// </summary>
internal static class WorkerProcessGroup
{
    /// <summary>Makes this process the leader of a new process group whose id is its own pid. Must run before the
    /// worker connects to the host, so no plugin code can start a descendant outside the group.</summary>
    internal static void JoinOwnGroup()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (setpgid(0, 0) == 0)
            {
                return;
            }

            var error = Marshal.GetLastPInvokeError();
            Console.Error.WriteLine(
                $"forge-plugin-worker: setpgid(0, 0) failed with errno {error}; the host can only kill the process tree by handle.");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine(
                $"forge-plugin-worker: setpgid is unavailable ({exception.Message}); the host can only kill the process tree by handle.");
        }
    }

#pragma warning disable SYSLIB1054
    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int setpgid(int processId, int groupId);
#pragma warning restore SYSLIB1054
}

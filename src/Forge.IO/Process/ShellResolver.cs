namespace Forge.IO;

/// <summary>How a shell wants a command string handed to it.</summary>
public enum ShellDialect
{
    /// <summary>PowerShell 7 (<c>-NoProfile -NonInteractive -Command</c>).</summary>
    Pwsh,

    /// <summary><c>cmd.exe</c> (<c>/d /s /c</c>).</summary>
    Cmd,

    /// <summary>A Bourne-compatible shell (<c>-c</c>).</summary>
    Bash,

    /// <summary>A minimal POSIX shell (<c>-c</c>).</summary>
    Sh,
}

/// <summary>One resolved shell: the executable to start and how to make it run a command string and exit.</summary>
/// <param name="FileName">The full path of the shell executable.</param>
/// <param name="Dialect">The syntax the shell parses.</param>
/// <remarks>
/// <c>cmd</c> parses the remainder of its own command line with its own rules and does not undo the quoting the process
/// launcher applies to an argument containing a double quote, so a cmd script that uses inner double quotes arrives with
/// those quotes escaped. That is accepted rather than worked around: passing a raw command line would bypass the argument
/// vector that keeps a path or a message from being re-parsed, and <c>cmd</c> is only the fallback dialect on Windows.
/// </remarks>
public sealed record Shell(string FileName, ShellDialect Dialect)
{
    /// <summary>The dialect as tool output names it: <c>pwsh</c>, <c>cmd</c>, <c>bash</c> or <c>sh</c>.</summary>
    public string DialectName => Dialect switch
    {
        ShellDialect.Pwsh => "pwsh",
        ShellDialect.Cmd => "cmd",
        ShellDialect.Bash => "bash",
        _ => "sh",
    };

    /// <summary>The arguments that make this shell run <paramref name="command"/> and exit, in this dialect's own syntax.</summary>
    public IReadOnlyList<string> ArgumentsForScript(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Dialect switch
        {
            ShellDialect.Pwsh => ["-NoProfile", "-NonInteractive", "-Command", command],
            ShellDialect.Cmd => ["/d", "/s", "/c", command],
            _ => ["-c", command],
        };
    }
}

/// <summary>
/// Chooses the shell that runs a command string on this platform: <c>pwsh</c> when it is there, the system command
/// interpreter otherwise, and <c>/bin/bash</c> or <c>/bin/sh</c> on Unix. Resolution only decides which executable to
/// start; it never substitutes a different dialect after a start failure, because a <c>cmd</c> fallback for a <c>pwsh</c>
/// script is a silently wrong answer rather than a slower right one.
/// </summary>
public static class ShellResolver
{
    /// <summary>
    /// Resolves the shell to run scripts with.
    /// </summary>
    /// <param name="overridePath">
    /// A configured shell: an absolute path, or a name looked up on <c>PATH</c>. Empty means "the platform default".
    /// </param>
    /// <exception cref="FileNotFoundException">
    /// The configured shell does not exist, or no shell of the platform default set is installed. The message names the
    /// shell or the paths that were tried.
    /// </exception>
    public static Shell Resolve(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var trimmed = overridePath.Trim();
            var executable = FindExecutable(trimmed)
                ?? throw new FileNotFoundException(
                    $"The configured shell '{trimmed}' does not exist or is not on PATH.",
                    trimmed);
            return new Shell(executable, DialectOf(executable));
        }

        return OperatingSystem.IsWindows() ? ResolveWindows() : ResolveUnix();
    }

    private static Shell ResolveWindows()
    {
        if (FindExecutable("pwsh") is { } pwsh)
        {
            return new Shell(pwsh, ShellDialect.Pwsh);
        }

        if (PowerShellSevenPath() is { } installed)
        {
            return new Shell(installed, ShellDialect.Pwsh);
        }

        if (Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } commandProcessor && File.Exists(commandProcessor))
        {
            return new Shell(Path.GetFullPath(commandProcessor), ShellDialect.Cmd);
        }

        if (FindExecutable("cmd") is { } cmd)
        {
            return new Shell(cmd, ShellDialect.Cmd);
        }

        throw new FileNotFoundException(
            "No shell was found: 'pwsh' is not on PATH, PowerShell 7 is not installed, and neither %ComSpec% nor cmd.exe is available.");
    }

    private static Shell ResolveUnix()
    {
        if (File.Exists("/bin/bash"))
        {
            return new Shell("/bin/bash", ShellDialect.Bash);
        }

        if (File.Exists("/bin/sh"))
        {
            return new Shell("/bin/sh", ShellDialect.Sh);
        }

        throw new FileNotFoundException("No shell was found: neither /bin/bash nor /bin/sh exists.");
    }

    /// <summary>The PowerShell 7 install location, which is on PATH only when the user added it or the machine installer did.</summary>
    private static string? PowerShellSevenPath()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        if (string.IsNullOrEmpty(programFiles))
        {
            programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }

        if (string.IsNullOrEmpty(programFiles))
        {
            return null;
        }

        var candidate = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Resolves a configured shell: a path (anything with a directory separator or a root) must exist as written, a bare
    /// name is looked up on <c>PATH</c> with <c>PATHEXT</c> on Windows. Returns the full path, or <c>null</c>.
    /// </summary>
    private static string? FindExecutable(string name)
    {
        if (Path.IsPathRooted(name) || name.Contains('/') || name.Contains('\\'))
        {
            return File.Exists(name) ? Path.GetFullPath(name) : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folder = directory.Trim('"');
            if (folder.Length == 0 || !Directory.Exists(folder))
            {
                continue;
            }

            var direct = Path.Combine(folder, name);
            if (File.Exists(direct))
            {
                return Path.GetFullPath(direct);
            }

            if (!OperatingSystem.IsWindows())
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder, name + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The dialect a configured shell parses, read from its file name; anything unrecognized gets <c>-c</c>, which is what
    /// every Bourne-style shell accepts.
    /// </summary>
    private static ShellDialect DialectOf(string executable)
    {
        var name = Path.GetFileName(executable);
        if (name.Length == 0)
        {
            return ShellDialect.Sh;
        }

        return name.ToLowerInvariant() switch
        {
            "pwsh" or "pwsh.exe" or "powershell" or "powershell.exe" => ShellDialect.Pwsh,
            "cmd" or "cmd.exe" => ShellDialect.Cmd,
            "bash" or "bash.exe" => ShellDialect.Bash,
            _ => ShellDialect.Sh,
        };
    }
}

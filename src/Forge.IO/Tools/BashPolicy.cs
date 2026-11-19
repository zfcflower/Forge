using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// What <c>bash</c> may not do, because a dedicated tool owns it. The rule is the one the tool whitelist already states:
/// no tool, no capability — <c>bash</c> is never a way around it. So a command that duplicates a built-in tool is refused
/// whether or not the caller holds that tool (a caller without it simply does not have the capability), and the refusal names
/// the owner so the model corrects itself in one step instead of retrying.
/// <para>Files (<c>read</c>/<c>grep</c>/<c>find</c>/<c>ls</c>/<c>edit</c>/<c>write</c>) and web pages (<c>web_fetch</c>) are
/// refused for everyone. Git depends on the caller: an agent that holds no git tool gets no git through <c>bash</c> at all
/// (that is every built-in sub-agent), and one that does hold them gets the git commands no tool covers (<c>show</c>,
/// <c>blame</c>, <c>rev-parse</c>, … for any git tool; <c>add</c>, <c>commit</c>, <c>stash</c>, <c>reset</c>, <c>restore</c>,
/// <c>cherry-pick</c>, … for one that holds a write tool) but not the ones a tool owns. <c>gh pr</c>, <c>gh run</c>,
/// <c>gh workflow</c> and <c>gh api</c> follow the same rule with the <c>pr_*</c> tools: refused for a caller that holds none,
/// free for one that does; the rest of <c>gh</c> is not restricted. A <c>grep</c>/<c>head</c>/<c>tail</c> that only filters a
/// pipe (<c>dotnet build | grep error</c>) is not a file operation and is allowed.</para>
/// <para>This is a guard rail on the command text, not a sandbox: what a script or a build tool runs inside is not seen.</para>
/// </summary>
internal static class BashPolicy
{
    private static readonly string[] GitTools = ["git_status", "git_log", "git_diff", "git_fetch", "git_branch", "git_sync"];

    private static readonly Dictionary<string, string> FileOwners = BuildFileOwners();

    /// <summary>The copy, move and delete commands and the tool that owns each.</summary>
    private static readonly Dictionary<string, string> FileOperationOwners = new(StringComparer.Ordinal)
    {
        ["cp"] = "copy", ["copy"] = "copy", ["copy-item"] = "copy", ["cpi"] = "copy",
        ["mv"] = "move", ["move"] = "move", ["move-item"] = "move", ["mi"] = "move", ["ren"] = "move", ["rename"] = "move", ["rename-item"] = "move",
        ["rm"] = "delete", ["del"] = "delete", ["erase"] = "delete", ["rmdir"] = "delete", ["rd"] = "delete", ["remove-item"] = "delete",
        ["ri"] = "delete", ["unlink"] = "delete",
    };

    private static readonly HashSet<string> WebCommands = new(StringComparer.Ordinal)
    {
        "curl", "wget", "invoke-webrequest", "iwr", "invoke-restmethod", "irm",
    };

    /// <summary>Commands that finish in milliseconds and touch nothing a build reads: they do not wait for the workspace lock.</summary>
    private static readonly HashSet<string> CheapCommands = new(StringComparer.Ordinal)
    {
        "echo", "pwd", "cd", "whoami", "hostname", "date", "which", "where", "true", "false", "printenv",
    };

    /// <summary>The git subcommands that only read (and are cheap): no tool owns them, so any git tool unlocks them, and they skip the workspace lock.</summary>
    private static readonly HashSet<string> ReadOnlyGit = new(StringComparer.Ordinal)
    {
        string.Empty, "show", "blame", "rev-parse", "rev-list", "ls-files", "ls-tree", "describe", "shortlog", "cat-file", "reflog",
        "version", "help", "branch",
    };

    private static readonly string[] WriteGitTools = ["git_branch", "git_sync"];

    /// <summary>Options that take the next word as their value, so that word is not a URL (case-sensitive: curl's <c>-O</c> takes none, wget's does).</summary>
    private static readonly HashSet<string> WebValueFlags = new(StringComparer.Ordinal)
    {
        "-o", "-H", "-d", "-X", "-A", "-u", "-b", "-c", "-e", "-F", "-T", "-m", "-w", "-K", "-x", "-r", "-C", "-E", "-D", "-P",
        "--output", "--header", "--data", "--data-raw", "--data-binary", "--data-urlencode", "--request", "--user-agent", "--user",
        "--cookie", "--cookie-jar", "--referer", "--form", "--upload-file", "--max-time", "--connect-timeout", "--proxy", "--retry",
        "--output-dir", "--config", "--write-out", "--cacert", "--cert", "--key", "--limit-rate",
    };

    /// <summary>The PowerShell web cmdlets' value parameters (their names are case-insensitive); <c>-Uri</c> is deliberately absent, its value is a URL.</summary>
    private static readonly HashSet<string> PowerShellValueFlags = new(StringComparer.Ordinal)
    {
        "-outfile", "-method", "-body", "-headers", "-contenttype", "-timeoutsec", "-useragent", "-credential", "-websession",
        "-maximumredirection", "-infile", "-proxy",
    };

    private static readonly HashSet<string> CheapDotnetArguments = new(StringComparer.Ordinal)
    {
        "--version", "--info", "--list-sdks", "--list-runtimes", "-h", "--help",
    };

    private static readonly HashSet<string> BranchMutatingFlags = new(StringComparer.Ordinal)
    {
        "-d", "-D", "-m", "-M", "-c", "-C", "-u", "-f", "--delete", "--move", "--copy", "--force", "--set-upstream-to",
        "--unset-upstream", "--edit-description",
    };

    private static readonly HashSet<string> BranchListingFlags = new(StringComparer.Ordinal)
    {
        "--list", "-l", "--contains", "--no-contains", "--merged", "--no-merged", "--points-at",
    };

    /// <summary>A refusal text (an error result for the model), or null when every command in the line may run.</summary>
    public static string? Check(IReadOnlyList<ShellCommand> commands, IToolRegistry? tools, string? workspace = null)
    {
        foreach (var command in commands)
        {
            var refusal = CheckOne(command, tools, workspace);
            if (refusal is not null)
            {
                return refusal;
            }
        }

        return null;
    }

    /// <summary>True when every command is on the short list of trivially cheap ones; an empty or unrecognised line is not.</summary>
    public static bool IsCheap(IReadOnlyList<ShellCommand> commands)
    {
        if (commands.Count == 0)
        {
            return false;
        }

        foreach (var command in commands)
        {
            if (CheapCommands.Contains(command.Name))
            {
                continue;
            }

            if (command.Name == "dotnet" && command.Arguments.Count > 0 && command.Arguments.All(CheapDotnetArguments.Contains))
            {
                continue;
            }

            // Only the reading git subcommands are cheap: stash, reset, restore, clean … rewrite sources a build reads.
            if (command.Name == "git")
            {
                var subcommand = Subcommand(command.Arguments, out var gitRest);
                if (ReadOnlyGit.Contains(subcommand) && (subcommand != "branch" || !IsBranchMutation(gitRest)))
                {
                    continue;
                }
            }

            return false;
        }

        return true;
    }

    private static string? CheckOne(ShellCommand command, IToolRegistry? tools, string? workspace)
    {
        if (FileOperationOwners.TryGetValue(command.Name, out var operationOwner))
        {
            return DuplicatesFileOperationTool(command, operationOwner, tools, workspace) ? Refuse(command.Name, operationOwner) : null;
        }

        if (FileOwners.TryGetValue(command.Name, out var owner))
        {
            return command.FromPipe && ReadsOnlyStdin(command) ? null : Refuse(command.Name, owner);
        }

        if (command.Name == "sed" && IsInPlaceSed(command.Arguments))
        {
            return Refuse("sed -i", "edit");
        }

        if (WebCommands.Contains(command.Name))
        {
            return IsLoopbackOnly(command.Arguments)
                ? null
                : $"bash does not fetch web pages ('{command.Name}'): that belongs to the web_fetch tool, and an agent without it does not get web access through bash. "
                    + "A loopback address (localhost, 127.0.0.1) is allowed, for testing a server you started.";
        }

        return command.Name switch
        {
            "git" => CheckGit(command.Arguments, tools),
            "gh" => CheckGh(command.Arguments, tools),
            _ => null,
        };
    }

    /// <summary>
    /// <c>gh pr</c>, <c>gh run</c>, <c>gh workflow</c> and <c>gh api</c> (which reaches the same endpoints) are pull-request and CI
    /// operations: an agent that holds no <c>pr_*</c> tool has no such capability, and bash is not a way around that. One that
    /// does hold them may use <c>gh</c> freely (the tools do not cover all of it, and what it does is its own business).
    /// <c>gh repo sync</c> is a git sync and follows the git rule.
    /// </summary>
    private static readonly HashSet<string> GhRepositoryCommands = new(["pr", "run", "workflow", "api"], StringComparer.OrdinalIgnoreCase);

    private static string? CheckGh(IReadOnlyList<string> arguments, IToolRegistry? tools)
    {
        var words = arguments.Where(argument => !argument.StartsWith('-')).Take(2).ToArray();
        if (words.Length == 0)
        {
            return null;
        }

        // A holder of any pr_* tool gets all of gh, whatever the subcommand.
        if (HoldsAnyPrTool(tools))
        {
            return null;
        }

        var subcommand = words[0];
        if (GhRepositoryCommands.Contains(subcommand))
        {
            return $"bash does not run 'gh {subcommand}': this agent holds no pr_* tool, and bash is not a way around that. " +
                "Pull requests and their CI runs belong to the main agent.";
        }

        if (subcommand.Equals("repo", StringComparison.OrdinalIgnoreCase) && words.Length > 1
            && words[1].Equals("sync", StringComparison.OrdinalIgnoreCase) && !HoldsAnyGitTool(tools))
        {
            return "bash does not run 'gh repo sync': this agent holds no git tool, and bash is not a way around that.";
        }

        return null;
    }

    private static bool HoldsAnyPrTool(IToolRegistry? tools)
    {
        if (tools is null)
        {
            return false;
        }

        foreach (var tool in tools.Tools)
        {
            if (tool.Definition.Name.StartsWith("pr_", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Refuse(string what, string owner) =>
        $"bash does not run '{what}': that is the '{owner}' tool's job. Use the {owner} tool; bash is for what no other tool does " +
        "(building, testing, running programs, package managers, copying, moving and deleting files).";

    private static string? CheckGit(IReadOnlyList<string> arguments, IToolRegistry? tools)
    {
        if (!HoldsAnyGitTool(tools))
        {
            return "bash does not run git for this agent: it holds no git tool, and bash is not a way around that. " +
                "Git state (commits, branches, syncing) belongs to the main agent and the host.";
        }

        var subcommand = Subcommand(arguments, out var rest);
        switch (subcommand)
        {
            case "status":
                return Owned(subcommand, "git_status", tools);
            case "log":
                return Owned(subcommand, "git_log", tools);
            case "diff":
                return Owned(subcommand, "git_diff", tools);
            case "fetch":
                return Owned(subcommand, "git_fetch", tools);
            case "switch":
                return Owned(subcommand, "git_branch", tools);
            case "checkout":
                return Owned(subcommand, "git_branch", tools, "To restore files from a commit use 'git restore'.");
            case "branch" when IsBranchMutation(rest):
                return Owned(subcommand, "git_branch", tools);
            case "pull" or "push" or "rebase" or "merge":
                return Owned(subcommand, "git_sync", tools);
            default:
                // What no tool covers: reading commands need any git tool, the rest (add, commit, stash, reset, restore, …) a write tool.
                return ReadOnlyGit.Contains(subcommand) || HoldsWriteGitTool(tools)
                    ? null
                    : $"bash does not run 'git {subcommand}': this agent holds only read-only git tools (git_branch or git_sync are what allow changing the repository), " +
                        "and bash is not a way around that.";
        }
    }

    private static string Owned(string subcommand, string tool, IToolRegistry? tools, string? addendum = null)
    {
        var held = tools is not null && tools.TryGet(tool, out _);
        var text = held
            ? $"bash does not run 'git {subcommand}': use the {tool} tool."
            : $"bash does not run 'git {subcommand}': that belongs to the {tool} tool, which this agent does not hold, and bash does not replace it.";
        return addendum is null ? text : text + " " + addendum;
    }

    private static bool HoldsWriteGitTool(IToolRegistry? tools)
    {
        if (tools is null)
        {
            return false;
        }

        foreach (var name in WriteGitTools)
        {
            if (tools.TryGet(name, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HoldsAnyGitTool(IToolRegistry? tools)
    {
        if (tools is null)
        {
            return false;
        }

        foreach (var name in GitTools)
        {
            if (tools.TryGet(name, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The git subcommand after the global options (<c>-C dir</c>, <c>-c k=v</c>, <c>--no-pager</c>, …) and the words after it.</summary>
    private static string Subcommand(IReadOnlyList<string> arguments, out IReadOnlyList<string> rest)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is "-C" or "-c" or "--git-dir" or "--work-tree" or "--namespace")
            {
                index++;
                continue;
            }

            if (argument.StartsWith('-'))
            {
                continue;
            }

            rest = [.. arguments.Skip(index + 1)];
            return argument.ToLowerInvariant();
        }

        rest = [];
        return string.Empty;
    }

    /// <summary><c>git branch</c> with a name or a modifying flag changes branches; plain listing does not.</summary>
    private static bool IsBranchMutation(IReadOnlyList<string> rest)
    {
        var listing = false;
        var positional = false;
        foreach (var argument in rest)
        {
            if (BranchMutatingFlags.Contains(argument) || argument.StartsWith("--set-upstream-to=", StringComparison.Ordinal))
            {
                return true;
            }

            if (BranchListingFlags.Contains(argument))
            {
                listing = true;
            }
            else if (!argument.StartsWith('-'))
            {
                positional = true;
            }
        }

        return positional && !listing;
    }

    private static bool IsInPlaceSed(IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument == "--in-place" || argument.StartsWith("--in-place=", StringComparison.Ordinal))
            {
                return true;
            }

            // -i, -i.bak, and clusters such as -ni or -Ei.
            if (argument.Length > 1 && argument[0] == '-' && argument[1] != '-' && argument.Contains('i'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the command names at least one URL and every URL it names is a loopback host. Every word that is not an option or an option's value counts as a URL, scheme or not.</summary>
    private static bool IsLoopbackOnly(IReadOnlyList<string> arguments)
    {
        var urls = 0;
        var skipNext = false;
        foreach (var argument in arguments)
        {
            if (skipNext)
            {
                skipNext = false;
                if (!argument.Contains("://", StringComparison.Ordinal))
                {
                    continue;
                }
            }

            if (argument.StartsWith('-'))
            {
                skipNext = WebValueFlags.Contains(argument) || PowerShellValueFlags.Contains(argument.ToLowerInvariant());
                continue;
            }

            urls++;
            string host;
            if (argument.Contains("://", StringComparison.Ordinal))
            {
                if (!Uri.TryCreate(argument, UriKind.Absolute, out var uri))
                {
                    return false;
                }

                host = uri.Host;
            }
            else
            {
                host = argument.StartsWith('[') ? argument[..(argument.IndexOf(']') + 1)] : argument.Split(':', '/')[0];
            }

            if (!IsLoopbackHost(host))
            {
                return false;
            }
        }

        return urls > 0;
    }

    /// <summary>
    /// True for a search or paging command that only filters its input pipe: no file operand, no recursion, so it reads
    /// <c>dotnet build | grep error</c>'s output and no file. The file tools cannot see process output, which is why this is allowed.
    /// </summary>
    private static bool ReadsOnlyStdin(ShellCommand command)
    {
        var arguments = command.Arguments;
        switch (command.Name)
        {
            case "head" or "tail":
                return arguments.All(argument => argument.StartsWith('-') || IsNumber(argument));
            case "grep" or "egrep" or "fgrep" or "rg" or "ag" or "ack":
                return FilterOperandsOk(arguments, '-');
            case "findstr":
                return FilterOperandsOk(arguments, '/');
            case "select-string" or "sls":
                return FilterOperandsOk(arguments, '-');
            default:
                return false;
        }
    }

    private static bool FilterOperandsOk(IReadOnlyList<string> arguments, char optionPrefix)
    {
        var patternGiven = false;
        var operands = 0;
        foreach (var argument in arguments)
        {
            if (argument.Length > 1 && argument[0] == optionPrefix)
            {
                var name = argument.TrimStart(optionPrefix).ToLowerInvariant();
                if (name is "path" or "literalpath" or "recursive" or "dereference-recursive"
                    || (optionPrefix == '/' && name == "s") || (optionPrefix == '-' && name == "r")
                    || name.StartsWith("include", StringComparison.Ordinal))
                {
                    return false;
                }

                if (name is "pattern" or "regexp" or "file" or "e" || name.StartsWith("regexp=", StringComparison.Ordinal)
                    || name.StartsWith("file=", StringComparison.Ordinal) || name.StartsWith("c:", StringComparison.Ordinal)
                    || name.StartsWith("g:", StringComparison.Ordinal))
                {
                    patternGiven = true;
                }
                else if (optionPrefix == '-' && argument[1] != '-' && argument.Length > 2)
                {
                    // A short-option cluster such as -rn, -ie, -Hn.
                    if (argument.Contains('r') || argument.Contains('R'))
                    {
                        return false;
                    }

                    if (argument.Contains('e') || argument.Contains('f'))
                    {
                        patternGiven = true;
                    }
                }

                continue;
            }

            if (!IsNumber(argument))
            {
                operands++;
            }
        }

        return operands <= (patternGiven ? 0 : 1);
    }

    private static bool IsNumber(string argument) => argument.Length > 0 && argument.All(char.IsAsciiDigit);

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host is "[::1]" or "::1"
        || (host.StartsWith("127.", StringComparison.Ordinal) && Uri.CheckHostName(host) == UriHostNameType.IPv4);

    /// <summary>
    /// True when the command does what the owning tool does. Not a duplicate: a caller that holds no such tool, a shell
    /// expansion the tools do not do (<c>$VAR</c>, a command substitution), a command fed by a pipe, or a path the tool would refuse because it lies outside the workspace.
    /// </summary>
    private static bool DuplicatesFileOperationTool(ShellCommand command, string owner, IToolRegistry? tools, string? workspace)
    {
        if (command.FromPipe || (tools is not null && !tools.TryGet(owner, out _)))
        {
            return false;
        }

        var paths = 0;
        foreach (var argument in command.Arguments)
        {
            if (argument.Length == 0 || argument[0] == '-' || (argument[0] == '/' && command.Name is "del" or "rd" or "rmdir" or "copy" or "move" or "ren"))
            {
                continue;
            }

            if (argument.AsSpan().IndexOfAny("$`") >= 0 || (workspace is not null && !InsideWorkspace(argument, workspace)))
            {
                return false;
            }

            paths++;
        }

        return paths > 0;
    }

    private static bool InsideWorkspace(string argument, string workspace)
    {
        if (argument[0] == '~')
        {
            return false;
        }

        try
        {
            return WorkspacePath.IsUnder(
                Path.IsPathRooted(argument) ? argument : Path.Combine(workspace, argument),
                workspace);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> BuildFileOwners()
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        Add("read", "cat", "head", "tail", "get-content", "gc");
        Add("ls", "ls", "dir", "get-childitem", "gci");
        Add("find", "find", "fd", "fdfind");
        Add("grep", "grep", "egrep", "fgrep", "rg", "ag", "ack", "findstr", "select-string", "sls");
        Add("write", "tee", "tee-object", "set-content", "add-content", "out-file");
        if (OperatingSystem.IsWindows())
        {
            // `type` is a file reader in cmd and PowerShell but a harmless builtin in bash, so it is only refused where it reads files.
            Add("read", "type");
        }

        return owners;

        void Add(string owner, params string[] names)
        {
            foreach (var name in names)
            {
                owners[name] = owner;
            }
        }
    }
}

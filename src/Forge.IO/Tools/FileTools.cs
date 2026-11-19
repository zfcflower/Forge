using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// The file and shell tools an agent runs with, built once and shared by every session: they are stateless, take the
/// workspace from each call's <see cref="ToolContext"/>, and read their bounds through <see cref="Func{ToolOptions}"/> so a
/// preference change applies to the next call.
/// <para>The set is the one the built-in agents name — <c>read</c>, <c>write</c>, <c>edit</c>, <c>ls</c>, <c>find</c>,
/// <c>grep</c>, <c>copy</c>, <c>move</c>, <c>delete</c>, <c>bash</c> — and nothing more: fewer, sharper tools are cheaper to
/// route and to cache than a family of synonyms. Copy, move and delete are tools of their own because a process start costs
/// far more than the file operation, and because the workspace bound and the path gate cannot see inside a shell command.</para>
/// </summary>
public sealed class FileTools
{
    public FileTools()
        : this(ToolOptions.Default)
    {
    }

    public FileTools(ToolOptions options)
        : this(FixedOptions(options), null)
    {
    }

    /// <param name="options">The bounds to read on every call.</param>
    /// <param name="runner">The process runner to share; null creates one for this set.</param>
    public FileTools(Func<ToolOptions> options, IProcessRunner? runner = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var processes = runner ?? new ProcessRunner(options().MaxConcurrentProcesses);

        Read = new ReadTool(options);
        Write = new WriteTool();
        Edit = new EditTool(options);
        List = new ListTool(options);
        Find = new FindTool(options);
        Grep = new GrepTool(options, processes);
        Copy = new CopyTool();
        Move = new MoveTool();
        Delete = new DeleteTool();
        Bash = new BashTool(processes, options);

        All = [Read, Write, Edit, List, Find, Grep, Copy, Move, Delete, Bash];
    }

    public ITool Read { get; }

    public ITool Write { get; }

    public ITool Edit { get; }

    public ITool List { get; }

    public ITool Find { get; }

    public ITool Grep { get; }

    public ITool Copy { get; }

    public ITool Move { get; }

    public ITool Delete { get; }

    public ITool Bash { get; }

    /// <summary>Every tool in registration order; <see cref="ToolRegistry"/> sorts them by name anyway.</summary>
    public IReadOnlyList<ITool> All { get; }

    private static Func<ToolOptions> FixedOptions(ToolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return () => options;
    }
}

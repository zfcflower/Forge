using System.Globalization;
using System.Text;
using Forge.ScriptRuntime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Forge.Scripting;

/// <summary>
/// Wraps a run_code script into the one entry point signature the artifact declares:
/// <c>Task&lt;object?&gt; (ScriptGlobals, CancellationToken)</c>. The generated type derives from
/// <see cref="ScriptGlobals"/>, so <c>Tools</c>, <c>Log</c> and <c>Cancellation</c> are in scope exactly as they were in
/// the old script engine, and the script text sits verbatim inside an instance method behind a <c>#line</c> directive, so
/// its own line and column numbers are what diagnostics and stack frames report - there is no offset to subtract and no
/// second source file.
/// <para>
/// The old script semantics this reproduces: top-level statements run inside an async method (so top-level
/// <c>await</c> works), the script's trailing expression becomes the script's result, and a returned task is awaited and
/// unwrapped. A trailing statement expression (a call, an assignment, a <c>new</c>, an increment or an <c>await</c>) is
/// returned the same way unless the semantic model reports its type as <c>void</c> or as a ref-like type: only a
/// genuinely void one - a <c>Log(...)</c> call, an <c>await</c> of a non-generic task - or one that yields a ref struct
/// (which cannot be boxed) stays a statement.
/// </para>
/// </summary>
internal static class ScriptSourceBuilder
{
    /// <summary>Diagnostics and stack frames name the script by this file, exactly as the old script engine did.</summary>
    public const string SourceFile = "script.csx";

    private const string ResultCast = "return (object?)(";

    /// <summary>The generated method the script body lives in.</summary>
    private const string ScriptMethodName = "RunScriptAsync";

    /// <summary>Builds the wrapper source for <paramref name="code"/>; <paramref name="scriptRoot"/> is the user code
    /// parsed as script, used to lift the script's own using directives and to find one trailing expression.
    /// <paramref name="returnStatementExpression"/> is set once the semantic model has shown the trailing statement
    /// expression is neither void nor ref-like; without it the expression stays a statement, which is what those need.</summary>
    public static string Build(string code, SyntaxNode scriptRoot, bool returnStatementExpression = false)
    {
        ArgumentNullException.ThrowIfNull(code);

        // A script may start with its own using directives, which C# only accepts at file scope. They are lifted into the
        // file header unchanged, and the body that follows is line-mapped from its real script line, so both the
        // directives and every line number after them stay exactly what the script wrote.
        var (usings, body, bodyLine) = SplitUsings(code, scriptRoot);
        var (expression, statementExpression) = TrailingExpression(scriptRoot);

        var builder = new StringBuilder(code.Length + 1536);
        foreach (var import in ScriptSurface.Imports)
        {
            builder.Append("using ").Append(import).Append(";\n");
        }

        builder.Append(usings);
        builder.Append("#nullable disable\n");
        builder.Append("internal sealed class ").Append(ScriptArtifact.DefaultEntryPointType)
            .Append(" : global::Forge.Scripting.ScriptGlobals\n{\n");

        // The script type is built from the globals instance the executor passes, so the inherited Tools/Log/Cancellation
        // members are exactly this run's.
        builder.Append("    private ").Append(ScriptArtifact.DefaultEntryPointType)
            .Append("(global::Forge.Scripting.ScriptGlobals globals)\n")
            .Append("        : base(globals.Tools, globals.Log, globals.Cancellation)\n    {\n    }\n\n");

        builder.Append("    internal static async global::System.Threading.Tasks.Task<object?> ")
            .Append(ScriptArtifact.DefaultEntryPointMethod)
            .Append("(global::Forge.Scripting.ScriptGlobals globals, global::System.Threading.CancellationToken cancellationToken)\n");
        builder.Append("    {\n");
        builder.Append("        _ = cancellationToken;\n");
        builder.Append("        var script = new ").Append(ScriptArtifact.DefaultEntryPointType).Append("(globals);\n");
        builder.Append("        return await script.RunScriptAsync().ConfigureAwait(false);\n    }\n\n");

        builder.Append("    private async global::System.Threading.Tasks.Task<object?> ").Append(ScriptMethodName)
            .Append("()\n    {\n");
        builder.Append("#line ").Append(bodyLine.ToString(CultureInfo.InvariantCulture))
            .Append(" \"").Append(SourceFile).Append("\"\n");

        if (expression is not null && (!statementExpression || returnStatementExpression))
        {
            var start = expression.SpanStart - (code.Length - body.Length);
            builder.Append(body, 0, start);
            builder.Append(ResultCast).Append(body, start, expression.Span.Length).Append(");");
            builder.Append(body, start + expression.Span.Length, body.Length - start - expression.Span.Length);
        }
        else
        {
            builder.Append(body);
        }

        builder.Append("\n#line default\n        return null;\n    }\n}\n");
        return builder.ToString();
    }

    /// <summary>Splits the leading using directives off the script and reports the 1-based script line the body starts
    /// on, so the body keeps its own line numbers.</summary>
    private static (string Usings, string Body, int BodyLine) SplitUsings(string code, SyntaxNode scriptRoot)
    {
        if (scriptRoot is not CompilationUnitSyntax unit || unit.Usings.Count == 0)
        {
            return (string.Empty, code, 1);
        }

        var end = unit.Usings[^1].FullSpan.End;
        if (end <= 0 || end >= code.Length)
        {
            return (string.Empty, code, 1);
        }

        var header = code[..end];
        var line = 1;
        foreach (var c in header)
        {
            if (c == '\n')
            {
                line++;
            }
        }

        return (header.Length > 0 && header[^1] == '\n' ? header : header + "\n", code[end..], line);
    }

    /// <summary>
    /// The script's last top-level expression, with whether it is also a legal statement expression: a binary
    /// expression, a member access, a literal or a conditional is a trailing value by itself, while a call, a <c>new</c>,
    /// an assignment, an increment or an <c>await</c> only becomes one when its type is not void.
    /// </summary>
    private static (ExpressionSyntax? Expression, bool IsStatementExpression) TrailingExpression(SyntaxNode scriptRoot)
    {
        if (scriptRoot is not CompilationUnitSyntax unit || unit.Members.Count == 0)
        {
            return (null, false);
        }

        if (unit.Members[^1] is not GlobalStatementSyntax { Statement: ExpressionStatementSyntax statement })
        {
            return (null, false);
        }

        var expression = statement.Expression;
        var isStatementExpression = expression
            is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
            or AssignmentExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
            or AwaitExpressionSyntax;
        return (expression, isStatementExpression);
    }

    /// <summary>True when the script ends in an expression statement, so its value is only returned once the semantic
    /// model has decided the expression is not void.</summary>
    public static bool HasTrailingStatementExpression(SyntaxNode scriptRoot) =>
        TrailingExpression(scriptRoot) is (not null, true);

    /// <summary>
    /// True when the trailing statement expression produces a value and must therefore be rewritten to
    /// <c>return (object?)(…)</c>. The type is read from the semantic model of the wrapper compiled with the expression
    /// still a statement, so no list of syntax types decides it and a void call stays a statement. A ref-like type stays
    /// a statement too: a ref struct cannot be boxed into the result, and the old script engine returned <c>null</c> for
    /// such a script as well.
    /// </summary>
    public static bool TrailingStatementReturnsValue(SemanticModel model, SyntaxNode wrapperRoot)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(wrapperRoot);
        if (TrailingStatement(wrapperRoot) is not { } statement)
        {
            return false;
        }

        var type = model.GetTypeInfo(statement.Expression).Type;
        return type is null || (type.SpecialType != SpecialType.System_Void && !type.IsRefLikeType);
    }

    /// <summary>The script's trailing expression statement as it appears in the generated method: the builder's own
    /// trailing <c>return null;</c> is the statement after it.</summary>
    private static ExpressionStatementSyntax? TrailingStatement(SyntaxNode wrapperRoot)
    {
        foreach (var method in wrapperRoot.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Identifier.ValueText != ScriptMethodName ||
                method.Body is not { } body ||
                body.Statements.Count < 2 ||
                body.Statements[^1] is not ReturnStatementSyntax)
            {
                continue;
            }

            return body.Statements[^2] as ExpressionStatementSyntax;
        }

        return null;
    }
}

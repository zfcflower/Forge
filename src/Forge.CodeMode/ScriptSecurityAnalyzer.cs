using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Forge.CodeMode;

internal readonly record struct ApiViolation(string Description, int Line, int Column);

/// <summary>
/// Semantic walk over a compiled script that rejects the members the reference whitelist cannot remove: the
/// core library itself carries <c>System.IO.File</c>, <c>System.Type</c> reflection and threading types.
/// </summary>
internal static class ScriptSecurityAnalyzer
{
    private static readonly string[] BannedNamespaces =
    [
        "System.IO",
        "System.Net",
        "System.Diagnostics",
        "System.Reflection",
        "System.Runtime.InteropServices",
        "System.Runtime.Loader",
        "System.Runtime.CompilerServices",
        "System.Runtime.Remoting",
        "System.Runtime.Serialization",
        "System.Resources",
        "System.Security",
        "System.Configuration",
        "System.Management",
        "System.CodeDom",
        "System.DirectoryServices",
        "System.Xml",
        "System.Data",
        "System.Timers",
        "System.Transactions",
        "System.Windows",
        "Microsoft",
    ];

    /// <summary>A null array bans the type outright; otherwise it lists the only members a script may touch.</summary>
    private static readonly Dictionary<string, string[]?> RestrictedTypes = new(StringComparer.Ordinal)
    {
        ["System.Environment"] = null,
        ["System.AppContext"] = null,
        ["System.AppDomain"] = null,
        ["System.Activator"] = null,
        ["System.GC"] = null,
        ["System.Console"] = null,
        ["System.IntPtr"] = null,
        ["System.UIntPtr"] = null,
        ["System.TypedReference"] = null,
        ["System.ArgIterator"] = null,
        ["System.RuntimeArgumentHandle"] = null,
        ["System.RuntimeFieldHandle"] = null,
        ["System.RuntimeMethodHandle"] = null,
        ["System.RuntimeTypeHandle"] = null,
        ["System.Type"] = ["Name", "FullName", "Namespace", "ToString", "Equals", "GetHashCode"],
        ["System.Reflection.MemberInfo"] = ["Name", "FullName", "Namespace", "ToString", "Equals", "GetHashCode"],
        ["System.Delegate"] = ["Invoke", "Equals", "GetHashCode", "ToString"],
        ["System.MulticastDelegate"] = ["Invoke", "Equals", "GetHashCode", "ToString"],
        ["System.Threading.Thread"] = null,
        ["System.Threading.ThreadPool"] = null,
        ["System.Threading.Timer"] = null,
        ["System.Threading.PeriodicTimer"] = null,
        ["System.Threading.WaitHandle"] = null,
        ["System.Threading.Mutex"] = null,
        ["System.Threading.Semaphore"] = null,
        ["System.Threading.EventWaitHandle"] = null,
        ["System.Threading.Tasks.TaskFactory"] = null,
        ["System.Threading.Tasks.TaskScheduler"] = null,
    };

    public static List<ApiViolation> Analyze(SemanticModel model, int maxViolations, CancellationToken ct) =>
        new Walker(model, maxViolations, ct).Run();

    private sealed class Walker(SemanticModel model, int maxViolations, CancellationToken ct)
    {
        private readonly List<ApiViolation> violations = new(4);
        private readonly List<(string Api, int Index)> reportedApis = new(4);
        private readonly HashSet<string> messages = new(StringComparer.Ordinal);

        public List<ApiViolation> Run()
        {
            var root = model.SyntaxTree.GetRoot(ct);
            foreach (var token in root.DescendantTokens())
            {
                ct.ThrowIfCancellationRequested();
                if (token.IsKind(SyntaxKind.UnsafeKeyword))
                {
                    ReportMessage(token.GetLocation(), "unsafe code is not allowed");
                    break;
                }
            }

            foreach (var node in root.DescendantNodesAndSelf())
            {
                ct.ThrowIfCancellationRequested();
                if (violations.Count >= maxViolations)
                {
                    break;
                }

                if (RejectBySyntax(node) is { } text)
                {
                    ReportMessage(node.GetLocation(), text);
                    continue;
                }

                if (!BindsSymbol(node))
                {
                    continue;
                }

                var symbol = model.GetSymbolInfo(node, ct).Symbol;
                if (symbol is null)
                {
                    if (node is ExpressionSyntax)
                    {
                        Inspect(model.GetTypeInfo(node, ct).Type, node.GetLocation());
                    }

                    continue;
                }

                Inspect(symbol, node.GetLocation());
            }

            return violations;
        }

        private void Inspect(ISymbol? symbol, Location location)
        {
            switch (symbol)
            {
                case null:
                    return;
                case IAliasSymbol alias:
                    Inspect(alias.Target, location);
                    return;
                case INamespaceSymbol ns:
                    if (IsBannedNamespace(ns.ToDisplayString()))
                    {
                        Report(location, ns.ToDisplayString());
                    }

                    return;
                case ITypeSymbol type:
                    InspectType(type, location);
                    return;
                case IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction }:
                    return;
                case IMethodSymbol method:
                    InspectMember(method, method.Name, location);
                    return;
                case IPropertySymbol property:
                    InspectMember(property, property.Name, location);
                    return;
                case IFieldSymbol field:
                    InspectMember(field, field.Name, location);
                    return;
                case IEventSymbol @event:
                    InspectMember(@event, @event.Name, location);
                    return;
            }
        }

        private void InspectType(ITypeSymbol type, Location location)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    InspectType(array.ElementType, location);
                    return;
                case IPointerTypeSymbol or IFunctionPointerTypeSymbol:
                    Report(location, type.ToDisplayString());
                    return;
                case INamedTypeSymbol named when named.TypeKind != TypeKind.Error:
                    var full = FullName(named);
                    if (RestrictedRule(named, memberName: null) is { } restriction)
                    {
                        if (restriction)
                        {
                            Report(location, full);
                        }

                        return;
                    }

                    if (IsBannedNamespace(NamespaceOf(named)))
                    {
                        Report(location, full);
                        return;
                    }

                    foreach (var argument in named.TypeArguments)
                    {
                        InspectType(argument, location);
                    }

                    return;
            }
        }

        private void InspectMember(ISymbol member, string name, Location location)
        {
            if (member is IMethodSymbol { MethodKind: MethodKind.Constructor } && member.ContainingType is { } constructed)
            {
                InspectType(constructed, location);
            }

            if (member.ContainingType is not { } owner)
            {
                return;
            }

            var ownerFull = FullName(owner);
            var display = member is IMethodSymbol { MethodKind: MethodKind.Constructor } ? ownerFull : ownerFull + "." + name;
            if (RestrictedRule(owner, name) is { } restriction)
            {
                if (restriction)
                {
                    Report(location, display);
                }

                return;
            }

            if (IsBannedNamespace(NamespaceOf(owner)))
            {
                Report(location, display);
            }
        }

        /// <summary>True when the restricted type bans this use, false when it is allowed, null when no rule applies.</summary>
        private static bool? RestrictedRule(INamedTypeSymbol type, string? memberName)
        {
            for (var current = type; current is not null; current = current.ContainingType)
            {
                if (!RestrictedTypes.TryGetValue(FullName(current), out var allowed))
                {
                    continue;
                }

                if (allowed is null)
                {
                    return true;
                }

                // An allowed-list entry still lets a script name the type; only members outside the list are banned.
                return memberName is not null &&
                       !Array.Exists(allowed, candidate => string.Equals(candidate, memberName, StringComparison.Ordinal));
            }

            return null;
        }

        private void Report(Location location, string api)
        {
            for (var i = 0; i < reportedApis.Count; i++)
            {
                var (existing, index) = reportedApis[i];
                if (existing.Equals(api, StringComparison.Ordinal) ||
                    existing.StartsWith(api + ".", StringComparison.Ordinal))
                {
                    return;
                }

                if (api.StartsWith(existing + ".", StringComparison.Ordinal))
                {
                    reportedApis[i] = (api, index);
                    var previous = violations[index];
                    violations[index] = previous with { Description = "banned API: " + api };
                    return;
                }
            }

            var span = location.GetLineSpan().StartLinePosition;
            reportedApis.Add((api, violations.Count));
            violations.Add(new ApiViolation("banned API: " + api, span.Line + 1, span.Character + 1));
        }

        private void ReportMessage(Location location, string text)
        {
            if (!messages.Add(text))
            {
                return;
            }

            var span = location.GetLineSpan().StartLinePosition;
            violations.Add(new ApiViolation(text, span.Line + 1, span.Character + 1));
        }
    }

    private static string? RejectBySyntax(SyntaxNode node) => node switch
    {
        TypeOfExpressionSyntax => "typeof is not allowed (reflection)",
        UnsafeStatementSyntax or PointerTypeSyntax or FunctionPointerTypeSyntax or FixedStatementSyntax
            or StackAllocArrayCreationExpressionSyntax => "unsafe code is not allowed",
        ReferenceDirectiveTriviaSyntax => "#r directives are not allowed",
        LoadDirectiveTriviaSyntax => "#load directives are not allowed",
        _ => null,
    };

    private static bool BindsSymbol(SyntaxNode node) => node switch
    {
        IdentifierNameSyntax or GenericNameSyntax or QualifiedNameSyntax or AliasQualifiedNameSyntax
            or MemberAccessExpressionSyntax or MemberBindingExpressionSyntax or InvocationExpressionSyntax
            or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
            or ElementAccessExpressionSyntax or UsingDirectiveSyntax or AttributeSyntax => true,
        _ => false,
    };

    private static string NamespaceOf(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : string.Empty;

    private static bool IsBannedNamespace(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var banned in BannedNamespaces)
        {
            if (name.Equals(banned, StringComparison.Ordinal))
            {
                return true;
            }

            if (name.Length > banned.Length && name[banned.Length] == '.' &&
                name.StartsWith(banned, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string FullName(INamedTypeSymbol type)
    {
        var name = type.Name;
        for (var current = type.ContainingType; current is not null; current = current.ContainingType)
        {
            name = current.Name + "." + name;
        }

        var ns = NamespaceOf(type);
        return ns.Length == 0 ? name : ns + "." + name;
    }
}

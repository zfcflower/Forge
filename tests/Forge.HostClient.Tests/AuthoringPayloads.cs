using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>Wire answers of the authoring surface shared by the payload, response and resilience suites: one
/// complete <c>extensions.stage</c> answer and one complete <c>extensions.commit</c> answer for plugin "demo",
/// so a rejection case can change exactly the field it is about (with a <c>with</c> expression).</summary>
internal static class AuthoringPayloads
{
    /// <summary>A complete, valid <c>extensions.stage</c> answer.</summary>
    internal static Sdk.StagedExtension ValidStaged { get; } = new(
        "demo",
        "1.0.0",
        Sdk.ExtensionKinds.Plugin,
        [new Sdk.StagedFile("Plugin.cs", "// code")],
        [new Sdk.Permission(Sdk.PermissionKind.ReadFile)],
        [new Sdk.ExtensionDependency("Dep", "1.0.0")],
        "s",
        "d",
        "sdk-1",
        []);

    /// <summary>The <c>status</c> of <see cref="ValidCommit"/>.</summary>
    internal static Sdk.PluginStatusInfo ValidCommitStatus { get; } =
        new("demo", "1.0.0", "approved", "s", "d", "sdk-1", [], null);

    /// <summary>A complete, valid <c>extensions.commit</c> answer.</summary>
    internal static Sdk.ExtensionCommitResult ValidCommit { get; } = new(ValidCommitStatus, ["demo-tool"]);
}

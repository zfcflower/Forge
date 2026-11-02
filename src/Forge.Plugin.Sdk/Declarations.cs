using System.Text.Json;

namespace Forge.Plugin.Sdk;

/// <summary>A slash command contributed by a plugin; the body of the command runs in the Worker.</summary>
public sealed record CommandDeclaration(string Name, string Description, string ArgumentHint = "");

/// <summary>A Markdown skill workflow contributed by a plugin (frontmatter + step text an agent follows).</summary>
public sealed record WorkflowDeclaration(string Name, string Description, string Body);

/// <summary>A declarative UI panel; the main program renders <see cref="Schema"/> as Avalonia controls.
/// No plugin may return an in-process control.</summary>
public sealed record PanelDeclaration(string Id, string Title, UiSchema Schema);

/// <summary>Declarative UI schema. <see cref="Kind"/> is one of: form, table, markdown, text, button, status.</summary>
public sealed record UiSchema(string Kind, JsonElement Definition);

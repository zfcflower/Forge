using MessagePack;

namespace Forge.Plugin.Sdk;

/// <summary>A slash command contributed by a plugin; the body of the command runs in the Worker.</summary>
[MessagePackObject]
public sealed partial record CommandDeclaration([property: Key(0)] string Name, [property: Key(1)] string Description, [property: Key(2)] string ArgumentHint = "");

/// <summary>A Markdown skill workflow contributed by a plugin (frontmatter + step text an agent follows).</summary>
[MessagePackObject]
public sealed partial record WorkflowDeclaration([property: Key(0)] string Name, [property: Key(1)] string Description, [property: Key(2)] string Body);

/// <summary>A declarative UI panel; the main program renders <see cref="Schema"/> as Avalonia controls.
/// No plugin may return an in-process control.</summary>
[MessagePackObject]
public sealed partial record PanelDeclaration([property: Key(0)] string Id, [property: Key(1)] string Title, [property: Key(2)] UiSchema Schema);

/// <summary>Declarative UI schema. <see cref="Kind"/> is one of: form, table, markdown, text, button, status;
/// <see cref="DefinitionJson"/> is the kind's JSON definition object, parsed only by the renderer.</summary>
[MessagePackObject]
public sealed partial record UiSchema([property: Key(0)] string Kind, [property: Key(1)] string DefinitionJson);

namespace Forge.HostClient.Tests;

/// <summary>Wire answers of the authoring surface shared by the payload, response and resilience suites: one
/// complete <c>extensions.stage</c> answer and one complete <c>extensions.commit</c> answer for plugin "demo",
/// so a rejection case can mutate exactly the field it is about.</summary>
internal static class AuthoringPayloads
{
    /// <summary>A complete, valid <c>extensions.stage</c> answer.</summary>
    internal const string ValidStagedJson =
        "{\"pluginId\":\"demo\",\"version\":\"1.0.0\",\"kind\":\"plugin\"," +
        "\"files\":[{\"path\":\"Plugin.cs\",\"text\":\"// code\"}]," +
        "\"permissions\":[{\"kind\":0}]," +
        "\"dependencies\":[{\"name\":\"Dep\",\"version\":\"1.0.0\"}]," +
        "\"sourceHash\":\"s\",\"dependencyHash\":\"d\",\"sdkVersion\":\"sdk-1\"," +
        "\"diagnostics\":[]}";

    /// <summary>A complete, valid <c>extensions.commit</c> answer.</summary>
    internal const string ValidCommitJson =
        "{\"status\":{\"pluginId\":\"demo\",\"version\":\"1.0.0\",\"status\":\"approved\"," +
        "\"sourceHash\":\"s\",\"dependencyHash\":\"d\",\"sdkVersion\":\"sdk-1\"," +
        "\"permissions\":[],\"error\":null},\"toolNames\":[\"demo-tool\"]}";
}

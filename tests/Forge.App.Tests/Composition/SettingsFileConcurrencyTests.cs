using System.Text.Json.Nodes;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Network;
using Forge.App.Providers;
using Forge.App.Web;
using Xunit;

namespace Forge.App.Tests.Composition;

/// <summary>
/// The gate every <c>settings.json</c> writer takes (<see cref="Composition.SettingsFileMutex"/>, App SPEC 35). Each
/// writer keeps the sections it does not own by re-reading the file before it writes, so two writers that overlap would
/// merge into the same snapshot and the later one would silently drop the earlier one's section — an atomic replace
/// makes the file valid, not the merge correct. These are five of the store-level writers a running shell really
/// interleaves (the compaction block, the network section, the usage log, the web section, the agent defaults), started
/// together round after round; every round each one writes a value that names the round, so a dropped section and a
/// dropped update both show up in the file that survives.
/// </summary>
public sealed class SettingsFileConcurrencyTests : IDisposable
{
    private const int Rounds = 30;

    private readonly string root = TestTempFiles.NewDirectory("forge-settings-concurrency-");

    [Fact]
    public async Task Writers_that_interleave_over_one_settings_file_lose_no_section_and_no_update()
    {
        var path = Path.Combine(root, "settings.json");
        // A section no writer in this build owns: every merge has to carry it through, round after round.
        await File.WriteAllTextAsync(path, """{"handWritten":{"keep":true}}""");

        for (var round = 0; round < Rounds; round++)
        {
            await WriteFromEveryWriterAtOnceAsync(path, round);
        }

        var last = Rounds - 1;
        var sections = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();

        Assert.True(sections["handWritten"]!["keep"]!.GetValue<bool>());

        var compaction = sections["compaction"]!.AsObject();
        Assert.True(compaction["enabled"]!.GetValue<bool>());
        Assert.Equal(50 + last, compaction["triggerPercent"]!.GetValue<int>());
        Assert.Equal(1 + last, compaction["targetPercent"]!.GetValue<int>());

        var network = sections["network"]!.AsObject();
        Assert.Equal("servers", network["dns"]!.GetValue<string>());
        Assert.Equal([$"10.0.0.{last}"], [.. network["dnsServers"]!.AsArray().Select(static server => server!.GetValue<string>())]);

        // The switch alternates every round, so the surviving value names the round that wrote last.
        Assert.Equal(last % 2 == 0, sections["usageLog"]!["enabled"]!.GetValue<bool>());

        var web = sections["web"]!.AsObject();
        Assert.Equal($"https://searxng.test/{last}", web["searxngUrl"]!.GetValue<string>());
        Assert.True(web["allowPrivateNetwork"]!.GetValue<bool>());

        Assert.Equal($"model-{last}", sections["agents"]!["root"]!["model"]!.GetValue<string>());
    }

    public void Dispose() => TestTempFiles.DeleteTree(root);

    /// <summary>One round: the five writers are released together, each with its own fresh read of the file.</summary>
    private static async Task WriteFromEveryWriterAtOnceAsync(string path, int round)
    {
        using var start = new Barrier(5);
        await Task.WhenAll(
            Task.Run(() =>
            {
                start.SignalAndWait();
                new CompactionSettingsStore(path).Save(new CompactionSettings(true, 50 + round, 1 + round));
            }),
            Task.Run(() =>
            {
                start.SignalAndWait();
                new NetworkSettingsStore(path).Save(
                    new NetworkSettings(DnsMode.Servers, [$"10.0.0.{round}"], null, ProxyMode.System, null, []));
            }),
            Task.Run(() =>
            {
                start.SignalAndWait();
                new UsageLogSettingsStore(path).Save(new UsageLogSettings(round % 2 == 0));
            }),
            Task.Run(() =>
            {
                start.SignalAndWait();
                new WebSettingsStore(path).Save(new WebSettings(WebSettings.Searxng, $"https://searxng.test/{round}", true));
            }),
            Task.Run(() =>
            {
                start.SignalAndWait();
                new AgentDefaultsStore(path).Save("root", new AgentDefault(null, $"model-{round}", ThinkingEffort.Off));
            }));
    }
}

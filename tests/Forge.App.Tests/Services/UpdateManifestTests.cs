using System.Diagnostics;
using System.Text;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// Pins <see cref="UpdateManifestParser"/> (wave W10b): the frozen schema 1 shape is read defensively, so a bad manifest
/// fails with a readable reason instead of a half-populated channel. The last two tests run the release job's own generator
/// (<c>scripts/make-update-manifest.ps1</c>) and feed its output back through the parser: the CI artifact and the app must agree
/// on the shape, and a release must leave every channel answered.
/// </summary>
public sealed class UpdateManifestTests
{
    private const string Rid = "win-x64";

    private static bool TryParse(string json, out UpdateManifest? manifest, out string? error) =>
        UpdateManifestParser.TryParse(Encoding.UTF8.GetBytes(json), out manifest, out error);

    private static string GoodManifest() =>
        UpdateTestManifest.Stable("1.2.0", Rid, "https://example.test/Forge.zip", new byte[42]);

    [Fact]
    public void A_valid_manifest_parses_into_its_channels_and_packages()
    {
        Assert.True(TryParse(GoodManifest(), out var manifest, out var error), error);

        var stable = manifest!.Channel("stable");
        Assert.NotNull(stable);
        Assert.Equal("1.2.0", stable.Version);
        Assert.Equal("2026-11-15T00:00:00Z", stable.ReleasedAt);
        Assert.Equal("# Notes", stable.Notes);

        var package = stable.Package(Rid);
        Assert.NotNull(package);
        Assert.Equal("https://example.test/Forge.zip", package.Url);
        Assert.Equal(UpdateTestManifest.HexSha256(new byte[42]), package.Sha256);
        Assert.Equal(42, package.SizeBytes);
    }

    [Fact]
    public void A_missing_size_is_zero()
    {
        const string json =
            """{"schema":1,"channels":{"stable":{"version":"1.2.0","packages":{"win-x64":{"url":"https://example.test/Forge.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}}}}""";

        Assert.True(TryParse(json, out var manifest, out _));
        Assert.Equal(0, manifest!.Channel("stable")!.Package(Rid)!.SizeBytes);
    }

    [Fact]
    public void A_sha256_is_accepted_in_either_case_and_kept_verbatim()
    {
        // The parser is only the shape gate (64 hex characters); the comparison against the download happens in the
        // service and ignores case, so a CI that uppercases Get-FileHash must not be rejected here.
        var upper = UpdateTestManifest.HexSha256(new byte[42]).ToUpperInvariant();
        var json = UpdateTestManifest.StableWithHash("1.2.0", Rid, "https://example.test/Forge.zip", upper, 42);

        Assert.True(TryParse(json, out var manifest, out var error), error);
        Assert.Equal(upper, manifest!.Channel("stable")!.Package(Rid)!.Sha256);
    }

    [Theory]
    [InlineData("""{"schema":1,"channels":{"stable":{"version":"1.2.0","notes":"","packages":{"win-x64":{"url":"https://example.test/Forge.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}}}}""")]
    [InlineData("""{"schema":1,"channels":{"stable":{"version":"1.2.0","packages":{"win-x64":{"url":"https://example.test/Forge.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}}}}""")]
    public void An_empty_or_absent_notes_field_reads_as_empty_not_null(string json)
    {
        // The settings section renders the notes as Markdown and shows "no notes" when HasNotes is false; a null would
        // reach the renderer instead.
        Assert.True(TryParse(json, out var manifest, out var error), error);
        Assert.Equal(string.Empty, manifest!.Channel("stable")!.Notes);
    }

    [Fact]
    public void Long_notes_are_carried_verbatim()
    {
        var notes = new string('n', 300_000);
        var json = UpdateTestManifest.Stable("1.2.0", Rid, "https://example.test/Forge.zip", new byte[4], notes);

        Assert.True(TryParse(json, out var manifest, out var error), error);
        Assert.Equal(notes.Length, manifest!.Channel("stable")!.Notes.Length);
        Assert.Equal(notes, manifest.Channel("stable")!.Notes);
    }

    [Fact]
    public void A_manifest_larger_than_the_cap_is_rejected()
    {
        // A hostile or broken server must not become an in-memory tree: the cap is checked before parsing.
        var payload = new byte[UpdateManifestParser.MaxBytes + 1];
        payload[0] = (byte)'{';

        Assert.False(UpdateManifestParser.TryParse(payload, out var manifest, out var error));
        Assert.Null(manifest);
        Assert.Contains("larger than", error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"schema":2,"channels":{}}""")]
    [InlineData("""{"schema":1.0,"channels":{}}""")]
    [InlineData("""{"schema":"1","channels":{}}""")]
    [InlineData("""{"schema":1e0,"channels":{}}""")]
    public void Only_the_literal_schema_number_one_is_accepted(string json)
    {
        // CI (scripts/make-update-manifest.ps1) and the app must agree on the same frozen schema 1; a future manifest or
        // a JSON spelling that round-trips to a float/string is a failure, not a best-effort read.
        Assert.False(TryParse(json, out var manifest, out var error));
        Assert.Null(manifest);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"schema":1}""")]
    [InlineData("""{"schema":1,"channels":[]}""")]
    [InlineData("""{"schema":1,"channels":{"stable":{"version":"1.2.0"}}}""")]
    [InlineData("""{"schema":1,"channels":{"stable":{"version":"1.2.0","packages":{"win-x64":{"url":"ftp://example.test/Forge.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}}}}""")]
    [InlineData("""{"schema":1,"channels":{"stable":{"version":"1.2.0","packages":{"win-x64":{"url":"https://example.test/Forge.zip","sha256":"zzzz"}}}}}""")]
    [InlineData("""{"schema":1,"channels":{"stable":{"version":"1.2.0","packages":{"win-x64":{"url":"https://example.test/Forge.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sizeBytes":-1}}}}}""")]
    public void A_bad_manifest_is_rejected_with_a_reason(string json)
    {
        Assert.False(TryParse(json, out var manifest, out var error));
        Assert.Null(manifest);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public async Task The_release_script_builds_a_manifest_this_parser_reads_with_every_channel_answered()
    {
        // CI generates updates.json with scripts/make-update-manifest.ps1 and every install reads it. The beta release merges the
        // previous manifest so a stable user's URL still answers their channel; without it the manifest holds one key and the
        // other channel's users get nothing (the app now reports that as "no update", which would hide the regression).
        var root = TestTempFiles.NewDirectory("forge-update-manifest-script-");
        try
        {
            var stableDirectory = Directory.CreateDirectory(Path.Combine(root, "stable")).FullName;
            var betaDirectory = Directory.CreateDirectory(Path.Combine(root, "beta")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(stableDirectory, "Forge-1.2.0-win-x64.zip"), "stable windows"u8.ToArray());
            await File.WriteAllBytesAsync(Path.Combine(stableDirectory, "Forge-1.2.0-linux-x64.tar.gz"), "stable linux"u8.ToArray());
            await File.WriteAllBytesAsync(Path.Combine(betaDirectory, "Forge-1.3.0-beta.1-win-x64.zip"), "beta windows"u8.ToArray());
            var stableManifest = Path.Combine(root, "stable-updates.json");
            var betaManifest = Path.Combine(root, "beta-updates.json");

            var stableRun = await RunGeneratorAsync(
                root,
                [
                    "-ArtifactsDir", stableDirectory,
                    "-Version", "1.2.0",
                    "-Channel", "stable",
                    "-BaseUrl", "https://example.test/download/v1.2.0",
                    "-Notes", "# 1.2.0",
                    "-Output", stableManifest,
                ]);
            Assert.True(stableRun.ExitCode == 0, stableRun.Output);

            Assert.True(TryParse(await File.ReadAllTextAsync(stableManifest), out var stable, out var stableError), stableError);
            Assert.Equal("1.2.0", stable!.Channel("stable")!.Version);
            Assert.Equal("https://example.test/download/v1.2.0/Forge-1.2.0-win-x64.zip", stable.Channel("stable")!.Package(Rid)!.Url);
            Assert.Equal(
                UpdateTestManifest.HexSha256("stable windows"u8.ToArray()),
                stable.Channel("stable")!.Package(Rid)!.Sha256);
            Assert.Equal(14, stable.Channel("stable")!.Package(Rid)!.SizeBytes);

            var betaRun = await RunGeneratorAsync(
                root,
                [
                    "-ArtifactsDir", betaDirectory,
                    "-Version", "1.3.0-beta.1",
                    "-Channel", "beta",
                    "-BaseUrl", "https://example.test/download/v1.3.0-beta.1",
                    "-MergeFrom", stableManifest,
                    "-Output", betaManifest,
                ]);
            Assert.True(betaRun.ExitCode == 0, betaRun.Output);

            Assert.True(TryParse(await File.ReadAllTextAsync(betaManifest), out var merged, out var mergedError), mergedError);
            Assert.Equal("1.3.0-beta.1", merged!.Channel("beta")!.Version);
            Assert.Equal("1.2.0", merged.Channel("stable")!.Version);
            Assert.NotNull(merged.Channel("stable")!.Package("linux-x64"));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task The_release_script_refuses_two_packages_for_the_same_rid()
    {
        // A RID is one entry in the manifest; two packages mapping to it (a .zip and a .tar.gz published together) would
        // silently overwrite each other and hand one platform the other's payload.
        var root = TestTempFiles.NewDirectory("forge-update-manifest-script-");
        try
        {
            var artifacts = Directory.CreateDirectory(Path.Combine(root, "dist")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(artifacts, "Forge-2.0.0-win-x64.zip"), "zip"u8.ToArray());
            await File.WriteAllBytesAsync(Path.Combine(artifacts, "Forge-2.0.0-win-x64.tar.gz"), "tar"u8.ToArray());

            var run = await RunGeneratorAsync(
                root,
                [
                    "-ArtifactsDir", artifacts,
                    "-Version", "2.0.0",
                    "-Channel", "stable",
                    "-Output", Path.Combine(root, "updates.json"),
                ]);

            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("two packages map to RID", run.Output, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(root, "updates.json")));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    /// <summary>
    /// Runs the release generator with the same interpreter the release job uses (<c>pwsh</c>; Windows PowerShell is the fallback
    /// on a machine without the store build). A missing interpreter is a real failure, not a skip: the script is the artifact
    /// every install depends on.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunGeneratorAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var root = RepoRoot.Find()
            ?? throw new InvalidOperationException("no checkout root found for the test binaries.");
        var script = Path.Combine(root, "scripts", "make-update-manifest.ps1");
        Assert.True(File.Exists(script), "the release generator is missing: " + script);

        var start = new ProcessStartInfo(PowerShellPath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script })
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("PowerShell could not be started: " + PowerShellPath);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("make-update-manifest.ps1 did not finish within 120 s.");
        }

        return (process.ExitCode, await output.ConfigureAwait(false) + await errors.ConfigureAwait(false));
    }

    private static string PowerShellPath
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return "pwsh";
            }

            string[] candidates =
            [
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "PowerShell",
                    "7",
                    "pwsh.exe"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe"),
            ];
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return "pwsh";
        }
    }
}

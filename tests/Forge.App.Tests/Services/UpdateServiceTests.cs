using System.Text;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.Tests.Approval;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// Pins <see cref="UpdateService"/>'s stage-A flow (wave W10b) against a scripted HTTP handler and an in-memory UI-state
/// store: manifest fetch and channel choice, version gating, download + SHA-256 + staging, staging cleanup, cancellation,
/// one-check-at-a-time, and the automatic pump's due-ness, first tick and survival.
/// </summary>
public sealed class UpdateServiceTests
{
    private const string Rid = "win-x64";

    [Fact]
    public async Task An_equal_or_older_offered_version_is_up_to_date_and_does_not_download()
    {
        using var sandbox = UpdateTestService.Build(currentVersion: "1.2.0");
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.1.0", Rid, out _);

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpToDate, outcome.Status);
        Assert.Null(outcome.Offer);
        var request = Assert.Single(sandbox.Handler.Requests);
        Assert.Equal(UpdateTestManifest.ManifestUrl, request);
        Assert.Equal(1, sandbox.UiState.Puts);
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0+build.7")]
    [InlineData("1.2.0", "1.2.0-beta.1")]
    [InlineData("2.0.0", "1.9.9")]
    public async Task An_offered_version_with_the_same_numbers_or_older_is_not_an_update(string current, string offered)
    {
        // A CI build stamped "1.2.0+<sha>" must not be told to install the release it was built from, and a channel that
        // still points at an older release must not regress the install.
        using var sandbox = UpdateTestService.Build(currentVersion: current);
        UpdateTestService.SetStableOffer(sandbox.Handler, offered, Rid, out var packageUrl);

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpToDate, outcome.Status);
        Assert.Null(outcome.Offer);
        Assert.Equal([UpdateTestManifest.ManifestUrl], sandbox.Handler.Requests);
        Assert.DoesNotContain(packageUrl, sandbox.Handler.Requests);
    }

    [Fact]
    public async Task A_newer_version_is_downloaded_verified_and_staged()
    {
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        var payload = UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", "win-x64", out _);

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, outcome.Status);
        Assert.NotNull(outcome.Offer);
        Assert.Equal("1.2.0", outcome.Offer!.Version);
        Assert.Equal("# Notes", outcome.Offer.Notes);
        Assert.True(File.Exists(outcome.Offer.PackagePath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(outcome.Offer.PackagePath));
        Assert.Equal(outcome.Offer.PackagePath, sandbox.Service.StagedPackagePath);
        Assert.Equal(1, sandbox.UiState.Puts);
        Assert.StartsWith(sandbox.Root, outcome.Offer.PackagePath);
    }

    [Fact]
    public async Task A_bad_manifest_is_a_readable_failure()
    {
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl, "not json");

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Failed, outcome.Status);
        Assert.False(outcome.Message.Resolve().StartsWith('[', StringComparison.Ordinal));
        var request = Assert.Single(sandbox.Handler.Requests);
        Assert.Equal(UpdateTestManifest.ManifestUrl, request);
    }

    [Fact]
    public async Task A_hash_mismatch_is_a_failure_and_leaves_no_staged_file()
    {
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        const string rid = "win-x64";
        var payload = Encoding.UTF8.GetBytes("payload that does not match");
        var packageUrl = "https://example.test/package/Forge-1.2.0-win-x64.zip";
        const string wrongHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl,
            UpdateTestManifest.StableWithHash("1.2.0", rid, packageUrl, wrongHash, payload.Length));
        sandbox.Handler.Package(packageUrl, payload);

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Failed, outcome.Status);
        Assert.Null(sandbox.Service.StagedPackagePath);
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_network_failure_is_a_readable_failure()
    {
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", "win-x64", out _);
        sandbox.Handler.ThrowNetworkOnPackage = true;

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Failed, outcome.Status);
        Assert.False(outcome.Message.Resolve().StartsWith('[', StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_download_can_be_cancelled_from_progress()
    {
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        var payload = new byte[256 * 1024];
        new Random(42).NextBytes(payload);
        var packageUrl = "https://example.test/package/Forge-1.2.0-win-x64.zip";
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl, UpdateTestManifest.Stable("1.2.0", "win-x64", packageUrl, payload));
        sandbox.Handler.Package(packageUrl, payload);

        using var cts = new CancellationTokenSource();
        void Progress(UpdateDownloadProgress progress)
        {
            if (progress.BytesRead > 0)
            {
                cts.Cancel();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sandbox.Service.CheckForUpdateAsync(manual: true, cts.Token, Progress));

        Assert.Null(sandbox.Service.StagedPackagePath);
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("stable", "stable")]
    [InlineData("beta", "beta")]
    public async Task The_channel_selection_downloads_only_the_chosen_channel(string channel, string expected)
    {
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0", channel: channel);
        var stablePayload = Encoding.UTF8.GetBytes("stable payload");
        var betaPayload = Encoding.UTF8.GetBytes("beta payload");
        var stableUrl = "https://example.test/package/Forge-1.2.0-win-x64.zip";
        var betaUrl = "https://example.test/package/Forge-1.3.0-win-x64.zip";
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl,
            UpdateTestManifest.TwoChannels("1.2.0", "1.3.0", "win-x64", stableUrl, betaUrl, stablePayload, betaPayload));
        sandbox.Handler.Package(stableUrl, stablePayload);
        sandbox.Handler.Package(betaUrl, betaPayload);

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, outcome.Status);
        var expectedUrl = expected == "stable" ? stableUrl : betaUrl;
        Assert.Contains(expectedUrl, sandbox.Handler.Requests);
        Assert.DoesNotContain(expected == "stable" ? betaUrl : stableUrl, sandbox.Handler.Requests);
        Assert.Equal(Encoding.UTF8.GetBytes(expected + " payload"), await File.ReadAllBytesAsync(outcome.Offer!.PackagePath));
    }

    [Fact]
    public async Task Staging_a_newer_version_cleans_the_previous_staged_version()
    {
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", "win-x64", out _);
        await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);
        Assert.True(Directory.Exists(Path.Combine(sandbox.Root, "1.2.0")));

        UpdateTestService.SetStableOffer(sandbox.Handler, "1.3.0", "win-x64", out _);
        await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(sandbox.Root, "1.2.0")));
        Assert.True(Directory.Exists(Path.Combine(sandbox.Root, "1.3.0")));
    }

    [Fact]
    public async Task A_manifest_without_the_current_rid_is_a_readable_failure()
    {
        // CI publishes one package per RID; a release that forgot the user's platform must say so instead of downloading
        // somebody else's binary or failing deep inside the download.
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0", rid: Rid);
        const string json =
            """{"schema":1,"channels":{"stable":{"version":"1.2.0","packages":{"linux-x64":{"url":"https://example.test/Forge-linux-x64.tar.gz","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"osx-arm64":{"url":"https://example.test/Forge-osx-arm64.tar.gz","sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}}}}""";
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl, json);

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Failed, outcome.Status);
        Assert.Equal(UpdatesStrings.ErrorNoPackage("1.2.0", Rid), outcome.Message);
        Assert.Null(sandbox.Service.StagedPackagePath);
        Assert.Equal([UpdateTestManifest.ManifestUrl], sandbox.Handler.Requests);
    }

    [Fact]
    public async Task A_manifest_without_the_configured_channel_is_up_to_date_instead_of_failing()
    {
        // The manifest may publish beta only; a stable-configured install must not silently fall back to another channel, and
        // it must not be told the check broke either: "my channel has nothing newer" is a normal answer, with its own wording
        // (DOCUMENTED DEFECT: the section used to show a failure for a manifest that simply had no stable channel yet).
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0", channel: "stable");
        var betaUrl = "https://example.test/package/Forge-1.3.0-win-x64.zip";
        var betaPayload = Encoding.UTF8.GetBytes("beta payload");
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl,
            UpdateTestManifest.SingleChannel("beta", "1.3.0", Rid, betaUrl, betaPayload));
        sandbox.Handler.Package(betaUrl, betaPayload);

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpToDate, outcome.Status);
        Assert.Null(outcome.Offer);
        Assert.Equal(UpdatesStrings.ResultChannelMissing("stable"), outcome.Message);
        Assert.DoesNotContain(betaUrl, sandbox.Handler.Requests);
    }

    [Fact]
    public async Task A_newer_prerelease_is_staged_under_its_full_version_token()
    {
        // The staged directory is the version token the offer names; a prerelease suffix must survive into the path the
        // user is asked to open.
        using var sandbox = UpdateTestService.Build(currentVersion: "1.2.0");
        var payload = UpdateTestService.SetStableOffer(sandbox.Handler, "1.3.0-beta.1", Rid, out _);

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, outcome.Status);
        Assert.Equal("1.3.0-beta.1", outcome.Offer!.Version);
        Assert.Equal(Path.Combine(sandbox.Root, "1.3.0-beta.1"), Path.GetDirectoryName(outcome.Offer.PackagePath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(outcome.Offer.PackagePath));
    }

    [Fact]
    public async Task A_redirected_package_is_verified_against_the_bytes_the_transport_delivered()
    {
        // Release assets are served through a redirect (GitHub -> objects.githubusercontent.com). HttpClient follows it,
        // so the hash must be taken over the final body, not the first response's (empty) one.
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        var payload = Encoding.UTF8.GetBytes("redirected payload");
        var declaredUrl = "https://example.test/releases/download/v1.2.0/Forge-1.2.0-win-x64.zip";
        var cdnUrl = "https://objects.example.test/signed/Forge-1.2.0-win-x64.zip";
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl,
            UpdateTestManifest.Stable("1.2.0", Rid, declaredUrl, payload));
        sandbox.Handler.Redirect(declaredUrl, cdnUrl);
        sandbox.Handler.Package(cdnUrl, payload);

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, outcome.Status);
        Assert.Equal(payload, await File.ReadAllBytesAsync(outcome.Offer!.PackagePath));
        Assert.Contains(cdnUrl, sandbox.Handler.Requests);
    }

    [Fact]
    public async Task A_redirect_that_lands_on_a_different_payload_fails_verification()
    {
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        var declaredPayload = Encoding.UTF8.GetBytes("what the manifest signed");
        var deliveredPayload = (byte[])declaredPayload.Clone();
        deliveredPayload[0] ^= 0xFF;
        var declaredUrl = "https://example.test/releases/download/v1.2.0/Forge-1.2.0-win-x64.zip";
        var cdnUrl = "https://objects.example.test/signed/Forge-1.2.0-win-x64.zip";
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl,
            UpdateTestManifest.Stable("1.2.0", Rid, declaredUrl, declaredPayload));
        sandbox.Handler.Redirect(declaredUrl, cdnUrl);
        sandbox.Handler.Package(cdnUrl, deliveredPayload);

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Failed, outcome.Status);
        Assert.Equal(UpdatesStrings.ErrorHashMismatch, outcome.Message);
        Assert.Null(sandbox.Service.StagedPackagePath);
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(500, 10)]
    [InlineData(5, 10)]
    [InlineData(1234, 0)]
    public async Task A_package_whose_size_disagrees_with_the_manifest_is_rejected(long declaredSize, int bodyLength)
    {
        // sizeBytes is the manifest's second line of defence behind SHA-256 (and the only one that bounds a hostile
        // download before it is hashed); a mismatch means the offer is not the file that was signed.
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        var payload = new byte[bodyLength];
        var packageUrl = "https://example.test/package/Forge-1.2.0-win-x64.zip";
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl,
            UpdateTestManifest.StableWithHash("1.2.0", Rid, packageUrl, UpdateTestManifest.HexSha256(payload), declaredSize));
        sandbox.Handler.Package(packageUrl, payload);

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        var expected = declaredSize > bodyLength
            ? UpdatesStrings.ErrorSizeMismatch(bodyLength, declaredSize)
            : UpdatesStrings.ErrorDownloadTooLarge(declaredSize);
        Assert.Equal(UpdateCheckStatus.Failed, outcome.Status);
        Assert.Equal(expected, outcome.Message);
        Assert.Null(sandbox.Service.StagedPackagePath);
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_transport_that_omits_content_length_reports_the_manifest_size_as_the_total()
    {
        // Chunked release downloads carry no Content-Length; the progress line must fall back to the declared size instead
        // of showing "of unknown".
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        var payload = Encoding.UTF8.GetBytes("payload without a declared length");
        var packageUrl = "https://example.test/package/Forge-1.2.0-win-x64.zip";
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl,
            UpdateTestManifest.StableWithHash("1.2.0", Rid, packageUrl, UpdateTestManifest.HexSha256(payload), payload.Length));
        sandbox.Handler.Respond(packageUrl, () =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentLength = null;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
        });

        var progress = new List<UpdateDownloadProgress>();
        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None, progress.Add);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, outcome.Status);
        var last = Assert.Single(progress);
        Assert.Equal(payload.Length, last.BytesRead);
        Assert.Equal(payload.Length, last.TotalBytes);
    }

    [Fact]
    public async Task A_transport_that_declares_more_than_the_manifest_size_is_rejected_before_writing()
    {
        // A lying Content-Length must be refused up front: the download is bounded by what the manifest signed.
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        var payload = Encoding.UTF8.GetBytes("ten bytes");
        var packageUrl = "https://example.test/package/Forge-1.2.0-win-x64.zip";
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl,
            UpdateTestManifest.StableWithHash("1.2.0", Rid, packageUrl, UpdateTestManifest.HexSha256(payload), payload.Length));
        sandbox.Handler.Respond(packageUrl, () =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentLength = payload.Length + 100;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
        });

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Failed, outcome.Status);
        Assert.Equal(UpdatesStrings.ErrorDownloadTooLarge(payload.Length), outcome.Message);
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Re_checking_the_same_version_overwrites_it_and_cleans_a_stale_temp_file()
    {
        // Re-checking after a released fix must replace the staged copy, and the half-written .tmp of an interrupted
        // earlier attempt of the same version must not survive next to it.
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        var firstPayload = UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", Rid, out var packageUrl);
        var first = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, first.Status);

        var targetPath = first.Offer!.PackagePath;
        await File.WriteAllTextAsync(targetPath + ".tmp", "half written from an interrupted run");
        var secondPayload = Encoding.UTF8.GetBytes("re-signed payload of the same version");
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl,
            UpdateTestManifest.Stable("1.2.0", Rid, packageUrl, secondPayload));
        sandbox.Handler.Package(packageUrl, secondPayload);

        var second = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, second.Status);
        Assert.Equal(targetPath, second.Offer!.PackagePath);
        Assert.Equal(secondPayload, await File.ReadAllBytesAsync(targetPath));
        Assert.NotEqual(firstPayload, await File.ReadAllBytesAsync(targetPath));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*.tmp", SearchOption.AllDirectories));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(targetPath)!));
    }

    [Fact]
    public async Task A_cancelled_download_leaves_nothing_behind_and_the_next_check_recovers()
    {
        // Cancelling must not poison the next attempt: no lingering .tmp, no stale StagedPackagePath, and a fresh token
        // stages the same offer normally.
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        var payload = new byte[256 * 1024];
        new Random(42).NextBytes(payload);
        var packageUrl = "https://example.test/package/Forge-1.2.0-win-x64.zip";
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl, UpdateTestManifest.Stable("1.2.0", Rid, packageUrl, payload));
        sandbox.Handler.Package(packageUrl, payload);

        using (var cts = new CancellationTokenSource())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sandbox.Service.CheckForUpdateAsync(
                manual: true,
                cts.Token,
                progress =>
                {
                    if (progress.BytesRead > 0)
                    {
                        cts.Cancel();
                    }
                }));
        }

        Assert.Null(sandbox.Service.StagedPackagePath);
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*", SearchOption.AllDirectories));

        var recovered = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, recovered.Status);
        Assert.Equal(payload, await File.ReadAllBytesAsync(recovered.Offer!.PackagePath));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_manifest_response_larger_than_the_cap_is_a_readable_failure()
    {
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        var notes = new string('n', (int)UpdateManifestParser.MaxBytes + 1024);
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl,
            UpdateTestManifest.Stable("1.2.0", Rid, "https://example.test/Forge.zip", new byte[8], notes));

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Failed, outcome.Status);
        Assert.Equal(UpdatesStrings.ErrorManifestTooLarge, outcome.Message);
        Assert.Equal([UpdateTestManifest.ManifestUrl], sandbox.Handler.Requests);
    }

    [Fact]
    public async Task Open_download_location_opens_the_staging_root_then_the_staged_package()
    {
        var opened = new List<string>();
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0", openFolder: opened.Add);

        sandbox.Service.OpenDownloadLocation();

        Assert.Equal([sandbox.Root], opened);

        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", Rid, out _);
        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);
        sandbox.Service.OpenDownloadLocation();

        Assert.Equal(outcome.Offer!.PackagePath, opened[^1]);
    }

    [Fact]
    public async Task An_automatic_check_skips_development_builds_but_a_manual_check_does_not()
    {
        using var sandbox = UpdateTestService.Build(currentVersion: "0.0.0-ci.123");
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", "win-x64", out _);

        var automatic = await sandbox.Service.CheckForUpdateAsync(manual: false, CancellationToken.None);
        Assert.Equal(UpdateCheckStatus.SkippedDevBuild, automatic.Status);
        Assert.Empty(sandbox.Handler.Requests);

        var manual = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, manual.Status);
    }

    [Fact]
    public async Task The_automatic_pump_checks_only_when_due()
    {
        // never: the pump stays quiet even with no previous check.
        using (var never = UpdateTestService.Build(currentVersion: "1.0.0", frequency: UpdateSchedule.Never))
        {
            UpdateTestService.SetStableOffer(never.Handler, "1.2.0", "win-x64", out _);
            await never.Service.RunAutoCheckIfDueAsync(CancellationToken.None);
            Assert.Empty(never.Handler.Requests);
        }

        // daily, checked just now: not due.
        using (var daily = UpdateTestService.Build(currentVersion: "1.0.0", frequency: UpdateSchedule.Daily))
        {
            daily.UiState.Seed("updates.lastCheck", 1, "{\"checkedAt\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"}");
            await daily.Service.RunAutoCheckIfDueAsync(CancellationToken.None);
            Assert.Empty(daily.Handler.Requests);
        }

        // daily, never checked: due.
        using (var due = UpdateTestService.Build(currentVersion: "1.0.0", frequency: UpdateSchedule.Daily))
        {
            UpdateTestService.SetStableOffer(due.Handler, "1.2.0", "win-x64", out _);
            await due.Service.RunAutoCheckIfDueAsync(CancellationToken.None);
            Assert.Contains(UpdateTestManifest.ManifestUrl, due.Handler.Requests);
        }
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("{\"checkedAt\":\"not a date\"}")]
    [InlineData("{\"checkedAt\":42}")]
    [InlineData("{}")]
    public async Task A_corrupt_last_check_entry_reads_as_never_checked(string storedJson)
    {
        // ui_state is shared with other features and survives downgrades; an entry this build cannot read must degrade to
        // "check now", not to "never check again" and not to an exception out of the pump.
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0", frequency: UpdateSchedule.Daily);
        sandbox.UiState.Seed("updates.lastCheck", 1, storedJson);
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", Rid, out _);

        Assert.Null(await sandbox.Service.GetLastCheckAsync(CancellationToken.None));

        await sandbox.Service.RunAutoCheckIfDueAsync(CancellationToken.None);

        Assert.Contains(UpdateTestManifest.ManifestUrl, sandbox.Handler.Requests);
    }

    [Fact]
    public async Task A_manual_check_runs_even_when_the_frequency_is_never()
    {
        // "never" governs the automatic pump only; the Check-now button is the user's explicit override.
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0", frequency: UpdateSchedule.Never);
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", Rid, out _);

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, outcome.Status);
    }

    [Fact]
    public async Task The_pump_records_the_check_on_the_injected_clock_and_waits_for_the_next_window()
    {
        // The recorded time and the schedule must share one clock: recording DateTimeOffset.UtcNow while reading an
        // injected clock (or vice versa) would make the pump check every hour or never again.
        var clock = new UpdateTestClock(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0", frequency: UpdateSchedule.Daily, time: clock);
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", Rid, out _);

        await sandbox.Service.RunAutoCheckIfDueAsync(CancellationToken.None);
        var afterFirstCheck = sandbox.Handler.Requests.Count;
        Assert.Contains(UpdateTestManifest.ManifestUrl, sandbox.Handler.Requests);
        Assert.Equal(clock.Now, await sandbox.Service.GetLastCheckAsync(CancellationToken.None));

        await sandbox.Service.RunAutoCheckIfDueAsync(CancellationToken.None);
        Assert.Equal(afterFirstCheck, sandbox.Handler.Requests.Count);

        clock.Advance(TimeSpan.FromHours(23));
        await sandbox.Service.RunAutoCheckIfDueAsync(CancellationToken.None);
        Assert.Equal(afterFirstCheck, sandbox.Handler.Requests.Count);

        clock.Advance(TimeSpan.FromHours(1));
        await sandbox.Service.RunAutoCheckIfDueAsync(CancellationToken.None);
        Assert.True(
            sandbox.Handler.Requests.Count > afterFirstCheck,
            "a full day after the recorded check the pump must check again");
    }

    [Fact]
    public async Task A_last_check_recorded_ahead_of_the_clock_holds_the_pump_until_the_clock_catches_up()
    {
        // A wall clock set back after a check leaves the entry in the future. The pump must resume on its own once the
        // clock passes the recorded time plus the interval — it must not stay wedged forever, and it must not spam in the
        // meantime.
        var clock = new UpdateTestClock(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0", frequency: UpdateSchedule.Daily, time: clock);
        sandbox.UiState.Seed("updates.lastCheck", 1, "{\"checkedAt\":\"" + clock.Now.AddHours(2).ToString("O") + "\"}");
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", Rid, out _);

        await sandbox.Service.RunAutoCheckIfDueAsync(CancellationToken.None);
        Assert.Empty(sandbox.Handler.Requests);

        clock.Advance(TimeSpan.FromHours(20));
        await sandbox.Service.RunAutoCheckIfDueAsync(CancellationToken.None);
        Assert.Empty(sandbox.Handler.Requests);

        clock.Advance(TimeSpan.FromHours(6));
        await sandbox.Service.RunAutoCheckIfDueAsync(CancellationToken.None);
        Assert.Contains(UpdateTestManifest.ManifestUrl, sandbox.Handler.Requests);
    }

    [Fact]
    public async Task A_failed_check_is_retried_after_the_short_failure_window_not_the_whole_day()
    {
        // DOCUMENTED DEFECT D3 (see the W10b test report): the record carries "status":"failed", and the schedule reads it, so
        // an offline launch costs the short retry window instead of a full day, while a check that completed keeps the
        // configured interval.
        var clock = new UpdateTestClock(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0", frequency: UpdateSchedule.Daily, time: clock);
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl, System.Net.HttpStatusCode.InternalServerError);

        await sandbox.Service.RunAutoCheckIfDueAsync(CancellationToken.None);
        Assert.Single(sandbox.Handler.Requests);
        Assert.Equal(clock.Now, await sandbox.Service.GetLastCheckAsync(CancellationToken.None));

        clock.Advance(TimeSpan.FromHours(5));
        await sandbox.Service.RunAutoCheckIfDueAsync(CancellationToken.None);
        Assert.Single(sandbox.Handler.Requests);

        clock.Advance(TimeSpan.FromHours(1));
        await sandbox.Service.RunAutoCheckIfDueAsync(CancellationToken.None);
        Assert.Equal(2, sandbox.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("https://")]
    [InlineData("ftp://example.test/updates.json")]
    [InlineData("file:///c:/updates.json")]
    public async Task A_manifest_url_that_is_not_an_absolute_http_url_fails_readably(string url)
    {
        // DOCUMENTED DEFECT D1 (see the W10b test report): the URL is free text in the section. A typo is the user's most
        // likely mistake, so it must come back as a readable failure — not as an exception out of a command that shows the
        // user nothing at all.
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0", manifestUrl: () => url);

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Failed, outcome.Status);
        Assert.Equal(UpdatesStrings.ErrorManifestUrlInvalid(url), outcome.Message);
        Assert.Empty(sandbox.Handler.Requests);
    }

    [Fact]
    public async Task A_zero_byte_package_is_rejected_even_when_the_manifest_declares_no_size()
    {
        // DOCUMENTED DEFECT D6 (see the W10b test report): sizeBytes 0 means "unknown", so the size check is skipped and an
        // empty body would pass SHA-256 against the hash of the empty string and become a staged, installable offer. No
        // release package is zero bytes.
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        var packageUrl = "https://example.test/package/Forge-1.2.0-win-x64.zip";
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl,
            UpdateTestManifest.StableWithHash("1.2.0", Rid, packageUrl, UpdateTestManifest.HexSha256([]), 0));
        sandbox.Handler.Package(packageUrl, []);

        var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Failed, outcome.Status);
        Assert.Equal(UpdatesStrings.ErrorEmptyPackage, outcome.Message);
        Assert.Null(sandbox.Service.StagedOffer);
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_check_that_collides_with_a_running_check_is_turned_away_instead_of_racing_it()
    {
        // One staging directory, one download at a time: a "Check now" click during the pump's check must come back with a
        // readable result, and a pump tick during a manual check must skip the tick (not queue behind it, not record it).
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0", frequency: UpdateSchedule.Daily);
        var payload = Encoding.UTF8.GetBytes("package 1.2.0 payload");
        var packageUrl = "https://example.test/package/Forge-1.2.0-win-x64.zip";
        var manifest = UpdateTestManifest.Stable("1.2.0", Rid, packageUrl, payload);
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sandbox.Handler.Respond(UpdateTestManifest.ManifestUrl, () =>
        {
            holding.SetResult();
            release.Task.GetAwaiter().GetResult();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(manifest, Encoding.UTF8, "application/json"),
            };
        });
        sandbox.Handler.Package(packageUrl, payload);

        var running = Task.Run(() => sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None));
        await holding.Task;

        await sandbox.Service.RunAutoCheckIfDueAsync(CancellationToken.None);
        var collided = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Failed, collided.Status);
        Assert.Equal(UpdatesStrings.ErrorCheckBusy, collided.Message);

        release.SetResult();
        var finished = await running;

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, finished.Status);
        Assert.Equal([UpdateTestManifest.ManifestUrl, packageUrl], sandbox.Handler.Requests);
        Assert.Equal(1, sandbox.UiState.Puts);
    }

    [Fact]
    public async Task Start_checks_immediately_instead_of_waiting_a_whole_period()
    {
        // DOCUMENTED DEFECT (see the W10b review): the pump waited a full period before its first look, so an app started
        // and closed inside that hour never checked. The first tick is the launch itself.
        var clock = new UpdateTestClock(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0", frequency: UpdateSchedule.Daily, time: clock);
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", Rid, out _);

        sandbox.Service.Start();

        await ApprovalFixtures.WaitUntilAsync(
            () => sandbox.UiState.Puts > 0,
            "the startup check to fetch the manifest and record its outcome");
        Assert.Equal(1, sandbox.Handler.Requests.Count(request => request == UpdateTestManifest.ManifestUrl));
        Assert.Equal(clock.Now, await sandbox.Service.GetLastCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Start_leaves_an_automatic_check_that_is_not_due_alone()
    {
        // The startup tick consults the schedule like every other tick: a check one hour ago must not be repeated at launch.
        var clock = new UpdateTestClock(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0", frequency: UpdateSchedule.Daily, time: clock);
        sandbox.UiState.Seed(
            "updates.lastCheck",
            1,
            "{\"checkedAt\":\"" + clock.Now.AddHours(-1).ToString("O") + "\",\"status\":\"up-to-date\"}");
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", Rid, out _);

        sandbox.Service.Start();

        // The clock read is the proof the tick ran: without it, "no request" would also pass if the pump never started.
        await ApprovalFixtures.WaitUntilAsync(() => clock.Reads > 0, "the startup tick to consult the schedule");
        Assert.Empty(sandbox.Handler.Requests);
    }

    [Fact]
    public async Task The_pump_survives_an_unexpected_exception_and_keeps_checking()
    {
        // The pump runs for the life of the app: one faulty tick (a clock, the file system) must be logged and swallowed, not
        // end the loop — otherwise a single bad launch silently stops every future automatic check.
        var clock = new FaultingUpdateClock(
            new UpdateTestClock(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero)),
            failures: 1);
        using var sandbox = UpdateTestService.Build(
            currentVersion: "1.0.0",
            frequency: UpdateSchedule.Daily,
            time: clock,
            autoCheckPeriod: TimeSpan.FromMilliseconds(25));
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", Rid, out _);

        sandbox.Service.Start();

        await ApprovalFixtures.WaitUntilAsync(
            () => sandbox.Handler.Requests.Count > 0,
            "the pump to recover on the tick after the failing one");
        Assert.Contains(UpdateTestManifest.ManifestUrl, sandbox.Handler.Requests);
    }

    [Fact]
    public async Task Dispose_stops_a_running_pump()
    {
        // The clock moves itself, so every tick of a live pump is due and would fetch the manifest within milliseconds: requests
        // that keep arriving after Dispose are a pump that outlived the app.
        var clock = new AutoAdvancingUpdateClock(
            new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero),
            TimeSpan.FromDays(2));
        using var sandbox = UpdateTestService.Build(
            currentVersion: "1.0.0",
            frequency: UpdateSchedule.Daily,
            time: clock,
            autoCheckPeriod: TimeSpan.FromMilliseconds(20));
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", Rid, out _);

        sandbox.Service.Start();
        await ApprovalFixtures.WaitUntilAsync(
            () => sandbox.Handler.Requests.Count > 0,
            "the startup check of the running pump");

        sandbox.Service.Dispose();
        await Task.Delay(100);
        var settled = sandbox.Handler.Requests.Count;
        await Task.Delay(300);

        Assert.Equal(settled, sandbox.Handler.Requests.Count);
    }

    [Fact]
    public async Task Dispose_stops_the_automatic_pump_and_is_idempotent()
    {
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0", frequency: UpdateSchedule.Daily);
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", Rid, out _);

        sandbox.Service.Dispose();
        sandbox.Service.Dispose();
        await sandbox.Service.RunAutoCheckIfDueAsync(CancellationToken.None);

        // Shutdown must stop the background pump: no request may start on a service the app has already torn down.
        Assert.Empty(sandbox.Handler.Requests);
    }
}

using System.Net.Http;
using Forge.App.Approval;
using Forge.App.Composition;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Approval;

/// <summary>
/// The composition seam the extensions repository needs (wave W10a): <see cref="ApprovalServices.Create"/> hands the
/// injected preferences and the repository <see cref="HttpClient"/> to the extensions settings section, without breaking
/// the call shape everything else uses.
/// </summary>
public sealed class ApprovalServicesRepositoryWiringTests
{
    [Fact]
    public async Task Create_injects_the_preferences_and_the_repository_client_into_the_settings_section()
    {
        var (root, previous) = TempDataRoot.Redirect("forge-approval-services-wiring-");
        try
        {
            var preferences = new AppPreferences();
            preferences.ExtensionRepositoryUrl = "https://example.test/repo.json";
            using var repositoryHttp = new HttpClient(new UnreachableHandler()) { Timeout = Timeout.InfiniteTimeSpan };

            await using (var services = ApprovalServices.Create(
                             AppPaths.Create(),
                             host: null,
                             hostProblem: default,
                             notifications: null,
                             preferences,
                             repositoryHttp))
            {
                Assert.Equal("https://example.test/repo.json", services.SettingsSection.RepositoryUrl);
                Assert.True(services.SettingsSection.HasRepositoryUrl);

                // Editing the section is the same statement as editing the preference: the next save persists it.
                services.SettingsSection.RepositoryUrl = "https://example.test/changed.json";
                Assert.Equal("https://example.test/changed.json", preferences.ExtensionRepositoryUrl);

                // No host means nothing to install into, so browsing is not offered - and building the section must not
                // have reached the network either (the handler throws if it is ever called).
                Assert.Null(services.Extensions);
                Assert.False(services.SettingsSection.BrowseRepositoryCommand.CanExecute(null));
            }

            // The pre-W10a call shape still builds a working section; the repository URL simply starts empty.
            await using (var legacy = ApprovalServices.Create(AppPaths.Create(), host: null, hostProblem: default))
            {
                Assert.Equal(string.Empty, legacy.SettingsSection.RepositoryUrl);
                Assert.False(legacy.SettingsSection.HasRepositoryUrl);
                Assert.False(legacy.SettingsSection.HasRepositoryProblem);
            }
        }
        finally
        {
            previous.Restore();
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task A_changed_repository_url_survives_a_second_services_instance_on_the_same_preferences()
    {
        var (root, previous) = TempDataRoot.Redirect("forge-approval-services-reload-");
        try
        {
            var preferences = new AppPreferences();
            await using (var first = ApprovalServices.Create(
                             AppPaths.Create(),
                             host: null,
                             hostProblem: default,
                             notifications: null,
                             preferences))
            {
                first.SettingsSection.RepositoryUrl = "https://example.test/kept.json";
            }

            await using var second = ApprovalServices.Create(
                AppPaths.Create(),
                host: null,
                hostProblem: default,
                notifications: null,
                preferences);

            Assert.Equal("https://example.test/kept.json", second.SettingsSection.RepositoryUrl);
        }
        finally
        {
            previous.Restore();
            TestTempFiles.DeleteTree(root);
        }
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "the extensions settings section reached the network while it was only being constructed");
    }
}

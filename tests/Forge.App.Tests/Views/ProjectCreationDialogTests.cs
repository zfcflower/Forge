using Avalonia.Controls;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Views;

/// <summary>
/// The clone / new-project dialog as a real window, headless (wave W9). <see cref="ViewModels.ProjectCreationViewModel"/>
/// tests cover the model, and the shell's flow tests answer the dialog with a recording double — so without this file nothing
/// runs the one path the user actually takes: a successful creation has to <em>close</em> the window and hand the created
/// directory back to <c>ShowDialog</c>. A close held back while the model still looked busy would leave the project
/// registered nowhere while the window stayed open, which no model test can see.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ProjectCreationDialogTests
{
    [Fact]
    public async Task A_successful_creation_closes_the_dialog_with_the_created_directory()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var answer = await SharedUi.Session.Dispatch(
            async () =>
            {
                var parent = TestTempFiles.NewDirectory("forge-project-dialog-window-");
                try
                {
                    var service = new ProjectCreationService(
                        new GitCli(),
                        new AppPreferences { ProjectsCloneDirectory = parent },
                        parent);
                    using var model = new ProjectCreationViewModel(ProjectCreationMode.Create, service);
                    model.Name = "from-the-dialog";

                    var owner = new Window { Width = 400, Height = 300 };
                    owner.Show();
                    try
                    {
                        var dialog = new ProjectCreationDialog { DataContext = model };
                        var result = dialog.ShowDialog<string?>(owner);

                        // Starting through the model is what the primary button does; the dialog's job is what follows.
                        Assert.True(await model.StartAsync());
                        var returned = await result.WaitAsync(TimeSpan.FromSeconds(60));
                        return new Answer(returned, model.CreatedDirectory);
                    }
                    finally
                    {
                        owner.Close();
                    }
                }
                finally
                {
                    TestTempFiles.DeleteTree(parent);
                }
            },
            budget.Token);

        // The window closed by itself (otherwise ShowDialog never completed) and answered with the folder the shell registers.
        Assert.Equal(answer.Created, answer.Returned);
        Assert.NotNull(answer.Created);
    }

    private sealed record Answer(string? Returned, string? Created);
}

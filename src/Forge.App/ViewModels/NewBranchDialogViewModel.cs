using System.ComponentModel;
using System.Diagnostics;
using Forge.App.Localization;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>
/// The new-branch dialog: one start point (the shared <see cref="BranchPickerViewModel"/>) and one branch name. The name
/// is validated as it is typed - debounced, on the thread pool, applied on the UI thread - and only a name that git
/// accepts and no local branch already carries enables Create. The dialog itself only renders; the answer travels back as
/// a <see cref="NewBranchChoice"/>.
/// </summary>
internal sealed class NewBranchDialogViewModel : ObservableObject, IDisposable
{
    /// <summary>Quiet time after the last keystroke before the name is checked; typed names are also checked once at open.</summary>
    private static readonly TimeSpan ValidationDelay = TimeSpan.FromMilliseconds(250);

    private readonly GitCli git;
    private readonly NewBranchRequest request;
    private readonly CancellationTokenSource lifetime;
    private CancellationTokenSource? validationSource;
    private string branchName;
    private NameState nameState = NameState.Pending;
    private string? hint;
    private bool isLoadingBranches = true;
    private bool disposed;
    private int validationGeneration;

    /// <param name="git">The git reader; also the authority on what a legal branch name is.</param>
    /// <param name="request">The repository whose branches are the start points, and the name to prefill.</param>
    /// <param name="ct">Cancels the dialog's work when the flow that opened it goes away.</param>
    public NewBranchDialogViewModel(GitCli git, NewBranchRequest request, CancellationToken ct)
    {
        this.git = git;
        this.request = request;
        branchName = request.SuggestedName;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Picker = new BranchPickerViewModel(git);
        Picker.PropertyChanged += OnPickerPropertyChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        // The list starts empty and the dialog opens on "loading": without this the hint would only appear once the
        // read already finished, which is exactly when it is no longer true.
        RefreshHint();
    }

    /// <summary>Start point of the new branch; the same picker the changes panel shows as its compare base.</summary>
    public BranchPickerViewModel Picker { get; }

    /// <summary>The name of the new branch, prefilled with the flow's suggestion and trimmed on the way out.</summary>
    public string BranchName
    {
        get => branchName;
        set
        {
            if (Set(ref branchName, value ?? string.Empty))
            {
                // Nothing is verified about the new text yet, so Create has to go down with the first keystroke rather
                // than with the answer that arrives a debounce later.
                nameState = NameState.Pending;
                RefreshHint();
                ScheduleValidation(immediate: false);
            }
        }
    }

    /// <summary>
    /// Why Create is unavailable, or null while the dialog has nothing to complain about: the branch list is loading, no
    /// start point exists, or the typed name is missing, invalid or already taken.
    /// </summary>
    public string? Hint
    {
        get => hint;
        private set
        {
            if (Set(ref hint, value))
            {
                OnPropertyChanged(nameof(HasHint));
            }
        }
    }

    public bool HasHint => hint is { Length: > 0 };

    /// <summary>The start-point trigger: the chosen branch's own name, or the placeholder while none is available.</summary>
    public string BaseBranchLabel => Picker.SelectedBranchName is { Length: > 0 } name
        ? name
        : ProjectStrings.NewBranchBasePlaceholder;

    /// <summary>True only for a name git accepts that no local branch already uses, once the start point is known.</summary>
    public bool CanCreate => !isLoadingBranches && nameState == NameState.Ok && Picker.SelectedBranch is not null;

    /// <summary>The answer the dialog closes with; null whenever Create must not create anything.</summary>
    public NewBranchChoice? Result => CanCreate && Picker.SelectedBranch is { } start
        ? new NewBranchChoice(start.RefName, branchName.Trim())
        : null;

    /// <summary>
    /// Fills the start-point list, then checks the prefilled name against it: the repository may already carry that
    /// branch, and Create has to start out disabled for exactly that reason. Runs once, when the dialog opens.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            await Picker.LoadAsync(request.RepositoryPath, skipCurrentBranch: false, lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or GitException or UnauthorizedAccessException)
        {
            // A repository whose branches cannot be read has no start point; the dialog says so and stays uncreatable.
            Trace.WriteLine("[new-branch] could not read the branches: " + exception.Message);
        }
        finally
        {
            if (!disposed)
            {
                isLoadingBranches = false;
                RefreshHint();
            }
        }

        if (!disposed)
        {
            ScheduleValidation(immediate: true);
        }
    }

    /// <summary>
    /// Releases the language subscription, the picker's, and both cancellation sources. The dialog closes exactly once, so
    /// nothing can call into a disposed instance afterwards.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        Picker.PropertyChanged -= OnPickerPropertyChanged;
        Interlocked.Increment(ref validationGeneration);
        var pending = Interlocked.Exchange(ref validationSource, null);
        if (pending is not null)
        {
            pending.Cancel();
            pending.Dispose();
        }

        lifetime.Cancel();
        lifetime.Dispose();
    }

    private void ScheduleValidation(bool immediate)
    {
        var generation = Interlocked.Increment(ref validationGeneration);
        var source = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref validationSource, source);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        _ = ValidateAsync(generation, immediate, source.Token);
    }

    private async Task ValidateAsync(int generation, bool immediate, CancellationToken ct)
    {
        try
        {
            if (!immediate)
            {
                await Task.Delay(ValidationDelay, ct).ConfigureAwait(true);
            }

            var name = branchName.Trim();
            if (name.Length == 0)
            {
                Apply(generation, NameState.Required);
                return;
            }

            // git's own ref-name rules, so the dialog and `git worktree add` cannot disagree about what a name is.
            var valid = await Task.Run(() => git.IsValidBranchNameAsync(request.RepositoryPath, name, ct), ct)
                .ConfigureAwait(true);
            Apply(generation, !valid ? NameState.Invalid : HasLocalBranch(name) ? NameState.Taken : NameState.Ok);
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke, a closed dialog or the flow being cancelled: this answer is not shown.
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or GitException or UnauthorizedAccessException)
        {
            // Without an answer from git the name stays unverified, so Create stays disabled instead of failing later.
            Trace.WriteLine("[new-branch] could not validate the branch name: " + exception.Message);
            Apply(generation, NameState.Unverified);
        }
    }

    /// <summary>Applies one validation result unless a newer keystroke already asked for another one.</summary>
    private void Apply(int generation, NameState state)
    {
        if (generation != Volatile.Read(ref validationGeneration) || disposed)
        {
            return;
        }

        nameState = state;
        RefreshHint();
    }

    private bool HasLocalBranch(string name)
    {
        var branches = Picker.LocalBranches;
        for (var index = 0; index < branches.Count; index++)
        {
            if (string.Equals(branches[index].Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void RefreshHint()
    {
        Hint = isLoadingBranches
            ? ProjectStrings.NewBranchLoading
            : Picker.SelectedBranch is null
                ? ProjectStrings.NewBranchNoBase
                : nameState switch
                {
                    NameState.Required => ProjectStrings.NewBranchNameRequired,
                    NameState.Invalid => ProjectStrings.NewBranchNameInvalid,
                    NameState.Taken => ProjectStrings.NewBranchNameTaken,
                    _ => null,
                };
        OnPropertyChanged(nameof(CanCreate));
    }

    private void OnPickerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BranchPickerViewModel.SelectedBranch):
            case nameof(BranchPickerViewModel.SelectedBranchName):
                OnPropertyChanged(nameof(BaseBranchLabel));
                RefreshHint();
                break;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshHint();
        OnPropertyChanged(nameof(BaseBranchLabel));
    }

    private enum NameState
    {
        /// <summary>Not checked yet: nothing has been typed, or a check is still queued.</summary>
        Pending,
        Required,
        Invalid,
        Taken,
        Unverified,
        Ok,
    }
}

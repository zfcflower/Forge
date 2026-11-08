using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Editor.Text;
using DocumentChange = AvaloniaEdit.Document.DocumentChangeEventArgs;
using EditorDocument = AvaloniaEdit.Document.TextDocument;
using ForgeDocument = Forge.Editor.Text.TextDocument;
using ForgeTextChangedEventArgs = Forge.Editor.Text.TextChangedEventArgs;

namespace Forge.App.Editing;

internal sealed class ForgeEditorMirror : IDisposable
{
    private readonly FileDocumentViewModel model;
    private readonly ForgeDocument forge;
    private long mirrorVersion;
    private bool forwarding;
    private bool applying;
    private bool disposed;

    public ForgeEditorMirror(FileDocumentViewModel model, ForgeDocument forge)
    {
        this.model = model;
        this.forge = forge;
        var snapshot = forge.Snapshot;
        mirrorVersion = snapshot.Version;
        Document = new EditorDocument(snapshot.GetText());
        Document.UndoStack.SizeLimit = 1000;
        Document.Changed += OnMirrorChanged;
        forge.Changed += OnForgeChanged;
    }

    public EditorDocument Document { get; }

    /// <summary>The mirrored Forge snapshot: what the theme folding heuristic scans, off the UI thread, without touching the editor document.</summary>
    public TextSnapshot Snapshot => forge.Snapshot;

    public bool IsFaulted { get; private set; }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Document.Changed -= OnMirrorChanged;
        forge.Changed -= OnForgeChanged;
    }

    private void OnMirrorChanged(object? sender, DocumentChange e)
    {
        if (applying || disposed || IsFaulted)
        {
            return;
        }

        if (forge.Version != mirrorVersion)
        {
            Fault();
            return;
        }

        TextSnapshot snapshot;
        forwarding = true;
        try
        {
            snapshot = forge.ApplyChange(new TextChange(e.Offset, e.RemovalLength, e.InsertionLength == 0 ? null : e.InsertedText.Text));
        }
        finally
        {
            forwarding = false;
        }

        mirrorVersion = snapshot.Version;
        model.NotifyDocumentEdited();
    }

    private void OnForgeChanged(object? sender, ForgeTextChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Fault);
            return;
        }

        if (forwarding)
        {
            return;
        }

        ApplyToMirror(e);
    }

    private void ApplyToMirror(ForgeTextChangedEventArgs e)
    {
        if (disposed || IsFaulted)
        {
            return;
        }

        if (e.Old.Version != mirrorVersion)
        {
            Fault();
            return;
        }

        applying = true;
        try
        {
            var change = e.MinimalChange;
            Document.Replace(change.Offset, change.Length, change.Text);
            mirrorVersion = e.New.Version;
        }
        finally
        {
            applying = false;
        }

        model.NotifyDocumentEdited();
    }

    private void Fault()
    {
        if (disposed || IsFaulted)
        {
            return;
        }

        IsFaulted = true;
        model.ReportEditorFault(new LocText(InspectorStrings.FilesEditorDesynced));
    }
}

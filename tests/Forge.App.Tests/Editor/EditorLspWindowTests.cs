using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Forge.App.Editing;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Editor.Languages;
using Forge.Editor.Lsp;
using Xunit;

namespace Forge.App.Tests.Editor;

[Collection("forge-app-ui")]
public sealed class EditorLspWindowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-lsp-window-" + Guid.NewGuid().ToString("N"));
    public EditorLspWindowTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public Task Ctrl_space_applies_the_selected_completion_as_one_undo_step_and_syncs_the_server() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync(root, "foo\n", new() { ["textDocument/completion"] = """{"isIncomplete":false,"items":[{"label":"foobar","additionalTextEdits":[{"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":0}},"newText":"// added\n"}]}]}""" });
        rig.Editor.CaretOffset = 3;
        rig.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        await rig.Server.WaitForAsync("textDocument/completion");
        await rig.Until(() => rig.CompletionShown);
        rig.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await rig.Until(() => rig.Editor.Document.Text == "foobar\n// added\n");
        await rig.Until(() => !rig.CompletionShown);
        var changes = new StringBuilder();
        for (var i = 0; i < 3 && !(changes.ToString().Contains("foobar", StringComparison.Ordinal) && changes.ToString().Contains("// added", StringComparison.Ordinal)); i++)
        {
            using var change = JsonDocument.Parse(await rig.Server.WaitForAsync("textDocument/didChange", i + 1));
            changes.Append(change.RootElement.GetProperty("params").GetRawText());
        }
        Assert.Contains("foobar", changes.ToString(), StringComparison.Ordinal);
        Assert.Contains("// added", changes.ToString(), StringComparison.Ordinal);
        ((System.Windows.Input.ICommand)rig.Model.SaveCommand).Execute(null);
        await rig.Until(() => File.ReadAllText(rig.Model.Path) == "foobar\n// added\n");
        rig.Window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        await rig.Until(() => rig.Editor.Document.Text == "foo\n");
    });

    [Fact]
    public Task Typing_a_dot_requests_completion_at_the_exact_caret_position() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync(root, "foo\n");
        rig.Editor.CaretOffset = 3;
        rig.Window.KeyTextInput(".");
        using var request = JsonDocument.Parse(await rig.Server.WaitForAsync("textDocument/completion"));
        var position = request.RootElement.GetProperty("params").GetProperty("position");
        Assert.Equal(0, position.GetProperty("line").GetInt32());
        Assert.Equal(4, position.GetProperty("character").GetInt32());
        Assert.Equal("foo.\n", rig.Editor.Document.Text);
    });

    [Fact]
    public Task A_completion_answered_after_the_user_kept_typing_is_dropped() => Ui(async () =>
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await Rig.OpenAsync(root, "foo\n",
            new() { ["textDocument/completion"] = """{"isIncomplete":false,"items":[{"label":"foobar"}]}""" },
            new() { ["textDocument/completion"] = hold });
        rig.Editor.CaretOffset = 3;
        rig.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        await rig.Server.WaitForAsync("textDocument/completion");
        rig.Window.KeyTextInput("x");
        hold.SetResult();
        await rig.Server.WaitForAsync("textDocument/didChange");
        await rig.Pump(400);
        Assert.False(rig.CompletionShown);
        rig.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await rig.Pump(100);
        Assert.Equal("foox\n\n", rig.Editor.Document.Text);
    });

    [Fact]
    public Task F12_on_a_same_file_definition_moves_the_caret_without_reloading() => Ui(async () =>
    {
        var path = Path.Combine(root, "Program.cs");
        var uri = LspUri.FromPath(path);
        await using var rig = await Rig.OpenAsync(root, "class A { }\nclass B : A { }\n", new()
        {
            ["textDocument/definition"] = $$$$"""[{"uri":"{{{{uri}}}}","range":{"start":{"line":0,"character":6},"end":{"line":0,"character":7}}}]""",
        });
        var document = rig.Editor.Document;
        rig.Editor.CaretOffset = "class A { }\n".Length + 10;
        rig.Window.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.None);
        using var request = JsonDocument.Parse(await rig.Server.WaitForAsync("textDocument/definition"));
        Assert.Equal(1, request.RootElement.GetProperty("params").GetProperty("position").GetProperty("line").GetInt32());
        await rig.Until(() => rig.Editor.CaretOffset == 6);
        Assert.Same(document, rig.Editor.Document);
        Assert.Equal(1, rig.Server.Count("textDocument/didOpen"));
        Assert.False(rig.Model.IsDirty);
    });

    [Fact]
    public Task F12_on_another_file_raises_a_navigation_request_and_leaves_this_document_untouched() => Ui(async () =>
    {
        var other = Path.Combine(root, "Other.cs");
        await File.WriteAllTextAsync(other, "class Z { }\n");
        await using var rig = await Rig.OpenAsync(root, "class B { Z z; }\n", new()
        {
            ["textDocument/definition"] = $$$$"""[{"uri":"{{{{LspUri.FromPath(other)}}}}","range":{"start":{"line":2,"character":3},"end":{"line":2,"character":4}}}]""",
        });
        EditorLspDefinitionTarget? target = null;
        rig.Model.DefinitionRequested += (_, t) => target = t;
        rig.Editor.CaretOffset = 10;
        rig.Window.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.None);
        await rig.Until(() => target is not null);
        Assert.Equal(Path.GetFullPath(other), Path.GetFullPath(target!.Path), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, target.Position.Line);
        Assert.Equal(3, target.Position.Character);
        Assert.Equal("class B { Z z; }\n", rig.Editor.Document.Text);
        Assert.Equal(10, rig.Editor.CaretOffset);
    });

    [Fact]
    public Task Arrow_keys_move_the_selection_in_the_completion_list() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync(root, "foo" + ((char)10).ToString(), new()
        {
            ["textDocument/completion"] = """{"isIncomplete":false,"items":[{"label":"foobar"},{"label":"foobaz"},{"label":"fooqux"}]}""",
        });
        rig.Editor.CaretOffset = 3;
        rig.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        await rig.Server.WaitForAsync("textDocument/completion");
        await rig.Until(() => rig.View?.CompletionPopup is not null);

        var list = rig.View!.CompletionPopup!.CompletionList;
        await rig.Until(() => list.ListBox?.ItemCount == 3);
        var selected = list.ListBox!.SelectedIndex;

        // Moving the selection is only half of it: the selected row has to look selected. The app's workbench list styles
        // blank out ListBoxItem backgrounds globally, which used to leave the popup's selection invisible.
        var background = SelectedItemBackground(list);
        Assert.NotNull(background);
        Assert.InRange(background!.Value.A, 1, 255);

        rig.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        WorkbenchUi.Settle(rig.Window);
        Assert.Equal(selected + 1, list.ListBox.SelectedIndex);

        rig.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        WorkbenchUi.Settle(rig.Window);
        Assert.Equal(selected, list.ListBox.SelectedIndex);
    });

    [Fact]
    public Task A_discovered_server_is_named_on_the_editor_status_line() => Ui(async () =>
    {
        var registry = LanguageRegistry.Default.WithDiscoveredServers(
        [
            new DiscoveredLanguageServer(
                "csharp",
                [".cs", ".csx"],
                [],
                new LanguageServerConfig("/opt/vscode/roslyn-language-server", ["--stdio"], new Dictionary<string, string>()),
                "VS Code"),
        ]);
        var newline = ((char)10).ToString();
        await using var rig = await Rig.OpenAsync(root, "class A { }" + newline, registry: registry);

        await rig.Until(() => rig.Model.HasLspStatus);
        Assert.Contains("roslyn-language-server", rig.Model.LspStatus!, StringComparison.Ordinal);
        Assert.Contains("VS Code", rig.Model.LspStatus!, StringComparison.Ordinal);
    });

    [Fact]
    public Task Pulled_diagnostics_reach_the_editor_without_a_push_notification() => Ui(async () =>
    {
        var newline = ((char)10).ToString();
        const string Capabilities = """
            {"capabilities":{"textDocumentSync":{"openClose":true,"change":2,"save":true},"diagnosticProvider":{"interFileDependencies":true}}}
            """;
        await using var rig = await Rig.OpenAsync(
            root,
            "class A { }" + newline,
            new()
            {
                ["textDocument/diagnostic"] = """{"kind":"full","items":[{"range":{"start":{"line":0,"character":6},"end":{"line":0,"character":7}},"severity":1,"message":"pulled","code":"CS9"}]}""",
            },
            capabilities: Capabilities);

        var renderer = rig.Editor.TextArea.TextView.BackgroundRenderers.OfType<EditorLspDiagnosticRenderer>().Single();
        await rig.Server.WaitForAsync("textDocument/diagnostic");
        // The server never publishes anything here: the editor has to ask, and the answer has to reach the view.
        await rig.Until(() => rig.Model.EditorDiagnostics.Items.Count == 1);
        Assert.True(rig.Server.Count("textDocument/diagnostic") > 0, "the editor pulled the diagnostics");

        await rig.Until(() => renderer.FindAt(6).Count == 1);
        Assert.Equal("pulled", renderer.FindAt(6)[0].Message);
        Assert.Equal("CS9", renderer.FindAt(6)[0].Code);
    });

    [Fact]
    public Task A_declared_trigger_character_opens_the_completion_list() => Ui(async () =>
    {
        var newline = ((char)10).ToString();
        await using var rig = await Rig.OpenAsync(root, "foo" + newline, new()
        {
            ["textDocument/completion"] = """{"isIncomplete":false,"items":[{"label":"beta"}]}""",
        });
        rig.Editor.CaretOffset = 3;
        rig.Window.KeyTextInput(":");
        await rig.Server.WaitForAsync("textDocument/completion");
        await rig.Until(() => rig.CompletionShown);
        rig.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await rig.Until(() => !rig.CompletionShown);
    });

    [Fact]
    public Task Folding_ranges_from_the_server_are_used_instead_of_the_heuristic() => Ui(async () =>
    {
        var newline = ((char)10).ToString();
        await using var rig = await Rig.OpenAsync(
            root,
            "// alpha" + newline + "// beta" + newline + "// gamma" + newline,
            new()
            {
                ["textDocument/foldingRange"] = """[{"startLine":0,"endLine":2,"kind":"comment"}]""",
            });
        var view = rig.Window.Content as global::Forge.App.Views.FileDocumentView;
        Assert.NotNull(view);
        Assert.NotNull(view!.Folding);
        await rig.Until(() => view.Folding!.HasFoldings);
        Assert.Contains(
            rig.Editor.TextArea.LeftMargins,
            margin => margin is global::AvaloniaEdit.Folding.FoldingMargin);
        view.Folding!.FoldAll();
        Assert.True(view.Folding.AnyFolded);
        await rig.Server.WaitForAsync("textDocument/foldingRange");
    });

    [Fact]
    public Task Published_diagnostics_are_rendered_and_cleared_on_the_real_text_view() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync(root, "class A { }\n");
        var renderer = rig.Editor.TextArea.TextView.BackgroundRenderers.OfType<EditorLspDiagnosticRenderer>().Single();
        await rig.Server.WaitForAsync("textDocument/didOpen");
        rig.Server.Notify("textDocument/publishDiagnostics",
            $$$"""{"uri":"{{{LspUri.FromPath(rig.Path)}}}","diagnostics":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":5}},"severity":1,"message":"boom","code":"E1"}]}""");
        await rig.Until(() => renderer.FindAt(2).Count == 1);
        var diagnostic = Assert.Single(renderer.FindAt(2));
        Assert.Equal("boom", diagnostic.Message);
        Assert.Equal((0, 5), (diagnostic.Start, diagnostic.End));
        Assert.Empty(renderer.FindAt(8));
        rig.Server.Notify("textDocument/publishDiagnostics", $$"""{"uri":"{{LspUri.FromPath(rig.Path)}}","diagnostics":[]}""");
        await rig.Until(() => renderer.FindAt(2).Count == 0);
    });

    /// <summary>The background brush of the selected completion row's content presenter, or null when it has none.</summary>
    private static Avalonia.Media.Color? SelectedItemBackground(AvaloniaEdit.CodeCompletion.CompletionList list)
    {
        if (list.ListBox is not { } box || box.SelectedIndex < 0)
        {
            return null;
        }

        if (box.ContainerFromIndex(box.SelectedIndex) is not Control container)
        {
            return null;
        }

        var presenter = container.GetVisualDescendants()
            .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
            .FirstOrDefault(static candidate => candidate.Name == "PART_ContentPresenter");
        return (presenter?.Background as Avalonia.Media.ISolidColorBrush)?.Color;
    }

    private static async Task Ui(Func<Task> body)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await SharedUi.Session.Dispatch(async () => { await body(); return 0; }, budget.Token);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private LanguageSessionPool? pool;
        public required string Path { get; init; }
        public required Window Window { get; init; }
        public required TextEditor Editor { get; init; }
        public FileDocumentView? View { get; private set; }
        public required FileDocumentViewModel Model { get; init; }
        public ScriptedServer Server { get; private set; } = null!;
        public bool CompletionShown => !Editor.TextArea.StackedInputHandlers.IsEmpty;

        public static async Task<Rig> OpenAsync(string root, string text,
            Dictionary<string, string>? results = null, Dictionary<string, TaskCompletionSource>? holds = null,
            LanguageRegistry? registry = null, string? capabilities = null)
        {
            var path = System.IO.Path.Combine(root, "Program.cs");
            await File.WriteAllTextAsync(path, text);
            ScriptedServer? server = null;
            var previousFactory = LanguageServerProcess.DefaultFactory;
            var sessionPool = new LanguageSessionPool(registry ?? LanguageRegistry.Default);
            var model = FileDocumentViewModel.Create(null, sessionPool);
            Window? window = null;
            try
            {
                LanguageServerProcess.DefaultFactory = (_, policy) => server = new ScriptedServer(policy, results ?? [], holds ?? [], capabilities);
                model.Workspace = root;
                await model.LoadAsync(path, CancellationToken.None);
                await model.LspOpening;
                var view = new FileDocumentView { DataContext = model };
                window = new Window { Width = 800, Height = 400, Content = view };
                window.Show();
                WorkbenchUi.Settle(window);
                var editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
                editor.TextArea.Focus();
                Assert.NotNull(server);
                return new Rig { Path = path, Window = window, Editor = editor, Model = model, pool = sessionPool, Server = server, View = view };
            }
            catch
            {
                window?.Close();
                await model.ReleaseDocumentAsync();
                await sessionPool.DisposeAsync();
                throw;
            }
            finally
            {
                LanguageServerProcess.DefaultFactory = previousFactory;
            }
        }

        public async Task Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                WorkbenchUi.Settle(Window);
                if (condition())
                {
                    return;
                }
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
                await Task.Delay(10);
            }
        }

        public async Task Pump(int milliseconds)
        {
            var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < until)
            {
                WorkbenchUi.Settle(Window);
                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await Model.ReleaseDocumentAsync();
            await pool!.DisposeAsync();
        }
    }

    private sealed class ScriptedServer : LanguageServerProcess
    {
        private const string Capabilities = """{"capabilities":{"textDocumentSync":{"openClose":true,"change":2,"save":true},"completionProvider":{"triggerCharacters":[".",":"]},"foldingRangeProvider":true,"hoverProvider":true,"definitionProvider":true}}""";
        private readonly object gate = new();
        private readonly object writeGate = new();
        private readonly List<(string Method, string Raw)> received = [];
        private readonly List<IDisposable> pipes = [];
        private readonly Dictionary<string, string> results;
        private readonly Dictionary<string, TaskCompletionSource> holds;
        private Stream? output;
        private bool running;

        public ScriptedServer(RestartPolicy policy, Dictionary<string, string> results, Dictionary<string, TaskCompletionSource> holds, string? capabilities = null)
            : base(new LanguageServerLaunch("fake-language-server"), policy)
        {
            this.results = results;
            this.holds = holds;
            this.capabilities = capabilities;
        }

        private readonly string? capabilities;

        private string InitializeResult => capabilities ?? Capabilities;
        public override event EventHandler<LanguageServerExitedEventArgs>? Exited;
        public override bool IsRunning { get { lock (gate) { return running; } } }
        public int Count(string method) { lock (gate) { return received.Count(m => m.Method == method); } }

        public override ProcessStreams Start()
        {
            lock (gate)
            {
                var clientToServer = new AnonymousPipeServerStream(PipeDirection.Out);
                var serverInput = new AnonymousPipeClientStream(PipeDirection.In, clientToServer.ClientSafePipeHandle);
                var serverToClient = new AnonymousPipeServerStream(PipeDirection.Out);
                var clientInput = new AnonymousPipeClientStream(PipeDirection.In, serverToClient.ClientSafePipeHandle);
                pipes.AddRange([clientToServer, serverInput, serverToClient, clientInput]);
                output = serverToClient;
                running = true;
                _ = Task.Run(() => Serve(serverInput, serverToClient));
                return new ProcessStreams(clientToServer, clientInput);
            }
        }

        public override ValueTask StopAsync(CancellationToken ct = default)
        {
            bool wasRunning;
            lock (gate)
            {
                wasRunning = running;
                running = false;
                foreach (var pipe in pipes) { pipe.Dispose(); }
                pipes.Clear();
            }
            foreach (var hold in holds.Values) { hold.TrySetResult(); }
            if (wasRunning) { Exited?.Invoke(this, new LanguageServerExitedEventArgs(0, true)); }
            return ValueTask.CompletedTask;
        }

        public override async Task<ProcessStreams> RestartAsync(CancellationToken ct = default)
        {
            await StopAsync(ct);
            return Start();
        }
        public override async ValueTask DisposeAsync() => await StopAsync();

        public void Notify(string method, string paramsJson)
        {
            Stream? stream;
            lock (gate) { stream = output; }
            Send(stream!, "{\"jsonrpc\":\"2.0\",\"method\":\"" + method + "\",\"params\":" + paramsJson + "}");
        }

        public async Task<string> WaitForAsync(string method, int occurrence = 1)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                lock (gate)
                {
                    var matches = received.Where(m => m.Method == method).ToList();
                    if (matches.Count >= occurrence) { return matches[occurrence - 1].Raw; }
                }
                Assert.True(DateTime.UtcNow < deadline, "The server never received " + method + " #" + occurrence);
                await Task.Delay(10);
            }
        }

        private void Serve(Stream input, Stream stream)
        {
            try
            {
                while (ReadFrame(input) is { } json)
                {
                    using var document = JsonDocument.Parse(json);
                    var message = document.RootElement;
                    var hasMethod = message.TryGetProperty("method", out var methodElement);
                    var method = hasMethod ? methodElement.GetString() ?? string.Empty : string.Empty;
                    lock (gate) { received.Add((method, json)); }
                    if (hasMethod && message.TryGetProperty("id", out var id))
                    {
                        if (holds.TryGetValue(method, out var hold)) { hold.Task.Wait(); }
                        var result = method == "initialize" ? InitializeResult : results.GetValueOrDefault(method, "null");
                        Send(stream, "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":" + result + "}");
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                System.Diagnostics.Trace.WriteLine("[test-lsp] pipe closed: " + exception.Message);
            }
        }

        private static string? ReadFrame(Stream stream)
        {
            var length = -1;
            var line = new StringBuilder();
            while (true)
            {
                var next = stream.ReadByte();
                if (next < 0) { return null; }
                if (next != '\n') { line.Append((char)next); continue; }
                var header = line.ToString().TrimEnd('\r');
                line.Clear();
                if (header.Length == 0) { if (length >= 0) { break; } continue; }
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    length = int.Parse(header["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);
                }
            }
            var body = new byte[length];
            var read = 0;
            while (read < length)
            {
                var count = stream.Read(body, read, length - read);
                if (count == 0) { return null; }
                read += count;
            }
            return Encoding.UTF8.GetString(body);
        }

        private void Send(Stream stream, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
            lock (writeGate) { stream.Write(header); stream.Write(body); stream.Flush(); }
        }
    }
}

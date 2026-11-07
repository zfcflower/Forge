using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Forge.App.Tests.Workbench;
using Forge.App.Views;
using Forge.App.ViewModels;
using Forge.Editor.Languages;
using Forge.Editor.Lsp;
using Xunit;

namespace Forge.App.Tests.ViewModels;

[Collection("forge-app-ui")]
public sealed class FileEditorLspTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-file-lsp-" + Guid.NewGuid().ToString("N"));
    public FileEditorLspTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public async Task Open_edit_save_and_release_send_didOpen_didChange_didSave_and_didClose()
    {
        var path = Path.Combine(root, "Program.cs");
        await File.WriteAllTextAsync(path, "class A { }\n");
        await using var lsp = new FakeLsp();
        await Ui(async () =>
        {
            var model = FileDocumentViewModel.Create(null, lsp.Pool);
            model.Workspace = root;
            await model.LoadAsync(path, CancellationToken.None);
            Assert.False(model.HasError, model.Error);
            await model.LspOpening;
            Assert.NotNull(lsp.Server);
            Assert.Contains("class A", await lsp.Server!.WaitForAsync("textDocument/didOpen"), StringComparison.Ordinal);
            model.IsEditing = true;
            model.EditText = "class A { } // appended\n";
            Assert.Contains("appended", await lsp.Server.WaitForAsync("textDocument/didChange"), StringComparison.Ordinal);
            ((ICommand)model.SaveCommand).Execute(null);
            await lsp.Server.WaitForAsync("textDocument/didSave");
            Assert.Equal("class A { } // appended\n", await File.ReadAllTextAsync(path));
            await model.ReleaseDocumentAsync();
            await lsp.Server.WaitForAsync("textDocument/didClose");
            Assert.False(model.HasDocument);
            Assert.False(model.IsEditing);
        });
    }

    [Fact]
    public async Task Typing_and_saving_in_the_real_editor_send_exact_didChange_and_didSave_then_closing_the_view_sends_didClose()
    {
        var path = Path.Combine(root, "Program.cs");
        await File.WriteAllTextAsync(path, "class A { }\nclass B { }\n");
        await using var lsp = new FakeLsp();
        await Ui(async () =>
        {
            var model = FileDocumentViewModel.Create(null, lsp.Pool);
            model.Workspace = root;
            await model.LoadAsync(path, CancellationToken.None);
            await model.LspOpening;
            await lsp.Server!.WaitForAsync("textDocument/didOpen");
            var view = new FileDocumentView { DataContext = model };
            var window = new Window { Width = 800, Height = 400, Content = view };
            window.Show();
            WorkbenchUi.Settle(window);
            var editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            editor.TextArea.Focus();
            editor.CaretOffset = "class A { }\n".Length;
            window.KeyTextInput("X");
            WorkbenchUi.Settle(window);
            using var change = JsonDocument.Parse(await lsp.Server.WaitForAsync("textDocument/didChange"));
            var change0 = change.RootElement.GetProperty("params").GetProperty("contentChanges")[0];
            Assert.Equal("X", change0.GetProperty("text").GetString());
            var start = change0.GetProperty("range").GetProperty("start");
            Assert.Equal(1, start.GetProperty("line").GetInt32());
            Assert.Equal(0, start.GetProperty("character").GetInt32());
            var end = change0.GetProperty("range").GetProperty("end");
            Assert.Equal(1, end.GetProperty("line").GetInt32());
            Assert.Equal(0, end.GetProperty("character").GetInt32());
            window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
            await lsp.Server.WaitForAsync("textDocument/didSave");
            Assert.Equal("class A { }\nXclass B { }\n", await File.ReadAllTextAsync(path));
            window.Close();
            await model.ReleaseDocumentAsync();
            await lsp.Server.WaitForAsync("textDocument/didClose");
        });
    }

    [Fact]
    public async Task Loading_another_file_closes_the_previous_document_on_the_server()
    {
        var first = Path.Combine(root, "A.cs");
        var second = Path.Combine(root, "B.cs");
        await File.WriteAllTextAsync(first, "class A { }\n");
        await File.WriteAllTextAsync(second, "class B { }\n");
        await using var lsp = new FakeLsp();
        await Ui(async () =>
        {
            var model = FileDocumentViewModel.Create(null, lsp.Pool);
            model.Workspace = root;
            await model.LoadAsync(first, CancellationToken.None);
            await model.LspOpening;
            await lsp.Server!.WaitForAsync("textDocument/didOpen");
            await model.LoadAsync(second, CancellationToken.None);
            await model.LspOpening;
            await lsp.Server.WaitForAsync("textDocument/didClose");
            await lsp.Server.WaitForAsync("textDocument/didOpen", occurrence: 2);
            Assert.Equal("class B { }", model.GetLinesText(1, 1).TrimEnd('\r', '\n'));
            await model.ReleaseDocumentAsync();
        });
    }

    [Fact]
    public async Task A_slow_handshake_does_not_delay_the_content_and_didOpen_follows_when_the_server_answers()
    {
        var path = Path.Combine(root, "Slow.cs");
        await File.WriteAllTextAsync(path, "class A { }\n");
        await using var lsp = new FakeLsp { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await Ui(async () =>
        {
            var model = FileDocumentViewModel.Create(null, lsp.Pool);
            model.Workspace = root;
            var load = model.LoadAsync(path, CancellationToken.None);
            Assert.Same(load, await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(5))));
            await load;
            Assert.True(model.HasDocument);
            Assert.Equal("class A { }", model.GetLinesText(1, 1).TrimEnd('\r', '\n'));
            Assert.False(model.LspOpening.IsCompleted);
            lsp.Gate.SetResult();
            await model.LspOpening;
            Assert.Contains("class A", await lsp.Server!.WaitForAsync("textDocument/didOpen"), StringComparison.Ordinal);
            model.IsEditing = true;
            model.EditText = "class A { } // later\n";
            Assert.Contains("later", await lsp.Server.WaitForAsync("textDocument/didChange"), StringComparison.Ordinal);
            await model.ReleaseDocumentAsync();
        });
    }

    [Fact]
    public async Task Releasing_during_a_slow_handshake_cancels_the_open_and_never_revives_the_document()
    {
        var path = Path.Combine(root, "Cancel.cs");
        await File.WriteAllTextAsync(path, "class A { }\n");
        await using var lsp = new FakeLsp { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await Ui(async () =>
        {
            var model = FileDocumentViewModel.Create(null, lsp.Pool);
            model.Workspace = root;
            await model.LoadAsync(path, CancellationToken.None);
            await model.ReleaseDocumentAsync();
            lsp.Gate.SetResult();
            try { await model.LspOpening; }
            catch (OperationCanceledException) { System.Diagnostics.Trace.WriteLine("[test-lsp] open cancelled"); }
            await Task.Delay(300);
            Assert.False(model.HasDocument);
            Assert.Equal(string.Empty, model.EditText);
            Assert.Null(model.EditorDocument);
            Assert.Equal(0, lsp.Server?.Count("textDocument/didOpen") ?? 0);
        });
    }

    [Fact]
    public async Task Releasing_while_a_file_is_still_loading_leaves_no_document()
    {
        var path = Path.Combine(root, "Big.txt");
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat("0123456789abcdef\n", 200_000)));
        await using var lsp = new FakeLsp();
        await Ui(async () =>
        {
            var model = FileDocumentViewModel.Create(null, lsp.Pool);
            var load = model.LoadAsync(path, CancellationToken.None);
            await model.ReleaseDocumentAsync();
            try { await load; }
            catch (OperationCanceledException) { System.Diagnostics.Trace.WriteLine("[test-lsp] load cancelled"); }
            Assert.False(model.HasDocument);
            Assert.Equal(string.Empty, model.EditText);
            Assert.Null(model.EditorDocument);
            Assert.False(model.IsLoading);
        });
    }

    private static async Task Ui(Func<Task> body)
    {
        var session = SharedUi.Session;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await session.Dispatch(async () => { await body(); return 0; }, budget.Token);
    }

    private sealed class FakeLsp : IAsyncDisposable
    {
        private readonly Func<LanguageServerLaunch, RestartPolicy, LanguageServerProcess>? previous = LanguageServerProcess.DefaultFactory;
        public FakeLsp()
        {
            LanguageServerProcess.DefaultFactory = (_, policy) => Server = new RecordingServer(policy, Gate);
            Pool = new LanguageSessionPool(LanguageRegistry.Default);
        }

        public LanguageSessionPool Pool { get; }
        public TaskCompletionSource? Gate { get; init; }
        public RecordingServer? Server { get; private set; }
        public async ValueTask DisposeAsync()
        {
            Gate?.TrySetResult();
            LanguageServerProcess.DefaultFactory = previous;
            await Pool.DisposeAsync();
        }
    }

    private sealed class RecordingServer : LanguageServerProcess
    {
        private const string Capabilities = """{"capabilities":{"textDocumentSync":{"openClose":true,"change":2,"save":true}}}""";
        private readonly object gate = new();
        private readonly object writeGate = new();
        private readonly List<(string Method, string Raw)> received = [];
        private readonly List<IDisposable> pipes = [];
        private bool running;
        private readonly TaskCompletionSource? initializeGate;
        public RecordingServer(RestartPolicy policy, TaskCompletionSource? initializeGate = null) : base(new LanguageServerLaunch("fake-language-server"), policy) =>
            this.initializeGate = initializeGate;
        public int Count(string method) { lock (gate) { return received.Count(m => m.Method == method); } }
        public override event EventHandler<LanguageServerExitedEventArgs>? Exited;
        public override bool IsRunning { get { lock (gate) { return running; } } }

        public override ProcessStreams Start()
        {
            lock (gate)
            {
                var clientToServer = new AnonymousPipeServerStream(PipeDirection.Out);
                var serverInput = new AnonymousPipeClientStream(PipeDirection.In, clientToServer.ClientSafePipeHandle);
                var serverToClient = new AnonymousPipeServerStream(PipeDirection.Out);
                var clientInput = new AnonymousPipeClientStream(PipeDirection.In, serverToClient.ClientSafePipeHandle);
                pipes.AddRange([clientToServer, serverInput, serverToClient, clientInput]);
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

            if (wasRunning) { Exited?.Invoke(this, new LanguageServerExitedEventArgs(0, true)); }
            return ValueTask.CompletedTask;
        }

        public override async Task<ProcessStreams> RestartAsync(CancellationToken ct = default)
        {
            await StopAsync(ct);
            return Start();
        }
        public override async ValueTask DisposeAsync() => await StopAsync();

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

        private void Serve(Stream input, Stream output)
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
                    if (method == "initialize") { initializeGate?.Task.Wait(); }
                    if (hasMethod && message.TryGetProperty("id", out var id))
                    {
                        Send(output, "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":" + (method == "initialize" ? Capabilities : "null") + "}");
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

        private void Send(Stream output, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
            lock (writeGate) { output.Write(header); output.Write(body); output.Flush(); }
        }
    }
}

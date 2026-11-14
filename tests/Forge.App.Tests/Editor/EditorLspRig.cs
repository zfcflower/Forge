using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Editor.Languages;
using Forge.Editor.Lsp;
using Xunit;

namespace Forge.App.Tests.Editor;

/// <summary>
/// A file document shown in a real window with an in-process language server behind it: a fresh temp directory, a
/// <c>Program.cs</c> written into it, a session pool whose process factory the <see cref="ScriptedServer"/> replaces, and the
/// window/editor/VM trio a test drives. The server answers every request with the JSON a test registered for that method, so
/// an editor feature can be exercised without a language server on the machine.
/// </summary>
internal sealed class EditorLspRig : IAsyncDisposable
{
    private LanguageSessionPool? pool;

    public required string Path { get; init; }

    public required Window Window { get; init; }

    public required TextEditor Editor { get; init; }

    public FileDocumentView? View { get; private set; }

    public required FileDocumentViewModel Model { get; init; }

    public ScriptedServer Server { get; private set; } = null!;

    /// <summary>True while the completion list is showing any stacked input handler, i.e. the popup is open.</summary>
    public bool CompletionShown => !Editor.TextArea.StackedInputHandlers.IsEmpty;

    /// <summary>
    /// Opens <paramref name="text"/> as <c>&lt;root&gt;/Program.cs</c> and shows it in an 800x400 window.
    /// <paramref name="results"/> maps a JSON-RPC method to the JSON that is returned as its <c>result</c>;
    /// <paramref name="holds"/> maps a method to a gate the server waits on before answering it; <paramref name="capabilities"/>
    /// replaces the whole <c>initialize</c> result.
    /// </summary>
    public static async Task<EditorLspRig> OpenAsync(string root, string text,
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
            return new EditorLspRig { Path = path, Window = window, Editor = editor, Model = model, pool = sessionPool, Server = server, View = view };
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

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds, failing after ten seconds.</summary>
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

    /// <summary>Pumps the dispatcher for a fixed stretch of time, for "this must not happen" assertions.</summary>
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

    /// <summary>
    /// The fake language server: it speaks the same framed JSON-RPC over two anonymous pipes the real process would, records
    /// every request it received, and answers each one from the registered JSON per method (a method with no registration
    /// answers <c>null</c>). <c>initialize</c> answers the declared capabilities.
    /// </summary>
    internal sealed class ScriptedServer : LanguageServerProcess
    {
        private const string Capabilities = """
            {"capabilities":{"textDocumentSync":{"openClose":true,"change":2,"save":true},"completionProvider":{"triggerCharacters":[".",":"]},"foldingRangeProvider":true,"hoverProvider":true,"definitionProvider":true,"referencesProvider":true,"semanticTokensProvider":{"legend":{"tokenTypes":["variable","function","class"],"tokenModifiers":[]},"full":true}}}
            """;
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

        /// <summary>How many requests of that method arrived, answered or waiting.</summary>
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

        /// <summary>Sends a server-to-client notification, e.g. <c>textDocument/publishDiagnostics</c>.</summary>
        public void Notify(string method, string paramsJson)
        {
            Stream? stream;
            lock (gate) { stream = output; }
            Send(stream!, "{\"jsonrpc\":\"2.0\",\"method\":\"" + method + "\",\"params\":" + paramsJson + "}");
        }

        /// <summary>The raw JSON of the <paramref name="occurrence"/>-th request of that method, waiting for it to arrive.</summary>
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

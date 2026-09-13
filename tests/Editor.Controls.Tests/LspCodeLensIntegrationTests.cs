using System.Windows.Threading;
using Editor.Controls.Lsp;
using Editor.Core.Lsp;

namespace Editor.Controls.Tests;

public sealed class LspCodeLensIntegrationTests
{
    [Fact]
    public void View_bridge_publishes_only_resolved_executable_code_lenses()
    {
        WpfTestHost.Run(() =>
        {
            static LspRange Range(int line) => new(
                new LspPosition(line, 0), new LspPosition(line, 1));

            var unresolved = new LspCodeLens(Range(1), RawJson: "{\"id\":1}");
            var unresolvedWithoutCommand = new LspCodeLens(Range(2), RawJson: "{\"id\":2}");
            var executable = new LspCodeLens(
                Range(3), new LspCodeActionCommand("already.run", "Already"));
            var resolved = new LspCodeLens(
                Range(1), new LspCodeActionCommand("test.run", "Run tests"));
            var document = new FakeDocument(
                [unresolved, unresolvedWithoutCommand, executable], resolved, supportsResolve: true);
            var workspace = new FakeWorkspace(document);
            var bridge = new LspViewBridge(Dispatcher.CurrentDispatcher, workspace);
            IReadOnlyList<LspCodeLens>? published = null;
            var frame = new DispatcherFrame();
            bridge.CodeLensesChanged += lenses =>
            {
                if (lenses.Count > 0)
                {
                published = lenses;
                    frame.Continue = false;
                }
            };

            try
            {
                bridge.OnFileOpened(@"C:\work\sample.cs", "class C {}\n");
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);

                Assert.NotNull(published);
                Assert.Equal([resolved, executable], published);

                document.Ready = false;
                document.Connected = false;
                var cleared = false;
                var clearFrame = new DispatcherFrame();
                bridge.CodeLensesChanged += lenses =>
                {
                    if (lenses.Count == 0)
                    {
                        cleared = true;
                        clearFrame.Continue = false;
                    }
                };
                document.RaiseStateChanged();
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => clearFrame.Continue = false));
                Dispatcher.PushFrame(clearFrame);

                Assert.True(cleared);
                Assert.Empty(bridge.CurrentCodeLenses);
            }
            finally
            {
                bridge.Dispose();
            }
        });
    }

    private sealed class FakeWorkspace(FakeDocument document) : ILspWorkspace
    {
        public ILspDocument? OpenDocument(string filePath, string initialText) => document;
        public bool IsServerAvailableFor(string extension) => true;
        public Task<IReadOnlyList<LspSymbolInformation>> GetWorkspaceSymbolsAsync(
            string query, bool isClass, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LspSymbolInformation>>([]);
        public Task<LspWorkspaceDiagnosticResult?> RequestWorkspaceDiagnosticsAsync(CancellationToken ct = default) =>
            Task.FromResult<LspWorkspaceDiagnosticResult?>(null);
        public Task<CallHierarchyItem?> PrepareCallHierarchyAsync(string uri, int line, int character) =>
            Task.FromResult<CallHierarchyItem?>(null);
        public Task<CallHierarchyIncomingCall[]?> GetIncomingCallsAsync(CallHierarchyItem item) =>
            Task.FromResult<CallHierarchyIncomingCall[]?>(null);
        public Task<CallHierarchyOutgoingCall[]?> GetOutgoingCallsAsync(CallHierarchyItem item) =>
            Task.FromResult<CallHierarchyOutgoingCall[]?>(null);
        public Task<TypeHierarchyItem?> PrepareTypeHierarchyAsync(string uri, int line, int character) =>
            Task.FromResult<TypeHierarchyItem?>(null);
        public Task<TypeHierarchyItem[]?> GetSupertypesAsync(TypeHierarchyItem item) =>
            Task.FromResult<TypeHierarchyItem[]?>(null);
        public Task<TypeHierarchyItem[]?> GetSubtypesAsync(TypeHierarchyItem item) =>
            Task.FromResult<TypeHierarchyItem[]?>(null);

        public event Action<string, IReadOnlyList<LspDiagnostic>>? DiagnosticsPublished { add { } remove { } }
        public event Action? ServerStateChanged { add { } remove { } }
    }

    private sealed class FakeDocument(
        IReadOnlyList<LspCodeLens> lenses,
        LspCodeLens resolved,
        bool supportsResolve) : ILspDocument
    {
        public string Uri => "file:///C:/work/sample.cs";
        public string FilePath => @"C:\work\sample.cs";
        public string LanguageId => "csharp";
        public bool Connected { get; set; } = true;
        public bool Ready { get; set; } = true;
        public bool IsConnected => Connected;
        public bool IsReady => Ready;
        public bool IsWriter => true;
        public IReadOnlyList<LspDiagnostic> CurrentDiagnostics => [];
        public bool ServerSupportsFoldingRange => false;
        public bool ServerSupportsRangeFormatting => false;
        public bool ServerSupportsSelectionRange => false;
        public bool ServerSupportsWorkspaceDiagnostics => false;
        public bool ServerSupportsCodeLens => true;
        public bool ServerSupportsCodeLensResolve => supportsResolve;

        public void UpdateText(string text) { }
        public Task<IReadOnlyList<LspCodeLens>> RequestCodeLensesAsync(CancellationToken ct = default) =>
            Task.FromResult(lenses);
        public Task<LspCodeLens?> ResolveCodeLensAsync(LspCodeLens lens, CancellationToken ct = default) =>
            Task.FromResult<LspCodeLens?>(lens.RawJson?.Contains("\"id\":1", StringComparison.Ordinal) == true ? resolved : null);
        public Task<IReadOnlyList<LspCompletionItem>> RequestCompletionAsync(int line, int character, CancellationToken ct = default) => throw Unused();
        public Task<LspHover?> RequestHoverAsync(int line, int character) => throw Unused();
        public Task<(string Uri, int Line, int Column)?> RequestDefinitionAsync(int line, int character) => throw Unused();
        public Task<LspSignatureHelp?> RequestSignatureHelpAsync(int line, int character, CancellationToken ct = default) => throw Unused();
        public Task<LspWorkspaceEdit?> RequestRenameAsync(int line, int character, string newName) => throw Unused();
        public Task<IReadOnlyList<LspLocation>> RequestReferencesAsync(int line, int character) => throw Unused();
        public Task<IReadOnlyList<LspCodeAction>> RequestCodeActionsAsync(int line, int character) => throw Unused();
        public Task<IReadOnlyList<LspTextEdit>> RequestFormattingAsync(int tabSize, bool insertSpaces) => throw Unused();
        public Task<IReadOnlyList<LspTextEdit>> RequestRangeFormattingAsync(LspRange range, int tabSize, bool insertSpaces) => throw Unused();
        public Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsAsync() => Task.FromResult<IReadOnlyList<DocumentSymbol>>([]);
        public Task<IReadOnlyList<LspFoldingRange>> RequestFoldingRangesAsync() => Task.FromResult<IReadOnlyList<LspFoldingRange>>([]);
        public Task<IReadOnlyList<InlayHint>> RequestInlayHintsAsync(int startLine, int endLine) => Task.FromResult<IReadOnlyList<InlayHint>>([]);
        public Task<SemanticToken[]?> RequestSemanticTokensAsync() => Task.FromResult<SemanticToken[]?>([]);
        public Task<IReadOnlyList<DocumentHighlight>?> RequestDocumentHighlightAsync(int line, int character, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DocumentHighlight>?>([]);
        public Task<LspSelectionRange?> RequestSelectionRangeAsync(int line, int character) => Task.FromResult<LspSelectionRange?>(null);
        public void Dispose() { }
        public void RaiseStateChanged() => StateChangedHandlers?.Invoke();

        public event Action<IReadOnlyList<LspDiagnostic>>? DiagnosticsChanged { add { } remove { } }
        private event Action? StateChangedHandlers;
        public event Action? StateChanged
        {
            add => StateChangedHandlers += value;
            remove => StateChangedHandlers -= value;
        }
        public event Action<string>? StatusMessage { add { } remove { } }

        private static NotSupportedException Unused() => new("このテストでは使用しない要求です。");
    }
}

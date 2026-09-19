using System.Windows.Threading;
using Editor.Controls.Lsp;
using Editor.Core.Lsp;

namespace Editor.Controls.Tests;

public sealed class LspCodeLensIntegrationTests
{
    /// <summary>ブリッジは 2 回流す——1 回目は<b>行を確保するため</b>の未解決のまま（範囲は
    /// 一覧応答の時点で確定している）、2 回目が解決済みで押せるレンズ。解決を待ってから 1 回だけ
    /// 流していた頃は、実測で開いてから 3.2 秒後に全宣言の下が一斉にずれていた。</summary>
    [Fact]
    public void View_bridge_reserves_rows_then_publishes_resolved_executable_code_lenses()
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
                [unresolved, unresolvedWithoutCommand, executable], resolved, supportsResolve: true)
            { Path = @"C:\work\publish-sample.cs" };
            var workspace = new FakeWorkspace(document);
            var bridge = new LspViewBridge(Dispatcher.CurrentDispatcher, workspace);
            var publishes = new List<IReadOnlyList<LspCodeLens>>();
            var frame = new DispatcherFrame();
            bridge.CodeLensesChanged += lenses =>
            {
                if (lenses.Count == 0) return;
                publishes.Add(lenses);
                if (publishes.Count == 2) frame.Continue = false;
            };

            try
            {
                bridge.OnFileOpened(@"C:\work\sample.cs", "class C {}\n");
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);

                Assert.Equal(2, publishes.Count);
                // 1回目＝行の確保。未解決のまま、範囲（＝行）だけがそろっている。
                Assert.Equal([unresolved, unresolvedWithoutCommand, executable], publishes[0]);
                // 2回目＝解決できたものにラベルが入る。resolve に失敗したものも<b>落とさない</b>
                // （行が消えると本文が跳ねる）。押せるラベルを出さないのは描画側の役目。
                Assert.Equal([resolved, unresolvedWithoutCommand, executable], publishes[1]);

                document.Ready = false;
                // サーバーが落ちた／つなぎ直し中。行は消さず、古い印を立てるだけ
                // （消すと本文がまるごと跳ね、つながり直した数秒後にまた戻ってくる）。
                document.Connected = false;
                var stale = false;
                var staleFrame = new DispatcherFrame();
                bridge.CodeLensStaleChanged += value =>
                {
                    stale = value;
                    staleFrame.Continue = false;
                };
                document.RaiseStateChanged();
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => staleFrame.Continue = false));
                Dispatcher.PushFrame(staleFrame);

                Assert.True(stale);
                Assert.NotEmpty(bridge.CurrentCodeLenses);
            }
            finally
            {
                bridge.Dispose();
            }
        });
    }

    /// <summary>打鍵で CodeLens を捨てない。捨てていた頃は、注釈行が畳まれて本文が上へ跳ね、
    /// 700ms のデバウンス後に戻ってきて上下にガタつき、「x 個の参照」も消えて出直して見えた。
    /// 行が増減したときは位置が当てにならないので、消さずに<b>古い印</b>を立てる
    /// （薄く出して押せなくする）。</summary>
    [Fact]
    public void Editing_never_drops_the_code_lenses_but_marks_them_stale()
    {
        WpfTestHost.Run(() =>
        {
            static LspRange Range(int line) => new(
                new LspPosition(line, 0), new LspPosition(line, 1));

            var executable = new LspCodeLens(
                Range(0), new LspCodeActionCommand("test.run", "Run"));
            var document = new FakeDocument([executable], executable, supportsResolve: false)
            { Path = @"C:\work\editing-sample.cs" };
            var bridge = new LspViewBridge(Dispatcher.CurrentDispatcher, new FakeWorkspace(document));
            var frame = new DispatcherFrame();
            var publishes = 0;
            bool? stale = null;
            bridge.CodeLensesChanged += lenses =>
            {
                if (lenses.Count == 0) return;
                if (++publishes == 2) frame.Continue = false;
            };
            bridge.CodeLensStaleChanged += value => stale = value;

            try
            {
                bridge.OnFileOpened(@"C:\work\sample.cs", "class C {}\n");
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Assert.NotEmpty(bridge.CurrentCodeLenses);

                // 行内の打鍵——行は動かないので、古い印も立てない。
                bridge.OnTextChanged("class D {}\n");
                Assert.NotEmpty(bridge.CurrentCodeLenses);
                Assert.NotEqual(true, stale);

                // 改行を足した——下の宣言の行がずれる。それでもラベルは消さず、押せなくするだけ。
                bridge.OnTextChanged("class D {}\n\n");
                Assert.NotEmpty(bridge.CurrentCodeLenses);
                Assert.True(stale);
            }
            finally
            {
                bridge.Dispose();
            }
        });
    }

    /// <summary>2 度目に同じファイルを開いたら、サーバーの答えを待たずに<b>その場で</b>注釈行が立つ。
    /// 一覧応答は実測で 0.5 秒かかり、そのあいだ行が無いと本文を読み始めた頃にずれるため。</summary>
    [Fact]
    public void Reopening_a_file_reserves_the_rows_before_the_server_answers()
    {
        WpfTestHost.Run(() =>
        {
            var path = @"C:\work\cached-lens-sample.cs";
            var lens = new LspCodeLens(
                new LspRange(new LspPosition(7, 0), new LspPosition(7, 1)),
                new LspCodeActionCommand("test.run", "Run"));
            var document = new FakeDocument([lens], lens, supportsResolve: false) { Path = path };
            var bridge = new LspViewBridge(Dispatcher.CurrentDispatcher, new FakeWorkspace(document));
            var text = string.Join("\n", Enumerable.Repeat("line", 20));
            var frame = new DispatcherFrame();
            bridge.CodeLensesChanged += lenses => { if (lenses.Count > 0) frame.Continue = false; };

            try
            {
                bridge.OnFileOpened(path, text);
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Assert.NotEmpty(bridge.CurrentCodeLenses);

                // 開き直す——ディスパッチャを回す前に、もう行が立っている。
                bridge.OnFileOpened(path, text);
                Assert.Equal(7, Assert.Single(bridge.CurrentCodeLenses).Range.Start.Line);
            }
            finally
            {
                bridge.Dispose();
            }
        });
    }

    /// <summary>再取得のたびに「x 個の参照」が消えて出直さない。一覧応答のレンズは未解決＝
    /// ラベルが無いので、そのまま流すと resolve が済むまで文字が消えて明滅して見える。</summary>
    [Fact]
    public void Reserving_rows_keeps_the_labels_already_known()
    {
        static LspRange Range(int line, int character) => new(
            new LspPosition(line, character), new LspPosition(line, character + 1));

        var shown = new LspCodeLens(
            Range(3, 4), new LspCodeActionCommand("editor.action.showReferences", "2 個の参照"));
        // 宣言行に一文字打つと桁がずれる。行が同じならラベルは引き継ぐ。
        var refreshed = new LspCodeLens(Range(3, 5), RawJson: "{\"id\":9}");
        var newLine = new LspCodeLens(Range(8, 4), RawJson: "{\"id\":10}");

        var merged = LspViewBridge.KeepKnownLabels([shown], [refreshed, newLine]);

        Assert.Equal("2 個の参照", merged[0].Title);
        // 押したときに使うのは新しい応答のほう（古い data で解決しない）。
        Assert.Equal("{\"id\":9}", merged[0].RawJson);
        Assert.Equal(Range(3, 5), merged[0].Range);
        // 知らない行はそのまま——ラベルは resolve が済んでから入る。
        Assert.Null(merged[1].Command);
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
        /// <summary>CodeLens の行はファイルごとに憶えられるので、テストごとに別の綴りを使えるようにする。</summary>
        public string Path { get; set; } = @"C:\work\sample.cs";
        public string FilePath => Path;
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

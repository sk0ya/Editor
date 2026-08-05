using System.Threading;

namespace Editor.Core.Lsp;

/// <summary>
/// A handle on one open LSP text document, obtained from <see cref="ILspWorkspace.OpenDocument"/>.
/// The handle — not the view that holds it — is what the server knows about: <c>didOpen</c> is sent
/// once per URI however many handles exist, and <see cref="IDisposable.Dispose"/> drops this handle's
/// reference (<c>didClose</c> only once the last one goes away).
///
/// <para><b>Threading:</b> every member is safe to call from any thread, and <b>all events fire on a
/// background thread</b> (the JSON-RPC read loop). Marshalling to a UI dispatcher is the subscriber's
/// job — see the same note on <see cref="ILspWorkspace"/>.</para>
/// </summary>
public interface ILspDocument : IDisposable
{
    /// <summary>The <c>file://</c> URI this handle is bound to.</summary>
    string Uri { get; }

    /// <summary>The local path this handle was opened for.</summary>
    string FilePath { get; }

    /// <summary>The LSP language id negotiated for this document (e.g. "csharp").</summary>
    string LanguageId { get; }

    /// <summary>True while the language server process backing this document is alive.</summary>
    bool IsConnected { get; }

    /// <summary>True once <c>didOpen</c> has been sent for this URI (requests before that are dropped).</summary>
    bool IsReady { get; }

    /// <summary>
    /// True when this handle is the one whose text is mirrored to the server. The first handle opened
    /// for a URI is the writer; later ones are readers whose <see cref="UpdateText"/> is a no-op.
    /// Ownership transfers to the next remaining handle when the writer is disposed.
    /// </summary>
    bool IsWriter { get; }

    /// <summary>現在サーバーへ通知済みの文書版。ホストが版を管理しない場合は null。</summary>
    int? Version => null;

    /// <summary>The most recent <c>publishDiagnostics</c> payload for this URI (shared by every handle on it).</summary>
    IReadOnlyList<LspDiagnostic> CurrentDiagnostics { get; }

    bool ServerSupportsFoldingRange { get; }
    bool ServerSupportsRangeFormatting { get; }
    bool ServerSupportsSelectionRange { get; }
    bool ServerSupportsWorkspaceDiagnostics { get; }
    IReadOnlyList<string> CompletionTriggerCharacters => ["."];

    /// <summary>サーバーが申告した code action kind。空は未申告。</summary>
    IReadOnlyList<string> ServerCodeActionKinds => [];
    /// <summary>サーバーが <c>codeAction/resolve</c> に対応しているか。</summary>
    bool ServerSupportsCodeActionResolve => false;

    /// <summary>Mirror the buffer text to the server (<c>didChange</c>). No-op when <see cref="IsWriter"/> is false.</summary>
    void UpdateText(string text);

    Task<IReadOnlyList<LspCompletionItem>> RequestCompletionAsync(int line, int character, CancellationToken ct = default);
    Task<LspHover?> RequestHoverAsync(int line, int character);
    Task<(string Uri, int Line, int Column)?> RequestDefinitionAsync(int line, int character);
    Task<LspSignatureHelp?> RequestSignatureHelpAsync(int line, int character, CancellationToken ct = default);
    Task<LspWorkspaceEdit?> RequestRenameAsync(int line, int character, string newName);
    Task<IReadOnlyList<LspLocation>> RequestReferencesAsync(int line, int character);
    Task<IReadOnlyList<LspCodeAction>> RequestCodeActionsAsync(int line, int character);

    /// <summary>範囲を指定して code action を取得する。「メソッドの抽出」のように
    /// <b>選択範囲そのものが対象</b>のリファクタリングは、キャレット1点では候補が出ない。
    /// <paramref name="only"/> は <c>context.only</c>（<see cref="LspCodeActionKinds"/>）。
    /// 既定実装は範囲を捨てて位置版へ落ちる（未対応ホスト向けの劣化動作）。</summary>
    Task<IReadOnlyList<LspCodeAction>> RequestCodeActionsAsync(
        LspRange range, IReadOnlyList<string>? only, CancellationToken ct = default)
        => RequestCodeActionsAsync(range.Start.Line, range.Start.Character);

    /// <summary>未解決の code action を確定させる。解決できなければ null（呼び出し側は元のまま使う）。</summary>
    Task<LspCodeAction?> ResolveCodeActionAsync(LspCodeAction action, CancellationToken ct = default)
        => Task.FromResult<LspCodeAction?>(null);

    /// <summary>コマンド型 code action を実行する。編集はサーバー起点の <c>workspace/applyEdit</c> で返る。</summary>
    Task<bool> ExecuteCommandAsync(LspCodeActionCommand command, CancellationToken ct = default)
        => Task.FromResult(false);
    Task<IReadOnlyList<LspTextEdit>> RequestFormattingAsync(int tabSize, bool insertSpaces);
    Task<IReadOnlyList<LspTextEdit>> RequestRangeFormattingAsync(LspRange range, int tabSize, bool insertSpaces);
    Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsAsync();
    Task<IReadOnlyList<LspFoldingRange>> RequestFoldingRangesAsync();
    Task<IReadOnlyList<InlayHint>> RequestInlayHintsAsync(int startLine, int endLine);
    Task<SemanticToken[]?> RequestSemanticTokensAsync();
    Task<IReadOnlyList<DocumentHighlight>?> RequestDocumentHighlightAsync(int line, int character, CancellationToken ct = default);
    Task<LspSelectionRange?> RequestSelectionRangeAsync(int line, int character);

    /// <summary>New diagnostics for this URI. <b>Fires on a background thread.</b></summary>
    event Action<IReadOnlyList<LspDiagnostic>>? DiagnosticsChanged;

    /// <summary><see cref="IsConnected"/>/<see cref="IsReady"/> changed (initial open, crash, reconnect).
    /// <b>Fires on a background thread.</b></summary>
    event Action? StateChanged;

    /// <summary>Human-readable progress for the status bar ("LSP: ready", init failures, reconnects).
    /// <b>Fires on a background thread.</b></summary>
    event Action<string>? StatusMessage;
}

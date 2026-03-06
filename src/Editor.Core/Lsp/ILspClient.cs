namespace Editor.Core.Lsp;

public class DiagnosticsChangedEventArgs(string uri, IReadOnlyList<LspDiagnostic> diagnostics) : EventArgs
{
    public string Uri { get; } = uri;
    public IReadOnlyList<LspDiagnostic> Diagnostics { get; } = diagnostics;
}

public interface ILspClient : IDisposable
{
    bool IsRunning { get; }
    /// <summary>サーバーが textDocument/foldingRange をサポートしているか。InitializeAsync 後に確定する。</summary>
    bool SupportsFoldingRange { get; }
    /// <summary>サーバーが workspace/symbol をサポートしているか。InitializeAsync 後に確定する。</summary>
    bool SupportsWorkspaceSymbol { get; }
    event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;

    Task InitializeAsync(string rootUri);
    Task OpenDocumentAsync(string uri, string languageId, string text);
    Task ChangeDocumentAsync(string uri, int version, string text);
    Task CloseDocumentAsync(string uri);
    Task<IReadOnlyList<LspCompletionItem>> GetCompletionAsync(string uri, LspPosition position, CancellationToken ct = default);
    Task<LspHover?> GetHoverAsync(string uri, LspPosition position, CancellationToken ct = default);
    Task<(string Uri, int Line, int Column)?> GetDefinitionAsync(string uri, LspPosition position, CancellationToken ct = default);
    Task<LspSignatureHelp?> GetSignatureHelpAsync(string uri, LspPosition position, CancellationToken ct = default);
    Task<IReadOnlyList<LspTextEdit>> GetFormattingEditsAsync(string uri, int tabSize, bool insertSpaces, CancellationToken ct = default);
    Task<LspWorkspaceEdit?> GetRenameAsync(string uri, LspPosition position, string newName, CancellationToken ct = default);
    Task<IReadOnlyList<LspLocation>> GetReferencesAsync(string uri, LspPosition position, bool includeDeclaration = true, CancellationToken ct = default);
    Task<IReadOnlyList<LspFoldingRange>> GetFoldingRangesAsync(string uri, CancellationToken ct = default);
    Task<IReadOnlyList<LspSymbolInformation>> GetWorkspaceSymbolsAsync(string query, CancellationToken ct = default);
    Task<IReadOnlyList<DocumentSymbol>> GetDocumentSymbolsAsync(string uri, CancellationToken ct = default);
    Task<IReadOnlyList<LspCodeAction>> GetCodeActionsAsync(string uri, LspRange range, CancellationToken ct = default);
}

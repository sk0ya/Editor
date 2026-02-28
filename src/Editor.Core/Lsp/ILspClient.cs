namespace Editor.Core.Lsp;

public class DiagnosticsChangedEventArgs(string uri, IReadOnlyList<LspDiagnostic> diagnostics) : EventArgs
{
    public string Uri { get; } = uri;
    public IReadOnlyList<LspDiagnostic> Diagnostics { get; } = diagnostics;
}

public interface ILspClient : IDisposable
{
    bool IsRunning { get; }
    event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;

    Task InitializeAsync(string rootUri);
    Task OpenDocumentAsync(string uri, string languageId, string text);
    Task ChangeDocumentAsync(string uri, int version, string text);
    Task CloseDocumentAsync(string uri);
    Task<IReadOnlyList<LspCompletionItem>> GetCompletionAsync(string uri, LspPosition position, CancellationToken ct = default);
    Task<LspHover?> GetHoverAsync(string uri, LspPosition position, CancellationToken ct = default);
    Task<string?> GetDefinitionUriAsync(string uri, LspPosition position, CancellationToken ct = default);
    Task<LspSignatureHelp?> GetSignatureHelpAsync(string uri, LspPosition position, CancellationToken ct = default);
    Task<IReadOnlyList<LspTextEdit>> GetFormattingEditsAsync(string uri, int tabSize, bool insertSpaces, CancellationToken ct = default);
}

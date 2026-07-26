using System.Threading;

namespace Editor.Core.Lsp;

/// <summary>
/// Owns the LSP <b>session</b> for a workspace: language-server processes (pooled per
/// executable × workspace root), <c>initialize</c>/capability negotiation, document
/// synchronization with reference counting, and every request whose scope is the workspace
/// rather than one buffer. The host implements this; editor controls only consume it.
///
/// <para>Because it is workspace-scoped rather than tab-scoped, consumers that are not editors
/// (a search pane, an outline, a problems list, an agent tool) can query it directly, and the
/// answers do not depend on which files happen to be open.</para>
///
/// <para><b>Threading:</b> the implementation is thread-safe, and <b>all events on this interface and on
/// <see cref="ILspDocument"/> fire on a background thread</b> (the JSON-RPC read loop). Subscribers that
/// touch UI state must marshal to their own dispatcher themselves. Breaking that contract produces the
/// "diagnostics arrive but no squiggles / occasional crash" class of bug.</para>
/// </summary>
public interface ILspWorkspace
{
    /// <summary>
    /// Open (or join) the document for <paramref name="filePath"/>, starting the language server for
    /// its extension if this is the first document needing it. Returns null when no server is
    /// configured for the extension or the process could not be started.
    /// </summary>
    ILspDocument? OpenDocument(string filePath, string initialText);

    /// <summary>True when a language server is configured for <paramref name="extension"/> (e.g. ".cs").</summary>
    bool IsServerAvailableFor(string extension);

    /// <summary>
    /// Search symbols across the workspace, merged and de-duplicated over every running server.
    /// Starts the servers implied by the workspace roots when none are running yet, so this works
    /// with no editor tab open.
    /// </summary>
    Task<IReadOnlyList<LspSymbolInformation>> GetWorkspaceSymbolsAsync(string query, bool isClass, CancellationToken ct = default);

    /// <summary>Pull workspace-wide diagnostics from every server that supports <c>workspace/diagnostic</c>.</summary>
    Task<LspWorkspaceDiagnosticResult?> RequestWorkspaceDiagnosticsAsync(CancellationToken ct = default);

    Task<CallHierarchyItem?> PrepareCallHierarchyAsync(string uri, int line, int character);
    Task<CallHierarchyIncomingCall[]?> GetIncomingCallsAsync(CallHierarchyItem item);
    Task<CallHierarchyOutgoingCall[]?> GetOutgoingCallsAsync(CallHierarchyItem item);
    Task<TypeHierarchyItem?> PrepareTypeHierarchyAsync(string uri, int line, int character);
    Task<TypeHierarchyItem[]?> GetSupertypesAsync(TypeHierarchyItem item);
    Task<TypeHierarchyItem[]?> GetSubtypesAsync(TypeHierarchyItem item);

    /// <summary>Diagnostics for any URI, whether or not a view has it open. <b>Fires on a background thread.</b></summary>
    event Action<string, IReadOnlyList<LspDiagnostic>>? DiagnosticsPublished;

    /// <summary>A server started, died, or finished initializing. <b>Fires on a background thread.</b></summary>
    event Action? ServerStateChanged;
}

/// <summary>
/// Write access to the extension→language-server table, injected by the host so the editor's
/// <c>:LspAdd</c>/<c>:LspRemove</c>/<c>:LspList</c>/<c>:LspReset</c> ex commands act on the
/// <b>same</b> table the host's settings UI and its <see cref="ILspWorkspace"/> read from.
/// When no host injects one, those commands report that they are unavailable.
/// </summary>
public interface ILspServerAdmin
{
    /// <summary>The effective table (built-ins merged with user changes), sorted by extension.</summary>
    IReadOnlyList<LspServerEntry> List();

    /// <summary>The server for an extension (e.g. ".cs"), or null when none is configured.</summary>
    LspServerDef? GetForExtension(string extension);

    /// <summary>Add or replace the server for an extension, persisting the change.</summary>
    void Set(string extension, LspServerDef def);

    /// <summary>Drop a custom mapping, or hide a built-in one. True when something changed.</summary>
    bool Remove(string extension);

    /// <summary>Discard user changes for an extension, restoring the built-in default. True when something changed.</summary>
    bool Reset(string extension);
}

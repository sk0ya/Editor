using Editor.Core.Formatting;
using Editor.Core.Lsp;
using Editor.Core.Models;

namespace Editor.Core.Engine.ExCommands;

/// <summary>
/// Handles LSP-triggered ex commands (:Format, :Rename, :QuickFix, :CodeAction, :Symbols, :CallHierarchy,
/// :TypeHierarchy) and the :Lsp*/:Fmt* server/formatter configuration commands.
///
/// <para>The <c>:Lsp*</c> commands are only an input frontend: they edit whatever extension→server
/// table the host injected as <paramref name="lspServerAdmin"/> — the same one the host's settings UI
/// and its <see cref="ILspWorkspace"/> use. With no host table injected they report that they are
/// unavailable, rather than writing to a private table nobody reads.</para>
/// </summary>
public class LspCommands(ILspServerAdmin? lspServerAdmin, FormatterRegistry formatterRegistry)
{
    private const string NoAdmin = "LSP: server configuration is not available in this host";

    /// <param name="formatRange">
    /// Resolved 0-based inclusive line range of the ex range prefix, or null when the command had none.
    /// Only <c>:Format</c> uses it — `:'&lt;,'&gt;Format` formats just the selected lines.
    /// </param>
    public bool TryHandle(string cmd, out ExResult result, (int Start, int End)? formatRange = null)
    {
        // :Format / :{range}Format — format the document (or just the range) via LSP
        if (cmd is "Format" or "format")
        {
            result = new ExResult(true, null,
                VimEvent.FormatDocumentRequested(formatRange?.Start, formatRange?.End));
            return true;
        }

        // :Rename [newname] — LSP workspace rename
        if (cmd.Equals("Rename", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("Rename ", StringComparison.OrdinalIgnoreCase))
        {
            var newName = cmd.Length > 7 ? cmd[7..].Trim() : null;
            if (string.IsNullOrEmpty(newName)) newName = null;
            result = new ExResult(true, null, VimEvent.LspRenameRequested(newName));
            return true;
        }

        // :QuickFix / :CodeAction — offer actions in the same popup, but keep
        // diagnostic-only Quick Fix distinct so hosts can use their fallback provider.
        // Exposed as an ex command so a host can reach it without synthesizing keystrokes
        // (e.g. from its own context menu), which `ga` cannot do in Insert mode or with Vim off.
        // The popup itself is navigable in those states too (↑/↓/Enter/Escape — j/k only where a
        // bare letter isn't text input); see the guard in VimEditorControl.OnPreviewKeyDown.
        if (cmd.Equals("QuickFix", StringComparison.OrdinalIgnoreCase))
        {
            result = new ExResult(true, null, VimEvent.QuickFixRequested());
            return true;
        }
        if (cmd.Equals("CodeAction", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("CodeActions", StringComparison.OrdinalIgnoreCase))
        {
            result = new ExResult(true, null, VimEvent.CodeActionRequested());
            return true;
        }

        // :FixAll — request source.fixAll for the entire current document.
        if (cmd.Equals("FixAll", StringComparison.OrdinalIgnoreCase))
        {
            result = new ExResult(true, null, VimEvent.FixAllRequested());
            return true;
        }

        // :Symbols [query] / :sym [query] / :outline — list document or workspace symbols
        // With a query argument: workspace/symbol search. Without: document symbols outline.
        if (cmd.StartsWith("Symbols", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("sym", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("outline", StringComparison.OrdinalIgnoreCase))
        {
            var spaceIdx = cmd.IndexOf(' ');
            var query = spaceIdx >= 0 ? cmd[(spaceIdx + 1)..].Trim() : null;
            if (string.IsNullOrEmpty(query)) query = null;
            result = new ExResult(true, null, VimEvent.SymbolsRequested(query));
            return true;
        }

        // :CallHierarchy — LSP call hierarchy for symbol under cursor
        if (cmd.Equals("CallHierarchy", StringComparison.OrdinalIgnoreCase))
        {
            result = new ExResult(true, null, VimEvent.CallHierarchyRequested());
            return true;
        }

        // :TypeHierarchy — LSP type hierarchy for symbol under cursor
        if (cmd.Equals("TypeHierarchy", StringComparison.OrdinalIgnoreCase))
        {
            result = new ExResult(true, null, VimEvent.TypeHierarchyRequested());
            return true;
        }

        // :diagnostics — request workspace diagnostics via LSP
        if (cmd.Equals("diagnostics", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("diag", StringComparison.OrdinalIgnoreCase))
        {
            result = new ExResult(true, null, VimEvent.WorkspaceDiagnosticsRequested());
            return true;
        }

        // :Lsp / :LspList — show the configured extension→language-server table
        if (cmd.Equals("Lsp", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("LspList", StringComparison.OrdinalIgnoreCase))
        {
            result = new ExResult(true, FormatLspServers());
            return true;
        }

        // :LspAdd <ext> <executable> [args...] — register/replace the server for an extension
        if (cmd.StartsWith("LspAdd ", StringComparison.OrdinalIgnoreCase))
        {
            result = ExecuteLspAdd(cmd[7..]);
            return true;
        }

        // :LspRemove <ext> — remove a custom server, or hide a built-in one
        if (cmd.StartsWith("LspRemove ", StringComparison.OrdinalIgnoreCase))
        {
            result = ExecuteLspRemove(cmd[10..]);
            return true;
        }

        // :LspReset <ext> — drop user changes for an extension, restoring the built-in default
        if (cmd.StartsWith("LspReset ", StringComparison.OrdinalIgnoreCase))
        {
            result = ExecuteLspReset(cmd[9..]);
            return true;
        }

        // :Fmt / :FmtList — show the configured extension→CLI-formatter table
        if (cmd.Equals("Fmt", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("FmtList", StringComparison.OrdinalIgnoreCase))
        {
            result = new ExResult(true, FormatFormatters());
            return true;
        }

        // :FmtSet <ext> <executable> [args...] — register/replace the CLI formatter for an extension
        if (cmd.StartsWith("FmtSet ", StringComparison.OrdinalIgnoreCase))
        {
            result = ExecuteFmtSet(cmd[7..]);
            return true;
        }

        // :FmtRemove <ext> — drop the configured formatter for an extension
        if (cmd.StartsWith("FmtRemove ", StringComparison.OrdinalIgnoreCase))
        {
            result = ExecuteFmtRemove(cmd[10..]);
            return true;
        }

        result = default!;
        return false;
    }

    private string FormatLspServers()
    {
        if (lspServerAdmin is null) return NoAdmin;

        var entries = lspServerAdmin.List();
        if (entries.Count == 0)
            return "(no language servers configured)";

        var lines = entries.Select(e =>
        {
            var args = e.Server.Args.Length > 0 ? " " + string.Join(' ', e.Server.Args) : "";
            var origin = e.Origin switch
            {
                LspServerOrigin.Custom   => "custom",
                LspServerOrigin.Removed  => "removed",
                _                        => "built-in",
            };
            // A hidden built-in is shown struck so the user can see what :LspReset would restore.
            var cmd = e.Origin == LspServerOrigin.Removed ? $"(disabled: {e.Server.Executable}{args})" : $"{e.Server.Executable}{args}";
            return $"  {e.Extension,-10} {cmd}  [{origin}]";
        });
        return "LSP servers (extension → command):\n" + string.Join('\n', lines);
    }

    private ExResult ExecuteLspAdd(string rest)
    {
        if (lspServerAdmin is null) return new ExResult(false, NoAdmin);

        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return new ExResult(false, "Usage: :LspAdd <ext> <executable> [args...]");

        var ext = LspExtensions.NormalizeExt(parts[0]);
        if (ext.Length == 0)
            return new ExResult(false, "E474: invalid extension");

        var executable = parts[1];
        var args = parts.Length > 2 ? parts[2..] : [];
        // languageId is derived from the extension (".cs" → "cs"); servers key off the executable, not this id.
        var languageId = ext.TrimStart('.');
        lspServerAdmin.Set(ext, new LspServerDef(executable, args, languageId));

        var argsText = args.Length > 0 ? " " + string.Join(' ', args) : "";
        return new ExResult(true, $"LSP: {ext} → {executable}{argsText}");
    }

    private ExResult ExecuteLspRemove(string rest)
    {
        if (lspServerAdmin is null) return new ExResult(false, NoAdmin);

        var ext = LspExtensions.NormalizeExt(rest.Trim());
        if (ext.Length == 0)
            return new ExResult(false, "Usage: :LspRemove <ext>");

        return lspServerAdmin.Remove(ext)
            ? new ExResult(true, $"LSP: removed {ext}")
            : new ExResult(false, $"LSP: no server configured for {ext}");
    }

    private ExResult ExecuteLspReset(string rest)
    {
        if (lspServerAdmin is null) return new ExResult(false, NoAdmin);

        var ext = LspExtensions.NormalizeExt(rest.Trim());
        if (ext.Length == 0)
            return new ExResult(false, "Usage: :LspReset <ext>");

        return lspServerAdmin.Reset(ext)
            ? new ExResult(true, $"LSP: reset {ext} to its built-in default")
            : new ExResult(false, $"LSP: nothing to reset for {ext}");
    }

    private string FormatFormatters()
    {
        var entries = formatterRegistry.List();
        if (entries.Count == 0)
            return "(no formatters configured) — :FmtSet <ext> <cmd>, or run :Format to auto-detect";

        var lines = entries.Select(e =>
        {
            var args = e.Def.Args.Length > 0 ? " " + string.Join(' ', e.Def.Args) : "";
            return $"  {e.Extension,-10} {e.Def.Executable}{args}";
        });
        return "Formatters (extension → command, stdin→stdout):\n" + string.Join('\n', lines);
    }

    private ExResult ExecuteFmtSet(string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return new ExResult(false, "Usage: :FmtSet <ext> <executable> [args...]  (use {file} for the path)");

        var ext = FormatterRegistry.NormalizeExt(parts[0]);
        if (ext.Length == 0)
            return new ExResult(false, "E474: invalid extension");

        var executable = parts[1];
        var args = parts.Length > 2 ? parts[2..] : [];
        formatterRegistry.Set(ext, new FormatterDef(executable, args));

        var argsText = args.Length > 0 ? " " + string.Join(' ', args) : "";
        return new ExResult(true, $"Format: {ext} → {executable}{argsText}");
    }

    private ExResult ExecuteFmtRemove(string rest)
    {
        var ext = FormatterRegistry.NormalizeExt(rest.Trim());
        if (ext.Length == 0)
            return new ExResult(false, "Usage: :FmtRemove <ext>");

        return formatterRegistry.Remove(ext)
            ? new ExResult(true, $"Format: removed {ext}")
            : new ExResult(false, $"Format: no formatter configured for {ext}");
    }
}

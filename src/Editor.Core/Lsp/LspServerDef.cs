namespace Editor.Core.Lsp;

/// <summary>Maps a file extension to a language server executable and its arguments.</summary>
public record LspServerDef(string Executable, string[] Args, string LanguageId);

/// <summary>Where an effective mapping came from: a built-in default, a user override, or a hidden built-in.</summary>
public enum LspServerOrigin { BuiltIn, Custom, Removed }

/// <summary>One row in the effective extension→server table, with its origin for display.</summary>
public record LspServerEntry(string Extension, LspServerDef Server, LspServerOrigin Origin);

/// <summary>Extension-string normalization shared by every owner of an extension→server table
/// (".CS" → ".cs", "cs" → ".cs"). Kept here rather than on the table itself so hosts, ex commands
/// and tests all normalize identically without depending on a particular table implementation.</summary>
public static class LspExtensions
{
    /// <summary>Normalize a user-supplied extension to a leading-dot, lower-invariant form.</summary>
    public static string NormalizeExt(string? extension)
    {
        var ext = extension?.Trim() ?? "";
        if (ext.Length == 0) return "";
        if (ext[0] != '.') ext = "." + ext;
        return ext.ToLowerInvariant();
    }
}

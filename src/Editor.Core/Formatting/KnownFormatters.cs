namespace Editor.Core.Formatting;

/// <summary>
/// A known CLI formatter the editor can *suggest* for an extension. These are never auto-applied —
/// when <c>:Format</c> finds no configured formatter and LSP can't format either, the editor probes
/// the user's PATH for these and offers (or, when exactly one is installed, registers and uses) it.
/// All entries follow the stdin→stdout convention; <c>{file}</c> is replaced with the current path.
/// </summary>
public record FormatterCandidate(string Executable, string[] Args, string DisplayName, string? DocsUrl = null);

/// <summary>The built-in suggestion catalog keyed by extension. The active table is <see cref="FormatterRegistry"/>.</summary>
public static class KnownFormatters
{
    // prettier reads from stdin and needs --stdin-filepath to choose a parser; dprint takes the path positionally.
    private static FormatterCandidate Prettier(params string[] _) =>
        new("prettier", ["--stdin-filepath", "{file}"], "Prettier", "https://prettier.io/");
    private static readonly FormatterCandidate Dprint =
        new("dprint", ["fmt", "--stdin", "{file}"], "dprint", "https://dprint.dev/");

    private static readonly IReadOnlyDictionary<string, FormatterCandidate[]> Catalog =
        new Dictionary<string, FormatterCandidate[]>(StringComparer.OrdinalIgnoreCase)
        {
            [".md"]       = [Prettier(), Dprint],
            [".markdown"] = [Prettier(), Dprint],
            [".json"]     = [Prettier(), Dprint],
            [".jsonc"]    = [Prettier(), Dprint],
            [".yaml"]     = [Prettier()],
            [".yml"]      = [Prettier()],
            [".html"]     = [Prettier()],
            [".css"]      = [Prettier()],
            [".scss"]     = [Prettier()],
            [".js"]       = [Prettier(), Dprint],
            [".jsx"]      = [Prettier(), Dprint],
            [".ts"]       = [Prettier(), Dprint],
            [".tsx"]      = [Prettier(), Dprint],
            [".py"]       = [new("black", ["-q", "-"], "Black", "https://black.readthedocs.io/"),
                             new("ruff", ["format", "-"], "Ruff", "https://docs.astral.sh/ruff/")],
            [".rs"]       = [new("rustfmt", [], "rustfmt", "https://github.com/rust-lang/rustfmt")],
            [".go"]       = [new("gofmt", [], "gofmt", "https://pkg.go.dev/cmd/gofmt")],
            [".lua"]      = [new("stylua", ["-"], "StyLua", "https://github.com/JohnnyMorganz/StyLua")],
        };

    /// <summary>Suggested formatters for an extension (e.g. ".md"), best first. Empty when none are known.</summary>
    public static IReadOnlyList<FormatterCandidate> ForExtension(string extension)
    {
        var ext = FormatterRegistry.NormalizeExt(extension);
        return ext.Length > 0 && Catalog.TryGetValue(ext, out var list) ? list : [];
    }
}

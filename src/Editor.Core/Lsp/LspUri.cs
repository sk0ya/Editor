using System;
using System.Collections.Generic;
using System.IO;

namespace Editor.Core.Lsp;

/// <summary>
/// The one place that turns a server-supplied <c>file:</c> URI into a local path (and back into a
/// canonical URI). Every URI that arrives from a language server must go through here.
///
/// <para>Why this exists: servers disagree on how a Windows drive letter is spelled. Roslyn sends
/// <c>file:///c:/work/a.cs</c>, but anything built on <c>vscode-uri</c> (typescript-language-server,
/// and VS Code itself) percent-encodes the colon: <c>file:///c%3A/work/a.ts</c>. .NET does not
/// recognize the encoded form as a DOS path, so <c>new Uri(uri).LocalPath</c> silently yields
/// <c>/c:/work/a.ts</c> — a string that matches no open document and that
/// <c>Path.GetFullPath</c> happily turns into <c>C:\c:\work\a.ts</c>. That is why rename, go-to
/// definition, find-references and push diagnostics all looked broken for TypeScript while
/// completion and hover (which only echo back the URI we sent) worked.</para>
/// </summary>
public static class LspUri
{
    /// <summary>Comparer for URI keys. File systems we target are case-insensitive, and servers
    /// differ on drive-letter case (<c>file:///c:/…</c> vs <c>file:///C:/…</c>), so URIs must never
    /// be matched with the default ordinal comparer.</summary>
    public static readonly IEqualityComparer<string> Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Canonical form of a server-supplied URI: file URIs come back as <c>file:///C:/work/a.ts</c>
    /// (colon unencoded), anything else is returned unchanged.
    /// </summary>
    public static string Normalize(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return uri ?? "";
        var localPath = TryToLocalPath(uri);
        // ドライブもホストも無い形（"file:///a.cs" 等、テストや疑似 URI）は書き換えない。
        if (localPath is null || !Path.IsPathFullyQualified(localPath)) return uri;
        try { return new Uri(localPath).AbsoluteUri; }
        catch { return uri; }
    }

    /// <summary>Local path for a <c>file:</c> URI, or null when it is not one (or is unparseable).</summary>
    public static string? TryToLocalPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return null;
        var localPath = RepairDrivePrefix(parsed.LocalPath);
        return localPath.Length == 0 ? null : localPath;
    }

    /// <summary>Local path for a <c>file:</c> URI, falling back to the URI text itself (for display).</summary>
    public static string ToLocalPathOrOriginal(string? uri) => TryToLocalPath(uri) ?? uri ?? "";

    /// <summary>Do two URIs point at the same file? Compares local paths when both are file URIs.</summary>
    public static bool SamePath(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var pa = TryToLocalPath(a);
        var pb = TryToLocalPath(b);
        if (pa is null || pb is null) return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return string.Equals(FullPathOrSelf(pa), FullPathOrSelf(pb), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Is <paramref name="path"/> the file the URI refers to?</summary>
    public static bool MatchesPath(string? uri, string? path)
    {
        var uriPath = TryToLocalPath(uri);
        if (uriPath is null || string.IsNullOrEmpty(path)) return false;
        return string.Equals(FullPathOrSelf(uriPath), FullPathOrSelf(path), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Canonical URI for a local path — the form we send to servers.</summary>
    public static string FromPath(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private static string FullPathOrSelf(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    /// <summary>
    /// <c>"/c:/work/a.ts"</c> → <c>"c:\work\a.ts"</c>. That leading slash is what .NET leaves behind
    /// when the drive colon arrived percent-encoded; every other shape is returned untouched.
    /// </summary>
    private static string RepairDrivePrefix(string localPath)
    {
        if (localPath.Length < 3) return localPath;
        if (localPath[0] is not ('/' or '\\')) return localPath;
        if (!char.IsAsciiLetter(localPath[1]) || localPath[2] != ':') return localPath;
        if (localPath.Length > 3 && localPath[3] is not ('/' or '\\')) return localPath;
        return localPath[1..].Replace('/', Path.DirectorySeparatorChar);
    }
}

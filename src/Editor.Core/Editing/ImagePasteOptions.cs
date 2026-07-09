namespace Editor.Core.Editing;

/// <summary>
/// The resolved target for a pasted clipboard image.
/// <see cref="AbsolutePath"/> is where the image bytes should be written on disk;
/// <see cref="LinkPath"/> is the (forward-slash) path to embed in the Markdown link,
/// relative to the Markdown file's own directory; <see cref="AltText"/> seeds the
/// <c>![alt]</c> portion of the link.
/// </summary>
public sealed record ImagePasteTarget(string AbsolutePath, string LinkPath, string AltText);

/// <summary>
/// Pure configuration + path resolution for pasting a clipboard image into a Markdown
/// document. Holds the user-configurable rules (destination directory and file-name
/// templates) and, given a Markdown file path + timestamp, computes where the image
/// should be saved and how the link should be written. No WPF / clipboard / file I/O
/// here — the WPF host reads the clipboard, calls <see cref="Resolve"/>, then writes the
/// bytes and inserts the link text.
///
/// Template placeholders (case-insensitive):
///   {filename} — the Markdown file's name without extension (or "image" when unsaved)
///   {date}     — yyyyMMdd
///   {time}     — HHmmss
///   {datetime} — yyyyMMdd-HHmmss
///   {seq}      — a 1-based counter, incremented until the target file name is unique
/// The <see cref="Directory"/> template additionally understands nothing else and is
/// interpreted relative to the Markdown file's directory. Both templates use '/' as the
/// separator regardless of platform.
/// </summary>
public sealed class ImagePasteOptions
{
    /// <summary>
    /// Destination directory for saved images, relative to the Markdown file's own
    /// directory. Default: <c>images</c>. Supports the {filename}/{date}/{time}/{datetime}
    /// placeholders (e.g. <c>assets/{filename}</c>).
    /// </summary>
    public string Directory { get; set; } = "images";

    /// <summary>
    /// File name (including extension) for the saved image. Default:
    /// <c>{filename}-{datetime}.png</c>. Supports the
    /// {filename}/{date}/{time}/{datetime}/{seq} placeholders. If the resolved name
    /// already exists on disk and no {seq} placeholder is present, a <c>-N</c> suffix is
    /// appended to keep it unique.
    /// </summary>
    public string FileName { get; set; } = "{filename}-{datetime}.png";

    /// <summary>Alt text placed inside <c>![...]</c>. Defaults to the image file stem when empty.</summary>
    public string AltText { get; set; } = "";

    /// <summary>
    /// Resolves the save location + link for an image pasted into <paramref name="markdownFilePath"/>.
    /// <paramref name="fileExists"/> probes the filesystem so the {seq} counter (or the
    /// fallback <c>-N</c> suffix) can skip names that are already taken; pass a predicate
    /// that always returns false to disable uniqueness checks.
    /// </summary>
    public ImagePasteTarget Resolve(string? markdownFilePath, DateTime timestamp, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        var baseDir = string.IsNullOrEmpty(markdownFilePath)
            ? System.IO.Directory.GetCurrentDirectory()
            : (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(markdownFilePath)) ?? ".");

        var stem = string.IsNullOrEmpty(markdownFilePath)
            ? "image"
            : System.IO.Path.GetFileNameWithoutExtension(markdownFilePath);
        if (string.IsNullOrEmpty(stem)) stem = "image";

        var relDir = ExpandCommon(Directory.Trim(), stem, timestamp).Replace('\\', '/').Trim('/');
        var targetDir = string.IsNullOrEmpty(relDir)
            ? baseDir
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, relDir.Replace('/', System.IO.Path.DirectorySeparatorChar)));

        var fileName = ResolveFileName(FileName.Trim(), stem, timestamp, targetDir, fileExists);
        var absolutePath = System.IO.Path.Combine(targetDir, fileName);

        var linkPath = ToRelativeLink(baseDir, absolutePath);
        var alt = string.IsNullOrEmpty(AltText)
            ? System.IO.Path.GetFileNameWithoutExtension(fileName)
            : ExpandCommon(AltText, stem, timestamp);

        return new ImagePasteTarget(absolutePath, linkPath, alt);
    }

    private static string ResolveFileName(string template, string stem, DateTime ts, string targetDir, Func<string, bool> fileExists)
    {
        if (string.IsNullOrEmpty(template)) template = "{filename}-{datetime}.png";

        bool hasSeq = template.Contains("{seq}", StringComparison.OrdinalIgnoreCase);
        if (hasSeq)
        {
            for (int seq = 1; ; seq++)
            {
                var name = Sanitize(ReplaceSeq(ExpandCommon(template, stem, ts), seq));
                if (!fileExists(System.IO.Path.Combine(targetDir, name))) return name;
            }
        }

        var baseName = Sanitize(ExpandCommon(template, stem, ts));
        if (!fileExists(System.IO.Path.Combine(targetDir, baseName))) return baseName;

        // No {seq} placeholder but the name is taken — append -N before the extension.
        var ext = System.IO.Path.GetExtension(baseName);
        var withoutExt = System.IO.Path.GetFileNameWithoutExtension(baseName);
        for (int n = 1; ; n++)
        {
            var name = $"{withoutExt}-{n}{ext}";
            if (!fileExists(System.IO.Path.Combine(targetDir, name))) return name;
        }
    }

    private static string ExpandCommon(string template, string stem, DateTime ts) =>
        template
            .Replace("{filename}", stem, StringComparison.OrdinalIgnoreCase)
            .Replace("{datetime}", ts.ToString("yyyyMMdd-HHmmss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", ts.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", ts.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase);

    private static string ReplaceSeq(string s, int seq) =>
        s.Replace("{seq}", seq.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Strips characters that are invalid in a file name (keeps the extension dot).</summary>
    private static string Sanitize(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    /// <summary>Path from <paramref name="baseDir"/> to <paramref name="target"/>, forward-slashed.</summary>
    private static string ToRelativeLink(string baseDir, string target)
    {
        var rel = System.IO.Path.GetRelativePath(baseDir, target).Replace('\\', '/');
        // Keep same-directory links clean but do not prepend "./" — Markdown handles both.
        return rel;
    }
}

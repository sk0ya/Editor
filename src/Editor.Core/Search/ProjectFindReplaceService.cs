using System.Text;
using System.Text.RegularExpressions;

namespace Editor.Core.Search;

public sealed record ProjectSearchOptions(
    string RootDirectory,
    string Pattern,
    string? FileGlob = null,
    bool IgnoreCase = false,
    string? CurrentFilePath = null,
    long MaxFileBytes = 5_000_000,
    IReadOnlySet<string>? ExcludedDirectoryNames = null);

public sealed record ProjectSearchMatch(
    string FilePath,
    int Line,
    int Column,
    int Length,
    string LineText);

public sealed record ProjectReplaceResult(
    int MatchCount,
    int FileCount,
    IReadOnlyList<string> UpdatedFiles,
    IReadOnlyList<string> SkippedFiles,
    IReadOnlyList<string> Errors);

public static class ProjectFindReplaceService
{
    public static readonly IReadOnlySet<string> DefaultExcludedDirectoryNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "bin", "obj", ".vs", ".idea", ".vscode",
            "node_modules", "__pycache__", "dist", "build", "out",
            "target", "packages", ".nuget", ".gradle", "vendor",
        };

    public static IReadOnlyList<ProjectSearchMatch> Find(ProjectSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var regex = CreateRegex(options);
        var matches = new List<ProjectSearchMatch>();

        foreach (var filePath in EnumerateCandidateFiles(options))
        {
            if (!CanReadAsText(filePath, options.MaxFileBytes))
                continue;

            try
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(filePath))
                {
                    foreach (Match match in regex.Matches(line))
                    {
                        if (!match.Success)
                            continue;

                        matches.Add(new ProjectSearchMatch(
                            filePath,
                            lineNumber,
                            match.Index,
                            match.Length,
                            line));
                    }
                    lineNumber++;
                }
            }
            catch
            {
                // The caller gets best-effort project search results; unreadable files are skipped.
            }
        }

        return matches;
    }

    public static ProjectReplaceResult Replace(ProjectSearchOptions options, string replacement)
    {
        ArgumentNullException.ThrowIfNull(options);
        replacement ??= "";

        var regex = CreateRegex(options);
        var updatedFiles = new List<string>();
        var skippedFiles = new List<string>();
        var errors = new List<string>();
        var matchCount = 0;

        foreach (var filePath in EnumerateCandidateFiles(options))
        {
            if (!CanReadAsText(filePath, options.MaxFileBytes))
            {
                skippedFiles.Add(filePath);
                continue;
            }

            try
            {
                var encoding = DetectEncoding(filePath, out var text);
                var replaced = regex.Replace(text, match =>
                {
                    var replacementText = match.Result(replacement);
                    matchCount++;
                    return replacementText;
                });

                if (!string.Equals(text, replaced, StringComparison.Ordinal))
                {
                    File.WriteAllText(filePath, replaced, encoding);
                    updatedFiles.Add(filePath);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{filePath}: {ex.Message}");
            }
        }

        return new ProjectReplaceResult(
            matchCount,
            updatedFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            updatedFiles,
            skippedFiles,
            errors);
    }

    private static Regex CreateRegex(ProjectSearchOptions options)
    {
        var regexOptions = RegexOptions.CultureInvariant;
        if (options.IgnoreCase)
            regexOptions |= RegexOptions.IgnoreCase;
        return new Regex(options.Pattern, regexOptions);
    }

    private static IEnumerable<string> EnumerateCandidateFiles(ProjectSearchOptions options)
    {
        if (options.FileGlob == "%")
        {
            if (!string.IsNullOrWhiteSpace(options.CurrentFilePath) && File.Exists(options.CurrentFilePath))
                yield return Path.GetFullPath(options.CurrentFilePath);
            yield break;
        }

        var root = Path.GetFullPath(options.RootDirectory);
        if (!Directory.Exists(root))
            yield break;

        var excluded = options.ExcludedDirectoryNames ?? DefaultExcludedDirectoryNames;
        var extensions = GetExtensionsFromGlob(options.FileGlob);
        var dirs = new Stack<string>();
        dirs.Push(root);

        while (dirs.Count > 0)
        {
            var dir = dirs.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var file in files)
            {
                if (extensions != null &&
                    !extensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    continue;

                yield return file;
            }

            IEnumerable<string> subDirs;
            try { subDirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }

            foreach (var subDir in subDirs)
            {
                if (!excluded.Contains(Path.GetFileName(subDir)))
                    dirs.Push(subDir);
            }
        }
    }

    private static string[]? GetExtensionsFromGlob(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob))
            return null;

        var lastSlash = glob.LastIndexOfAny(['/', '\\']);
        var pattern = lastSlash >= 0 ? glob[(lastSlash + 1)..] : glob;

        if (pattern.StartsWith("*.{", StringComparison.Ordinal) && pattern.EndsWith('}'))
        {
            return pattern[3..^1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ext => ext.StartsWith('.') ? ext : "." + ext)
                .ToArray();
        }

        var dotIndex = pattern.IndexOf('.');
        if (dotIndex >= 0 && dotIndex < pattern.Length - 1)
            return ["." + pattern[(dotIndex + 1)..].TrimStart('.')];

        return null;
    }

    private static bool CanReadAsText(string filePath, long maxFileBytes)
    {
        FileInfo info;
        try { info = new FileInfo(filePath); }
        catch { return false; }

        if (!info.Exists || info.Length > maxFileBytes)
            return false;

        try
        {
            Span<byte> buffer = stackalloc byte[(int)Math.Min(4096, info.Length)];
            using var stream = File.OpenRead(filePath);
            var read = stream.Read(buffer);
            if (read == 0)
                return true;

            var suspiciousControls = 0;
            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b == 0)
                    return false;

                if (b < 0x20 && b is not (0x09 or 0x0A or 0x0D or 0x0C))
                    suspiciousControls++;
            }

            return suspiciousControls <= Math.Max(1, read / 100);
        }
        catch
        {
            return false;
        }
    }

    private static Encoding DetectEncoding(string filePath, out string text)
    {
        using var reader = new StreamReader(
            filePath,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: true);
        text = reader.ReadToEnd();
        return reader.CurrentEncoding;
    }
}

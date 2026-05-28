using System.Text;
using System.Text.RegularExpressions;
using Editor.Core.Buffer;

namespace Editor.Core.Config;

public sealed class EditorConfigSettings
{
    public string? IndentStyle { get; set; }
    public string? IndentSize { get; set; }
    public int? TabWidth { get; set; }
    public string? EndOfLine { get; set; }
    public string? Charset { get; set; }

    public void ApplyTo(VimOptions options, VimBuffer? buffer = null)
    {
        if (TabWidth is > 0)
            options.TabStop = TabWidth.Value;

        if (IndentStyle != null)
        {
            if (IndentStyle.Equals("space", StringComparison.OrdinalIgnoreCase))
                options.ExpandTab = true;
            else if (IndentStyle.Equals("tab", StringComparison.OrdinalIgnoreCase))
                options.ExpandTab = false;
        }

        if (IndentSize != null)
        {
            if (IndentSize.Equals("tab", StringComparison.OrdinalIgnoreCase))
            {
                if (TabWidth is > 0)
                    options.ShiftWidth = TabWidth.Value;
            }
            else if (int.TryParse(IndentSize, out var indentSize) && indentSize > 0)
            {
                options.ShiftWidth = indentSize;
            }
        }

        if (TryGetFileFormat(out var fileFormat))
        {
            options.FileFormat = fileFormat;
            if (buffer != null)
                buffer.FileFormat = fileFormat;
        }

        if (TryGetFileEncoding(out var fileEncoding))
        {
            options.FileEncoding = fileEncoding;
            if (buffer != null)
                buffer.FileEncoding = fileEncoding;
        }
    }

    public bool TryGetFileEncoding(out string fileEncoding) => TryMapCharset(Charset, out fileEncoding);

    private bool TryGetFileFormat(out string fileFormat) => TryMapEndOfLine(EndOfLine, out fileFormat);

    private static bool TryMapEndOfLine(string? value, out string fileFormat)
    {
        fileFormat = "";
        if (value == null) return false;

        fileFormat = value.ToLowerInvariant() switch
        {
            "lf" => "unix",
            "crlf" => "dos",
            "cr" => "mac",
            _ => ""
        };
        return fileFormat.Length > 0;
    }

    private static bool TryMapCharset(string? value, out string fileEncoding)
    {
        fileEncoding = "";
        if (value == null) return false;

        fileEncoding = value.ToLowerInvariant() switch
        {
            "utf-8" => "utf-8",
            "utf-8-bom" => "utf-8-bom",
            "latin1" => "latin1",
            "utf-16le" => "utf-16le",
            "utf-16be" => "utf-16be",
            _ => ""
        };
        return fileEncoding.Length > 0;
    }
}

public static class EditorConfig
{
    public static EditorConfigSettings LoadForFile(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var startDir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(startDir))
            return new EditorConfigSettings();

        var configFiles = FindConfigFiles(startDir);
        var settings = new EditorConfigSettings();
        foreach (var configFile in configFiles)
            ApplyMatchingSections(configFile, fullPath, settings);
        return settings;
    }

    public static void ApplyForFile(string filePath, VimOptions options, VimBuffer? buffer = null)
    {
        LoadForFile(filePath).ApplyTo(options, buffer);
    }

    private static List<string> FindConfigFiles(string startDir)
    {
        var found = new List<string>();
        var dir = new DirectoryInfo(startDir);

        while (dir != null)
        {
            var configPath = Path.Combine(dir.FullName, ".editorconfig");
            if (File.Exists(configPath))
            {
                found.Add(configPath);
                if (DeclaresRoot(configPath))
                    break;
            }
            dir = dir.Parent;
        }

        found.Reverse();
        return found;
    }

    private static bool DeclaresRoot(string configPath)
    {
        foreach (var rawLine in File.ReadLines(configPath))
        {
            var line = StripComment(rawLine).Trim();
            if (line.StartsWith('['))
                return false;

            var pair = SplitPair(line);
            if (pair.Key.Equals("root", StringComparison.OrdinalIgnoreCase) &&
                pair.Value.Equals("true", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void ApplyMatchingSections(string configPath, string filePath, EditorConfigSettings settings)
    {
        var configDir = Path.GetDirectoryName(configPath)!;
        var relativePath = Path.GetRelativePath(configDir, filePath).Replace('\\', '/');
        if (relativePath.StartsWith("..", StringComparison.Ordinal))
            return;

        var fileName = Path.GetFileName(filePath);
        var sectionMatches = false;

        foreach (var rawLine in File.ReadLines(configPath))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var pattern = line[1..^1].Trim();
                sectionMatches = IsMatch(pattern, relativePath, fileName);
                continue;
            }

            if (!sectionMatches)
                continue;

            var pair = SplitPair(line);
            if (pair.Key.Length == 0)
                continue;

            ApplyProperty(settings, pair.Key, pair.Value);
        }
    }

    private static void ApplyProperty(EditorConfigSettings settings, string key, string value)
    {
        var isUnset = value.Equals("unset", StringComparison.OrdinalIgnoreCase);
        switch (key.ToLowerInvariant())
        {
            case "indent_style":
                settings.IndentStyle = isUnset ? null : value;
                break;
            case "indent_size":
                settings.IndentSize = isUnset ? null : value;
                break;
            case "tab_width":
                if (isUnset)
                    settings.TabWidth = null;
                else if (int.TryParse(value, out var tabWidth) && tabWidth > 0)
                    settings.TabWidth = tabWidth;
                break;
            case "end_of_line":
                settings.EndOfLine = isUnset ? null : value;
                break;
            case "charset":
                settings.Charset = isUnset ? null : value;
                break;
        }
    }

    private static (string Key, string Value) SplitPair(string line)
    {
        var idx = line.IndexOf('=');
        if (idx < 0)
            return ("", "");

        return (line[..idx].Trim(), line[(idx + 1)..].Trim());
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        var semi = line.IndexOf(';');
        var idx = hash < 0 ? semi : semi < 0 ? hash : Math.Min(hash, semi);
        return idx < 0 ? line : line[..idx];
    }

    private static bool IsMatch(string pattern, string relativePath, string fileName)
    {
        foreach (var expanded in ExpandBraces(pattern))
        {
            var normalized = expanded.TrimStart('/');
            var target = normalized.Contains('/') ? relativePath : fileName;
            if (GlobToRegex(normalized).IsMatch(target))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> ExpandBraces(string pattern)
    {
        var open = pattern.IndexOf('{');
        var close = open >= 0 ? pattern.IndexOf('}', open + 1) : -1;
        if (open < 0 || close < 0)
        {
            yield return pattern;
            yield break;
        }

        var prefix = pattern[..open];
        var suffix = pattern[(close + 1)..];
        foreach (var part in pattern[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            yield return prefix + part + suffix;
    }

    private static Regex GlobToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (ch == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(ch.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

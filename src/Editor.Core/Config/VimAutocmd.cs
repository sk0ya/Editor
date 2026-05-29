using System.Text;
using System.Text.RegularExpressions;

namespace Editor.Core.Config;

public sealed record VimAutocmd(string Group, string Event, string Pattern, string Command);

public sealed class VimAutocmdRegistry
{
    private readonly List<VimAutocmd> _items = [];

    public IReadOnlyList<VimAutocmd> Items => _items;

    public void Add(string group, string eventName, string pattern, string command) =>
        _items.Add(new VimAutocmd(group, eventName, pattern, command));

    public void Clear(string? group = null, string? eventName = null, string? pattern = null)
    {
        _items.RemoveAll(a =>
            (group == null || string.Equals(a.Group, group, StringComparison.OrdinalIgnoreCase)) &&
            (eventName == null || string.Equals(a.Event, eventName, StringComparison.OrdinalIgnoreCase)) &&
            (pattern == null || string.Equals(a.Pattern, pattern, StringComparison.OrdinalIgnoreCase)));
    }

    public IEnumerable<VimAutocmd> Match(string eventName, string filePathOrType)
    {
        var fileName = Path.GetFileName(filePathOrType);
        foreach (var item in _items)
        {
            if (!string.Equals(item.Event, eventName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (PatternMatches(item.Pattern, filePathOrType) || PatternMatches(item.Pattern, fileName))
                yield return item;
        }
    }

    public string Format()
    {
        if (_items.Count == 0) return "(no autocommands)";

        var sb = new StringBuilder();
        string? group = null;
        foreach (var item in _items.OrderBy(a => a.Group).ThenBy(a => a.Event).ThenBy(a => a.Pattern))
        {
            if (!string.Equals(group, item.Group, StringComparison.Ordinal))
            {
                group = item.Group;
                sb.AppendLine(group.Length == 0 ? "--- Autocommands ---" : $"--- {group} ---");
            }

            sb.AppendLine($"{item.Event,-12} {item.Pattern,-16} {item.Command}");
        }

        return sb.ToString().TrimEnd();
    }

    private static bool PatternMatches(string patternList, string value)
    {
        foreach (var pattern in patternList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (pattern == "*" || string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase))
                return true;

            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            if (Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
        }

        return false;
    }
}

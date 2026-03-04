using System.Text.RegularExpressions;

namespace Editor.Core.Spell;

public class SpellChecker
{
    private HashSet<string> _words = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded = false;

    public bool IsEnabled { get; set; } = false;
    public bool IsLoaded => _loaded;

    public void Load(string? dictPath = null)
    {
        _words.Clear();

        var paths = new List<string>();
        if (dictPath != null) paths.Add(dictPath);

        // Common dictionary locations
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        paths.Add(Path.Combine(home, ".vim", "spell", "en.utf-8.add"));
        paths.Add(Path.Combine(home, "vimfiles", "spell", "en.utf-8.add"));

        // Linux/Mac system dictionaries
        paths.Add("/usr/share/dict/words");
        paths.Add("/usr/share/dict/american-english");
        paths.Add("/usr/share/dict/british-english");

        // App-local dictionary
        var appDir = AppContext.BaseDirectory;
        paths.Add(Path.Combine(appDir, "spell", "en.txt"));

        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            try
            {
                foreach (var line in File.ReadLines(p))
                {
                    var word = line.Trim();
                    if (word.Length == 0 || word.StartsWith('#')) continue;
                    // Strip Vim .add file flags (e.g. "word/ABCDE")
                    var slash = word.IndexOf('/');
                    if (slash > 0) word = word[..slash];
                    if (word.Length >= 2)
                        _words.Add(word);
                }
                _loaded = true;
                return;
            }
            catch { /* try next */ }
        }

        // No dictionary found — mark loaded with empty dict (no false positives)
        _loaded = false;
    }

    public bool Check(string word)
    {
        if (!_loaded || !IsEnabled) return true;
        if (string.IsNullOrEmpty(word)) return true;
        // Strip possessive / contractions for check
        var w = word.TrimEnd('\'').TrimEnd('s');
        return _words.Contains(word) || _words.Contains(w) || IsProperNoun(word);
    }

    private static bool IsProperNoun(string word) =>
        word.Length > 0 && char.IsUpper(word[0]);

    // Find misspelled words in a line, returns (startCol, endCol) spans
    public IReadOnlyList<(int Start, int End)> FindErrors(string line)
    {
        if (!_loaded || !IsEnabled) return [];
        var result = new List<(int, int)>();
        int i = 0;
        while (i < line.Length)
        {
            // Skip non-word chars
            while (i < line.Length && !char.IsLetter(line[i])) i++;
            if (i >= line.Length) break;
            int start = i;
            while (i < line.Length && char.IsLetter(line[i])) i++;
            var word = line[start..i];
            if (!Check(word))
                result.Add((start, i - 1));
        }
        return result;
    }

    // Suggest corrections (up to maxSuggestions) sorted by edit distance
    public IReadOnlyList<string> Suggest(string word, int maxSuggestions = 10)
    {
        if (!_loaded) return [];
        word = word.ToLowerInvariant();

        var scored = new List<(int Score, string Word)>();
        foreach (var w in _words)
        {
            var score = EditDistance(word, w.ToLowerInvariant());
            if (score <= 3)
                scored.Add((score, w));
        }

        return scored.OrderBy(x => x.Score).ThenBy(x => x.Word)
                     .Take(maxSuggestions)
                     .Select(x => x.Word)
                     .ToList();
    }

    // Damerau-Levenshtein distance (capped at 4 for performance)
    private static int EditDistance(string a, string b)
    {
        if (a == b) return 0;
        int la = a.Length, lb = b.Length;
        if (Math.Abs(la - lb) > 3) return 4;
        if (la == 0) return lb;
        if (lb == 0) return la;

        var prev2 = new int[lb + 1];
        var prev  = new int[lb + 1];
        var curr  = new int[lb + 1];

        for (int j = 0; j <= lb; j++) prev[j] = j;

        for (int i = 1; i <= la; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= lb; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    curr[j] = Math.Min(curr[j], prev2[j - 2] + cost);
            }
            var tmp = prev2; prev2 = prev; prev = curr; curr = tmp;
        }
        return prev[lb];
    }
}

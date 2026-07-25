namespace Editor.Core.Engine;

public enum CommandDefinitionKind
{
    Action,
    Motion,
    Operator,
    TextObject,
    Prefix
}

public sealed record CommandDefinition(
    string Id,
    string Sequence,
    CommandDefinitionKind Kind,
    string? Output = null);

public enum CommandGrammarMatchKind
{
    None,
    Prefix,
    Exact
}

public sealed record CommandGrammarMatch(
    CommandGrammarMatchKind Kind,
    CommandDefinition? Definition = null,
    bool HasLongerMatches = false);

public sealed record CommandGrammarDiagnostic(
    string Sequence,
    string Message);

/// <summary>
/// Registration-driven command grammar backed by an immutable read snapshot of a trie.
/// </summary>
public sealed class CommandGrammar
{
    private sealed class Node
    {
        public Dictionary<char, Node> Children { get; } = [];
        public CommandDefinition? Definition { get; set; }
    }

    private readonly object _gate = new();
    private readonly List<CommandDefinition> _definitions = [];
    private volatile Node _root = new();

    public IReadOnlyList<CommandDefinition> Definitions
    {
        get
        {
            lock (_gate)
                return _definitions.ToArray();
        }
    }

    public IReadOnlyList<CommandGrammarDiagnostic> Register(
        params CommandDefinition[] definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        lock (_gate)
        {
            var diagnostics = Validate(definitions);
            if (diagnostics.Count > 0)
                return diagnostics;

            _definitions.AddRange(definitions);
            _root = BuildTrie(_definitions);
            return [];
        }
    }

    public CommandGrammarMatch Match(string sequence)
    {
        if (string.IsNullOrEmpty(sequence))
            return new CommandGrammarMatch(CommandGrammarMatchKind.Prefix);

        var node = _root;
        foreach (var character in sequence)
        {
            if (!node.Children.TryGetValue(character, out node!))
                return new CommandGrammarMatch(CommandGrammarMatchKind.None);
        }

        if (node.Definition is { Kind: not CommandDefinitionKind.Prefix } definition)
            return new CommandGrammarMatch(
                CommandGrammarMatchKind.Exact,
                definition,
                node.Children.Count > 0);
        return node.Children.Count > 0 || node.Definition?.Kind == CommandDefinitionKind.Prefix
            ? new CommandGrammarMatch(CommandGrammarMatchKind.Prefix, node.Definition)
            : new CommandGrammarMatch(CommandGrammarMatchKind.None);
    }

    public CommandDefinition? FindLongestPrefix(
        string sequence,
        CommandDefinitionKind kind)
    {
        lock (_gate)
            return _definitions
                .Where(definition =>
                    definition.Kind == kind &&
                    sequence.StartsWith(definition.Sequence, StringComparison.Ordinal))
                .OrderByDescending(definition => definition.Sequence.Length)
                .FirstOrDefault();
    }

    private List<CommandGrammarDiagnostic> Validate(
        IReadOnlyList<CommandDefinition> additions)
    {
        var diagnostics = new List<CommandGrammarDiagnostic>();
        var ids = new HashSet<string>(
            _definitions.Select(definition => definition.Id),
            StringComparer.OrdinalIgnoreCase);
        var sequences = new HashSet<string>(
            _definitions.Select(definition => definition.Sequence),
            StringComparer.Ordinal);

        foreach (var definition in additions)
        {
            if (string.IsNullOrWhiteSpace(definition.Id))
                diagnostics.Add(new(definition.Sequence, "Command id is required."));
            if (string.IsNullOrEmpty(definition.Sequence))
                diagnostics.Add(new(definition.Sequence, "Command sequence is required."));
            if (!ids.Add(definition.Id))
                diagnostics.Add(new(definition.Sequence,
                    $"Command id '{definition.Id}' is already registered."));
            if (!sequences.Add(definition.Sequence))
                diagnostics.Add(new(definition.Sequence,
                    $"Command sequence '{definition.Sequence}' is already registered."));
        }

        var candidates = _definitions.Concat(additions).ToArray();
        foreach (var definition in additions)
        {
            var shadow = candidates.FirstOrDefault(other =>
                !ReferenceEquals(other, definition) &&
                WouldShadow(other, definition));
            if (shadow != null)
                diagnostics.Add(new(definition.Sequence,
                    $"Command sequence '{definition.Sequence}' is unreachable because " +
                    $"'{shadow.Sequence}' completes first."));

            var shadowed = candidates.FirstOrDefault(other =>
                !ReferenceEquals(other, definition) &&
                WouldShadow(definition, other));
            if (shadowed != null)
                diagnostics.Add(new(definition.Sequence,
                    $"Command sequence '{definition.Sequence}' would make " +
                    $"'{shadowed.Sequence}' unreachable."));
        }

        return diagnostics;
    }

    private static bool WouldShadow(
        CommandDefinition ancestor,
        CommandDefinition descendant)
    {
        if (ancestor.Kind == CommandDefinitionKind.Prefix ||
            descendant.Output != null ||
            ancestor.Sequence.Length >= descendant.Sequence.Length ||
            !descendant.Sequence.StartsWith(ancestor.Sequence, StringComparison.Ordinal))
            return false;

        static bool IsStandalone(CommandDefinitionKind kind) =>
            kind is CommandDefinitionKind.Action or CommandDefinitionKind.Motion;

        return IsStandalone(ancestor.Kind) && IsStandalone(descendant.Kind) ||
               ancestor.Kind == CommandDefinitionKind.TextObject &&
               descendant.Kind == CommandDefinitionKind.TextObject;
    }

    private static Node BuildTrie(IEnumerable<CommandDefinition> definitions)
    {
        var root = new Node();
        foreach (var definition in definitions)
        {
            var node = root;
            foreach (var character in definition.Sequence)
            {
                if (!node.Children.TryGetValue(character, out var child))
                {
                    child = new Node();
                    node.Children.Add(character, child);
                }
                node = child;
            }
            node.Definition = definition;
        }
        return root;
    }

    public static CommandGrammar CreateBuiltIn()
    {
        var grammar = new CommandGrammar();
        var definitions = new List<CommandDefinition>();

        void Add(
            CommandDefinitionKind kind,
            IEnumerable<string> sequences)
        {
            foreach (var sequence in sequences)
                definitions.Add(new CommandDefinition(
                    $"builtin.{kind.ToString().ToLowerInvariant()}.{EscapeId(sequence)}",
                    sequence,
                    kind));
        }

        Add(CommandDefinitionKind.Prefix, ["g", "z", "Z", "[", "]"]);
        Add(CommandDefinitionKind.Operator,
            ["d", "c", "y", ">", "<", "=", "gc", "gq", "gu", "gU", "g~", "ys"]);
        Add(CommandDefinitionKind.TextObject,
        [
            "iw", "iW", "i(", "i)", "ib", "i{", "i}", "iB", "i[", "i]",
            "i<", "i>", "i\"", "i'", "i`", "it", "is", "ip",
            "aw", "aW", "a(", "a)", "ab", "a{", "a}", "aB", "a[", "a]",
            "a<", "a>", "a\"", "a'", "a`", "at", "as", "ap"
        ]);
        Add(CommandDefinitionKind.Motion,
        [
            "h", "j", "k", "l", "w", "b", "e", "W", "B", "E",
            "0", "^", "$", "G", "H", "M", "L", "{", "}", "%",
            ";", ",", "n", "N", "*", "#", "gg", "ge", "gE", "gj", "gk",
            "g_", "gn", "gN",
            "[m", "]m", "[M", "]M", "[[", "]]", "[]", "][",
            "[{", "]}", "[(", "])"
        ]);
        Add(CommandDefinitionKind.Action,
        [
            "~", "x", "X", "p", "P", "u", "\x12", ".", "J",
            "a", "A", "i", "I", "o", "O", "s", "S", "C", "D", "Y", "U",
            "R", "v", "V", "\x16",
            "gt", "gT", "gd", "gr", "ga", "gf", "gx", "gv", "gi", "gJ",
            "gp", "gP", "g;", "g,", "gch", "gct",
            "zz", "zt", "zb", "za", "zc", "zo", "zM", "zR", "zf", "z=",
            "zj", "zk", "zd", "zD", "zE", "zn", "zN", "ZZ", "ZQ"
        ]);
        definitions.AddRange(
        [
            new CommandDefinition("builtin.key.left", "Left", CommandDefinitionKind.Motion, "h"),
            new CommandDefinition("builtin.key.right", "Right", CommandDefinitionKind.Motion, "l"),
            new CommandDefinition("builtin.key.up", "Up", CommandDefinitionKind.Motion, "k"),
            new CommandDefinition("builtin.key.down", "Down", CommandDefinitionKind.Motion, "j"),
        ]);

        var diagnostics = grammar.Register([.. definitions]);
        if (diagnostics.Count > 0)
            throw new InvalidOperationException(
                string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Message)));
        return grammar;
    }

    private static string EscapeId(string sequence) =>
        string.Join("_", sequence.Select(character => $"u{(int)character:x4}"));
}

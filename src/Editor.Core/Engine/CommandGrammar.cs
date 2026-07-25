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
    CommandDefinitionKind Kind);

public enum CommandGrammarMatchKind
{
    None,
    Prefix,
    Exact
}

public sealed record CommandGrammarMatch(
    CommandGrammarMatchKind Kind,
    CommandDefinition? Definition = null);

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
            return new CommandGrammarMatch(CommandGrammarMatchKind.Exact, definition);
        return node.Children.Count > 0 || node.Definition?.Kind == CommandDefinitionKind.Prefix
            ? new CommandGrammarMatch(CommandGrammarMatchKind.Prefix, node.Definition)
            : new CommandGrammarMatch(CommandGrammarMatchKind.None);
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

        return diagnostics;
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
}

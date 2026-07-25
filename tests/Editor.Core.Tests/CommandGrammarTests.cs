using Editor.Core.Engine;

namespace Editor.Core.Tests;

public class CommandGrammarTests
{
    [Fact]
    public void Register_BuildsExactAndPrefixMatches()
    {
        var grammar = new CommandGrammar();

        var diagnostics = grammar.Register(
            new CommandDefinition("prefix.g", "g", CommandDefinitionKind.Prefix),
            new CommandDefinition("action.custom", "gx", CommandDefinitionKind.Action));

        Assert.Empty(diagnostics);
        Assert.Equal(CommandGrammarMatchKind.Prefix, grammar.Match("g").Kind);
        Assert.Equal(CommandGrammarMatchKind.Exact, grammar.Match("gx").Kind);
    }

    [Fact]
    public void Register_DetectsDuplicateIdAndSequence()
    {
        var grammar = new CommandGrammar();
        grammar.Register(new CommandDefinition(
            "action.one", "zx", CommandDefinitionKind.Action));

        var diagnostics = grammar.Register(
            new CommandDefinition("action.one", "zy", CommandDefinitionKind.Action),
            new CommandDefinition("action.two", "zx", CommandDefinitionKind.Action));

        Assert.Equal(2, diagnostics.Count);
        Assert.Single(grammar.Definitions);
    }

    [Fact]
    public void Snapshot_IsUnaffectedByRejectedBatch()
    {
        var grammar = new CommandGrammar();
        grammar.Register(new CommandDefinition(
            "action.one", "aa", CommandDefinitionKind.Action));

        grammar.Register(
            new CommandDefinition("action.two", "bb", CommandDefinitionKind.Action),
            new CommandDefinition("action.one", "cc", CommandDefinitionKind.Action));

        Assert.Equal(CommandGrammarMatchKind.None, grammar.Match("bb").Kind);
    }
}

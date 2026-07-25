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

    [Fact]
    public void Parser_AcceptsRegisteredPrefixCommandWithoutParserChanges()
    {
        var grammar = new CommandGrammar();
        grammar.Register(
            new CommandDefinition("prefix.z", "z", CommandDefinitionKind.Prefix),
            new CommandDefinition("action.zx", "zx", CommandDefinitionKind.Action));
        var parser = new CommandParser(grammar: grammar);

        var first = parser.Feed("z");
        var second = parser.Feed("x");

        Assert.Equal(CommandState.Incomplete, first.State);
        Assert.Equal(CommandState.Complete, second.State);
        Assert.Equal("zx", second.Command!.Value.Motion);
    }

    [Fact]
    public void Parser_ComposesRegisteredMotionWithOperator()
    {
        var grammar = new CommandGrammar();
        grammar.Register(
            new CommandDefinition("motion.qx", "qx", CommandDefinitionKind.Motion));
        var parser = new CommandParser(grammar: grammar);

        parser.Feed("d");
        parser.Feed("q");
        var result = parser.Feed("x");

        Assert.Equal(CommandState.Complete, result.State);
        Assert.Equal("d", result.Command!.Value.Operator);
        Assert.Equal("qx", result.Command.Value.Motion);
    }

    [Fact]
    public void Parser_PreservesLegacyUppercaseAction()
    {
        Assert.Equal(
            CommandGrammarMatchKind.Exact,
            CommandGrammar.CreateBuiltIn().Match("U").Kind);
        var parser = new CommandParser();

        var result = parser.Feed("U");

        Assert.Equal(CommandState.Complete, result.State);
        Assert.Equal("U", result.Command!.Value.Motion);
    }

    [Fact]
    public void BuiltInDefinitions_AreAcceptedByParser()
    {
        var grammar = CommandGrammar.CreateBuiltIn();
        foreach (var definition in grammar.Definitions.Where(definition =>
                     definition.Kind is CommandDefinitionKind.Action or CommandDefinitionKind.Motion))
        {
            var parser = new CommandParser();
            (CommandState State, ParsedCommand? Command) result = default;
            if (definition.Output != null)
            {
                result = parser.Feed(definition.Sequence);
            }
            else
            {
                foreach (var key in definition.Sequence)
                    result = parser.Feed(key.ToString());
            }

            Assert.True(
                result.State == CommandState.Complete,
                $"Built-in '{definition.Sequence}' did not complete.");
        }
    }

    [Fact]
    public void BuiltInTextObjectsComposeWithOperators()
    {
        var grammar = CommandGrammar.CreateBuiltIn();
        foreach (var definition in grammar.Definitions.Where(definition =>
                     definition.Kind == CommandDefinitionKind.TextObject))
        {
            var parser = new CommandParser();
            parser.Feed("d");
            (CommandState State, ParsedCommand? Command) result = default;
            foreach (var key in definition.Sequence)
                result = parser.Feed(key.ToString());

            Assert.True(
                result.State == CommandState.Complete,
                $"Text object '{definition.Sequence}' did not complete.");
        }
    }
}

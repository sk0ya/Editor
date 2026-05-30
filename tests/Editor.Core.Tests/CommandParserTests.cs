using Editor.Core.Engine;

namespace Editor.Core.Tests;

public class CommandParserTests
{
    [Fact]
    public void CountedMotion_ParsesCountBeforeMotion()
    {
        var parser = new CommandParser();

        Assert.Equal(CommandState.Incomplete, parser.Feed("3").State);
        var (state, command) = parser.Feed("w");

        Assert.Equal(CommandState.Complete, state);
        Assert.NotNull(command);
        Assert.Equal(3, command.Value.Count);
        Assert.Null(command.Value.Operator);
        Assert.Equal("w", command.Value.Motion);
    }

    [Fact]
    public void OperatorMotion_ParsesMotionCount()
    {
        var parser = new CommandParser();

        Assert.Equal(CommandState.Incomplete, parser.Feed("d").State);
        Assert.Equal(CommandState.Incomplete, parser.Feed("2").State);
        var (state, command) = parser.Feed("w");

        Assert.Equal(CommandState.Complete, state);
        Assert.NotNull(command);
        Assert.Equal(2, command.Value.Count);
        Assert.Equal("d", command.Value.Operator);
        Assert.Equal("w", command.Value.Motion);
    }

    [Fact]
    public void OperatorTextObjects_ParseWithoutCounts()
    {
        var changeParser = new CommandParser();
        changeParser.Feed("c");
        changeParser.Feed("i");
        var (changeState, changeCommand) = changeParser.Feed("w");

        Assert.Equal(CommandState.Complete, changeState);
        Assert.NotNull(changeCommand);
        Assert.Equal(1, changeCommand.Value.Count);
        Assert.Equal("c", changeCommand.Value.Operator);
        Assert.Equal("iw", changeCommand.Value.Motion);

        var deleteParser = new CommandParser();
        deleteParser.Feed("d");
        deleteParser.Feed("i");
        var (deleteState, deleteCommand) = deleteParser.Feed("w");

        Assert.Equal(CommandState.Complete, deleteState);
        Assert.NotNull(deleteCommand);
        Assert.Equal(1, deleteCommand.Value.Count);
        Assert.Equal("d", deleteCommand.Value.Operator);
        Assert.Equal("iw", deleteCommand.Value.Motion);
    }

    [Fact]
    public void OperatorTextObject_ParsesMotionCount()
    {
        var parser = new CommandParser();

        Assert.Equal(CommandState.Incomplete, parser.Feed("d").State);
        Assert.Equal(CommandState.Incomplete, parser.Feed("3").State);
        Assert.Equal(CommandState.Incomplete, parser.Feed("i").State);
        var (state, command) = parser.Feed("w");

        Assert.Equal(CommandState.Complete, state);
        Assert.NotNull(command);
        Assert.Equal(3, command.Value.Count);
        Assert.Equal("d", command.Value.Operator);
        Assert.Equal("iw", command.Value.Motion);
    }
}

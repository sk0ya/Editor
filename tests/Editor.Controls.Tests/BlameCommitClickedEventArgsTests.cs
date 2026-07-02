using Editor.Controls.Git;

namespace Editor.Controls.Tests;

public sealed class BlameCommitClickedEventArgsTests
{
    [Fact]
    public void Ctor_ExposesHashAndDisplayAnnotation()
    {
        var blame = new EditorBlameLine("abc1234", "koya", "2026-05-31", "バグ修正");
        var e = new BlameCommitClickedEventArgs(41, blame);

        Assert.Equal(41, e.Line);
        Assert.Equal("abc1234", e.CommitHash);
        Assert.Equal("abc1234 (koya, 2026-05-31)", e.Annotation);
        Assert.Same(blame, e.Blame);
    }

    [Fact]
    public void EditorBlameLine_TooltipIncludesSummary()
    {
        var blame = new EditorBlameLine("abc1234", "koya", "2026-05-31", "バグ修正");
        Assert.Equal("abc1234 (koya, 2026-05-31)\nバグ修正", blame.Tooltip);

        var noSummary = new EditorBlameLine("abc1234", "koya", "2026-05-31", "");
        Assert.Equal("abc1234 (koya, 2026-05-31)", noSummary.Tooltip);
    }
}

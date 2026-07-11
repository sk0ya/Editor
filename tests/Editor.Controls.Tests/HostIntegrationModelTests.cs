using Editor.Controls.HostIntegration;

namespace Editor.Controls.Tests;

public sealed class HostIntegrationModelTests
{
    [Fact]
    public void TextRange_Create_UsesZeroBasedEndExclusivePositions()
    {
        var range = EditorTextRange.Create(1, 2, 3, 4);
        Assert.Equal(new EditorTextPosition(1, 2), range.Start);
        Assert.Equal(new EditorTextPosition(3, 4), range.End);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(1, 0, 0, 0)]
    [InlineData(0, 2, 0, 1)]
    public void TextRange_Create_RejectsInvalidPositions(int sl, int sc, int el, int ec)
    {
        Assert.ThrowsAny<ArgumentException>(() => EditorTextRange.Create(sl, sc, el, ec));
    }

    [Fact]
    public void DiagnosticAndQuickfix_DoNotExposeCoreTypes()
    {
        var publicTypes = new[] { typeof(EditorDiagnostic), typeof(EditorQuickfixItem), typeof(EditorTextRange) };
        Assert.All(publicTypes.SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters())),
            p => Assert.DoesNotContain("Editor.Core", p.ParameterType.FullName ?? ""));
    }

    [Fact]
    public void QuickfixChangedEventArgs_PreservesReadOnlySnapshot()
    {
        var item = new EditorQuickfixItem("a.cs", EditorTextRange.Create(0, 0, 0, 1), "problem");
        var snapshot = Array.AsReadOnly(new[] { item });
        var args = new EditorQuickfixChangedEventArgs("Build", snapshot);
        Assert.Throws<NotSupportedException>(() => ((IList<EditorQuickfixItem>)args.Items).Clear());
        Assert.Equal("Build", args.Title);
    }
}

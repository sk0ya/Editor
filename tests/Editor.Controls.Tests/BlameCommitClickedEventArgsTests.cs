using Editor.Controls.Git;

namespace Editor.Controls.Tests;

public sealed class BlameCommitClickedEventArgsTests
{
    [Theory]
    [InlineData("abc1234 (koya, 2026-05-31)", "abc1234")]
    [InlineData("  abc1234 (koya, 2026-05-31)", "abc1234")]  // GetBlameAnnotations の先頭空白付き形式
    [InlineData("0f3e9d2 (山田 太郎, 2026-01-01)", "0f3e9d2")]
    [InlineData("0123456789abcdef0123456789abcdef01234567 (a, b)", "0123456789abcdef0123456789abcdef01234567")]
    public void TryParseCommitHash_ParsesLeadingHash(string annotation, string expected)
        => Assert.Equal(expected, BlameCommitClickedEventArgs.TryParseCommitHash(annotation));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc (too short)")]                    // 4桁未満
    [InlineData("not-a-hash (koya, 2026-05-31)")]      // 16進以外
    [InlineData("ABC1234 (koya, 2026-05-31)")]         // git のハッシュは小文字
    [InlineData("0123456789abcdef0123456789abcdef012345678 (a, b)")] // 41桁
    public void TryParseCommitHash_ReturnsNullForNonHash(string annotation)
        => Assert.Null(BlameCommitClickedEventArgs.TryParseCommitHash(annotation));

    [Fact]
    public void Ctor_ParsesHashAndKeepsAnnotation()
    {
        var e = new BlameCommitClickedEventArgs(41, "  abc1234 (koya, 2026-05-31)");
        Assert.Equal(41, e.Line);
        Assert.Equal("abc1234", e.CommitHash);
        Assert.Equal("  abc1234 (koya, 2026-05-31)", e.Annotation);
    }
}

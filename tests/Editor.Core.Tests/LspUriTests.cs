using Editor.Core.Lsp;

namespace Editor.Core.Tests;

/// <summary>
/// typescript-language-server（vscode-uri 系）はドライブのコロンを <c>%3A</c> で符号化して返す。
/// 素の <c>new Uri(uri).LocalPath</c> はこれを DOS パスと認識できず <c>/c:/…</c> を返すため、
/// rename / 参照 / 定義ジャンプ / 診断の照合が全て外れていた。その回帰テスト。
/// </summary>
public class LspUriTests
{
    // ── TryToLocalPath ──

    [Fact]
    public void PercentEncodedDriveColon_BecomesAWindowsPath()
    {
        Assert.Equal(@"c:\work\a.ts", LspUri.TryToLocalPath("file:///c%3A/work/a.ts"));
    }

    [Fact]
    public void PlainDriveColon_IsUnchanged()
    {
        Assert.Equal(@"C:\work\a.cs", LspUri.TryToLocalPath("file:///C:/work/a.cs"));
    }

    [Fact]
    public void EncodedPathCharacters_AreDecoded()
    {
        Assert.Equal(@"c:\work dir\a b.ts", LspUri.TryToLocalPath("file:///c%3A/work%20dir/a%20b.ts"));
    }

    [Fact]
    public void JapaneseFileName_SurvivesTheRoundTrip()
    {
        var uri = LspUri.FromPath(@"C:\作業\ノート.ts");
        Assert.Equal(@"C:\作業\ノート.ts", LspUri.TryToLocalPath(uri));
    }

    [Fact]
    public void UncPath_IsNotMistakenForAnEncodedDrive()
    {
        Assert.Equal(@"\\server\share\a.ts", LspUri.TryToLocalPath("file://server/share/a.ts"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("untitled:Untitled-1")]
    [InlineData("https://example.com/a.ts")]
    [InlineData("not a uri at all")]
    public void NonFileUris_HaveNoLocalPath(string? uri)
    {
        Assert.Null(LspUri.TryToLocalPath(uri));
    }

    [Fact]
    public void ToLocalPathOrOriginal_KeepsNonFileUrisForDisplay()
    {
        Assert.Equal("untitled:Untitled-1", LspUri.ToLocalPathOrOriginal("untitled:Untitled-1"));
        Assert.Equal("", LspUri.ToLocalPathOrOriginal(null));
    }

    // ── Normalize ──

    [Fact]
    public void Normalize_MakesBothDriveSpellingsIdentical()
    {
        Assert.Equal(
            LspUri.Normalize("file:///c:/work/a.ts"),
            LspUri.Normalize("file:///c%3A/work/a.ts"));
    }

    [Fact]
    public void Normalize_ProducesTheFormWeSendToServers()
    {
        Assert.Equal(LspUri.FromPath(@"c:\work\a.ts"), LspUri.Normalize("file:///c%3A/work/a.ts"));
    }

    [Fact]
    public void Normalize_LeavesNonFileUrisAlone()
    {
        Assert.Equal("untitled:Untitled-1", LspUri.Normalize("untitled:Untitled-1"));
        Assert.Equal("", LspUri.Normalize(null));
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var once = LspUri.Normalize("file:///c%3A/work/a.ts");
        Assert.Equal(once, LspUri.Normalize(once));
    }

    // ── 照合 ──

    [Fact]
    public void MatchesPath_AcceptsTheEncodedFormOfTheOpenBuffer()
    {
        // これが false だったせいで、rename の編集が「編集中のファイル自身」に当たらず
        // 全部「他ファイル」としてホストに投げられていた。
        Assert.True(LspUri.MatchesPath("file:///c%3A/work/a.ts", @"C:\work\a.ts"));
    }

    [Fact]
    public void MatchesPath_RejectsADifferentFile()
    {
        Assert.False(LspUri.MatchesPath("file:///c%3A/work/a.ts", @"C:\work\b.ts"));
        Assert.False(LspUri.MatchesPath("file:///c%3A/work/a.ts", ""));
        Assert.False(LspUri.MatchesPath(null, @"C:\work\a.ts"));
    }

    [Fact]
    public void SamePath_IgnoresEncodingAndDriveLetterCase()
    {
        Assert.True(LspUri.SamePath("file:///c%3A/work/a.ts", "file:///C:/work/a.ts"));
        Assert.False(LspUri.SamePath("file:///c%3A/work/a.ts", "file:///C:/work/b.ts"));
    }

    [Fact]
    public void Comparer_MatchesUriKeysCaseInsensitively()
    {
        var map = new Dictionary<string, int>(LspUri.Comparer) { ["file:///C:/work/a.ts"] = 1 };
        Assert.True(map.ContainsKey("file:///c:/work/a.ts"));
    }
}

using Editor.Core.Editing;

namespace Editor.Core.Tests;

public class ImagePasteOptionsTests
{
    private static readonly DateTime Ts = new(2026, 7, 9, 13, 5, 30);

    // Nothing exists on disk by default so uniqueness logic doesn't kick in.
    private static bool NeverExists(string _) => false;

    [Fact]
    public void Resolve_Defaults_SavesUnderImagesDirBesideFile()
    {
        var opts = new ImagePasteOptions();
        var target = opts.Resolve(Path.Combine("C:", "docs", "notes.md"), Ts, NeverExists);

        Assert.Equal("images/notes-20260709-130530.png", target.LinkPath);
        Assert.EndsWith(Path.Combine("images", "notes-20260709-130530.png"), target.AbsolutePath);
        Assert.Equal("notes-20260709-130530", target.AltText);
    }

    [Fact]
    public void Resolve_DirectoryTemplate_ExpandsPlaceholders()
    {
        var opts = new ImagePasteOptions { Directory = "assets/{filename}/{date}", FileName = "{time}.png" };
        var target = opts.Resolve(Path.Combine("C:", "docs", "notes.md"), Ts, NeverExists);

        Assert.Equal("assets/notes/20260709/130530.png", target.LinkPath);
    }

    [Fact]
    public void Resolve_SeqPlaceholder_IncrementsUntilUnique()
    {
        var opts = new ImagePasteOptions { Directory = ".", FileName = "img-{seq}.png" };
        // img-1 and img-2 already exist → expect img-3.
        bool Exists(string p) => Path.GetFileName(p) is "img-1.png" or "img-2.png";

        var target = opts.Resolve(Path.Combine("C:", "docs", "notes.md"), Ts, Exists);

        Assert.Equal("img-3.png", target.LinkPath);
    }

    [Fact]
    public void Resolve_NoSeqButNameTaken_AppendsNumericSuffix()
    {
        var opts = new ImagePasteOptions { Directory = ".", FileName = "pic.png" };
        bool Exists(string p) => Path.GetFileName(p) == "pic.png";

        var target = opts.Resolve(Path.Combine("C:", "docs", "notes.md"), Ts, Exists);

        Assert.Equal("pic-1.png", target.LinkPath);
    }

    [Fact]
    public void Resolve_UnsavedFile_UsesImageStemAndCwd()
    {
        var opts = new ImagePasteOptions();
        var target = opts.Resolve(null, Ts, NeverExists);

        Assert.Equal("images/image-20260709-130530.png", target.LinkPath);
    }

    [Fact]
    public void Resolve_CustomAltText_ExpandsPlaceholders()
    {
        var opts = new ImagePasteOptions { AltText = "{filename} screenshot" };
        var target = opts.Resolve(Path.Combine("C:", "docs", "notes.md"), Ts, NeverExists);

        Assert.Equal("notes screenshot", target.AltText);
    }

    [Fact]
    public void Resolve_EmptyDirectory_SavesBesideFile()
    {
        var opts = new ImagePasteOptions { Directory = "", FileName = "shot.png" };
        var target = opts.Resolve(Path.Combine("C:", "docs", "notes.md"), Ts, NeverExists);

        Assert.Equal("shot.png", target.LinkPath);
    }
}

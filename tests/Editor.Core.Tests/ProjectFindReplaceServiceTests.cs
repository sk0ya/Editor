using Editor.Core.Search;

namespace Editor.Core.Tests;

public class ProjectFindReplaceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EditorProjectReplaceTests", Guid.NewGuid().ToString("N"));

    public ProjectFindReplaceServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Find_SkipsExcludedDirectories()
    {
        Write("src/app.cs", "needle\n");
        Write(".git/config", "needle\n");
        Write("bin/output.txt", "needle\n");
        Write("obj/cache.txt", "needle\n");

        var matches = ProjectFindReplaceService.Find(new ProjectSearchOptions(_root, "needle"));

        var match = Assert.Single(matches);
        Assert.EndsWith(Path.Combine("src", "app.cs"), match.FilePath);
    }

    [Fact]
    public void Find_SkipsBinaryAndHugeFiles()
    {
        Write("src/text.txt", "needle\n");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllBytes(Path.Combine(_root, "src", "binary.dat"), [0x66, 0x00, 0x6f, 0x6f]);
        File.WriteAllText(Path.Combine(_root, "src", "huge.txt"), "needle" + new string('x', 32));

        var matches = ProjectFindReplaceService.Find(
            new ProjectSearchOptions(_root, "needle", MaxFileBytes: 8));

        var match = Assert.Single(matches);
        Assert.EndsWith(Path.Combine("src", "text.txt"), match.FilePath);
    }

    [Fact]
    public void Replace_UpdatesMatchingTextFilesOnly()
    {
        Write("src/a.cs", "alpha beta\nbeta\n");
        Write("src/b.txt", "beta\n");
        Write("src/c.md", "beta\n");

        var result = ProjectFindReplaceService.Replace(
            new ProjectSearchOptions(_root, "beta", FileGlob: "**/*.{cs,txt}"),
            "gamma");

        Assert.Equal(3, result.MatchCount);
        Assert.Equal(2, result.FileCount);
        Assert.Equal("alpha gamma\ngamma\n", Read("src/a.cs"));
        Assert.Equal("gamma\n", Read("src/b.txt"));
        Assert.Equal("beta\n", Read("src/c.md"));
    }

    [Fact]
    public void Replace_UsesRegexReplacementGroups()
    {
        Write("src/a.txt", "name: alpha\n");

        var result = ProjectFindReplaceService.Replace(
            new ProjectSearchOptions(_root, "name: (\\w+)"),
            "id: $1");

        Assert.Equal(1, result.MatchCount);
        Assert.Equal("id: alpha\n", Read("src/a.txt"));
    }

    private void Write(string relativePath, string content)
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private string Read(string relativePath)
    {
        return File.ReadAllText(Path.Combine(_root, relativePath));
    }
}

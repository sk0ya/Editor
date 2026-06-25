using System.IO;
using Editor.Core.Formatting;

namespace Editor.Core.Tests;

public class FormatterRegistryTests
{
    private static string TempStore() =>
        Path.Combine(Path.GetTempPath(), "fmt-reg-test-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void GetForExtension_UnconfiguredReturnsNull()
    {
        var reg = new FormatterRegistry();   // in-memory, no built-in defaults

        Assert.Null(reg.GetForExtension(".md"));
    }

    [Fact]
    public void Set_AddsFormatter_AndNormalizesExtension()
    {
        var reg = new FormatterRegistry();

        reg.Set("MD", new FormatterDef("prettier", ["--stdin-filepath", "{file}"]));

        var def = reg.GetForExtension(".md");
        Assert.NotNull(def);
        Assert.Equal("prettier", def!.Executable);
        Assert.Equal(["--stdin-filepath", "{file}"], def.Args);
        Assert.Equal("prettier", reg.GetForExtension(".MD")!.Executable);
    }

    [Fact]
    public void Set_ReplacesExistingFormatter()
    {
        var reg = new FormatterRegistry();
        reg.Set(".md", new FormatterDef("prettier", []));

        reg.Set(".md", new FormatterDef("dprint", ["fmt", "--stdin", "{file}"]));

        Assert.Equal("dprint", reg.GetForExtension(".md")!.Executable);
        Assert.Single(reg.List());
    }

    [Fact]
    public void Remove_DropsFormatter()
    {
        var reg = new FormatterRegistry();
        reg.Set(".md", new FormatterDef("prettier", []));

        Assert.True(reg.Remove(".md"));
        Assert.Null(reg.GetForExtension(".md"));
        Assert.Empty(reg.List());
    }

    [Fact]
    public void Remove_UnknownReturnsFalse()
    {
        var reg = new FormatterRegistry();

        Assert.False(reg.Remove(".md"));
    }

    [Fact]
    public void Persistence_RoundTrips()
    {
        var path = TempStore();
        try
        {
            var reg = new FormatterRegistry(path);
            reg.Set(".md", new FormatterDef("prettier", ["--stdin-filepath", "{file}"]));

            var reloaded = new FormatterRegistry(path);

            var def = reloaded.GetForExtension(".md");
            Assert.Equal("prettier", def!.Executable);
            Assert.Equal(["--stdin-filepath", "{file}"], def.Args);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ConfigureDefault_RedirectsPersistenceLocation()
    {
        var path = TempStore();
        try
        {
            FormatterRegistry.ConfigureDefault(path);
            FormatterRegistry.Default.Set(".md", new FormatterDef("dprint", []));

            Assert.True(File.Exists(path));   // persisted to the host-chosen location, not %APPDATA%/sk0ya.Editor
            Assert.Equal("dprint", new FormatterRegistry(path).GetForExtension(".md")!.Executable);
        }
        finally
        {
            FormatterRegistry.ConfigureDefault(null);   // leave Default in-memory for other tests
            File.Delete(path);
        }
    }

    [Fact]
    public void InMemoryRegistry_DoesNotThrowOnSet()
    {
        var reg = new FormatterRegistry();
        reg.Set(".md", new FormatterDef("prettier", []));
        Assert.Equal("prettier", reg.GetForExtension(".md")!.Executable);
    }

    [Fact]
    public void KnownFormatters_SuggestsMarkdownCandidates()
    {
        var candidates = KnownFormatters.ForExtension(".md");

        Assert.NotEmpty(candidates);
        Assert.Contains(candidates, c => c.Executable == "prettier");
    }

    [Fact]
    public void KnownFormatters_UnknownExtensionIsEmpty()
    {
        Assert.Empty(KnownFormatters.ForExtension(".unknownext"));
    }
}

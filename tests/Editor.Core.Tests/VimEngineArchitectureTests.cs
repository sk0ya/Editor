using System.Reflection;
using Editor.Core.Engine;

namespace Editor.Core.Tests;

public class VimEngineArchitectureTests
{
    [Fact]
    public void PublicFacade_OwnsOnlyTheInternalRuntime()
    {
        var fields = typeof(VimEngine).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        var field = Assert.Single(fields);
        Assert.Equal("VimEngineRuntime", field.FieldType.Name);
        Assert.True(field.FieldType.IsNotPublic);
    }

    [Fact]
    public void Runtime_IsNotPartOfThePublicApi()
    {
        var runtime = typeof(VimEngine).Assembly.GetType(
            "Editor.Core.Engine.VimEngineRuntime",
            throwOnError: true)!;

        Assert.True(runtime.IsNotPublic);
        Assert.True(runtime.IsSealed);
    }
}

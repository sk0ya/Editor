using Editor.Core.Models;

namespace Editor.Core.Tests;

public class LocationListNavigatorTests
{
    [Fact]
    public void Move_ClampsWithinList()
    {
        var navigator = new LocationListNavigator();
        navigator.Reset(3);

        Assert.Equal(0, navigator.Move(1));
        Assert.Equal(2, navigator.Move(5));
        Assert.Equal(0, navigator.Move(-5));
    }

    [Fact]
    public void Goto_NoArgumentUsesCurrentOrFirst()
    {
        var navigator = new LocationListNavigator();
        navigator.Reset(3);

        Assert.Equal(0, navigator.Goto(-1));
        Assert.Equal(1, navigator.Goto(1));
        Assert.Equal(1, navigator.Goto(-1));
    }

    [Fact]
    public void Goto_ClampsOutOfRangeIndex()
    {
        var navigator = new LocationListNavigator();
        navigator.Reset(3);

        Assert.Equal(2, navigator.Goto(99));
        Assert.Equal(0, navigator.Goto(0));
    }

    [Fact]
    public void SetCount_PreservesAndClampsCurrentIndex()
    {
        var navigator = new LocationListNavigator();
        navigator.Reset(5);
        navigator.Goto(4);

        navigator.SetCount(2);

        Assert.Equal(1, navigator.CurrentIndex);
        Assert.Equal(2, navigator.Count);
    }

    [Fact]
    public void EmptyList_HasNoTarget()
    {
        var navigator = new LocationListNavigator();
        navigator.Reset(0);

        Assert.Null(navigator.Move(1));
        Assert.Null(navigator.Goto(-1));
    }
}

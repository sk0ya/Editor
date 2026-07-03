using Editor.Core.Registers;

namespace Editor.Core.Tests;

public class RegisterManagerTests
{
    [Fact]
    public void SetDelete_LinewiseDelete_ShiftsIntoRegister1()
    {
        var mgr = new RegisterManager();
        mgr.SetDelete('"', new Register("alpha", RegisterType.Line));

        Assert.Equal("alpha", mgr.Get('1').Text);
        Assert.Equal("alpha", mgr.GetUnnamed().Text);
    }

    [Fact]
    public void SetDelete_MultipleLinewiseDeletes_ShiftRingInOrder()
    {
        var mgr = new RegisterManager();
        mgr.SetDelete('"', new Register("first", RegisterType.Line));
        mgr.SetDelete('"', new Register("second", RegisterType.Line));
        mgr.SetDelete('"', new Register("third", RegisterType.Line));

        Assert.Equal("third", mgr.Get('1').Text);
        Assert.Equal("second", mgr.Get('2').Text);
        Assert.Equal("first", mgr.Get('3').Text);
        Assert.True(mgr.Get('4').IsEmpty);
    }

    [Fact]
    public void SetDelete_RingCapsAtNine_DropsOldest()
    {
        var mgr = new RegisterManager();
        for (int i = 1; i <= 10; i++)
            mgr.SetDelete('"', new Register($"line{i}", RegisterType.Line));

        // Most recent (line10) is "1; the ring holds the last 9 (line10..line2); line1 fell off.
        Assert.Equal("line10", mgr.Get('1').Text);
        Assert.Equal("line9", mgr.Get('2').Text);
        Assert.Equal("line2", mgr.Get('9').Text);
    }

    [Fact]
    public void SetDelete_MultiLineCharwiseDelete_ShiftsIntoRegister1()
    {
        var mgr = new RegisterManager();
        mgr.SetDelete('"', new Register("foo\nbar", RegisterType.Character));

        Assert.Equal("foo\nbar", mgr.Get('1').Text);
    }

    [Fact]
    public void SetDelete_SmallCharwiseDelete_GoesToMinusRegister_NotNumberedRing()
    {
        var mgr = new RegisterManager();
        mgr.SetDelete('"', new Register("x", RegisterType.Character));

        Assert.Equal("x", mgr.Get('-').Text);
        Assert.True(mgr.Get('1').IsEmpty);
        Assert.Equal("x", mgr.GetUnnamed().Text);
    }

    [Fact]
    public void SetDelete_SmallDelete_DoesNotDisturbExistingNumberedRing()
    {
        var mgr = new RegisterManager();
        mgr.SetDelete('"', new Register("alpha", RegisterType.Line));
        mgr.SetDelete('"', new Register("x", RegisterType.Character)); // small delete afterwards

        Assert.Equal("alpha", mgr.Get('1').Text); // unchanged by the small delete
        Assert.Equal("x", mgr.Get('-').Text);
    }

    [Fact]
    public void SetDelete_SingleLineBlockwiseDelete_StillShiftsIntoRegister1()
    {
        // Vim never treats a blockwise delete as "small", even when it spans only one line/column.
        var mgr = new RegisterManager();
        mgr.SetDelete('"', new Register("x", RegisterType.Block));

        Assert.Equal("x", mgr.Get('1').Text);
        Assert.True(mgr.Get('-').IsEmpty);
    }

    [Fact]
    public void SetDelete_ExplicitNamedRegister_BypassesNumberedRing()
    {
        var mgr = new RegisterManager();
        mgr.SetDelete('a', new Register("alpha", RegisterType.Line));

        Assert.Equal("alpha", mgr.Get('a').Text);
        Assert.True(mgr.Get('1').IsEmpty);
        // Explicit-register deletes still don't touch the unnamed register's stored copy
        // the way Set() behaves for any other named register.
        Assert.NotEqual("alpha", mgr.Get('0').Text);
    }

    [Fact]
    public void SetYank_DoesNotShiftNumberedDeleteRing()
    {
        var mgr = new RegisterManager();
        mgr.SetDelete('"', new Register("deleted", RegisterType.Line));
        mgr.SetYank('"', new Register("yanked", RegisterType.Line));

        // A yank always goes to "0 and unnamed, but must never disturb "1.
        Assert.Equal("yanked", mgr.Get('0').Text);
        Assert.Equal("deleted", mgr.Get('1').Text);
    }
}

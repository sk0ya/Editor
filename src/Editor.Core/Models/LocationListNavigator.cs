namespace Editor.Core.Models;

public sealed class LocationListNavigator
{
    public int Count { get; private set; }
    public int CurrentIndex { get; private set; } = -1;

    public void Reset(int count)
    {
        Count = Math.Max(0, count);
        CurrentIndex = -1;
    }

    public void SetCount(int count)
    {
        Count = Math.Max(0, count);
        CurrentIndex = Count == 0 ? -1 : Math.Clamp(CurrentIndex, -1, Count - 1);
    }

    public int? Move(int delta)
    {
        if (Count == 0)
            return null;

        CurrentIndex = Math.Clamp(CurrentIndex + delta, 0, Count - 1);
        return CurrentIndex;
    }

    public int? Goto(int index)
    {
        if (Count == 0)
            return null;

        CurrentIndex = index < 0
            ? Math.Max(0, CurrentIndex)
            : Math.Clamp(index, 0, Count - 1);
        return CurrentIndex;
    }
}

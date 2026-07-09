using Editor.Core.Models;

namespace Editor.Core.Engine;

/// <summary>
/// Word-motion convenience wrappers over <see cref="MotionEngine.Calculate"/>, exposing
/// <c>w</c>/<c>b</c>/<c>e</c> (and their WORD variants) as direct cursor-to-cursor calls.
/// </summary>
public static class MotionEngineExtensions
{
    public static CursorPosition WordForward(this MotionEngine me, CursorPosition cursor, int count, bool WORD)
    {
        var mot = me.Calculate(WORD ? "W" : "w", cursor, count);
        return mot?.Target ?? cursor;
    }

    public static CursorPosition WordBackward(this MotionEngine me, CursorPosition cursor, int count, bool WORD)
    {
        var mot = me.Calculate(WORD ? "B" : "b", cursor, count);
        return mot?.Target ?? cursor;
    }

    public static CursorPosition WordEnd(this MotionEngine me, CursorPosition cursor, int count, bool WORD)
    {
        var mot = me.Calculate(WORD ? "E" : "e", cursor, count);
        return mot?.Target ?? cursor;
    }
}

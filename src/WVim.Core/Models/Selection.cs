namespace WVim.Core.Models;

public enum SelectionType { Character, Line, Block }

public record struct Selection(CursorPosition Start, CursorPosition End, SelectionType Type)
{
    public CursorPosition NormalizedStart =>
        Start.Line < End.Line || (Start.Line == End.Line && Start.Column <= End.Column) ? Start : End;

    public CursorPosition NormalizedEnd =>
        Start.Line < End.Line || (Start.Line == End.Line && Start.Column <= End.Column) ? End : Start;

    public bool IsEmpty => Start == End;

    public bool ContainsLine(int line)
    {
        var s = NormalizedStart;
        var e = NormalizedEnd;
        return line >= s.Line && line <= e.Line;
    }
}

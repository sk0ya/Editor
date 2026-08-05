namespace Editor.Core.Models;

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

    /// <summary>この選択の内側の位置か。<see cref="SelectionType.Line"/> は行だけ、
    /// <see cref="SelectionType.Block"/> は行と桁の矩形で判定する。
    ///
    /// <para>用途は「選択の内側で右クリックされたか」——内側なら選択を壊さずメニューを出す
    /// （選択を消してしまうと「選択して右クリック→メソッドの抽出」が成立しない）。
    /// 終端は**含む**扱いにする。ちょうど末尾をクリックしたときに選択が消えるのは意図に反するため。</para></summary>
    public bool Contains(int line, int column)
    {
        var s = NormalizedStart;
        var e = NormalizedEnd;
        if (line < s.Line || line > e.Line) return false;

        return Type switch
        {
            SelectionType.Line => true,
            SelectionType.Block =>
                column >= Math.Min(s.Column, e.Column) && column <= Math.Max(s.Column, e.Column),
            _ =>
                (line > s.Line || column >= s.Column) &&
                (line < e.Line || column <= e.Column),
        };
    }
}

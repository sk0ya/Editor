namespace Editor.Core.Models;

public record struct CursorPosition(int Line, int Column)
{
    public static readonly CursorPosition Zero = new(0, 0);

    public CursorPosition WithLine(int line) => this with { Line = line };
    public CursorPosition WithColumn(int column) => this with { Column = column };

    public override readonly string ToString() => $"Ln{Line + 1}, Col{Column + 1}";
}

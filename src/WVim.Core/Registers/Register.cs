namespace WVim.Core.Registers;

public enum RegisterType { Character, Line, Block }

public record Register(string Text, RegisterType Type)
{
    public static readonly Register Empty = new("", RegisterType.Character);

    public bool IsEmpty => string.IsNullOrEmpty(Text);

    public string[] GetLines() => Text.Split('\n');
}

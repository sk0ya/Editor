namespace Editor.Core.Folds;

/// <summary>言語ごとのシンタックスベース・フォールド検出を定義するインターフェース。</summary>
public interface IFoldLanguage
{
    string[] Extensions { get; }
    IReadOnlyList<(int StartLine, int EndLine)> Detect(string[] lines);
}

using WVim.Core.Models;

namespace WVim.Core.Marks;

public class MarkManager
{
    private readonly Dictionary<char, CursorPosition> _marks = [];
    private readonly List<CursorPosition> _jumpList = [];
    private int _jumpIndex = -1;
    private const int MaxJumpList = 100;

    public void SetMark(char name, CursorPosition pos)
    {
        if (char.IsLetter(name))
            _marks[name] = pos;
    }

    public CursorPosition? GetMark(char name)
    {
        return _marks.TryGetValue(name, out var pos) ? pos : null;
    }

    public void AddJump(CursorPosition pos)
    {
        // Truncate forward history
        if (_jumpIndex >= 0 && _jumpIndex < _jumpList.Count - 1)
            _jumpList.RemoveRange(_jumpIndex + 1, _jumpList.Count - _jumpIndex - 1);

        // Don't add duplicates
        if (_jumpList.Count > 0 && _jumpList[^1] == pos) return;

        _jumpList.Add(pos);
        if (_jumpList.Count > MaxJumpList)
            _jumpList.RemoveAt(0);

        _jumpIndex = _jumpList.Count - 1;
    }

    public CursorPosition? JumpBack()
    {
        if (_jumpIndex <= 0) return null;
        _jumpIndex--;
        return _jumpList[_jumpIndex];
    }

    public CursorPosition? JumpForward()
    {
        if (_jumpIndex >= _jumpList.Count - 1) return null;
        _jumpIndex++;
        return _jumpList[_jumpIndex];
    }

    public void ClearMarks() => _marks.Clear();
}

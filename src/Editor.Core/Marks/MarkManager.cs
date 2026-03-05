using System.Linq;
using Editor.Core.Models;

namespace Editor.Core.Marks;

public class MarkManager
{
    private readonly Dictionary<char, CursorPosition> _marks = [];
    private readonly BoundedHistory _jumps = new(100);
    private readonly BoundedHistory _changes = new(100);

    public void SetMark(char name, CursorPosition pos)
    {
        if (char.IsLetter(name))
            _marks[name] = pos;
    }

    public CursorPosition? GetMark(char name)
    {
        return _marks.TryGetValue(name, out var pos) ? pos : null;
    }

    public void AddJump(CursorPosition pos) => _jumps.Add(pos);
    public CursorPosition? JumpBack() => _jumps.Back();
    public CursorPosition? JumpForward() => _jumps.Forward();

    public void AddChange(CursorPosition pos) => _changes.Add(pos);
    public CursorPosition? ChangeBack() => _changes.Back();
    public CursorPosition? ChangeForward() => _changes.Forward();

    public string FormatChangeList()
    {
        var list = _changes.Items;
        if (list.Count == 0) return "change list is empty";
        int cur = _changes.Index;
        return "change  line  col\n" + string.Join("\n", list.Select(
            (p, i) => $"{(i == cur ? '>' : ' ')} {i + 1,3}  {p.Line + 1,5}  {p.Column,4}"));
    }

    public void ClearMarks() => _marks.Clear();

    private sealed class BoundedHistory(int maxSize)
    {
        private readonly List<CursorPosition> _list = [];
        private int _index = -1;

        public IReadOnlyList<CursorPosition> Items => _list;
        public int Index => _index;

        public void Add(CursorPosition pos)
        {
            if (_index >= 0 && _index < _list.Count - 1)
                _list.RemoveRange(_index + 1, _list.Count - _index - 1);

            if (_list.Count > 0 && _list[^1] == pos) return;

            _list.Add(pos);
            if (_list.Count > maxSize)
                _list.RemoveAt(0);

            _index = _list.Count - 1;
        }

        public CursorPosition? Back()
        {
            if (_index <= 0) return null;
            _index--;
            return _list[_index];
        }

        public CursorPosition? Forward()
        {
            if (_index >= _list.Count - 1) return null;
            _index++;
            return _list[_index];
        }
    }
}

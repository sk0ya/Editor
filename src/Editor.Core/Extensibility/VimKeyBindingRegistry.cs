using Editor.Core.Engine;
using Editor.Core.Macros;
using Editor.Core.Models;

namespace Editor.Core.Extensibility;

/// <summary>The modes in which a key binding is active.</summary>
[Flags]
public enum VimModeSet
{
    None = 0,
    Normal = 1 << 0,
    Insert = 1 << 1,
    Replace = 1 << 2,
    Visual = 1 << 3,
    VisualLine = 1 << 4,
    VisualBlock = 1 << 5,
    Command = 1 << 6,
    SearchForward = 1 << 7,
    SearchBackward = 1 << 8,
    AnyVisual = Visual | VisualLine | VisualBlock,
    AnyInsert = Insert | Replace,
    AnyCommandLine = Command | SearchForward | SearchBackward,
    All = Normal | AnyInsert | AnyVisual | AnyCommandLine,
}

/// <summary>Metadata for a programmable Vim key binding.</summary>
public sealed record VimKeyBindingDescriptor(
    string Id,
    VimModeSet Modes,
    string Keys,
    string? DisplayName = null,
    string? Description = null);

/// <summary>
/// State passed to an extension key handler. Handlers can use the engine's public
/// editing APIs and return the resulting events to the host.
/// </summary>
public sealed record VimKeyBindingContext(
    VimEngine Engine,
    VimMode Mode,
    IReadOnlyList<VimKeyStroke> Strokes);

public delegate IReadOnlyList<VimEvent> VimKeyBindingHandler(VimKeyBindingContext context);

/// <summary>
/// Thread-safe registry for mode-aware, multi-stroke key bindings. Registrations
/// are removable and can temporarily shadow an existing binding.
/// </summary>
public sealed class VimKeyBindingRegistry
{
    internal sealed record Entry(
        long Id,
        VimKeyBindingDescriptor Descriptor,
        IReadOnlyList<VimKeyStroke> Strokes,
        VimKeyBindingHandler Handler);

    internal readonly record struct Match(Entry? Exact, bool HasPrefix, bool HasLongerPrefix);

    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly CommandTable<string, VimMode, Entry> _table =
        new(StringComparer.Ordinal);
    private readonly List<IDisposable> _tableRegistrations = [];
    private volatile Entry[] _activeSnapshot = [];
    private long _nextId;

    public static VimKeyBindingRegistry Default { get; } = new();

    internal bool IsEmpty
    {
        get { lock (_gate) return _entries.Count == 0; }
    }

    public IDisposable Register(
        VimKeyBindingDescriptor descriptor,
        VimKeyBindingHandler handler,
        RegistrationPolicy policy = RegistrationPolicy.Reject)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(descriptor.Id))
            throw new ArgumentException("A binding id is required.", nameof(descriptor));
        if (descriptor.Modes == VimModeSet.None)
            throw new ArgumentException("At least one mode is required.", nameof(descriptor));

        var strokes = KeyMappingResolver.ParseMappingSequence(descriptor.Keys);
        if (strokes.Count == 0)
            throw new ArgumentException("At least one valid key is required.", nameof(descriptor));

        lock (_gate)
        {
            var collision = _entries.LastOrDefault(e =>
                e.Descriptor.Id.Equals(descriptor.Id, StringComparison.OrdinalIgnoreCase));
            if (collision is not null && policy == RegistrationPolicy.Reject)
                throw new InvalidOperationException($"Key binding '{descriptor.Id}' is already registered.");

            var entry = new Entry(++_nextId, descriptor, strokes, handler);
            _entries.Add(entry);
            RebuildTable();
            return new Registration(() => Remove(entry));
        }
    }

    public IReadOnlyList<VimKeyBindingDescriptor> Bindings
    {
        get
        {
            lock (_gate)
                return _entries
                    .GroupBy(e => e.Descriptor.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Last().Descriptor)
                    .OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }
    }

    public IReadOnlyList<CommandTableDiagnostic> Diagnostics =>
        _table.Snapshot.Diagnostics;

    internal Match Resolve(VimMode mode, IReadOnlyList<VimKeyStroke> input)
    {
        var canonical = Canonical(input);
        Entry? exact = null;
        if (_table.TryResolve($"{mode}|{canonical}", mode, out var exactHandler))
            exact = exactHandler(mode);

        bool prefix = false;
        foreach (var entry in _activeSnapshot.Reverse())
        {
            if ((entry.Descriptor.Modes & ToModeSet(mode)) == 0 ||
                entry.Strokes.Count <= input.Count ||
                !StartsWith(entry.Strokes, input))
                continue;
            prefix = true;
            break;
        }

        return new Match(exact, prefix, exact is not null && prefix);
    }

    private IEnumerable<Entry> ActiveEntries(VimMode mode)
    {
        var flag = ToModeSet(mode);
        // Newer registrations win, which gives Replace registrations stack semantics.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = _entries.Count - 1; i >= 0; i--)
            if (seen.Add(_entries[i].Descriptor.Id) && (_entries[i].Descriptor.Modes & flag) != 0)
                yield return _entries[i];
    }

    private static bool StartsWith(IReadOnlyList<VimKeyStroke> candidate, IReadOnlyList<VimKeyStroke> input)
    {
        if (input.Count > candidate.Count) return false;
        for (int i = 0; i < input.Count; i++)
            if (!KeyMappingResolver.AreSameStroke(candidate[i], input[i])) return false;
        return true;
    }

    private static VimModeSet ToModeSet(VimMode mode) => mode switch
    {
        VimMode.Normal => VimModeSet.Normal,
        VimMode.Insert => VimModeSet.Insert,
        VimMode.Replace => VimModeSet.Replace,
        VimMode.Visual => VimModeSet.Visual,
        VimMode.VisualLine => VimModeSet.VisualLine,
        VimMode.VisualBlock => VimModeSet.VisualBlock,
        VimMode.Command => VimModeSet.Command,
        VimMode.SearchForward => VimModeSet.SearchForward,
        VimMode.SearchBackward => VimModeSet.SearchBackward,
        _ => VimModeSet.None,
    };

    private void Remove(Entry entry)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Id == entry.Id);
            RebuildTable();
        }
    }

    private void RebuildTable()
    {
        foreach (var registration in _tableRegistrations)
            registration.Dispose();
        _tableRegistrations.Clear();
        _activeSnapshot = ActiveEntriesForAllModes().ToArray();
        foreach (var entry in _activeSnapshot)
            foreach (var mode in EnumerateModes(entry.Descriptor.Modes))
            {
                var key = $"{mode}|{Canonical(entry.Strokes)}";
                _tableRegistrations.Add(_table.RegisterExact(
                    $"{entry.Descriptor.Id}\0{mode}",
                    key,
                    _ => entry,
                    CommandLayer.Extension,
                    RegistrationPriority(entry.Id)));
            }
    }

    private IEnumerable<Entry> ActiveEntriesForAllModes()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = _entries.Count - 1; i >= 0; i--)
            if (seen.Add(_entries[i].Descriptor.Id))
                yield return _entries[i];
    }

    private static IEnumerable<VimMode> EnumerateModes(VimModeSet modes)
    {
        foreach (var mode in Enum.GetValues<VimMode>())
            if ((modes & ToModeSet(mode)) != 0)
                yield return mode;
    }

    private static string Canonical(IReadOnlyList<VimKeyStroke> strokes) =>
        string.Join("|", strokes.Select(stroke =>
            $"{stroke.Ctrl}:{stroke.Shift}:{stroke.Alt}:{stroke.Key.Length}:{stroke.Key}"));

    private static int RegistrationPriority(long registrationId) =>
        registrationId >= int.MaxValue ? int.MaxValue : (int)registrationId;

    private sealed class Registration(Action action) : IDisposable
    {
        private Action? _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

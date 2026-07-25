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

    internal Match Resolve(VimMode mode, IReadOnlyList<VimKeyStroke> input)
    {
        lock (_gate)
        {
            Entry? exact = null;
            bool prefix = false;
            bool longer = false;

            foreach (var entry in ActiveEntries(mode))
            {
                if (!StartsWith(entry.Strokes, input)) continue;
                if (entry.Strokes.Count == input.Count)
                    exact ??= entry;
                else
                {
                    prefix = true;
                    if (exact is not null) longer = true;
                }
            }

            return new Match(exact, prefix, exact is not null && (longer || prefix));
        }
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
        lock (_gate) _entries.RemoveAll(e => e.Id == entry.Id);
    }

    private sealed class Registration(Action action) : IDisposable
    {
        private Action? _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

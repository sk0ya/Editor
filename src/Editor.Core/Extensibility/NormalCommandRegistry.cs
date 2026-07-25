using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Core.Extensibility;

/// <summary>Metadata for a programmable Normal-mode command.</summary>
public sealed record NormalCommandDescriptor(
    string Id,
    IReadOnlyList<string> Motions,
    string? DisplayName = null,
    string? Description = null);

/// <summary>State passed to a registered Normal-mode command handler.</summary>
public sealed record NormalCommandContext(VimEngine Engine, ParsedCommand Command);

public delegate IReadOnlyList<VimEvent> NormalCommandHandler(NormalCommandContext context);

/// <summary>
/// Thread-safe registry for parsed Normal-mode commands. Registrations are
/// removable and replacements use stack semantics.
/// </summary>
public sealed class NormalCommandRegistry
{
    private sealed record Entry(
        long Id,
        NormalCommandDescriptor Descriptor,
        NormalCommandHandler Handler);

    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private long _nextId;

    public static NormalCommandRegistry Default { get; } = new();

    public IDisposable Register(
        NormalCommandDescriptor descriptor,
        NormalCommandHandler handler,
        RegistrationPolicy policy = RegistrationPolicy.Reject)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(descriptor.Id))
            throw new ArgumentException("A command id is required.", nameof(descriptor));
        if (descriptor.Motions.Count == 0 ||
            descriptor.Motions.Any(string.IsNullOrEmpty) ||
            descriptor.Motions.Distinct(StringComparer.Ordinal).Count() != descriptor.Motions.Count)
            throw new ArgumentException("Command motions must be non-empty and unique.", nameof(descriptor));

        lock (_gate)
        {
            var collision = _entries.LastOrDefault(e =>
                e.Descriptor.Id.Equals(descriptor.Id, StringComparison.OrdinalIgnoreCase));
            if (collision is not null && policy == RegistrationPolicy.Reject)
                throw new InvalidOperationException($"Normal command '{descriptor.Id}' is already registered.");

            var stable = descriptor with { Motions = Array.AsReadOnly(descriptor.Motions.ToArray()) };
            var entry = new Entry(++_nextId, stable, handler);
            _entries.Add(entry);
            return new Registration(() => Remove(entry));
        }
    }

    public IReadOnlyList<NormalCommandDescriptor> Commands
    {
        get
        {
            lock (_gate)
                return ActiveEntries()
                    .Select(e => e.Descriptor)
                    .OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }
    }

    internal bool TryResolve(string? motion, out NormalCommandHandler handler)
    {
        if (motion is null)
        {
            handler = null!;
            return false;
        }

        lock (_gate)
        {
            foreach (var entry in ActiveEntries())
            {
                if (!entry.Descriptor.Motions.Contains(motion, StringComparer.Ordinal)) continue;
                handler = entry.Handler;
                return true;
            }
        }

        handler = null!;
        return false;
    }

    private IEnumerable<Entry> ActiveEntries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = _entries.Count - 1; i >= 0; i--)
            if (seen.Add(_entries[i].Descriptor.Id))
                yield return _entries[i];
    }

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

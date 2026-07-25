namespace Editor.Core.Extensibility;

public enum CommandLayer
{
    BuiltIn = 0,
    Extension = 100,
    User = 200
}

public sealed record CommandTableDiagnostic(
    string Id,
    string Message,
    bool IsUnreachable);

public sealed record CommandTableEntry<TKey, TContext, TResult>(
    long RegistrationId,
    string Id,
    CommandLayer Layer,
    int Priority,
    TKey? Key,
    Func<TKey, TContext, bool>? Pattern,
    Func<TContext, TResult> Handler)
{
    public bool IsExact => Pattern is null;
}

public sealed record CommandTableSnapshot<TKey, TContext, TResult>(
    IReadOnlyList<CommandTableEntry<TKey, TContext, TResult>> Entries,
    IReadOnlyList<CommandTableDiagnostic> Diagnostics);

/// <summary>
/// Layered exact/pattern command resolver with immutable read snapshots.
/// </summary>
public sealed class CommandTable<TKey, TContext, TResult>
    where TKey : notnull
{
    private readonly object _gate = new();
    private readonly IEqualityComparer<TKey> _comparer;
    private readonly List<CommandTableEntry<TKey, TContext, TResult>> _entries = [];
    private long _nextId;
    private volatile CommandTableSnapshot<TKey, TContext, TResult> _snapshot =
        new([], []);
    private bool _dirty;

    public CommandTable(IEqualityComparer<TKey>? comparer = null)
    {
        _comparer = comparer ?? EqualityComparer<TKey>.Default;
    }

    public CommandTableSnapshot<TKey, TContext, TResult> Snapshot
    {
        get
        {
            lock (_gate)
            {
                if (_dirty)
                    RebuildSnapshot();
                return _snapshot;
            }
        }
    }

    public IDisposable RegisterExact(
        string id,
        TKey key,
        Func<TContext, TResult> handler,
        CommandLayer layer = CommandLayer.Extension,
        int priority = 0) =>
        Register(new(
            0, id, layer, priority, key, null, handler));

    public IDisposable RegisterPattern(
        string id,
        Func<TKey, TContext, bool> pattern,
        Func<TContext, TResult> handler,
        CommandLayer layer = CommandLayer.Extension,
        int priority = 0) =>
        Register(new(
            0, id, layer, priority, default, pattern, handler));

    public bool TryResolve(
        TKey key,
        TContext context,
        out Func<TContext, TResult> handler)
    {
        foreach (var entry in Snapshot.Entries)
        {
            var matches = entry.IsExact
                ? entry.Key is not null && _comparer.Equals(entry.Key, key)
                : entry.Pattern!(key, context);
            if (!matches)
                continue;
            handler = entry.Handler;
            return true;
        }

        handler = null!;
        return false;
    }

    private IDisposable Register(
        CommandTableEntry<TKey, TContext, TResult> candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Id);
        ArgumentNullException.ThrowIfNull(candidate.Handler);
        lock (_gate)
        {
            var entry = candidate with { RegistrationId = ++_nextId };
            _entries.Add(entry);
            _dirty = true;
            return new Registration(() => Remove(entry.RegistrationId));
        }
    }

    private void Remove(long registrationId)
    {
        lock (_gate)
        {
            _entries.RemoveAll(entry => entry.RegistrationId == registrationId);
            _dirty = true;
        }
    }

    private void RebuildSnapshot()
    {
        var activeById = _entries
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(entry => entry.Layer)
                .ThenByDescending(entry => entry.Priority)
                .ThenByDescending(entry => entry.RegistrationId)
                .First())
            .OrderByDescending(entry => entry.Layer)
            .ThenByDescending(entry => entry.Priority)
            .ThenByDescending(entry => entry.IsExact)
            .ThenByDescending(entry => entry.RegistrationId)
            .ToArray();

        var diagnostics = new List<CommandTableDiagnostic>();
        var exactWinners = new Dictionary<TKey, CommandTableEntry<TKey, TContext, TResult>>(
            _comparer);
        foreach (var entry in activeById.Where(entry => entry.IsExact))
        {
            if (entry.Key is null)
                continue;
            if (!exactWinners.TryAdd(entry.Key, entry))
            {
                var winner = exactWinners[entry.Key];
                diagnostics.Add(new(entry.Id,
                    $"Exact registration is shadowed by '{winner.Id}' in layer {winner.Layer}.",
                    IsUnreachable: true));
            }
        }

        _snapshot = new CommandTableSnapshot<TKey, TContext, TResult>(
            activeById,
            diagnostics);
        _dirty = false;
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}

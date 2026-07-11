using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Core.Extensibility;

public sealed record EditorCommandContext(string Name, string Arguments, string RawCommand, string Range,
    CursorPosition Cursor, IServiceProvider? Services = null);
public sealed record EditorCommandResult(bool Success, string? Message = null, VimEvent? Event = null, bool TextModified = false)
{
    public static EditorCommandResult Ok(string? message = null) => new(true, message);
    public static EditorCommandResult Error(string message) => new(false, message);
}
public delegate EditorCommandResult EditorCommandHandler(EditorCommandContext context);
public delegate ValueTask<EditorCommandResult> AsyncEditorCommandHandler(EditorCommandContext context, CancellationToken cancellationToken);
public sealed record EditorCommandDescriptor(string Name, string? DisplayName = null, string? Description = null,
    IReadOnlyList<string>? Aliases = null, bool IsAsync = false);

/// <summary>Thread-safe registry. Returned handles own registrations; snapshots are immutable and name-sorted.</summary>
public sealed class EditorCommandRegistry
{
    private sealed record Entry(long Id, EditorCommandDescriptor Descriptor, EditorCommandHandler? Sync, AsyncEditorCommandHandler? Async, string[] Names);
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Entry>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId;
    public static EditorCommandRegistry Default { get; } = new();

    public IDisposable Register(EditorCommandDescriptor descriptor, EditorCommandHandler handler, RegistrationPolicy policy = RegistrationPolicy.Reject)
        => RegisterCore(descriptor with { IsAsync = false }, handler, null, policy);
    public IDisposable RegisterAsync(EditorCommandDescriptor descriptor, AsyncEditorCommandHandler handler, RegistrationPolicy policy = RegistrationPolicy.Reject)
        => RegisterCore(descriptor with { IsAsync = true }, null, handler, policy);

    private IDisposable RegisterCore(EditorCommandDescriptor descriptor, EditorCommandHandler? sync, AsyncEditorCommandHandler? async, RegistrationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var requested = new[] { descriptor.Name }.Concat(descriptor.Aliases ?? []).ToArray();
        if (requested.Any(string.IsNullOrWhiteSpace) || requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Length)
            throw new ArgumentException("Command names must be non-empty and unique.", nameof(descriptor));
        lock (_gate)
        {
            var collisions = requested.SelectMany(n => _entries.TryGetValue(n, out var x) && x.Count > 0 ? [x[^1]] : Array.Empty<Entry>()).DistinctBy(e => e.Id).ToArray();
            if (collisions.Length > 0 && policy == RegistrationPolicy.Reject) throw new InvalidOperationException($"Command '{requested.First(n => _entries.ContainsKey(n))}' is already registered.");
            // Replacing any identity shadows that registration's complete alias set atomically.
            var names = requested.Concat(collisions.SelectMany(e => e.Names)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var stableDescriptor = descriptor with { Aliases = Array.AsReadOnly((descriptor.Aliases ?? []).ToArray()) };
            var entry = new Entry(++_nextId, stableDescriptor, sync, async, names);
            foreach (var name in names) (_entries.TryGetValue(name, out var list) ? list : _entries[name] = []).Add(entry);
            return new Registration(() => Remove(entry));
        }
    }

    public IReadOnlyList<EditorCommandDescriptor> Commands { get { lock (_gate) return _entries.Values.Where(x => x.Count > 0).Select(x => x[^1]).DistinctBy(x => x.Id).Select(x => x.Descriptor).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(); } }

    public ValueTask<EditorCommandResult?> ExecuteAsync(string line, CursorPosition cursor, IServiceProvider? services = null, CancellationToken token = default)
        => ExecuteAsync(line, "", cursor, services, token);
    public async ValueTask<EditorCommandResult?> ExecuteAsync(string line, string range, CursorPosition cursor, IServiceProvider? services = null, CancellationToken token = default)
        => await ExecuteParsedAsync(line, line, range, cursor, services, token).ConfigureAwait(false);

    internal async ValueTask<EditorCommandResult?> ExecuteParsedAsync(string command, string raw, string range,
        CursorPosition cursor, IServiceProvider? services, CancellationToken token)
    {
        if (!TryResolve(command, out var e, out var name, out var args)) return null;
        var context = new EditorCommandContext(name, args, raw, range, cursor, services);
        return e.Sync is not null ? e.Sync(context) : await e.Async!(context, token).ConfigureAwait(false);
    }

    internal bool TryExecuteSynchronously(string command, string raw, string range, CursorPosition cursor, IServiceProvider? services, out ExResult result)
    {
        if (!TryResolve(command, out var e, out var name, out var args)) { result = default!; return false; }
        if (e.Sync is null) { result = new(false, $"Command '{name}' is asynchronous; invoke EditorCommandRegistry.ExecuteAsync."); return true; }
        var value = e.Sync(new(name, args, raw, range, cursor, services));
        result = new(value.Success, value.Message, value.Event, value.TextModified); return true;
    }

    private bool TryResolve(string line, out Entry entry, out string name, out string args)
    {
        var text = line.Trim().TrimStart(':').TrimStart(); var split = text.IndexOfAny([' ', '\t', '\r', '\n']);
        name = split < 0 ? text : text[..split]; args = split < 0 ? "" : text[(split + 1)..].Trim();
        lock (_gate) if (_entries.TryGetValue(name, out var list) && list.Count > 0) { entry = list[^1]; return true; }
        entry = null!; return false;
    }
    private void Remove(Entry entry) { lock (_gate) foreach (var name in entry.Names) if (_entries.TryGetValue(name, out var list)) { list.RemoveAll(x => x.Id == entry.Id); if (list.Count == 0) _entries.Remove(name); } }
    private sealed class Registration(Action action) : IDisposable { private Action? _action = action; public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke(); }
}

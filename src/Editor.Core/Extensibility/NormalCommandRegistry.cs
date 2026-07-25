using Editor.Core.Engine;
using Editor.Core.Editing;
using Editor.Core.Models;
using Editor.Core.Registers;

namespace Editor.Core.Extensibility;

/// <summary>Metadata for a programmable Normal-mode command.</summary>
public sealed record NormalCommandDescriptor(
    string Id,
    IReadOnlyList<string> Motions,
    string? DisplayName = null,
    string? Description = null);

/// <summary>Read-only buffer capability exposed to Normal command extensions.</summary>
public interface INormalBufferView
{
    int LineCount { get; }
    string GetLine(int index);
    int GetLineLength(int index);
    string GetText();
}

/// <summary>
/// Capabilities available to a Normal command. Buffer changes can only be made through
/// <see cref="Edit"/>, which preserves the engine's undo and event invariants.
/// </summary>
public interface INormalCommandContext
{
    ParsedCommand Command { get; }
    INormalBufferView Buffer { get; }
    CursorPosition Cursor { get; }
    Selection? Selection { get; }
    VimMode Mode { get; }
    string? FilePath { get; }

    Motion? CalculateMotion(string motion, int count = 1);
    EditTransactionResult Edit(Action<EditTransaction> mutation);
    void MoveCursor(CursorPosition cursor);
    Register GetRegister(char name);
    void SetRegister(char name, Register value);
}

internal sealed class NormalCommandContext(
    ParsedCommand command,
    INormalBufferView buffer,
    Func<CursorPosition> getCursor,
    Func<Selection?> getSelection,
    Func<VimMode> getMode,
    Func<string?> getFilePath,
    Func<string, int, Motion?> calculateMotion,
    Func<Action<EditTransaction>, EditTransactionResult> edit,
    Action<CursorPosition> moveCursor,
    Func<char, Register> getRegister,
    Action<char, Register> setRegister) : INormalCommandContext
{
    public ParsedCommand Command { get; } = command;
    public INormalBufferView Buffer { get; } = buffer;
    public CursorPosition Cursor => getCursor();
    public Selection? Selection => getSelection();
    public VimMode Mode => getMode();
    public string? FilePath => getFilePath();
    public Motion? CalculateMotion(string motion, int count = 1) => calculateMotion(motion, count);
    public EditTransactionResult Edit(Action<EditTransaction> mutation) => edit(mutation);
    public void MoveCursor(CursorPosition cursor) => moveCursor(cursor);
    public Register GetRegister(char name) => getRegister(name);
    public void SetRegister(char name, Register value) => setRegister(name, value);
}

public delegate IReadOnlyList<VimEvent> NormalCommandHandler(INormalCommandContext context);

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
    private readonly CommandTable<
        string,
        INormalCommandContext,
        IReadOnlyList<VimEvent>> _table = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _tableRegistrations = [];
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
            RebuildTable();
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

    public IReadOnlyList<CommandTableDiagnostic> Diagnostics =>
        _table.Snapshot.Diagnostics;

    internal bool TryResolve(
        string? motion,
        INormalCommandContext context,
        out NormalCommandHandler handler)
    {
        if (motion is null)
        {
            handler = null!;
            return false;
        }

        if (_table.TryResolve(motion, context, out var resolved))
        {
            handler = commandContext => resolved(commandContext);
            return true;
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
        // Register oldest first so the command table's newest-registration
        // tiebreaker preserves the registry's existing newest-wins behavior.
        foreach (var entry in ActiveEntries().Reverse())
            foreach (var motion in entry.Descriptor.Motions)
                _tableRegistrations.Add(_table.RegisterExact(
                    $"{entry.Descriptor.Id}\0{motion}",
                    motion,
                    context => entry.Handler(context),
                    CommandLayer.Extension,
                    RegistrationPriority(entry.Id)));
    }

    private static int RegistrationPriority(long registrationId) =>
        registrationId >= int.MaxValue ? int.MaxValue : (int)registrationId;

    private sealed class Registration(Action action) : IDisposable
    {
        private Action? _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

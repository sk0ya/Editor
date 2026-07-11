using Editor.Core.Models;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace Editor.Core.Buffer;

public record UndoState
{
    [JsonConstructor]
    public UndoState(string[] lines, CursorPosition cursor, int changeNumber, DateTimeOffset timestamp)
        => (Lines, Cursor, ChangeNumber, Timestamp) = (lines, cursor, changeNumber, timestamp);

    public UndoState(string[] lines, CursorPosition cursor)
        : this(lines, cursor, 0, DateTimeOffset.Now)
    {
    }

    public string[] Lines { get; init; }
    public CursorPosition Cursor { get; init; }
    public int ChangeNumber { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public record UndoHistoryEntry(int Number, string State, DateTimeOffset Timestamp);
public record UndoTraversalResult(int Count, UndoState? State);

/// <summary>
/// A branch of history that was abandoned when a new edit was made after undoing.
/// <see cref="ForkChangeNumber"/> is the change number at the top of the undo stack
/// at the moment the branch split off (0 if the undo stack was empty then).
/// </summary>
public record UndoBranch(int Index, int ForkChangeNumber, DateTimeOffset ArchivedAt, int StateCount);
public sealed record UndoTreeNode(int ChangeNumber, int ParentChangeNumber, DateTimeOffset Timestamp, string Location, int? BranchIndex);
public sealed record UndoTree(IReadOnlyList<UndoTreeNode> Nodes, int CurrentChangeNumber);

/// <summary>Limits applied when reading an undo-history file supplied by an external host.</summary>
public sealed record UndoPersistenceLimits(
    int MaxStates = 5000,
    int MaxLinesPerState = 1_000_000,
    int MaxCharactersPerState = 64 * 1024 * 1024,
    long MaxFileBytes = 256L * 1024 * 1024);

/// <summary>Result of importing persisted undo history.</summary>
public sealed record UndoImportResult(bool Success, string? Error = null);

public class UndoManager
{
    private const int PersistenceVersion = 2;
    private readonly Stack<UndoState> _undoStack = new();
    private readonly Stack<UndoState> _redoStack = new();
    private readonly List<ArchivedBranch> _archivedBranches = new();
    private readonly Func<DateTimeOffset> _clock;
    private const int MaxHistory = 1000;
    private const int MaxArchivedBranches = 50;
    private int _nextChangeNumber;
    private int _currentChangeNumber;
    private UndoState? _currentState;

    private record ArchivedBranch(int ForkChangeNumber, DateTimeOffset ArchivedAt, List<UndoState> States);

    private sealed record PersistenceDocument(
        int Version,
        string? DocumentId,
        string? CurrentContentHash,
        int NextChangeNumber,
        int CurrentChangeNumber,
        UndoState? CurrentState,
        List<UndoState> Undo,
        List<UndoState> Redo,
        List<PersistenceBranch> Branches);
    private sealed record PersistenceBranch(int ForkChangeNumber, DateTimeOffset ArchivedAt, List<UndoState> States);

    private static readonly JsonSerializerOptions PersistenceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public UndoManager(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>Serializes all linear and archived undo branches. The current buffer text is not included.</summary>
    [Obsolete("Unbound undo data can attach to the wrong document. Use ExportHistory(TextBuffer, documentId).")]
    public string ExportHistory()
        => ExportHistoryCore(null, null);

    /// <summary>Exports history bound to a stable document identity and its exact current contents.</summary>
    public string ExportHistory(TextBuffer buffer, string documentId)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return ExportHistoryCore(documentId, ComputeContentHash(buffer), buffer);
    }

    private string ExportHistoryCore(string? documentId, string? contentHash, TextBuffer? currentBuffer = null)
    {
        var persistedCurrent = _currentState == null ? null : CloneState(_currentState);
        if (persistedCurrent != null && currentBuffer != null)
            persistedCurrent = persistedCurrent with
            {
                Lines = currentBuffer.Snapshot(),
                Cursor = currentBuffer.ClampCursor(persistedCurrent.Cursor)
            };
        var document = new PersistenceDocument(
            PersistenceVersion,
            documentId,
            contentHash,
            _nextChangeNumber,
            _currentChangeNumber,
            persistedCurrent,
            _undoStack.ToList(), // Stack enumeration is top first; import preserves that ordering.
            _redoStack.ToList(),
            _archivedBranches.Select(b => new PersistenceBranch(
                b.ForkChangeNumber, b.ArchivedAt, b.States.ToList())).ToList());
        return JsonSerializer.Serialize(document, PersistenceJsonOptions);
    }

    /// <summary>Atomically writes <see cref="ExportHistory"/> to disk.</summary>
    [Obsolete("Unbound undo data can attach to the wrong document. Use SaveHistory(path, TextBuffer, documentId).")]
    public void SaveHistory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        WriteHistoryAtomically(path, ExportHistoryCore(null, null));
    }

    public void SaveHistory(string path, TextBuffer buffer, string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        WriteHistoryAtomically(path, ExportHistory(buffer, documentId));
    }

    private static void WriteHistoryAtomically(string path, string json)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            { writer.Write(json); writer.Flush(); stream.Flush(true); }
            File.Move(temporaryPath, fullPath, true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    /// <summary>
    /// Imports a versioned history document. Invalid data never partially changes this manager.
    /// Hosts should only load history belonging to the same document revision.
    /// </summary>
    [Obsolete("Unbound undo data can attach to the wrong document. Use ImportHistory(json, TextBuffer, documentId).")]
    public UndoImportResult ImportHistory(string json, UndoPersistenceLimits? limits = null)
    {
        limits ??= new UndoPersistenceLimits();
        if (json == null) return new(false, "Undo history is null.");
        if (Encoding.UTF8.GetByteCount(json) > limits.MaxFileBytes)
            return new(false, "Undo history exceeds the configured file-size limit.");

        PersistenceDocument? document;
        try { document = JsonSerializer.Deserialize<PersistenceDocument>(json, PersistenceJsonOptions); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        { return new(false, $"Invalid undo history JSON: {ex.Message}"); }
        if (document == null) return new(false, "Undo history is empty.");
        var error = ValidatePersistenceDocument(document, limits);
        if (error != null) return new(false, error);

        return ApplyPersistenceDocument(document);
    }

    public UndoImportResult ImportHistory(string json, TextBuffer buffer, string documentId, UndoPersistenceLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        limits ??= new UndoPersistenceLimits();
        if (Encoding.UTF8.GetByteCount(json) > limits.MaxFileBytes) return new(false, "Undo history exceeds the configured file-size limit.");
        PersistenceDocument? document;
        try { document = JsonSerializer.Deserialize<PersistenceDocument>(json, PersistenceJsonOptions); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return new(false, $"Invalid undo history JSON: {ex.Message}"); }
        if (document == null) return new(false, "Undo history is empty.");
        var error = ValidatePersistenceDocument(document, limits);
        if (error != null) return new(false, error);
        if (!StringComparer.Ordinal.Equals(document.DocumentId, documentId)) return new(false, "Undo history belongs to a different document.");
        if (!StringComparer.Ordinal.Equals(document.CurrentContentHash, ComputeContentHash(buffer))) return new(false, "Undo history does not match the current document contents.");
        if (document.CurrentState == null || !document.CurrentState.Lines.SequenceEqual(buffer.Snapshot()))
            return new(false, "Undo history current state does not match the current document contents.");
        return ApplyPersistenceDocument(document);
    }

    private UndoImportResult ApplyPersistenceDocument(PersistenceDocument document)
    {

        _undoStack.Clear();
        _redoStack.Clear();
        _archivedBranches.Clear();
        PushTopFirst(_undoStack, document.Undo);
        PushTopFirst(_redoStack, document.Redo);
        _archivedBranches.AddRange(document.Branches.Select(b =>
            new ArchivedBranch(b.ForkChangeNumber, b.ArchivedAt, b.States.Select(CloneState).ToList())));
        _nextChangeNumber = document.NextChangeNumber;
        _currentChangeNumber = document.CurrentChangeNumber;
        _currentState = document.CurrentState == null ? null : CloneState(document.CurrentState);
        return new(true);
    }

    /// <summary>Loads history from disk subject to strict resource limits.</summary>
    [Obsolete("Unbound undo data can attach to the wrong document. Use LoadHistory(path, TextBuffer, documentId).")]
    public UndoImportResult LoadHistory(string path, UndoPersistenceLimits? limits = null)
    {
        limits ??= new UndoPersistenceLimits();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            if (stream.Length > limits.MaxFileBytes) return new(false, "Undo history exceeds the configured file-size limit.");
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
            var json = reader.ReadToEnd(); // bounded by the length checked on this same open handle
            return ImportHistory(json, limits);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(false, $"Unable to read undo history: {ex.Message}"); }
    }

    public UndoImportResult LoadHistory(string path, TextBuffer buffer, string documentId, UndoPersistenceLimits? limits = null)
    {
        limits ??= new UndoPersistenceLimits();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            if (stream.Length > limits.MaxFileBytes) return new(false, "Undo history exceeds the configured file-size limit.");
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096);
            return ImportHistory(reader.ReadToEnd(), buffer, documentId, limits);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(false, $"Unable to read undo history: {ex.Message}"); }
    }

    private static string ComputeContentHash(TextBuffer buffer)
    {
        var bytes = Encoding.UTF8.GetBytes(buffer.GetText());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void PushTopFirst(Stack<UndoState> stack, IEnumerable<UndoState> topFirst)
    {
        foreach (var state in topFirst.Reverse()) stack.Push(CloneState(state));
    }

    private static UndoState CloneState(UndoState state) =>
        new([.. state.Lines], state.Cursor, state.ChangeNumber, state.Timestamp);

    private static string? ValidatePersistenceDocument(PersistenceDocument d, UndoPersistenceLimits limits)
    {
        if (d.Version != PersistenceVersion) return $"Unsupported undo history version {d.Version}.";
        if (limits.MaxStates < 0 || limits.MaxLinesPerState < 1 || limits.MaxCharactersPerState < 0 || limits.MaxFileBytes < 0)
            return "Undo persistence limits are invalid.";
        if (d.Undo == null || d.Redo == null || d.Branches == null) return "Undo history is incomplete.";
        if (d.Branches.Count > MaxArchivedBranches) return "Undo history has too many branches.";
        if (d.Undo.Count > MaxHistory || d.Redo.Count > MaxHistory) return "Undo history exceeds the runtime history limit.";
        var states = d.Undo.Concat(d.Redo).Concat(d.Branches.SelectMany(b => b.States ?? []));
        var count = 0;
        var maxChange = 0;
        var allChangeNumbers = new HashSet<int>();
        foreach (var state in states)
        {
            if (++count > limits.MaxStates) return "Undo history has too many states.";
            if (state?.Lines == null || state.Lines.Length == 0 || state.Lines.Length > limits.MaxLinesPerState)
                return "Undo history contains an invalid line collection.";
            long characters = 0;
            foreach (var line in state.Lines)
            {
                if (line == null || line.Contains('\n') || line.Contains('\r')) return "Undo history contains an invalid line.";
                characters += line.Length;
                if (characters > limits.MaxCharactersPerState) return "An undo state exceeds the character limit.";
            }
            if (state.ChangeNumber <= 0) return "Undo history contains an invalid change number.";
            if (state.Timestamp == default) return "Undo history contains an invalid timestamp.";
            if (!allChangeNumbers.Add(state.ChangeNumber)) return "Undo history contains duplicate change numbers.";
            if (state.Cursor.Line < 0 || state.Cursor.Line >= state.Lines.Length || state.Cursor.Column < 0 ||
                state.Cursor.Column > state.Lines[state.Cursor.Line].Length) return "Undo history contains an invalid cursor.";
            maxChange = Math.Max(maxChange, state.ChangeNumber);
        }
        if (d.NextChangeNumber < maxChange || d.NextChangeNumber < 0 || d.CurrentChangeNumber < 0 || d.CurrentChangeNumber > d.NextChangeNumber)
            return "Undo history change sequence is invalid.";
        if (d.CurrentChangeNumber > 0 && (d.CurrentState == null || d.CurrentState.ChangeNumber != d.CurrentChangeNumber))
            return "Undo history current state is invalid.";
        if (d.CurrentState != null)
        {
            if (++count > limits.MaxStates) return "Undo history has too many states.";
            var currentError = ValidateState(d.CurrentState, limits);
            if (currentError != null) return currentError;
        }
        foreach (var branch in d.Branches)
            if (branch == null || branch.States == null || branch.States.Count == 0 || branch.ForkChangeNumber < 0 ||
                branch.ForkChangeNumber > d.NextChangeNumber) return "Undo history contains an invalid branch.";
        if (!ValidateDescendingTopFirst(d.Undo) || !ValidateAscendingTopFirst(d.Redo))
            return "Undo history contains an invalid stack ordering.";
        foreach (var branch in d.Branches)
            if (!ValidateForwardSequence(branch.States)) return "Undo history contains an invalid branch ordering.";
        return null;
    }

    private static string? ValidateState(UndoState state, UndoPersistenceLimits limits)
    {
        if (state.Lines == null || state.Lines.Length == 0 || state.Lines.Length > limits.MaxLinesPerState)
            return "Undo history contains an invalid line collection.";
        long characters = 0;
        foreach (var line in state.Lines)
        {
            if (line == null || line.Contains('\n') || line.Contains('\r')) return "Undo history contains an invalid line.";
            characters += line.Length;
            if (characters > limits.MaxCharactersPerState) return "An undo state exceeds the character limit.";
        }
        if (state.ChangeNumber <= 0 || state.Timestamp == default) return "Undo history contains an invalid current state.";
        if (state.Cursor.Line < 0 || state.Cursor.Line >= state.Lines.Length || state.Cursor.Column < 0 ||
            state.Cursor.Column > state.Lines[state.Cursor.Line].Length) return "Undo history contains an invalid cursor.";
        return null;
    }

    private static bool ValidateDescendingTopFirst(IReadOnlyList<UndoState> states)
    {
        var ids = new HashSet<int>();
        for (var i = 0; i < states.Count; i++)
            if (!ids.Add(states[i].ChangeNumber) || (i > 0 && states[i - 1].ChangeNumber <= states[i].ChangeNumber)) return false;
        return true;
    }

    private static bool ValidateAscendingTopFirst(IReadOnlyList<UndoState> states)
    {
        var ids = new HashSet<int>();
        for (var i = 0; i < states.Count; i++)
            if (!ids.Add(states[i].ChangeNumber) || (i > 0 && states[i - 1].ChangeNumber >= states[i].ChangeNumber)) return false;
        return true;
    }

    private static bool ValidateForwardSequence(IReadOnlyList<UndoState> states)
    {
        var ids = new HashSet<int>();
        for (var i = 0; i < states.Count; i++)
            if (!ids.Add(states[i].ChangeNumber) || (i > 0 && states[i - 1].ChangeNumber >= states[i].ChangeNumber)) return false;
        return true;
    }

    public void Snapshot(TextBuffer buffer, CursorPosition cursor)
    {
        Snapshot(buffer.Snapshot(), cursor);
    }

    public void Snapshot(string[] lines, CursorPosition cursor)
    {
        if (_redoStack.Count > 0)
            ArchiveRedoStack(_nextChangeNumber + 1); // the checkpoint below names this exact fork content
        if (_undoStack.Count >= MaxHistory)
        {
            // Remove oldest
            var arr = _undoStack.ToArray();
            _undoStack.Clear();
            for (int i = arr.Length - 1; i > 0; i--)
                _undoStack.Push(arr[i]);
        }
        var checkpoint = new UndoState([.. lines], cursor, ++_nextChangeNumber, _clock());
        _undoStack.Push(checkpoint);
        _currentChangeNumber = _nextChangeNumber;
        _currentState = CloneState(checkpoint);
    }

    // Preserves an abandoned "future" (the redo stack) instead of discarding it, so it
    // can later be rediscovered via ListBranches/SwitchToBranch/JumpToChangeNumber.
    private void ArchiveRedoStack(int? forkChangeNumber = null)
    {
        var fork = forkChangeNumber ?? _currentChangeNumber;
        _archivedBranches.Add(new ArchivedBranch(fork, _clock(), _redoStack.ToList()));
        if (_archivedBranches.Count > MaxArchivedBranches)
            _archivedBranches.RemoveAt(0);
        _redoStack.Clear();
    }

    public UndoState? Undo(TextBuffer buffer, CursorPosition currentCursor)
    {
        if (!CanUndo) return null;
        var state = _undoStack.Pop();
        _redoStack.Push(new UndoState(buffer.Snapshot(), currentCursor, state.ChangeNumber, state.Timestamp));
        buffer.RestoreSnapshot(state.Lines);
        _currentChangeNumber = state.ChangeNumber;
        _currentState = CloneState(state);
        return state;
    }

    public UndoState? Redo(TextBuffer buffer, CursorPosition currentCursor)
    {
        if (!CanRedo) return null;
        var state = _redoStack.Pop();
        _undoStack.Push(new UndoState(buffer.Snapshot(), currentCursor, state.ChangeNumber, state.Timestamp));
        buffer.RestoreSnapshot(state.Lines);
        _currentChangeNumber = state.ChangeNumber;
        _currentState = CloneState(state);
        return state;
    }

    public UndoTraversalResult Earlier(TextBuffer buffer, CursorPosition currentCursor, int count)
    {
        return MoveEarlier(buffer, currentCursor, () => count-- > 0);
    }

    public UndoTraversalResult Later(TextBuffer buffer, CursorPosition currentCursor, int count)
    {
        return MoveLater(buffer, currentCursor, () => count-- > 0);
    }

    public UndoTraversalResult Earlier(TextBuffer buffer, CursorPosition currentCursor, TimeSpan age)
    {
        if (_undoStack.Count == 0) return new UndoTraversalResult(0, null);
        var target = _undoStack.Peek().Timestamp - age;
        return MoveEarlier(buffer, currentCursor, () => _undoStack.Count > 0 && _undoStack.Peek().Timestamp > target);
    }

    public UndoTraversalResult Later(TextBuffer buffer, CursorPosition currentCursor, TimeSpan age)
    {
        if (_redoStack.Count == 0) return new UndoTraversalResult(0, null);
        var currentTimestamp = _undoStack.Count > 0
            ? _undoStack.Peek().Timestamp
            : _redoStack.Peek().Timestamp - TimeSpan.FromTicks(1);
        var target = currentTimestamp + age;
        return MoveLater(buffer, currentCursor, () => _redoStack.Count > 0 && _redoStack.Peek().Timestamp <= target);
    }

    private UndoTraversalResult MoveEarlier(TextBuffer buffer, CursorPosition currentCursor, Func<bool> shouldContinue)
    {
        var count = 0;
        UndoState? last = null;
        var cursor = currentCursor;

        while (CanUndo && shouldContinue())
        {
            last = Undo(buffer, cursor);
            if (last == null) break;
            cursor = buffer.ClampCursor(last.Cursor);
            count++;
        }

        return new UndoTraversalResult(count, last);
    }

    private UndoTraversalResult MoveLater(TextBuffer buffer, CursorPosition currentCursor, Func<bool> shouldContinue)
    {
        var count = 0;
        UndoState? last = null;
        var cursor = currentCursor;

        while (CanRedo && shouldContinue())
        {
            last = Redo(buffer, cursor);
            if (last == null) break;
            cursor = buffer.ClampCursor(last.Cursor);
            count++;
        }

        return new UndoTraversalResult(count, last);
    }

    public IReadOnlyList<UndoHistoryEntry> GetHistory()
    {
        var undoEntries = _undoStack.Reverse().ToArray();
        var redoEntries = _redoStack.ToArray();
        var history = new List<UndoHistoryEntry>(undoEntries.Length + redoEntries.Length);

        for (int i = 0; i < undoEntries.Length; i++)
        {
            var state = i == undoEntries.Length - 1 ? "current" : "done";
            history.Add(new UndoHistoryEntry(undoEntries[i].ChangeNumber, state, undoEntries[i].Timestamp));
        }

        foreach (var state in redoEntries)
            history.Add(new UndoHistoryEntry(state.ChangeNumber, "redo", state.Timestamp));

        return history;
    }

    public string FormatUndoList()
    {
        var history = GetHistory();
        var sb = new StringBuilder();

        if (history.Count == 0)
        {
            sb.Append("undo list is empty");
        }
        else
        {
            sb.AppendLine("number  state    time");
            foreach (var entry in history)
            {
                var marker = entry.State == "current" ? '>' : ' ';
                sb.AppendLine($"{marker} {entry.Number,5}  {entry.State,-7}  {entry.Timestamp:HH:mm:ss}");
            }
        }

        if (_archivedBranches.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("branches:");
            for (int i = 0; i < _archivedBranches.Count; i++)
            {
                var branch = _archivedBranches[i];
                var numbers = branch.States.Select(s => s.ChangeNumber).ToList();
                var range = numbers.Min() == numbers.Max() ? $"{numbers.Min()}" : $"{numbers.Min()}-{numbers.Max()}";
                sb.AppendLine($"  [{i}] fork@{branch.ForkChangeNumber}  changes {range}  archived {branch.ArchivedAt:HH:mm:ss}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Archived branches abandoned by editing after an undo, oldest first.</summary>
    public IReadOnlyList<UndoBranch> ListBranches()
    {
        var list = new List<UndoBranch>(_archivedBranches.Count);
        for (int i = 0; i < _archivedBranches.Count; i++)
        {
            var branch = _archivedBranches[i];
            list.Add(new UndoBranch(i, branch.ForkChangeNumber, branch.ArchivedAt, branch.States.Count));
        }
        return list;
    }

    /// <summary>Returns the current ancestry, redo future, and archived sibling futures as a topology.</summary>
    public UndoTree GetTree()
    {
        var nodes = new List<UndoTreeNode>();
        var ancestry = _undoStack.Reverse().ToList();
        var parent = 0;
        foreach (var state in ancestry)
        {
            nodes.Add(new(state.ChangeNumber, parent, state.Timestamp, "undo", null));
            parent = state.ChangeNumber;
        }
        parent = ancestry.LastOrDefault()?.ChangeNumber ?? 0;
        foreach (var state in _redoStack)
        {
            nodes.Add(new(state.ChangeNumber, parent, state.Timestamp, "redo", null));
            parent = state.ChangeNumber;
        }
        for (var i = 0; i < _archivedBranches.Count; i++)
        {
            parent = _archivedBranches[i].ForkChangeNumber;
            foreach (var state in _archivedBranches[i].States)
            {
                nodes.Add(new(state.ChangeNumber, parent, state.Timestamp, "archived", i));
                parent = state.ChangeNumber;
            }
        }
        return new(nodes, _currentChangeNumber);
    }

    /// <summary>
    /// Restores an archived branch's states into the redo stack, so <see cref="Redo"/> can walk
    /// forward into it again. Only valid when currently sitting at exactly the branch's fork point
    /// (undo stack top — or "fully undone" for fork 0 — matches the fork change number). Anything
    /// already pending in the redo stack at that moment is itself archived first (it is a sibling
    /// of the branch being switched into, not discarded) rather than blocking the switch.
    /// </summary>
    public bool SwitchToBranch(int index, TextBuffer buffer, CursorPosition currentCursor)
    {
        if (index < 0 || index >= _archivedBranches.Count) return false;

        var branch = _archivedBranches[index];
        var forkPoint = _currentChangeNumber;
        if (forkPoint != branch.ForkChangeNumber || !CurrentBufferMatchesChange(buffer, forkPoint)) return false;

        // Remove the target branch before archiving any pending redo stack, so the archive-cap
        // eviction below (which only ever drops the oldest entry) can never evict the very
        // branch we just chose to switch into.
        _archivedBranches.RemoveAt(index);

        if (_redoStack.Count > 0)
            ArchiveRedoStack();

        foreach (var state in Enumerable.Reverse(branch.States))
            _redoStack.Push(state);
        return true;
    }

    private bool CurrentBufferMatchesChange(TextBuffer buffer, int changeNumber)
    {
        if (changeNumber == 0) return _undoStack.Count == 0;
        var state = _currentState?.ChangeNumber == changeNumber
            ? _currentState
            : _undoStack.Concat(_redoStack).FirstOrDefault(s => s.ChangeNumber == changeNumber);
        return state != null && state.Lines.SequenceEqual(buffer.Snapshot());
    }

    /// <summary>
    /// Jumps directly to the state tagged with <paramref name="changeNumber"/>, searching the
    /// current undo stack, the current redo stack, and (if reachable) archived branches, in that
    /// order. Returns null if the change number cannot be found or cannot be cleanly reached.
    /// </summary>
    public UndoTraversalResult? JumpToChangeNumber(int changeNumber, TextBuffer buffer, CursorPosition currentCursor)
    {
        if (changeNumber == 0)
        {
            var cursor = currentCursor;
            UndoState? last = null;
            var count = 0;
            while (CanUndo)
            {
                last = Undo(buffer, cursor);
                if (last == null) break;
                cursor = buffer.ClampCursor(last.Cursor);
                count++;
            }
            return new UndoTraversalResult(count, last);
        }

        if (_undoStack.Any(s => s.ChangeNumber == changeNumber))
            return UndoUntil(buffer, currentCursor, changeNumber);

        if (_redoStack.Any(s => s.ChangeNumber == changeNumber))
            return RedoUntil(buffer, currentCursor, changeNumber);

        for (int i = 0; i < _archivedBranches.Count; i++)
        {
            var branch = _archivedBranches[i];
            if (!branch.States.Any(s => s.ChangeNumber == changeNumber)) continue;

            // Only support the simple case: we can reach this branch's exact fork point by
            // walking the stacks we already have (no rewriting of unrelated branches).
            var cursor = currentCursor;
            if (!TryReachForkPoint(branch.ForkChangeNumber, buffer, ref cursor)) return null;
            if (!SwitchToBranch(i, buffer, cursor)) return null;

            var result = RedoUntil(buffer, cursor, changeNumber);
            return result.State != null && result.State.ChangeNumber == changeNumber ? result : null;
        }

        return null;
    }

    private bool TryReachForkPoint(int forkChangeNumber, TextBuffer buffer, ref CursorPosition cursor)
    {
        bool AtFork() => _currentChangeNumber == forkChangeNumber && CurrentBufferMatchesChange(buffer, forkChangeNumber);

        if (AtFork()) return true;

        if (forkChangeNumber == 0 || _undoStack.Any(s => s.ChangeNumber == forkChangeNumber))
        {
            while (CanUndo && !AtFork())
            {
                var state = Undo(buffer, cursor);
                if (state == null) break;
                cursor = buffer.ClampCursor(state.Cursor);
            }
        }
        else if (_redoStack.Any(s => s.ChangeNumber == forkChangeNumber))
        {
            while (CanRedo && !AtFork())
            {
                var state = Redo(buffer, cursor);
                if (state == null) break;
                cursor = buffer.ClampCursor(state.Cursor);
            }
        }

        return AtFork();
    }

    private UndoTraversalResult UndoUntil(TextBuffer buffer, CursorPosition currentCursor, int changeNumber)
    {
        var cursor = currentCursor;
        UndoState? last = null;
        var count = 0;
        while (CanUndo && (last == null || last.ChangeNumber != changeNumber))
        {
            last = Undo(buffer, cursor);
            if (last == null) break;
            cursor = buffer.ClampCursor(last.Cursor);
            count++;
        }
        return new UndoTraversalResult(count, last);
    }

    private UndoTraversalResult RedoUntil(TextBuffer buffer, CursorPosition currentCursor, int changeNumber)
    {
        var cursor = currentCursor;
        UndoState? last = null;
        var count = 0;
        while (CanRedo && (last == null || last.ChangeNumber != changeNumber))
        {
            last = Redo(buffer, cursor);
            if (last == null) break;
            cursor = buffer.ClampCursor(last.Cursor);
            count++;
        }
        return new UndoTraversalResult(count, last);
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _archivedBranches.Clear();
        _nextChangeNumber = 0;
        _currentChangeNumber = 0;
        _currentState = null;
    }
}

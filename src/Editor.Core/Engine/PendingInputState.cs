namespace Editor.Core.Engine;

/// <summary>
/// Represents the single pending, modal input operation owned by the engine.
/// State records carry the data needed to finish the operation so parallel
/// boolean flags cannot describe an impossible combination.
/// </summary>
public abstract record PendingInputState
{
    public sealed record None : PendingInputState;
    public sealed record InsertRegister : PendingInputState;
    public sealed record ExpressionRegister(string Expression) : PendingInputState;
    public sealed record Digraph(char? FirstCharacter) : PendingInputState;
    public sealed record InsertCompletion : PendingInputState;
}

/// <summary>Owns pending-input transitions and cancellation.</summary>
public sealed class PendingInputController
{
    public PendingInputState Current { get; private set; } = new PendingInputState.None();

    public bool HasPendingInput => Current is not PendingInputState.None;

    public void Begin(PendingInputState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state is PendingInputState.None)
            throw new ArgumentException("Use Cancel to clear pending input.", nameof(state));
        Current = state;
    }

    public void Cancel() => Current = new PendingInputState.None();
}

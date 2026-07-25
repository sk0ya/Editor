using Editor.Core.Models;

namespace Editor.Core.Engine;

/// <summary>Data-driven dispatcher for Visual-mode motions.</summary>
internal sealed class VisualMotionDispatcher
{
    internal delegate bool Handler(ParsedCommand command, List<VimEvent> events);
    internal delegate bool Pattern(ParsedCommand command);

    private readonly Dictionary<string, Handler> _exact = new(StringComparer.Ordinal);
    private readonly List<(Pattern Matches, Handler Handler)> _patterns = [];

    public VisualMotionDispatcher Register(string motion, Handler handler)
        => Register([motion], handler);

    public VisualMotionDispatcher Register(IEnumerable<string> motions, Handler handler)
    {
        foreach (var motion in motions)
            if (!_exact.TryAdd(motion, handler))
                throw new InvalidOperationException($"Visual motion '{motion}' is already registered.");
        return this;
    }

    public VisualMotionDispatcher RegisterPattern(Pattern matches, Handler handler)
    {
        _patterns.Add((matches, handler));
        return this;
    }

    public bool Dispatch(ParsedCommand command, List<VimEvent> events)
    {
        if (command.Motion is { } motion && _exact.TryGetValue(motion, out var handler))
            return handler(command, events);
        foreach (var candidate in _patterns)
            if (candidate.Matches(command))
                return candidate.Handler(command, events);
        return false;
    }
}

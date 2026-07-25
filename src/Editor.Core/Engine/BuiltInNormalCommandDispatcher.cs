using Editor.Core.Models;

namespace Editor.Core.Engine;

/// <summary>
/// Dispatch table for built-in parsed Normal commands. Exact commands use a
/// dictionary; grammar-shaped commands such as r{char} use ordered predicates.
/// </summary>
internal sealed class BuiltInNormalCommandDispatcher
{
    internal delegate void Handler(ParsedCommand command, List<VimEvent> events);
    internal delegate bool Pattern(ParsedCommand command);

    private readonly Dictionary<string, Handler> _exact = new(StringComparer.Ordinal);
    private readonly List<(Pattern Matches, Handler Handler)> _patterns = [];
    private Handler? _fallback;

    public BuiltInNormalCommandDispatcher Register(string motion, Handler handler)
        => Register([motion], handler);

    public BuiltInNormalCommandDispatcher Register(IEnumerable<string> motions, Handler handler)
    {
        foreach (var motion in motions)
            if (!_exact.TryAdd(motion, handler))
                throw new InvalidOperationException($"Built-in Normal command '{motion}' is already registered.");
        return this;
    }

    public BuiltInNormalCommandDispatcher RegisterPattern(Pattern matches, Handler handler)
    {
        _patterns.Add((matches, handler));
        return this;
    }

    public BuiltInNormalCommandDispatcher SetFallback(Handler handler)
    {
        _fallback = handler;
        return this;
    }

    public void Dispatch(ParsedCommand command, List<VimEvent> events)
    {
        if (command.Motion is { } motion && _exact.TryGetValue(motion, out var handler))
        {
            handler(command, events);
            return;
        }

        foreach (var candidate in _patterns)
        {
            if (!candidate.Matches(command)) continue;
            candidate.Handler(command, events);
            return;
        }

        _fallback?.Invoke(command, events);
    }
}

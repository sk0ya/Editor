using Editor.Core.Models;
using Editor.Core.Extensibility;

namespace Editor.Core.Engine;

/// <summary>
/// Dispatch table for built-in parsed Normal commands. Exact commands use a
/// dictionary; grammar-shaped commands such as r{char} use ordered predicates.
/// </summary>
internal sealed class BuiltInNormalCommandDispatcher
{
    internal delegate void Handler(INormalCommandContext context, List<VimEvent> events);
    internal delegate void LegacyHandler(ParsedCommand command, List<VimEvent> events);
    internal delegate bool Pattern(ParsedCommand command);

    private readonly Dictionary<string, Handler> _exact = new(StringComparer.Ordinal);
    private readonly List<(Pattern Matches, Handler Handler)> _patterns = [];
    private Handler? _fallback;

    public BuiltInNormalCommandDispatcher Register(string motion, LegacyHandler handler)
        => Register([motion], handler);

    public BuiltInNormalCommandDispatcher Register(IEnumerable<string> motions, LegacyHandler handler)
        => RegisterContext(motions, (context, events) => handler(context.Command, events));

    public BuiltInNormalCommandDispatcher RegisterContext(IEnumerable<string> motions, Handler handler)
    {
        foreach (var motion in motions)
            if (!_exact.TryAdd(motion, handler))
                throw new InvalidOperationException($"Built-in Normal command '{motion}' is already registered.");
        return this;
    }

    public BuiltInNormalCommandDispatcher RegisterPatternContext(Pattern matches, Handler handler)
    {
        _patterns.Add((matches, handler));
        return this;
    }

    public BuiltInNormalCommandDispatcher RegisterPattern(Pattern matches, LegacyHandler handler)
        => RegisterPatternContext(matches, (context, events) => handler(context.Command, events));

    public BuiltInNormalCommandDispatcher SetFallbackContext(Handler handler)
    {
        _fallback = handler;
        return this;
    }

    public BuiltInNormalCommandDispatcher SetFallback(LegacyHandler handler)
        => SetFallbackContext((context, events) => handler(context.Command, events));

    public void Dispatch(INormalCommandContext context, List<VimEvent> events)
    {
        var command = context.Command;
        if (command.Motion is { } motion && _exact.TryGetValue(motion, out var handler))
        {
            handler(context, events);
            return;
        }

        foreach (var candidate in _patterns)
        {
            if (!candidate.Matches(command)) continue;
            candidate.Handler(context, events);
            return;
        }

        _fallback?.Invoke(context, events);
    }
}

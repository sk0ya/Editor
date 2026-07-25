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

    private readonly CommandTable<
        string,
        (INormalCommandContext Context, List<VimEvent> Events),
        bool> _table = new(StringComparer.Ordinal);
    private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
    private int _patternPriority;

    public BuiltInNormalCommandDispatcher Register(string motion, LegacyHandler handler)
        => Register([motion], handler);

    public BuiltInNormalCommandDispatcher Register(IEnumerable<string> motions, LegacyHandler handler)
        => RegisterContext(motions, (context, events) => handler(context.Command, events));

    public BuiltInNormalCommandDispatcher RegisterContext(IEnumerable<string> motions, Handler handler)
    {
        foreach (var motion in motions)
        {
            if (!_exact.Add(motion))
                throw new InvalidOperationException($"Built-in Normal command '{motion}' is already registered.");
            _table.RegisterExact(
                $"normal.builtin.{MotionId(motion)}",
                motion,
                execution =>
                {
                    handler(execution.Context, execution.Events);
                    return true;
                },
                CommandLayer.BuiltIn);
        }
        return this;
    }

    public BuiltInNormalCommandDispatcher RegisterPatternContext(Pattern matches, Handler handler)
    {
        _table.RegisterPattern(
            $"normal.builtin.pattern.{_patternPriority}",
            (_, execution) => matches(execution.Context.Command),
            execution =>
            {
                handler(execution.Context, execution.Events);
                return true;
            },
            CommandLayer.BuiltIn,
            priority: -_patternPriority++);
        return this;
    }

    public BuiltInNormalCommandDispatcher RegisterPattern(Pattern matches, LegacyHandler handler)
        => RegisterPatternContext(matches, (context, events) => handler(context.Command, events));

    public BuiltInNormalCommandDispatcher SetFallbackContext(Handler handler)
    {
        _table.RegisterPattern(
            "normal.builtin.fallback",
            (_, _) => true,
            execution =>
            {
                handler(execution.Context, execution.Events);
                return true;
            },
            CommandLayer.BuiltIn,
            priority: int.MinValue);
        return this;
    }

    public BuiltInNormalCommandDispatcher SetFallback(LegacyHandler handler)
        => SetFallbackContext((context, events) => handler(context.Command, events));

    public void Dispatch(INormalCommandContext context, List<VimEvent> events)
    {
        var execution = (context, events);
        if (context.Command.Motion is { } motion &&
            _table.TryResolve(motion, execution, out var handler))
            handler(execution);
    }

    private static string MotionId(string motion) =>
        string.Join("_", motion.Select(character => ((int)character).ToString("x4")));
}

using Editor.Core.Models;
using Editor.Core.Extensibility;

namespace Editor.Core.Engine;

/// <summary>Data-driven dispatcher for Visual-mode motions.</summary>
internal sealed class VisualMotionDispatcher
{
    internal delegate bool Handler(ParsedCommand command, List<VimEvent> events);
    internal delegate bool Pattern(ParsedCommand command);

    private readonly CommandTable<
        string,
        (ParsedCommand Command, List<VimEvent> Events),
        bool> _table = new(StringComparer.Ordinal);
    private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
    private int _patternPriority;

    public VisualMotionDispatcher Register(string motion, Handler handler)
        => Register([motion], handler);

    public VisualMotionDispatcher Register(IEnumerable<string> motions, Handler handler)
    {
        foreach (var motion in motions)
        {
            if (!_exact.Add(motion))
                throw new InvalidOperationException($"Visual motion '{motion}' is already registered.");
            _table.RegisterExact(
                $"visual.{MotionId(motion)}",
                motion,
                context => handler(context.Command, context.Events),
                CommandLayer.BuiltIn);
        }
        return this;
    }

    public VisualMotionDispatcher RegisterPattern(Pattern matches, Handler handler)
    {
        _table.RegisterPattern(
            $"visual.pattern.{_patternPriority}",
            (_, context) => matches(context.Command),
            context => handler(context.Command, context.Events),
            CommandLayer.BuiltIn,
            priority: -_patternPriority++);
        return this;
    }

    public bool Dispatch(ParsedCommand command, List<VimEvent> events)
    {
        var context = (command, events);
        return command.Motion is { } motion &&
               _table.TryResolve(motion, context, out var handler) &&
               handler(context);
    }

    private static string MotionId(string motion) =>
        string.Join("_", motion.Select(character => ((int)character).ToString("x4")));
}

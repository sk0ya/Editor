using Editor.Core.Buffer;
using Editor.Core.Extensibility;
using Editor.Core.Models;

namespace Editor.Core.Engine;

public enum MotionApplication
{
    Normal,
    Visual,
    Operator
}

public enum MotionShape
{
    Characterwise,
    Linewise,
    Blockwise
}

public sealed record MotionRequest(
    string Motion,
    CursorPosition Start,
    int Count = 1,
    MotionApplication Application = MotionApplication.Normal,
    string? Operator = null,
    char? FindCharacter = null,
    int ViewportTopLine = 0,
    int ViewportVisibleLines = 25,
    bool AllowEndOfLine = false);

public sealed record MotionResult(
    CursorPosition Start,
    CursorPosition Target,
    MotionType Type,
    MotionShape Shape,
    bool AddToJumpList,
    bool UpdateStickyColumn);

public interface IMotionService
{
    MotionResult? Calculate(MotionRequest request);
}

/// <summary>
/// Extension point for fold-aware or display-line-aware motion calculation.
/// Returning true prevents the logical-line backend from recalculating the motion.
/// </summary>
public interface IMotionOverride
{
    bool TryCalculate(
        MotionRequest request,
        INormalBufferView buffer,
        out MotionResult? result);
}

/// <summary>
/// Shared motion calculation entry point for Normal, Visual, and operator applications.
/// Mode-specific code applies the returned result but does not recalculate the target.
/// </summary>
public sealed class MotionService(
    BufferManager buffers,
    IEnumerable<IMotionOverride>? overrides = null) : IMotionService
{
    private readonly IMotionOverride[] _overrides = overrides?.ToArray() ?? [];

    public MotionResult? Calculate(MotionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var buffer = buffers.Current.Text;
        foreach (var motionOverride in _overrides)
            if (motionOverride.TryCalculate(request, buffer, out var overridden))
                return overridden;

        var engine = new MotionEngine(buffer, buffers.Current.FilePath);
        engine.SetViewport(request.ViewportTopLine, request.ViewportVisibleLines);

        var motionName = request.Motion;
        if (request.Application == MotionApplication.Operator &&
            request.Operator == "c" &&
            motionName is "w" or "W")
        {
            var line = buffer.GetLine(request.Start.Line);
            if (request.Start.Column < line.Length && !char.IsWhiteSpace(line[request.Start.Column]))
                motionName = motionName == "w" ? "e" : "E";
        }

        Motion? motion;
        if (request.FindCharacter.HasValue && motionName is "f" or "F" or "t" or "T")
        {
            var forward = motionName is "f" or "t";
            var before = motionName is "t" or "T";
            var target = engine.FindChar(
                request.Start,
                request.FindCharacter.Value,
                forward,
                before,
                Math.Max(1, request.Count));
            motion = new Motion(target, MotionType.Inclusive);
        }
        else if (request.Application == MotionApplication.Operator && motionName is "w" or "W")
        {
            motion = engine.WordForwardOperatorEnd(
                request.Start,
                Math.Max(1, request.Count),
                motionName == "W");
        }
        else if (request.AllowEndOfLine && motionName is "l" or "Right")
        {
            motion = new Motion(
                engine.MoveRight(request.Start, Math.Max(1, request.Count), true),
                MotionType.Exclusive);
        }
        else
        {
            motion = engine.Calculate(motionName, request.Start, Math.Max(1, request.Count));
        }

        if (!motion.HasValue)
            return null;

        var value = motion.Value;
        return new MotionResult(
            request.Start,
            value.Target,
            value.Type,
            value.Type == MotionType.Linewise || value.LinewiseForced
                ? MotionShape.Linewise
                : MotionShape.Characterwise,
            AddToJumpList(motionName),
            UpdateStickyColumn(motionName));
    }

    private static bool AddToJumpList(string motion) =>
        motion is "gg" or "G" or "{" or "}" or "%" or "H" or "M" or "L"
            or "[m" or "]m" or "[M" or "]M";

    private static bool UpdateStickyColumn(string motion) =>
        motion is not ("j" or "k" or "gj" or "gk");
}

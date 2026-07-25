using Editor.Core.Extensibility;
using Editor.Core.Macros;
using Editor.Core.Models;

namespace Editor.Core.Engine;

/// <summary>
/// Resolves raw input through programmable bindings and user mappings before it
/// reaches Vim's mode handlers. This class owns all ambiguous-prefix state, so
/// <see cref="VimEngine"/> does not need to coordinate two independent buffers.
/// </summary>
internal sealed class KeyInputPipeline(
    VimEngine engine,
    VimKeyBindingRegistry bindings,
    Func<VimMode> getMode,
    Func<VimMode, Dictionary<string, string>?> getMaps,
    Func<bool> isCommandParserPending,
    Action<VimKeyStroke, List<VimEvent>> dispatchResolved)
{
    private readonly List<VimKeyStroke> _bindingInput = [];
    private readonly List<VimKeyStroke> _mappingInput = [];

    public bool HasPendingInput => _bindingInput.Count > 0 || _mappingInput.Count > 0;

    public void Clear()
    {
        _bindingInput.Clear();
        _mappingInput.Clear();
    }

    /// <summary>
    /// Processes one raw stroke. When <paramref name="resolveMappings"/> is false,
    /// the stroke is delivered literally (used by macros and non-recursive maps).
    /// </summary>
    public void Process(VimKeyStroke stroke, List<VimEvent> events, bool resolveMappings)
    {
        if (!resolveMappings)
        {
            dispatchResolved(stroke, events);
            return;
        }

        if (!TryApplyBinding(stroke, events))
            ProcessMapping(stroke, events);
    }

    /// <summary>Resolves pending ambiguous prefixes as literal input.</summary>
    public void Flush(List<VimEvent> events)
    {
        while (_bindingInput.Count > 0)
        {
            var literal = TakeFirst(_bindingInput);
            ProcessMapping(literal, events);
        }

        FlushMappingInput(events);
    }

    private bool TryApplyBinding(VimKeyStroke stroke, List<VimEvent> events)
    {
        if (bindings.IsEmpty && _bindingInput.Count == 0)
            return false;

        _bindingInput.Add(stroke);
        while (_bindingInput.Count > 0)
        {
            var match = bindings.Resolve(getMode(), _bindingInput);
            if (match.Exact is not null)
            {
                if (match.HasLongerPrefix) return true;

                var input = _bindingInput.ToArray();
                _bindingInput.Clear();
                var result = match.Exact.Handler(new VimKeyBindingContext(engine, getMode(), input));
                if (result is not null) events.AddRange(result);
                return true;
            }

            if (match.HasPrefix) return true;
            ProcessMapping(TakeFirst(_bindingInput), events);
        }

        return true;
    }

    private void ProcessMapping(VimKeyStroke stroke, List<VimEvent> events)
    {
        var maps = getMaps(getMode());
        if ((maps is null || maps.Count == 0) && _mappingInput.Count == 0)
        {
            dispatchResolved(stroke, events);
            return;
        }

        _mappingInput.Add(stroke);
        while (_mappingInput.Count > 0)
        {
            maps = getMaps(getMode());
            if (maps is null || maps.Count == 0)
            {
                FlushMappingInput(events);
                return;
            }

            var match = KeyMappingResolver.ResolveMapMatch(maps, _mappingInput);
            if (match.HasExactMatch)
            {
                if (match.HasLongerPrefix) return;

                _mappingInput.Clear();
                foreach (var mapped in KeyMappingResolver.ParseMappingSequence(match.MappedValue ?? ""))
                    dispatchResolved(mapped, events);
                return;
            }

            if (match.HasPrefix) return;
            dispatchResolved(TakeFirst(_mappingInput), events);

            // A parser prefix such as "g" has already claimed the input. Holding
            // another literal merely because a user map also starts with "g"
            // would incorrectly require a third key to complete "gg".
            if (isCommandParserPending())
                FlushMappingInput(events);
        }
    }

    private void FlushMappingInput(List<VimEvent> events)
    {
        while (_mappingInput.Count > 0)
            dispatchResolved(TakeFirst(_mappingInput), events);
    }

    private static VimKeyStroke TakeFirst(List<VimKeyStroke> input)
    {
        var first = input[0];
        input.RemoveAt(0);
        return first;
    }
}

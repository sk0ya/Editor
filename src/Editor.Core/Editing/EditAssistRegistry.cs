namespace Editor.Core.Editing;

/// <summary>
/// Resolves the <see cref="IEditAssist"/> that applies to a given file. Register
/// additional assists (most-recently-registered wins) to extend smart-editing
/// behaviour without modifying <c>VimEngine</c>.
/// </summary>
public sealed class EditAssistRegistry
{
    private readonly List<IEditAssist> _assists = new()
    {
        new MarkdownEditAssist(),
        new CStyleCommentEditAssist(),
        new HtmlTagEditAssist(),
    };

    /// <summary>Process-wide default registry used by <c>VimEngine</c>.</summary>
    public static EditAssistRegistry Default { get; } = new();

    /// <summary>Adds an assist, giving it priority over previously registered ones.</summary>
    public void Register(IEditAssist assist) => _assists.Insert(0, assist);

    /// <summary>Returns the first assist that applies to <paramref name="filePath"/>, or null.</summary>
    public IEditAssist? Resolve(string? filePath)
    {
        foreach (var assist in _assists)
            if (assist.AppliesTo(filePath))
                return assist;
        return null;
    }

    // The hook dispatchers below try every applicable assist in priority order and fall
    // through to the next on decline, rather than committing to whichever assist Resolve()
    // happens to return first — two assists can both AppliesTo() the same extension while
    // each only implementing a disjoint subset of hooks (e.g. comment continuation vs.
    // tag auto-close both apply to .tsx), so a single "winning" assist would silently
    // starve the other of its hook.

    /// <summary>Tries <see cref="IEditAssist.OnEnter"/> on each applicable assist in order.</summary>
    public EditResult OnEnter(EditContext ctx)
    {
        foreach (var assist in _assists)
        {
            if (!assist.AppliesTo(ctx.FilePath)) continue;
            var result = assist.OnEnter(ctx);
            if (result.Handled) return result;
        }
        return EditResult.NotHandled;
    }

    /// <summary>Tries <see cref="IEditAssist.OnTab"/> on each applicable assist in order.</summary>
    public EditResult OnTab(EditContext ctx, bool shift)
    {
        foreach (var assist in _assists)
        {
            if (!assist.AppliesTo(ctx.FilePath)) continue;
            var result = assist.OnTab(ctx, shift);
            if (result.Handled) return result;
        }
        return EditResult.NotHandled;
    }

    /// <summary>Tries <see cref="IEditAssist.OnChar"/> on each applicable assist in order.</summary>
    public EditResult OnChar(EditContext ctx, char typed)
    {
        foreach (var assist in _assists)
        {
            if (!assist.AppliesTo(ctx.FilePath)) continue;
            var result = assist.OnChar(ctx, typed);
            if (result.Handled) return result;
        }
        return EditResult.NotHandled;
    }

    /// <summary>Tries <see cref="IEditAssist.OpenLinePrefix"/> on each applicable assist in order.</summary>
    public string? OpenLinePrefix(EditContext ctx, bool above)
    {
        foreach (var assist in _assists)
        {
            if (!assist.AppliesTo(ctx.FilePath)) continue;
            var prefix = assist.OpenLinePrefix(ctx, above);
            if (prefix != null) return prefix;
        }
        return null;
    }
}

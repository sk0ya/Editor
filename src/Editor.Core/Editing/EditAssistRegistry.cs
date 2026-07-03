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
}

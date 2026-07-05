namespace Editor.Core.Matchit.Languages;

/// <summary>Ruby keyword-chain matching: if/elsif/else/end, def/end, class/end, module/end,
/// case/when/else/end, do/end.</summary>
public class RubyMatchitLanguage : IMatchitLanguage
{
    public string[] Extensions => [".rb"];

    public IReadOnlyList<string[][]> KeywordChains =>
    [
        [ ["if"], ["elsif"], ["else"], ["end"] ],
        [ ["def"], ["end"] ],
        [ ["class"], ["end"] ],
        [ ["module"], ["end"] ],
        [ ["case"], ["when"], ["else"], ["end"] ],
        [ ["do"], ["end"] ],
    ];
}

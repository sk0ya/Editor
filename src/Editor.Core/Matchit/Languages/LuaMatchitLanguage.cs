namespace Editor.Core.Matchit.Languages;

/// <summary>Lua keyword-chain matching: if/then/elseif/else/end, for/do/end, while/do/end,
/// function/end, repeat/until.</summary>
public class LuaMatchitLanguage : IMatchitLanguage
{
    public string[] Extensions => [".lua"];

    public IReadOnlyList<string[][]> KeywordChains =>
    [
        [ ["if"], ["then"], ["elseif"], ["else"], ["end"] ],
        [ ["for"], ["do"], ["end"] ],
        [ ["while"], ["do"], ["end"] ],
        [ ["function"], ["end"] ],
        [ ["repeat"], ["until"] ],
    ];
}

namespace Editor.Core.Config;

public class VimOptions
{
    // Display
    public bool Number { get; set; } = true;
    public bool RelativeNumber { get; set; } = false;
    public bool CursorLine { get; set; } = true;
    public bool Wrap { get; set; } = false;
    public int ScrollOff { get; set; } = 5;
    public bool ShowMode { get; set; } = true;
    public bool ShowCmd { get; set; } = true;

    // Editing
    public int TabStop { get; set; } = 4;
    public int ShiftWidth { get; set; } = 4;
    public bool ExpandTab { get; set; } = true;
    public bool AutoIndent { get; set; } = true;
    public bool SmartIndent { get; set; } = true;

    // Search
    public bool IgnoreCase { get; set; } = true;
    public bool SmartCase { get; set; } = true;
    public bool HlSearch { get; set; } = true;
    public bool IncrSearch { get; set; } = true;

    // Appearance
    public string ColorScheme { get; set; } = "dracula";
    public string FontFamily { get; set; } = "Cascadia Code";
    public double FontSize { get; set; } = 14;
    public bool Syntax { get; set; } = true;

    // Behaviour
    public bool Hidden { get; set; } = true; // Allow hidden buffers
    public int History { get; set; } = 1000;
    public bool Ruler { get; set; } = true;
    public string BackSpace { get; set; } = "indent,eol,start";
    public string Clipboard { get; set; } = ""; // "unnamed" or "unnamedplus"

    // Apply a setting string like "number", "nonu", "tabstop=4"
    public string? Apply(string setting)
    {
        setting = setting.Trim();
        bool negate = setting.StartsWith("no", StringComparison.OrdinalIgnoreCase) && setting.Length > 2;
        string name = negate ? setting[2..] : setting;

        // Handle key=value
        var eqIdx = name.IndexOf('=');
        if (eqIdx >= 0)
        {
            var key = name[..eqIdx].ToLower();
            var val = name[(eqIdx + 1)..];
            return ApplyKeyValue(key, val);
        }

        // Boolean toggle/set/unset
        return ApplyBool(name.ToLower(), !negate);
    }

    private string? ApplyBool(string name, bool value)
    {
        return name switch
        {
            "number" or "nu" => Set(ref _dummy, value, () => Number = value),
            "relativenumber" or "rnu" => Set(ref _dummy, value, () => RelativeNumber = value),
            "cursorline" or "cul" => Set(ref _dummy, value, () => CursorLine = value),
            "wrap" => Set(ref _dummy, value, () => Wrap = value),
            "expandtab" or "et" => Set(ref _dummy, value, () => ExpandTab = value),
            "autoindent" or "ai" => Set(ref _dummy, value, () => AutoIndent = value),
            "smartindent" or "si" => Set(ref _dummy, value, () => SmartIndent = value),
            "ignorecase" or "ic" => Set(ref _dummy, value, () => IgnoreCase = value),
            "smartcase" or "scs" => Set(ref _dummy, value, () => SmartCase = value),
            "hlsearch" or "hls" => Set(ref _dummy, value, () => HlSearch = value),
            "incsearch" or "is" => Set(ref _dummy, value, () => IncrSearch = value),
            "syntax" => Set(ref _dummy, value, () => Syntax = value),
            "hidden" => Set(ref _dummy, value, () => Hidden = value),
            "ruler" => Set(ref _dummy, value, () => Ruler = value),
            "showmode" => Set(ref _dummy, value, () => ShowMode = value),
            "showcmd" => Set(ref _dummy, value, () => ShowCmd = value),
            _ => $"Unknown option: {name}"
        };
    }

    private string? ApplyKeyValue(string key, string value)
    {
        return key switch
        {
            "tabstop" or "ts" when int.TryParse(value, out var n) => Set(() => TabStop = n),
            "shiftwidth" or "sw" when int.TryParse(value, out var n) => Set(() => ShiftWidth = n),
            "scrolloff" or "so" when int.TryParse(value, out var n) => Set(() => ScrollOff = n),
            "history" when int.TryParse(value, out var n) => Set(() => History = n),
            "colorscheme" or "cs" => Set(() => ColorScheme = value),
            "fontfamily" => Set(() => FontFamily = value),
            "fontsize" when double.TryParse(value, out var d) => Set(() => FontSize = d),
            "clipboard" or "cb" => Set(() => Clipboard = value),
            _ => $"Invalid value for: {key}"
        };
    }

    private bool _dummy;
    private static string? Set(ref bool _, bool __, Action action) { action(); return null; }
    private static string? Set(Action action) { action(); return null; }
}

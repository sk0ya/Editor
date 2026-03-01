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
    public bool WrapScan { get; set; } = true;

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

        // `set option&` resets to default — treat as no-op (already at default on fresh instance)
        if (setting.EndsWith('&'))
            return null;

        bool negate = setting.StartsWith("no", StringComparison.OrdinalIgnoreCase) && setting.Length > 2;
        string name = negate ? setting[2..] : setting;

        // Handle key=value (also key+=val, key^=val, key-=val — treat all as assignment)
        var eqIdx = name.IndexOfAny(['+', '^', '-', '=']);
        if (eqIdx >= 0 && name[eqIdx] == '=' || (eqIdx >= 0 && eqIdx + 1 < name.Length && name[eqIdx + 1] == '='))
        {
            int valStart = name.IndexOf('=') + 1;
            var key = name[..name.IndexOf('=')].TrimEnd('+', '^', '-').ToLower();
            var val = name[valStart..];
            return ApplyKeyValue(key, val);
        }

        // Boolean toggle/set/unset
        return ApplyBool(name.ToLower(), !negate);
    }

    private string? ApplyBool(string name, bool value)
    {
        return name switch
        {
            "number" or "nu"                   => Set(() => Number = value),
            "relativenumber" or "rnu"          => Set(() => RelativeNumber = value),
            "cursorline" or "cul"              => Set(() => CursorLine = value),
            "wrap"                             => Set(() => Wrap = value),
            "expandtab" or "et"                => Set(() => ExpandTab = value),
            "autoindent" or "ai"               => Set(() => AutoIndent = value),
            "smartindent" or "si"              => Set(() => SmartIndent = value),
            "ignorecase" or "ic"               => Set(() => IgnoreCase = value),
            "smartcase" or "scs"               => Set(() => SmartCase = value),
            "hlsearch" or "hls"                => Set(() => HlSearch = value),
            "incsearch" or "is"                => Set(() => IncrSearch = value),
            "wrapscan" or "ws"                 => Set(() => WrapScan = value),
            "syntax"                           => Set(() => Syntax = value),
            "hidden"                           => Set(() => Hidden = value),
            "ruler"                            => Set(() => Ruler = value),
            "showmode"                         => Set(() => ShowMode = value),
            "showcmd"                          => Set(() => ShowCmd = value),
            // No-op options — parse silently, no effect in this editor
            "errorbells" or "eb"               => null,
            "visualbell" or "vb"               => null,
            "infercase" or "inf"               => null,
            "splitbelow" or "sb"               => null,
            "splitright" or "spr"              => null,
            "autoread" or "ar"                 => null,
            "autochdir" or "acd"               => null,
            "backup" or "bk"                   => null,
            "swapfile" or "swf"                => null,
            "wildmenu" or "wmnu"               => null,
            "showmatch" or "sm"                => null,
            "breakindent" or "bri"             => null,
            "undofile" or "udf"                => null,
            "lazyredraw" or "lz"               => null,
            "list"                             => null,
            "paste"                            => null,
            "compatible" or "cp"               => null,
            "modeline" or "ml"                 => null,
            "startofline" or "sol"             => null,
            "ttyfast" or "tf"                  => null,
            _                                  => null  // silently ignore unknown bool options
        };
    }

    private string? ApplyKeyValue(string key, string value)
    {
        return key switch
        {
            "tabstop" or "ts"               when int.TryParse(value, out var n) => Set(() => TabStop = n),
            "shiftwidth" or "sw"            when int.TryParse(value, out var n) => Set(() => ShiftWidth = n),
            "scrolloff" or "so"             when int.TryParse(value, out var n) => Set(() => ScrollOff = n),
            "history"                       when int.TryParse(value, out var n) => Set(() => History = n),
            "colorscheme" or "cs"                                               => Set(() => ColorScheme = value),
            "fontfamily"                                                         => Set(() => FontFamily = value),
            "fontsize"                      when double.TryParse(value, out var d) => Set(() => FontSize = d),
            "clipboard" or "cb"                                                 => Set(() => Clipboard = value),
            // No-op key=value options
            "mouse"                         => null,
            "encoding" or "enc"             => null,
            "fileencoding" or "fenc"        => null,
            "iminsert"                      => null,
            "imsearch"                      => null,
            "laststatus" or "ls"            => null,
            "shortmess" or "shm"            => null,
            "wildignore" or "wig"           => null,
            "backupdir" or "bdir"           => null,
            "directory" or "dir"            => null,
            "undodir"                       => null,
            "softtabstop" or "sts"          => null,
            "textwidth" or "tw"             => null,
            "colorcolumn" or "cc"           => null,
            "foldmethod" or "fdm"           => null,
            "completeopt" or "cot"          => null,
            "updatetime" or "ut"            => null,
            "timeoutlen" or "tm"            => null,
            "ttimeoutlen"                   => null,
            "formatoptions" or "fo"         => null,
            "t_vb"                          => null,
            "t_si" or "t_ei" or "t_sr"      => null,
            _                               => null  // silently ignore unknown key=value options
        };
    }

    private static string? Set(Action action) { action(); return null; }
}

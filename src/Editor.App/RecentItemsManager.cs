using System.IO;
using System.Text.Json;

namespace Editor.App;

internal sealed class RecentItemsManager
{
    private const int MaxItems = 10;

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WVim", "recent.json");

    public sealed class SessionState
    {
        public string? FolderPath { get; set; }
        public List<string> TabFiles { get; set; } = [];
        public string? ActiveFile { get; set; }
    }

    private sealed class RecentData
    {
        public List<string> Folders { get; set; } = [];
        public List<string> Files { get; set; } = [];
        public SessionState? LastSession { get; set; }
        public string TabPlacement { get; set; } = "Top";
        public string ThemeName { get; set; } = "Dracula";
        public string? CustomBackground { get; set; }
        public string? CustomAccent { get; set; }
    }

    private RecentData _data;

    public RecentItemsManager()
    {
        _data = Load();
    }

    public IReadOnlyList<string> RecentFolders => _data.Folders;
    public IReadOnlyList<string> RecentFiles => _data.Files;
    public SessionState? LastSession => _data.LastSession;
    public string TabPlacement => _data.TabPlacement;
    public string ThemeName => _data.ThemeName;
    public string? CustomBackground => _data.CustomBackground;
    public string? CustomAccent => _data.CustomAccent;

    public void AddFolder(string path)
    {
        AddItem(_data.Folders, path);
        Save();
    }

    public void AddFile(string path)
    {
        AddItem(_data.Files, path);
        Save();
    }

    public void SaveTabPlacement(string placement)
    {
        _data.TabPlacement = placement;
        Save();
    }

    public void SaveTheme(string themeName, string? customBackground, string? customAccent)
    {
        _data.ThemeName = themeName;
        _data.CustomBackground = customBackground;
        _data.CustomAccent = customAccent;
        Save();
    }

    public void SaveSession(string? folderPath, IEnumerable<string> tabFiles, string? activeFile)
    {
        _data.LastSession = new SessionState
        {
            FolderPath = folderPath,
            TabFiles = tabFiles.ToList(),
            ActiveFile = activeFile,
        };
        Save();
    }

    private static void AddItem(List<string> list, string item)
    {
        list.RemoveAll(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, item);
        if (list.Count > MaxItems)
            list.RemoveRange(MaxItems, list.Count - MaxItems);
    }

    private static RecentData Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<RecentData>(json) ?? new RecentData();
            }
        }
        catch { }
        return new RecentData();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }
}

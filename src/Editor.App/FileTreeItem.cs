using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Editor.App;

// ─────────── File tree model ─────────────────────────────────

/// <summary>Document symbol entry shown in the Outline sidebar panel.</summary>
public sealed class OutlineItem
{
    public required string Name     { get; init; }
    public required int    Depth    { get; init; }
    public required string KindIcon { get; init; }
    public required string KindColor{ get; init; }
    public required int    Line     { get; init; }
    public required int    Col      { get; init; }
    public Thickness IndentMargin => new(Depth * 16, 0, 0, 0);
}

public sealed class FileTreeItem
{
    private bool _childrenLoaded;
    private readonly bool _isPlaceholder;

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public string Icon { get; }
    public Brush IconBrush { get; }
    public FileTreeItem? Parent { get; }
    public bool IsExpanded { get; set; }
    public ObservableCollection<FileTreeItem> Children { get; } = [];

    public FileTreeItem(string path, FileTreeItem? parent = null)
    {
        FullPath = path;
        Name = Path.GetFileName(path) is { Length: > 0 } n ? n : path;
        IsDirectory = Directory.Exists(path);
        Icon = IsDirectory ? "\uED41" : "\uE7C3";
        IconBrush = IsDirectory
            ? new SolidColorBrush(Color.FromRgb(0xE6, 0xC0, 0x7B))
            : new SolidColorBrush(Color.FromRgb(0x99, 0xBB, 0xDD));
        Parent = parent;

        if (IsDirectory)
            Children.Add(Placeholder);
    }

    private FileTreeItem()
    {
        _isPlaceholder = true;
        FullPath = string.Empty;
        Name = string.Empty;
        Icon = string.Empty;
        IconBrush = Brushes.Transparent;
        _childrenLoaded = true;
        IsExpanded = true;
    }

    private static readonly FileTreeItem Placeholder = new();

    public void Expand()
    {
        if (_childrenLoaded || _isPlaceholder) return;
        _childrenLoaded = true;
        Children.Clear();
        if (!Directory.Exists(FullPath)) return;
        try
        {
            foreach (var d in Directory.GetDirectories(FullPath)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                Children.Add(new FileTreeItem(d, this));
            foreach (var f in Directory.GetFiles(FullPath)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                Children.Add(new FileTreeItem(f, this));
        }
        catch { /* access denied, etc. */ }
    }

    public void Refresh()
    {
        if (!IsDirectory) return;
        _childrenLoaded = false;
        Children.Clear();
        Children.Add(Placeholder);
        if (IsExpanded)
            Expand();
    }

    /// <summary>Load top-level children of a folder (eager, for root display).</summary>
    public static ObservableCollection<FileTreeItem> LoadChildren(string folderPath)
    {
        var items = new ObservableCollection<FileTreeItem>();
        try
        {
            foreach (var d in Directory.GetDirectories(folderPath)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                items.Add(new FileTreeItem(d));
            foreach (var f in Directory.GetFiles(folderPath)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                items.Add(new FileTreeItem(f));
        }
        catch { }
        return items;
    }
}

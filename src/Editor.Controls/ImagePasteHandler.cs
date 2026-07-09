using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Editor.Core.Editing;

namespace Editor.Controls;

/// <summary>
/// Bridges a clipboard image to a saved file + Markdown link. The pure rules (where to save,
/// how to name the file) live in <see cref="ImagePasteOptions"/> (Editor.Core); this WPF-side
/// class supplies the two things Core cannot: reading the image bytes off the system clipboard
/// and writing them to disk. When the clipboard holds an image and the current file is Markdown,
/// <see cref="TryBuildMarkdownLink"/> saves the image next to the document and returns the
/// <c>![alt](path)</c> text to insert; otherwise it returns null and the normal paste proceeds.
/// </summary>
public sealed class ImagePasteHandler
{
    private static readonly string[] MarkdownExtensions = [".md", ".markdown"];

    public ImagePasteOptions Options { get; set; } = new();

    public static bool IsMarkdownFile(string? filePath) =>
        filePath != null && MarkdownExtensions.Contains(
            Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the system clipboard currently holds bitmap image data. Cheap enough to gate
    /// a paste keystroke on; the actual decode/save only happens in <see cref="TryBuildMarkdownLink"/>.
    /// </summary>
    public static bool ClipboardHasImage()
    {
        try { return Clipboard.ContainsImage() || Clipboard.ContainsData("PNG"); }
        catch { return false; }
    }

    /// <summary>
    /// If the current file is Markdown and the clipboard holds an image, saves the image per
    /// <see cref="Options"/> and returns the Markdown link text to insert. Returns null when the
    /// paste is not an image-into-Markdown case, or if saving fails (<paramref name="error"/> is
    /// set in the failure case so the caller can surface it). The buffer is never touched here —
    /// the caller inserts the returned text.
    /// </summary>
    public string? TryBuildMarkdownLink(string? markdownFilePath, out string? error)
    {
        error = null;
        if (!IsMarkdownFile(markdownFilePath)) return null;

        byte[]? png;
        try { png = GetClipboardPngBytes(); }
        catch (Exception ex) { error = $"Image paste failed: {ex.Message}"; return null; }
        if (png == null) return null;

        try
        {
            var target = Options.Resolve(markdownFilePath, DateTime.Now, File.Exists);
            var dir = Path.GetDirectoryName(target.AbsolutePath);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            File.WriteAllBytes(target.AbsolutePath, png);
            return $"![{target.AltText}]({target.LinkPath})";
        }
        catch (Exception ex)
        {
            error = $"Image save failed: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Reads the clipboard image as PNG bytes. Prefers a raw "PNG" clipboard payload (preserves
    /// alpha/exact bytes when an app provides it) and otherwise re-encodes the DIB bitmap the
    /// clipboard exposes. Returns null when there is no image.
    /// </summary>
    private static byte[]? GetClipboardPngBytes()
    {
        if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream ms)
        {
            var bytes = ms.ToArray();
            if (bytes.Length > 0) return bytes;
        }

        if (!Clipboard.ContainsImage()) return null;
        var source = Clipboard.GetImage();
        if (source == null) return null;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var outStream = new MemoryStream();
        encoder.Save(outStream);
        return outStream.ToArray();
    }
}

using System.IO;
using Editor.Core.Config;

namespace Editor.Core.Buffer;

/// <summary>
/// Everything opening a file needs from disk, gathered <b>without touching the engine</b> so a host
/// can gather it on a background thread and then apply it in one step on the UI thread.
/// </summary>
/// <remarks>
/// Opening a file used to do all of this on whichever thread called <c>LoadFile</c> — in a WPF host
/// that is the UI thread, so typing, scrolling and painting stopped for the whole of it:
/// the <c>.editorconfig</c> walk up the directory tree (a <c>File.Exists</c> per level, then reading
/// and parsing every file found), reading the file's bytes, encoding and binary detection, decoding,
/// and splitting the text into lines. None of that needs the UI thread; only installing the result
/// does. <see cref="Prepare"/> does the first part, and <c>VimEngine.LoadFile</c> takes the
/// result and does the second.
/// <para>
/// The prepared buffer is a plain object with no affinity to any thread, and it is only ever handed
/// to the engine that asked for it. If the file was already open by the time the load is applied,
/// the engine keeps the open buffer and the prepared one is simply dropped — the same outcome as
/// the synchronous path, which also reuses an already-open buffer.
/// </para>
/// </remarks>
public sealed class PreparedFileLoad
{
    private PreparedFileLoad(string path, EditorConfigSettings config, VimBuffer? buffer)
    {
        Path = path;
        Config = config;
        Buffer = buffer;
    }

    /// <summary>The path this was prepared for. A load for any other path ignores it.</summary>
    public string Path { get; }

    internal EditorConfigSettings Config { get; }

    /// <summary>The buffer read from disk, or null when the file does not exist yet.</summary>
    internal VimBuffer? Buffer { get; }

    /// <summary>
    /// Reads and decodes everything <c>VimEngine.LoadFile</c> would otherwise read itself.
    /// Safe to call from any thread; it touches no engine state.
    /// </summary>
    public static PreparedFileLoad Prepare(string path)
    {
        var config = EditorConfig.LoadForFile(path);
        config.TryGetFileEncoding(out var preferredEncoding);
        // A file that isn't there yet is created empty by the load itself; there is nothing to read.
        var buffer = File.Exists(path) ? new VimBuffer(path, preferredEncoding) : null;
        return new PreparedFileLoad(path, config, buffer);
    }

    /// <summary>Whether this preparation is usable for <paramref name="path"/>.</summary>
    internal bool Matches(string path)
        => string.Equals(Path, path, System.StringComparison.OrdinalIgnoreCase);
}

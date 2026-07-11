using Editor.Core.Extensibility;
using Editor.Core.Syntax.Languages;

namespace Editor.Core.Syntax;

public sealed record SyntaxLanguageDescriptor(string Name, IReadOnlyList<string> Extensions);

/// <summary>Thread-safe registry of factories. A fresh language instance is created for every engine selection.</summary>
public sealed class SyntaxLanguageRegistry
{
    private sealed record Entry(long Id, SyntaxLanguageDescriptor Descriptor, Func<ISyntaxLanguage> Factory);
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Entry>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId;
    public static SyntaxLanguageRegistry Default { get; } = CreateDefault();

    public IDisposable Register(SyntaxLanguageDescriptor descriptor, Func<ISyntaxLanguage> factory, RegistrationPolicy policy = RegistrationPolicy.Reject)
    {
        ArgumentNullException.ThrowIfNull(descriptor); ArgumentNullException.ThrowIfNull(factory);
        if (string.IsNullOrWhiteSpace(descriptor.Name) || descriptor.Extensions is null || descriptor.Extensions.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("A name and valid extensions are required.", nameof(descriptor));
        lock (_gate)
        {
            var collision = Active().Any(e => e.Descriptor.Name.Equals(descriptor.Name, StringComparison.OrdinalIgnoreCase) || e.Descriptor.Extensions.Any(a => descriptor.Extensions.Any(b => Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase))));
            if (collision && policy == RegistrationPolicy.Reject) throw new InvalidOperationException($"Syntax language '{descriptor.Name}' conflicts with an existing registration.");
            _byName.TryGetValue(descriptor.Name, out var entries); entries ??= (_byName[descriptor.Name] = []);
            var extensions = Array.AsReadOnly(descriptor.Extensions.Select(Normalize).ToArray());
            var entry = new Entry(++_nextId, new(descriptor.Name, extensions), factory); entries.Add(entry);
            return new Registration(() => Remove(entry));
        }
    }

    public IReadOnlyList<SyntaxLanguageDescriptor> Languages { get { lock (_gate) return Active().Select(e => e.Descriptor).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(); } }
    internal ISyntaxLanguage? CreateByName(string name) { lock (_gate) return _byName.TryGetValue(name, out var e) && e.Count > 0 ? Create(e[^1]) : null; }
    internal ISyntaxLanguage? CreateByExtension(string extension) { lock (_gate) { var normalized = Normalize(extension); var e = Active().OrderByDescending(x => x.Id).FirstOrDefault(x => x.Descriptor.Extensions.Any(v => v.Equals(normalized, StringComparison.OrdinalIgnoreCase))); return e is null ? null : Create(e); } }
    private static ISyntaxLanguage Create(Entry e) => e.Factory() ?? throw new InvalidOperationException($"Syntax factory '{e.Descriptor.Name}' returned null.");
    private IEnumerable<Entry> Active() => _byName.Values.Where(x => x.Count > 0).Select(x => x[^1]);
    private void Remove(Entry e) { lock (_gate) { if (!_byName.TryGetValue(e.Descriptor.Name, out var list)) return; list.RemoveAll(x => x.Id == e.Id); if (list.Count == 0) _byName.Remove(e.Descriptor.Name); } }
    private static string Normalize(string extension) => extension.StartsWith('.') ? extension : "." + extension;
    private static SyntaxLanguageRegistry CreateDefault()
    {
        var r = new SyntaxLanguageRegistry();
        Add<CSharpSyntax>(r); Add<PythonSyntax>(r); Add<XmlSyntax>(r); Add<MarkdownSyntax>(r); Add<JavaScriptSyntax>(r); Add<TypeScriptSyntax>(r); Add<RustSyntax>(r); Add<JsonSyntax>(r); Add<TomlSyntax>(r); Add<YamlSyntax>(r); Add<ShellSyntax>(r); Add<CssSyntax>(r); Add<SqlSyntax>(r); Add<CppSyntax>(r); Add<GoSyntax>(r); Add<BatchSyntax>(r); Add<PowerShellSyntax>(r); Add<LuaSyntax>(r); Add<RubySyntax>(r); return r;
    }
    private static void Add<T>(SyntaxLanguageRegistry r) where T : ISyntaxLanguage, new() { var sample = new T(); r.Register(new(sample.Name, sample.Extensions), static () => new T()); }
    private sealed class Registration(Action action) : IDisposable { private Action? _action = action; public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke(); }
}

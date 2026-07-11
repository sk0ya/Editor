# Host extensibility

Hosts can add syntax languages and commands without changing Editor source or loading assemblies by reflection. The host remains responsible for loading and trusting plug-in assemblies.

Registrations are thread-safe and owned by the returned `IDisposable`. Dispose it when disabling or unloading a plug-in. `RegistrationPolicy.Reject` is the default; `Replace` temporarily shadows an existing registration and restores it when disposed.

```csharp
var languages = new SyntaxLanguageRegistry();
using var language = languages.Register(
    new SyntaxLanguageDescriptor("My language", [".mine"]),
    () => new MySyntaxLanguage());
var syntax = new SyntaxEngine(languages);

var commands = new EditorCommandRegistry();
using var command = commands.RegisterAsync(
    new EditorCommandDescriptor("deploy", "Deploy", "Deploy the workspace", ["dp"]),
    async (context, cancellationToken) => {
        await DeployAsync(context.Arguments, cancellationToken);
        return EditorCommandResult.Ok("Deployed");
    });
```

Use `Register` for synchronous commands and `RegisterAsync` for asynchronous commands. Pass the registry into `VimEngine`, or set `VimEditorControlOptions.Commands`, to expose synchronous handlers through Ex commands. An asynchronous registration is never started by the synchronous Ex path; invoke it with `ExecuteAsync`. `VimEditorControl.ExtensionCommands` and `Commands` provide stable, sorted metadata snapshots suitable for a host command palette. `EditorCommandContext.Services` can carry host services without coupling Editor.Core to a dependency-injection library.

For raw Ex input, prefer `ExCommandProcessor.ExecuteExtensionAsync`, `VimEngine.ExecuteExtensionCommandAsync`, or `VimEditorControl.ExecuteExtensionCommandAsync`. These use the same range parser as synchronous Ex execution, preserve the exact raw input, and pass the parsed range, arguments, cursor, host services, and cancellation token to asynchronous handlers.

Syntax registrations are factories because tokenizers may contain document state. The registry creates a distinct instance for each engine/language selection; factories must return a new instance and may be called from any thread. Registry metadata snapshots are immutable arrays and do not expose live collections.

`SyntaxLanguageRegistry.Default` contains all built-in languages and `EditorCommandRegistry.Default` is the process-wide convenience registry. Dedicated registries are recommended when plug-in isolation or deterministic lifetime is important.

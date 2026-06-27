---
name: nuget-publish
description: Bump the NuGet package version and publish the Editor package to nuget.org. Use when the user asks to release, bump version, pack, or publish the Editor NuGet package (e.g. "nugetのバージョン上げてpublish", "release to nuget").
---

# NuGet Publish

Bump the version and publish the Editor package to nuget.org. Just do it — don't ask about scope, keys, or CI.

## Package layout (one published package, all DLLs bundled)

As of 2026-06-27 the Editor ships as a **single** NuGet package, `sk0ya.Editor.Controls`, packed from the
**Defaults project** (top of the dependency tree). It bundles all three DLLs (`Editor.Core.dll`,
`Editor.Controls.dll`, `Editor.Controls.Defaults.dll`) into `lib/`, with LSP/Git included.

- `src/Editor.Controls.Defaults/Editor.Controls.Defaults.csproj` — **the** package, `PackageId` = `sk0ya.Editor.Controls`, `IsPackable=true`. Bundles the other DLLs via a `BuildOutputInPackage` target; its `ProjectReference`s use `PrivateAssets="all"` so no NuGet dependency is emitted.
- `src/Editor.Core/Editor.Core.csproj` — `IsPackable=false` (not published).
- `src/Editor.Controls/Editor.Controls.csproj` — `IsPackable=false`, no `PackageId` (not published).

The old `sk0ya.Editor.Core` / `sk0ya.Editor.Controls.Defaults` ids are abandoned (frozen at 1.0.28).

## Steps

1. **Find current version**: grep `<Version>` in the three `.csproj` files. They should all match.
2. **Bump**: increment patch version (e.g. 1.0.29 → 1.0.30) in **all three** `.csproj` files via Edit (kept in sync so assembly versions match), even though only Defaults is packed. Use a higher bump only if the user specifies (minor/major).
3. **Pack** only the Defaults project:
   ```
   dotnet pack src/Editor.Controls.Defaults/Editor.Controls.Defaults.csproj -c Release
   ```
   Output lands at `src/Editor.Controls.Defaults/bin/Release/sk0ya.Editor.Controls.<VER>.nupkg`.
   (Optional sanity check: the package's `lib/` should contain all three DLLs and the nuspec dependency group should be empty.)
4. **Push** to nuget.org using the `NUGET_API_KEY` env var:
   ```
   dotnet nuget push src/Editor.Controls.Defaults/bin/Release/sk0ya.Editor.Controls.<VER>.nupkg --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
   ```
   Replace `<VER>` with the new version.
5. **Commit & push** the version bump:
   ```
   git add -A && git commit -m "Bump NuGet package to <VER>" && git push
   ```
   End the commit message with the `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>` trailer.
6. **Report** the new version and confirm the push succeeded ("Created" / "パッケージがプッシュされました") plus the commit hash.

## Notes

- The single package is self-contained (empty dependency group + WPF `frameworkReference` only). No ILMerge — DLLs are copied into `lib/`, which is WPF-safe.
- Commit straight to `main` (no feature branch). Tag only if the user asks.

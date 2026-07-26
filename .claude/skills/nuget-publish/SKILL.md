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

**Commit before you pack.** The build stamps the current `HEAD` into the DLLs' `InformationalVersion` and
into the nuspec `<repository commit="…">` (SourceLink). If you pack with uncommitted work in the tree, the
binaries contain that work but the recorded commit points at the *previous* commit — so SourceLink resolves
to sources that don't match the shipped IL, and the package looks like it was built from a stale state.
This is unfixable after pushing, because a NuGet version can never be re-uploaded.

1. **Find current version**: grep `<Version>` in the three `.csproj` files. They should all match.
2. **Bump**: increment patch version (e.g. 1.0.29 → 1.0.30) in **all three** `.csproj` files via Edit (kept in sync so assembly versions match), even though only Defaults is packed. Use a higher bump only if the user specifies (minor/major).
3. **Commit everything** (`git status --short` must come back empty afterwards) — the bump plus any other
   pending work that will end up in this release:
   ```
   git add -A && git commit -m "Bump NuGet package to <VER>"
   ```
   End the commit message with the `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>` trailer.
   Don't `git push` yet — that comes after nuget.org accepts the package.
4. **Pack** only the Defaults project:
   ```
   dotnet pack src/Editor.Controls.Defaults/Editor.Controls.Defaults.csproj -c Release
   ```
   Output lands at `src/Editor.Controls.Defaults/bin/Release/sk0ya.Editor.Controls.<VER>.nupkg`.
5. **Verify the package before pushing** — the recorded commit must be the commit from step 3:
   ```powershell
   $ver="<VER>"; $sp="$env:TEMP\nupkg-check"; Remove-Item -Recurse -Force $sp -ErrorAction SilentlyContinue
   Expand-Archive "src/Editor.Controls.Defaults/bin/Release/sk0ya.Editor.Controls.$ver.nupkg" $sp
   git rev-parse HEAD
   Select-String -Path "$sp\sk0ya.Editor.Controls.nuspec" -Pattern 'version>|repository|<dependencies|<group'
   Get-ChildItem "$sp\lib\net9.0-windows7.0" | ForEach-Object {
     "{0} {1}" -f $_.Name, [Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName).ProductVersion }
   ```
   Check: nuspec version = `<VER>`; `repository commit=` **equals `git rev-parse HEAD`**; all three DLLs
   present with `ProductVersion` = `<VER>+<HEAD>`; dependency group empty.
   If the commit doesn't match, **stop** — commit the stray changes, re-pack, and re-verify.
6. **Push** to nuget.org using the `NUGET_API_KEY` env var:
   ```
   dotnet nuget push src/Editor.Controls.Defaults/bin/Release/sk0ya.Editor.Controls.<VER>.nupkg --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
   ```
   Replace `<VER>` with the new version.
7. **Push the commit**: `git push`.
8. **Report** the new version and confirm the push succeeded ("Created" / "パッケージがプッシュされました") plus the commit hash.

## Notes

- The single package is self-contained (empty dependency group + WPF `frameworkReference` only). No ILMerge — DLLs are copied into `lib/`, which is WPF-safe.
- Commit straight to `main` (no feature branch). Tag only if the user asks.
- Verifying a package **already on** nuget.org: download it
  (`https://api.nuget.org/v3-flatcontainer/sk0ya.editor.controls/<VER>/sk0ya.editor.controls.<VER>.nupkg`)
  and compare the extracted DLLs' hashes with the local pack output. The whole-`.nupkg` hash will *not*
  match — nuget.org adds a `.signature.p7s` repository signature — so always compare the DLLs, not the archive.
  Zip entry timestamps for the DLLs are stored in UTC while the nuspec's are local, so a JST-local build looks
  9 hours "older" than the pack; that gap is normal, not staleness.

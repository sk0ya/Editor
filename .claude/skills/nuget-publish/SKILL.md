---
name: nuget-publish
description: Bump the NuGet package version and publish all packages to nuget.org. Use when the user asks to release, bump version, pack, or publish the Editor NuGet packages (e.g. "nugetのバージョン上げてpublish", "release to nuget").
---

# NuGet Publish

Bump the version and publish the three Editor packages to nuget.org. Just do it — don't ask about scope, keys, or CI.

## Packages (bump in lockstep)

All three share one version and must be bumped together:

- `src/Editor.Core/Editor.Core.csproj` — `sk0ya.Editor.Core`
- `src/Editor.Controls/Editor.Controls.csproj` — `sk0ya.Editor.Controls`
- `src/Editor.Controls.Defaults/Editor.Controls.Defaults.csproj` — `sk0ya.Editor.Controls.Defaults`

## Steps

1. **Find current version**: grep `<Version>` in the three `.csproj` files. They should all match.
2. **Bump**: increment patch version (e.g. 1.0.9 → 1.0.10) in all three `.csproj` files via Edit. Use a higher bump only if the user specifies (minor/major).
3. **Pack** (one project per `dotnet pack` invocation — it rejects multiple projects):
   ```
   dotnet pack src/Editor.Core/Editor.Core.csproj -c Release
   dotnet pack src/Editor.Controls/Editor.Controls.csproj -c Release
   dotnet pack src/Editor.Controls.Defaults/Editor.Controls.Defaults.csproj -c Release
   ```
   Output `.nupkg` files land in each project's `bin/Release/`.
4. **Push** to nuget.org using the `NUGET_API_KEY` env var:
   ```
   dotnet nuget push src/Editor.Core/bin/Release/sk0ya.Editor.Core.<VER>.nupkg --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
   dotnet nuget push src/Editor.Controls/bin/Release/sk0ya.Editor.Controls.<VER>.nupkg --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
   dotnet nuget push src/Editor.Controls.Defaults/bin/Release/sk0ya.Editor.Controls.Defaults.<VER>.nupkg --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
   ```
   Replace `<VER>` with the new version.
5. **Commit & push** the version bump:
   ```
   git add -A && git commit -m "Bump NuGet packages to <VER>" && git push
   ```
   End the commit message with the `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>` trailer.
6. **Report** the new version and confirm each push succeeded ("Created" / "パッケージがプッシュされました") plus the commit hash.

## Notes

- `Editor.Controls` and `Editor.Controls.Defaults` use `ProjectReference` to `Editor.Core`, so packing auto-adds a package dependency on the matching version — that's why lockstep bumping matters.
- Commit straight to `main` (no feature branch). Tag only if the user asks.

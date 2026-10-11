# Repository Guidelines

## Project Structure

The application is a Windows Forms C# project in `src/CodexTokenOverlay/`, targeting .NET 10 for Windows. UI assets are in `Assets/`; deterministic test and probe code lives alongside production code in `*Tests.cs`. `tests/fixtures/` holds external probe inputs. Windows build, verification, startup, and preview scripts are in `scripts/`; generated UI images are in `previews/`. `third_party/` contains runtime notices and licenses. Build output and local publish artifacts belong in ignored `bin/`, `obj/`, and `artifacts/` directories.

## Build and Verify

Use Windows with the .NET 10 SDK selected by `global.json`. From the repository root:

```powershell
dotnet build .\src\CodexTokenOverlay\CodexTokenOverlay.csproj -c Release
dotnet publish .\src\CodexTokenOverlay\CodexTokenOverlay.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\verify
.\scripts\Verify.ps1 -ExecutablePath .\artifacts\verify\CodexRelayUsage.exe
```

The build compiles the app; publish creates a self-contained x64 executable. `Verify.ps1` runs synthetic tests and Windows UI/startup probes, so run it on an interactive Windows desktop. `scripts/Publish-Local.ps1` performs the fuller release packaging and preview-generation workflow.

## Coding Style

Follow the surrounding C# style: four spaces, nullable reference types, file-scoped `CodexTokenOverlay` namespaces, PascalCase types and public members, and camelCase locals/parameters. Keep UI behavior in the existing focused forms/controllers and add deterministic checks beside related `*Tests.cs` code. PowerShell scripts should remain compatible with Windows PowerShell where their test harnesses require it.

## Testing

There is no separate test project or `dotnet test` suite. Add regression checks to the relevant in-project test class and wire new probes into `DiagnosticsRunner` or `Verify.ps1` when needed. Use synthetic logs and fixtures; do not depend on personal Codex sessions for routine tests. Ensure the build and verification commands above pass before submitting.

The `*Tests.cs` classes and the synthetic self-test are intentionally compiled into the shipped executable. `Verify.ps1` must exercise the exact bits users receive, including WinForms behavior that cannot be tested in a stripped build, so do not move them behind an `#if DEBUG` guard or a separate test assembly without preserving a publish-time verification path.

## Commits and Pull Requests

History includes release titles and scoped prefixes such as `fix:`, `feat:`, and `ui:`. Use a concise imperative subject; use a prefix for focused changes (for example, `fix: preserve session selection`). PRs should explain user-visible behavior and rationale, list build/test results, link related issues when applicable, and include screenshots for UI changes. Call out Windows-only manual checks that remain.

## Security and Local Data

The app reads local Codex logs and estimates costs; never commit real session logs, credentials, or personal `prices.json`/settings files. Keep test data synthetic, and preserve the documented read-only behavior when changing log handling.

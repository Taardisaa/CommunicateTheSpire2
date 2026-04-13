# Repository Guidelines

## Project Structure & Module Organization
- Root C# mod source: `ModEntry.cs`, `HarmonyPatches.cs`, `CtS2Log.cs`.
- Project file: `CtS2.csproj` (`net9.0`, Godot .NET SDK 4.5.1).
- Runtime dependencies: `lib/` (expects `sts2.dll` and `0Harmony.dll`, unless `STS2_GAME_PATH` is set).
- Packaging project: `pck_only/` (used to export `CommunicateTheSpire2.pck`).
- Protocol and controller scaffolding: `Protocol/`, `controller/`, `Ipc/`.
- Docs: `docs/`; config/metadata: `project.godot`, `mod_manifest.json`, `export_presets.cfg`.
- Build output: `bin/`; temporary Godot/MSBuild output: `.godot/`.

## Build, Test, and Development Commands
- Build DLL (Release): `dotnet build CtS2.csproj -c Release`
- Build DLL with explicit game DLL path:
  `dotnet build CtS2.csproj -c Release -p:Sts2GamePath="D:\...\Slay the Spire 2\data_sts2_windows_x86_64"`
- Export PCK (headless Godot):
  `"<GodotConsoleExe>" --headless --path pck_only --export-pack "PCK" CommunicateTheSpire2.pck`
- Verify compilation quickly (optional): `dotnet build CtS2.csproj -c Debug`

## Coding Style & Naming Conventions
- Language: C# 12 with nullable enabled.
- Indentation: tabs in existing `.cs` files; preserve file-local style.
- Naming: `PascalCase` for types/methods/properties, `_camelCase` for private fields, descriptive file names matching primary type.
- Keep Harmony patches narrow and explicit; log meaningful lifecycle events to `CommunicateTheSpire2.log`.

## Testing Guidelines
- No formal automated test project exists yet.
- Minimum validation for changes:
  1. Build succeeds in `Release`.
  2. Game loads mod with both `CommunicateTheSpire2.dll` and `CommunicateTheSpire2.pck` in the game `mods/` folder.
  3. `%APPDATA%\SlayTheSpire2\CommunicateTheSpire2.log` shows expected initialization lines.
- If adding testable protocol logic, prefer isolated unit tests in a future `tests/` project.

## Commit & Pull Request Guidelines
- Follow concise, imperative commit subjects (current history pattern includes scoped subjects, e.g., `Docs: mark as placeholder`).
- Keep commits focused (one logical change per commit).
- PRs should include:
  - What changed and why.
  - Local verification steps/commands run.
  - Any gameplay/mod-loading evidence (log excerpts or screenshots) for behavior changes.
  - Linked issue/task when available.

## Additional Info

Checkout the parent directory of the current project dir. It contains the extract game data, and the CommunicationMod for Slay the Spire 1.
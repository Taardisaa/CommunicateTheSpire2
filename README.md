# CommunicateTheSpire2

**[中文版](README_CN.md)**

Slay the Spire 2 mod scaffolding for building an IPC bridge (inspired by StS1 CommunicationMod). Currently this repo is a scaffold: it verifies mod loading and writes logs.

## Status

**Placeholder / under active development.** The current implementation only proves mod loading and includes a minimal **stdio transport + `ready` handshake** to a controller process. It does **not** yet stream game state or execute gameplay commands.

- **Roadmap**: see `docs/PLAN.md`

---

## What you need

- **Slay the Spire 2** installed (e.g. via Steam).
- **Dotnet 9.0.303** (for building the mod DLL). [Download](https://github.com/dotnet/core/blob/main/release-notes/9.0/9.0.7/9.0.303.md).
- **Godot 4.5.1** with console executable (for building the PCK). Example: `Godot_v4.5.1-stable_mono_win64_console.exe` ([Godot v4.5.1](https://github.com/godotengine/godot/releases/tag/4.5-stable))
- Game DLLs: **sts2.dll** and **0Harmony.dll** from the game directory (no game source code required).

---

## Part 1: Build the mod

You will produce two files: **CommunicateTheSpire2.pck** and **CommunicateTheSpire2.dll**. Both are required for the mod to run.

### Step 1 — Get the game’s DLLs

The mod DLL is built against the game’s assemblies. You only need two DLLs from the installed game.

1. Open your game install folder.
2. Locate **sts2.dll** and **0Harmony.dll**. They a subfolder called **data_sts2_windows_x86_64** on Windows.
3. Create the folder **CommunicateTheSpire2/lib/** (inside the repo).
4. Copy **sts2.dll** and **0Harmony.dll** into **CommunicateTheSpire2/lib/**.

<!-- Alternatively, set the environment variable **STS2_GAME_PATH** to the folder that contains those two DLLs (e.g. `D:\...\Slay the Spire 2\data_sts2_windows_x86_64`). Then you can skip copying into `lib/`. -->

---

### Step 2 — Build the mod DLL

From the **repository root** (the folder that contains `CommunicateTheSpire2/`), run:

```bash
dotnet build CommunicateTheSpire2/CtS2.csproj -c Release
```

<!-- If you did not use **SlayTheSpire2-demo-mod/lib/** and instead use **STS2_GAME_PATH**, set it first (Windows PowerShell):

```powershell
$env:STS2_GAME_PATH = "D:\Software\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64"
dotnet build SlayTheSpire2-demo-mod/CtS2.csproj -c Release
``` -->
<!-- 
Or pass the path on the command line:

```bash
dotnet build SlayTheSpire2-demo-mod/CtS2.csproj -c Release -p:Sts2GamePath="D:\...\Slay the Spire 2\data_sts2_windows_x86_64"
``` -->

**Output:**  
`CommunicateTheSpire2/bin/Release/CommunicateTheSpire2.dll` (and optionally `CommunicateTheSpire2.pdb`).  
Keep **CommunicateTheSpire2.dll**; you will copy it into the game’s mods folder later.

---

### Step 3 — Build the mod PCK

The game loads mods only when it finds a **.pck** file. The PCK must contain **mod_manifest.json** at root. Use the **pck_only** project so Godot does not try to export C# or pack build artifacts.

1. **Godot path**  
   Use your Godot 4 **console** executable, e.g.:  
   `D:\Software\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe`

2. From the **repository root**, run (adjust the Godot path if needed):

```bash
D:\Software\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe --headless --path CommunicateTheSpire2/pck_only --export-pack "PCK" ../CommunicateTheSpire2.pck
```

Or open **CommunicateTheSpire2/pck_only** in the Godot editor and use **Project → Export → PCK** to export **CommunicateTheSpire2.pck** into the **CommunicateTheSpire2** folder.

**Output:**  
`CommunicateTheSpire2/CommunicateTheSpire2.pck`.  
This file is small (~1–2 KB) and contains only the manifest and minimal project data.

---

### Build summary

After Steps 1–3 you should have:

| File              | Location                     | Use in install |
|-------------------|------------------------------|-----------------|
| **CommunicateTheSpire2.dll**   | `CommunicateTheSpire2/bin/Release/`      | Copy to game **mods** folder |
| **CommunicateTheSpire2.pck**   | `CommunicateTheSpire2/`                 | Copy to game **mods** folder |

---

## Part 2: Install the mod

### Step 1 — Locate the game’s mods folder

Create a sub directory **mods** inside the game install, e.g., `…\Steam\steamapps\common\Slay the Spire 2\mods\`.

<!-- The mods folder must be **next to the game executable**.

- **Typical path (Steam):**  
  `…\Steam\steamapps\common\Slay the Spire 2\mods\`
- The executable is usually **SlayTheSpire2.exe** in that same **Slay the Spire 2** folder. Create **mods** there if it does not exist. -->

### Step 2 — Copy the mod files

Copy into that **mods** folder:

1. **CommunicateTheSpire2.pck**
2. **CommunicateTheSpire2.dll**

Example (PowerShell, adjust paths to match your machine):

```powershell
$mods = "D:\Software\Steam\steamapps\common\Slay the Spire 2\mods"
Copy-Item "CommunicateTheSpire2\CommunicateTheSpire2.pck" $mods
Copy-Item "CommunicateTheSpire2\bin\Release\CommunicateTheSpire2.dll" $mods
```

Result:

```
…\Slay the Spire 2\
  SlayTheSpire2.exe
  mods\
    CommunicateTheSpire2.pck
    CommunicateTheSpire2.dll
```

### Step 3 — Enable mods in the game

1. Start **Slay the Spire 2**.
2. If a popup appears on the main menu asking to enable mods, choose **Yes** (load mods).
3. The game will **quit** so mods can load. Start the game again.
4. Go to **Settings → Modding**. You should see **Communicate The Spire 2** in the list and it should be enabled.

### Step 4 — Verify the mod is running

The mod writes a log file so you can confirm it ran without using the console.

- **Windows:**  
  `%APPDATA%\SlayTheSpire2\CommunicateTheSpire2.log`  
  (e.g. `C:\Users\<YourUser>\AppData\Roaming\SlayTheSpire2\CommunicateTheSpire2.log`)
- Open that file after starting the game with the mod enabled. You should see lines such as:
  - `CommunicateTheSpire2 assembly loaded; ModEntry static constructor ran (ModManager is initializing this mod).`
  - `Init() entered — ModManager called our initializer.`
  - `Harmony postfix ran after ModManager.Initialize — hooking works.`

<!-- If **CtS2.log** never appears, see **docs/mod-troubleshooting.md** (e.g. mods folder location, agreeing to load mods, or only having the DLL without the PCK). -->

<!-- ---

## Quick reference

| Goal              | Action |
|-------------------|--------|
| **Build DLL**     | Put **sts2.dll** and **0Harmony.dll** in **SlayTheSpire2-demo-mod/lib/** (or set **STS2_GAME_PATH**), then run `dotnet build SlayTheSpire2-demo-mod/CtS2.csproj -c Release`. |
| **Build PCK**     | Run Godot with `--path SlayTheSpire2-demo-mod/pck_only --export-pack "PCK" SlayTheSpire2-demo-mod/CtS2.pck`. |
| **Install**       | Copy **CtS2.pck** and **CtS2.dll** into the game’s **mods** folder (next to the .exe). |
| **Enable**        | Start the game, accept the “enable mods?” popup if shown, restart when prompted. |
| **Verify**        | Check **%APPDATA%\SlayTheSpire2\CtS2.log** for the mod’s log lines. |

--- -->

<!-- ## More information

- **Mod system and enabling mods:** `docs/enabling-mods.md`
- **Mod not loading or no log file:** `docs/mod-troubleshooting.md`
- **Mod design (ModTheSpire2, Harmony, etc.):** `docs/modthespire2-design.md` -->


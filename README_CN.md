# CommunicateTheSpire2

**[English](README.md)**

适用于 **《杀戮尖塔 2》** 的模组脚手架，用于后续构建 IPC 桥接（参考 StS1 CommunicationMod）。目前仅验证模组加载并写入日志文件。

## 状态

**占位 / 正在积极开发中。** 当前实现只用于验证模组加载，并包含最小的 **stdio 传输 + `ready` 握手**（启动外部控制器进程并等待其就绪）。目前**尚未**实现状态推送或执行游戏指令。

- **路线图**：见 `docs/PLAN.md`

---

## 所需环境

- 已安装 **《杀戮尖塔 2》**（例如通过 Steam）。
- **Dotnet 9.0.303**（用于编译模组 DLL）。[下载](https://github.com/dotnet/core/blob/main/release-notes/9.0/9.0.7/9.0.303.md)。
- **Godot 4.5.1** 带控制台可执行文件（用于编译 PCK）。例如：`Godot_v4.5.1-stable_mono_win64_console.exe`（[Godot v4.5.1](https://github.com/godotengine/godot/releases/tag/4.5-stable)）
- 游戏 DLL：游戏目录中的 **sts2.dll** 和 **0Harmony.dll**（无需游戏源码）。

---

## 第一部分：编译模组

你将得到两个文件：**CommunicateTheSpire2.pck** 和 **CommunicateTheSpire2.dll**。两者都是模组运行所必需的。

### 步骤 1 — 获取游戏的 DLL

模组 DLL 是针对游戏的程序集编译的。你只需要从已安装游戏中获取两个 DLL。

1. 打开游戏安装目录。
2. 找到 **sts2.dll** 和 **0Harmony.dll**。在 Windows 上它们位于名为 **data_sts2_windows_x86_64** 的子文件夹中。
3. 在仓库内创建文件夹 **CommunicateTheSpire2/lib/**。
4. 将 **sts2.dll** 和 **0Harmony.dll** 复制到 **CommunicateTheSpire2/lib/**。

<!-- 或者，将环境变量 **STS2_GAME_PATH** 设置为包含这两个 DLL 的文件夹（例如 `D:\...\Slay the Spire 2\data_sts2_windows_x86_64`）。这样就不需要复制到 `lib/`。 -->

---

### 步骤 2 — 编译模组 DLL

在**仓库根目录**（包含 `CommunicateTheSpire2/` 的文件夹）下运行：

```bash
dotnet build CommunicateTheSpire2/DemoMod.csproj -c Release
```

<!-- 如果未使用 **SlayTheSpire2-demo-mod/lib/** 而使用 **STS2_GAME_PATH**，请先设置（Windows PowerShell）：

```powershell
$env:STS2_GAME_PATH = "D:\Software\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64"
dotnet build SlayTheSpire2-demo-mod/DemoMod.csproj -c Release
``` -->
<!-- 
或在命令行中传入路径：

```bash
dotnet build SlayTheSpire2-demo-mod/DemoMod.csproj -c Release -p:Sts2GamePath="D:\...\Slay the Spire 2\data_sts2_windows_x86_64"
``` -->

**输出：**  
`CommunicateTheSpire2/bin/Release/CommunicateTheSpire2.dll`（以及可选的 `CommunicateTheSpire2.pdb`）。  
保留 **CommunicateTheSpire2.dll**；稍后需要将其复制到游戏的 mods 文件夹。

---

### 步骤 3 — 编译模组 PCK

游戏只有在找到 **.pck** 文件时才会加载模组。PCK 必须在根目录包含 **mod_manifest.json**。使用 **pck_only** 项目，这样 Godot 不会尝试导出 C# 或打包编译产物。

1. **Godot 路径**  
   使用你的 Godot 4 **控制台**可执行文件，例如：  
   `D:\Software\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe`

2. 在**仓库根目录**下运行（如需请调整 Godot 路径）：

```bash
"D:\Software\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe" --headless --path CommunicateTheSpire2/pck_only --export-pack "PCK" ../CommunicateTheSpire2.pck
```

或者在 Godot 编辑器中打开 **CommunicateTheSpire2/pck_only**，使用 **项目 → 导出 → PCK** 将 **CommunicateTheSpire2.pck** 导出到 **CommunicateTheSpire2** 文件夹。

**输出：**  
`CommunicateTheSpire2/CommunicateTheSpire2.pck`。  
该文件较小（约 1–2 KB），仅包含清单和最少项目数据。

---

### 编译小结

完成步骤 1–3 后，你应该得到：

| 文件              | 位置                     | 安装时用途 |
|-------------------|------------------------------|-----------------|
| **CommunicateTheSpire2.dll**   | `CommunicateTheSpire2/bin/Release/`      | 复制到游戏 **mods** 文件夹 |
| **CommunicateTheSpire2.pck**   | `CommunicateTheSpire2/`                 | 复制到游戏 **mods** 文件夹 |

---

## 第二部分：安装模组

### 步骤 1 — 找到游戏的 mods 文件夹

在游戏安装目录下创建子目录 **mods**，例如：`…\Steam\steamapps\common\Slay the Spire 2\mods\`。

<!-- mods 文件夹必须**与游戏可执行文件同级**。

- **常见路径（Steam）：**  
  `…\Steam\steamapps\common\Slay the Spire 2\mods\`
- 可执行文件通常在该 **Slay the Spire 2** 文件夹中，名为 **SlayTheSpire2.exe**。若不存在 **mods** 文件夹，请在该处创建。 -->

### 步骤 2 — 复制模组文件

将以下文件复制到该 **mods** 文件夹：

1. **CommunicateTheSpire2.pck**
2. **CommunicateTheSpire2.dll**

示例（PowerShell，请根据你的机器调整路径）：

```powershell
$mods = "D:\Software\Steam\steamapps\common\Slay the Spire 2\mods"
Copy-Item "CommunicateTheSpire2\CommunicateTheSpire2.pck" $mods
Copy-Item "CommunicateTheSpire2\bin\Release\CommunicateTheSpire2.dll" $mods
```

结果目录结构：

```
…\Slay the Spire 2\
  SlayTheSpire2.exe
  mods\
    CommunicateTheSpire2.pck
    CommunicateTheSpire2.dll
```

### 步骤 3 — 在游戏中启用模组

1. 启动 **《杀戮尖塔 2》**。
2. 若主菜单出现询问是否启用模组的弹窗，选择 **是**（加载模组）。
3. 游戏会**退出**以便加载模组。请再次启动游戏。
4. 进入 **设置 → 模组**。你应能看到列表中的 **Demo Mod**，并处于启用状态。

### 步骤 4 — 确认模组已运行

演示模组会写入一个日志文件，这样你无需使用控制台即可确认它已运行。

- **Windows：**  
  `%APPDATA%\SlayTheSpire2\CommunicateTheSpire2.log`  
  （例如 `C:\Users\<你的用户名>\AppData\Roaming\SlayTheSpire2\CommunicateTheSpire2.log`）
- 在启用模组并启动游戏后打开该文件。你应该能看到类似以下内容：
  - `CommunicateTheSpire2 assembly loaded; ModEntry static constructor ran (ModManager is initializing this mod).`（CommunicateTheSpire2 程序集已加载；ModEntry 静态构造函数已执行（ModManager 正在初始化此模组）。）
  - `Init() entered — ModManager called our initializer.`（已进入 Init() — ModManager 调用了我们的初始化器。）
  - `Harmony postfix ran after ModManager.Initialize — hooking works.`（ModManager.Initialize 之后 Harmony 后置补丁已运行 — 钩子工作正常。）

## 许可证

本项目采用 MIT License，详见 [LICENSE](LICENSE)。

<!-- 若 **DemoMod.log** 从未出现，请参阅 **docs/mod-troubleshooting.md**（例如 mods 文件夹位置、是否同意加载模组，或是否只有 DLL 而没有 PCK）。 -->

<!-- ---

## 快速参考

| 目标              | 操作 |
|-------------------|--------|
| **编译 DLL**     | 将 **sts2.dll** 和 **0Harmony.dll** 放入 **SlayTheSpire2-demo-mod/lib/**（或设置 **STS2_GAME_PATH**），然后运行 `dotnet build SlayTheSpire2-demo-mod/DemoMod.csproj -c Release`。 |
| **编译 PCK**     | 使用 `--path SlayTheSpire2-demo-mod/pck_only --export-pack "PCK" SlayTheSpire2-demo-mod/DemoMod.pck` 运行 Godot。 |
| **安装**       | 将 **DemoMod.pck** 和 **DemoMod.dll** 复制到游戏的 **mods** 文件夹（与 .exe 同级）。 |
| **启用**        | 启动游戏，若出现“是否启用模组？”弹窗则选择是，按提示重启。 |
| **验证**        | 查看 **%APPDATA%\SlayTheSpire2\DemoMod.log** 中的模组日志行。 |

--- -->

<!-- ## 更多信息

- **模组系统与启用模组：** `docs/enabling-mods.md`
- **模组未加载或无日志文件：** `docs/mod-troubleshooting.md`
- **模组设计（ModTheSpire2、Harmony 等）：** `docs/modthespire2-design.md` -->


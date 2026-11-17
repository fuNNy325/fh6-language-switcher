# FH6 Language Switcher

(游戏刚上的时候用ai写的神秘小工具，现在沟槽的pg把音频和文本分开设置了，所以用不到了）

一个轻量的 Windows 图形工具，用于交换 Forza Horizon 6 本地字符表 ZIP 的**文件名**，尝试在其他语言配音下使用简体中文菜单和字幕。

C# + WinForms，.NET Framework 4.8，无第三方依赖、无联网、无需安装。MIT 开源。程序不包含、不下载、不分发游戏文件，也不会解压或编辑 ZIP 内部内容。

> 工具按照本地文件结构实现。文件交换、校验和故障恢复已使用模拟 ZIP 测试；实际游戏中的配音、菜单和字幕效果仍需验证。它不负责安装语音包或修改 Steam / 游戏语言设置，语言 ZIP 的存在也不代表游戏提供对应配音。

## 使用

1. 完全退出游戏。首次使用前，确保语言 ZIP **未经手动换名**；不确定时，先在 Steam 中验证游戏文件完整性。
2. 从 Release 或构建产物中解压工具，保持 `FH6LanguageSwitcher.exe` 和同名 `.exe.config` 在同一文件夹，双击 EXE。
3. 选择游戏根目录，也可直接选择 `media\Stripped\StringTables`。支持扫描常见 Steam 库。
4. 点击“检查”，选择目标语言，例如 `英语 · EN`。
5. 点击“备份并交换文件名”。工具会先备份，再交换 `CHS.zip` 和 `EN.zip` 的名字。
6. 在游戏 / Steam 中选择与目标 ZIP 对应的语言，按游戏要求重启并检查字幕、菜单和配音。

默认目录：

```text
F:\SteamLibrary\steamapps\common\ForzaHorizon6
└─ media\Stripped\StringTables
   ├─ CHS.zip
   ├─ EN.zip
   └─ ...
```

正常交换通过同一磁盘上的临时名字完成 `File.Move` 重命名。以 EN 为例：

| 文件名 | 交换之前 | 交换之后 |
| --- | --- | --- |
| `CHS.zip` | 原 CHS 文件 | 原 EN 文件 |
| `EN.zip` | 原 EN 文件 | 原 CHS 文件 |

再次点击同一种语言不会反复对换。选择另一种语言会在同一事务中还原上一组、交换新一组。点击“还原文件名”回到本工具首次备份时的状态。**工具无法判断首次备份的 ZIP 内部到底是哪种语言**。

## 备份、更新与恢复

备份保存在 `StringTables\.fh6-language-switcher`，与 EXE 放在哪里无关。只备份涉及的语言包；所有已记录文件和备份都使用 SHA-256 校验。此目录由工具维护，启用交换期间请保留它，勿手动编辑记录。

- **操作中断 / 程序退出**：重新启动，检查同一目录，点击“恢复中断操作”，恢复到本次操作之前。即使中断时 `CHS.zip` 暂时不存在，也能重新打开该目录。若回滚失败，记录和快照会保留。
- **文件被游戏更新、Steam 验证或手动操作改动**：工具会停止交换和普通还原，避免用旧备份覆盖新文件。
- **更新后继续使用**：先通过 Steam 验证游戏文件完整性，确保所有语言包均恢复为当前版本的原始文件；再点“重建备份基线”。旧备份及记录会保留归档，下次交换备份当前版本。重建操作本身不会恢复或修改 ZIP。
- **中断记录尚在，但文件随后又被其他程序改动**：恢复会拒绝覆盖冲突文件。先保留整个备份目录。若决定放弃旧事务，可关闭工具，将整个 `.fh6-language-switcher` 文件夹改名留存，然后通过 Steam 验证文件完整性，再重新检查和交换。
- **首次使用前已经手动交换**：先手动还原或通过 Steam 验证文件完整性，再使用本工具。
- **权限不足 / 文件被占用**：退出游戏和占用 ZIP 的程序，检查目录写入权限；确有需要时，以管理员身份运行。

正常切换和还原通过重命名完成。仅在失败或中断恢复时，工具可能用快照重建文件；ZIP 字节保持原样。记录使用写入后刷新和原子替换；多个 ZIP 的重命名由事务日志协调，不能保证系统断电时文件系统的行为。该工具是独立社区项目，与游戏开发商、发行商无关联。

## 构建与测试

Windows 10 / 11，安装 .NET Framework 4.8。使用 Windows 自带的 C# 编译器；**不需要 `dotnet` SDK、NuGet 或 Dev Kit**。VS Code C# 插件可用于编辑，插件本身不等同于 SDK。

在项目根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

这只对当前 PowerShell 进程设置执行策略，不修改系统策略。输出位于 `dist/`。构建脚本启用编译警告即错误。测试使用 `dist/test-data/` 下自动生成的临时 ZIP，不访问真实游戏目录，结束后清理自己的测试数据。

打包便携版和仅含源码的压缩包：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Package.ps1
```

测试覆盖真实文件重命名（Windows 文件标识不变）、重复操作、切换与还原、相同哈希文件、损坏 ZIP / 备份、外部更新、文件锁、并发互斥，以及每个重命名步骤的异常回滚和子进程崩溃恢复。

如需手动测试 GUI，可生成与游戏无关的示例文件：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\New-Demo.ps1
.\dist\FH6LanguageSwitcher.exe --directory ".\artifacts\demo"
```

## 项目结构

```text
src/SwapEngine.cs        重命名、备份、哈希、事务和恢复
src/MainForm.cs          中文图形界面、Steam 目录查找
src/Program.cs           应用入口和版本信息
tests/EngineTests.cs     无第三方框架的 Windows 集成测试
build.ps1               离线编译和测试
.github/workflows/      自动构建与打包
```

## 源码与发布

源码仓库：[fuNNy325/fh6-language-switcher](https://github.com/fuNNy325/fh6-language-switcher)。仓库仅提交源码和文档；`.gitignore` 已排除 EXE、ZIP、字符表、备份和本地测试数据。

[GitHub Actions](https://github.com/fuNNy325/fh6-language-switcher/actions) 会构建、测试并提供可下载的工具压缩包。正式版本发布在 [Releases](https://github.com/fuNNy325/fh6-language-switcher/releases)。

获取源码：

```powershell
git clone https://github.com/fuNNy325/fh6-language-switcher.git
cd fh6-language-switcher
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

推送 `v1.0.0` 等版本标签，会自动创建附带工具压缩包和 SHA-256 文件的 GitHub Release。若仓库禁止 Actions 写入，需在仓库设置中允许工作流创建 Release，或手动上传构建产物。

```powershell
git tag v1.0.0
git push origin v1.0.0
```

欢迎提交问题和改进；反馈请附游戏版本、目标语言代码、操作步骤和日志，并去除个人路径。不上传游戏 ZIP 或字符表内容。

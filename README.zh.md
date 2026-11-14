# Griffin

[Español](README.md) · [English](README.en.md) · **中文** · [日本語](README.ja.md)

**少女前线2：追放** 私服（国服 PC 客户端 **4.0.5136** 与安卓 **4.0.5137**，表格版本 `1195672`），使用 C#（.NET 8）和 PostgreSQL 编写。
灵感来自 [NTRSimulator](https://github.com/raphaeIl/NTRSimulator)（见[致谢](#致谢)）。

## 相关项目

| 项目 | 用途 |
|---|---|
| [**GRIFFIN-ES-EN**](https://github.com/jhon1466/GRIFFIN-ES-EN) | 语言补丁：将游戏切换为**西班牙语**或**英语**，支持安卓（Shizuku 应用）和 PC。 |
| [**GRIFFIN-TOOLS**](https://github.com/jhon1466/GRIFFIN-TOOLS) | **Griffin GM Tools**：安装、运行并管理服务器的桌面应用（发放人形、武器、物品，管理账号，开启活动……）。 |

> 本项目非营利，仅用于学习与保存目的。与散爆网络及任何发行商无关，且**不包含任何游戏文件**：需要你自己安装客户端。

## 推荐方式：Griffin GM Tools

**现在无需自行编译服务器。** [Griffin GM Tools](https://github.com/jhon1466/GRIFFIN-TOOLS/releases) 会下载本仓库的最新 Release（已编译的服务器 + 补丁），安装 PostgreSQL，启动服务器并自动保持更新，还会把补丁复制到游戏目录。只需：

1. 下载并打开 `Griffin-GM-Tools.exe`。
2. 在 **本地服务器** 中点击 **一键安装**，然后点击 **启动服务器**。
3. 选择游戏目录，点击 **安装补丁**，再点击 **开始游戏**。

游戏运行期间，启动器还会对客户端代码打补丁，修复旧活动的菜单；游戏关闭后会恢复官方文件。

以下内容仅在你想自己编译服务器时需要。

## 环境要求（手动编译）

| 要求 | 说明 |
|---|---|
| 系统 | Windows 10/11（补丁和启动器仅支持 Windows x64） |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | 用于编译和运行服务器 |
| [PostgreSQL](https://www.postgresql.org/download/) 12 或更高 | 位于 `localhost:5432`，用户 `postgres`，密码 `password`（可在 `Griffin/appsettings.json` 的 `ConnectionStrings:Postgres` 修改）。`griffin` 数据库会自动创建 |
| 国服少女前线2：追放客户端 **4.0.5136** | 安装在 `C:\Program Files\GF2Exilium\GF2 Game` |
| Griffin Patcher | `griffin_patcher.dll` + `griffin_launcher.exe`，可在 [Releases](../../releases) 下载（可选：用 [Rust](https://rustup.rs) 自行编译，代码在 `GriffinPatcher/`） |

客户端表格（`Resources/Tables`）会在服务器首次启动时自动准备：若版本相同则从客户端缓存复制（或从 `GRIFFIN_CLIENT_TABLES_DIR` 指定的路径），否则自动下载。

## 启动方法

1. **克隆**仓库：
   ```powershell
   git clone https://github.com/jhon1466/GRIFFIN.git
   cd GRIFFIN
   ```
2. **安装补丁**（仅一次）：从 [Releases](../../releases) 下载 `griffin-patcher-win-x64.zip`，将 `griffin_patcher.dll` 和 `griffin_launcher.exe` 复制到游戏目录（`GF2Exilium\GF2 Game\`）。
3. **启动服务器**：
   ```powershell
   .\scripts\start-server.ps1
   ```
   脚本会以 Release 编译，检查 PostgreSQL 是否响应、80/443/7001 端口是否空闲，然后启动服务器。保持窗口打开；按 `Ctrl+C` 停止。
4. 在游戏目录中**以管理员身份**运行 `griffin_launcher.exe` **启动游戏**。补丁会把 `*.sunborngame.com` 的 HTTP 重定向到 `https://127.0.0.1` 并接受本地证书，无需 mitmproxy。
5. 创建账号/登录，进入大厅后，打开与内置联系人的聊天并输入：
   - `/inventory provision`：表格中的货币、礼物、食材、钥匙和物品。
   - `/inventory addall`：所有人形、武器、模块、皮肤、服装和背景。
   - `/help`：命令列表。

可选环境变量（`GRIFFIN_*`）：`GRIFFIN_CLIENT_TABLES_DIR`、`GRIFFIN_STAMINA=full`（体力常满，仅测试用）、`GRIFFIN_FREE_STAGES=1`（关卡免费，仅测试用）。

## 安卓

服务器同时支持国服**安卓 4.0.5137** 客户端，并可与 PC 同时使用：它会识别连接的客户端并按各自版本响应。

| | PC | 安卓 |
|---|---|---|
| 客户端 | 4.0.5136 | 4.0.5137 |
| 资源（`ResVersion`） | 4.0.5136.14945.29000 | 4.0.5137.14945.29001 |
| 二进制版本 | 4.0.5136.3257 | 4.0.5137.3257 |
| 表格 | 1195672 | 1195672 |

用手机游玩：

1. 手机与服务器电脑必须在**同一网络**（Wi-Fi 或电脑热点）。
2. 在 `Griffin/appsettings.json` 中，把 `GameServer:Host` 设为电脑的**局域网 IP**（手机能访问的地址），而不是 `127.0.0.1`。
3. APK 的 SDK 必须指向 `http://<电脑 IP>`（80 端口）。本仓库**不包含**任何 APK 或修改工具。
4. 在 Windows 防火墙中为局域网开放 80 和 7001 端口。
5. 首次进入游戏会从官方 CDN 下载全部资源（约 **44 GB**），需要联网和足够空间。

若 APK 有新版本，可通过 `GRIFFIN_ANDROID_CLIENT_VERSION`、`GRIFFIN_ANDROID_AB_RESOURCE_VERSION`、`GRIFFIN_ANDROID_BINARY_VERSION` 和 `GRIFFIN_ANDROID_GAME_CLIENT_VERSION` 修改安卓相关值，无需重新编译。

## 状态与路线图

服务器对客户端约 1020 种请求中的约 430 种有自己的处理；其余返回空响应或客户端原生错误，以免界面崩溃。

### 可用
- [x] 登录、本地 SDK（HTTPS 443）、服务器列表和游戏连接（TCP 7001）
- [x] 背包：人形、武器、武器模块、皮肤、服装、部件、背景、物品和货币
- [x] 聊天中的管理命令（`/inventory`、`/help`、`/timewarp`）
- [x] 抽卡（人形、武器和皮肤卡池），保底会保存
- [x] 完整主线：表格中的所有关卡（超过 5000 个）都能开始、游玩和结算，含奖励、星数，以及通过成就解锁章节和往期活动剧情
- [x] 体力随时间恢复，也可购买
- [x] 人形：等级、阶级、品级、天赋树（含关键节点）、心智螺旋、公共天赋钥匙和改造核心（55 级）
- [x] 武器：等级、锁定、突破和分解
- [x] 宿舍和 3D 大厅：服装、服装部件、按等级解锁的姿势、厨房、花园、礼物
- [x] 人形好感：偏好加成的礼物、等级事件（请求和协同作战）及奖励
- [x] 邮件、每日任务、成就、战令、商店和个人资料
- [x] 控制室和远征
- [x] 好友以及与内置联系人的聊天
- [x] 自有补丁（Griffin Patcher），无需代理即可重定向 SDK
- [x] 服务器验证战斗（客户端模拟战斗，服务器接受结果）
- [x] 自有管理面板：[Griffin GM Tools](https://github.com/jhon1466/GRIFFIN-TOOLS)（另有聊天命令）
- [x] 好感：誓约（8 级）以及 5–7 级的协同作战
- [x] 改造核心：插件（芯片背包）及其真实消耗

### 部分可用 / 未在真实客户端验证
- [ ] 练习和模拟模式（许多请求只返回空响应）
- [ ] 季节活动（大多只返回示例数据）

### 尚不可用
- [ ] PvP 和多人战斗（需要战斗服务器）
- [ ] 小队 / 公会
- [ ] 暗区及其他在线探索模式
- [ ] 国际服及其他客户端版本（已支持 PC 4.0.5136 和安卓 4.0.5137）

## 目录结构

| 目录 | 内容 |
|---|---|
| `Griffin/` | 可执行项目（主机、`appsettings.json`） |
| `Griffin.GameServer/` | 游戏处理器、服务和命令 |
| `Griffin.SDKServer/` | HTTPS SDK 服务器 |
| `Griffin.Common/` | TCP 网络、加密、封包分发 |
| `Griffin.Common.Proto/` | 协议和表格的 `.proto` 定义 |
| `Griffin.Common.Table/` | 客户端表格读取器 |
| `Griffin.Database/` | Entity Framework Core + PostgreSQL |
| `Griffin.Command/` | 命令引擎 |
| `Griffin.ProtocolSmoke/` | 协议测试客户端 |
| `GriffinPatcher/` | 补丁代码（Rust）；二进制文件在 Releases |
| `scripts/` | `start-server.ps1` |
| `tools/` | 代码生成器（见下文） |

## 代码生成器

部分服务器代码由 `.proto` 和客户端表格生成。**生成的代码已在仓库中，使用服务器无需运行这些脚本。** 只有修改 `.proto` 或将表格更新到其他客户端版本时才需要（需 [Python 3](https://www.python.org/downloads/)）：

| 脚本 | 生成 | 何时运行 |
|---|---|---|
| `python tools/gen-handlers.py` | `Griffin.Common.Proto/Handlers/PacketHandlerBases.g.cs`（各请求处理器的基类） | 修改 `Griffin.Common.Proto/protos/GF2_deobfuscated.proto` 时 |
| `python tools/gen-achievements.py` | `Griffin.GameServer/Services/AchievementConditions.g.cs`（解锁章节、姿势和天赋树的成就条件） | 客户端表格版本变化时 |
| `python tools/gen-cores.py` | `Griffin.GameServer/Services/RemouldingCores.g.cs`（每个人形的改造核心） | 客户端表格版本变化时 |

后两个脚本读取 `Griffin/bin/Release/net8.0/Resources/Tables` 中的表格（可通过参数指定其他目录）。`tools/tabledeep.py` 是它们使用的表格读取器，也可用来手动查看表格。

## 常见问题

- **无法编译 / 缺少包**：运行 `dotnet restore Griffin.sln` 后重新编译。
- **进入时提示“客户端数据异常”**：PostgreSQL 无响应或账号数据加载失败；请检查服务是否运行、连接字符串是否正确。
- **安卓：“当前客户端版本已过时”或“解析错误提示”**：APK 不是 4.0.5137，或服务器未识别为安卓；请把 `GRIFFIN_ANDROID_*` 变量设为你的 APK 版本。
- **安卓：“下载时发生未知错误”**：手机无法联网从官方 CDN 下载资源。
- **游戏无法连接**：在服务器已启动的情况下以管理员身份运行 `griffin_launcher.exe`，并确认 80、443、7001 端口没有被其他程序占用。

## 致谢

- **[NTRSimulator](https://github.com/raphaeIl/NTRSimulator)**（raphaeIl）：Griffin 受此项目启发，并基于其部分代码。

## 许可证

[MIT](LICENSE)。

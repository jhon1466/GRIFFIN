# Griffin

[Español](README.md) · **English** · [中文](README.zh.md) · [日本語](README.ja.md)

Private server for **Girls' Frontline 2: Exilium** (CN PC client **4.0.5136** and Android **4.0.5137**, tables `1195672`), written in C# (.NET 8) with PostgreSQL.
Inspired by [NTRSimulator](https://github.com/raphaeIl/NTRSimulator) (see [Credits](#credits)).

## Related projects

| Project | What it is for |
|---|---|
| [**GRIFFIN-ES-EN**](https://github.com/jhon1466/GRIFFIN-ES-EN) | Language patch: puts the game in **Spanish** or **English**, on Android (Shizuku app) and PC. |
| [**GRIFFIN-TOOLS**](https://github.com/jhon1466/GRIFFIN-TOOLS) | **Griffin GM Tools**: desktop app that installs, runs and administers the server (grant dolls, weapons, items, manage accounts, open events…). |

> Non-profit project made for educational and preservation purposes. It is not affiliated with SunBorn or any publisher of the game and **includes no game files**: you need your own installed client.

## Recommended way: Griffin GM Tools

**You no longer need to build the server.** [Griffin GM Tools](https://github.com/jhon1466/GRIFFIN-TOOLS/releases) downloads the latest release of this repository (prebuilt server + patch), installs PostgreSQL, starts the server, keeps it up to date and copies the patch into the game folder. You only have to:

1. Download and open `Griffin-GM-Tools.exe`.
2. In **Local server**, press **Install everything** and then **Start server**.
3. Choose the game folder, press **Install patch** and then **Play**.

While the game is open, the launcher also applies a patch to the client code that fixes the menus of old events; when the game closes it puts the official file back.

The following sections are only needed if you want to build the server yourself.

## Requirements (manual build)

| Requirement | Details |
|---|---|
| System | Windows 10/11 (the patch and the launcher are Windows x64 only) |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | to build and run the server |
| [PostgreSQL](https://www.postgresql.org/download/) 12 or newer | on `localhost:5432`, user `postgres`, password `password` (changed in `Griffin/appsettings.json`, key `ConnectionStrings:Postgres`). The `griffin` database is created automatically |
| CN GF2 Exilium client **4.0.5136** | installed in `C:\Program Files\GF2Exilium\GF2 Game` |
| Griffin Patcher | `griffin_patcher.dll` + `griffin_launcher.exe`, from [Releases](../../releases) (optional: [Rust](https://rustup.rs) to build it yourself, code in `GriffinPatcher/`) |

The client tables (`Resources/Tables`) are prepared automatically the first time the server starts: they are copied from your client cache if it is the same version (or from the path in `GRIFFIN_CLIENT_TABLES_DIR`), otherwise they are downloaded.

## How to start

1. **Clone** the repository:
   ```powershell
   git clone https://github.com/jhon1466/GRIFFIN.git
   cd GRIFFIN
   ```
2. **Install the patch** (once): download `griffin-patcher-win-x64.zip` from [Releases](../../releases) and copy `griffin_patcher.dll` and `griffin_launcher.exe` into the game folder (`GF2Exilium\GF2 Game\`).
3. **Start the server**:
   ```powershell
   .\scripts\start-server.ps1
   ```
   It builds in Release, checks that PostgreSQL answers and that ports 80, 443 and 7001 are free, and starts the server. Leave it open; stop it with `Ctrl+C`.
4. **Start the game** with `griffin_launcher.exe` (**as administrator**) from the game folder. The patch redirects the HTTP of `*.sunborngame.com` to `https://127.0.0.1` and accepts the local certificate, so no mitmproxy is needed.
5. Create your account / log in and, in the lobby, open the chat with the built-in contact and type:
   - `/inventory provision`: currencies, gifts, ingredients, keys and items from the tables.
   - `/inventory addall`: every character, weapon, module, skin, outfit and background.
   - `/help`: list of commands.

Optional environment variables (`GRIFFIN_*`): `GRIFFIN_CLIENT_TABLES_DIR`, `GRIFFIN_STAMINA=full` (always full stamina, testing only), `GRIFFIN_FREE_STAGES=1` (free stages, testing only).

## Android

The server also supports the CN **Android 4.0.5137** client, at the same time as the PC one: it detects which client is talking and answers each with its own version.

| | PC | Android |
|---|---|---|
| Client | 4.0.5136 | 4.0.5137 |
| Resources (`ResVersion`) | 4.0.5136.14945.29000 | 4.0.5137.14945.29001 |
| Binary version | 4.0.5136.3257 | 4.0.5137.3257 |
| Tables | 1195672 | 1195672 |

To play from a phone:

1. The phone and the server PC must be on the **same network** (Wi-Fi or the PC's hotspot).
2. In `Griffin/appsettings.json`, set `GameServer:Host` to the PC's **local IP** (the one the phone sees), not `127.0.0.1`.
3. The APK must point its SDK to `http://<PC IP>` (port 80). This repository **does not include** any APK or tool to modify it.
4. Open ports 80 and 7001 in the Windows firewall for the local network.
5. The first time, the game downloads all its resources (about **44 GB**) from the official CDN, so it needs internet and free space.

If a new APK version comes out, the Android values are changed without rebuilding with `GRIFFIN_ANDROID_CLIENT_VERSION`, `GRIFFIN_ANDROID_AB_RESOURCE_VERSION`, `GRIFFIN_ANDROID_BINARY_VERSION` and `GRIFFIN_ANDROID_GAME_CLIENT_VERSION`.

## Status and roadmap

The server answers about 430 of the client's ~1020 request messages with its own behaviour; the rest return empty replies or native client errors so screens do not break.

### Works
- [x] Login, local SDK (HTTPS 443), server list and game connection (TCP 7001)
- [x] Inventory: characters, weapons, weapon modules, skins, outfits, pieces, backgrounds, items and currencies
- [x] Admin commands from the chat (`/inventory`, `/help`, `/timewarp`)
- [x] Gacha (character, weapon and skin banners) with saved pity
- [x] Full campaign: every stage in the tables (over 5000) can be started, played and resolved, with rewards, stars and unlocking of chapters and of past event stories through achievements
- [x] Stamina that regenerates over time and can be bought
- [x] Characters: level, class, grade, talent tree (including the key node), Mind Spiral, public talent keys and remoulding core (at level 55)
- [x] Weapons: level, lock, breakthrough and dismantling
- [x] Dorm and 3D lobby: outfits, clothing pieces, poses unlocked by level, kitchen, garden, gifts
- [x] Character affection: gifts with preference bonus, level events (request and joint battle) and rewards
- [x] Mail, daily missions, achievements, battle pass, shop and profile
- [x] Control room and expeditions
- [x] Friends and chat with a built-in contact
- [x] Own patch (Griffin Patcher) that redirects the SDK without a proxy
- [x] Server-validated combat (the client simulates the battle and the server accepts the result)
- [x] Own admin panel: [Griffin GM Tools](https://github.com/jhon1466/GRIFFIN-TOOLS) (besides the chat commands)
- [x] Affection: oath (level 8) and joint battle of levels 5–7
- [x] Remoulding core: its plugins (chip inventory) and its real cost

### Partial / not verified on the real client
- [ ] Practice and simulation modes (many requests only return an empty reply)
- [ ] Seasonal events and activities (most only answer with sample data)

### Does not work yet
- [ ] PvP and multiplayer combat (they need a battle server)
- [ ] Band / Guild
- [ ] Dark zone and other online exploration modes
- [ ] Support for the global server and other client versions (PC 4.0.5136 and Android 4.0.5137 already work)

## Structure

| Folder | Contents |
|---|---|
| `Griffin/` | executable project (host, `appsettings.json`) |
| `Griffin.GameServer/` | game handlers, services and commands |
| `Griffin.SDKServer/` | HTTPS SDK server |
| `Griffin.Common/` | TCP networking, encryption, packet dispatch |
| `Griffin.Common.Proto/` | `.proto` definitions of the protocol and tables |
| `Griffin.Common.Table/` | client table reader |
| `Griffin.Database/` | Entity Framework Core + PostgreSQL |
| `Griffin.Command/` | command engine |
| `Griffin.ProtocolSmoke/` | protocol test client |
| `GriffinPatcher/` | patch code (Rust); the binaries are in Releases |
| `scripts/` | `start-server.ps1` |
| `tools/` | code generators (see below) |

## Code generators

Part of the server code is generated from the `.proto` files and the client tables. **The generated code is already in the repository, so nothing here has to be run to use the server.** It is only needed (with [Python 3](https://www.python.org/downloads/)) if you change the `.proto` files or update the tables to another client version:

| Script | Generates | When to run it |
|---|---|---|
| `python tools/gen-handlers.py` | `Griffin.Common.Proto/Handlers/PacketHandlerBases.g.cs` (handler base classes of every request) | when `Griffin.Common.Proto/protos/GF2_deobfuscated.proto` changes |
| `python tools/gen-achievements.py` | `Griffin.GameServer/Services/AchievementConditions.g.cs` (achievement conditions that unlock chapters, poses and talent trees) | when the client table version changes |
| `python tools/gen-cores.py` | `Griffin.GameServer/Services/RemouldingCores.g.cs` (remoulding core of each character) | when the client table version changes |

The last two read the tables from `Griffin/bin/Release/net8.0/Resources/Tables` (another folder can be given as an argument). `tools/tabledeep.py` is the table reader they use; it can also inspect a table by hand.

## Common problems

- **Does not build / missing packages**: run `dotnet restore Griffin.sln` and build again.
- **"abnormal client data" when entering**: PostgreSQL does not answer or the account data does not load; check that the service is running and the connection string is correct.
- **Android: "当前客户端版本已过时" (outdated client) or "解析错误提示" (parse error)**: the APK is not 4.0.5137, or the server does not recognize it as Android; set the `GRIFFIN_ANDROID_*` variables to your APK version.
- **Android: "下载时发生未知错误" (download error)**: the phone has no internet access to download the resources from the official CDN.
- **The game does not connect**: open `griffin_launcher.exe` as administrator with the server already running and check that nothing else uses ports 80, 443 and 7001.

## Credits

- **[NTRSimulator](https://github.com/raphaeIl/NTRSimulator)** (raphaeIl): Griffin is inspired by this project and builds on part of its base.

## License

[MIT](LICENSE).

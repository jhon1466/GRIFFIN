# Griffin

[Español](README.md) · [English](README.en.md) · [中文](README.zh.md) · **日本語**

**ドールズフロントライン2：エクシリウム** のプライベートサーバーです（中国版 PC クライアント **4.0.5136** と Android **4.0.5137**、テーブル `1195672`）。C#（.NET 8）と PostgreSQL で作られています。
[NTRSimulator](https://github.com/raphaeIl/NTRSimulator) を参考にしています（[クレジット](#クレジット)を参照）。

## 関連プロジェクト

| プロジェクト | 用途 |
|---|---|
| [**GRIFFIN-ES-EN**](https://github.com/jhon1466/GRIFFIN-ES-EN) | 言語パッチ：ゲームを**スペイン語**または**英語**にします。Android（Shizuku アプリ）と PC に対応。 |
| [**GRIFFIN-TOOLS**](https://github.com/jhon1466/GRIFFIN-TOOLS) | **Griffin GM Tools**：サーバーのインストール・起動・管理を行うデスクトップアプリ（人形・武器・アイテムの付与、アカウント管理、イベント開催など）。 |

> 非営利・教育および保存目的のプロジェクトです。SunBorn やゲームの運営会社とは無関係で、**ゲームのファイルは含まれていません**。ご自身でクライアントをインストールする必要があります。

## おすすめの方法：Griffin GM Tools

**サーバーを自分でビルドする必要はなくなりました。** [Griffin GM Tools](https://github.com/jhon1466/GRIFFIN-TOOLS/releases) がこのリポジトリの最新 Release（ビルド済みサーバー＋パッチ）をダウンロードし、PostgreSQL をインストールし、サーバーを起動して自動で最新に保ち、パッチをゲームフォルダにコピーします。手順は次のとおりです：

1. `Griffin-GM-Tools.exe` をダウンロードして開きます。
2. **ローカルサーバー** で **すべてインストール** を押し、続けて **サーバー起動** を押します。
3. ゲームフォルダを選び、**パッチをインストール** を押してから **プレイ** を押します。

ゲームの起動中、ランチャーはクライアントのコードにもパッチを当て、過去イベントのメニューを修正します。ゲームを閉じると公式のファイルに戻します。

以下の内容は、サーバーを自分でビルドしたい場合にのみ必要です。

## 必要なもの（手動ビルド）

| 項目 | 詳細 |
|---|---|
| OS | Windows 10/11（パッチとランチャーは Windows x64 専用） |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | サーバーのビルドと実行に使用 |
| [PostgreSQL](https://www.postgresql.org/download/) 12 以上 | `localhost:5432`、ユーザー `postgres`、パスワード `password`（`Griffin/appsettings.json` の `ConnectionStrings:Postgres` で変更可能）。`griffin` データベースは自動で作成されます |
| 中国版 GF2 エクシリウム クライアント **4.0.5136** | `C:\Program Files\GF2Exilium\GF2 Game` にインストール |
| Griffin Patcher | `griffin_patcher.dll` + `griffin_launcher.exe`。[Releases](../../releases) からダウンロード（任意：[Rust](https://rustup.rs) で自分でビルド可能、コードは `GriffinPatcher/`） |

クライアントのテーブル（`Resources/Tables`）はサーバーの初回起動時に自動で準備されます。同じバージョンならクライアントのキャッシュ（または `GRIFFIN_CLIENT_TABLES_DIR` で指定したパス）からコピーし、そうでなければダウンロードします。

## 起動方法

1. リポジトリを**クローン**します：
   ```powershell
   git clone https://github.com/jhon1466/GRIFFIN.git
   cd GRIFFIN
   ```
2. **パッチをインストール**します（初回のみ）：[Releases](../../releases) から `griffin-patcher-win-x64.zip` をダウンロードし、`griffin_patcher.dll` と `griffin_launcher.exe` をゲームフォルダ（`GF2Exilium\GF2 Game\`）にコピーします。
3. **サーバーを起動**します：
   ```powershell
   .\scripts\start-server.ps1
   ```
   Release でビルドし、PostgreSQL の応答とポート 80・443・7001 の空きを確認してからサーバーを起動します。開いたままにしておき、`Ctrl+C` で停止します。
4. ゲームフォルダから `griffin_launcher.exe` を**管理者として**実行して**ゲームを起動**します。パッチが `*.sunborngame.com` の HTTP を `https://127.0.0.1` へ転送し、ローカル証明書を受け入れるため、mitmproxy は不要です。
5. アカウントを作成／ログインし、ロビーで内蔵の連絡先とのチャットを開いて入力します：
   - `/inventory provision`：テーブルにある通貨、ギフト、食材、鍵、アイテム。
   - `/inventory addall`：すべての人形、武器、モジュール、スキン、衣装、背景。
   - `/help`：コマンド一覧。

任意の環境変数（`GRIFFIN_*`）：`GRIFFIN_CLIENT_TABLES_DIR`、`GRIFFIN_STAMINA=full`（スタミナ常に満タン、テスト用）、`GRIFFIN_FREE_STAGES=1`（ステージ無料、テスト用）。

## Android

サーバーは中国版 **Android 4.0.5137** クライアントにも、PC 版と同時に対応しています。接続してきたクライアントを判別し、それぞれのバージョンで応答します。

| | PC | Android |
|---|---|---|
| クライアント | 4.0.5136 | 4.0.5137 |
| リソース（`ResVersion`） | 4.0.5136.14945.29000 | 4.0.5137.14945.29001 |
| バイナリバージョン | 4.0.5136.3257 | 4.0.5137.3257 |
| テーブル | 1195672 | 1195672 |

スマホで遊ぶには：

1. スマホとサーバーの PC が**同じネットワーク**（Wi-Fi または PC のホットスポット）にある必要があります。
2. `Griffin/appsettings.json` の `GameServer:Host` に、`127.0.0.1` ではなく PC の**ローカル IP**（スマホから見えるアドレス）を設定します。
3. APK の SDK が `http://<PC の IP>`（ポート 80）を向いている必要があります。このリポジトリには APK やその改造ツールは**含まれていません**。
4. Windows ファイアウォールでローカルネットワーク向けにポート 80 と 7001 を開放します。
5. 初回はゲームが公式 CDN から全リソース（約 **44 GB**）をダウンロードするため、インターネット接続と空き容量が必要です。

APK の新バージョンが出た場合は、`GRIFFIN_ANDROID_CLIENT_VERSION`、`GRIFFIN_ANDROID_AB_RESOURCE_VERSION`、`GRIFFIN_ANDROID_BINARY_VERSION`、`GRIFFIN_ANDROID_GAME_CLIENT_VERSION` で再ビルドせずに Android の値を変更できます。

## 現状とロードマップ

サーバーはクライアントの約 1020 種類のリクエストのうち約 430 種類に独自の処理で応答します。残りは画面が壊れないよう、空の応答またはクライアント標準のエラーを返します。

### 動作するもの
- [x] ログイン、ローカル SDK（HTTPS 443）、サーバー一覧、ゲーム接続（TCP 7001）
- [x] 所持品：人形、武器、武器モジュール、スキン、衣装、パーツ、背景、アイテム、通貨
- [x] チャットからの管理コマンド（`/inventory`、`/help`、`/timewarp`）
- [x] ガチャ（人形・武器・スキンのバナー）、天井カウントを保存
- [x] メインストーリー全体：テーブルの全ステージ（5000 以上）を開始・プレイ・精算でき、報酬、星、実績による章や過去イベントストーリーの解放に対応
- [x] 時間で回復し、購入もできるスタミナ
- [x] 人形：レベル、クラス、グレード、才能ツリー（キーノードを含む）、マインドスパイラル、共通の才能キー、改造コア（レベル 55）
- [x] 武器：レベル、ロック、突破、分解
- [x] 宿舎と 3D ロビー：衣装、衣装パーツ、レベルで解放されるポーズ、キッチン、庭、ギフト
- [x] 人形の好感度：好みによるボーナス付きのギフト、レベルイベント（依頼と共同作戦）と報酬
- [x] メール、デイリーミッション、実績、バトルパス、ショップ、プロフィール
- [x] コントロールルームと遠征
- [x] フレンドと内蔵の連絡先とのチャット
- [x] プロキシなしで SDK を転送する独自パッチ（Griffin Patcher）
- [x] サーバーで検証される戦闘（クライアントが戦闘をシミュレートし、サーバーが結果を受け入れる）
- [x] 独自の管理パネル：[Griffin GM Tools](https://github.com/jhon1466/GRIFFIN-TOOLS)（チャットのコマンドも利用可能）
- [x] 好感度：誓約（レベル 8）とレベル 5–7 の共同作戦
- [x] 改造コア：プラグイン（チップ所持品）と実際のコスト

### 一部のみ／実機クライアントで未検証
- [ ] 練習・シミュレーションモード（多くのリクエストは空の応答のみ）
- [ ] 季節イベントとアクティビティ（大半はサンプルデータのみ）

### まだ動作しないもの
- [ ] PvP とマルチプレイ戦闘（バトルサーバーが必要）
- [ ] 小隊／ギルド
- [ ] ダークゾーンなどのオンライン探索モード
- [ ] グローバルサーバーと他のクライアントバージョン（PC 4.0.5136 と Android 4.0.5137 は対応済み）

## 構成

| フォルダ | 内容 |
|---|---|
| `Griffin/` | 実行プロジェクト（ホスト、`appsettings.json`） |
| `Griffin.GameServer/` | ゲームのハンドラー、サービス、コマンド |
| `Griffin.SDKServer/` | HTTPS SDK サーバー |
| `Griffin.Common/` | TCP 通信、暗号化、パケット振り分け |
| `Griffin.Common.Proto/` | プロトコルとテーブルの `.proto` 定義 |
| `Griffin.Common.Table/` | クライアントテーブルの読み込み |
| `Griffin.Database/` | Entity Framework Core + PostgreSQL |
| `Griffin.Command/` | コマンドエンジン |
| `Griffin.ProtocolSmoke/` | プロトコルのテストクライアント |
| `GriffinPatcher/` | パッチのコード（Rust）。バイナリは Releases にあります |
| `scripts/` | `start-server.ps1` |
| `tools/` | コード生成ツール（下記参照） |

## コード生成ツール

サーバーのコードの一部は `.proto` とクライアントのテーブルから生成されます。**生成済みのコードはリポジトリに含まれているため、サーバーを使うだけならこれらを実行する必要はありません。** `.proto` を変更したり、テーブルを別のクライアントバージョンに更新したりする場合のみ必要です（[Python 3](https://www.python.org/downloads/) が必要）：

| スクリプト | 生成するもの | 実行するタイミング |
|---|---|---|
| `python tools/gen-handlers.py` | `Griffin.Common.Proto/Handlers/PacketHandlerBases.g.cs`（各リクエストのハンドラー基底クラス） | `Griffin.Common.Proto/protos/GF2_deobfuscated.proto` を変更したとき |
| `python tools/gen-achievements.py` | `Griffin.GameServer/Services/AchievementConditions.g.cs`（章・ポーズ・才能ツリーを解放する実績の条件） | クライアントのテーブルのバージョンが変わったとき |
| `python tools/gen-cores.py` | `Griffin.GameServer/Services/RemouldingCores.g.cs`（各人形の改造コア） | クライアントのテーブルのバージョンが変わったとき |

後の 2 つは `Griffin/bin/Release/net8.0/Resources/Tables` のテーブルを読み込みます（引数で別のフォルダを指定可能）。`tools/tabledeep.py` はそれらが使うテーブル読み込みツールで、テーブルを手動で確認するのにも使えます。

## よくある問題

- **ビルドできない／パッケージが足りない**：`dotnet restore Griffin.sln` を実行してから再ビルドしてください。
- **入る時に「客户端数据异常」（クライアントデータ異常）**：PostgreSQL が応答しないか、アカウントデータを読み込めていません。サービスが動いているか、接続文字列が正しいか確認してください。
- **Android：「当前客户端版本已过时」（クライアントが古い）または「解析错误提示」（解析エラー）**：APK が 4.0.5137 ではないか、サーバーが Android と認識していません。`GRIFFIN_ANDROID_*` 変数をお使いの APK のバージョンに合わせてください。
- **Android：「下载时发生未知错误」（ダウンロードエラー）**：スマホが公式 CDN からリソースをダウンロードするためのインターネット接続がありません。
- **ゲームが接続できない**：サーバーを起動した状態で `griffin_launcher.exe` を管理者として実行し、ポート 80・443・7001 を他のプログラムが使っていないか確認してください。

## クレジット

- **[NTRSimulator](https://github.com/raphaeIl/NTRSimulator)**（raphaeIl）：Griffin はこのプロジェクトを参考にし、その一部を基にしています。

## ライセンス

[MIT](LICENSE)。

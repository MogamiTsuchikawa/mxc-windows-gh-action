# MXC GitHub Actions Runner

[![Build and test](https://github.com/MogamiTsuchikawa/mxc-windows-gh-action/actions/workflows/ci.yml/badge.svg)](https://github.com/MogamiTsuchikawa/mxc-windows-gh-action/actions/workflows/ci.yml)

C# / .NET 10 で実装した、Windows の MXC ProcessContainer 内で GitHub Actions のセルフホストランナーを動かす CLI です。1 回の起動につきランナーを一時登録し、1 ジョブ終了後に停止して作業ディレクトリを削除します。

配布 ZIP を利用する場合は、.NET 10 ランタイムがある Windows で、ユーザープロファイル外のツール用ディレクトリ（例: `D:\tools\mxc-gh-runner`）へ ZIP 全体を展開し、展開先で `./mxc-gh-runner.exe init` を実行してください。以下の使用例の `./artifacts/publish/mxc-gh-runner.exe` は `./mxc-gh-runner.exe` に読み替えます。ZIP には対応する MinGit BusyBox、操作手順、`examples/`・`docs/` を同梱します。

## ビルド

開発には .NET 10 SDK、実行には MXC ProcessContainer が利用できる Windows と .NET 10 ランタイムが必要です。対応状態は `doctor`、実際の .NET 起動と入出力は `smoke` で確認します。現在の対象は `github.com` のリポジトリ単位ランナーです。

```powershell
dotnet restore Mxc.GitHubRunner.slnx --locked-mode
dotnet test Mxc.GitHubRunner.slnx -c Release --no-restore
dotnet publish src/Mxc.GitHubRunner -c Release -o artifacts/publish
```

配布する場合は `artifacts/publish` ディレクトリ全体をコピーしてください。実行ファイル名は `mxc-gh-runner.exe` です。ホストに Git や PowerShell 7 をインストールすると `init` が標準のインストール先を検出します。その他のビルドツールは設定の `toolPaths` に追加します。

`actions/checkout` には公式 MinGit BusyBox を使用します。配布 ZIP の `init` は同梱版を優先的に選択します。ソースからビルドした場合や別の Git を指定する場合は、[Git 互換性と MinGit BusyBox の導入手順](docs/git-compatibility.md) を確認してください。通常版 Git の MSYS シェルヘルパーは現在の MXC で起動できません。

## 使い方

```powershell
./artifacts/publish/mxc-gh-runner.exe init
./artifacts/publish/mxc-gh-runner.exe doctor
./artifacts/publish/mxc-gh-runner.exe smoke
```

生成された `mxc-runner.json` の `repository` を対象の `https://github.com/OWNER/REPO` に書き換えます。既存設定は `init` で上書きされません。

作業データの既定の置き場所は設定ファイルと同じ場所の `.mxc-runner` です。CLI の展開先から `init` した場合など、その場所が読み取り専用の許可範囲と重なるときは `%LOCALAPPDATA%\MxcGitHubRunner` を使います。実行ファイル・Windows・.NET・指定ツールのディレクトリと作業データを分けることで、作業領域へ意図した書き込み許可を設定できます。

```powershell
./artifacts/publish/mxc-gh-runner.exe plan
./artifacts/publish/mxc-gh-runner.exe prepare
```

`plan` は設定を検証して絶対パスへ解決した結果を表示します。`prepare` は GitHub 公式ランナーをダウンロードし、リリースの SHA-256 と照合して展開します。GitHub へのランナー登録は行いません。

認証情報は環境変数で渡します。GitHub の対象リポジトリの **Settings → Actions → Runners → New self-hosted runner** に表示される登録トークンを利用できます。PowerShell 7 では、次のように画面へ表示せず入力できます。

```powershell
$env:MXC_RUNNER_REGISTRATION_TOKEN = Read-Host 'Runner registration token' -MaskInput
try {
    ./artifacts/publish/mxc-gh-runner.exe run
} finally {
    Remove-Item Env:MXC_RUNNER_REGISTRATION_TOKEN -ErrorAction SilentlyContinue
}
```

または、対象リポジトリのランナー管理権限を持つアクセストークンを `MXC_GITHUB_TOKEN` に設定します。この場合はホスト側で登録トークンを発行し、終了時に残った登録を API で削除します。両方を設定した場合、登録には `MXC_RUNNER_REGISTRATION_TOKEN` を使い、残った登録の削除には `MXC_GITHUB_TOKEN` を使います。

`run` が待機状態になったら、対象リポジトリで次のラベルを指定したワークフローを実行します。

```yaml
jobs:
  build:
    runs-on: [self-hosted, Windows, X64, mxc]
    steps:
      - name: Confirm runner
        shell: powershell
        run: |
          Write-Output "Runner: $env:RUNNER_NAME"
          dotnet --version
```

ARM64 の場合は `X64` を `ARM64` に変更します。手動実行できる基本例は [examples/mxc-workflow.yml](examples/mxc-workflow.yml)、JavaScript の checkout アクションと C# ビルドを含む検証用ワークフローは [examples/e2e-validation.yml](examples/e2e-validation.yml) にあります。後者は Git と PowerShell 7 が必要です。対象ファイルをリポジトリの `.github/workflows/` に配置してください。ランナーを再度起動すると次の 1 ジョブを受け付けます。

## コマンド

| コマンド | 動作 |
| --- | --- |
| `init [--config PATH]` | 設定ファイルの新規作成 |
| `doctor` | Windows と MXC の対応状態を確認 |
| `plan [--config PATH]` | 設定検証と解決済み設定の表示 |
| `smoke [--config PATH]` | 認証不要の MXC 起動、標準入力、環境分離、ファイル入出力検証 |
| `prepare [--config PATH]` | ランナー取得と SHA-256 検証 |
| `run [--config PATH]` | 一時登録、1 ジョブ待機・実行、後処理 |

設定ファイルの既定値は現在のディレクトリの `mxc-runner.json` です。相対パスは設定ファイルのあるディレクトリを基準に解決します。終了コードは通常 `0`、エラー `1`、待機・実行全体の時間切れ `124`、Ctrl+C `130` です。`run` はそれ以外の場合にランナープロセスの終了コードを返します。**ワークフローの成功・失敗は GitHub Actions 側で確認してください。**

## 設定

[examples/mxc-runner.example.json](examples/mxc-runner.example.json) が設定例です。未知・重複プロパティはエラーになります。トークンを設定ファイルへ書き込む項目はありません。

| 項目 | 既定値・制約 |
| --- | --- |
| `repository` | HTTPS の GitHub リポジトリ URL。`run` 前に `OWNER/REPO` を置換 |
| `runnerVersion` | `latest` または `2.338.0` のような 3 要素のバージョン |
| `architecture` | `x64` / `arm64`。実行時はホストと一致が必要 |
| `stateDirectory` | 通常 `.mxc-runner`。`init` は必要に応じ `%LOCALAPPDATA%\MxcGitHubRunner` を選択。ルート、読み取り専用ツール領域と重なるパスは不可 |
| `namePrefix` | `mxc`。英数字開始、英数字・`.`・`_`・`-`、1～40 文字 |
| `labels` | `["mxc"]`。最大 20 個、各 1～64 文字、文字種は `namePrefix` と同じ |
| `toolPaths` | `[]`。存在するツールディレクトリ。読み取り許可と PATH に使用 |
| `timeoutMinutes` | `120`。1～1440 分。登録・ジョブ待機・実行する MXC プロセスの上限 |
| `keepWorkDirectory` | `false`。`true` では終了後の調査用に作業ディレクトリを保持 |

状態ディレクトリには、公式ランナーのキャッシュ `packages/`、実行ごとの `runs/<GUID>/`、保持される標準出力・標準エラーログ `logs/` が作られます。`keepWorkDirectory` を有効にした作業領域にはジョブのデータやランナー資格情報が残ることがあります。

## 実行境界と制限

- `Microsoft.Mxc.Sdk` 1.0.0 の ProcessContainer を使用します。ホスト側でパッケージ検証と GitHub API 操作を行い、登録処理とジョブ実行は MXC 内で行います。
- ホストの環境変数を一括継承せず、実行ごとにプロファイルと一時ディレクトリを作成します。アクセストークンを MXC の環境変数や起動引数へ渡しません。登録トークンは標準入力のパイプで渡します。
- 作業領域を読み書き可能にし、Windows・.NET・CLI 本体・指定ツールを読み取り可能にします。ランナーのパス検証用に作業領域の親ディレクトリからドライブのルートまでの名前列挙を許可しますが、その許可だけでは兄弟ディレクトリ内のファイル内容を読み書きできません。Git の `cmd` を指定した場合は Git のインストールツリーも読み取り対象になります。
- `stateDirectory` は読み取り専用の CLI・Windows・.NET・指定ツールの配下にも、それらを含む親ディレクトリにも置けません。`plan` で重複を検出し、`prepare`・`run` はダウンロードや登録前に拒否します。
- 実行領域に空の `global.json` と `Directory.Build.props`・`Directory.Build.targets`・`Directory.Packages.props` を作成し、SDK/MSBuild が上位のホストリポジトリ設定を探索して取り込むのを止めます。チェックアウトしたリポジトリ内の設定は通常どおり利用できます。
- ジョブの成果物は削除前にワークフロー内でアップロードしてください。CLI のログは標準出力・標準エラーの転送ログです。
- ネットワークは外向き通信を許可し、受信とホストループバックを拒否します。通信先ドメインの許可リスト機能はありません。
- .NET ランタイムを起動するため、MXC の UI 無効化スイッチは使用しません。クリップボード許可は `None`、入力注入は無効に設定します。GUI を完全に無効化した実行方式ではありません。
- 登録トークンだけを使う場合、ジョブ受信前のタイムアウト・中断後にオフラインの登録が残ることがあります。表示されたランナー名をリポジトリの Actions 設定で削除してください。
- GitHub Enterprise Server、組織単位ランナー、常駐サービス、複数ジョブの自動スケール、Linux コンテナジョブは初期版の対象外です。Windows のビルド・実行ツールはホストへ別途導入してください。
- `shell: bash` や MSYS/Cygwin に依存するツールは現在の MXC の名前付きオブジェクト制限の影響を受けます。PowerShell 系のシェルと、Git ヘルパーには MinGit BusyBox を使用します。

詳細は [docs/architecture.md](docs/architecture.md) を参照してください。

## 開発時の検証

```powershell
dotnet test Mxc.GitHubRunner.slnx -c Release
```

自動テストでは設定、GitHub API・ダウンロード検証、コマンド引数、環境変数、削除対象の制限、MXC の入出力・キャンセル処理を検証します。CI は Windows 上でビルドと自動テストを行います。実際の MXC 起動や GitHub ジョブ実行は、対応ホスト上の `smoke` と対象リポジトリで別途確認します。

確認した環境・結果と再現手順は [docs/verification.md](docs/verification.md) に記録します。

参考: [GitHub セルフホストランナー](https://docs.github.com/en/actions/hosting-your-own-runners/managing-self-hosted-runners/about-self-hosted-runners)、[actions/runner](https://github.com/actions/runner)、[actions/setup-dotnet](https://github.com/actions/setup-dotnet)。

## ライセンス

このプロジェクト独自のソースコードとドキュメントは [MIT License](LICENSE)（Copyright (c) 2026 MogamiTsuchikawa）で公開します。

同梱・利用する第三者ソフトウェアには、それぞれの元のライセンスが適用されます。Git / MinGit BusyBox の GPLv2、Microsoft.Mxc.Sdk の Microsoft 名義のライセンス、.NET の配布元の利用条件や通知文書を、このプロジェクトの MIT License に置き換えるものではありません。詳細と元の通知文書の場所は [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) を参照してください。

# Get Started — 日本語

[English](GET_STARTED.md) | [プロジェクト概要](README.ja.md)

Windows 上で CLI を準備し、MXC 内の一時ランナーに GitHub Actions の最初のジョブを割り当てます。以下の例は Windows x64 向けです。

## 1. 必要なものを用意する

- MXC ProcessContainer が利用できる Windows ホスト。Windows の版名だけで判断せず、後述の `doctor` と `smoke` で確認します。
- CLI の実行には .NET 10 ランタイム。このガイドの C# ジョブやソースからのビルドには .NET 10 SDK が必要です。ソースの `global.json` は SDK `10.0.200` と `latestFeature` を指定しています。
- ランナーを追加できる `github.com` のリポジトリと、その設定を操作できる権限。
- ホストから GitHub、公式ランナーの配布先、使用するパッケージ配布先への通信。

ホストの操作例と最初のワークフローは Windows PowerShell で実行できます。`shell: pwsh` を使う場合だけ、PowerShell 7 を別途インストールしてください。その他のビルドツールもホスト側に用意します。

## 2. CLI を入手する

### Actions アーティファクトを使う

1. GitHub にサインインし、[Build and test](https://github.com/MogamiTsuchikawa/mxc-windows-gh-action/actions/workflows/ci.yml) を開きます。
2. `main` ブランチの成功した実行を選び、**Artifacts** の **mxc-gh-runner-windows** をダウンロードします。
3. ZIP 全体を、ユーザープロファイル外のツール用ディレクトリへ展開します。以下では `D:\tools\mxc-gh-runner` を例にします。実際に存在するドライブに合わせて変更してください。

現在は Actions アーティファクトで配布しており、GitHub Releases でのリリースはまだありません。アーティファクトのダウンロードには GitHub へのサインインが必要です。

実行ファイルだけを取り出さず、DLL、ネイティブライブラリ、`tools/MinGitBusyBox/`、ドキュメントを含むディレクトリ全体を保持します。配布物には .NET ランタイムと SDK は含まれません。

```powershell
Set-Location 'D:\tools\mxc-gh-runner'
.\mxc-gh-runner.exe --help
```

配布物の `init` は、同梱 MinGit BusyBox を優先して選択します。「3. 設定を作成する」へ進んでください。

### ソースからビルドする

.NET 10 SDK とホスト側の Git を用意し、ユーザープロファイル外で取得・ビルドします。ソース ZIP を使う場合も、[公開リポジトリ](https://github.com/MogamiTsuchikawa/mxc-windows-gh-action) から取得できます。

```powershell
git clone https://github.com/MogamiTsuchikawa/mxc-windows-gh-action.git D:\src\mxc-windows-gh-action
Set-Location 'D:\src\mxc-windows-gh-action'
dotnet restore Mxc.GitHubRunner.slnx --locked-mode
dotnet test Mxc.GitHubRunner.slnx -c Release --no-restore
dotnet publish src/Mxc.GitHubRunner -c Release --no-restore -o artifacts/publish
Set-Location artifacts/publish
```

各コマンドが成功してから次へ進みます。配布する場合は `artifacts/publish` 全体をコピーしてください。この `dotnet publish` だけでは MinGit BusyBox は同梱されません。`actions/checkout` を使うため、次の公式パッケージをホスト側で取得します。

```powershell
$gitRoot = 'D:\tools\MinGitBusyBox'
if (Test-Path -LiteralPath $gitRoot) {
    throw 'The MinGit BusyBox destination already exists.'
}
$gitArchive = Join-Path $env:TEMP ('mxc-mingit-' + [guid]::NewGuid().ToString('N') + '.zip')
Invoke-WebRequest 'https://github.com/git-for-windows/git/releases/download/v2.56.0.windows.2/MinGit-2.56.0.2-busybox-64-bit.zip' -OutFile $gitArchive
$expected = 'a8a9172a6747926648b78a3411f3b94813dd22da8a8ec5dbe61122bc414edd63'
if ((Get-FileHash -LiteralPath $gitArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    throw 'MinGit BusyBox SHA-256 mismatch.'
}
Expand-Archive -LiteralPath $gitArchive -DestinationPath $gitRoot
Remove-Item -LiteralPath $gitArchive
```

これは [Git for Windows v2.56.0.windows.2](https://github.com/git-for-windows/git/releases/tag/v2.56.0.windows.2) の公式 BusyBox 版です。次の設定手順で、このディレクトリの `cmd` を `toolPaths` の先頭に指定します。通常の MinGit ZIP とは別の配布物です。詳細は [Git 互換性](docs/git-compatibility.md) を参照してください。

## 3. 設定を作成する

以降のコマンドは、`mxc-gh-runner.exe` があるディレクトリで実行します。

```powershell
.\mxc-gh-runner.exe init
.\mxc-gh-runner.exe doctor
notepad .\mxc-runner.json
```

`init` は設定ファイルを新規作成し、既存ファイルは上書きしません。まず `repository` を、自分がジョブを実行するリポジトリの URL に変更します。

```json
"repository": "https://github.com/OWNER/REPO"
```

生成済みの設定の該当行だけを編集します。これは JSON 全体ではありません。トークンを設定ファイルへ書き込む項目はありません。

ソースからビルドし、MinGit を手動導入した場合は `toolPaths` も変更します。

```json
"toolPaths": [
  "D:\\tools\\MinGitBusyBox\\cmd"
]
```

配布物を使う場合は、`init` が設定した同梱版のパスをそのまま使えます。複数の Git を指定した場合は先にあるものが選択されるため、通常版 Git の `cmd` を前に置かないでください。PowerShell 7 がインストール済みで `pwsh` を使う場合は、そのインストール先（通常 `C:\Program Files\PowerShell\7`）も配列へ追加します。存在しないディレクトリは指定できません。

作業データの `stateDirectory` は、CLI、Windows、.NET、指定ツールと分けます。それらの配下にも、それらを含む親ディレクトリにも置けません。`init` は設定ファイル隣の `.mxc-runner` を検討し、CLI の読み取り許可範囲と重なる場合は `%LOCALAPPDATA%\MxcGitHubRunner` を選びます。生成値を維持するのが簡単です。ツール自体はユーザープロファイル外へ配置してください。

別の設定を作る場合は、`init --config D:\runner-config\mxc-runner.json` のように指定できます。その後の `plan`・`smoke`・`prepare`・`run` にも同じ `--config` を付けます。相対パスは設定ファイルのあるディレクトリが基準です。`doctor` は設定不要で、`--config` を受け付けません。

## 4. ローカル動作を確認する

```powershell
.\mxc-gh-runner.exe plan
.\mxc-gh-runner.exe smoke
.\mxc-gh-runner.exe prepare
```

`plan` は設定を検証し、解決済みのパスを表示します。`smoke` は GitHub の認証情報なしで実際に MXC 内の .NET を起動し、標準入力、認証環境変数の非継承、一時ファイルの読み書きを確認します。`doctor` が成功しても、`smoke` の確認は必要です。

`prepare` は公式 GitHub Actions ランナーをダウンロードして SHA-256 を検証し、キャッシュへ展開します。ランナーの登録は行いません。初回はダウンロードに時間がかかります。既定の `runnerVersion` は `latest` です。固定する場合は `2.338.0` のようなバージョンを設定します。

## 5. ワークフローを用意する

対象リポジトリの `.github/workflows/mxc-check.yml` に、次の内容を保存し、GitHub の既定ブランチへ反映します。実行ホストには .NET 10 SDK が必要です。

```yaml
name: MXC first job

on:
  workflow_dispatch:

permissions:
  contents: read

jobs:
  check:
    runs-on: [self-hosted, Windows, X64, mxc]
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@v7.0.1
      - name: Build and run C#
        shell: powershell
        run: |
          $ErrorActionPreference = 'Stop'
          dotnet --version
          if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
          $project = Join-Path $env:RUNNER_TEMP 'mxc-first-job'
          dotnet new console --framework net10.0 --output $project
          if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
          dotnet run --project $project --no-restore
          if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

`mxc` は既定のカスタムラベルです。設定の `labels` を変更した場合は `runs-on` も合わせます。ARM64 の場合は `X64` を `ARM64` に変更しますが、本プロジェクトの実機検証と同梱 Git の配布物は x64 向けです。

[基本ワークフロー](examples/mxc-workflow.yml) と、PowerShell 7 を使う [実ジョブ検証用ワークフロー](examples/e2e-validation.yml) も用意しています。ワークフローは信頼して実行できるリポジトリに配置してください。

## 6. 登録してジョブを実行する

対象リポジトリの **Settings → Actions → Runners → New self-hosted runner** を開き、Windows 向けに表示される登録コマンドから登録トークンを取得します。画面に表示されるランナーのダウンロード・登録コマンドは、この CLI が代わりに実行します。

ホストの PowerShell で、トークンを画面や履歴へ直接書かずに入力します。

```powershell
$registrationToken = Read-Host 'Runner registration token' -AsSecureString
try {
    $env:MXC_RUNNER_REGISTRATION_TOKEN = [System.Net.NetworkCredential]::new('', $registrationToken).Password
    .\mxc-gh-runner.exe run
} finally {
    Remove-Item Env:\MXC_RUNNER_REGISTRATION_TOKEN -ErrorAction SilentlyContinue
    $registrationToken.Dispose()
}
```

ランナーがジョブ待機状態になったら、ブラウザーで対象リポジトリの **Actions → MXC first job → Run workflow** を実行します。`run` はワークフローの起動操作は行いません。一致するラベルを持つジョブが GitHub から割り当てられるのを待ちます。

1 ジョブが終わるとランナーは終了します。次のジョブには CLI をもう一度起動してください。登録トークンには期限があるため、再実行時は新しいトークンを取得します。登録・待機・ジョブ実行を含む MXC プロセスの上限は既定で 120 分です。`timeoutMinutes` で 1～1440 分に変更できます。

### PAT を使う場合

リポジトリのランナー管理 API を操作できる PAT を、ホストの `MXC_GITHUB_TOKEN` に設定する方式も実装しています。CLI はホスト側で登録トークンを取得し、終了時には残ったランナー登録の API 削除を試みます。PAT 自体は MXC の環境変数や起動引数へ渡しません。

```powershell
$accessToken = Read-Host 'GitHub runner-management token' -AsSecureString
try {
    $env:MXC_GITHUB_TOKEN = [System.Net.NetworkCredential]::new('', $accessToken).Password
    .\mxc-gh-runner.exe run
} finally {
    Remove-Item Env:\MXC_GITHUB_TOKEN -ErrorAction SilentlyContinue
    $accessToken.Dispose()
}
```

両方の環境変数がある場合、登録には `MXC_RUNNER_REGISTRATION_TOKEN`、残った登録の削除には `MXC_GITHUB_TOKEN` を使います。PAT 方式の API 処理は模擬 HTTP による自動テストで確認しています。実 GitHub ジョブの検証には登録トークン方式を使用しました。

## 7. 結果と後片付けを確認する

**ジョブの成功・失敗は GitHub Actions の実行画面で確認します。** CLI の `run` が返すのはランナープロセスの終了コードで、ワークフローの結論ではありません。時間切れは `124`、Ctrl+C は `130`、CLI のエラーは通常 `1` です。

`stateDirectory` には次のデータが作られます。

| ディレクトリ | 内容 |
| --- | --- |
| `packages/` | 取得した公式ランナーのテンプレート。次回も再利用 |
| `runs/<GUID>/` | その実行のプロファイル、ランナー、作業領域。既定では終了時に削除 |
| `logs/` | 保持される標準出力・標準エラーの転送ログ |

ジョブが正常に処理されると、一時ランナー自身が GitHub の登録を削除します。登録トークンだけで認証し、ジョブ受信前に中断・時間切れとなった場合は、オフライン登録が残ることがあります。ログに表示された名前を **Settings → Actions → Runners** で確認し、残っていれば削除してください。

成果物は作業領域の削除前にワークフロー内でアップロードします。`keepWorkDirectory: true` は調査用の保持設定で、ジョブのデータやランナー資格情報も残る場合があります。通常は既定の `false` を使います。

## コマンドと設定の参照

| コマンド | 動作 |
| --- | --- |
| `init [--config PATH]` | 既存ファイルを上書きせず、設定を新規作成 |
| `doctor` | Windows / MXC の対応状態を検出。設定オプションは受け付けない |
| `plan [--config PATH]` | 設定の検証と解決済みパスの表示 |
| `smoke [--config PATH]` | GitHub 認証不要のローカル MXC 起動確認 |
| `prepare [--config PATH]` | 公式ランナーの取得と SHA-256 検証 |
| `run [--config PATH]` | 登録、1 ジョブの待機・実行、後処理 |

設定ファイルの既定値は現在のディレクトリの `mxc-runner.json` です。[設定例](examples/mxc-runner.example.json) も参照してください。未知の項目や重複した項目は拒否され、大文字・小文字だけが異なる名前も重複として扱います。トークンを保存する設定項目はありません。

| 項目 | 既定値と制約 |
| --- | --- |
| `repository` | `https://github.com/OWNER/REPO`。実行前に実際の URL へ変更 |
| `runnerVersion` | `latest`、または `2.338.0` のような3要素のバージョン |
| `architecture` | `init` がホストに合わせて選択。`x64` または `arm64` で、実行時もホストと一致する必要がある |
| `stateDirectory` | `.mxc-runner`、または `init` が選んだ安全な別の場所。読み取り専用のツール領域と包含関係を持たず、ドライブのルートを指定しない |
| `namePrefix` | `mxc`。1～40文字の ASCII 英数字・`.`・`_`・`-`。先頭は英数字。実行ごとに一意の接尾辞を追加 |
| `labels` | `["mxc"]`。最大20個、各1～64文字。使用できる文字と先頭文字の規則は `namePrefix` と同じ |
| `toolPaths` | PATH と読み取り許可に使う既存ディレクトリ。Git の `cmd` を指定すると、そのインストール領域も読み取り許可に含める |
| `timeoutMinutes` | `120`。MXC 内の登録・待機・実行に1～1440分の上限を設定。ダウンロードと準備の時間は含まない |
| `keepWorkDirectory` | `false`。`true` で終了後の実行データを保持。ランナープロセスは終了する |

## トラブルシューティング

| 症状 | 確認すること |
| --- | --- |
| CLI 自体が起動しない | .NET 10 ランタイムがあるか、配布ディレクトリ全体を展開したか |
| `doctor` が未対応を報告する | Windows と MXC の対応状態。`doctor` はバックエンド検出、実際の起動確認は `smoke` |
| `smoke` やツール起動でアクセス拒否 | CLI とツールをユーザープロファイル外へ配置したか。読み取り許可だけでは既存のファイル権限による制約を解消できない場合がある |
| `stateDirectory` の重複エラー | CLI・Windows・.NET・ツールと包含関係のない専用の作業パスにする。`init` の生成値と `plan` を確認 |
| `OWNER/REPO` や認証のエラー | 実際のリポジトリ URL、管理権限、登録トークンの有効期限を確認 |
| ジョブが待機し続ける | ランナーがオンラインか、`Windows`・`X64`・`mxc` などの全ラベルが一致するか |
| `NtCreateDirectoryObject`、`sh.exe`、`bash.exe` の初期化失敗 | 通常版 Git/MSYS の制約。MinGit BusyBox を最初の Git として選び、ジョブのシェルは PowerShell を使う |
| `pwsh` が見つからない | PowerShell 7 をインストールして `toolPaths` に追加するか、`shell: powershell` を使う |
| .NET SDK が見つからない | ランタイムだけでなく必要な SDK をホストへ導入する。確認には `dotnet --version` を使う。`dotnet --info` は追加のシステム照会で失敗する場合がある |

この CLI は常駐サービスや自動スケーラーではありません。GitHub Enterprise Server、組織単位ランナー、Linux コンテナジョブも対象外です。外向き通信は許可され、通信先の許可リスト設定はありません。.NET のため UI サブシステムを有効にし、クリップボードと入力注入を個別に制限しています。

詳しい境界は [実装構成](docs/architecture.md)、確認済みの環境と未確認範囲は [検証記録](docs/verification.md) を参照してください。

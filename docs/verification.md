# 検証記録

検証日: 2026-10-11（日本時間）。初期版の実装環境と実際に行った確認を記録します。

## 環境

| 項目 | 確認値 |
| --- | --- |
| OS | Windows 10.0.26300 / x64 |
| MXC バックエンド | ProcessContainer / BaseContainer |
| MXC SDK | Microsoft.Mxc.Sdk 1.0.0 |
| .NET SDK | 10.0.204 |
| .NET ランタイム | 10.0.8 |
| GitHub Actions ランナー | 2.338.0 / win-x64 |
| Git | 2.56.0.windows.2 / 公式 MinGit BusyBox（配布物に同梱） |

プロジェクトは `net10.0`、開発用 `global.json` は `10.0.200` と `latestFeature` を指定しています。インストール済みの 10.0.204 で復元・ビルド・テスト・公開を確認しました。

## ローカルでの確認

- `dotnet restore Mxc.GitHubRunner.slnx --locked-mode` が成功。
- Release ビルドの自動テスト 108 件が成功（失敗・スキップなし）。
- 公開済み `mxc-gh-runner.exe doctor` が MXC ProcessContainer と BaseContainer を検出。
- 公開済み `mxc-gh-runner.exe smoke` が実際に MXC 内の .NET 10.0.8 を起動し、標準入力、ホスト認証環境変数の非継承、一時ファイルの書き込みと読み込みを確認。
- `init` の再実行は既存ファイルを上書きせず失敗し、実行前後の SHA-256 が一致。
- CLI の展開先に設定を作成する `init` が、作業領域としてインストール先の外側の `%LOCALAPPDATA%\MxcGitHubRunner` を選択。公開済み実行ファイルでも確認。
- `plan` が設定ファイル基準でパスを解決し、`doctor` が未対応オプションを拒否。
- `prepare` が GitHub 公式ランナー 2.338.0 を取得し、SHA-256 検証を通過。
- MXC 内で `Runner.Listener --version` と資格情報暗号化に必要な DPAPI の往復を確認。
- SDK/MSBuild 設定の探索境界を設けた状態で、MXC 内の `dotnet new`・restore・`dotnet run` による C# プログラムの生成と実行を確認。
- 最終配布ディレクトリに同梱した MinGit BusyBox を使い、MXC 内で `git init`・`submodule foreach`・HTTPS fetch・checkout・rev-parse が成功。公開済み CLI の `init` が同梱版を先頭に選択することを確認。

## GitHub Actions の実ジョブ

最終公開ビルドと同梱 Git を使用し、検証用プライベートリポジトリでの実ジョブが **成功**しました。実行全体は 40 秒、`smoke` ジョブは 30 秒です。

実際の一時セルフホストランナーに GitHub からジョブが割り当てられ、次の確認を通過しました。

- MXC 内の実行ごとのプロファイルと一時領域。
- ホストの認証用環境変数がジョブに継承されないこと。
- 作業領域のファイル作成と読み取り。
- Git の実行。
- C# コンソールプロジェクトの作成、ビルド、実行。
- JavaScript アクション `actions/checkout@v7.0.1` によるプライベートリポジトリの取得と HEAD の確認（5 秒）。
- checkout の後処理と認証設定の削除（2 秒、警告なし）。

ジョブ後はランナープロセスが終了コード `0` で停止しました。ランナー自身による `.credentials` と `.runner` の削除、ローカル `runs/` 配下に実行ディレクトリが残っていないことを確認しました。GitHub の **Settings → Actions → Runners** でも登録が残っていないことを確認しています。

## 再現手順

```powershell
dotnet restore Mxc.GitHubRunner.slnx --locked-mode
dotnet build Mxc.GitHubRunner.slnx -c Release --no-restore -t:Rebuild
dotnet test Mxc.GitHubRunner.slnx -c Release --no-build --no-restore
dotnet publish src/Mxc.GitHubRunner -c Release --no-build --no-restore -o artifacts/publish
./artifacts/publish/mxc-gh-runner.exe init
./artifacts/publish/mxc-gh-runner.exe doctor
./artifacts/publish/mxc-gh-runner.exe smoke
```

生成した設定の `repository` を検証用リポジトリへ変更し、[実際に検証したワークフロー](../examples/e2e-validation.yml) を対象リポジトリの `.github/workflows/` に配置します。このワークフローでは MinGit BusyBox と PowerShell 7 を使用します。対象リポジトリの登録トークンをホストの `MXC_RUNNER_REGISTRATION_TOKEN` 環境変数へ設定し、`run` を開始してからワークフローを手動実行してください。トークンは設定ファイルや検証記録へ保存しません。

```powershell
./artifacts/publish/mxc-gh-runner.exe prepare
./artifacts/publish/mxc-gh-runner.exe run
```

ジョブ結果を GitHub Actions の実行ページで確認し、ホストではランナーの終了、作業領域の削除、転送ログの保存を確認します。`keepWorkDirectory` は既定の `false` を使います。

GitHub ホスト型ランナー上の CI 実行と ARM64 実機の確認は、このローカル検証の結果には含みません。

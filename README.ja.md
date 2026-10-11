# MXC GitHub Actions Runner

[English](README.md) | 日本語

[![Build and test](https://github.com/MogamiTsuchikawa/mxc-windows-gh-action/actions/workflows/ci.yml/badge.svg)](https://github.com/MogamiTsuchikawa/mxc-windows-gh-action/actions/workflows/ci.yml)

Windows の Microsoft MXC ProcessContainer 内で、GitHub Actions のセルフホストランナーを動かす C# / .NET 10 製 CLI です。1 回の起動でランナーを一時登録し、1 ジョブが終了すると停止します。既定では作業領域も削除します。

**導入と最初のジョブ実行は [Get Started 日本語版](GET_STARTED.ja.md) を参照してください。**

## できること

- GitHub 公式ランナーを取得し、SHA-256 を検証して再利用可能なテンプレートを作成します。
- 登録処理とジョブを同じ MXC 内で実行し、実行ごとのプロファイル・一時領域・環境変数を用意します。
- ホストの環境変数を一括継承せず、登録トークンを標準入力で渡します。PAT を使う GitHub API 操作はホスト側で行います。
- 標準出力・標準エラーのログを保存し、通常終了・中断時の後処理を行います。
- 配布物に公式 MinGit BusyBox を同梱し、`actions/checkout` で必要な Git ヘルパーを利用できます。

実行の流れは、`init` → `doctor` / `smoke` → `prepare` → `run` です。CLI はジョブを待ち受けるランナーを起動し、実際のワークフローは GitHub Actions が割り当てます。

## 対応範囲

対象は MXC ProcessContainer が利用できる Windows と、`github.com` のリポジトリ単位ランナーです。CLI の実行には .NET 10 ランタイム、C# ビルドには .NET 10 SDK が必要です。Windows x64 で実ジョブを検証しています。ARM64 の設定はありますが、実機では未検証です。

ホストの OS と許可したツールを使う方式です。作業領域に読み書きを許可し、Windows・.NET・CLI・指定ツールには読み取りを許可します。外向き通信は許可し、受信とホストループバックは拒否します。.NET の起動に必要な UI サブシステムは有効で、クリップボードと入力注入を個別に制限します。

通常版 Git の Bash/MSYS ヘルパーには現在の MXC との互換性制限があります。Git には MinGit BusyBox、ワークフローのシェルには `powershell`、またはインストール済みの `pwsh` を使用してください。

常駐サービス、自動スケール、組織単位ランナー、GitHub Enterprise Server、Linux コンテナジョブは対象外です。次のジョブには CLI を再起動します。登録トークンだけでジョブ受信前に中断した場合、GitHub に残るオフライン登録を手動で削除する必要があります。

## 配布と検証

現在の配布先は [Build and test の Actions アーティファクト](https://github.com/MogamiTsuchikawa/mxc-windows-gh-action/actions/workflows/ci.yml) です。成功した `main` の実行から `mxc-gh-runner-windows` を取得できます。ダウンロードには GitHub へのサインインが必要です。GitHub Releases でのリリースはまだ公開していません。ソースからのビルド手順も Get Started にあります。

Windows x64 / MXC BaseContainer で、ランナーの登録、プライベートリポジトリの checkout、C# ビルド・実行、通常終了後のローカル作業領域と GitHub 登録の削除を確認しました。GitHub ホスト型 CI でもビルドと 108 件の自動テストが成功しています。実環境の確認と自動テストの範囲は [検証記録](docs/verification.md) にまとめています。

## 関連ドキュメント

- [Get Started](GET_STARTED.ja.md)：導入、設定、認証、最初のワークフロー、トラブルシューティング
- [実装構成と実行境界](docs/architecture.md)
- [Git 互換性と MinGit BusyBox](docs/git-compatibility.md)
- [設定例](examples/mxc-runner.example.json) / [基本ワークフロー](examples/mxc-workflow.yml) / [実ジョブ検証用ワークフロー](examples/e2e-validation.yml)

## ライセンス

このプロジェクト独自のソースコードとドキュメントは [MIT License](LICENSE) です。同梱・利用する第三者ソフトウェアには、それぞれのライセンスが適用されます。[第三者ソフトウェアの通知](THIRD_PARTY_NOTICES.md) を参照してください。

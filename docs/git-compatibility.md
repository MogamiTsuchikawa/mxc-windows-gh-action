# Git と checkout アクション

現在の MXC ProcessContainer では、通常の Git for Windows に付属する MSYS の `sh.exe`・`bash.exe` が Windows の名前付きオブジェクト領域を作れず起動に失敗します。ネイティブの `git --version` や `git init` が動いても、シェルを使うサブモジュール操作などは失敗する場合があります。この制限は [Microsoft MXC の issue #1061](https://github.com/microsoft/mxc/issues/1061) でも報告されています。

`actions/checkout` には、Git for Windows の公式 **MinGit BusyBox** を使用します。BusyBox のネイティブ実装を使う Git ヘルパーで MSYS シェルへの依存を避けます。標準の MinGit ZIP と BusyBox 版 ZIP は別の配布物です。ワークフローの通常のシェルは `powershell` または `pwsh` を指定してください。GNU Bash 自体を利用可能にする設定ではありません。

このアプリケーションの配布 ZIP は、検証済みの公式パッケージ全体を `tools/MinGitBusyBox/` に同梱します。`init` は必要な実行ファイルが揃った同梱版を優先して `toolPaths` に設定するため、配布 ZIP を使用する場合は以下の手動導入を省略できます。公式パッケージの `LICENSE.txt` などの通知文書もそのまま含めます。

上流ソースは [Git for Windows](https://github.com/git-for-windows/git) と [Git for Windows の BusyBox 移植](https://github.com/git-for-windows/busybox-w32) で公開されています。同梱依存ライブラリの通知文書は `ucrt64/share/licenses/`・`usr/share/licenses/`・`ucrt64/doc/git-credential-manager/` も参照してください。

## Windows x64 への導入

検証に使用する版は [Git for Windows v2.56.0.windows.2](https://github.com/git-for-windows/git/releases/tag/v2.56.0.windows.2) の `MinGit-2.56.0.2-busybox-64-bit.zip` です。リリースに掲載されている SHA-256 は次の値です。

```text
a8a9172a6747926648b78a3411f3b94813dd22da8a8ec5dbe61122bc414edd63
```

以下はホストの PowerShell で実行する初回導入例です。`$gitRoot` は環境に合わせて変更してください。検証時はユーザープロファイル外の `D:\develop\mxc-runner-tools\MinGitBusyBox` で動作を確認しました。CLI の展開先・`stateDirectory`・ユーザープロファイルとは別のツール用ディレクトリを指定し、既存のインストールは上書きしません。

```powershell
$gitRoot = 'D:\develop\mxc-runner-tools\MinGitBusyBox'
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

ソースからビルドして手動導入した場合は、`init` で作成した設定の `toolPaths` を編集し、最初の Git ディレクトリをこの `cmd` へ変更してください。`init` 自体は Git をダウンロードしません。インストール先は実際の値に合わせます。

```json
"toolPaths": [
  "D:\\develop\\mxc-runner-tools\\MinGitBusyBox\\cmd",
  "C:\\Program Files\\PowerShell\\7"
]
```

`toolPaths` の順序が PATH の検索順です。通常版 Git の `cmd` を先に置くとそちらが選択されます。PowerShell 7 の行はインストール済みの場合だけ追加します。Git のインストール先は `stateDirectory` の外に置いてください。

この検証ホストでは `%LOCALAPPDATA%` 配下に置いた MinGit は継承された権限の影響で起動できませんでした。読み取り許可に登録するだけで全配置の実行が保証されるわけではないため、プロファイル外のツール領域を使用してください。ホストの既存ファイル ACL を変更する回避策は採用していません。

`init` は実際に先に選択される Git に MSYS シェルが付属する場合だけ警告します。MinGit BusyBox に残っている他の補助ツール用の MSYS DLL だけで警告することはありません。

ARM64 の公式配布物も同じリリースにありますが、この検証記録の実機確認は Windows x64 です。

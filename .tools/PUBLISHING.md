# GitHub・VCCでの手動公開

GitHubユーザーは `moffox`、本体リポジトリは `mof-tools-mesh-join`、共通の配布一覧は `mof-tools-vpm` を使用します。Git操作はTortoiseGit、GitHubの設定とリリース公開はブラウザーで行います。自動公開するActionsは使用しません。

## 最初の設定

1. GitHubで公開リポジトリ `moffox/mof-tools-mesh-join` を作成します。既存履歴を送るため、READMEやLICENSEを自動生成せず空で作成します。
2. 本体フォルダーを右クリックし、TortoiseGitのリモート設定にGitHubのURLを登録します。Pushで現在のブランチを送ります。ブランチは現在の `master` のまま使用できます。
3. GitHubの本体リポジトリで Settings → General → Releases → Enable release immutability を有効にします。新しく公開するリリースが対象です。
4. 共通一覧側の初期設定は、隣の `VpmListing/README.md` に従います。

## バージョンを公開する

1. `package.json` のバージョンと `CHANGELOG.md` を確定し、TortoiseGitで対象ファイルだけをCommitします。`.dist` と `.validation` は対象外です。
2. Windows PowerShell 5.1またはPowerShell 7で本体フォルダーを開き、次を実行します。

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .tools/Build-Package.ps1
   ```

3. `.dist/v0.6.1/` のような版別フォルダーにZIP、`package.json`、`SHA256SUMS.txt` が作成されます。ZIPの直下にpackage.jsonがあるVPM形式です。Tests、検証用アバター、`.validation`、`.git`、公開補助スクリプトは入りません。元の.metaとGUIDは維持します。
4. 配布ZIPから別プロジェクトに導入し、動作を確認します。更新版では元のシーンを自動保存せず、複製で確認してください。
5. TortoiseGitで確定したコミットに `v0.6.1` のようなタグを付け、コミットとタグをPushします。
6. GitHub Releases → Draft a new releaseで、そのタグを選びます。作成した3ファイルをすべて添付し、アップロード完了後にSave draftで保存します。
7. 内容を確認してPublish releaseを押します。Immutable releasesでは公開後に添付ファイルの追加・差し替え・削除ができません。修正はバージョンを上げ、別タグ・別リリースで行います。
8. 公開後に共通の配布一覧へ登録します。手順は `VpmListing/README.md` に記載しています。

ビルドスクリプトはZIP内と添付用package.jsonのダウンロードURLを現在のバージョンから設定します。既存の出力は上書きしません。公開前の作り直しでは、別の出力先を `-OutputDirectory` で指定できます。公開済みの版の差し替えには使用しません。

リポジトリの作成・リモート登録・Commit・Push・タグ作成・GitHub公開は、このスクリプトでは行いません。

上記コマンドの実行許可はそのPowerShellプロセスだけに適用し、Windows全体の設定は変更しません。

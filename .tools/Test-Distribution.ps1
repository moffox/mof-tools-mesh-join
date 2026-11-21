param([string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot -Parent
if (!$PackageDirectory) { $PackageDirectory = Join-Path $root '.dist/v0.6.1' }
$manifestPath = Join-Path $PackageDirectory 'package.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$zipPath = Join-Path $PackageDirectory "$($manifest.name)-$($manifest.version).zip"
$count = 0
function Check([bool]$Condition,[string]$Message) {
    if (!$Condition) { throw $Message }
    $script:count++
}
function Reject([scriptblock]$Action,[string]$Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Check $rejected $Message
}
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = @($zip.Entries | ForEach-Object { $_.FullName })
    Check ('package.json' -in $names) 'package.jsonがZIP直下にありません。'
    Check (!($names | Where-Object { $_ -match '(^|/)(Tests|Assets|\.validation|\.git|\.tools|Library)(/|$)' })) '開発・検証データが含まれています。'
    $guids = @{}
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName
        Check ($name -notmatch '\\|(^/)|(^|/)\.\.(/|$)') "不正なZIPパス: $name"
        if ($name -notlike '*.meta') { Check ("$name.meta" -in $names) ".metaがありません: $name" }
        if ($name -eq 'package.json') { continue }
        $input = $entry.Open(); $memory = [IO.MemoryStream]::new()
        try { $input.CopyTo($memory); $bytes = $memory.ToArray() } finally { $input.Dispose(); $memory.Dispose() }
        Check ([Convert]::ToBase64String($bytes) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root $name)))) "配布時に元ファイルを変更しています: $name"
        if ($name -like '*.meta') {
            $guid = [regex]::Match([Text.Encoding]::UTF8.GetString($bytes),'(?m)^guid: ([a-f0-9]{32})').Groups[1].Value
            Check ([bool]$guid -and !$guids.ContainsKey($guid)) "GUIDが不正または重複: $name"
            $guids[$guid] = $name
        }
    }
} finally { $zip.Dispose() }
Check ($manifest.author.email -ceq 'github-moffox@outlook.jp') '公開メールが違います。'
Check ($manifest.displayName -ceq 'MA Mesh Join by Mof') 'VCC表示名が違います。'
Check ($manifest.license -ceq 'LicenseRef-MofTools') 'SPDXカスタムライセンス識別子が違います。'
Check ($manifest.url -ceq "https://github.com/moffox/mof-tools-mesh-join/releases/download/v$($manifest.version)/$($manifest.name)-$($manifest.version).zip") '配布URLが違います。'
$expectedHash = ((Get-Content -LiteralPath (Join-Path $PackageDirectory 'SHA256SUMS.txt') -Raw).Split(' '))[0]
Check ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $expectedHash) 'ZIPのSHA-256が一致しません。'
Reject { & (Join-Path $PSScriptRoot 'Build-Package.ps1') -OutputDirectory $PackageDirectory } '同じ出力を上書きできてしまいます。'

# Exercise listing imports in a disposable copy; no unreleased version enters the real listing.
$checkRoot = Join-Path $root ('.dist/listing-check-' + [Guid]::NewGuid().ToString('N'))
$listingSource = Join-Path (Split-Path $root -Parent) 'VpmListing'
New-Item -ItemType Directory -Path (Join-Path $checkRoot '.tools') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $listingSource 'source.json') -Destination $checkRoot
Copy-Item -LiteralPath (Join-Path $listingSource '.tools/Update-Listing.ps1') -Destination (Join-Path $checkRoot '.tools')
$update = Join-Path $checkRoot '.tools/Update-Listing.ps1'
& $update -PackageManifest $manifestPath -PackageZip $zipPath -SkipDownloadCheck
& $update -PackageManifest $manifestPath -PackageZip $zipPath -SkipDownloadCheck
$index = Get-Content -LiteralPath (Join-Path $checkRoot 'docs/index.json') -Raw -Encoding UTF8 | ConvertFrom-Json
Check ($index.packages.($manifest.name).versions.($manifest.version).zipSHA256 -ceq $expectedHash) 'VCC一覧のハッシュが違います。'
function Fixture([string]$Name,[string]$Version,[string]$Description) {
    $data = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $data.name = $Name; $data.version = $Version; $data.description = $Description
    $data.url = "https://github.com/moffox/sample-tool/releases/download/v$Version/$Name-$Version.zip"
    $directory = Join-Path $checkRoot ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory | Out-Null
    $json = Join-Path $directory 'package.json'
    [IO.File]::WriteAllText($json,($data | ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
    $archivePath = Join-Path $directory 'fixture.zip'
    $archive = [IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Create)
    try { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$json,'package.json') | Out-Null } finally { $archive.Dispose() }
    return @($json,$archivePath)
}
$newVersion = Fixture $manifest.name '0.6.2' 'Version fixture'
& $update -PackageManifest $newVersion[0] -PackageZip $newVersion[1] -SkipDownloadCheck
$newTool = Fixture 'dev.moftools.sample-tool' '1.0.0' 'Other tool fixture'
& $update -PackageManifest $newTool[0] -PackageZip $newTool[1] -SkipDownloadCheck
$index = Get-Content -LiteralPath (Join-Path $checkRoot 'docs/index.json') -Raw -Encoding UTF8 | ConvertFrom-Json
Check (@($index.packages.($manifest.name).versions.PSObject.Properties).Count -eq 2) '既存バージョンが失われています。'
Check ($null -ne $index.packages.'dev.moftools.sample-tool'.versions.'1.0.0') '複数ツールを管理できません。'
Check ($index.url -ceq 'https://moffox.github.io/mof-tools-vpm/index.json') '共通一覧のURLが変わっています。'
$conflict = Fixture $manifest.name $manifest.version 'Conflicting fixture'
Reject { & $update -PackageManifest $conflict[0] -PackageZip $conflict[1] -SkipDownloadCheck } '登録済みバージョンを書き換えできてしまいます。'
Reject { & $update -PackageManifest $newVersion[0] -PackageZip $newTool[1] -SkipDownloadCheck } 'ZIPと添付manifestの不一致を見逃しています。'
Write-Host "PASS: $count distribution checks"

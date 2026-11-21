param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-JsonTable([string]$Text) {
    $object = $Text | ConvertFrom-Json
    $table = [ordered]@{}
    foreach ($property in $object.PSObject.Properties) { $table[$property.Name] = $property.Value }
    return $table
}

$packageRoot = Split-Path $PSScriptRoot -Parent
$manifest = Read-JsonTable (Get-Content -LiteralPath (Join-Path $packageRoot 'package.json') -Raw -Encoding UTF8)
if ($manifest.name -notmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)+$' -or
    $manifest.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
    throw 'パッケージ名またはバージョンが不正です。'
}
$assetName = "$($manifest.name)-$($manifest.version).zip"
$manifest.url = "https://github.com/moffox/mof-tools-mesh-join/releases/download/v$($manifest.version)/$assetName"
if (!$OutputDirectory) { $OutputDirectory = Join-Path $packageRoot ".dist/v$($manifest.version)" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$zipPath = Join-Path $OutputDirectory $assetName
foreach ($name in @($assetName, 'package.json', 'SHA256SUMS.txt')) {
    if (Test-Path -LiteralPath (Join-Path $OutputDirectory $name)) {
        throw "既存の配布物を上書きしません: $OutputDirectory。公開済みの場合はバージョンを上げてください。"
    }
}

# Only these production files can enter the distribution. No project or test fixtures are scanned.
$files = @('package.json', 'package.json.meta', 'Editor.meta', 'Mesh Join.meta',
    'Mesh Join/Runtime.meta', 'Mesh Join/Editor.meta')
foreach ($directory in @('Editor', 'Mesh Join/Runtime', 'Mesh Join/Editor')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $packageRoot $directory) -Recurse -File | ForEach-Object {
        $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
    }
}
foreach ($document in @('README.md', 'CHANGELOG.md', 'LICENSE.md', 'THIRD_PARTY_NOTICES.md')) {
    $files += $document, "$document.meta"
}
$files = @($files | Sort-Object -Unique)
foreach ($file in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $packageRoot $file) -PathType Leaf)) { throw "必要なファイルがありません: $file" }
    if ($file -notlike '*.meta' -and "$file.meta" -notin $files) { throw ".metaがありません: $file" }
}
if (!$manifest.author.email -or !$manifest.vpmDependencies -or !$manifest.license) { throw '作者・依存関係・ライセンス情報が不足しています。' }
$manifestJson = ($manifest | ConvertTo-Json -Depth 30) + "`n"
$encoding = [Text.UTF8Encoding]::new($false)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in $files) {
            $entry = $archive.CreateEntry($file, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
            $bytes = if ($file -eq 'package.json') { $encoding.GetBytes($manifestJson) } else { [IO.File]::ReadAllBytes((Join-Path $packageRoot $file)) }
            $entryStream = $entry.Open()
            try { $entryStream.Write($bytes,0,$bytes.Length) } finally { $entryStream.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'package.json'), $manifestJson, $encoding)
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS.txt'), "$hash  $assetName`n", $encoding)
Write-Host "配布物を作成しました: $OutputDirectory"
Write-Host 'ZIP、package.json、SHA256SUMS.txtをGitHubのDraftリリースに添付してください。公開処理は実行していません。'

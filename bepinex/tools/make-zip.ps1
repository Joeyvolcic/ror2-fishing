# Zips bepinex\package (manifest.json, README.md, icon.png, plugins\RoR2Fishing.dll) into
# bepinex\dist\JV94-RoR2Fishing-<version>.zip for r2modman "Import local mod" (and later Thunderstore upload).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$pkg  = Join-Path $root "package"
$ver  = (Get-Content (Join-Path $pkg "manifest.json") -Raw | ConvertFrom-Json).version_number
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
$zip  = Join-Path $dist ("JV94-RoR2Fishing-" + $ver + ".zip")
foreach ($f in "manifest.json", "README.md", "icon.png", "plugins\RoR2Fishing.dll") {
    if (-not (Test-Path (Join-Path $pkg $f))) { throw "package is missing $f" }
}
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in "manifest.json", "README.md", "icon.png", "plugins\RoR2Fishing.dll") {
        # forward slashes inside the zip (Thunderstore / r2modman expect them)
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, (Join-Path $pkg $f), $f.Replace("\", "/")) | Out-Null
    }
}
finally { $z.Dispose() }
"zip: $zip"

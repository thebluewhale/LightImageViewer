param([string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$portableExe = Join-Path $projectRoot 'dist/LightImageViewer.exe'
if (-not (Test-Path -LiteralPath $portableExe)) {
    throw 'Build dist/LightImageViewer.exe first with scripts/Build.ps1.'
}
if (-not $CompilerPath) {
    $CompilerPath = Join-Path $projectRoot '.tools/inno/ISCC.exe'
    if (-not (Test-Path -LiteralPath $CompilerPath)) {
        $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($compilerCommand) { $CompilerPath = $compilerCommand.Source }
    }
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath)) {
    throw 'Inno Setup 7 compiler not found. Pass -CompilerPath <path-to-ISCC.exe>.'
}
$beforeHash = (Get-FileHash -LiteralPath $portableExe -Algorithm SHA256).Hash
$appVersion = (Get-Item -LiteralPath $portableExe).VersionInfo.ProductVersion
& (Join-Path $PSScriptRoot 'Write-FileAssociations.ps1')
& $CompilerPath "/DRepoRoot=$projectRoot" (Join-Path $projectRoot 'installer/ImageViewer.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
$afterHash = (Get-FileHash -LiteralPath $portableExe -Algorithm SHA256).Hash
if ($beforeHash -ne $afterHash) { throw 'Portable executable changed unexpectedly.' }
$setupExe = Join-Path $projectRoot "dist/LightImageViewer-Setup-$appVersion.exe"
if (-not (Test-Path -LiteralPath $setupExe)) { throw "Missing installer: $setupExe" }
$setupHash = (Get-FileHash -LiteralPath $setupExe -Algorithm SHA256).Hash
@(
    "$afterHash  LightImageViewer.exe"
    "$setupHash  LightImageViewer-Setup-$appVersion.exe"
) | Set-Content -LiteralPath (Join-Path $projectRoot 'dist/SHA256SUMS.txt') -Encoding ASCII
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $projectRoot 'dist/README.md')
Get-Item -LiteralPath $portableExe,$setupExe | Select-Object FullName,Length

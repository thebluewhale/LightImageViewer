param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/cli-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $dotnetPath = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnetPath)) { $dotnetPath = 'dotnet' }
    if (-not $SkipTests) {
        python tests/create_fixtures.py
        if ($LASTEXITCODE -ne 0) { throw 'Fixture generation failed (requires Python + Pillow)' }
        & $dotnetPath run --project tests/ImageViewer.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }
    # Publish to staging so SDK cleanup cannot remove older portable EXEs in dist.
    & $dotnetPath publish src/ImageViewer/ImageViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    New-Item -ItemType Directory -Force dist | Out-Null
    Copy-Item -LiteralPath artifacts/publish/LightImageViewer.exe -Destination dist/LightImageViewer.exe
    Copy-Item -LiteralPath README.md,THIRD-PARTY-NOTICES.txt -Destination dist
    Get-Item dist/LightImageViewer.exe | Select-Object FullName,Length
    Get-FileHash dist/LightImageViewer.exe -Algorithm SHA256
} finally { Pop-Location }

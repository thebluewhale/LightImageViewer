$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$catalog = Get-Content -LiteralPath (Join-Path $projectRoot 'src/ImageViewer/ImageCatalog.cs') -Raw
$declaration = [regex]::Match($catalog, '(?s)Extensions\s*=\s*\[(?<items>[^\]]+)\]')
if (-not $declaration.Success) { throw 'ImageCatalog extension declaration not found.' }
$extensions = @([regex]::Matches($declaration.Groups['items'].Value, '"\.(?<extension>[a-z0-9]+)"') | ForEach-Object { $_.Groups['extension'].Value })
if ($extensions.Count -eq 0) { throw 'No image extensions found.' }
$entries = foreach ($extension in $extensions) {
    'Root: HKA; Subkey: "{#CapabilitiesKey}\FileAssociations"; ValueType: string; ValueName: ".{#AssociationExtensionPrefix}' + $extension + '"; ValueData: "{#ImageProgId}"'
    'Root: HKA; Subkey: "Software\Classes\Applications\{#ApplicationRegistrationName}\SupportedTypes"; ValueType: string; ValueName: ".{#AssociationExtensionPrefix}' + $extension + '"; ValueData: ""'
    'Root: HKA; Subkey: "Software\Classes\.{#AssociationExtensionPrefix}' + $extension + '\OpenWithProgids"; ValueType: string; ValueName: "{#ImageProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty'
}
$outputDirectory = Join-Path $projectRoot 'artifacts/installer'
New-Item -ItemType Directory -Force $outputDirectory | Out-Null
$entries | Set-Content -LiteralPath (Join-Path $outputDirectory 'FileAssociations.iss') -Encoding utf8

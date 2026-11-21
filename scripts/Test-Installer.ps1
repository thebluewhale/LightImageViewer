param([string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $CompilerPath) { $CompilerPath = Join-Path $projectRoot '.tools/inno/ISCC.exe' }
$runId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $projectRoot "artifacts/installer-tests/$runId"
$installDir = Join-Path $testRoot 'installed'
$settingsDir = Join-Path $testRoot 'settings'
$appId = 'IVT.' + $runId.Substring(0, 24)
$associationName = "$appId.exe"
$extensionPrefix = "livtest-$runId-"
$capabilitiesPath = "HKCU:\Software\$appId\Capabilities"
$capabilitiesValue = "Software\$appId\Capabilities"
$registeredAppsPath = 'HKCU:\Software\RegisteredApplications'
$progId = "$appId.Image"
$progIdPath = "HKCU:\Software\Classes\$progId"
$applicationPath = "HKCU:\Software\Classes\Applications\$associationName"
$catalog = Get-Content -LiteralPath (Join-Path $projectRoot 'src/ImageViewer/ImageCatalog.cs') -Raw
$items = [regex]::Match($catalog, '(?s)Extensions\s*=\s*\[(?<items>[^\]]+)\]').Groups['items'].Value
$extensions = @([regex]::Matches($items, '"\.(?<extension>[a-z0-9]+)"') | ForEach-Object { '.' + $extensionPrefix + $_.Groups['extension'].Value })
$foreignExtensionPath = "HKCU:\Software\Classes\.${extensionPrefix}png"
$groupName = "LightImageViewer Installer Test $runId"
$registryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${appId}_is1"
$machineRegistryPath = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${appId}_is1"
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) "$groupName/LightImageViewer.lnk"
$testInstaller = Join-Path $testRoot 'LightImageViewer-Setup-Test.exe'
$portableExe = Join-Path $projectRoot 'dist/LightImageViewer.exe'
$portableHash = (Get-FileHash -LiteralPath $portableExe -Algorithm SHA256).Hash
$expectedVersion = (Get-Item -LiteralPath $portableExe).VersionInfo.ProductVersion
$report = [System.Collections.Generic.List[string]]::new()

function Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "FAIL: $Description" }
    $report.Add("PASS: $Description")
    Write-Output "PASS: $Description"
}
function Run-InstallerProcess([string]$File, [string[]]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Exit code $($process.ExitCode): $File" }
}
function Install-TestApp([string]$LogName) {
    Run-InstallerProcess $testInstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/LANG=korean',('/LOG="' + (Join-Path $testRoot $LogName) + '"'))
}
function Uninstall-TestApp([switch]$DeleteSettings, [string]$LogName) {
    $uninstaller = Join-Path $installDir 'unins000.exe'
    $uninstallArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $testRoot $LogName) + '"'))
    if ($DeleteSettings) { $uninstallArguments += '/DELETESETTINGS=1' }
    Run-InstallerProcess $uninstaller $uninstallArguments
    # Inno may finish self-deletion in its temporary helper after the main process exits.
    for ($attempt = 0; $attempt -lt 50 -and (Test-Path -LiteralPath $uninstaller); $attempt++) {
        Start-Sleep -Milliseconds 100
    }
}

New-Item -ItemType Directory -Force $testRoot,$settingsDir | Out-Null
Check (-not (Test-Path -LiteralPath $registryPath)) 'Unique test app ID has no existing registration'
Check (-not (Test-Path -LiteralPath $shortcutPath)) 'Unique test shortcut does not already exist'
try {
    & (Join-Path $PSScriptRoot 'Write-FileAssociations.ps1')
    & $CompilerPath "/DRepoRoot=$projectRoot" "/DRequiredPrivileges=lowest" "/DApplicationId=$appId" "/DApplicationName=LightImageViewer Installer Test" "/DApplicationRegistrationName=$associationName" "/DAssociationExtensionPrefix=$extensionPrefix" "/DInstallFolder=$installDir" "/DSettingsFolder=$settingsDir" "/DStartMenuFolder=$groupName" '/DOutputFilename=LightImageViewer-Setup-Test' "/O$testRoot" (Join-Path $projectRoot 'installer/ImageViewer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Test installer compilation failed.' }
    # Use synthetic extensions: never alter real image associations during tests.
    New-Item -Path "$foreignExtensionPath\OpenWithProgids" -Force | Out-Null
    Set-Item -LiteralPath $foreignExtensionPath -Value 'Other.Image'
    New-ItemProperty -LiteralPath "$foreignExtensionPath\OpenWithProgids" -Name 'Other.Image' -Value '' -PropertyType String | Out-Null
    $settingsText = '{"Left":100,"Top":120,"Width":900,"Height":700,"Maximized":false}'
    Set-Content -LiteralPath (Join-Path $settingsDir 'window.json') -Value $settingsText
    Set-Content -LiteralPath (Join-Path $settingsDir 'error.log') -Value 'test log'
    Set-Content -LiteralPath (Join-Path $settingsDir 'unrelated-user-file.txt') -Value 'must remain'
    Install-TestApp 'install.log'
    Check ((Get-Content -LiteralPath (Join-Path $testRoot 'install.log') -Raw) -match 'Administrative install mode: No') 'Installer runs without administrative install mode'
    $installLog = Get-Content -LiteralPath (Join-Path $testRoot 'install.log') -Raw
    $windowsBuild = [int](Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name CurrentBuildNumber)
    $settingsTarget = 'ms-settings:defaultapps'
    if ($windowsBuild -ge 22000) { $settingsTarget += '?registeredAppUser=' + $appId }
    Check ($installLog.Contains('Default apps settings target: ' + $settingsTarget)) 'Default apps link targets the registered app and installation scope'
    Check (-not $installLog.Contains('Opening default apps settings:')) 'Silent installation does not launch Windows Settings'
    Check (Test-Path -LiteralPath (Join-Path $installDir 'LightImageViewer.exe')) 'App installed into designated current-user test directory'
    Check ((Get-FileHash -LiteralPath (Join-Path $installDir 'LightImageViewer.exe') -Algorithm SHA256).Hash -eq $portableHash) 'Installed EXE matches portable EXE byte for byte'
    Check (Test-Path -LiteralPath $registryPath) 'Installed apps entry registered in HKCU'
    Check (-not (Test-Path -LiteralPath $machineRegistryPath)) 'No machine-wide uninstall registration'
    $registration = Get-ItemProperty -LiteralPath $registryPath
    Check ($registration.DisplayVersion -eq $expectedVersion) 'Installed apps version is correct'
    Check ($registration.UninstallString -like '*unins000.exe*') 'Uninstall command registered'
    Check ((Get-Item -LiteralPath $registeredAppsPath).GetValue($appId) -eq $capabilitiesValue) 'RegisteredApplications points to app capabilities'
    Check ((Get-Item -LiteralPath $capabilitiesPath).GetValue('ApplicationName') -eq $appId) 'Default apps candidate has an application name'
    $associations = Get-Item -LiteralPath "$capabilitiesPath\FileAssociations"
    Check (@($associations.GetValueNames()).Count -eq $extensions.Count) 'Capabilities include exactly the supported extension count'
    $supportedTypes = Get-Item -LiteralPath "$applicationPath\SupportedTypes"
    foreach ($extension in $extensions) {
        if ($associations.GetValue($extension) -ne $progId) { throw "Missing capability: $extension" }
        if ($supportedTypes.GetValueNames() -notcontains $extension) { throw "Missing SupportedTypes: $extension" }
        $openWith = Get-Item -LiteralPath "HKCU:\Software\Classes\$extension\OpenWithProgids"
        if ($openWith.GetValueNames() -notcontains $progId) { throw "Missing OpenWithProgids: $extension" }
    }
    Check ($true) 'Every supported format is registered for Default apps and Open with'
    $expectedCommand = '"' + (Join-Path $installDir 'LightImageViewer.exe') + '" "%1"'
    Check ((Get-Item -LiteralPath "$progIdPath\shell\open\command").GetValue('') -eq $expectedCommand) 'ProgID open command quotes executable and image paths'
    Check ((Get-Item -LiteralPath "$applicationPath\shell\open\command").GetValue('') -eq $expectedCommand) 'Applications open command quotes executable and image paths'
    Check ((Get-Item -LiteralPath $foreignExtensionPath).GetValue('') -eq 'Other.Image') 'Installation preserves the existing extension default'
    Check (Test-Path -LiteralPath $shortcutPath) 'Current-user Start menu shortcut created'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    Check ($shortcut.TargetPath -eq (Join-Path $installDir 'LightImageViewer.exe')) 'Shortcut targets installed app'
    Install-TestApp 'upgrade.log'
    Check ((Get-Content -LiteralPath (Join-Path $settingsDir 'window.json') -Raw).Trim() -eq $settingsText) 'Reinstall preserves user settings'
    Check (@(Get-ChildItem -LiteralPath $installDir -Filter 'unins*.exe').Count -eq 1) 'Reinstall uses one existing uninstaller'
    Check ((Get-Item -LiteralPath "$capabilitiesPath\FileAssociations").GetValue($extensions[0]) -eq $progId) 'Reinstall retains file association registration'
    Uninstall-TestApp -LogName 'uninstall-keep.log'
    Check (-not (Test-Path -LiteralPath (Join-Path $installDir 'LightImageViewer.exe'))) 'Uninstall removes installed EXE'
    Check (-not (Test-Path -LiteralPath $registryPath)) 'Uninstall removes installed apps entry'
    Check (-not (Test-Path -LiteralPath $shortcutPath)) 'Uninstall removes Start menu shortcut'
    Check (Test-Path -LiteralPath (Join-Path $settingsDir 'window.json')) 'Default uninstall preserves settings'
    Check (Test-Path -LiteralPath (Join-Path $settingsDir 'error.log')) 'Default uninstall preserves error log'
    Check ($null -eq (Get-Item -LiteralPath $registeredAppsPath).GetValue($appId)) 'Uninstall removes RegisteredApplications value'
    Check (-not (Test-Path -LiteralPath $capabilitiesPath)) 'Uninstall removes app capabilities'
    Check (-not (Test-Path -LiteralPath $progIdPath)) 'Uninstall removes app ProgID'
    Check (-not (Test-Path -LiteralPath $applicationPath)) 'Uninstall removes Applications registration'
    foreach ($extension in $extensions) {
        $openWith = Get-Item -LiteralPath "HKCU:\Software\Classes\$extension\OpenWithProgids" -ErrorAction SilentlyContinue
        if ($openWith -and $openWith.GetValueNames() -contains $progId) { throw "Stale OpenWith entry: $extension" }
    }
    Check ($true) 'Uninstall removes all app OpenWithProgids values'
    Check ((Get-Item -LiteralPath $foreignExtensionPath).GetValue('') -eq 'Other.Image') 'Uninstall preserves existing extension default'
    Check ((Get-Item -LiteralPath "$foreignExtensionPath\OpenWithProgids").GetValueNames() -contains 'Other.Image') 'Uninstall preserves other apps OpenWith entries'
    Install-TestApp 'reinstall.log'
    Uninstall-TestApp -DeleteSettings -LogName 'uninstall-delete.log'
    Check (-not (Test-Path -LiteralPath (Join-Path $settingsDir 'window.json'))) 'Explicit delete choice removes window settings'
    Check (-not (Test-Path -LiteralPath (Join-Path $settingsDir 'error.log'))) 'Explicit delete choice removes error log'
    Check (Test-Path -LiteralPath (Join-Path $settingsDir 'unrelated-user-file.txt')) 'Unknown user files are preserved'
    Check (-not (Test-Path -LiteralPath $registryPath)) 'Second uninstall removes registration'
    Check (-not (Test-Path -LiteralPath $shortcutPath)) 'Second uninstall removes shortcut'
    Check ((Get-FileHash -LiteralPath $portableExe -Algorithm SHA256).Hash -eq $portableHash) 'Original portable EXE is unchanged'
} finally {
    # Cleanup uses only the generated, uniquely identified test app's own uninstaller.
    if (Test-Path -LiteralPath (Join-Path $installDir 'unins000.exe')) {
        Uninstall-TestApp -LogName 'cleanup.log'
    }
    foreach ($extension in $extensions) {
        if (-not $extension.StartsWith('.' + $extensionPrefix, [StringComparison]::Ordinal)) { throw 'Unexpected test extension cleanup target' }
        $testExtensionPath = "HKCU:\Software\Classes\$extension"
        if (Test-Path -LiteralPath $testExtensionPath) { Remove-Item -LiteralPath $testExtensionPath -Recurse -Force }
    }
    $report | Set-Content -LiteralPath (Join-Path $testRoot 'results.txt')
    Write-Output "Report: $testRoot"
}

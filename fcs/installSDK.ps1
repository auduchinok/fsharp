# Ensures that .net core is up to date.
# first get the required version from global.json
$json = ConvertFrom-Json (Get-Content "$PSScriptRoot/global.json" -Raw)
$required_version = $json.sdk.version

# Running dotnet --version stupidly fails if the required SDK version is higher 
# than the currently installed version. So move global.json out the way 
# and then put it back again 
Rename-Item "$PSScriptRoot/global.json" "$PSScriptRoot/global.json.bak"
$current_version = (dotnet --version)
Rename-Item "$PSScriptRoot/global.json.bak" "$PSScriptRoot/global.json"
Write-Host "Required .NET version: $required_version, Installed: $current_version"

if ($current_version -lt $required_version) {
  # Current installed version is too low.
  # Install new version as a local only dependency. 
  $urlCurrent = "https://dotnetcli.blob.core.windows.net/dotnet/Sdk/$required_version/dotnet-sdk-$required_version-win-x64.zip"
  Write-Host "Installing .NET Core $required_version from $urlCurrent"
  $env:DOTNET_INSTALL_DIR = "$PSScriptRoot/.dotnetsdk"
  New-Item -Type Directory $env:DOTNET_INSTALL_DIR -Force | Out-Null
  (New-Object System.Net.WebClient).DownloadFile($urlCurrent, "dotnet.zip")
  Write-Host "Unzipping to $env:DOTNET_INSTALL_DIR"
  Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::ExtractToDirectory("dotnet.zip", $env:DOTNET_INSTALL_DIR)
  
  Write-Host "Using .NET SDK from $env:DOTNET_INSTALL_DIR"
  $env:PATH = "$env:DOTNET_INSTALL_DIR;$env:PATH"
}

# Run in an elevated PowerShell. Configure HTTPS certificate settings before starting.
param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$DataDirectory, [string]$Urls = 'https://0.0.0.0:7443')
$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path -LiteralPath $Executable).Path
$dataPath = [IO.Path]::GetFullPath($DataDirectory)
New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
if (-not [Diagnostics.EventLog]::SourceExists('RestaurantPOS')) { New-EventLog -LogName Application -Source RestaurantPOS }
New-Service -Name RestaurantPOS -DisplayName '335 Restaurant POS' -BinaryPathName ('"' + $exePath + '"') -StartupType Automatic
New-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\RestaurantPOS' -Name Environment -PropertyType MultiString -Value @('ASPNETCORE_ENVIRONMENT=Production', "Data__Directory=$dataPath", "ASPNETCORE_URLS=$Urls") -Force | Out-Null
sc.exe failure RestaurantPOS reset= 86400 actions= restart/5000/restart/15000/restart/60000
if ($LASTEXITCODE -ne 0) { throw 'Could not configure service recovery' }
Write-Host 'Installed but not started. Configure certificate, data/key ACLs and Setup__Token, then Start-Service RestaurantPOS.'

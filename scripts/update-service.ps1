param([Parameter(Mandatory)][string]$CurrentExecutable, [Parameter(Mandatory)][string]$NewExecutable, [Parameter(Mandatory)][string]$DataDirectory, [Parameter(Mandatory)][string]$BackupDestination)
$ErrorActionPreference = 'Stop'
$newPath = (Resolve-Path -LiteralPath $NewExecutable).Path
Stop-Service RestaurantPOS
& "$PSScriptRoot\backup-database.ps1" -Executable $CurrentExecutable -DataDirectory $DataDirectory -Destination $BackupDestination
# Publish into a separate version directory first. Existing application files remain available for rollback.
sc.exe config RestaurantPOS binPath= ('"' + $newPath + '"')
if ($LASTEXITCODE -ne 0) { throw 'Service update failed; previous binary remains available' }
Start-Service RestaurantPOS

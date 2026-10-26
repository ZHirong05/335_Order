param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$DataDirectory, [Parameter(Mandatory)][string]$Backup)
$ErrorActionPreference = 'Stop'
$service = Get-Service -Name RestaurantPOS -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') { throw 'Stop RestaurantPOS before restoring' }
$sourcePath = (Resolve-Path -LiteralPath $Backup).Path
$dataPath = [IO.Path]::GetFullPath($DataDirectory)
$targetPath = [IO.Path]::GetFullPath((Join-Path $dataPath 'restaurant.db'))
if ([IO.Path]::GetDirectoryName($targetPath) -ne $dataPath.TrimEnd('\')) { throw 'Target is outside the data directory' }
if ($sourcePath -eq $targetPath) { throw 'Backup and target must differ' }
& $Executable verify $sourcePath
if ($LASTEXITCODE -ne 0) { throw 'Backup integrity verification failed' }
$stamp = Get-Date -Format yyyyMMddHHmmssfff
# Preserve database and WAL companions together for rollback; never delete a live WAL.
foreach ($suffix in @('', '-wal', '-shm')) {
    $filePath = $targetPath + $suffix
    if (Test-Path -LiteralPath $filePath) { Move-Item -LiteralPath $filePath -Destination ($filePath + '.before-restore-' + $stamp) }
}
Copy-Item -LiteralPath $sourcePath -Destination $targetPath
Write-Host 'Restored. Start the service and verify login, orders and kitchen before resuming work.'

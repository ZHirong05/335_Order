param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$DataDirectory, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$env:Data__Directory = [IO.Path]::GetFullPath($DataDirectory)
& $Executable backup ([IO.Path]::GetFullPath($Destination))
if ($LASTEXITCODE -ne 0) { throw 'Online SQLite backup failed' }

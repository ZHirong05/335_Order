param([string]$Output = "$PSScriptRoot\..\artifacts\win-x64", [switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$selfContained = (-not $FrameworkDependent.IsPresent).ToString().ToLowerInvariant()
dotnet publish "$PSScriptRoot\..\src\RestaurantPOS.Web" -c Release -r win-x64 --self-contained $selfContained -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

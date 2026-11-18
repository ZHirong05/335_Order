$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runtime = Join-Path $env:LOCALAPPDATA '335_Order\dotnet'
$env:DOTNET_ROOT = $runtime
$env:PATH = "$runtime;$env:PATH"
$url = 'http://127.0.0.1:5083/dashboard'
$mutex = New-Object System.Threading.Mutex($false, 'Local\335_Order_Startup')
$locked = $false
function Test-Ready {
    try {
        $response = Invoke-WebRequest 'http://127.0.0.1:5083/api/health' -UseBasicParsing -TimeoutSec 5 -Proxy $null
        return $response.StatusCode -eq 200 -and $response.Content.Trim() -eq 'Healthy'
    } catch { return $false }
}
function Test-Port {
    $client = New-Object System.Net.Sockets.TcpClient
    try { return $client.ConnectAsync('127.0.0.1', 5083).Wait(1000) -and $client.Connected } catch { return $false } finally { $client.Dispose() }
}
try {
    try { $locked = $mutex.WaitOne(120000) } catch [System.Threading.AbandonedMutexException] { $locked = $true }
    if (!$locked) { throw 'Another startup is still in progress. Please try again shortly.' }
    if (!(Test-Ready)) {
        $logs = Join-Path $root '.local'
        New-Item -ItemType Directory -Path $logs -Force | Out-Null
        $process = $null
        if (!(Test-Port)) {
            $env:ASPNETCORE_ENVIRONMENT = 'Development'
            $project = Join-Path $root 'src\RestaurantPOS.Web'
            $dll = Join-Path $project 'bin\Debug\net10.0\RestaurantPOS.Web.dll'
            if (!(Test-Path $dll)) { throw 'Application build is missing. Run the project build first.' }
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
            $outLog = Join-Path $logs "server-$stamp.log"
            $errLog = Join-Path $logs "server-$stamp-error.log"
            $process = Start-Process -FilePath (Join-Path $runtime 'dotnet.exe') -ArgumentList @(('"' + $dll + '"'), '--urls', 'http://0.0.0.0:5083') -WorkingDirectory $project -WindowStyle Hidden -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru
        }
        $ready = $false
        for ($i = 0; $i -lt 60; $i++) {
            if (Test-Ready) { $ready = $true; break }
            if ($process -and $process.HasExited) {
                if (Test-Ready) { $ready = $true; break }
                throw "335_Order could not start. See $errLog and $outLog."
            }
            Start-Sleep -Seconds 1
        }
        if (!$ready) { throw "335_Order is not responding. Port 5083 may be occupied or startup is incomplete. See logs in $logs." }
    }
    Start-Process $url
} catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($_.Exception.Message, '335_Order startup') | Out-Null
    exit 1
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}

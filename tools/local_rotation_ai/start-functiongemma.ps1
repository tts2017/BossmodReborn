param(
    [int]$Port = 8089,
    [string]$Model = "google/functiongemma-270m-it"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Venv = Join-Path $Root ".venv"
$Python = Join-Path $Venv "Scripts\python.exe"
$Mutex = [Threading.Mutex]::new($false, "Local\BossModFunctionGemma-$Port")
$HasMutex = $false

try {
    $HasMutex = $Mutex.WaitOne(0)
    if (-not $HasMutex) {
        return
    }

    $Client = [Net.Sockets.TcpClient]::new()
    try {
        $Connect = $Client.ConnectAsync([Net.IPAddress]::Loopback, $Port)
        if ($Connect.Wait(200) -and $Client.Connected) {
            return
        }
    }
    catch {
    }
    finally {
        $Client.Dispose()
    }

    if (-not (Test-Path -LiteralPath $Python)) {
        py -3 -m venv $Venv
    }

    & $Python -m pip install --disable-pip-version-check -r (Join-Path $Root "requirements.txt")
    & $Python (Join-Path $Root "functiongemma_server.py") --model $Model --port $Port
}
finally {
    if ($HasMutex) {
        $Mutex.ReleaseMutex()
    }
    $Mutex.Dispose()
}

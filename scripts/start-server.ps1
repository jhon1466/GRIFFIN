<#
.SYNOPSIS
    Compila e inicia el servidor Griffin.

.DESCRIPTION
    1. Comprueba que existe el SDK de .NET 8.
    2. Comprueba que los puertos 80, 443 y 7001 están libres.
    3. Avisa si PostgreSQL no responde (host y puerto de ConnectionStrings:Postgres de Griffin\appsettings.json).
    4. Compila Griffin en Release y lo ejecuta en esta ventana. Se detiene con Ctrl+C.

.PARAMETER NoBuild
    No compila: ejecuta lo que ya haya en Griffin\bin\Release\net8.0.
#>
param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'Griffin\Griffin.csproj'
$serverDirectory = Join-Path $repoRoot 'Griffin\bin\Release\net8.0'
$serverDll = Join-Path $serverDirectory 'Griffin.dll'
$requiredPorts = @(80, 443, 7001)

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'No encuentro dotnet. Instala el SDK de .NET 8: https://dotnet.microsoft.com/download/dotnet/8.0'
}
if (-not ((& dotnet --list-sdks) -match '^(8|9|10)\.')) {
    throw 'Hace falta el SDK de .NET 8 o superior (dotnet --list-sdks).'
}

$busy = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in $requiredPorts })
if ($busy.Count -gt 0) {
    $list = ($busy | Sort-Object LocalPort -Unique | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort) (PID $($_.OwningProcess))" }) -join ', '
    throw "Los puertos necesarios ya están en uso: $list. Cierra ese programa (¿otra copia de Griffin?) y vuelve a ejecutar el script."
}

# PostgreSQL: solo un aviso, la conexión real la hace el servidor
$settings = Get-Content -LiteralPath (Join-Path $repoRoot 'Griffin\appsettings.json') -Raw | ConvertFrom-Json
$connection = [string]$settings.ConnectionStrings.Postgres
$dbHost = if ($connection -match 'Host=([^;]+)') { $Matches[1] } else { 'localhost' }
$dbPort = if ($connection -match 'Port=(\d+)') { [int]$Matches[1] } else { 5432 }
$reachable = $false
try {
    $client = [System.Net.Sockets.TcpClient]::new()
    $reachable = $client.ConnectAsync($dbHost, $dbPort).Wait(2000)
    $client.Dispose()
} catch { }
if (-not $reachable) {
    Write-Warning "PostgreSQL no responde en ${dbHost}:${dbPort}. Inícialo (y revisa Griffin\appsettings.json) o el servidor no podrá guardar cuentas."
}

if (-not $NoBuild) {
    Write-Host 'Compilando Griffin (Release)...'
    & dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) {
        throw 'La compilación falló. Corrige los errores mostrados antes de iniciar el servidor.'
    }
}

if (-not (Test-Path -LiteralPath $serverDll)) {
    throw "No existe $serverDll. Ejecuta el script sin -NoBuild."
}

# Token de la API GM (la usa Griffin GM Tools): se genera la primera vez y se reutiliza en cada arranque.
$gmTokenPath = Join-Path $repoRoot 'GRIFFIN_GM_TOKEN.txt'
if (Test-Path -LiteralPath $gmTokenPath) {
    $gmToken = [System.IO.File]::ReadAllText($gmTokenPath).Trim()
} elseif (-not [string]::IsNullOrWhiteSpace($env:GRIFFIN_GM_TOKEN)) {
    $gmToken = $env:GRIFFIN_GM_TOKEN.Trim()
    [System.IO.File]::WriteAllText($gmTokenPath, $gmToken, [System.Text.UTF8Encoding]::new($false))
} else {
    $randomBytes = New-Object byte[] 32
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($randomBytes) } finally { $generator.Dispose() }
    $gmToken = [Convert]::ToBase64String($randomBytes)
    [System.IO.File]::WriteAllText($gmTokenPath, $gmToken, [System.Text.UTF8Encoding]::new($false))
}
if ($gmToken.Length -lt 24) {
    throw "El token de $gmTokenPath tiene menos de 24 caracteres. Corrígelo o borra el archivo para generar uno nuevo."
}
$env:GRIFFIN_GM_TOKEN = $gmToken
Write-Host "Token de la API GM listo (para Griffin GM Tools): $gmTokenPath"

Write-Host 'Iniciando Griffin: HTTPS 443 y 80 (SDK) y TCP 7001 (juego). Ctrl+C para detenerlo.'
Push-Location $serverDirectory
try {
    & dotnet .\Griffin.dll
} finally {
    Pop-Location
}

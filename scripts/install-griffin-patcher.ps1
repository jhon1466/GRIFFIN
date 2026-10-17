param(
    # Folder with the ready-to-use build (griffin_patcher.dll + griffin_launcher.exe); by default <repo>\GriffinPatcher
    [string]$SourceDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'GriffinPatcher'),
    [string]$GameDirectory = 'C:\Program Files\GF2Exilium\GF2 Game'
)

# Installs Griffin Patcher in the game folder. The two files come from the Releases page (or cargo build --release in <repo>\GriffinPatcher).

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Abre PowerShell como administrador y vuelve a ejecutar este script. Necesita escribir en Program Files.'
}

if (-not (Test-Path -LiteralPath (Join-Path $GameDirectory 'GF2_Exilium.exe'))) {
    throw "No encuentro GF2_Exilium.exe en $GameDirectory. No se copió ningún archivo."
}

$expected = @{
    'griffin_patcher.dll' = 'CF108F5F2D7A603453519240476819072DB24520C4F1FAD3344B8671F24306BA'
    'griffin_launcher.exe' = 'EA9D7869AAF1BA2421F5038D1E1F333DC49C95FC50C04DCAE30E94FAFE5E57D0'
}

foreach ($name in $expected.Keys) {
    $source = Join-Path $SourceDirectory $name
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Falta $source. Descarga griffin_patcher.dll y griffin_launcher.exe de la sección Releases del repositorio y colócalos en la carpeta GriffinPatcher\ (o compílalos con cargo build --release en GriffinPatcher\)."
    }

    $actualHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    if ($actualHash -ne $expected[$name]) {
        throw "El SHA-256 de $name no coincide con el esperado. Si recompilaste el patcher, actualiza los hashes de este script. No se copió ningún archivo."
    }
}

foreach ($name in $expected.Keys) {
    Copy-Item -LiteralPath (Join-Path $SourceDirectory $name) -Destination $GameDirectory -Force
}

Write-Host 'Archivos copiados. Inicia griffin_launcher.exe como administrador cuando el servidor esté encendido.'

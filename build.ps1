# Gera o executável para distribuição em dist\.
#   .\build.ps1              -> dist\UsageCockpit.exe (portátil, não precisa instalar o .NET)
#   .\build.ps1 -Installer   -> também dist\UsageCockpit-Setup-<versão>.exe (requer Inno Setup 6)
param(
    [switch]$Installer,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'UsageCockpit\UsageCockpit.csproj'
$dist = Join-Path $root 'dist'

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

Remove-Item -Recurse -Force $dist -ErrorAction SilentlyContinue
# Sem EnableCompressionInSingleFile: o exe fica maior (~160 MB), mas o app usa ~80 MB a menos de RAM,
# porque as bibliotecas não precisam ser descompactadas na memória. O instalador comprime tudo (lzma2).
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $dist
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou' }

Write-Host "Portatil: $dist\UsageCockpit.exe"

if ($Installer) {
    $iscc = @(
        (Get-Command iscc -ErrorAction SilentlyContinue).Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 nao encontrado. Instale com: winget install JRSoftware.InnoSetup' }

    & $iscc "/DAppVersion=$Version" "/DSourceDir=$dist" "/O$dist" (Join-Path $root 'installer\UsageCockpit.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup falhou' }
    Write-Host "Instalador: $dist\UsageCockpit-Setup-$Version.exe"
}

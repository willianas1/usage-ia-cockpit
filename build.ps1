# Gera os arquivos de distribuição em dist\.
#   .\build.ps1              -> dist\UsageIACockpit-Portable.exe (arquivo único, não precisa instalar o .NET)
#   .\build.ps1 -Installer   -> também dist\UsageIACockpit-Setup-<versão>.exe (requer Inno Setup 6)
param(
    [switch]$Installer,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'UsageCockpit\UsageCockpit.csproj'
$dist = Join-Path $root 'dist'
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=none', '-nologo')

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

Remove-Item -Recurse -Force $dist -ErrorAction SilentlyContinue

# Portátil: um único exe, compactado (~65 MB). Na primeira execução o .NET descompacta as
# bibliotecas na memória, o que custa um pouco mais de RAM que a versão instalada.
dotnet publish $project @common -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o (Join-Path $dist 'portable')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (portatil) falhou' }
Move-Item (Join-Path $dist 'portable\UsageIACockpit.exe') (Join-Path $dist 'UsageIACockpit-Portable.exe')
Remove-Item -Recurse -Force (Join-Path $dist 'portable')
Write-Host "Portatil: $dist\UsageIACockpit-Portable.exe"

if ($Installer) {
    $iscc = @(
        (Get-Command iscc -ErrorAction SilentlyContinue).Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 nao encontrado. Instale com: winget install JRSoftware.InnoSetup' }

    # Instalador: a pasta publicada (sem arquivo único) comprime melhor no lzma2 do Inno Setup,
    # e o app instalado abre mais rápido e usa menos memória que o exe único.
    $app = Join-Path $dist 'app'
    dotnet publish $project @common -p:Version=$Version -o $app
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (instalador) falhou' }

    & $iscc "/DAppVersion=$Version" "/DSourceDir=$app" "/O$dist" (Join-Path $root 'installer\UsageCockpit.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup falhou' }
    Remove-Item -Recurse -Force $app
    Write-Host "Instalador: $dist\UsageIACockpit-Setup-$Version.exe"
}

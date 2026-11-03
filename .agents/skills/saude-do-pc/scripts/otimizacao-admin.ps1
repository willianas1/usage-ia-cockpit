# Otimizacoes que exigem ADMINISTRADOR. A skill nunca roda este script: quem roda e voce.
# Como rodar: abra o PowerShell como Administrador e execute (caminho completo):
#   powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Repositorio\monitor-uso-ia\.agents\skills\saude-do-pc\scripts\otimizacao-admin.ps1"
# Partes (todas por padrao; para pular uma, use o parametro -Sem...):
#   Servicos       servicos de terceiros da lista em comum.ps1 -> Manual (so os que estao em Automatico)
#   WindowsTemp    esvazia C:\Windows\Temp e os downloads antigos do Windows Update
#   WinSxS         Dism /StartComponentCleanup (componentes antigos do Windows)
#   Hibernacao     powercfg /h /type reduced (mantem a Inicializacao Rapida, encolhe o hiberfil.sys)
#   Restauracao    so MOSTRA o espaco dos pontos de restauracao (limitar e uma decisao sua; comando no fim)
# Tudo e reversivel. O spacedesk nunca e alterado.
#   Reverter um servico: Set-Service -Name '<nome>' -StartupType Automatic
#   Hibernacao completa de novo: powercfg /h /type full

#Requires -RunAsAdministrator
param([switch]$SemServicos, [switch]$SemWindowsTemp, [switch]$SemWinSxS, [switch]$SemHibernacao, [switch]$SemRestauracao, [string[]]$ManterServicos = @())
. "$PSScriptRoot\comum.ps1"
$ManterServicos = @(Split-Lista $ManterServicos)

$livreAntes = (Get-Volume -DriveLetter C).SizeRemaining
$resumo = New-Object System.Collections.Generic.List[string]

if (-not $SemServicos) {
  Write-Host "`n=== Servicos de terceiros -> Manual ===" -ForegroundColor Cyan
  foreach ($c in $script:ServicosParaManual) {
    foreach ($svc in @(Get-Service -Name $c.Nome -ErrorAction SilentlyContinue)) {
      if ($svc.Name -like $script:NuncaMexer -or $ManterServicos -contains $svc.Name) { Write-Host "  (mantido) $($svc.Name)"; continue }
      if ($svc.StartType -ne 'Automatic') { Write-Host "  (ja esta $($svc.StartType)) $($svc.Name)"; continue }
      try { Set-Service -Name $svc.Name -StartupType Manual -ErrorAction Stop; Write-Host "  $($svc.Name) -> Manual  ($($c.Motivo))"; $resumo.Add("servico $($svc.Name) -> Manual") }
      catch { Write-Host "  [falhou] $($svc.Name): $($_.Exception.Message)" -ForegroundColor Yellow }
    }
  }
}

if (-not $SemWindowsTemp) {
  Write-Host "`n=== Temporarios do Windows ===" -ForegroundColor Cyan
  $antes = (Get-Volume -DriveLetter C).SizeRemaining
  Get-ChildItem 'C:\Windows\Temp' -Force -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-1) } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
  Stop-Service wuauserv -Force -ErrorAction SilentlyContinue
  Get-ChildItem 'C:\Windows\SoftwareDistribution\Download' -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
  Start-Service wuauserv -ErrorAction SilentlyContinue
  $gb = [math]::Round(((Get-Volume -DriveLetter C).SizeRemaining - $antes) / 1GB, 2)
  Write-Host "  ~$gb GB liberados"; $resumo.Add("temporarios do Windows: ~$gb GB")
}

if (-not $SemWinSxS) {
  Write-Host "`n=== Limpeza de componentes do Windows (WinSxS) - pode levar alguns minutos ===" -ForegroundColor Cyan
  $antes = (Get-Volume -DriveLetter C).SizeRemaining
  Dism /Online /Cleanup-Image /StartComponentCleanup
  $gb = [math]::Round(((Get-Volume -DriveLetter C).SizeRemaining - $antes) / 1GB, 2)
  $resumo.Add("WinSxS: ~$gb GB")
}

if (-not $SemHibernacao) {
  Write-Host "`n=== Arquivo de hibernacao reduzido ===" -ForegroundColor Cyan
  $antes = (Get-Volume -DriveLetter C).SizeRemaining
  powercfg /h /type reduced
  $gb = [math]::Round(((Get-Volume -DriveLetter C).SizeRemaining - $antes) / 1GB, 2)
  $resumo.Add("hibernacao reduzida: ~$gb GB")
}

if (-not $SemRestauracao) {
  Write-Host "`n=== Espaco dos pontos de restauracao (so leitura) ===" -ForegroundColor Cyan
  vssadmin list shadowstorage
  Write-Host "  Se o 'Espaco usado' for grande, limite a 10 GB com:" -ForegroundColor Gray
  Write-Host "    vssadmin resize shadowstorage /for=C: /on=C: /maxsize=10GB" -ForegroundColor Gray
}

Write-Host "`n=== Memoria do kernel (pool nao paginado) ===" -ForegroundColor Cyan
Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory | ForEach-Object { "  agora: {0:N2} GB (normal: abaixo de 0,5 GB; acima de 1 GB, reinicie e observe)" -f ($_.PoolNonpagedBytes / 1GB) }

$total = [math]::Round(((Get-Volume -DriveLetter C).SizeRemaining - $livreAntes) / 1GB, 2)
Write-Host "`n=== Resumo ===" -ForegroundColor Green
$resumo | ForEach-Object { "  - $_" }
Write-Host ("  C: livre agora: {0:N1} GB (+{1} GB)" -f ((Get-Volume -DriveLetter C).SizeRemaining / 1GB), $total) -ForegroundColor Green
Write-Host "Reinicie o PC para concluir (encolhe o pagefile e zera a memoria presa no kernel)." -ForegroundColor Green

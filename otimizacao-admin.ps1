# Otimizacoes que exigem administrador.
# Como rodar: abra o PowerShell como Administrador e execute:
#   powershell -ExecutionPolicy Bypass -File "<caminho>\otimizacao-admin.ps1"
# Tudo aqui e reversivel. spacedesk NAO e alterado.

#Requires -RunAsAdministrator

Write-Host "`n=== 1. Servicos de terceiros -> Manual (iniciam so quando algo precisar) ===" -ForegroundColor Cyan
$manual = @(
  'CCleaner7',                       # CCleaner em segundo plano (redundante)
  'HPPrintScanDoctorService',        # HP Print and Scan Doctor
  'aspnet_state',                    # ASP.NET State Service (legado)
  'SQLWriter',                       # SQL Server VSS Writer (legado)
  'postgresql-x64-18',               # PostgreSQL: inicie com 'Start-Service postgresql-x64-18' quando for usar
  'SupportAssistAgent',              # Dell SupportAssist
  'Dell SupportAssist Remediation',  # Dell SupportAssist Remediation
  'DellTechHub'                      # Dell TechHub
  # Opcionais - descomente se NAO usar:
   'AnyDesk',                       # deixe Automatico se voce acessa este PC remotamente sem ninguem na frente
   'DellPairService',               # Dell Pair (pareamento de perifericos Dell)
   'DPMService'                     # Dell Peripheral Manager (config. de mouse/teclado Dell)
)
foreach ($s in $manual) {
  $svc = Get-Service -Name $s -ErrorAction SilentlyContinue
  if (-not $svc) { Write-Host "  (nao encontrado) $s"; continue }
  Set-Service -Name $s -StartupType Manual
  Write-Host "  $s -> Manual (era $($svc.StartType))"
}
# Para reverter um servico: Set-Service -Name '<nome>' -StartupType Automatic

Write-Host "`n=== 2. Limpeza de componentes do Windows (WinSxS) ===" -ForegroundColor Cyan
Dism /Online /Cleanup-Image /StartComponentCleanup

Write-Host "`n=== 3. Arquivo de hibernacao reduzido (mantem a Inicializacao Rapida) ===" -ForegroundColor Cyan
powercfg /h /type reduced

Write-Host "`n=== 4. Espaco usado por pontos de restauracao ===" -ForegroundColor Cyan
vssadmin list shadowstorage
# Se o "Espaco usado" acima for grande, limite a 10 GB descomentando:
# vssadmin resize shadowstorage /for=C: /on=C: /maxsize=10GB

Write-Host "`n=== 5. Memoria do kernel (pool nao paginado) ===" -ForegroundColor Cyan
Write-Host "  (o pool estava em 2,4 GB; o normal e < 0,5 GB. Rode de novo 1-2 dias apos reiniciar para ver se volta a crescer)"
Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory | ForEach-Object { "  Pool nao paginado agora: {0:N2} GB" -f ($_.PoolNonpagedBytes/1GB) }

Write-Host "`nC: livre: {0:N1} GB" -f ((Get-Volume C).SizeRemaining/1GB) -ForegroundColor Green
Write-Host "Pronto. Reinicie o PC para concluir (isso tambem encolhe o pagefile de 35 GB e zera o pool do kernel)." -ForegroundColor Green

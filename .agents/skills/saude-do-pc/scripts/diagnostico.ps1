# Diagnostico da saude do PC. SOMENTE LEITURA: nao altera, nao encerra e nao apaga nada.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File diagnostico.ps1 [-Rapido] [-Profundo]
#   -Rapido    pula a medicao das pastas grandes (Chrome, .cache, .codex), que pode levar 1-2 min.
#   -Profundo  tambem lista as maiores pastas do perfil e do AppData (para disco cheio; +2 a 5 min).
# Saida: resumo no console e um JSON completo em %TEMP%\saude-pc\diagnostico-<data>.json (ultima linha: JSON=<caminho>).

param([switch]$Rapido, [switch]$Profundo)
$ErrorActionPreference = 'SilentlyContinue'
. "$PSScriptRoot\comum.ps1"

$L = $script:Limites
$achados = New-Object System.Collections.Generic.List[object]
function Add-Achado($code, $nivel, $tipo, $titulo, $detalhe, $acao) {
  $achados.Add([pscustomobject]@{ Code = $code; Nivel = $nivel; Tipo = $tipo; Titulo = $titulo; Detalhe = $detalhe; Acao = $acao })
}
function Nivel($v, $warn, $danger) { if ($v -ge $danger) { 'danger' } elseif ($v -ge $warn) { 'warn' } else { $null } }

# ---------- Sistema ----------
$cs = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem
$cpuInfo = Get-CimInstance Win32_Processor | Select-Object -First 1
$uptime = (Get-Date) - $os.LastBootUpTime
$sistema = [pscustomobject]@{
  Modelo = "$($cs.Manufacturer) $($cs.Model)"; CPU = $cpuInfo.Name.Trim(); Nucleos = $cpuInfo.NumberOfCores; Threads = $cpuInfo.NumberOfLogicalProcessors
  RamGB = [math]::Round($cs.TotalPhysicalMemory / 1GB, 1); Windows = "$($os.Caption) $($os.BuildNumber)"
  LigadoDias = [math]::Round($uptime.TotalDays, 1); Admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------- CPU (5 amostras de 1,5 s) ----------
$amostras = 1..5 | ForEach-Object {
  $p = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'"
  $pi = Get-CimInstance Win32_PerfFormattedData_Counters_ProcessorInformation -Filter "Name='_Total'"
  Start-Sleep -Milliseconds 1500
  [pscustomobject]@{ Uso = [double]$p.PercentProcessorTime; Desempenho = [double]$pi.PercentProcessorPerformance }
}
$cpuUso = [math]::Round(($amostras.Uso | Measure-Object -Average).Average)
$cpuDes = [math]::Round(($amostras.Desempenho | Measure-Object -Average).Average)
$cpu = [pscustomobject]@{ UsoPct = $cpuUso; DesempenhoPct = $cpuDes; BaseMHz = $cpuInfo.MaxClockSpeed; MHzEstimado = [math]::Round($cpuInfo.MaxClockSpeed * $cpuDes / 100) }

# ---------- Energia ----------
$energia = Get-ModoEnergia
if ($energia.Tomada -eq 'eficiencia') {
  Add-Achado 'power_efficiency' 'warn' 'auto' 'Modo de energia em eficiencia na tomada' "A CPU fica limitada (~$($cpu.MHzEstimado) MHz agora, desempenho $cpuDes%)." 'Energia'
}
$n = Nivel $cpuUso $L.CpuWarn $L.CpuDanger
if ($n) { Add-Achado 'cpu_high' $n 'manual' "CPU em $cpuUso% durante a medicao" 'Veja os apps mais pesados em CPU abaixo; feche o que nao usa.' $null }

# ---------- Memoria ----------
$med = Get-Medidas
$commitPct = [math]::Round(100 * $med.CommitGB / $med.CommitLimiteGB)
$pf = Get-CimInstance Win32_PageFileUsage | ForEach-Object { [pscustomobject]@{ Arquivo = $_.Name; AlocadoGB = [math]::Round($_.AllocatedBaseSize / 1024, 1); UsoGB = [math]::Round($_.CurrentUsage / 1024, 1); PicoGB = [math]::Round($_.PeakUsage / 1024, 1) } }
$memoria = [pscustomobject]@{ DisponivelGB = $med.RamDisponivelGB; CommitGB = $med.CommitGB; CommitLimiteGB = $med.CommitLimiteGB; CommitPct = $commitPct; KernelNaoPaginadoGB = $med.KernelGB; Pagefile = $pf }
$n = Nivel $commitPct $L.CommitWarn $L.CommitDanger
if ($n) { Add-Achado 'memory_high' $n 'manual' "Memoria comprometida em $commitPct% ($(Fmt $med.CommitGB) de $(Fmt $med.CommitLimiteGB) GB)" 'Feche apps pesados (lista abaixo). No Chrome, ative Configuracoes > Desempenho > Economia de memoria.' $null }
$n = Nivel $med.KernelGB $L.KernelWarnGB $L.KernelDangerGB
if ($n) { Add-Achado 'kernel_pool_high' $n 'manual' "Memoria do kernel em $(Fmt $med.KernelGB 2) GB (normal: abaixo de 0,5 GB)" 'Reinicie. Se voltar a crescer em 1-2 dias, um driver esta vazando memoria (investigar com poolmon como administrador).' $null }
$pfTotal = ($pf | Measure-Object AlocadoGB -Sum).Sum
if ($pfTotal -gt $sistema.RamGB) { Add-Achado 'pagefile_big' 'warn' 'manual' "Arquivo de paginacao com $(Fmt $pfTotal) GB" 'Cresceu por falta de memoria. Reiniciar faz o Windows encolher; nao reduza na mao enquanto a memoria comprometida estiver alta.' $null }

# ---------- Tempo ligado ----------
$dias = [math]::Floor($uptime.TotalDays)
$n = Nivel $dias $L.UptimeWarnDias $L.UptimeDangerDias
if ($n) { Add-Achado 'uptime_long' $n 'manual' "Ligado ha $dias dias" 'Reinicie: libera memoria presa, encolhe o pagefile e conclui atualizacoes.' $null }

# ---------- Discos ----------
$discos = Get-Volume | Where-Object { $_.DriveLetter -and $_.DriveType -eq 'Fixed' -and $_.Size -gt 0 } | ForEach-Object {
  [pscustomobject]@{ Letra = "$($_.DriveLetter):"; TotalGB = [math]::Round($_.Size / 1GB, 1); LivreGB = [math]::Round($_.SizeRemaining / 1GB, 1); UsadoPct = [math]::Round(100 * ($_.Size - $_.SizeRemaining) / $_.Size) }
}
$fisicos = Get-PhysicalDisk | ForEach-Object { [pscustomobject]@{ Nome = $_.FriendlyName; Tipo = "$($_.MediaType) $($_.BusType)"; Saude = "$($_.HealthStatus)" } }
foreach ($d in $discos) {
  $n = Nivel $d.UsadoPct $L.DiscoWarn $L.DiscoDanger
  if ($n) { Add-Achado 'disk_low' $n 'misto' "Disco $($d.Letra) com $(100 - $d.UsadoPct)% livre ($(Fmt $d.LivreGB) GB)" 'Limpeza automatica de Temp e caches; com administrador, WinSxS, hibernacao e pontos de restauracao; e mover arquivos grandes para outro disco.' 'Temp,NpmCache' }
}
foreach ($f in $fisicos) { if ($f.Saude -and $f.Saude -ne 'Healthy') { Add-Achado 'disk_health' 'danger' 'manual' "Disco $($f.Nome) com saude '$($f.Saude)'" 'Faca backup e verifique o disco (ferramenta do fabricante).' $null } }

# ---------- Espaco recuperavel ----------
$u = $env:USERPROFILE; $la = $env:LOCALAPPDATA
$espaco = [ordered]@{
  TempTotalGB = Get-TamanhoGB $env:TEMP
  TempMaisDe1DiaGB = Get-TamanhoGB $env:TEMP -MaisAntigoQueDias 1 -Excluir $script:PastasExcluidasTemp
  NpmCacheGB = Get-TamanhoGB "$la\npm-cache" -Excluir '_npx'
  NpxGB = Get-TamanhoGB "$la\npm-cache\_npx"
  WindowsTempGB = Get-TamanhoGB 'C:\Windows\Temp'
  WindowsUpdateDownloadGB = Get-TamanhoGB 'C:\Windows\SoftwareDistribution\Download'
  LixeiraGB = Get-TamanhoGB 'C:\$Recycle.Bin'
  DownloadsGB = Get-TamanhoGB "$u\Downloads"
}
if (-not $Rapido) {
  $espaco.ChromeGB = Get-TamanhoGB "$la\Google\Chrome\User Data"
  $espaco.CacheUsuarioGB = Get-TamanhoGB "$u\.cache"
  $espaco.CodexGB = Get-TamanhoGB "$u\.codex"
}
$hib = Get-Item 'C:\hiberfil.sys' -Force; if ($hib) { $espaco.HiberfilGB = [math]::Round($hib.Length / 1GB, 1) }
$espaco = [pscustomobject]$espaco
# Maiores pastas do perfil (sem o AppData, medido a parte) e do AppData Local/Roaming.
$maioresPastas = @()
if ($Profundo) {
  $raizes = @(
    @{ Caminho = $u; Pular = @('AppData') }
    @{ Caminho = $la; Pular = @() }
    @{ Caminho = $env:APPDATA; Pular = @() }
  )
  $maioresPastas = @(foreach ($r in $raizes) {
    Get-ChildItem -LiteralPath $r.Caminho -Directory -Force | Where-Object { $r.Pular -notcontains $_.Name -and -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } |
      ForEach-Object { [pscustomobject]@{ Pasta = $_.FullName; GB = Get-TamanhoGB $_.FullName } }
  }) | Where-Object { $_.GB -ge 0.5 } | Sort-Object GB -Descending | Select-Object -First 15
}
if ($espaco.TempMaisDe1DiaGB -ge $L.TempGB) { Add-Achado 'temp_old' 'warn' 'auto' "Temp com $(Fmt $espaco.TempMaisDe1DiaGB) GB de arquivos com mais de 1 dia" 'Sobras de instaladores e downloads temporarios.' 'Temp' }
if ($espaco.NpmCacheGB -ge $L.NpmGB) { Add-Achado 'npm_cache' 'warn' 'auto' "Cache do npm com $(Fmt $espaco.NpmCacheGB) GB" 'E baixado de novo quando precisar.' 'NpmCache' }
if ($espaco.NpxGB -ge 1) { Add-Achado 'npx_cache' 'info' 'manual' "Pacotes do npx com $(Fmt $espaco.NpxGB) GB" "Sao pacotes baixados pelo npx, inclusive servidores MCP do Claude e do Codex. Com esses apps fechados, apague a pasta $la\npm-cache\_npx (volta a ser baixada quando precisar)." $null }
if ($espaco.LixeiraGB -ge 1) { Add-Achado 'recycle_bin' 'info' 'manual' "Lixeira com $(Fmt $espaco.LixeiraGB) GB" 'Esvazie a Lixeira se nao precisar do que esta la.' $null }
if ($espaco.DownloadsGB -ge 5) { Add-Achado 'downloads_big' 'info' 'manual' "Downloads com $(Fmt $espaco.DownloadsGB) GB" 'Revise e apague ou mova o que nao precisa (a skill nunca apaga seus arquivos).' $null }
if ($espaco.WindowsTempGB -ge 1 -or $espaco.WindowsUpdateDownloadGB -ge 1) { Add-Achado 'windows_temp' 'info' 'admin' "Temporarios do Windows: $(Fmt $espaco.WindowsTempGB) GB em Windows\Temp e $(Fmt $espaco.WindowsUpdateDownloadGB) GB de downloads do Windows Update" 'Precisa de administrador (script de administrador).' 'WindowsTemp' }

# ---------- Processos ----------
$todos = Get-Process
$porApp = $todos | Group-Object ProcessName | ForEach-Object {
  [pscustomobject]@{ App = $_.Name; Instancias = $_.Count; MemoriaGB = [math]::Round(($_.Group | Measure-Object PrivateMemorySize64 -Sum).Sum / 1GB, 2); CpuMinTotal = [math]::Round(($_.Group | Measure-Object CPU -Sum).Sum / 60, 1) }
}
$topMem = $porApp | Sort-Object MemoriaGB -Descending | Select-Object -First 10
# CPU agora: duas leituras de tempo de CPU com 3 s de intervalo.
$t0 = @{}; foreach ($p in $todos) { try { $t0[$p.Id] = $p.TotalProcessorTime.TotalSeconds } catch {} }
Start-Sleep 3
$cpuAgora = Get-Process | ForEach-Object { $s = $null; try { $s = $_.TotalProcessorTime.TotalSeconds } catch {}; if ($s -ne $null -and $t0.ContainsKey($_.Id)) { [pscustomobject]@{ App = $_.ProcessName; Seg = $s - $t0[$_.Id] } } } |
  Group-Object App | ForEach-Object { [pscustomobject]@{ App = $_.Name; CpuPct = [math]::Round(100 * ($_.Group | Measure-Object Seg -Sum).Sum / (3 * $sistema.Threads), 1) } } |
  Sort-Object CpuPct -Descending | Select-Object -First 8
$perf = Get-CimInstance Win32_PerfFormattedData_PerfOS_System
$contagem = [pscustomobject]@{ Processos = $todos.Count; Threads = $perf.Threads; Handles = ($todos | Measure-Object Handles -Sum).Sum }
$n = Nivel $contagem.Processos $L.ProcessosWarn $L.ProcessosDanger
if ($n) { Add-Achado 'processes_high' $n 'manual' "$($contagem.Processos) processos abertos" 'Feche apps e terminais esquecidos; reiniciar zera tudo.' $null }

$esquecidos = Get-ProcessosEsquecidos
$porId = @{}; Get-CimInstance Win32_Process | ForEach-Object { $porId[[int]$_.ProcessId] = $_ }
foreach ($e in $esquecidos) { $e | Add-Member -NotePropertyName MBArvore -NotePropertyValue (Get-MBArvore -Raiz $e.PID -PorId $porId) -Force }
$encerraveis = @($esquecidos | Where-Object Acao -eq 'encerrar')
if ($encerraveis.Count) {
  $mb = ($encerraveis | Measure-Object MBArvore -Sum).Sum
  Add-Achado 'stale_processes' 'warn' 'auto' "$($encerraveis.Count) grupos de processos esquecidos (~$(Fmt ($mb / 1024)) GB)" (($encerraveis | Group-Object Categoria | ForEach-Object { "$($_.Count)x $($_.Name)" }) -join '; ') 'Processos'
}
$avaliar = @($esquecidos | Where-Object Acao -eq 'avaliar')
if ($avaliar.Count) { Add-Achado 'orphan_helpers' 'info' 'auto' "$($avaliar.Count) auxiliares orfaos abertos ha mais de 1 dia" 'O programa que os abriu ja fechou. Encerrar so se voce confirmar.' 'Orfaos' }
$auxAntigos = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -in $script:NomesTerminais -and $_.CreationDate -lt (Get-Date).AddHours(-24) }).Count
if ($auxAntigos -ge $L.AuxiliaresWarn) { Add-Achado 'helpers_stale' 'warn' 'manual' "$auxAntigos terminais/auxiliares abertos ha mais de 1 dia" 'Feche abas de terminal que nao usa e reabra apps como o Codex de vez em quando.' $null }
$chrome = $porApp | Where-Object App -eq 'chrome'
if ($chrome -and $chrome.MemoriaGB -ge 6) { Add-Achado 'chrome_heavy' 'warn' 'manual' "Chrome com $($chrome.Instancias) processos e $(Fmt $chrome.MemoriaGB) GB" 'Ative a Economia de memoria, feche abas/perfis que nao usa e veja as abas pesadas com Shift+Esc no Chrome.' $null }

# ---------- Inicializacao e servicos ----------
$desab = @()
foreach ($k in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder') {
  $i = Get-Item $k; if ($i) { foreach ($nome in $i.Property) { $v = $i.GetValue($nome); if ($v -and $v[0] -eq 3) { $desab += $nome } } }
}
$inicializacao = Get-CimInstance Win32_StartupCommand | Where-Object { $desab -notcontains $_.Name } | Select-Object -ExpandProperty Name -Unique
$servicos = Get-CimInstance Win32_Service | ForEach-Object { $_ }
$candidatos = foreach ($c in $script:ServicosParaManual) {
  $servicos | Where-Object { $_.Name -like $c.Nome -and $_.StartMode -eq 'Auto' -and $_.Name -notlike $script:NuncaMexer } | ForEach-Object { [pscustomobject]@{ Servico = $_.Name; Estado = $_.State; Motivo = $c.Motivo } }
}
$candidatos = @($candidatos)
if ($candidatos.Count) { Add-Achado 'services_auto' 'info' 'admin' "$($candidatos.Count) servicos de terceiros iniciam sozinhos e podem ficar em Manual" (($candidatos.Servico) -join ', ') 'Servicos' }
$terceiros = $servicos | Where-Object { $_.State -eq 'Running' -and $_.PathName -and $_.PathName -notmatch '\\Windows\\(system32|SysWOW64)\\' } | Select-Object -ExpandProperty Name

# ---------- Eventos criticos (7 dias) ----------
$eventos = Get-WinEvent -FilterHashtable @{ LogName = 'System'; Level = 1, 2; StartTime = (Get-Date).AddDays(-7) } -MaxEvents 300 | Group-Object ProviderName | Sort-Object Count -Descending | Select-Object -First 6 | ForEach-Object { [pscustomobject]@{ Origem = $_.Name; Qtd = $_.Count } }
$telaAzul = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WER-SystemErrorReporting'; StartTime = (Get-Date).AddDays(-30) }).Count
if ($telaAzul -gt 0) { Add-Achado 'bsod' 'warn' 'manual' "$telaAzul tela(s) azul(is) nos ultimos 30 dias" 'Veja em Visualizador de Eventos > Sistema (WER-SystemErrorReporting); costuma ser driver.' $null }

# ---------- Saida ----------
$ordem = @{ danger = 0; warn = 1; info = 2 }
$resultado = [pscustomobject]@{
  Data = (Get-Date).ToString('s'); Sistema = $sistema; Cpu = $cpu; Energia = $energia; Memoria = $memoria; Discos = $discos; DiscosFisicos = $fisicos
  Espaco = $espaco; MaioresPastas = $maioresPastas; Contagem = $contagem; TopMemoria = $topMem; TopCpu = $cpuAgora; Esquecidos = $esquecidos
  Inicializacao = $inicializacao; ServicosCandidatos = $candidatos; ServicosTerceirosRodando = $terceiros; EventosCriticos7d = $eventos
  Achados = @($achados | Sort-Object { $ordem[$_.Nivel] })
}
$dir = Join-Path $env:TEMP 'saude-pc'; New-Item -ItemType Directory -Force $dir | Out-Null
$arquivo = Join-Path $dir ("diagnostico-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))
$resultado | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $arquivo -Encoding UTF8

"== {0} | {1} | {2} GB RAM | ligado ha {3} dias" -f $sistema.Modelo, $sistema.CPU, $sistema.RamGB, $sistema.LigadoDias
"CPU {0}% (desempenho {1}%, ~{2} MHz) | energia na tomada: {3}" -f $cpu.UsoPct, $cpu.DesempenhoPct, $cpu.MHzEstimado, $energia.Tomada
"RAM livre {0} GB | commit {1}/{2} GB ({3}%) | kernel {4} GB | processos {5}" -f $med.RamDisponivelGB, $med.CommitGB, $med.CommitLimiteGB, $commitPct, $med.KernelGB, $contagem.Processos
foreach ($d in $discos) { "Disco {0} {1}/{2} GB livres ({3}% usado)" -f $d.Letra, $d.LivreGB, $d.TotalGB, $d.UsadoPct }
if ($maioresPastas.Count) { "-- Maiores pastas --"; foreach ($p in $maioresPastas) { "{0,7} GB  {1}" -f (Fmt $p.GB), $p.Pasta } }
"-- Achados --"
foreach ($a in $resultado.Achados) { "[{0}/{1}] {2} :: {3}" -f $a.Nivel, $a.Tipo, $a.Titulo, $a.Detalhe }
"JSON=$arquivo"

# Funcoes e listas compartilhadas pelos scripts da skill saude-do-pc.
# Este arquivo so define coisas: nao le nem altera nada sozinho.
# Textos sem acento de proposito: o Windows PowerShell 5.1 le .ps1 sem BOM como ANSI.

# Servicos de terceiros que podem ficar em Manual (iniciam quando algo precisar).
# O spacedesk NUNCA entra aqui: o usuario usa todo dia.
$script:ServicosParaManual = @(
  @{ Nome = 'CCleaner7';                      Motivo = 'CCleaner em segundo plano (redundante)' }
  @{ Nome = 'HPPrintScanDoctorService';       Motivo = 'HP Print and Scan Doctor' }
  @{ Nome = 'aspnet_state';                   Motivo = 'ASP.NET State Service (legado)' }
  @{ Nome = 'SQLWriter';                      Motivo = 'SQL Server VSS Writer (legado)' }
  @{ Nome = 'postgresql-x64-*';               Motivo = 'PostgreSQL: Start-Service quando for usar' }
  @{ Nome = 'SupportAssistAgent';             Motivo = 'Dell SupportAssist' }
  @{ Nome = 'Dell SupportAssist Remediation'; Motivo = 'Dell SupportAssist Remediation' }
  @{ Nome = 'DellTechHub';                    Motivo = 'Dell TechHub' }
  @{ Nome = 'AnyDesk';                        Motivo = 'AnyDesk (sem acesso remoto sem ninguem na frente)' }
  @{ Nome = 'DellPairService';                Motivo = 'Dell Pair' }
  @{ Nome = 'DPMService';                     Motivo = 'Dell Peripheral Manager' }
)
$script:NuncaMexer = 'spacedesk*'

# Modo de energia (overlay do Windows 11).
$script:ModosEnergia = @{
  '961cc777-2547-4f9d-8174-7d86181b8a7a' = 'eficiencia'
  '3af9b8d9-7c97-431d-ad78-34a8bfea439f' = 'eficiencia'
  '00000000-0000-0000-0000-000000000000' = 'equilibrado'
  'ded574b5-45a0-4f42-8737-46345c09c238' = 'desempenho'
}

# Limites dos achados (os mesmos do app UsageCockpit, aba Saude do PC).
$script:Limites = @{
  CpuWarn = 70; CpuDanger = 90
  CommitWarn = 75; CommitDanger = 90
  DiscoWarn = 85; DiscoDanger = 95
  UptimeWarnDias = 7; UptimeDangerDias = 14
  KernelWarnGB = 1; KernelDangerGB = 2
  ProcessosWarn = 500; ProcessosDanger = 800
  AuxiliaresWarn = 20
  TempGB = 0.5; NpmGB = 0.5
}

$script:NomesAuxiliares = 'node.exe', 'node_repl.exe', 'cmd.exe', 'powershell.exe', 'pwsh.exe', 'bash.exe', 'conhost.exe'
# Para contar terminais esquecidos: sem o conhost, que acompanha qualquer console (inclusive de servicos).
$script:NomesTerminais = 'node.exe', 'node_repl.exe', 'cmd.exe', 'powershell.exe', 'pwsh.exe', 'bash.exe'

# Listas vindas de 'powershell -File' chegam como um texto so ("a,b,c"): separa por virgula ou espaco.
function Split-Lista {
  param([string[]]$Valores)
  foreach ($v in $Valores) { foreach ($p in ([string]$v -split '[,;\s]+')) { if ($p) { $p.Trim() } } }
}

# Numero com virgula decimal (pt-BR), para os textos dos achados.
$script:PtBr = [Globalization.CultureInfo]::GetCultureInfo('pt-BR')
function Fmt {
  param($Valor, [int]$Casas = 1)
  if ($null -eq $Valor) { return '--' }
  ([double]$Valor).ToString('N' + $Casas, $script:PtBr)
}

function Get-ModoEnergia {
  $k = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes' -ErrorAction SilentlyContinue
  $ac = if ($k) { [string]$k.ActiveOverlayAcPowerScheme } else { '' }
  $dc = if ($k) { [string]$k.ActiveOverlayDcPowerScheme } else { '' }
  $nome = { param($g) if ($script:ModosEnergia.ContainsKey($g)) { $script:ModosEnergia[$g] } elseif ($g) { 'outro' } else { 'desconhecido' } }
  $bat = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
  $naTomada = if ($bat) { $bat.BatteryStatus -ne 1 } else { $true }
  [pscustomobject]@{ Tomada = (& $nome $ac); Bateria = (& $nome $dc); NaTomada = $naTomada; BateriaPct = if ($bat) { $bat.EstimatedChargeRemaining } else { $null } }
}

# PIDs desta sessao e de todos os ancestrais: nunca sao encerrados.
function Get-PidsProtegidos {
  param($PorId)
  $set = @{}
  $p = $PorId[[int]$PID]
  while ($p -and -not $set.ContainsKey([int]$p.ProcessId)) {
    $set[[int]$p.ProcessId] = $true
    $p = $PorId[[int]$p.ParentProcessId]
  }
  return $set
}

# Classifica processos auxiliares antigos. Devolve so as RAIZES (encerrar a raiz com /T leva a arvore).
# Acao 'encerrar' = seguro encerrar; 'manter' = aberto por voce ou em uso; 'avaliar' = voce decide.
function Get-ProcessosEsquecidos {
  $agora = Get-Date
  $hoje = $agora.Date
  $procs = @(Get-CimInstance Win32_Process)
  $porId = @{}
  foreach ($p in $procs) { $porId[[int]$p.ProcessId] = $p }
  $protegidos = Get-PidsProtegidos -PorId $porId
  $lista = New-Object System.Collections.Generic.List[object]

  foreach ($p in $procs) {
    $id = [int]$p.ProcessId
    if ($protegidos.ContainsKey($id) -or -not $p.CreationDate) { continue }
    $pai = $porId[[int]$p.ParentProcessId]
    $paiNome = if ($pai -and $pai.CreationDate -le $p.CreationDate) { $pai.Name } else { $null } # PID reaproveitado = pai morto
    $idadeH = [math]::Round(($agora - $p.CreationDate).TotalHours, 1)
    $antesDeHoje = $p.CreationDate -lt $hoje
    $cmd = [string]$p.CommandLine
    $cat = $null; $acao = $null

    if ($p.Name -like 'codex-command-runner*' -and $antesDeHoje) { $cat = 'Codex: executor de comandos antigo'; $acao = 'encerrar' }
    elseif ($paiNome -eq 'codex.exe' -and $p.Name -in 'cmd.exe', 'node.exe', 'node_repl.exe' -and $antesDeHoje) { $cat = 'Codex: servidor MCP de conversa antiga'; $acao = 'encerrar' }
    elseif ($paiNome -eq 'ChatGPT.exe' -and $p.Name -in 'powershell.exe', 'cmd.exe', 'pwsh.exe' -and $antesDeHoje) { $cat = 'ChatGPT: shell de agente antigo'; $acao = 'encerrar' }
    elseif ($p.Name -eq 'node.exe' -and $cmd -match 'npm-cli\.js"?\s+run\s+(dev|start)|next(\.js)?["\\/]?\s+dev|\bvite\b|webpack(-dev-server| serve)' -and $idadeH -ge 24 -and $paiNome -ne 'node.exe') { $cat = 'Servidor de desenvolvimento aberto ha mais de 1 dia'; $acao = 'encerrar' }
    elseif ($p.Name -in 'powershell.exe', 'pwsh.exe' -and $paiNome -eq 'claude.exe' -and $cmd -match '-NoExit') { $cat = 'Aba de terminal do Claude'; $acao = 'manter' }
    elseif ($p.Name -in $script:NomesAuxiliares -and $paiNome -in 'explorer.exe', 'WindowsTerminal.exe', 'mintty.exe', 'git-bash.exe', 'Code.exe', 'OpenConsole.exe') { if ($idadeH -ge 24) { $cat = 'Terminal aberto por voce'; $acao = 'manter' } }
    elseif ($p.Name -in 'node.exe', 'cmd.exe', 'node_repl.exe' -and -not $paiNome -and $idadeH -ge 24) { $cat = 'Auxiliar orfao (o programa que abriu ja fechou)'; $acao = 'avaliar' }

    if (-not $cat) { continue }
    $cmdCurto = ($cmd -replace '\s+', ' '); if ($cmdCurto.Length -gt 110) { $cmdCurto = $cmdCurto.Substring(0, 110) + '...' }
    $lista.Add([pscustomobject]@{
      PID = $id; Nome = $p.Name; Pai = $paiNome; IdadeHoras = $idadeH
      MB = [math]::Round($p.PrivatePageCount / 1MB); Categoria = $cat; Acao = $acao; Comando = $cmdCurto
    })
  }
  # Remove filhos cujo ancestral ja esta na lista (para nao contar duas vezes).
  $ids = @{}; foreach ($c in $lista) { $ids[$c.PID] = $true }
  $raizes = foreach ($c in $lista) {
    $anc = $porId[[int]$porId[$c.PID].ParentProcessId]; $temRaiz = $false; $guard = 0
    while ($anc -and $guard -lt 20) { if ($ids.ContainsKey([int]$anc.ProcessId)) { $temRaiz = $true; break }; $anc = $porId[[int]$anc.ParentProcessId]; $guard++ }
    if (-not $temRaiz) { $c }
  }
  return @($raizes)
}

# Memoria privada somada da arvore de um processo, em MB.
function Get-MBArvore {
  param([int]$Raiz, $PorId)
  $filhos = @{}
  foreach ($p in $PorId.Values) { $pp = [int]$p.ParentProcessId; if (-not $filhos.ContainsKey($pp)) { $filhos[$pp] = @() }; $filhos[$pp] += $p }
  $total = 0; $fila = New-Object System.Collections.Queue; $fila.Enqueue($Raiz); $vistos = @{}
  while ($fila.Count) {
    $i = $fila.Dequeue(); if ($vistos.ContainsKey($i)) { continue }; $vistos[$i] = $true
    if ($PorId[$i]) { $total += $PorId[$i].PrivatePageCount }
    foreach ($f in @($filhos[$i])) { if ($f -and $f.CreationDate -ge $PorId[$i].CreationDate) { $fila.Enqueue([int]$f.ProcessId) } }
  }
  [math]::Round($total / 1MB)
}

# Tamanho de uma pasta em GB (opcionalmente so arquivos mais antigos que N dias). Ignora o que nao der para ler.
function Get-TamanhoGB {
  param([string]$Caminho, [double]$MaisAntigoQueDias = 0, [string[]]$Excluir = @())
  if (-not (Test-Path -LiteralPath $Caminho)) { return $null }
  $limite = (Get-Date).AddDays(-$MaisAntigoQueDias)
  $itens = Get-ChildItem -LiteralPath $Caminho -Force -ErrorAction SilentlyContinue | Where-Object { $Excluir -notcontains $_.Name }
  $soma = 0
  foreach ($i in $itens) {
    if ($MaisAntigoQueDias -gt 0 -and $i.LastWriteTime -ge $limite) { continue }
    if ($i.PSIsContainer) { $soma += (Get-ChildItem -LiteralPath $i.FullName -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum }
    else { $soma += $i.Length }
  }
  [math]::Round($soma / 1GB, 2)
}

function Get-Medidas {
  $os = Get-CimInstance Win32_OperatingSystem
  $m = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory
  $c = Get-Volume -DriveLetter C -ErrorAction SilentlyContinue
  [pscustomobject]@{
    RamDisponivelGB = [math]::Round($m.AvailableMBytes / 1024, 1)
    CommitGB = [math]::Round(($os.TotalVirtualMemorySize - $os.FreeVirtualMemory) / 1MB, 1)
    CommitLimiteGB = [math]::Round($os.TotalVirtualMemorySize / 1MB, 1)
    KernelGB = [math]::Round($m.PoolNonpagedBytes / 1GB, 2)
    Processos = @(Get-Process).Count
    CLivreGB = if ($c) { [math]::Round($c.SizeRemaining / 1GB, 1) } else { $null }
  }
}

# Pastas de cache/temporarios que a limpeza autonoma pode esvaziar (nunca dados do usuario).
$script:PastasExcluidasTemp = @('claude', 'saude-pc')

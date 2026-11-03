# Limpeza sem administrador. Por padrao SO SIMULA: mostra o que faria e nao altera nada.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File limpeza-usuario.ps1 -Acoes Energia,Processos,Temp,NpmCache            (simula)
#   powershell -NoProfile -ExecutionPolicy Bypass -File limpeza-usuario.ps1 -Acoes Energia,Processos,Temp,NpmCache -Executar  (faz)
# Acoes:
#   Energia    modo de energia na tomada -> Equilibrado (so se estiver em eficiencia)
#   Processos  encerra os grupos de processos esquecidos marcados 'encerrar' (Codex/ChatGPT antigos, servidores de dev com +1 dia)
#   Orfaos     encerra tambem os auxiliares orfaos ('avaliar'); so use com o OK explicito do usuario
#   Temp       apaga itens do %TEMP% com mais de 1 dia (exceto as pastas 'claude' e 'saude-pc')
#   NpmCache   npm cache clean --force
# -PIDs 123,456  restringe Processos/Orfaos a estes PIDs (os que o usuario aprovou na lista).
# -PularPIDs 789 nunca encerra estes PIDs.
# Nunca: nada que exija administrador, nada do spacedesk, nada criado hoje, esta sessao e seus ancestrais,
# terminais abertos pelo usuario, nem arquivos fora do %TEMP% e do cache do npm.

param(
  [string[]]$Acoes = @('Energia', 'Processos', 'Temp', 'NpmCache'),
  [switch]$Executar,
  [string[]]$PIDs = @(),
  [string[]]$PularPIDs = @()
)
$ErrorActionPreference = 'SilentlyContinue'
. "$PSScriptRoot\comum.ps1"

# Com -File, "a,b,c" chega como um texto so: aceita listas separadas por virgula ou espaco.
$Acoes = @(Split-Lista $Acoes)
$validas = 'Energia', 'Processos', 'Orfaos', 'Temp', 'NpmCache'
$invalidas = @($Acoes | Where-Object { $validas -notcontains $_ })
if ($invalidas.Count) { Write-Error ("Acao invalida: {0}. Use: {1}" -f ($invalidas -join ', '), ($validas -join ', ')); exit 2 }
$PIDs = @(Split-Lista $PIDs | ForEach-Object { [int]$_ })
$PularPIDs = @(Split-Lista $PularPIDs | ForEach-Object { [int]$_ })

$modo = if ($Executar) { 'EXECUTANDO' } else { 'SIMULACAO (nada sera alterado)' }
"== Limpeza do usuario: $modo"
$antes = Get-Medidas
$feito = New-Object System.Collections.Generic.List[object]
$falhou = New-Object System.Collections.Generic.List[object]
function Ok($acao, $texto) { $feito.Add([pscustomobject]@{ Acao = $acao; Resultado = $texto }); "  [ok] $texto" }
function Falha($acao, $texto) { $falhou.Add([pscustomobject]@{ Acao = $acao; Motivo = $texto }); "  [falhou] $texto" }

# ---------- Energia ----------
if ($Acoes -contains 'Energia') {
  "-- Energia"
  $e = Get-ModoEnergia
  if ($e.Tomada -ne 'eficiencia') { "  modo na tomada ja esta '$($e.Tomada)': nada a fazer" }
  elseif (-not $Executar) { "  mudaria o modo na tomada de 'eficiencia' para 'equilibrado'" }
  else {
    powercfg /overlaysetactive 00000000-0000-0000-0000-000000000000 2>$null | Out-Null
    $depois = Get-ModoEnergia
    if ($depois.Tomada -eq 'equilibrado') { Ok 'Energia' "modo de energia na tomada: eficiencia -> equilibrado" }
    else { Falha 'Energia' "o Windows nao aceitou a troca (continua '$($depois.Tomada)'); mude em Configuracoes > Sistema > Energia" }
  }
}

# ---------- Processos ----------
if ($Acoes -contains 'Processos' -or $Acoes -contains 'Orfaos') {
  "-- Processos esquecidos"
  $aceitas = @(); if ($Acoes -contains 'Processos') { $aceitas += 'encerrar' }; if ($Acoes -contains 'Orfaos') { $aceitas += 'avaliar' }
  $porId = @{}; Get-CimInstance Win32_Process | ForEach-Object { $porId[[int]$_.ProcessId] = $_ }
  $alvos = @(Get-ProcessosEsquecidos | Where-Object { $aceitas -contains $_.Acao -and ($PIDs.Count -eq 0 -or $PIDs -contains $_.PID) -and $PularPIDs -notcontains $_.PID })
  if (-not $alvos.Count) { "  nenhum processo para encerrar" }
  foreach ($a in $alvos) {
    $mb = Get-MBArvore -Raiz $a.PID -PorId $porId
    $desc = "{0} PID {1} ({2}, {3} h, ~{4} MB com os filhos)" -f $a.Nome, $a.PID, $a.Categoria, $a.IdadeHoras, $mb
    if (-not $Executar) { "  encerraria $desc"; continue }
    $out = taskkill /PID $a.PID /T /F 2>&1
    if ($LASTEXITCODE -eq 0 -or -not (Get-Process -Id $a.PID)) { Ok 'Processos' "encerrado $desc" }
    else { Falha 'Processos' "$desc :: $($out | Select-Object -Last 1)" }
  }
}

# ---------- Temp ----------
if ($Acoes -contains 'Temp') {
  "-- Temp (itens com mais de 1 dia)"
  $limite = (Get-Date).AddDays(-1)
  $itens = @(Get-ChildItem -LiteralPath $env:TEMP -Force | Where-Object { $script:PastasExcluidasTemp -notcontains $_.Name -and $_.LastWriteTime -lt $limite })
  $gb = Get-TamanhoGB $env:TEMP -MaisAntigoQueDias 1 -Excluir $script:PastasExcluidasTemp
  if (-not $itens.Count) { "  nada com mais de 1 dia" }
  elseif (-not $Executar) {
    "  apagaria $($itens.Count) itens (~$(Fmt $gb 2) GB). Maiores:"
    $itens | ForEach-Object { [pscustomobject]@{ Nome = $_.Name; GB = if ($_.PSIsContainer) { Get-TamanhoGB $_.FullName } else { [math]::Round($_.Length / 1GB, 2) } } } |
      Sort-Object GB -Descending | Select-Object -First 8 | ForEach-Object { "    {0,6} GB  {1}" -f (Fmt $_.GB 2), $_.Nome }
  }
  else {
    $livreAntes = (Get-Volume -DriveLetter C).SizeRemaining
    $presos = 0
    foreach ($i in $itens) { Remove-Item -LiteralPath $i.FullName -Recurse -Force -ErrorAction SilentlyContinue; if (Test-Path -LiteralPath $i.FullName) { $presos++ } }
    $liberado = [math]::Round(((Get-Volume -DriveLetter C).SizeRemaining - $livreAntes) / 1GB, 2)
    Ok 'Temp' "Temp: $($itens.Count - $presos) itens apagados, ~$(Fmt $liberado 2) GB liberados"
    if ($presos) { Falha 'Temp' "$presos itens em uso por algum programa ficaram (normal; saem depois de reiniciar)" }
  }
}

# ---------- Cache do npm ----------
if ($Acoes -contains 'NpmCache') {
  "-- Cache do npm"
  $npm = Get-Command npm -ErrorAction SilentlyContinue
  # O 'npm cache clean' nao mexe no _npx (pacotes do npx, inclusive servidores MCP em uso): fica de fora da conta.
  $gb = Get-TamanhoGB "$env:LOCALAPPDATA\npm-cache" -Excluir '_npx'
  if (-not $npm) { "  npm nao instalado: nada a fazer" }
  elseif (-not $gb) { "  cache vazio: nada a fazer" }
  elseif (-not $Executar) { "  limparia ~$(Fmt $gb 2) GB com 'npm cache clean --force'" }
  else {
    $livreAntes = (Get-Volume -DriveLetter C).SizeRemaining
    & npm cache clean --force 2>&1 | Out-Null
    $liberado = [math]::Round(((Get-Volume -DriveLetter C).SizeRemaining - $livreAntes) / 1GB, 2)
    if ($LASTEXITCODE -eq 0) { Ok 'NpmCache' "cache do npm limpo, ~$(Fmt $liberado 2) GB liberados" } else { Falha 'NpmCache' "npm cache clean retornou $LASTEXITCODE" }
  }
}

# ---------- Resultado ----------
if ($Executar) {
  Start-Sleep 3
  $depois = Get-Medidas
  "-- Antes -> depois"
  "  memoria comprometida: {0} -> {1} GB | RAM livre: {2} -> {3} GB" -f $antes.CommitGB, $depois.CommitGB, $antes.RamDisponivelGB, $depois.RamDisponivelGB
  "  processos: {0} -> {1} | C: livre: {2} -> {3} GB" -f $antes.Processos, $depois.Processos, $antes.CLivreGB, $depois.CLivreGB
  $dir = Join-Path $env:TEMP 'saude-pc'; New-Item -ItemType Directory -Force $dir | Out-Null
  $arquivo = Join-Path $dir ("limpeza-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))
  [pscustomobject]@{ Data = (Get-Date).ToString('s'); Antes = $antes; Depois = $depois; Feito = $feito; Falhou = $falhou } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $arquivo -Encoding UTF8
  "JSON=$arquivo"
}

---
name: saude-do-pc
description: Diagnostica e otimiza o PC Windows do usuário quando ele está lento ou travando. Roda um diagnóstico só de leitura (CPU e modo de energia, memória comprometida, memória do kernel, discos, pagefile, tempo ligado, processos esquecidos de Codex/ChatGPT/servidores de dev, Temp e caches, serviços e inicialização), separa o que dá para fazer sozinho, o que exige administrador e o que só o usuário pode fazer, executa a limpeza segura depois de um OK, prepara o script de administrador e termina com um relatório no chat. Use quando o usuário disser que o PC está lento, pesado, travando, sem espaço, ou pedir para "analisar a saúde do PC", "limpar o PC" ou rodar /saude-do-pc.
---

# Saúde do PC

Roteiro para diagnosticar e melhorar o PC Windows do usuário. Responda sempre em português do Brasil.

Os scripts ficam em `scripts/`, ao lado deste arquivo (`D:\Repositorio\monitor-uso-ia\.agents\skills\saude-do-pc\scripts`). Rode-os com:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "<pasta da skill>\scripts\<script>.ps1" <parâmetros>
```

| Script | O que faz | Precisa de admin |
|---|---|---|
| `diagnostico.ps1 [-Rapido] [-Profundo]` | Coleta tudo e lista os achados. **Só leitura.** Grava um JSON em `%TEMP%\saude-pc\` e imprime `JSON=<caminho>` na última linha. `-Rapido` pula as pastas grandes (Chrome, `.cache`, `.codex`). `-Profundo` também lista as 15 maiores pastas do perfil e do AppData (+2 a 5 min). | Não |
| `limpeza-usuario.ps1 -Acoes ... [-Executar] [-PIDs ...] [-PularPIDs ...]` | Ações seguras. **Sem `-Executar`, só simula** e mostra exatamente o que faria. Listas vão separadas por vírgula, sem espaço: `-Acoes Energia,Temp -PularPIDs 123,456`. | Não |
| `otimizacao-admin.ps1 [-Sem...] [-ManterServicos ...]` | Script padrão de administrador. **Quem roda é o usuário**, nunca você. | Sim |
| `comum.ps1` | Listas e funções compartilhadas (serviços candidatos, limites, regras dos processos esquecidos). Não roda sozinho. | — |

## Regras que nunca mudam

- **Nunca mexa no spacedesk** (serviços, drivers, processos). O usuário usa todo dia.
- **Nada que exija administrador é executado por você.** Vira um script para o usuário rodar como administrador. Não tente contornar o UAC.
- **Antes de qualquer alteração, mostre a lista exata e peça um único OK** (processos com PID e o que são, pastas e tamanhos, mudança de energia). Sem o OK, não execute.
- **Nunca encerre**: esta sessão e seus ancestrais, terminais que o usuário abriu (Explorer, Windows Terminal, Git Bash, VS Code), abas de terminal do Claude, nada iniciado hoje. Os scripts já protegem isso; não passe PIDs fora da lista do diagnóstico.
- **Nunca apague arquivos do usuário** (Downloads, Documentos, Área de Trabalho, perfis e dados do Chrome, `.codex`, `.claude`). Só `%TEMP%` com mais de 1 dia e o cache do npm. Para o resto, recomende.
- **Não reinicie o PC** nem feche apps do usuário: recomende.
- Prefira PowerShell a controlar a tela. Se um dia precisar controlar a tela, use o **monitor secundário**.
- Os nomes dos contadores de desempenho do Windows estão em português nesta máquina: use as classes WMI `Win32_PerfFormattedData_*` (como os scripts já fazem), nunca `Get-Counter` com nomes em inglês.

## Roteiro

### 1. Diagnóstico

Rode `diagnostico.ps1` (leva cerca de 2 min; use `-Rapido` se o usuário tiver pressa). Leia o JSON indicado em `JSON=` para os detalhes. Se aparecer `disk_low`, rode de novo com `-Profundo` (em segundo plano, enquanto apresenta o resto) para mostrar onde o espaço está: as maiores pastas viram recomendações manuais (nunca apague pastas do perfil).

Apresente:

1. Uma linha do sistema (modelo, CPU, RAM, dias ligado).
2. Uma tabela curta dos indicadores: CPU (uso e velocidade real), modo de energia, RAM livre, memória comprometida, memória do kernel, cada disco, processos.
3. Os **achados**, do mais grave ao menos grave, já separados nos três grupos abaixo, cada um com o número que comprova.
4. Os maiores consumidores (memória e CPU) e, se houver execução anterior em `%TEMP%\saude-pc\`, o que mudou desde ela.

O campo `Tipo` de cada achado indica o grupo:

| Tipo | Grupo | Exemplos |
|---|---|---|
| `auto` | **Posso fazer sozinho** (com seu OK) | `power_efficiency` → `Energia`; `stale_processes` → `Processos`; `orphan_helpers` → `Orfaos`; `temp_old` → `Temp`; `npm_cache` → `NpmCache` |
| `admin` | **Precisa de administrador** (script para você rodar) | `services_auto`, `windows_temp`, e as partes de disco do `disk_low` |
| `manual` | **Só você pode fazer** | `uptime_long`, `kernel_pool_high`, `memory_high`, `chrome_heavy`, `cpu_high`, `pagefile_big`, `recycle_bin`, `downloads_big`, `bsod`, `disk_health` |
| `misto` | Parte em cada grupo | `disk_low`: Temp/npm (auto) + WinSxS/hibernação/restauração (admin) + mover arquivos (manual) |

### 2. Pedir o OK

1. Rode `limpeza-usuario.ps1` **sem `-Executar`** com as ações dos achados `auto` (o campo `Acao` diz qual). Use `Orfaos` só se houver `orphan_helpers`, e deixe-o desmarcado por padrão.
2. Mostre o que a simulação listou: processos (PID, nome, categoria, idade, memória), itens do Temp e tamanho, mudança de energia.
3. Peça **um único OK**, deixando o usuário tirar itens (ex.: "menos o PID 1234" → `-PularPIDs 1234`; só alguns → `-PIDs ...`). Use a ferramenta de pergunta quando houver escolha; senão, pergunte em uma linha.

Se não houver achados `auto`, pule para a etapa 4.

### 3. Executar

Rode `limpeza-usuario.ps1 -Executar` com exatamente o que foi aprovado. O script mede antes e depois (memória comprometida, RAM livre, processos, espaço livre no C:) e lista `[ok]` e `[falhou]` com o motivo.

Se o modo de energia não mudar pelo `powercfg`, oriente: Configurações › Sistema › Energia e bateria › Modo de energia › Equilibrado.

### 4. Script de administrador

Se houver achados `admin` ou `misto`:

- **Achados cobertos pelo script padrão** (serviços da lista, temporários do Windows, WinSxS, hibernação, pontos de restauração): use `otimizacao-admin.ps1`. Diga quais partes ele vai fazer e passe `-Sem...` para pular o que não se aplica, ou `-ManterServicos` para serviços que o usuário quer manter automáticos (pergunte sobre AnyDesk se ele acessa o PC remotamente sem ninguém na frente).
- **Algo novo que o padrão não cobre** (outro serviço, outra pasta do sistema, um driver): gere um script sob medida em `%TEMP%\saude-pc\admin-<data>.ps1`, com o mesmo estilo do padrão: `#Requires -RunAsAdministrator`, textos sem acento (o PowerShell 5.1 lê `.ps1` sem BOM como ANSI), cada passo comentado e reversível, e o comando para desfazer. Se a novidade for recorrente, sugira incluí-la no `comum.ps1`.
- Sempre entregue o comando **com o caminho completo**, porque o PowerShell de administrador abre em `C:\Windows\system32`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Repositorio\monitor-uso-ia\.agents\skills\saude-do-pc\scripts\otimizacao-admin.ps1"
```

### 5. Relatório final (só no chat)

Não grave relatório em arquivo. Termine com três seções:

1. **O que foi feito**: cada ação com o número (ex.: "encerrei 12 grupos de processos do Codex, ~2,8 GB"; "Temp: 6,8 GB liberados"), e a tabela antes → depois.
2. **O que não deu certo**: cada `[falhou]` com o motivo e o que fazer.
3. **O que você precisa fazer**, em ordem de impacto, cada item com o passo a passo curto:
   - rodar o script de administrador (comando completo);
   - reiniciar, quando houver `uptime_long`, `kernel_pool_high` ou `pagefile_big`;
   - fechar ou ajustar apps (`memory_high`, `chrome_heavy`, `cpu_high`), dizendo quais;
   - revisar Downloads e Lixeira, se apareceram;
   - investigar driver se a memória do kernel voltar a crescer depois de reiniciar.

## Como interpretar os achados

- **Modo de energia "eficiência" na tomada** (overlay `961cc777-…` ou `3af9b8d9-…`): a CPU fica perto do clock base (ex.: 1,6 GHz num i7 que chega a 5 GHz). É a causa de lentidão mais barata de resolver.
- **Desempenho da CPU** (`DesempenhoPct`): ~100% é o clock base; acima disso é turbo. Uso alto com desempenho abaixo de 100% indica limitação de energia ou temperatura.
- **Memória comprometida** (commit) é o que conta para travamentos, mais que a RAM física: perto de 90%, o Windows faz o pagefile crescer e tudo fica lento.
- **Memória do kernel (pool não paginado)** acima de 1 GB é anormal (o normal é abaixo de 0,5 GB). Costuma ser driver vazando (antivírus, VPN, monitores virtuais) e acumula com o PC ligado. Reiniciar zera; se voltar a crescer em 1–2 dias, investigar com `poolmon` como administrador.
- **Pagefile maior que a RAM** é consequência de memória comprometida alta. Reiniciar faz o Windows encolher; não fixe um tamanho menor enquanto a memória estiver alta, ou faltará memória.
- **Processos esquecidos**: o app do Codex abre um conjunto de servidores MCP (`cmd` → `node`/`node_repl`) a cada conversa e não fecha os antigos; o ChatGPT deixa shells de agente abertos; servidores de desenvolvimento (`next dev`, `vite`) ficam esquecidos por dias. Encerrar libera memória e não quebra nada que esteja em uso hoje (se uma conversa antiga do Codex perder as ferramentas, basta fechar e reabrir o app).
- **Disco com menos de 15% livre** deixa o SSD mais lento para gravar e atrapalha atualizações. Os maiores ganhos costumam ser: Temp (sobras de instaladores), cache do npm, pagefile (reiniciando), WinSxS e pontos de restauração (administrador).
- **Cache do npm x `_npx`**: o `npm cache clean --force` limpa só o `_cacache`. A pasta `npm-cache\_npx` guarda os pacotes rodados pelo `npx`, inclusive servidores MCP que o Claude e o Codex podem estar usando. Não a apague com esses apps abertos; recomende apagar manualmente com eles fechados (achado `npx_cache`).
- **Temp que volta a encher**: nesta máquina, o instalador do Visual Studio recria a cada atualização as pastas `fjz1w3bd` e `5g2dd24j` no Temp (~3,5 GB, pacotes do .NET e a extensão do Copilot). Limpar resolve na hora; para parar de voltar, recomende atualizar o Visual Studio até o fim pelo Visual Studio Installer e, em Mais › Configurações dele, desligar "Manter o cache de download após a instalação".
- **Serviços de terceiros em Automático** (Dell SupportAssist/TechHub, HP Doctor, CCleaner, PostgreSQL, AnyDesk…) ficam rodando o tempo todo; em Manual, iniciam só quando algo precisa. A lista editável está em `comum.ps1`.

## Histórico dos achados nesta máquina

Primeira análise (29/10/2026), para comparar nas próximas: Dell Latitude 3540, i7-1355U, 32 GB, SSD NVMe de 512 GB (C: 279 GB, D: 195 GB). Modo de energia em eficiência na tomada (CPU presa em 1,65 GHz), memória comprometida em 82–88%, memória do kernel em 2,4 GB, C: com 2% livre (pagefile de 35 GB), ligado havia 13 dias, 643 processos (12 conjuntos de servidores MCP do Codex esquecidos, um `next dev` de 4,2 GB). A limpeza liberou ~11,6 GB (Temp 6,8 GB e npm 4,7 GB) e ~8 GB de memória comprometida.

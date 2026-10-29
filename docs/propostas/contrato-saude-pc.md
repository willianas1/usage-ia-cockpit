# Contrato: saúde do PC (`/v1/health`, schemaVersion 1)

Referência única para os três repositórios: `monitor-uso-ia` (app desktop, produtor), `usage-ia-api` (armazena) e `usage-ia-cockpit-echo-show` (exibe). Protótipo visual aprovado: `docs/propostas/echo-show-saude-pc.html` (no repositório `monitor-uso-ia`).

## Fluxo

```text
App desktop -- POST /v1/health (Bearer INGEST_KEY) --> API --> Redis: último estado + histórico de 24 h
Echo Show   -- GET /api/health (cookie) --> função Vercel -- GET /v1/health?view=compact&history=24h (Bearer READ_KEY) --> API
```

Mesma URL base e mesma chave de escrita do `/v1/usage`. Contrato separado: o `/v1/usage` não muda.

## POST /v1/health

Headers iguais ao `/v1/usage`: `Authorization: Bearer <chave>`, `Content-Type: application/json; charset=utf-8`, `User-Agent: UsageCockpit/<versão>`. Corpo menor que 16 KB.

```json
{
  "schemaVersion": 1,
  "client": { "name": "UsageCockpit", "version": "0.7.0" },
  "sentAt": "2026-10-29T12:06:05.1234567-03:00",
  "reason": "interval",
  "status": "danger",
  "uptimeSeconds": 1116000,
  "cpu": { "percent": 74.2, "speedPercent": 97, "speedMHz": 1650, "baseMHz": 1700 },
  "memory": {
    "totalBytes": 34037719040, "availableBytes": 8160437862, "usedPercent": 76.0,
    "commitBytes": 58626303590, "commitLimitBytes": 71403061248, "commitPercent": 82.1,
    "kernelNonPagedBytes": 2504976384
  },
  "power": { "source": "ac", "mode": "efficiency", "batteryPercent": 100 },
  "disks": [
    { "name": "C:", "system": true, "totalBytes": 299356848128, "freeBytes": 7086696448, "usedPercent": 97.6 },
    { "name": "D:", "system": false, "totalBytes": 209715200000, "freeBytes": 98569336832, "usedPercent": 53.0 }
  ],
  "counts": { "processes": 643, "threads": 9364, "handles": 399013, "longRunningHelpers": 47 },
  "topProcesses": [
    { "name": "chrome", "instances": 46, "cpuPercent": 32.1, "memoryBytes": 15232000000 }
  ],
  "alerts": [
    { "code": "disk_low", "level": "danger", "title": "Disco C: com 2% livre", "action": "Limpe Temp e caches ou mova arquivos para outro disco" }
  ]
}
```

Todas as datas são ISO 8601 com fuso. Campos que podem ser `null` sempre aparecem. Campos desconhecidos são ignorados (passthrough).

| Campo | Tipo | Regra |
|---|---|---|
| `schemaVersion` | `1` | Outro valor: 400. |
| `client.name` / `client.version` | texto | Iguais ao `/v1/usage`. |
| `sentAt` | data | Mesma regra de relógio futuro do `/v1/usage`. |
| `reason` | `interval` \| `alert` \| `heartbeat` \| `test` | `interval` a cada 15 s (60 s até a 0.7); `alert` quando o conjunto de alertas ou o `status` muda; `heartbeat` ao salvar a configuração; `test` é o botão Testar conexão (não grava estado nem histórico). |
| `status` | `ok` \| `warn` \| `danger` | O pior `level` entre os alertas; `ok` sem alertas. |
| `uptimeSeconds` | inteiro ≥ 0 | Tempo desde o boot. |
| `cpu.percent` | 0–100 | Uso total médio desde o envio anterior (no máximo 15 s). |
| `cpu.speedPercent` | número ≥ 0 ou `null` | "% Processor Performance" do Windows. Passa de 100 com turbo. |
| `cpu.speedMHz` | número ≥ 0 ou `null` | `baseMHz × speedPercent / 100`. |
| `cpu.baseMHz` | número > 0 ou `null` | Clock base do processador. |
| `memory.*Bytes` | inteiro ≥ 0 | `kernelNonPagedBytes` pode ser `null`. |
| `memory.usedPercent` / `commitPercent` | 0–100 | |
| `power.source` | `ac` \| `battery` \| `unknown` | |
| `power.mode` | `efficiency` \| `balanced` \| `performance` \| `unknown` | Modo de energia efetivo (overlay do Windows 11). |
| `power.batteryPercent` | 0–100 ou `null` | `null` sem bateria. |
| `disks` | lista, 1 a 26 | Só discos fixos prontos. `name` é a letra (`C:`), sem rótulo do volume. `system: true` no disco do Windows. |
| `counts.*` | inteiro ≥ 0 | `longRunningHelpers`: `node`, `node_repl`, `cmd`, `powershell`, `pwsh`, `conhost`, `bash` abertos há mais de 24 h. |
| `topProcesses` | lista, até 8 | Agrupado pelo nome do executável, sem `.exe`, sem caminho nem linha de comando. Ordenado por `cpuPercent` e depois `memoryBytes`. `cpuPercent` é a média no intervalo, de 0 a 100 do PC inteiro. `memoryBytes` é a memória privada somada. |
| `alerts` | lista, até 12 | `code` (texto curto, `[a-z_]+`), `level` (`warn` \| `danger`), `title` e `action` (português, até 120 caracteres). |

### Alertas calculados no app (limites num só lugar)

| `code` | `warn` | `danger` | Exemplo de `title` / `action` |
|---|---|---|---|
| `cpu_high` | média de 5 min ≥ 70% | ≥ 90% | "CPU em 74% há 5 min" / "Veja os apps mais pesados" |
| `memory_high` | `commitPercent` ≥ 75 | ≥ 90 | "Memória comprometida em 82%" / "Feche apps que não está usando" |
| `disk_low` | disco com `usedPercent` ≥ 85 | ≥ 95 | "Disco C: com 2% livre" / "Limpe Temp e caches ou mova arquivos para outro disco" |
| `power_efficiency` | `mode = efficiency` e `source = ac` | — | "Modo de energia em eficiência na tomada" / "Mude para Equilibrado em Configurações › Energia" |
| `uptime_long` | ligado há ≥ 7 dias | ≥ 14 dias | "Ligado há 12 dias" / "Reinicie para liberar memória" |
| `kernel_pool_high` | `kernelNonPagedBytes` ≥ 1 GB | ≥ 2 GB | "Memória do kernel em 2,4 GB" / "Reinicie; se voltar a crescer, investigue drivers" |
| `processes_high` | `processes` ≥ 500 | ≥ 800 | "643 processos abertos" / "Feche apps e terminais esquecidos" |
| `helpers_stale` | `longRunningHelpers` ≥ 20 | — | "47 processos auxiliares abertos há mais de 1 dia" / "Feche terminais e reabra apps como o Codex" |

Um alerta por `code` (para disco, o pior disco). Ordem da lista: `danger` antes de `warn`, depois a ordem da tabela.

### Respostas

Iguais ao `/v1/usage`: `204` grava; `400 invalid_payload` com `fields`; `401` chave errada; `413`; `415`; `429` com `Retry-After`. O app trata as respostas como hoje (401/403 para de enviar a saúde até salvar a configuração; 429 respeita `Retry-After`; outros erros com espera crescente).

## GET /v1/health

Chaves de leitura (`READ_KEYS`). Query:

- `view=compact|full` (padrão `full`; outro valor: `400 invalid_view`).
- `history=24h` (opcional; outro valor: `400 invalid_history`): inclui o histórico.

Sem dados: `200` com `hasData: false`.

```json
{
  "schemaVersion": 1,
  "hasData": true,
  "online": true,
  "receivedAt": "2026-10-29T15:06:05.400Z",
  "serverTime": "2026-10-29T15:06:20.000Z",
  "offlineAfterSeconds": 360,
  "health": { "...": "payload recebido (full) ou resumo (compact)" },
  "history": { "stepSeconds": 1800, "points": [ { "t": "2026-10-28T15:30:00.000Z", "cpu": 61.2, "memory": 79.8, "disk": 97.5, "speedMHz": 1702 } ] }
}
```

- `online`: mesma regra do uso (`OFFLINE_AFTER_SECONDS`).
- `compact` traz em `health`: `status`, `sentAt`, `uptimeSeconds`, `cpu`, `memory` (`usedPercent`, `commitPercent`, `availableBytes`, `totalBytes`, `kernelNonPagedBytes`), `power`, `systemDisk` (o disco com `system: true`, ou o primeiro), `counts`, `topProcesses` (até 5) e `alerts`.
- `history` só aparece com `history=24h`: 48 pontos de 30 min, do mais antigo para o mais recente, cobrindo as últimas 24 h. Cada ponto é a média das amostras no intervalo; intervalo sem amostra vira ponto com valores `null`. `disk` é o `usedPercent` do disco de sistema; `memory` é o `commitPercent`.

### Armazenamento (Redis)

- `${prefix}:health:latest`: último estado, com o mesmo script de comparação por `sentAtMs` do uso (envio atrasado não sobrescreve), e TTL `RETENTION_SECONDS`.
- `${prefix}:health:history`: uma amostra por minuto (a primeira de cada minuto vence), guardada em sorted set ou lista com corte para 24 h, no mesmo script atômico. Amostra: `{ t, cpu, memory, disk, speedMHz }`.
- Limite de taxa da escrita da saúde numa identidade própria (`write:health`), para não competir com o `/v1/usage`.

## Saúde ligada ou desligada (`healthEnabled` no `/v1/usage`)

O payload do `/v1/usage` ganha o campo de raiz `healthEnabled` (booleano): `true` quando o monitoramento e o envio da saúde estão ligados no app. A API devolve o campo nas leituras `full` e `compact` do `/v1/usage` (`null` quando o app não informa, versões anteriores à 0.8). O painel do Echo Show:

- `healthEnabled: false` → layout só com a IA (o de antes da saúde) e não consulta `/v1/health`;
- `healthEnabled: true` → layout com a saúde;
- `null` → decide pelos dados de `/v1/health` (`hasData` e `online`), consultando-o com menos frequência.

## Comportamento do app

- Configurações › nova aba **Saúde do PC**: "Monitorar a saúde do PC" (desligado por padrão) e "Enviar a saúde para a API" (usa URL e chave de Configurações › Envio para API).
- Coleta sem administrador e sem depender do idioma do Windows. CPU a cada 5 s; processos, WMI e discos a cada 30 s. Meta de custo: menos de 0,5% de CPU.
- Envia a cada 15 s e na hora (respeitando 15 s entre envios) quando o `status` ou o conjunto de `code` dos alertas muda. **Pausa junto com as consultas de IA** (botão ou fim do horário de trabalho), a partir da 0.9; até a 0.8 continuava enviando.
- Só lê: não encerra processos, não executa PowerShell nem outros programas.

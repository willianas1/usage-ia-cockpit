# Envio do uso para uma API

O Usage IA Cockpit pode enviar o uso atual do Claude Code e do Codex (ChatGPT) para uma API sua. Com isso, os mesmos números do painel ficam disponíveis para outras aplicações: um dashboard, um bot, um widget no celular.

O app só **envia**. Você implementa a API que recebe, seguindo o contrato abaixo, e decide o que fazer com o dado: guardar o último valor, repassar ou exibir.

## O que é enviado (e o que não é)

São enviados apenas dados consolidados: percentuais de uso, horários de reset, origem e idade da leitura, mensagem de erro do painel e se a ferramenta está em uso agora.

**Nunca são enviados** tokens ou logins dos provedores, IDs de conta, e-mail, nomes de projetos, caminhos de arquivo nem o conteúdo das conversas.

## Configuração no app

Abra **Configurações › Envio para API › Configurar…** e preencha:

| Campo | Exemplo |
|---|---|
| Enviar o uso para uma API | ligado |
| URL base | `https://minha-api.vercel.app` ou `https://meu-dominio.com/cockpit` |
| Chave de autenticação | um segredo longo e aleatório, gerado por você |

- A rota é fixa: o app sempre envia para `{URL base}/v1/usage`.
- A URL precisa ser **HTTPS**. HTTP só é aceito para `localhost`, `127.0.0.1` e `::1`, para quem está desenvolvendo a API.
- A chave fica gravada em `%APPDATA%\UsageCockpit\settings.json`, cifrada com DPAPI. Só o seu usuário do Windows, nesta máquina, consegue decifrá-la.
- **Testar conexão** envia um payload real com `reason: "test"`, usando os valores digitados, sem salvar.

Para gerar uma chave, você pode rodar no PowerShell:

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

## Requisição

```http
POST {URL base}/v1/usage
Authorization: Bearer <chave>
Content-Type: application/json; charset=utf-8
User-Agent: UsageCockpit/<versão>
```

O app não segue redirecionamentos, para que a chave não seja reenviada a outro endereço. Configure a URL final.

### Corpo (`schemaVersion: 1`)

```json
{
  "schemaVersion": 1,
  "client": { "name": "UsageCockpit", "version": "0.4.0" },
  "sentAt": "2026-10-22T14:32:05.1234567-03:00",
  "reason": "activity",
  "paused": false,
  "healthEnabled": true,
  "providers": {
    "claude": {
      "label": "Claude Code",
      "enabled": true,
      "inUse": true,
      "lastActivityAt": "2026-10-22T14:32:01.0000000-03:00",
      "status": "ok",
      "error": null,
      "usage": {
        "fetchedAt": "2026-10-22T14:31:40.0000000-03:00",
        "source": "api",
        "session": { "percent": 42.5, "resetsAt": "2026-10-22T17:00:00-03:00", "windowMinutes": 300 },
        "week": { "percent": 18, "resetsAt": "2026-10-27T09:00:00-03:00", "windowMinutes": 10080 }
      }
    },
    "codex": {
      "label": "Codex (ChatGPT)",
      "enabled": true,
      "inUse": false,
      "lastActivityAt": null,
      "status": "error",
      "error": "Sem conexão com a OpenAI",
      "usage": {
        "fetchedAt": "2026-10-22T14:20:10.0000000-03:00",
        "source": "logs",
        "session": { "percent": 7, "resetsAt": "2026-10-22T18:10:00-03:00", "windowMinutes": 300 },
        "week": { "percent": 31, "resetsAt": "2026-10-25T11:00:00-03:00", "windowMinutes": 10080 }
      }
    }
  }
}
```

Todas as datas seguem a ISO 8601 com fuso (o fuso local do PC que enviou). Campos que podem ser `null` sempre aparecem no JSON. Novos campos podem surgir sem mudar `schemaVersion`, então ignore os que você não conhece. Uma mudança incompatível usaria `schemaVersion: 2` e uma nova rota, `/v2/usage`.

### Campos

**Raiz**

| Campo | Tipo | Descrição |
|---|---|---|
| `schemaVersion` | número | Versão do contrato. Hoje é `1`. |
| `client.name` / `client.version` | texto | Sempre `UsageCockpit` e a versão do app. |
| `sentAt` | data | Momento do envio. Use para saber se o PC está online (veja [Detectar PC offline](#detectar-pc-offline)). |
| `reason` | texto | Motivo do envio: `refresh`, `activity`, `heartbeat` ou `test`. |
| `paused` | booleano | `true` quando o usuário pausou as consultas, pelo botão ou pelo horário de trabalho. Veja [Consultas pausadas](#consultas-pausadas). |
| `healthEnabled` | booleano | `true` quando Configurações › Saúde do PC está com o monitoramento **e** o envio ligados, ou seja, quando a saúde está chegando em `/v1/health`. O painel do Echo Show usa este campo para escolher o layout (com ou sem a saúde). Mudar a opção faz um envio na hora, mesmo com as consultas pausadas. Apps anteriores à 0.8 não mandam o campo. |
| `providers` | objeto | Sempre com as chaves `claude` e `codex`. |

**`providers.<id>`**

| Campo | Tipo | Descrição |
|---|---|---|
| `label` | texto | Nome exibido no painel (`Claude Code`, `Codex (ChatGPT)`). |
| `enabled` | booleano | Se a conta está ligada em Configurações › Contas monitoradas. Uma conta desligada vai com `inUse: false`, `lastActivityAt: null`, `status: "no_data"` e `usage: null`. |
| `inUse` | booleano | A ferramenta gravou atividade nos últimos 12 s (é o que faz os ponteiros tremerem). |
| `lastActivityAt` | data ou `null` | Última atividade vista **desde que o app abriu**. |
| `status` | texto | `ok`, `stale`, `error` ou `no_data`. Veja abaixo. |
| `error` | texto ou `null` | A mensagem exibida no painel quando a última consulta falhou, em português. |
| `usage` | objeto ou `null` | A última leitura boa. É `null` se nunca houve leitura. |

**`status`**, avaliado nesta ordem:

| Valor | Quando |
|---|---|
| `no_data` | Nunca houve leitura, ou a conta está desligada. |
| `error` | A última consulta falhou. `usage` ainda traz a leitura anterior, e `error` traz o motivo. |
| `stale` | A leitura tem mais de 15 minutos. |
| `ok` | A leitura é recente e a última consulta deu certo. |

**`usage`**

| Campo | Tipo | Descrição |
|---|---|---|
| `fetchedAt` | data | Quando a leitura foi feita. Pode vir do cache em disco, logo depois de o app abrir. |
| `source` | texto | `api` para a leitura direta do provedor, `logs` para o fallback do Codex a partir dos logs de sessão locais. |
| `session.percent` | número | Uso da janela de 5 horas, de 0 a 100, sem arredondar. |
| `session.resetsAt` | data ou `null` | Quando a janela de 5 h reinicia. |
| `session.windowMinutes` | número | Sempre `300`. |
| `week.percent` | número | Uso da janela semanal, de 0 a 100, sem arredondar. |
| `week.resetsAt` | data ou `null` | Quando a janela semanal reinicia. |
| `week.windowMinutes` | número | Sempre `10080`. |

### Limites do `inUse`

A atividade é detectada observando os arquivos de sessão locais de cada ferramenta:

- **Claude Code**: `~/.claude/projects/**/*.jsonl`. **Não** inclui o uso no claude.ai.
- **Codex**: `~/.codex/sessions/**/rollout-*.jsonl`, ou seja, o Codex CLI e as extensões de IDE. **Não** inclui conversas no ChatGPT pelo navegador ou pelo app, que não deixam rastro no PC.

Os percentuais refletem a conta inteira, inclusive o uso feito em outros lugares. Só o `inUse` é limitado a este PC.

## Quando o app envia

| `reason` | Quando |
|---|---|
| `refresh` | Ao fim de cada rodada de consultas: na abertura, no intervalo configurado, quando uma ferramenta para de trabalhar e ao atualizar manualmente. |
| `activity` | Quando o Claude Code ou o Codex começa ou para de trabalhar. |
| `heartbeat` | Se nada foi enviado nos últimos 5 minutos, logo depois de salvar a configuração e ao pausar ou continuar as consultas. |
| `test` | O botão **Testar conexão**. |

Envios próximos são agrupados, com no máximo um a cada 15 segundos, o menor intervalo de consulta. O envio acontece em segundo plano e nunca trava o painel.

### Consultas pausadas

Ao pausar, o app faz um último envio com `paused: true` e para de enviar, inclusive os de `heartbeat`. Ao continuar, envia de novo com `paused: false`. Durante a pausa o `sentAt` envelhece normalmente: se o último envio recebido tiver `paused: true`, mostre a conta como pausada em vez de offline.

Uma API que ignora campos desconhecidos não precisa mudar.

## Respostas esperadas

| Resposta da API | O que o app faz |
|---|---|
| `2xx` | Sucesso. O corpo da resposta é ignorado. |
| `401` ou `403` | Considera a chave recusada e **para de enviar** até a configuração ser salva de novo. |
| `429` | Espera o tempo do `Retry-After` (no mínimo 60 s) antes de tentar de novo. |
| `3xx` | Erro: redirecionamentos não são seguidos. |
| Outra resposta, timeout (10 s) ou falha de rede | Tenta de novo com espera crescente: 30 s, 1 min, 2 min… até 10 min. |

O status do último envio aparece em Configurações › Envio para API.

## Detectar PC offline

O app manda pelo menos um envio a cada 5 minutos enquanto está aberto e com as consultas ativas. Se o último `sentAt` recebido tiver mais de uns 6 minutos, considere o PC desligado ou o app fechado. Nesse caso, trate `inUse` como `false` e os percentuais como os últimos conhecidos.

## Recomendações para a API

- Compare a chave em **tempo constante**, por exemplo com `crypto.timingSafeEqual` no Node.
- Use uma chave de **escrita**, só para o app, separada das chaves de **leitura** que seus consumidores usam.
- Rejeite corpos grandes. Um envio normal tem menos de 2 KB, então 16 KB é um limite folgado.
- Valide `schemaVersion === 1` e responda `400` para outros valores.
- Se a API for serverless (Vercel, Netlify, Cloudflare), a memória não persiste entre requisições. Guarde o último payload num armazenamento chave-valor, como Upstash Redis ou Vercel KV, com expiração (TTL) de alguns minutos.

### Exemplo mínimo (Next.js / Vercel, App Router)

```ts
// app/v1/usage/route.ts
import { timingSafeEqual } from "node:crypto";
import { Redis } from "@upstash/redis";

const redis = Redis.fromEnv();

function authorized(req: Request, secret: string) {
  const got = Buffer.from(req.headers.get("authorization") ?? "");
  const want = Buffer.from(`Bearer ${secret}`);
  return got.length === want.length && timingSafeEqual(got, want);
}

export async function POST(req: Request) {
  if (!authorized(req, process.env.INGEST_KEY!)) return new Response(null, { status: 401 });
  const text = await req.text();
  if (text.length > 16_384) return new Response(null, { status: 413 });
  const body = JSON.parse(text);
  if (body?.schemaVersion !== 1) return new Response(null, { status: 400 });
  if (body.reason !== "test") await redis.set("usage:current", body, { ex: 15 * 60 });
  return new Response(null, { status: 204 });
}

export async function GET(req: Request) {
  if (!authorized(req, process.env.READ_KEY!)) return new Response(null, { status: 401 });
  const current = await redis.get<{ sentAt: string }>("usage:current");
  if (!current) return Response.json({ online: false }, { status: 404 });
  const online = Date.now() - Date.parse(current.sentAt) < 6 * 60_000;
  return Response.json({ online, ...current });
}
```

### Testar localmente

Um servidor de teste em PowerShell, que só imprime o que chega:

```powershell
$l = [System.Net.HttpListener]::new(); $l.Prefixes.Add("http://localhost:8787/"); $l.Start()
while ($true) { $c = $l.GetContext(); $r = [IO.StreamReader]::new($c.Request.InputStream).ReadToEnd()
  "$($c.Request.HttpMethod) $($c.Request.Url.AbsolutePath) $($c.Request.Headers['Authorization'])`n$r"
  $c.Response.StatusCode = 204; $c.Response.Close() }
```

No app, use a URL base `http://localhost:8787` e qualquer chave.

## Saúde do PC (`/v1/health`)

Além do uso, o app pode enviar indicadores de saúde do PC: CPU, velocidade do processador, memória, discos, modo de energia, contagens de processos e os processos mais pesados, já com alertas calculados. Serve para mostrar num painel (como o do Echo Show) quando o PC está lento por falta de memória, disco cheio, modo de economia de energia ou tempo demais sem reiniciar.

O contrato completo, compartilhado com a API e com o painel, está em [`docs/propostas/contrato-saude-pc.md`](propostas/contrato-saude-pc.md). Esta seção resume o que o app envia.

### Configuração no app

Em **Configurações › Saúde do PC**:

| Opção | O que faz | Padrão |
|---|---|---|
| Monitorar a saúde do PC | Liga o coletor. Desligado, nada é lido. | Desligado |
| Enviar a saúde para a API | Envia para `{URL base}/v1/health`. Usa a URL e a chave de **Sistema › Envio para API**, que precisa estar ligado. | Ligado |

A mesma aba mostra um resumo ao vivo (CPU, memória comprometida, disco do sistema, velocidade da CPU, modo de energia e alertas atuais), o status do último envio e o botão **Testar envio**, que manda a leitura atual com `reason: "test"`.

### O que é enviado (e o que não é)

São enviados só números e **nomes de executáveis** (por exemplo `chrome`, sem `.exe`), agrupados por nome.

**Nunca são enviados** caminhos de arquivos ou de executáveis, linhas de comando, títulos de janelas, nome de usuário, nome do computador, rótulos de volume (só a letra, como `C:`) nem o conteúdo de nenhum programa.

A coleta só lê: não pede administrador, não encerra processos e não executa PowerShell nem outros programas.

### Requisição

```http
POST {URL base}/v1/health
Authorization: Bearer <chave>
Content-Type: application/json; charset=utf-8
User-Agent: UsageCockpit/<versão>
```

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

Datas em ISO 8601 com fuso. Campos que podem ser `null` sempre aparecem. Ignore campos desconhecidos.

| Campo | Descrição | De onde vem |
|---|---|---|
| `reason` | `interval` (a cada 15 s), `alert` (o `status` ou o conjunto de `code` dos alertas mudou), `heartbeat` (a configuração foi salva) ou `test` (botão Testar envio; não deve gravar estado nem histórico). | |
| `status` | `ok`, `warn` ou `danger`: o pior `level` entre os alertas. | |
| `uptimeSeconds` | Tempo desde o boot. | `GetTickCount64` |
| `cpu.percent` | Uso total médio desde o envio anterior (no máximo 15 s), de 0 a 100. | `GetSystemTimes`, a cada 5 s |
| `cpu.speedPercent` | "% Processor Performance" do Windows; passa de 100 com turbo. `null` se indisponível. | Contador do PDH pelo nome em inglês (não depende do idioma), a cada 30 s |
| `cpu.speedMHz` | `baseMHz × speedPercent / 100`. | |
| `cpu.baseMHz` | Clock base do processador. | `CallNtPowerInformation` |
| `memory.*` | Memória física total e disponível, memória comprometida (commit) e o limite dela, e o pool não paginado do kernel (`null` se indisponível). | `GlobalMemoryStatusEx` e `GetPerformanceInfo` |
| `power.source` | `ac`, `battery` ou `unknown`. | `GetSystemPowerStatus` |
| `power.mode` | Modo de energia efetivo do Windows 11: `efficiency`, `balanced`, `performance` ou `unknown`. | `PowerGetEffectiveOverlayScheme` |
| `power.batteryPercent` | 0 a 100, ou `null` sem bateria. | `GetSystemPowerStatus` |
| `disks` | Discos fixos prontos (até 26). `name` é só a letra; `system: true` no disco do Windows. | `DriveInfo`, a cada 30 s |
| `counts` | Processos, threads e handles do sistema; `longRunningHelpers` conta `node`, `node_repl`, `cmd`, `powershell`, `pwsh`, `conhost` e `bash` abertos há mais de 24 h. | `GetPerformanceInfo` e lista de processos |
| `topProcesses` | Até 8, agrupados pelo nome do executável e ordenados por `cpuPercent` e depois `memoryBytes`. `cpuPercent` é a média dos últimos 30 s, de 0 a 100 do PC inteiro; `memoryBytes` é a memória privada somada. | Lista de processos, a cada 30 s |
| `alerts` | Até 12, um por `code`, `danger` antes de `warn`. | Calculados no app (abaixo) |

### Alertas

| `code` | `warn` | `danger` |
|---|---|---|
| `cpu_high` | média de 5 min ≥ 70% | ≥ 90% |
| `memory_high` | `commitPercent` ≥ 75 | ≥ 90 |
| `disk_low` | um disco com `usedPercent` ≥ 85 (vale o pior disco) | ≥ 95 |
| `power_efficiency` | modo de eficiência ligado na tomada | — |
| `uptime_long` | ligado há 7 dias ou mais | 14 dias ou mais |
| `kernel_pool_high` | `kernelNonPagedBytes` ≥ 1 GB | ≥ 2 GB |
| `processes_high` | 500 processos ou mais | 800 ou mais |
| `helpers_stale` | 20 ou mais `longRunningHelpers` | — |

O `cpu_high` só aparece depois de 5 minutos de amostras (logo depois de abrir o app ou de voltar da suspensão, ainda não há média). `title` e `action` vêm em português, prontos para exibir.

### Quando o app envia

- A cada 15 s (o mesmo ritmo em que o painel do Echo Show consulta) e, na hora, quando o `status` ou o conjunto de alertas muda, com no máximo um envio a cada 15 s.
- **Pausa junto com as consultas de IA** (botão ou fim do horário de trabalho) e volta a enviar na hora ao continuar. Assim, fora do expediente nada chega à API e o painel do Echo Show entra em espera.
- As respostas são tratadas como no `/v1/usage`: `2xx` é sucesso; `401`/`403` param o envio da saúde até a configuração ser salva de novo; `429` respeita o `Retry-After`; outras falhas são tentadas de novo com espera crescente.

Para ver o JSON gerado no seu PC sem enviar nada, rode `UsageIACockpit.exe --health saude.json`: o app faz uma leitura real (cerca de 5 s), grava o arquivo e fecha.

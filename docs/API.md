# Envio do uso para uma API

O Usage IA Cockpit pode enviar o uso atual do Claude Code e do Codex (ChatGPT) para uma API sua. Com isso, os mesmos números do painel ficam disponíveis para outras aplicações: um dashboard, um bot, um widget no celular.

O app só **envia**. Você implementa a API que recebe, seguindo o contrato abaixo, e decide o que fazer com o dado: guardar o último valor, repassar ou exibir.

## O que é enviado (e o que não é)

São enviados apenas dados consolidados: percentuais de uso, horários de reset, origem e idade da leitura, mensagem de erro do painel e se a ferramenta está em uso agora.

**Nunca são enviados** tokens ou logins dos provedores, IDs de conta, e-mail, nomes de projetos, caminhos de arquivo nem o conteúdo das conversas.

As [tarefas do Claude Code e do Codex](#tarefas-do-claude-code-e-do-codex-v1agents) vão por outra rota (`/v1/agents`) e incluem textos curtos das conversas (nome da sessão, pergunta pendente, último pedido e início da resposta final).

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

### Tablet na rede local

Na mesma janela, a seção **Tablet na rede local** liga um segundo destino. É o app Android [usage-ia-tablet](../../usage-ia-tablet), que recebe as mesmas rotas (`/v1/usage`, `/v1/health` e `/v1/agents`) e mostra o painel do Echo Show no próprio tablet, sem passar pela internet.

| Campo | Exemplo |
|---|---|
| URL | `https://192.168.1.113:8787` (aparece na tela do tablet) |
| Chave | gerada pelo tablet no primeiro uso |
| Impressão digital | SHA-256 do certificado do tablet, com 64 dígitos hexadecimais (espaços e `:` são ignorados) |

- O tablet usa um certificado autoassinado. O app aceita a conexão só quando a impressão digital do certificado é a configurada. Nome do host e cadeia de certificação não contam. HTTP não é aceito.
- Cada destino tem a própria fila, com espera e novas tentativas independentes. Um tablet desligado não atrasa os envios para a API.
- Certificado diferente do configurado conta como chave recusada: os envios para o tablet param até a configuração ser salva de novo. Isso acontece quando o app do tablet é reinstalado do zero ou quando outro aparelho assume o IP.
- Basta um destino ligado (API ou tablet) para o app coletar as tarefas e instalar os hooks do Claude Code.
- **Ritmo:** a API mantém os intervalos mínimos descritos neste documento (15 s para uso e saúde, 2 s para tarefas, progresso a cada 10 s), para preservar a cota da Vercel e do Redis. O tablet não tem cota:
  - recebe cada mudança em até 200 ms;
  - recebe a saúde a cada leitura (5 s);
  - recebe o progresso das tarefas a cada segundo.
- **Responder pelo tablet** (só com o tablet ligado; com a API, nada disso roda):
  - **Como a resposta chega ao PC:** o app busca as respostas tocadas no painel com `GET {URL do tablet}/v1/answers?wait=25`, uma requisição longa com a mesma chave e o mesmo certificado. A conexão sai do PC, e não há porta aberta nele.
  - **Claude Code:** o hook `PermissionRequest` (timeout de 600 s) espera a resposta do tablet e devolve permitir, permitir sempre (com as regras sugeridas) ou negar. Também responde ao `AskUserQuestion` de uma pergunta e aprova o plano do `ExitPlanMode`. O diálogo do terminal continua valendo: se você responder no PC antes, o hook desiste.
  - **Codex, aprovações:** hook `PermissionRequest` em `~/.codex/hooks.json`, instalado só com o tablet ligado. O Codex pede para confiar no hook uma vez, em Configurações › Hooks (ou `/hooks` na CLI). O pedido só aparece na tela do Codex depois que o hook termina, por isso ele espera o tablet por no máximo 60 s.
  - **Codex, perguntas assíncronas e texto livre:** a resposta vai como mensagem do usuário na conversa, pelo `codex queue --thread <id> --message <texto>`. As perguntas que travam a tarefa (`request_user_input` síncrono) continuam sendo respondidas só no PC.
  - No envio de `/v1/agents`, `question.answerable: true` indica que dá para responder pelo tablet naquele momento.

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
      "errorCode": null,
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
      "errorCode": "network",
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
| `errorCode` | texto ou `null` | O tipo da falha, para o painel decidir o que mostrar sem ler a mensagem: `login_required`, `rate_limited`, `network` ou `other`. É `null` quando a última consulta deu certo. Veja [Login expirado](#login-expirado). Apps anteriores à 0.10 não mandam o campo. |
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

### Login expirado

Com `errorCode: "login_required"`, o login da ferramenta venceu ou foi recusado (no Claude Code, o token de `~/.claude/.credentials.json`). As consultas automáticas dessa conta param, e `usage` continua com a última leitura boa, que vai ficando velha. No app, os medidores da conta ficam cinza e parados, com o botão **Renovar login**. O painel deve fazer o mesmo: mostrar a leitura em cinza, sem animação, com um aviso de login expirado no lugar do horário de reset.

A conta volta sozinha quando o login é renovado: pelo botão do app, que roda `claude auth status`, ou ao abrir o Claude Code no PC.

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

## Tarefas do Claude Code e do Codex (`/v1/agents`)

Com o envio para API ligado, o app também manda as **tarefas** do Claude Code e do Codex: o que está executando agora, o que parou esperando uma resposta sua e o que terminou nas últimas 8 horas, com os tokens gastos. O painel do Echo Show usa isso para listar as tarefas, avisar quando chega uma pergunta e quando uma tarefa termina.

Uma tarefa é o pedido mais recente de cada sessão do Claude Code (ou conversa do Codex): do momento em que você mandou a mensagem até a resposta terminar. Vão os tokens desse pedido (`tokens`) e os da conversa inteira (`conversation`).

### O que é enviado

Diferente do `/v1/usage` e do `/v1/health`, aqui vão **textos das conversas**, cortados no app:

| Campo | Conteúdo | Limite |
|---|---|---|
| `name` | Nome da sessão no Claude Code ou da conversa no Codex | 200 caracteres |
| `project` | Nome da pasta do projeto (sem o caminho) | 120 |
| `question.text` | Texto da pergunta pendente, ou o comando/arquivo do pedido de permissão | 600 |
| `question.options` | Opções da pergunta (até 6) | 80 cada |
| `prompt` | Seu último pedido na sessão | 300 |
| `summary` | Início da resposta final, só em tarefas concluídas | 400 |

**Nunca são enviados** o id real da sessão (vai um código derivado dele, como `c-1a2b3c4d5e6f`), caminhos completos, o restante da conversa, conteúdo de arquivos nem saídas de ferramentas. Tudo é lido no próprio PC, dos arquivos que o Claude Code e o Codex já gravam.

### De onde vem cada informação

| | Claude Code | Codex |
|---|---|---|
| Sessões abertas, nome e estado | `~/.claude/sessions/<pid>.json` (`name`, `status`: busy, idle, waiting) | `~/.codex/sessions/**/rollout-*.jsonl` (`task_started`, `task_complete`) e `~/.codex/session_index.jsonl` (nome) |
| Pergunta pendente | Ferramenta `AskUserQuestion` sem resposta no transcript | Ferramenta `request_user_input` sem resposta no rollout |
| Pedido de permissão | Hooks `PermissionRequest` e `Notification` (abaixo) ou `status: waiting` | Não fica gravado: não é detectado |
| Tokens do pedido | `message.usage` de cada resposta do pedido, uma vez por `message.id`, mais os subagentes (`<sessão>/subagents/*.jsonl`) | Diferença do `total_token_usage` entre o início e o fim do pedido |
| Tokens da conversa | Soma de todas as respostas da sessão e dos subagentes (lê até 64 MB do transcript; acima disso, o começo fica de fora) | O `total_token_usage` mais recente |
| % do limite de 5 h | — | Diferença do `rate_limits.primary.used_percent` no mesmo intervalo (aproximado com outras tarefas rodando junto) |

### Hooks do Claude Code

O pedido de permissão não aparece no transcript. Para detectá-lo, o app acrescenta ao `~/.claude/settings.json` dois hooks que rodam `UsageIACockpit.exe --claude-hook`:

- `PermissionRequest` (matcher `*`): traz a ferramenta e o comando, arquivo ou URL.
- `Notification`: só os tipos `permission_prompt` e `elicitation_dialog` contam; `idle_prompt` é ignorado.

O hook grava um arquivo pequeno por sessão em `%APPDATA%\UsageCockpit\hooks` e sai sem responder nada, então não muda a decisão de permissão. A instalação:

- acontece quando o envio para API está ligado e a conta do Claude está ativa; desligando qualquer um dos dois, as entradas saem;
- preserva os outros hooks do arquivo e guarda uma cópia do original em `settings.json.usage-ia-cockpit.bak` na primeira vez;
- é desfeita pelo desinstalador (`UsageIACockpit.exe --remove-hooks`).

Limitação: depois que você aprova a permissão, o aviso só some quando a ferramenta termina e o resultado entra na conversa. Um comando longo aprovado continua aparecendo como "pede permissão" até acabar.

### Requisição

```http
POST {URL base}/v1/agents
Authorization: Bearer <chave>
Content-Type: application/json; charset=utf-8
```

```json
{
  "schemaVersion": 1,
  "client": { "name": "UsageCockpit", "version": "0.13.0" },
  "sentAt": "2026-11-23T12:10:04.1234567-03:00",
  "reason": "change",
  "paused": false,
  "rates": { "claude": 88.4, "codex": 64 },
  "tasks": [
    {
      "id": "c-1a2b3c4d5e6f",
      "provider": "claude",
      "name": "Echo Show Claude/Codex monitoring expansion",
      "project": "monitor-uso-ia",
      "state": "waiting",
      "since": "2026-11-23T12:09:10.0000000-03:00",
      "startedAt": "2026-11-23T12:01:00.0000000-03:00",
      "endedAt": null,
      "question": { "kind": "question", "text": "Quer que eu monte a proposta com essas regras?", "tool": null, "options": ["Montar proposta", "Agora não"] },
      "prompt": "Pode implementar a sua proposta nas três aplicações, desktop, API e painel.",
      "summary": null,
      "tokens": { "input": 31200, "cacheWrite": 85000, "output": 9800, "cacheRead": 2400000 },
      "conversation": { "input": 412000, "cacheWrite": 690000, "output": 183000, "cacheRead": 36000000 },
      "cost": { "request": 1.0436, "conversation": 18.0308, "model": "Claude Opus 5.5" },
      "tokensPerSecond": 0,
      "limitPercent": null
    }
  ]
}
```

| Campo | Descrição |
|---|---|
| `reason` | `change` (tarefa nova, mudança de estado ou de pergunta), `progress` (tokens de tarefas executando), `heartbeat` (sinal de vida), `paused` (consultas pausadas) ou `test` (não grava nada) |
| `paused` | `true` quando as consultas do app estão pausadas; `tasks` vai vazio |
| `rates.claude`, `rates.codex` | Tokens de saída por segundo, somando as tarefas executando da ferramenta (média de ~30 s). `null` com a conta desligada |
| `tasks[].state` | `running`, `waiting` (pergunta ou permissão pendente) ou `done` (terminou há menos de 8 h) |
| `tasks[].since` | Quando entrou no estado atual. `startedAt`: início do pedido; `endedAt`: fim, só em `done` |
| `tasks[].question` | Só em `waiting`. `kind`: `question` (pergunta com opções), `permission` (com `tool`, ex.: `Bash`) ou `input` (o Claude Code espera algo que não deu para identificar) |
| `tasks[].tokens` | `input`: entrada nova; `cacheWrite`: entrada gravada no cache; `output`: saída (inclui raciocínio); `cacheRead`: contexto lido do cache. O painel mostra `input + cacheWrite + output` |
| `tasks[].conversation` | Mesmos campos de `tokens`, somando a conversa inteira (todos os pedidos da sessão). Apps anteriores à 0.12 não mandam; o painel usa então os do pedido |
| `tasks[].cost` | Custo equivalente em US$ pelos preços da API (abaixo): `request` (último pedido), `conversation` (conversa toda) e `model` (nome do modelo do preço). `null` quando o modelo não está na tabela; ausente em apps anteriores à 0.13 |
| `tasks[].tokensPerSecond` | Saída por segundo da tarefa; `null` em `done` |
| `tasks[].limitPercent` | Só no Codex: quanto do limite de 5 h o pedido consumiu |

A lista vem ordenada (perguntas, execuções, concluídas) e tem no máximo 20 tarefas; com mais, as concluídas mais antigas ficam de fora. O corpo pode passar de 16 KB; a API aceita até 64 KB nesta rota.

### Quando o app envia

- **Na hora** (no máximo um envio a cada 2 s) quando surge ou some uma tarefa, muda o estado ou chega outra pergunta.
- A cada **10 s** enquanto há tarefa executando e os tokens mudaram.
- Sinal de vida a cada **60 s** com tarefa ativa, ou a cada **5 min** sem nenhuma.
- **Pausa junto com as consultas** (botão ou fim do horário de trabalho): manda uma vez `paused: true`, sem tarefas, e para de ler os arquivos.
- O app lê os arquivos uma vez por segundo, numa thread de fundo; nada é executado.

Para ver o JSON no seu PC sem enviar nada, rode `UsageIACockpit.exe --agents tarefas.json`: o app lê as tarefas por cerca de 5 s, grava o arquivo e fecha.

### Custo equivalente

O `cost` de cada tarefa soma cada tipo de token pelo preço da API do modelo, em US$ por milhão de tokens, como os fornecedores cobram: entrada nova, gravação no cache, saída (com o raciocínio) e leitura do cache. Nas assinaturas (Claude Pro/Max, planos do ChatGPT) o limite segue a mesma lógica, mas a fórmula não é publicada: o valor serve de comparação, não é cobrança. A tabela fica em `UsageCockpit/Core/ModelPrices.cs`.

| Modelo | Entrada | Saída | Leitura do cache | Gravação no cache | Fonte |
|---|---|---|---|---|---|
| Claude Opus 5.5 | 4 | 20 | 0,20 | 5 (5 min) / 8 (1 h) | Anthropic |
| Claude Sonnet 5.5 | 2 | 10 | 0,10 | 1,25× / 2× a entrada | Anthropic |
| Demais Claude (Fable, Mythos, Opus, Sonnet, Haiku) | tabela | tabela | tabela | 1,25× / 2× a entrada | Anthropic |
| GPT-5.6 Terra | 2 | 12 | 0,20 | 2 (sem preço publicado; usa o da entrada) | página do modelo na OpenAI |
| GPT-6.2 Sol | 2 | 10 | 0,10 | 2,50 | sites que acompanham os preços da OpenAI |

- **Claude:** o custo é calculado por resposta, com o modelo dela (`message.model`), e a gravação no cache se divide em 5 min e 1 h pelo `usage.cache_creation`.
- **Codex:** pelo modelo do `turn_context`, sobre os mesmos totais de `tokens` e `conversation`. Prompts acima de 272 mil tokens têm preço maior na OpenAI, e isso não entra na conta.
- Modelo fora da tabela: `cost` vai `null` e o painel não mostra custo para a tarefa.


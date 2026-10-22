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
  "client": { "name": "UsageCockpit", "version": "0.3.0" },
  "sentAt": "2026-10-22T14:32:05.1234567-03:00",
  "reason": "activity",
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
| `heartbeat` | Se nada foi enviado nos últimos 5 minutos, e também logo depois de salvar a configuração. |
| `test` | O botão **Testar conexão**. |

Envios próximos são agrupados, com no máximo um a cada 5 segundos. O envio acontece em segundo plano e nunca trava o painel.

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

O app manda pelo menos um envio a cada 5 minutos enquanto está aberto. Se o último `sentAt` recebido tiver mais de uns 6 minutos, considere o PC desligado ou o app fechado. Nesse caso, trate `inUse` como `false` e os percentuais como os últimos conhecidos.

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

# Envio de falso positivo ao Bitdefender

Onde enviar: pelo formulário de envio de amostras do Bitdefender (opção **False positive**) ou, se a TI fizer o envio, pelo console do GravityZone. Anexe os executáveis da release, de preferência num `.zip` com a senha `infected`, que é o padrão aceito pelos laboratórios de antivírus.

---

**Tipo:** False positive

**Detecção:** `Gen:Trojan.Heur.DNP.mm0@aS3vi9i` (On-Access)

**Arquivos:**

| Arquivo | SHA-256 |
|---|---|
| `UsageIACockpit-Portable.exe` | `<SHA-256>` |
| `UsageIACockpit-Setup-<versão>.exe` | `<SHA-256>` |

**Produto:** Usage IA Cockpit, versão `<versão>`

**Código-fonte (MIT):** https://github.com/willianas1/usage-ia-cockpit
**Releases, com os hashes SHA-256 publicados:** https://github.com/willianas1/usage-ia-cockpit/releases

**Description (English):**

Usage IA Cockpit is an open-source (MIT) WPF desktop widget for Windows, built on .NET 9. It shows how much of their Claude Code and OpenAI Codex usage limits the signed-in user has consumed. The detection is heuristic, and we believe it is triggered by the following behaviors, all of which are intended and documented in the source code:

- It reads the OAuth token that Claude Code stores in `%USERPROFILE%\.claude\.credentials.json` and the token that Codex stores in `%USERPROFILE%\.codex\auth.json`. It uses them only to call the vendors' own usage endpoints: `https://api.anthropic.com/api/oauth/usage` and `https://chatgpt.com/backend-api/wham/usage`. The tokens are never sent anywhere else.
- It watches `%USERPROFILE%\.claude\projects` and `%USERPROFILE%\.codex\sessions` for file changes to show whether the tool is currently active. As a fallback, it reads the `rate_limits` line from local Codex session logs.
- An optional "publish" feature, disabled by default, posts aggregated usage percentages (no tokens, accounts or conversation content) to an HTTPS URL that the user configures. Its API key is stored encrypted with DPAPI (CurrentUser).
- An optional "Start with Windows" setting writes the executable path to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- The portable build is a .NET single-file bundle. The installer is built with Inno Setup 6 and installs per user, without administrator rights.

The detection blocks deployment to our company's employees, where Bitdefender GravityZone is the corporate endpoint protection. Please review the files and remove the detection.

Contact: `<seu e-mail>`

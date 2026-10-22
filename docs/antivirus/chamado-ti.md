# Chamado para a TI: liberar o Usage IA Cockpit no GravityZone

**Assunto:** Liberação do Usage IA Cockpit (falso positivo do Bitdefender) para o time

Olá,

O Bitdefender está classificando o **Usage IA Cockpit**, um app interno que desenvolvi, como `Gen:Trojan.Heur.DNP`. É uma detecção heurística, não uma assinatura de malware conhecido. Vocês já liberaram o app no meu computador. Agora preciso que a liberação valha para **as pessoas do time que vão instalá-lo**.

## O que o app faz

É um painel pequeno, sempre visível, que mostra quanto o usuário já gastou dos limites do Claude Code e do Codex (ChatGPT). O código é aberto (licença MIT): https://github.com/willianas1/usage-ia-cockpit

Entendo que a heurística disparou porque o app tem comportamentos parecidos com os de um programa que rouba credenciais. Cada um deles tem um motivo legítimo:

| Comportamento | Para quê |
|---|---|
| Lê `~/.claude/.credentials.json` e `~/.codex/auth.json` | Usa o token de login do próprio usuário **só** para consultar o uso dele na Anthropic e na OpenAI. O token nunca é enviado para outro lugar. |
| Acessa a internet | Faz apenas estas consultas: `https://api.anthropic.com/api/oauth/usage` e `https://chatgpt.com/backend-api/wham/usage`. |
| Envio opcional para uma API | Fica **desligado por padrão**. Só funciona se o usuário configurar uma URL HTTPS. Envia apenas percentuais e horários, nunca tokens, e-mails ou conteúdo de conversas. O contrato está em `docs/API.md` no repositório. |
| Usa DPAPI (`ProtectedData`) | Cifra a chave desse envio opcional com a conta do Windows do usuário. |
| Grava em `HKCU\...\CurrentVersion\Run` | Opção "Iniciar com o Windows", que o usuário liga ou desliga nas configurações. |
| Observa `~/.claude/projects` e `~/.codex/sessions` | Detecta se a ferramenta está em uso agora, vendo só quando os arquivos mudam. Se a consulta à OpenAI falhar, lê os logs do Codex apenas para achar a linha com os limites de uso (`rate_limits`). O conteúdo das conversas não é guardado nem enviado. |

## O que preciso

1. **Exclusão na política do GravityZone** aplicada aos computadores do time (não só ao meu):
   - de preferência **por certificado do editor**, se for possível (veja o item 2);
   - se não for, **por hash SHA-256** de cada versão. Os hashes são publicados em cada release do GitHub. Versão atual:
     - `UsageIACockpit-Portable.exe`: `<SHA-256>`
     - `UsageIACockpit-Setup-<versão>.exe`: `<SHA-256>`
   - Caminhos de instalação, como alternativa: `%LOCALAPPDATA%\Programs\UsageIACockpit\` (o instalador não pede administrador) ou a pasta escolhida para o portátil.
2. **Certificado de code signing da CA interna**, se a empresa tiver uma (AD CS). Com ele, assino os executáveis e a exclusão passa a valer para todas as versões, sem precisar atualizar hashes. O processo de build já está pronto para assinar.
3. **Envio do falso positivo ao Bitdefender** pelo console do GravityZone, se vocês puderem. Envios de clientes corporativos costumam ser analisados mais rápido.

Fico à disposição para mostrar o código ou fazer uma demonstração.

Obrigado,
Willian

# Instruções para agentes

## Nova versão

Quando o usuário pedir uma nova versão, ela é gerada pelo GitHub Actions, não por um build local. O workflow `.github/workflows/release.yml` roda o `build.ps1 -Installer` a partir da tag e publica o exe portátil, o instalador e o `SHA256SUMS.txt` na Release. Os arquivos que o time baixa são esses; um build local gera hashes diferentes e não serve para distribuir.

O pedido de nova versão já autoriza o commit, a tag e o push descritos abaixo.

1. Atualize a `<Version>` em `UsageCockpit/UsageCockpit.csproj`.
2. No `README.md`, em "Publicar uma nova versão", troque a versão do exemplo de `git tag` e `git push`.
3. Faça um commit só com essas mudanças, com a mensagem `Versão X.Y.Z`. Se houver outras alterações sem commit, pergunte ao usuário se elas entram na versão antes de continuar.
4. Crie a tag `vX.Y.Z` (formato `v1.2.3`, sufixo opcional como `v1.2.3-beta.1`) e envie o commit e a tag para o `origin`.
5. Acompanhe o workflow e, quando a Release sair, informe ao usuário os hashes do `SHA256SUMS.txt`. A TI usa esses hashes na exclusão do Bitdefender GravityZone e no envio de falso positivo (veja `docs/antivirus/`).

Não rode o `build.ps1` localmente como parte da versão. Rode só se o usuário pedir um build local.

## Build e antivírus

O Bitdefender, antivírus corporativo de quem usa o app, já acusou o app como `Gen:Trojan.Heur` (falso positivo heurístico). Para não aumentar a chance de detecção:

- Não reative o `EnableCompressionInSingleFile` no `build.ps1`: um exe que se descompacta sozinho é tratado como suspeito.
- Não ofusque o código nem use packers.
- A assinatura com `signtool` é opcional (`-CertThumbprint` ou `-PfxPath`) e só vale com um certificado confiável, como o da CA interna da empresa. Um certificado autoassinado não ajuda nos computadores dos outros usuários.

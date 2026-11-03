# Detalhamento do consumo do Chrome: três investigações independentes

Data: 30/10/2026. Status: proposta de experimentos; nenhuma das alternativas foi validada nesta máquina.

## 1. Objetivo e contexto para uma nova conversa

O usuário utiliza o **Usage IA Cockpit** nesta máquina Windows e quer descobrir quais abas, extensões ou serviços do Google Chrome estão consumindo mais CPU e memória. A consulta será feita na **página web do painel usado no dispositivo Echo**, identificado como Echo Show na documentação do projeto.

O recurso será inicialmente de uso individual. O usuário não quer encerrar processos pelo aplicativo ou pelo painel: ao identificar uma tarefa problemática, irá ao gerenciador de tarefas do computador para agir manualmente.

O resultado desejado é uma lista com **nome da tarefa, tipo, PID, CPU, memória e horário da leitura**, ordenável por CPU ou memória. A coleta precisa continuar enquanto o usuário trabalha em outra janela ou consulta o painel web. Uma solução que exige manter uma janela aberta pode ser aceitável como experimento, mas essa dependência deve ficar explícita.

As três investigações são:

1. **UI Automation:** ler a tabela do Gerenciador de tarefas do Chrome pela acessibilidade do Windows.
2. **Depuração/CDP:** obter identificação e métricas por interfaces de depuração do Chrome.
3. **Extensão com `chrome.processes`:** obter a relação entre tarefas e processos pela API específica, atualmente documentada para o canal Dev.

Cada investigação deve produzir um protótipo isolado e um relatório. A escolha e a integração ao aplicativo, à API e ao painel vêm depois da comparação.

### O que já foi observado

O agente conseguiu ler uma captura do Gerenciador de Tarefas do Windows, mas sua ferramenta de Computer Use bloqueou a leitura da janela interna do Chrome porque não conseguiu determinar a URL para aplicar suas regras de segurança. Isso **não testou a viabilidade de uma implementação própria no aplicativo**. Tampouco autoriza contornar uma restrição da ferramenta: quando uma ferramenta negar acesso, registrar a limitação e respeitar suas instruções.

As leituras pontuais daquela conversa não são um diagnóstico atual e não devem ser reutilizadas como resultado dos experimentos.

## 2. Estado atual do projeto

Repositório local: `D:\Repositorio\monitor-uso-ia`.

| Componente | Situação verificada no código/documentação |
|---|---|
| Aplicativo | C# / WPF, `net9.0-windows` |
| Coleta | `UsageCockpit/Core/SystemHealthMonitor.cs`, principalmente `Collector.SampleProcesses()` |
| Identificação atual | Enumera processos e usa PID para calcular deltas de CPU; depois agrupa pelo nome do executável |
| Memória dos processos | `PrivateMemorySize64`, somada por grupo |
| CPU dos processos | Variação de `TotalProcessorTime`, dividida pelo intervalo e pelo número de processadores lógicos |
| Frequência | Indicadores gerais a cada 5 segundos; processos a cada 30 segundos |
| Publicação | `UsageCockpit/Core/HealthPublisher.cs`, `POST /v1/health`, em intervalos de 15 segundos |
| Contrato atual | `schemaVersion: 1`; `HealthProcess` contém apenas nome, quantidade, CPU e memória agregados |
| API e painel | Repositórios separados: `usage-ia-api` e `usage-ia-cockpit-echo-show` |
| Leitura do painel | O contrato descreve consulta à visão `compact`, que reduz a lista de processos |
| Limite documentado | Corpo de saúde menor que 16 KB |
| Pausa | O envio acompanha a pausa das consultas; o coletor local pode continuar |

Referências locais: [SystemHealthMonitor.cs](../../UsageCockpit/Core/SystemHealthMonitor.cs), [HealthPublisher.cs](../../UsageCockpit/Core/HealthPublisher.cs), [contrato de saúde](contrato-saude-pc.md), [API](../API.md) e [AGENTS.md](../../AGENTS.md).

O fluxo futuro seria:

```text
Chrome + Windows
       |
       v
Coletor local: identificação das tarefas + consumo dos processos
       |
       v
Usage IA Cockpit --> API de saúde --> página web no Echo
```

A página web exibirá a última coleta recebida. O fluxo atual não oferece um canal para um clique remoto abrir uma janela no computador. Não presumir esse canal: o detalhamento deve chegar por coleta periódica.

## 3. Regras comuns de interpretação

### Processo e aba não são equivalentes

Uma aba pode envolver diversos processos, e um processo pode hospedar várias tarefas. GPU, rede e outras funções podem atender muitas páginas. Portanto, o resultado inicial mais fiel é **consumo por processo, acompanhado das tarefas associadas**. Um ranking de abas com consumo exclusivo exige evidências adicionais. Essa diferença decorre do [modelo de processos do Chromium](https://chromium.googlesource.com/chromium/src/+/main/docs/process_model_and_site_isolation.md).

Regras para todos os protótipos:

- Somar cada PID apenas uma vez. Se duas tarefas compartilharem um PID, apresentar o consumo como compartilhado.
- Não distribuir igualmente o consumo entre abas e apresentar essa divisão como medição real.
- Registrar tarefas sem PID e processos sem identificação; não inferir a associação pela ordem das listas ou pela semelhança dos valores.
- Distinguir PID do Windows, identificador interno de processo do Chrome, `tabId`, `targetId` e `sessionId`.
- Invalidar associações quando houver navegação, troca de processo, fechamento ou reinício. Associar PID ao horário de início do processo quando acessível, porque PIDs podem ser reutilizados.
- Registrar a cobertura: quantos processos do Chrome foram medidos e quantos puderam ser relacionados a tarefas.

### Métricas comparáveis

A CPU do aplicativo representa a capacidade do PC inteiro, de 0 a 100. Para medições do Windows, manter a fórmula:

```text
CPU do PC (%) = 100 × delta de tempo de CPU do processo
                     / (tempo real transcorrido × processadores lógicos)
```

A primeira amostra ainda não tem delta: usar estado de aquecimento ou valor ausente. Após suspensão, reinício ou mudança de processo, renovar a referência. Preservar o valor e a escala de origem ao comparar com o Chrome.

Memória privada, memória residente, o valor da coluna de memória do Chrome e heap JavaScript não são automaticamente equivalentes. Cada valor deve informar sua métrica. O objetivo inicial é manter a memória privada do Windows para a comparação com o app atual e aproveitar o Chrome para identificar a tarefa.

### Autonomia e atualidade

Avaliar separadamente:

- operação após a configuração inicial;
- continuidade com outras janelas em primeiro plano;
- funcionamento com a janela necessária minimizada;
- recuperação após reiniciar o Chrome ou o coletor;
- dependência de confirmação do usuário a cada conexão;
- comportamento após suspensão e retomada do PC.

Registrar horário de medição e horário da última associação válida. Uma CPU recente com um nome antigo pode identificar a tarefa errada. Quando a associação vencer, mostrar o processo como não identificado ou o detalhe como desatualizado. Ausência de dado não é consumo zero.

## 4. Alternativa 1 — UI Automation do Gerenciador do Chrome

### Hipótese

Um coletor C# consegue localizar a janela do Gerenciador de tarefas do Chrome e ler seus elementos de acessibilidade, extraindo o nome da tarefa e o PID. Os números de CPU e memória podem vir do Windows, com os valores da tabela usados para conferência.

O Chrome documenta suporte nativo a UI Automation no Windows a partir da versão 138. Isso sustenta investigar o caminho, mas **não garante que todas as linhas e colunas do gerenciador sejam expostas**. [Fonte oficial](https://developer.chrome.com/blog/windows-uia-support-update)

### Preparação

Registrar versão do Windows, versão/canal do Chrome e quantidade de perfis ou instâncias em uso. Trabalhar com o Chrome habitual. Identificar especificamente seu gerenciador interno, aberto por `Shift + Esc`, distinguindo-o do Gerenciador de Tarefas do Windows.

Para o primeiro teste, a janela pode ser aberta manualmente e a coluna de PID habilitada. A possibilidade de localizá-la ou abri-la pelo aplicativo deve ser avaliada posteriormente, de forma separada da leitura.

### Experimento

1. Criar um coletor descartável em `tools/chrome-diagnostics/uia/`, sem modificar o monitor principal.
2. Localizar a janela e inspecionar apenas sua árvore de acessibilidade. Registrar quais padrões de tabela, linhas, células e texto estão disponíveis.
3. Extrair nome, tipo quando informado e PID. Guardar uma amostra local para conferência manual com a tabela.
4. Confirmar se a tabela oferece todas as tarefas ou apenas as linhas visíveis. Testar linhas fora da área de rolagem, grupos com várias tarefas e filtros/categorias do gerenciador.
5. Fazer duas leituras do consumo dos PIDs pelo Windows. Cruzar com a identificação e produzir JSON local, sem publicar na API.
6. Repetir a leitura com outra janela em primeiro plano, com o gerenciador coberto e com ele minimizado. Observar se os dados continuam mudando; conseguir ler um valor antigo não prova atualização em segundo plano.
7. Observar abertura, navegação e fechamento de abas de teste, sem encerrar os processos reais do usuário. Conferir se os PIDs e nomes são atualizados corretamente.
8. Observar o que acontece ao fechar e reabrir o gerenciador. Medir quanto tempo leva para recuperar a associação e como o coletor sinaliza a indisponibilidade.
9. Medir custo durante pelo menos 10 minutos, começando com intervalo de 15 segundos. Comparar com uma linha de base sem o coletor e considerar também o custo do gerenciador do Chrome aberto.

Usar uma thread dedicada para a automação, sem bloquear a interface WPF. Limitar as buscas à janela relevante e evitar percorrer todo o desktop a cada ciclo. A orientação de threading e COM MTA está na [documentação da Microsoft](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading). Definir tempo limite e recuperação para leituras que travem; considerar um processo auxiliar se for necessário isolar chamadas bloqueadas.

### O que decide o resultado

| Resultado observado | Classificação |
|---|---|
| Lê nomes e PIDs com atualização em segundo plano e custo aceitável | Candidata principal para uso individual |
| Funciona apenas com o gerenciador aberto, mas coberto | Viável com essa dependência documentada |
| Depende de foco, cliques ou rolagem em cada coleta | Inadequada para a consulta remota desejada |
| Lê somente parte das linhas | Parcial; informar cobertura e verificar se os maiores consumidores podem ficar ausentes |
| Expõe nomes sem PIDs confiáveis | Insuficiente para cruzar com as medições atuais |
| A ferramenta do agente bloqueia o teste | Não testada; distinguir de falha técnica do protótipo |

### Limitações esperadas e entrega

A leitura depende da interface, idioma, layout e comportamento da versão instalada. Uma atualização do Chrome pode exigir manutenção. A janela pode precisar permanecer aberta; não prometer funcionamento invisível antes do teste.

Entregar código isolado, comando de execução, exemplo sanitizado de saída, cobertura, custo e tabela dos estados da janela testados. Relatório: `docs/experimentos/chrome-uia.md`.

## 5. Alternativa 2 — Depuração do Chrome / CDP

### Hipótese e alcance

Uma conexão de depuração pode identificar alvos do navegador e obter métricas de atividade das páginas. O ponto a provar é se, na versão instalada, também conseguimos relacionar essas páginas aos PIDs necessários para o consumo completo do processo.

**Listar abas e listar processos separadamente não prova a relação entre eles.** A definição consultada de `TargetInfo` traz identificadores, tipo, título e URL, mas não um campo explícito de PID do Windows. `SystemInfo.getProcessInfo` oferece informações de processos e tempo acumulado de CPU; isso não fornece, por si só, a ligação com cada aba. [Target](https://github.com/ChromeDevTools/devtools-protocol/blob/master/pdl/domains/Target.pdl) e [SystemInfo](https://github.com/ChromeDevTools/devtools-protocol/blob/master/pdl/domains/SystemInfo.pdl).

### Formas de conexão a avaliar

Escolher uma forma inicialmente. Estas são variantes da mesma alternativa, não três experimentos adicionais obrigatórios.

| Forma | Aplicação no experimento | Dependência |
|---|---|---|
| Conexão à sessão existente, pelo fluxo documentado para Chrome 144+ e DevTools MCP | Verificar as páginas reais do usuário após a configuração inicial | O fluxo documentado exige habilitação e confirmação de conexão; não presumir reconexão silenciosa nem cliente C# pronto |
| Porta ou pipe de depuração com diretório de dados separado | Validar capacidades em um ambiente de teste | O teste alcança essa instância; não demonstra acesso às abas já abertas no perfil habitual |
| Extensão auxiliar com `chrome.debugger` | Instrumentar abas pela API de extensão | Permissão `debugger`, avisos e restrições de comandos/targets; não equivale a uma sessão de depuração com acesso irrestrito ao navegador |

O Google documenta a conexão à sessão existente e a confirmação por sessão no [fluxo de conexão do DevTools MCP](https://developer.chrome.com/blog/chrome-devtools-mcp-debug-your-browser-session). Para as flags tradicionais, desde o Chrome 136 o diretório padrão de dados não é aceito; o teste deve usar um diretório próprio, conforme a [documentação de depuração remota](https://developer.chrome.com/blog/remote-debugging-port). Não copiar o perfil habitual para simular acesso às abas reais.

A extensão com `chrome.debugger` é um transporte para um subconjunto do protocolo. Inspecionar os domínios permitidos antes de depender de um comando, especialmente `SystemInfo` e rastreamento. [API debugger](https://developer.chrome.com/docs/extensions/reference/api/debugger)

### Experimento

1. Criar o protótipo em `tools/chrome-diagnostics/cdp/`. Registrar a forma de conexão, versão do navegador e alcance: perfil real ou instância de teste.
2. Listar alvos, preservando `targetId`, tipo e título. Acompanhar criação, mudança e desaparecimento dos alvos.
3. Consultar as métricas disponíveis via `Performance.enable` e `Performance.getMetrics`, quando suportadas. Registrar os nomes, unidades e escopo efetivamente retornados. [Definição de Performance](https://github.com/ChromeDevTools/devtools-protocol/blob/master/pdl/domains/Performance.pdl)
4. Distinguir tempo gasto em tarefas, tempo de CPU e memória JavaScript. Não chamar um índice de atividade do renderer de CPU total do processo. Não apresentar heap JavaScript como RAM total da aba.
5. Em conexão com acesso ao navegador, verificar `SystemInfo.getProcessInfo` e comparar seus identificadores com o Windows. Isso é uma validação separada da associação de abas.
6. Investigar uma relação documentada ou evidência verificável entre alvo/frame e PID. Caso seja necessário um trace curto, registrar o evento que sustenta a relação, a versão e o custo. Validar com o gerenciador interno; não converter uma heurística em identificação confirmada.
7. Se não existir relação confiável, entregar uma visão parcial por alvo, com métricas próprias e PID ausente. Manter o ranking por PID do Windows separado e declarar que o objetivo completo não foi atingido.
8. Testar páginas em segundo plano, workers, iframes e abas descartadas. Verificar se anexar o depurador altera a suspensão de abas ou carrega páginas que estavam descarregadas, distorcendo o consumo.
9. Testar desconexão, reinício e navegação. Documentar quando o usuário precisa confirmar novamente e quais associações são invalidadas.
10. Medir custo em uma sessão comparável por pelo menos 10 minutos. Perfis e traces detalhados devem ser pontuais; avaliar sua necessidade antes de incorporá-los a uma coleta contínua.

### O que decide o resultado

- **Completo:** relação alvo/tarefa–PID demonstrada, consumo do Windows cruzado corretamente, cobertura e funcionamento em segundo plano comprovados.
- **Parcial útil:** identifica páginas com maior atividade ou heap JavaScript, mas não atribui consumo total do processo. Pode ajudar no diagnóstico, com rótulos claros.
- **Condicional:** exige confirmar cada reconexão ou navegar em uma instância específica. Avaliar se a rotina pessoal aceita isso.
- **Inadequado ao objetivo:** só lista abas, usa associações especulativas ou altera de forma relevante o consumo que pretende medir.

### Entrega

Código isolado, instruções de conexão e desconexão, lista dos comandos suportados, exemplo sanitizado, evidência da associação quando existir e custo medido. Relatório: `docs/experimentos/chrome-cdp.md`. Conectar um agente via MCP pode ajudar no teste, mas não é, sozinho, a implementação de um coletor autônomo no aplicativo.

## 6. Alternativa 3 — Extensão com `chrome.processes`

### Hipótese e requisito decisivo

A API `chrome.processes` é o caminho mais direto entre os três para obter tarefas e processos do próprio navegador. A documentação consultada restringe sua disponibilidade ao **canal Dev**. Ativar o modo de desenvolvedor das extensões no Chrome Stable não troca o canal do navegador. [API processes](https://developer.chrome.com/docs/extensions/reference/api/processes)

Para uso individual, pode valer a investigação, mas é necessário considerar **onde estão as abas que se deseja monitorar**. Uma extensão no Chrome Dev não passa a monitorar as abas de outra instância do Chrome Stable. Um teste em um perfil vazio comprova a API, não o diagnóstico da sessão habitual.

### Preparação

Registrar o canal instalado e verificar a disponibilidade real da API com uma extensão mínima. Se for necessário instalar o Chrome Dev, preparar as instruções e combinar a execução com o usuário. Usar perfil separado no teste; não alterar o canal ou migrar o perfil habitual como efeito colateral da investigação.

### Experimento

1. Criar a extensão de prova em `tools/chrome-diagnostics/processes-api/extension/`. Começar pela detecção da capacidade, antes de criar uma ponte com o aplicativo.
2. Consultar processos e tarefas pela API. Usar `getProcessIdForTab` quando necessário; cruzar o ID interno retornado com `osProcessId`. Registrar tipos e tarefas disponíveis, inclusive serviços sem aba.
3. Comparar uma amostra com o gerenciador do Chrome e com os processos do Windows. Testar mais de uma tarefa por processo e mudanças após navegação.
4. Preservar a escala de CPU da origem: a API documenta percentual relativo a um núcleo, potencialmente superior a 100. Para comparar ao app, normalizar pelo número de processadores lógicos ou usar a medição do Windows. `privateMemory` e métricas de JavaScript também devem manter rótulos distintos. [Campos e métodos da API](https://developer.chrome.com/docs/extensions/reference/api/processes)
5. Validar o funcionamento em segundo plano e o ciclo de vida do service worker da extensão. Demonstrar retomada após ociosidade e reinício, sem depender de um popup aberto ou de um temporizador que pode parar.
6. Depois da leitura validada, preparar a comunicação com um coletor local. Preferir **Native Messaging**, com `allowed_origins` limitado à extensão. No Windows, é possível registrar o host no escopo do usuário. O host recebe mensagens por `stdin`/`stdout`; um aplicativo WPF já aberto precisará de uma ponte local ou de um modo auxiliar apropriado. [Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)
7. No primeiro protótipo, a ponte pode apenas escrever um JSON temporário. Não é necessário modificar o aplicativo para provar a integração.
8. Testar reconexão, mais de um perfil, fechamento do navegador e invalidação dos PIDs. Uma extensão instalada em um perfil não deve ser presumida como fornecedora de metadados de todos os outros; medir essa cobertura.
9. Medir custo por pelo menos 10 minutos, incluindo extensão e host, e comparar com a mesma carga sem coleta.

### O que decide o resultado

| Resultado observado | Interpretação |
|---|---|
| API disponível, PIDs corretos e coleta contínua | Forte candidata técnica dentro do navegador/canal testado |
| Funciona apenas em uma instância Dev usada para teste | Prova técnica válida, mas não cobre a sessão habitual do usuário |
| Exige que o usuário passe a usar outro canal para a navegação monitorada | Decisão de rotina a avaliar; uso individual reduz a dificuldade de distribuição, mas não elimina essa dependência |
| API indisponível no canal testado | Registrar como não suportada nessa configuração; não atribuir a uma falha de permissão genérica |
| Relação incompleta entre perfis ou tarefas | Informar cobertura; não apresentar o conjunto como todo o Chrome |

### Entrega

Extensão mínima, ponte local se necessária, instruções de instalação e remoção dos componentes do teste, exemplo sanitizado e evidências do canal/perfil alcançado. Relatório: `docs/experimentos/chrome-processes-api.md`.

## 7. Saída comum para comparar os experimentos

Usar um formato local de comparação, ainda **sem promover a proposta a contrato da API**:

| Campo | Significado |
|---|---|
| `source` | `uia`, `cdp` ou `processes-api` |
| `browserVersion`, `channel`, `sessionId` | Ambiente e sessão do coletor; `sessionId` aqui é próprio do experimento |
| `capturedAt`, `mappingCapturedAt` | Horários da medição e da associação dos nomes |
| `status`, `reason` | Disponível, parcial, desatualizado, desconectado ou não suportado, com explicação |
| `coverage` | PIDs medidos, PIDs relacionados e quantidade de itens omitidos |
| `processes[]` | Uma entrada por processo do Windows |
| `processes[].pid`, `startedAt` | Identidade; horário de início ausente se não puder ser consultado |
| `processes[].cpuPercent`, `sampleSeconds`, `cpuScope` | Valor, intervalo e escala; preferir `machine` quando normalizado como o app |
| `processes[].memoryBytes`, `memoryMetric` | Valor e tipo, por exemplo `privateBytes` |
| `processes[].tasks[]` | Nomes, tipos e identificadores das tarefas associadas; título não é chave única |
| `processes[].mappingStatus` | Confirmada, parcial, ausente ou desatualizada |
| `targets[]` | Métricas de alvos CDP sem relação confirmada com PID; não misturar com o ranking por processo |

Se só uma tarefa conhecida for encontrada em um PID, isso não comprova que ela é sua única ocupante. Quando a cobertura for incompleta, evitar afirmar consumo exclusivo da aba.

O protótipo deve guardar dados reais em diretório temporário local. Exemplos e relatórios versionáveis devem usar nomes sanitizados. Para a futura visualização, priorizar título e PID; URL completa, conteúdo de páginas e credenciais não são necessários para este objetivo.

## 8. Integração futura com API e página web

Só executar esta etapa depois de selecionar uma alternativa. Os três experimentos não devem modificar simultaneamente o contrato publicado.

1. Preservar a lista agregada atual e propor um detalhe opcional do Chrome. Decidir entre campo adicional e rota própria após conferir validação e limites da API real.
2. Conferir a visão `compact`, pois o painel a utiliza: adicionar um campo no desktop não garante que ele chegará à página.
3. Medir o tamanho serializado em UTF-8. Uma lista com dezenas de processos e títulos pode ultrapassar o limite documentado de 16 KB. Considerar os maiores por CPU e por memória, sem perder um dos rankings, com truncamento explícito, ou separar o detalhe em outra rota.
4. Manter data da coleta, cobertura e estado de disponibilidade. A publicação a cada 15 segundos pode carregar medições de processos coletadas a cada 30 segundos; informar a idade real.
5. Respeitar as configurações de pausa e de envio de saúde existentes.
6. Na web, permitir expandir Chrome, ordenar por CPU/memória e visualizar nome, PID e tarefas compartilhadas. Manter serviços como GPU e rede identificados quando houver evidência, sem atribuí-los a uma aba específica.
7. Não acrescentar comandos de encerramento, descarte, navegação ou controle remoto. O usuário age manualmente no computador.

## 9. Comparação e recomendação inicial

| Critério | UI Automation | Depuração / CDP | Extensão `processes` |
|---|---|---|---|
| Uso da sessão habitual | Hipótese favorável, depende da leitura da janela | Depende da conexão e dos alvos acessíveis | Depende de a sessão estar no canal suportado |
| Relação com PID | Precisa provar extração da coluna | Precisa investigar; não vem automaticamente da lista de alvos | API oferece ID do sistema operacional |
| Consumo completo do processo | Windows após cruzar PID | Windows se a relação for comprovada | Windows ou métricas da API com escala identificada |
| Janela do gerenciador aberta | Pode ser necessária | Não é requisito da coleta pelo protocolo | Não é requisito da API |
| Configuração extra | Leitor local e possível janela aberta | Depuração ou extensão auxiliar | Canal suportado, extensão e possível host |
| Principal risco técnico | Cobertura e atualização da árvore acessível | Relação alvo–PID e impacto da instrumentação | Canal de uso e continuidade da extensão |

**Ordem sugerida: UI Automation, CDP e API de processos.** Essa ordem reduz a mudança inicial na rotina de navegação. Cada investigação pode ser executada independentemente, sem esperar a anterior. Nenhuma deve ser apresentada como aprovada antes da medição local.

Usar a mesma carga de navegação para comparar. Se as conversas forem abertas simultaneamente, coordenar os testes reais do navegador: depuração, extensão e gerenciador podem alterar o consumo um do outro. Cada contexto deve escrever apenas no seu diretório de protótipo e no seu relatório.

O relatório final de cada alternativa deve responder:

1. O que foi implementado e qual comando reproduz o teste?
2. Quais versões, perfis e instâncias foram efetivamente cobertos?
3. A relação tarefa–PID foi comprovada? Como?
4. Quais métricas são completas e quais são parciais?
5. Funciona em segundo plano e após reconexão? Exige ação do usuário?
6. Qual a cobertura, o atraso e o custo adicional medidos?
7. Qual a conclusão: viável, viável com condições, parcial, inviável na configuração ou não testada?

## 10. Prompts para executar em conversas separadas

Os prompts abaixo são independentes. Cada um manda ler este documento completo para recuperar o contexto. Abrir outra conversa não cria isolamento de arquivos automaticamente; respeitar os diretórios indicados e o trabalho já existente.

### Conversa 1 — UI Automation

```text
Trabalhe no repositório D:\Repositorio\monitor-uso-ia. Leia AGENTS.md e o documento docs/propostas/detalhamento-chrome-tres-alternativas.md completo, especialmente a alternativa 1 e as regras comuns.

Execute apenas a investigação por UI Automation do Gerenciador de tarefas do Chrome. O objetivo é identificar tarefas e PIDs para depois mostrar CPU e memória na página web do meu painel Echo. É uso individual na minha máquina. Quero apenas leitura; o encerramento será manual por mim.

Crie uma prova de conceito isolada em tools/chrome-diagnostics/uia/ e registre os resultados em docs/experimentos/chrome-uia.md. Comprove extração dos nomes/PIDs, cobertura das linhas e atualização fora de foco, com janela coberta e minimizada. Cruze PIDs com consumo do Windows, trate tarefas compartilhadas e dados antigos, e meça o custo. Respeite as restrições das ferramentas; se não conseguir executar algum teste, registre-o como não testado.

Não altere o aplicativo principal, a API, o painel ou arquivos de outras investigações. Não finalize processos ou abas reais. Não gere release, não rode build.ps1 e não faça commit ou push. Um protótipo independente pode ser compilado e executado conforme necessário. Entregue a evidência, o comando de reprodução e uma conclusão de viabilidade.
```

### Conversa 2 — Depuração / CDP

```text
Trabalhe no repositório D:\Repositorio\monitor-uso-ia. Leia AGENTS.md e o documento docs/propostas/detalhamento-chrome-tres-alternativas.md completo, especialmente a alternativa 2 e as regras comuns.

Execute apenas a investigação por depuração/CDP do Chrome. O objetivo é identificar tarefas e PIDs para depois mostrar CPU e memória na página web do meu painel Echo. É uso individual na minha máquina. Quero apenas leitura; o encerramento será manual por mim.

Crie uma prova de conceito isolada em tools/chrome-diagnostics/cdp/ e registre os resultados em docs/experimentos/chrome-cdp.md. Descubra as capacidades reais da versão instalada e a forma de conexão adequada. Diferencie a sessão habitual de uma instância de teste. Liste alvos, examine métricas e prove a relação com PID antes de atribuir consumo total a uma aba. Se só conseguir atividade ou heap JavaScript, entregue esse resultado como parcial. Avalie custo, segundo plano, abas descartadas e reconexão. Respeite confirmações e restrições do navegador e das ferramentas; não migre meu perfil ou altere configurações de depuração silenciosamente.

Não altere o aplicativo principal, a API, o painel ou arquivos de outras investigações. Não finalize processos ou abas reais. Não gere release, não rode build.ps1 e não faça commit ou push. Um protótipo independente pode ser compilado e executado conforme necessário. Entregue a evidência, o comando de reprodução e uma conclusão de viabilidade.
```

### Conversa 3 — Extensão com API de processos

```text
Trabalhe no repositório D:\Repositorio\monitor-uso-ia. Leia AGENTS.md e o documento docs/propostas/detalhamento-chrome-tres-alternativas.md completo, especialmente a alternativa 3 e as regras comuns.

Execute apenas a investigação da extensão com chrome.processes. O objetivo é identificar tarefas e PIDs para depois mostrar CPU e memória na página web do meu painel Echo. É uso individual na minha máquina. Quero apenas leitura; o encerramento será manual por mim.

Crie a prova de conceito em tools/chrome-diagnostics/processes-api/ e registre os resultados em docs/experimentos/chrome-processes-api.md. Verifique primeiro o canal e a disponibilidade da API. Modo de desenvolvedor de extensões no Stable não equivale ao Chrome Dev. Se precisar instalar outro canal, prepare as instruções e solicite minha participação antes da instalação; preserve o perfil habitual. Comprove quais abas a instância realmente cobre, relacione o ID interno a osProcessId, normalize CPU e teste continuidade em segundo plano. Depois da leitura validada, prepare uma ponte local com Native Messaging se necessária, começando por saída JSON local.

Não altere o aplicativo principal, a API, o painel ou arquivos de outras investigações. Não finalize processos ou abas reais. Não gere release, não rode build.ps1 e não faça commit ou push. Um protótipo independente pode ser compilado e executado conforme necessário. Entregue a evidência, instruções de execução e remoção dos componentes de teste e uma conclusão de viabilidade.
```

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace UsageCockpit.Core;

// Contrato das tarefas (schemaVersion 1): docs/API.md, seção "Tarefas (/v1/agents)".
// A API e o painel do Echo Show leem estes nomes: mudar um campo aqui exige mudar lá.

public sealed record AgentsPayload(
    int SchemaVersion,
    PublishClient Client,
    DateTimeOffset SentAt,
    string Reason,
    bool Paused,
    AgentRates Rates,
    IReadOnlyList<AgentTask> Tasks,
    DailyModels? Models);

/// <summary>Saída por segundo de cada ferramenta (soma das tarefas executando). Nulo: ferramenta desligada.</summary>
public sealed record AgentRates(double? Claude, double? Codex);

/// <summary>
/// Envia as tarefas do Claude Code e do Codex para POST {urlBase}/v1/agents, com a URL e a chave do
/// envio do uso. Mudança de estado (nova tarefa, pergunta, conclusão) vai na hora, com no máximo um
/// envio a cada 2 s; enquanto há tarefa executando, os tokens vão a cada 10 s; sem nada mudando, um
/// sinal de vida a cada 60 s (com tarefa ativa) ou 5 min. Pausa junto com as consultas.
/// </summary>
public sealed class AgentsPublisher : ApiPublisher
{
    public const string Route = "v1/agents";
    public const int SchemaVersion = 1;

    static readonly TimeSpan MinGap = TimeSpan.FromSeconds(2);
    static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(10);
    /// <summary>No tablet, os tokens e a velocidade andam a cada leitura do monitor (1 s).</summary>
    static readonly TimeSpan TabletProgressEvery = TimeSpan.FromSeconds(1);
    static readonly TimeSpan LiveHeartbeat = TimeSpan.FromSeconds(60);
    static readonly TimeSpan IdleHeartbeat = TimeSpan.FromMinutes(5);

    readonly AgentTasksMonitor _monitor;
    string? _signature;
    long _tokens;

    public AgentsPublisher(AppSettings settings, AgentTasksMonitor monitor) : base(settings, Route, MinGap)
    {
        _monitor = monitor;
    }

    public AgentTasksMonitor Monitor => _monitor;

    /// <summary>Consultas pausadas: avisa a API uma vez (paused: true, sem tarefas) e para.</summary>
    public bool Paused { get; private set; }

    protected override bool Wanted => Settings.ShowClaude || Settings.ShowCodex;

    /// <summary>"paused" e "change" não são trocados por um envio de rotina que chegue enquanto esperam.</summary>
    protected override string MergeReason(string pending, string reason) =>
        Rank(reason) >= Rank(pending) ? reason : pending;

    static int Rank(string reason) => reason switch { "paused" => 3, "change" => 2, "heartbeat" => 1, _ => 0 };

    /// <summary>Liga ou desliga o monitor e os hooks conforme a configuração e o estado de pausa.</summary>
    public void Apply()
    {
        _monitor.ClaudeEnabled = Settings.ShowClaude;
        _monitor.CodexEnabled = Settings.ShowCodex;
        if (Configured && !Paused) _monitor.Start();
        else _monitor.Stop();
    }

    public void SetPaused(bool paused)
    {
        if (Paused == paused) return;
        Paused = paused;
        Apply();
        _signature = null;
        if (Configured) Enqueue(paused ? "paused" : "change");
    }

    /// <summary>A configuração mudou: esquece falhas e envia o estado atual.</summary>
    public void Reconfigure()
    {
        ResetFailures();
        Apply();
        _signature = null;
        if (Configured) Enqueue(Paused ? "paused" : "change");
        else ClearOutcomes();
    }

    /// <summary>Chamado na thread da interface a cada leitura do monitor (1 s).</summary>
    public void OnUpdated()
    {
        if (Paused || !Configured) return;
        var tasks = _monitor.Tasks;
        var signature = string.Join("|", tasks.Select(t =>
            $"{t.Id}:{t.State}:{t.StartedAt.ToUnixTimeSeconds()}:{t.Name}:{t.Question?.Text}"));
        var live = tasks.Any(t => t.State != AgentStates.Done);
        var tokens = tasks.Sum(t => t.Tokens.Output + t.Tokens.Input + t.Tokens.CacheWrite);

        if (signature != _signature)
        {
            _signature = signature;
            _tokens = tokens;
            Enqueue("change");
        }
        else if (live && tokens != _tokens && IdleFor(ProgressEvery, TabletProgressEvery))
        {
            _tokens = tokens;
            Enqueue("progress", ProgressEvery, TabletProgressEvery);
        }
        else
        {
            Enqueue("heartbeat", live ? LiveHeartbeat : IdleHeartbeat);
        }
    }

    public string PreviewJson() => JsonSerializer.Serialize(Build("test"), PrettyJson);

    protected override object BuildPayload(string reason) => Build(reason);

    AgentsPayload Build(string reason)
    {
        var paused = Paused || reason == "paused";
        return new AgentsPayload(
            SchemaVersion,
            new PublishClient("UsageCockpit", AppVersion),
            DateTimeOffset.Now,
            reason,
            paused,
            new AgentRates(Settings.ShowClaude ? _monitor.ClaudeRate : null, Settings.ShowCodex ? _monitor.CodexRate : null),
            paused ? Array.Empty<AgentTask>() : _monitor.Tasks,
            paused ? null : _monitor.Models);
    }
}

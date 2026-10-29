using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace UsageCockpit.Core;

// Contrato da saúde do PC (schemaVersion 1): docs/propostas/contrato-saude-pc.md e docs/API.md.
// A API e o painel do Echo Show leem estes nomes: mudar um campo aqui exige mudar lá.

public sealed record HealthPayload(
    int SchemaVersion,
    PublishClient Client,
    DateTimeOffset SentAt,
    string Reason,
    string Status,
    long UptimeSeconds,
    HealthCpu Cpu,
    HealthMemory Memory,
    HealthPower Power,
    IReadOnlyList<HealthDisk> Disks,
    HealthCounts Counts,
    IReadOnlyList<HealthProcess> TopProcesses,
    IReadOnlyList<HealthAlert> Alerts);

public sealed record HealthCpu(double Percent, double? SpeedPercent, double? SpeedMHz, double? BaseMHz);

public sealed record HealthMemory(
    long TotalBytes,
    long AvailableBytes,
    double UsedPercent,
    long CommitBytes,
    long CommitLimitBytes,
    double CommitPercent,
    long? KernelNonPagedBytes);

public sealed record HealthPower(string Source, string Mode, int? BatteryPercent);

public sealed record HealthDisk(
    string Name,
    [property: JsonPropertyName("system")] bool IsSystem,
    long TotalBytes,
    long FreeBytes,
    double UsedPercent);

public sealed record HealthCounts(int Processes, int Threads, int Handles, int LongRunningHelpers);

public sealed record HealthProcess(string Name, int Instances, double CpuPercent, long MemoryBytes);

public sealed record HealthAlert(string Code, string Level, string Title, string Action);

/// <summary>
/// Envia a saúde do PC para POST {urlBase}/v1/health, com a mesma URL e a mesma chave do envio do uso.
/// A cada 15 s e na hora (no máximo um envio a cada 15 s) quando o status ou o conjunto de alertas muda.
/// Ao contrário do uso, continua enviando com as consultas pausadas e fora do horário de trabalho.
/// </summary>
public sealed class HealthPublisher : ApiPublisher
{
    public const string Route = "v1/health";
    public const int SchemaVersion = 1;

    // No mesmo ritmo em que o painel do Echo Show consulta a API (15 s): cada consulta traz dado novo.
    static readonly TimeSpan MinGap = TimeSpan.FromSeconds(15);
    static readonly TimeSpan IntervalEvery = TimeSpan.FromSeconds(15);

    readonly SystemHealthMonitor _monitor;
    string? _lastSignature;
    string? _waitingReason;
    DateTimeOffset _lastPayloadAt = DateTimeOffset.MinValue;

    public HealthPublisher(AppSettings settings, SystemHealthMonitor monitor) : base(settings, Route, MinGap)
    {
        _monitor = monitor;
        monitor.Sampled += OnSampled;
    }

    public SystemHealthMonitor Monitor => _monitor;

    /// <summary>Envio ligado nas três chaves: saúde monitorada, saúde enviada e envio para API configurado.</summary>
    protected override bool Configured =>
        Settings.HealthEnabled && Settings.HealthPublishEnabled && Settings.PublishEnabled;

    protected override bool Ready => _monitor.Latest != null;

    /// <summary>"alert" e "heartbeat" não são trocados por um "interval" que chegue enquanto esperam.</summary>
    protected override string MergeReason(string pending, string reason) =>
        reason == "interval" && pending != "interval" ? pending : reason;

    /// <summary>Liga ou desliga o coletor conforme a configuração e manda um heartbeat se o envio estiver ligado.</summary>
    public void Apply()
    {
        if (Settings.HealthEnabled)
            _monitor.Start();
        else
            _monitor.Stop();
        Reconfigure();
    }

    /// <summary>A configuração mudou (aqui ou no envio para API): esquece falhas e manda um heartbeat.</summary>
    public void Reconfigure()
    {
        ResetFailures();
        if (Configured)
            Request("heartbeat");
        else
        {
            _waitingReason = null;
            SetOutcome(null);
        }
    }

    void Request(string reason)
    {
        if (_monitor.Latest == null)
        {
            // Coletor recém-ligado: envia assim que a primeira leitura chegar.
            _waitingReason = _waitingReason == null ? reason : MergeReason(_waitingReason, reason);
            return;
        }
        Enqueue(reason);
    }

    void OnSampled()
    {
        if (_monitor.Latest is not { } r) return;
        var signature = r.Status + ":" + string.Join(",", r.Alerts.Select(a => a.Code).OrderBy(c => c, StringComparer.Ordinal));

        if (_waitingReason is { } waiting)
        {
            _waitingReason = null;
            _lastSignature = signature;
            Enqueue(waiting);
        }
        else if (_lastSignature == null)
        {
            _lastSignature = signature;
            Enqueue("interval");
        }
        else if (signature != _lastSignature)
        {
            _lastSignature = signature;
            Enqueue("alert");
        }
        else if (DateTimeOffset.Now - LastAttemptAt >= IntervalEvery)
        {
            Enqueue("interval");
        }
    }

    /// <summary>Botão "Testar envio": manda a leitura atual com reason "test" para a URL e a chave salvas.</summary>
    public Task<PublishOutcome> TestAsync()
    {
        if (_monitor.Latest == null)
            return Task.FromResult(new PublishOutcome(false,
                Settings.HealthEnabled ? "Aguarde a primeira leitura (cerca de 5 s)" : "Ligue o monitoramento primeiro",
                DateTimeOffset.Now));
        return TestAsync(Settings.PublishBaseUrl, Settings.PublishKey ?? "");
    }

    /// <summary>JSON exatamente como seria enviado agora, ou nulo sem leitura. Usado no modo --health.</summary>
    public string? PreviewJson() => _monitor.Latest == null ? null : JsonSerializer.Serialize(Build("test"), PrettyJson);

    protected override object BuildPayload(string reason) => Build(reason);

    HealthPayload Build(string reason)
    {
        var r = _monitor.Latest ?? throw new InvalidOperationException("Sem leitura da saúde do PC");
        var now = DateTimeOffset.Now;

        // CPU média desde o envio anterior, no máximo 15 s. O teste não conta como envio.
        var window = now - _lastPayloadAt;
        if (window > IntervalEvery || window <= TimeSpan.Zero) window = IntervalEvery;
        if (reason != "test") _lastPayloadAt = now;

        return new HealthPayload(
            SchemaVersion,
            new PublishClient("UsageCockpit", AppVersion),
            now,
            reason,
            r.Status,
            r.UptimeSeconds,
            new HealthCpu(_monitor.CpuAverage(window), r.SpeedPercent, r.SpeedMHz, r.BaseMHz),
            r.Memory,
            r.Power,
            r.Disks,
            r.Counts,
            r.TopProcesses,
            r.Alerts);
    }
}

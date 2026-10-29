using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsageCockpit.Core;

// Contrato do envio (schemaVersion 1). Documentado em docs/API.md: mudar um campo aqui exige mudar lá.

public sealed record PublishPayload(
    int SchemaVersion,
    PublishClient Client,
    DateTimeOffset SentAt,
    string Reason,
    bool Paused,
    bool HealthEnabled,
    IReadOnlyDictionary<string, PublishProvider> Providers);

public sealed record PublishClient(string Name, string Version);

public sealed record PublishProvider(
    string Label,
    bool Enabled,
    bool InUse,
    DateTimeOffset? LastActivityAt,
    string Status,
    string? Error,
    PublishUsage? Usage);

public sealed record PublishUsage(DateTimeOffset FetchedAt, string Source, PublishUsageWindow Session, PublishUsageWindow Week);

public sealed record PublishUsageWindow(double Percent, DateTimeOffset? ResetsAt, int WindowMinutes);

/// <summary>
/// Envia o uso consolidado para uma API configurada pelo usuário: POST {urlBase}/v1/usage com
/// Authorization: Bearer. Nunca envia tokens dos provedores, só percentuais, horários e estado.
/// Todas as chamadas vêm da thread da interface; o envio em si é assíncrono e não bloqueia nada.
/// </summary>
public sealed class UsagePublisher : ApiPublisher
{
    public const string Route = "v1/usage";
    public const int SchemaVersion = 1;

    // Igual ao menor intervalo de consulta: no máximo um envio por consulta, mesmo com atividade.
    static readonly TimeSpan MinGap = TimeSpan.FromSeconds(AppSettings.MinPollSeconds);
    static readonly TimeSpan HeartbeatEvery = TimeSpan.FromMinutes(5);

    readonly MainViewModel _vm;

    public UsagePublisher(AppSettings settings, MainViewModel vm) : base(settings, Route, MinGap)
    {
        _vm = vm;
    }

    protected override bool Configured => Settings.PublishEnabled;

    /// <summary>Pede um envio. Pedidos próximos são agrupados (no máximo um a cada 30 s). Pausado, não envia.</summary>
    public void Publish(string reason)
    {
        if (_vm.IsPaused) return;
        Enqueue(reason);
    }

    /// <summary>Pausou ou voltou: avisa a API uma vez (campo paused); pausado, nada mais é enviado.</summary>
    public void PauseChanged() => Enqueue("heartbeat");

    /// <summary>
    /// A saúde do PC foi ligada ou desligada: avisa a API (campo healthEnabled) mesmo com as consultas
    /// pausadas, para o painel trocar de layout sem esperar a próxima consulta.
    /// </summary>
    public void HealthChanged() => Enqueue("heartbeat");

    /// <summary>Chamado periodicamente: manda um sinal de vida se nada foi enviado nos últimos 5 min.</summary>
    public void HeartbeatIfDue()
    {
        if (DateTimeOffset.Now - LastAttemptAt >= HeartbeatEvery)
            Publish("heartbeat");
    }

    /// <summary>A configuração mudou: esquece falhas anteriores e envia logo.</summary>
    public void Reconfigure()
    {
        ResetFailures();
        if (Settings.PublishEnabled)
            Publish("heartbeat");
        else
            SetOutcome(null);
    }

    /// <summary>JSON exatamente como seria enviado agora; usado como exemplo na janela de configuração.</summary>
    public string PreviewJson() => JsonSerializer.Serialize(Build("test"), PrettyJson);

    /// <summary>Monta {urlBase}/v1/usage. Exige HTTPS; HTTP só para localhost.</summary>
    public static Uri? Endpoint(string? baseUrl, out string? error) => Endpoint(baseUrl, Route, out error);

    protected override object BuildPayload(string reason) => Build(reason);

    PublishPayload Build(string reason) => new(
        SchemaVersion,
        new PublishClient("UsageCockpit", AppVersion),
        DateTimeOffset.Now,
        reason,
        _vm.IsPaused,
        // Só é "ligada" se a saúde também vai para a API: sem envio, o painel não teria o que mostrar.
        Settings.HealthEnabled && Settings.HealthPublishEnabled,
        new Dictionary<string, PublishProvider>
        {
            ["claude"] = Describe(_vm.Claude, Settings.ShowClaude),
            ["codex"] = Describe(_vm.Codex, Settings.ShowCodex),
        });

    static PublishProvider Describe(ProviderViewModel p, bool enabled)
    {
        // Conta desligada não é consultada nem observada: vai sem leitura e sem atividade.
        if (!enabled)
            return new PublishProvider(p.Label, false, false, null, "no_data", null, null);

        var s = p.Last;
        var status = s == null ? "no_data" : p.HasError ? "error" : p.IsStale ? "stale" : "ok";
        var usage = s == null ? null : new PublishUsage(
            s.FetchedAt.ToLocalTime(),
            s.Source,
            new PublishUsageWindow(s.SessionPercent, s.SessionResetsAt?.ToLocalTime(), 5 * 60),
            new PublishUsageWindow(s.WeekPercent, s.WeekResetsAt?.ToLocalTime(), 7 * 24 * 60));
        return new PublishProvider(p.Label, true, p.IsActive, p.LastActivityAt, status, p.Error, usage);
    }
}

/// <summary>Cifra textos com a conta do Windows (DPAPI): só o mesmo usuário, nesta máquina, consegue ler.</summary>
public static class Secret
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UsageCockpit.Publish");

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy,
                DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null; // copiado de outro usuário/máquina: o usuário digita a chave de novo
        }
    }
}

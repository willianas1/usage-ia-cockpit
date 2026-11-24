using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UsageCockpit.Core;

/// <summary>Resultado de um envio, exibido nas configurações.</summary>
public sealed record PublishOutcome(bool Ok, string Message, DateTimeOffset At);

/// <summary>Para onde vão os envios: a API configurada (HTTPS público) e o tablet na rede local.</summary>
public enum PublishTarget { Api, Tablet }

/// <summary>
/// Envio para a API configurada pelo usuário (POST {urlBase}/{rota}, Authorization: Bearer), comum ao
/// uso (/v1/usage), à saúde do PC (/v1/health) e às tarefas (/v1/agents). O mesmo envio pode ir também
/// para o tablet na rede local, por HTTPS com o certificado fixado pela impressão digital. Cada destino
/// tem a própria fila: agrupa pedidos próximos, espera mais a cada falha, para de vez com 401/403 até a
/// configuração ser salva de novo e respeita o Retry-After do 429. Um destino fora do ar não atrasa o outro.
/// Todas as chamadas vêm da thread da interface; o envio em si é assíncrono e não bloqueia nada.
/// </summary>
public abstract class ApiPublisher
{
    static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    protected static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    protected static readonly JsonSerializerOptions PrettyJson = new(JsonOptions) { WriteIndented = true };

    // Sem seguir redirecionamentos: a chave não pode ir parar em outro endereço.
    static readonly HttpClient Client = NewClient(new HttpClientHandler { AllowAutoRedirect = false });

    static readonly object TabletLock = new();
    static (string Pin, HttpClient Client)? _tabletClient;

    static HttpClient NewClient(HttpClientHandler handler)
    {
        var c = new HttpClient(handler) { Timeout = Timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"UsageCockpit/{AppVersion}");
        return c;
    }

    /// <summary>
    /// Cliente do tablet: aceita só o certificado com a impressão digital SHA-256 configurada. O nome
    /// do host e a cadeia não importam (o certificado é autoassinado e o endereço é um IP da rede local).
    /// </summary>
    static HttpClient TabletClient(string pin)
    {
        lock (TabletLock)
        {
            if (_tabletClient is { } c && c.Pin == pin) return c.Client;
            var client = NewClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                    cert != null && CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(cert.GetCertHashString(HashAlgorithmName.SHA256).ToUpperInvariant()),
                        Encoding.ASCII.GetBytes(pin)),
            });
            _tabletClient = (pin, client);
            return client;
        }
    }

    /// <summary>Impressão digital só com os dígitos hexadecimais, em maiúsculas (aceita espaços e dois-pontos).</summary>
    public static string NormalizePin(string? pin) =>
        new string((pin ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    /// <summary>Estado da fila de um destino.</summary>
    sealed class Channel(PublishTarget target)
    {
        public readonly PublishTarget Target = target;
        public string? Pending;
        public bool Flushing;
        public bool KeyRejected;
        public int Failures;
        public DateTimeOffset LastAttemptAt = DateTimeOffset.MinValue;
        public DateTimeOffset RetryAt = DateTimeOffset.MinValue;
        public CancellationTokenSource Wait = new();
        public PublishOutcome? Outcome;
    }

    protected readonly AppSettings Settings;
    readonly string _route;
    readonly TimeSpan _minGap;
    readonly Channel[] _channels = [new(PublishTarget.Api), new(PublishTarget.Tablet)];

    protected ApiPublisher(AppSettings settings, string route, TimeSpan minGap)
    {
        Settings = settings;
        _route = route;
        _minGap = minGap;
    }

    public static string AppVersion
    {
        get
        {
            var v = typeof(ApiPublisher).Assembly.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Resultado do último envio para a API.</summary>
    public PublishOutcome? LastOutcome => _channels[0].Outcome;

    /// <summary>Resultado do último envio para o tablet.</summary>
    public PublishOutcome? TabletOutcome => _channels[1].Outcome;

    public event Action? OutcomeChanged;

    /// <summary>Se este recurso deve ser enviado (independente do destino).</summary>
    protected abstract bool Wanted { get; }

    /// <summary>Se a configuração atual pede envios: o recurso ligado e algum destino ligado.</summary>
    protected bool Configured => Wanted && Settings.AnyPublishEnabled;

    /// <summary>Se já há o que enviar. Sem dados, o pedido pendente é descartado.</summary>
    protected virtual bool Ready => true;

    protected abstract object BuildPayload(string reason);

    /// <summary>Qual motivo fica quando chega um pedido com outro já pendente. Padrão: o mais novo.</summary>
    protected virtual string MergeReason(string pending, string reason) => reason;

    bool DestinationEnabled(PublishTarget t) => t == PublishTarget.Api ? Settings.PublishEnabled : Settings.TabletEnabled;

    bool Enabled(Channel c) => Wanted && DestinationEnabled(c.Target) && !c.KeyRejected;

    /// <summary>
    /// Pede um envio a cada destino ligado. Com <paramref name="ifIdleFor"/>, só aos destinos sem
    /// tentativa nesse intervalo (sinais de vida). Pedidos próximos são agrupados (no máximo um a cada
    /// intervalo mínimo, por destino).
    /// </summary>
    protected void Enqueue(string reason, TimeSpan? ifIdleFor = null)
    {
        var now = DateTimeOffset.Now;
        foreach (var c in _channels)
        {
            if (!Enabled(c)) continue;
            if (ifIdleFor is { } idle && now - c.LastAttemptAt < idle) continue;
            c.Pending = c.Pending == null ? reason : MergeReason(c.Pending, reason);
            if (!c.Flushing)
                _ = FlushAsync(c);
        }
    }

    /// <summary>Se algum destino ligado está sem tentativa de envio há pelo menos esse tempo.</summary>
    protected bool IdleFor(TimeSpan span)
    {
        var now = DateTimeOffset.Now;
        return _channels.Any(c => Enabled(c) && now - c.LastAttemptAt >= span);
    }

    /// <summary>A configuração mudou: esquece falhas anteriores e libera um envio imediato.</summary>
    protected void ResetFailures()
    {
        foreach (var c in _channels)
        {
            c.KeyRejected = false;
            c.Failures = 0;
            c.RetryAt = DateTimeOffset.MinValue;
            c.Wait.Cancel();
            c.Wait = new CancellationTokenSource();
            // Destino desligado: o status dele some das configurações.
            if (!DestinationEnabled(c.Target)) c.Outcome = null;
        }
    }

    async Task FlushAsync(Channel c)
    {
        c.Flushing = true;
        try
        {
            while (c.Pending != null)
            {
                var notBefore = c.LastAttemptAt + _minGap > c.RetryAt ? c.LastAttemptAt + _minGap : c.RetryAt;
                var wait = notBefore - DateTimeOffset.Now;
                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, c.Wait.Token); }
                    catch (TaskCanceledException) { }
                }
                if (!Enabled(c) || !Ready || c.Pending is not { } reason)
                {
                    c.Pending = null;
                    break;
                }
                c.Pending = null;
                c.LastAttemptAt = DateTimeOffset.Now;

                var (baseUrl, key, pin) = Destination(c.Target);
                if (Endpoint(c.Target, baseUrl, _route, out var error) is not { } url)
                {
                    SetOutcome(c, new PublishOutcome(false, error!, DateTimeOffset.Now));
                    break;
                }
                var outcome = await SendAsync(c.Target, url, key, pin, BuildPayload(reason));
                SetOutcome(c, outcome.Result);
                if (outcome.Result.Ok)
                {
                    c.Failures = 0;
                    c.RetryAt = DateTimeOffset.MinValue;
                }
                else if (outcome.KeyRejected)
                {
                    // Chave ou certificado errado não melhora sozinho: para até o usuário corrigir.
                    c.KeyRejected = true;
                }
                else
                {
                    c.Failures++;
                    var backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 30 * Math.Pow(2, c.Failures - 1)));
                    c.RetryAt = DateTimeOffset.Now + (outcome.RetryAfter is { } ra && ra > backoff ? ra : backoff);
                }
            }
        }
        finally
        {
            c.Flushing = false;
        }
    }

    (string? BaseUrl, string Key, string? Pin) Destination(PublishTarget t) => t == PublishTarget.Api
        ? (Settings.PublishBaseUrl, Settings.PublishKey ?? "", null)
        : (Settings.TabletBaseUrl, Settings.TabletKey ?? "", Settings.TabletCertSha256);

    void SetOutcome(Channel c, PublishOutcome? outcome)
    {
        c.Outcome = outcome;
        OutcomeChanged?.Invoke();
    }

    /// <summary>Limpa o status de todos os destinos (envio desligado).</summary>
    protected void ClearOutcomes()
    {
        foreach (var c in _channels) c.Outcome = null;
        OutcomeChanged?.Invoke();
    }

    /// <summary>Envia um payload com reason "test" para a URL e a chave dadas, sem salvar nada.</summary>
    public Task<PublishOutcome> TestAsync(string? baseUrl, string key) => TestAsync(PublishTarget.Api, baseUrl, key, null);

    /// <summary>Teste para um destino específico; no tablet, <paramref name="pin"/> é a impressão digital.</summary>
    public async Task<PublishOutcome> TestAsync(PublishTarget target, string? baseUrl, string key, string? pin)
    {
        if (Endpoint(target, baseUrl, _route, out var error) is not { } url)
            return new PublishOutcome(false, error!, DateTimeOffset.Now);
        return (await SendAsync(target, url, key, pin, BuildPayload("test"))).Result;
    }

    /// <summary>
    /// Monta {urlBase}/{rota}. Exige HTTPS; HTTP só para localhost, para quem está desenvolvendo a API.
    /// </summary>
    public static Uri? Endpoint(string? baseUrl, string route, out string? error) =>
        Endpoint(PublishTarget.Api, baseUrl, route, out error);

    /// <summary>Como o outro Endpoint; para o tablet, exige HTTPS sempre (é o que protege a chave na rede).</summary>
    public static Uri? Endpoint(PublishTarget target, string? baseUrl, string route, out string? error)
    {
        error = null;
        var tablet = target == PublishTarget.Tablet;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            error = tablet ? "Informe a URL do tablet (aparece na tela dele)" : "Informe a URL base da API";
            return null;
        }
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var b)
            || (b.Scheme != Uri.UriSchemeHttps && b.Scheme != Uri.UriSchemeHttp))
        {
            error = tablet ? "URL inválida: use https://ip-do-tablet:8787" : "URL inválida: use https://seu-dominio";
            return null;
        }
        if (b.Scheme == Uri.UriSchemeHttp && (tablet || !b.IsLoopback))
        {
            error = tablet ? "Use https:// (o tablet só recebe por HTTPS)" : "Use HTTPS (HTTP só é aceito para localhost)";
            return null;
        }
        if (!string.IsNullOrEmpty(b.Query) || !string.IsNullOrEmpty(b.Fragment) || !string.IsNullOrEmpty(b.UserInfo))
        {
            error = "A URL base não pode ter ?, # nem usuário/senha";
            return null;
        }
        var path = b.AbsolutePath.TrimEnd('/');
        return new Uri($"{b.Scheme}://{b.Authority}{path}/{route}");
    }

    readonly record struct SendResult(PublishOutcome Result, bool KeyRejected = false, TimeSpan? RetryAfter = null);

    static async Task<SendResult> SendAsync(PublishTarget target, Uri url, string key, string? pin, object payload)
    {
        var at = DateTimeOffset.Now;
        var tablet = target == PublishTarget.Tablet;
        var by = tablet ? "pelo tablet" : "pela API";
        var who = tablet ? "O tablet" : "A API";
        if (string.IsNullOrWhiteSpace(key))
            return new(new PublishOutcome(false, "Informe a chave de autenticação", at), KeyRejected: true);
        HttpClient client;
        if (tablet)
        {
            var normalized = NormalizePin(pin);
            if (normalized.Length != 64)
                return new(new PublishOutcome(false, "Informe a impressão digital do certificado (64 dígitos, aparece na tela do tablet)", at),
                    KeyRejected: true);
            client = TabletClient(normalized);
        }
        else
            client = Client;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions),
                    Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());

            using var res = await client.SendAsync(req);
            var code = (int)res.StatusCode;
            if (res.IsSuccessStatusCode)
                return new(new PublishOutcome(true, $"Enviado às {at:HH:mm:ss}", at));
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(new PublishOutcome(false, $"Chave recusada {by} ({code})", at), KeyRejected: true);
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                return new(new PublishOutcome(false, $"{who} pediu para esperar (429)", at), RetryAfter: Http.RetryAfter(res));
            if (code is >= 300 and < 400)
                return new(new PublishOutcome(false, $"{who} redirecionou ({code}): use a URL final", at));
            return new(new PublishOutcome(false, $"{who} respondeu {code}", at));
        }
        catch (FormatException)
        {
            return new(new PublishOutcome(false, "A chave tem caracteres inválidos", at), KeyRejected: true);
        }
        catch (TaskCanceledException)
        {
            return new(new PublishOutcome(false, "Tempo esgotado ao enviar", at));
        }
        catch (HttpRequestException e) when (tablet && e.InnerException is AuthenticationException)
        {
            // Outro aparelho no endereço, ou o app do tablet foi reinstalado e gerou outro certificado.
            return new(new PublishOutcome(false, "O certificado do tablet não confere com a impressão digital", at),
                KeyRejected: true);
        }
        catch (HttpRequestException)
        {
            return new(new PublishOutcome(false, tablet ? "Sem conexão com o tablet" : "Sem conexão com a API", at));
        }
    }
}

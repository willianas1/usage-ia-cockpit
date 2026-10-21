using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UsageCockpit.Core;

public interface IUsageProvider
{
    string Name { get; }
    Task<FetchResult> FetchAsync(CancellationToken ct);
}

static class Http
{
    public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    static Http()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("UsageCockpit/0.1");
    }

    public static TimeSpan RetryAfter(HttpResponseMessage r)
    {
        var ra = r.Headers.RetryAfter;
        var wait = ra?.Delta ?? (ra?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.Zero;
        // O endpoint do Claude às vezes manda Retry-After: 0. Nunca tentamos de novo antes de 60 s.
        return wait < TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait;
    }

    public static string Home(params string[] parts) =>
        Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }.Concat(parts).ToArray());
}

/// <summary>
/// Lê o token que o Claude Code salva em ~/.claude/.credentials.json e consulta o mesmo
/// endpoint usado pelo comando /usage. O token nunca é renovado nem gravado por este app,
/// para não invalidar a sessão do próprio Claude Code.
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    public string Name => "Claude Code";

    static string CredentialsPath => Http.Home(".claude", ".credentials.json");

    public async Task<FetchResult> FetchAsync(CancellationToken ct)
    {
        string token;
        try
        {
            if (!File.Exists(CredentialsPath))
                return FetchResult.Fail("Faça login no Claude Code (claude)");

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(CredentialsPath, ct));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || !oauth.TryGetProperty("accessToken", out var at))
                return FetchResult.Fail("Login do Claude Code não encontrado");

            token = at.GetString() ?? "";
            if (oauth.TryGetProperty("expiresAt", out var exp)
                && DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64()) <= DateTimeOffset.UtcNow)
                return FetchResult.Fail("Token expirado: abra o Claude Code para renovar");
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return FetchResult.Fail("Não consegui ler o login do Claude Code");
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");

        try
        {
            using var res = await Http.Client.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                return FetchResult.Fail("Muitas consultas, aguardando", Http.RetryAfter(res));
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return FetchResult.Fail("Token recusado: abra o Claude Code para renovar");
            if (!res.IsSuccessStatusCode)
                return FetchResult.Fail($"Erro {(int)res.StatusCode} ao consultar o uso");

            using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var root = body.RootElement;
            var (s, sr) = Window(root, "five_hour");
            var (w, wr) = Window(root, "seven_day");
            return FetchResult.Ok(new UsageSnapshot(s, sr, w, wr, DateTimeOffset.Now, "api"));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return FetchResult.Fail("Sem conexão com a Anthropic");
        }
    }

    static (double pct, DateTimeOffset? resets) Window(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object)
            return (0, null);
        var pct = w.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;
        DateTimeOffset? at = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), out var d) ? d : null;
        return (pct, at);
    }
}

/// <summary>
/// Lê ~/.codex/auth.json (login com a conta do ChatGPT) e consulta o endpoint de limites.
/// Se a rede falhar, usa o último "rate_limits" gravado nos logs de sessão do Codex.
/// </summary>
public sealed class CodexProvider : IUsageProvider
{
    public string Name => "Codex";

    static string AuthPath => Http.Home(".codex", "auth.json");
    static string SessionsPath => Http.Home(".codex", "sessions");

    public async Task<FetchResult> FetchAsync(CancellationToken ct)
    {
        var api = await FromApiAsync(ct);
        if (api.Snapshot != null || api.RetryAfter != null)
            return api;

        var local = FromSessionLogs();
        return local != null ? FetchResult.Ok(local) : api;
    }

    async Task<FetchResult> FromApiAsync(CancellationToken ct)
    {
        string token, account;
        try
        {
            if (!File.Exists(AuthPath))
                return FetchResult.Fail("Faça login no Codex (codex login)");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(AuthPath, ct));
            if (!doc.RootElement.TryGetProperty("tokens", out var t) || t.ValueKind != JsonValueKind.Object)
                return FetchResult.Fail("Codex sem login do ChatGPT (só API key)");
            token = t.GetProperty("access_token").GetString() ?? "";
            account = t.TryGetProperty("account_id", out var a) ? a.GetString() ?? "" : "";
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException
                                  or System.Collections.Generic.KeyNotFoundException)
        {
            return FetchResult.Fail("Não consegui ler o login do Codex");
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (account.Length > 0)
            req.Headers.Add("ChatGPT-Account-Id", account);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

        try
        {
            using var res = await Http.Client.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                return FetchResult.Fail("Muitas consultas, aguardando", Http.RetryAfter(res));
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return FetchResult.Fail("Token recusado: rode o Codex para renovar");
            if (!res.IsSuccessStatusCode)
                return FetchResult.Fail($"Erro {(int)res.StatusCode} ao consultar o uso");

            using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!body.RootElement.TryGetProperty("rate_limit", out var rl) || rl.ValueKind != JsonValueKind.Object)
                return FetchResult.Fail("Resposta sem limites de uso");
            var (s, sr) = Window(rl, "primary_window", "reset_at");
            var (w, wr) = Window(rl, "secondary_window", "reset_at");
            return FetchResult.Ok(new UsageSnapshot(s, sr, w, wr, DateTimeOffset.Now, "api"));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return FetchResult.Fail("Sem conexão com a OpenAI");
        }
    }

    /// <summary>Procura o "rate_limits" mais recente nos arquivos rollout-*.jsonl.</summary>
    static UsageSnapshot? FromSessionLogs()
    {
        try
        {
            if (!Directory.Exists(SessionsPath))
                return null;
            var files = new DirectoryInfo(SessionsPath)
                .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(5);

            foreach (var f in files)
            {
                string? last = null;
                using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    string? line;
                    while ((line = sr.ReadLine()) != null)
                        if (line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                            last = line;
                }
                if (last == null)
                    continue;

                using var doc = JsonDocument.Parse(last);
                var rl = FindProperty(doc.RootElement, "rate_limits");
                if (rl is not { ValueKind: JsonValueKind.Object } r)
                    continue;
                var (s, sAt) = Window(r, "primary", "resets_at");
                var (w, wAt) = Window(r, "secondary", "resets_at");
                return new UsageSnapshot(s, sAt, w, wAt, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime(), "logs");
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    static JsonElement? FindProperty(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var p in e.EnumerateObject())
        {
            if (p.NameEquals(name))
                return p.Value;
            if (FindProperty(p.Value, name) is { } found)
                return found;
        }
        return null;
    }

    static (double pct, DateTimeOffset? resets) Window(JsonElement parent, string name, string resetField)
    {
        if (!parent.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object)
            return (0, null);
        var pct = w.TryGetProperty("used_percent", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;
        DateTimeOffset? at = w.TryGetProperty(resetField, out var r) && r.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(r.GetInt64()) : null;
        return (pct, at);
    }
}

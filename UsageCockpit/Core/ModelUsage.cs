using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UsageCockpit.Core;

// Uso do dia por modelo, somando todas as conversas do Claude Code e do Codex desde a meia-noite.
// Vai no envio das tarefas (campo "models", docs/API.md); o painel do tablet desenha a distribuição.

/// <summary>Tokens de um modelo no dia. Model: o nome da tabela de preços ou o id que veio no arquivo.</summary>
public sealed record ModelUsage(string Provider, string Model, long Input, long CacheWrite, long Output, long CacheRead);

/// <summary>Uso do dia por modelo. Date: o dia local (aaaa-mm-dd) que os números somam.</summary>
public sealed record DailyModels(string Date, IReadOnlyList<ModelUsage> Items);

/// <summary>
/// Lê os mesmos arquivos das tarefas, mas todos os do dia e do começo: ~/.claude/projects/**/*.jsonl
/// (cada resposta tem message.model e message.usage) e ~/.codex/sessions/**/rollout-*.jsonl (o modelo
/// vem do turn_context e o token_count traz o total acumulado da conversa). Só as linhas novas são
/// lidas, e só as que têm uso viram JSON. Na virada do dia, recomeça do zero.
/// </summary>
sealed class DailyModelUsage
{
    static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(5);
    static readonly TimeSpan DiscoverEvery = TimeSpan.FromSeconds(30);
    /// <summary>Leituras de 8 MB por arquivo a cada vez: na primeira, o dia inteiro entra em poucos segundos.</summary>
    const int MaxChunks = 8;

    static string ClaudeDir => Http.Home(".claude", "projects");
    static string CodexDir => Http.Home(".codex", "sessions");

    static bool ClaudeLine(ReadOnlySpan<byte> line) => line.IndexOf("\"usage\""u8) >= 0;
    static bool CodexLine(ReadOnlySpan<byte> line) =>
        line.IndexOf("\"token_count\""u8) >= 0 || line.IndexOf("\"turn_context\""u8) >= 0;

    DateTime _day = DateTime.MinValue;
    DateTimeOffset _dayStart;
    DateTimeOffset _nextRead = DateTimeOffset.MinValue, _nextDiscover = DateTimeOffset.MinValue;
    readonly Dictionary<string, JsonlTail> _claudeFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, CodexFile> _codexFiles = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A mesma resposta do Claude aparece em várias linhas: uma entrada por id (a última vale).</summary>
    readonly Dictionary<string, (string Model, AgentTokens Tokens)> _claude = new();
    readonly Dictionary<string, AgentTokens> _codex = new();

    sealed class CodexFile
    {
        public CodexFile(string path) => Tail = new JsonlTail(path, int.MaxValue, CodexLine);
        public JsonlTail Tail { get; }
        public string? Model;
        public AgentTokens? Total;
    }

    public DailyModels? Current { get; private set; }

    public void Update(DateTimeOffset now, bool claude, bool codex)
    {
        if (now.Date != _day) Reset(now);
        if (now < _nextRead) return;
        _nextRead = now + ReadEvery;
        if (now >= _nextDiscover)
        {
            _nextDiscover = now + DiscoverEvery;
            if (claude) Discover(ClaudeDir, "*.jsonl", p => _claudeFiles.TryAdd(p, new JsonlTail(p, int.MaxValue, ClaudeLine)));
            if (codex) Discover(CodexDir, "rollout-*.jsonl", p => _codexFiles.TryAdd(p, new CodexFile(p)));
        }
        if (claude) foreach (var tail in _claudeFiles.Values) Drain(tail, ApplyClaude);
        if (codex)
            foreach (var f in _codexFiles.Values)
                Drain(f.Tail, r => ApplyCodex(f, r));
        Current = Build(claude, codex);
    }

    void Reset(DateTimeOffset now)
    {
        _day = now.Date;
        _dayStart = new DateTimeOffset(now.Date, now.Offset);
        _claudeFiles.Clear();
        _codexFiles.Clear();
        _claude.Clear();
        _codex.Clear();
        _nextRead = _nextDiscover = DateTimeOffset.MinValue;
    }

    /// <summary>Arquivos alterados hoje (a conversa pode ter começado antes; as linhas de antes não contam).</summary>
    void Discover(string dir, string pattern, Action<string> add)
    {
        if (!Directory.Exists(dir)) return;
        var since = _dayStart.UtcDateTime;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
                if (File.GetLastWriteTimeUtc(f) >= since) add(f);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    static void Drain(JsonlTail tail, Action<JsonElement> apply)
    {
        for (var i = 0; i < MaxChunks; i++)
        {
            foreach (var doc in tail.ReadNew(out _))
                using (doc)
                    apply(doc.RootElement);
            if (!tail.Behind) break;
        }
    }

    bool Today(JsonElement r) => AgentText.Time(r) is { } at && at >= _dayStart;

    void ApplyClaude(JsonElement r)
    {
        var message = AgentText.Obj(r, "message");
        var usage = AgentText.Obj(message, "usage");
        var model = AgentText.Str(message, "model");
        if (usage.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(model) || model == "<synthetic>" || !Today(r)) return;
        var id = (AgentText.Str(message, "id") ?? Guid.NewGuid().ToString()) + "|" + AgentText.Str(r, "requestId");
        _claude[id] = (model, new AgentTokens(
            AgentText.Long(usage, "input_tokens"), AgentText.Long(usage, "cache_creation_input_tokens"),
            AgentText.Long(usage, "output_tokens"), AgentText.Long(usage, "cache_read_input_tokens")));
    }

    /// <summary>O token_count traz o total da conversa: conta a diferença para o anterior, no modelo da vez.</summary>
    void ApplyCodex(CodexFile f, JsonElement r)
    {
        var p = AgentText.Obj(r, "payload");
        var type = AgentText.Str(r, "type");
        if (type == "turn_context")
        {
            f.Model = AgentText.Str(p, "model") ?? f.Model;
            return;
        }
        if (type != "event_msg" || AgentText.Str(p, "type") != "token_count") return;
        var total = AgentText.Obj(AgentText.Obj(p, "info"), "total_token_usage");
        if (total.ValueKind != JsonValueKind.Object) return;
        var input = AgentText.Long(total, "input_tokens");
        var cached = AgentText.Long(total, "cached_input_tokens");
        var now = new AgentTokens(Math.Max(0, input - cached), AgentText.Long(total, "cache_write_input_tokens"),
            AgentText.Long(total, "output_tokens"), cached);
        var delta = now.Minus(f.Total ?? AgentTokens.Zero);
        f.Total = now;
        if (!Today(r) || string.IsNullOrEmpty(f.Model)) return;
        _codex[f.Model] = (_codex.TryGetValue(f.Model, out var sum) ? sum : AgentTokens.Zero).Plus(delta);
    }

    DailyModels Build(bool claude, bool codex)
    {
        var items = new List<ModelUsage>();
        if (claude)
            items.AddRange(Group("claude", _claude.Values.Select(v => (v.Model, v.Tokens))));
        if (codex)
            items.AddRange(Group("codex", _codex.Select(kv => (kv.Key, kv.Value))));
        return new DailyModels(_day.ToString("yyyy-MM-dd"), items);
    }

    /// <summary>Soma por nome de modelo (o da tabela de preços, que junta variações como "[1m]"), do maior para o menor.</summary>
    static IEnumerable<ModelUsage> Group(string provider, IEnumerable<(string Model, AgentTokens Tokens)> rows) =>
        rows.GroupBy(r => ModelPrices.Find(r.Model)?.Name ?? r.Model)
            .Select(g =>
            {
                var t = g.Aggregate(AgentTokens.Zero, (s, r) => s.Plus(r.Tokens));
                return new ModelUsage(provider, g.Key, t.Input, t.CacheWrite, t.Output, t.CacheRead);
            })
            .Where(m => m.Input + m.CacheWrite + m.Output + m.CacheRead > 0)
            .OrderByDescending(m => m.Input + m.CacheWrite + m.Output + m.CacheRead);
}

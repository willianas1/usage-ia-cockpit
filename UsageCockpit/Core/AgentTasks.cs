using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace UsageCockpit.Core;

// Tarefas do Claude Code e do Codex: o que está executando, esperando o usuário ou terminou há pouco.
// Contrato do envio em docs/API.md (Tarefas, /v1/agents): mudar um campo aqui exige mudar lá,
// na Usage IA API e no painel do Echo Show.

/// <summary>Tokens de uma tarefa. Input é a entrada nova, sem o que foi lido do cache.</summary>
public sealed record AgentTokens(long Input, long CacheWrite, long Output, long CacheRead)
{
    public static readonly AgentTokens Zero = new(0, 0, 0, 0);

    public AgentTokens Plus(AgentTokens o) =>
        new(Input + o.Input, CacheWrite + o.CacheWrite, Output + o.Output, CacheRead + o.CacheRead);

    public AgentTokens Minus(AgentTokens o) => new(
        Math.Max(0, Input - o.Input), Math.Max(0, CacheWrite - o.CacheWrite),
        Math.Max(0, Output - o.Output), Math.Max(0, CacheRead - o.CacheRead));
}

/// <summary>
/// Custo equivalente em US$ pelos preços da API (ModelPrices): do pedido atual e da conversa inteira.
/// Model: nome do modelo usado no preço (o da resposta mais recente).
/// </summary>
public sealed record AgentCost(double Request, double Conversation, string Model);

/// <summary>
/// O que a tarefa espera do usuário. Kind: question, permission ou input. Answerable: dá para responder
/// pelo painel do tablet agora (há um hook esperando, ou o Codex aceita a resposta como mensagem).
/// </summary>
public sealed record AgentQuestion(string Kind, string Text, string? Tool, IReadOnlyList<string> Options, bool Answerable = false);

/// <summary>
/// Uma tarefa: o pedido mais recente de uma sessão do Claude Code ou de uma conversa do Codex.
/// State: running, waiting ou done. Since: quando entrou no estado atual. Tokens: do pedido atual;
/// Conversation: da conversa inteira (todos os pedidos da sessão).
/// </summary>
public sealed record AgentTask(
    string Id,
    string Provider,
    string Name,
    string? Project,
    string State,
    DateTimeOffset Since,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    AgentQuestion? Question,
    string? Prompt,
    string? Summary,
    AgentTokens Tokens,
    AgentTokens? Conversation,
    AgentCost? Cost,
    double? TokensPerSecond,
    double? LimitPercent);

public static class AgentStates
{
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Done = "done";
}

/// <summary>
/// Lê, no próprio PC, os arquivos que o Claude Code e o Codex já gravam e monta a lista de tarefas,
/// a cada segundo, numa thread de fundo. Não executa nada nem fala com a rede.
/// </summary>
public sealed class AgentTasksMonitor : IDisposable
{
    /// <summary>Tarefas concluídas continuam na lista por este tempo.</summary>
    public static readonly TimeSpan DoneVisible = TimeSpan.FromHours(8);
    /// <summary>Limite de tarefas na lista (perguntas e execuções primeiro). A API aceita até 20.</summary>
    public const int MaxTasks = 20;

    readonly Timer _timer;
    readonly ClaudeTasks _claude = new();
    readonly CodexTasks _codex = new();
    readonly DailyModelUsage _models = new();
    int _busy;
    bool _running;

    public AgentTasksMonitor()
    {
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public volatile bool ClaudeEnabled = true;
    public volatile bool CodexEnabled = true;

    public IReadOnlyList<AgentTask> Tasks { get; private set; } = Array.Empty<AgentTask>();

    /// <summary>Saída gerada por segundo (média de ~30 s), somando as tarefas executando de cada ferramenta.</summary>
    public double ClaudeRate { get; private set; }
    public double CodexRate { get; private set; }

    /// <summary>Uso do dia por modelo (atualizado a cada 5 s); nulo até a primeira leitura.</summary>
    public DailyModels? Models { get; private set; }

    /// <summary>Disparado numa thread de fundo depois de cada leitura.</summary>
    public event Action? Updated;

    public bool Running => _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _timer.Change(0, 1000);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var now = DateTimeOffset.Now;
            var list = new List<AgentTask>();
            if (ClaudeEnabled) list.AddRange(_claude.Scan(now));
            if (CodexEnabled) list.AddRange(_codex.Scan(now));
            Tasks = Order(list);
            ClaudeRate = Rate(list, "claude");
            CodexRate = Rate(list, "codex");
            // Falha na soma por modelo não pode parar as tarefas.
            try
            {
                _models.Update(now, ClaudeEnabled, CodexEnabled);
                Models = _models.Current;
            }
            catch (Exception e)
            {
                Log(e);
            }
            Updated?.Invoke();
        }
        catch (Exception e)
        {
            Log(e);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    static double Rate(IEnumerable<AgentTask> tasks, string provider) =>
        Math.Round(tasks.Where(t => t.Provider == provider && t.State != AgentStates.Done)
            .Sum(t => t.TokensPerSecond ?? 0), 1);

    /// <summary>Perguntas, depois execuções (mais antigas primeiro), depois concluídas (mais novas primeiro).</summary>
    static IReadOnlyList<AgentTask> Order(List<AgentTask> list) =>
        list.OrderBy(t => t.State == AgentStates.Waiting ? 0 : t.State == AgentStates.Running ? 1 : 2)
            .ThenBy(t => t.State == AgentStates.Done ? -t.Since.ToUnixTimeMilliseconds() : t.StartedAt.ToUnixTimeMilliseconds())
            .Take(MaxTasks)
            .ToList();

    internal static void Log(Exception e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "agents.log"), $"[{DateTime.Now:s}] {e.GetType().Name}: {e.Message}\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _timer.Dispose();
}

// ---------------------------------------------------------------------------------------------
// Utilidades comuns
// ---------------------------------------------------------------------------------------------

static class AgentText
{
    /// <summary>Junta espaços e quebras de linha e corta no limite, com reticências.</summary>
    public static string? Clip(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
            if (sb.Length > max) break;
        }
        if (sb.Length == 0) return null;
        return sb.Length > max ? sb.ToString(0, max - 1).TrimEnd() + "…" : sb.ToString();
    }

    static readonly System.Text.RegularExpressions.Regex Tags = new(@"</?[A-Za-z_][\w:.-]{0,60}[^<>]{0,200}>",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Início da resposta final, sem as tags que algumas automações usam (&lt;heartbeat&gt;…).</summary>
    public static string? Summary(string? text) => text == null ? null : Clip(Tags.Replace(text, " "), 400);

    /// <summary>Identificador público da tarefa: não expõe o id da sessão.</summary>
    public static string Id(string provider, string sessionId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(provider + ":" + sessionId));
        return (provider == "claude" ? "c-" : "x-") + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    public static string? Project(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        var name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return Clip(string.IsNullOrEmpty(name) ? cwd : name, 120);
    }

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    public static double? Double(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static DateTimeOffset? Time(JsonElement e, string name = "timestamp") =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToLocalTime() : null;

    /// <summary>Resumo do que a ferramenta vai fazer, para o pedido de permissão (comando, arquivo, URL…).</summary>
    public static string ToolSummary(string? tool, JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "command", "file_path", "notebook_path", "url", "query", "pattern", "description", "prompt", "path" })
                if (Str(input, key) is { Length: > 0 } v)
                    return Clip(v, 600)!;
            foreach (var p in input.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } s)
                    return Clip(s, 600)!;
        }
        return tool ?? "ferramenta";
    }

    /// <summary>Texto de um conteúdo de mensagem: string ou lista de blocos { type: "text", text }.</summary>
    public static string? ContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        string? last = null;
        foreach (var b in content.EnumerateArray())
            if (Str(b, "type") is "text" or "input_text" or "output_text" && Str(b, "text") is { Length: > 0 } t)
                last = last == null ? t : last + "\n" + t;
        return last;
    }
}

/// <summary>Saída por segundo nos últimos ~30 s, a partir do total de saída da tarefa.</summary>
sealed class RateMeter
{
    static readonly TimeSpan Window = TimeSpan.FromSeconds(30);
    readonly Queue<(DateTimeOffset At, long Output)> _samples = new();

    public void Reset() => _samples.Clear();

    public double? Sample(DateTimeOffset now, long output)
    {
        _samples.Enqueue((now, output));
        while (_samples.Count > 2 && now - _samples.Peek().At > Window)
            _samples.Dequeue();
        var first = _samples.Peek();
        var seconds = (now - first.At).TotalSeconds;
        if (seconds < 3) return null;
        return Math.Round(Math.Max(0, output - first.Output) / seconds, 1);
    }
}

/// <summary>
/// Lê só as linhas novas de um arquivo .jsonl que outro programa vai acrescentando. Na primeira
/// leitura de um arquivo grande, começa perto do fim (basta o pedido mais recente).
/// </summary>
sealed class JsonlTail
{
    /// <summary>Filtro barato antes do parse: só as linhas aceitas viram JSON.</summary>
    public delegate bool LineFilter(ReadOnlySpan<byte> line);

    const int MaxRead = 8 * 1024 * 1024;
    readonly string _path;
    readonly int _initialTail;
    readonly LineFilter? _filter;
    long _offset = -1;
    byte[] _partial = Array.Empty<byte>();

    public JsonlTail(string path, int initialTail = 4 * 1024 * 1024, LineFilter? filter = null)
    {
        _path = path;
        _initialTail = initialTail;
        _filter = filter;
    }

    public string Path => _path;
    public DateTime LastWriteUtc { get; private set; }
    /// <summary>A última leitura parou no limite de 8 MB: ainda há linhas para ler.</summary>
    public bool Behind { get; private set; }

    /// <summary>Linhas completas novas desde a última leitura. Arquivo encolheu: recomeça do início.</summary>
    public List<JsonDocument> ReadNew(out bool restarted)
    {
        restarted = false;
        Behind = false;
        var docs = new List<JsonDocument>();
        FileInfo info;
        try
        {
            info = new FileInfo(_path);
            if (!info.Exists) return docs;
        }
        catch (IOException)
        {
            return docs;
        }
        if (_offset >= 0 && info.Length == _offset && info.LastWriteTimeUtc == LastWriteUtc) return docs;
        LastWriteUtc = info.LastWriteTimeUtc;

        var skipFirst = false;
        if (_offset < 0 || info.Length < _offset)
        {
            restarted = _offset >= 0;
            _partial = Array.Empty<byte>();
            _offset = info.Length > _initialTail ? info.Length - _initialTail : 0;
            skipFirst = _offset > 0;
        }

        byte[] data;
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(_offset, SeekOrigin.Begin);
            var count = (int)Math.Min(MaxRead, Math.Max(0, fs.Length - _offset));
            data = new byte[_partial.Length + count];
            Buffer.BlockCopy(_partial, 0, data, 0, _partial.Length);
            var read = 0;
            while (read < count)
            {
                var n = fs.Read(data, _partial.Length + read, count - read);
                if (n == 0) break;
                read += n;
            }
            _offset += read;
            Behind = fs.Length > _offset;
            if (read < count) Array.Resize(ref data, _partial.Length + read);
        }
        catch (IOException)
        {
            return docs;
        }
        catch (UnauthorizedAccessException)
        {
            return docs;
        }

        var start = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != (byte)'\n') continue;
            var len = i - start;
            if (skipFirst)
                skipFirst = false;
            else if (len > 1 && (_filter == null || _filter(data.AsSpan(start, len))))
                Parse(data.AsMemory(start, len), docs);
            start = i + 1;
        }
        _partial = start < data.Length ? data[start..] : Array.Empty<byte>();
        if (skipFirst) _partial = Array.Empty<byte>(); // nenhuma quebra de linha ainda: descarta o pedaço
        return docs;
    }

    static void Parse(ReadOnlyMemory<byte> line, List<JsonDocument> docs)
    {
        try
        {
            var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) docs.Add(doc);
            else doc.Dispose();
        }
        catch (JsonException)
        {
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Claude Code
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Claude Code: cada sessão aberta tem um arquivo ~/.claude/sessions/&lt;pid&gt;.json com o nome e o
/// status (busy, idle, waiting); a conversa fica em ~/.claude/projects/&lt;pasta&gt;/&lt;sessão&gt;.jsonl,
/// com o consumo de cada resposta. Subagentes ficam em &lt;sessão&gt;/subagents/*.jsonl.
/// Pedidos de permissão chegam pelos hooks (ClaudeHooks), quando instalados.
/// </summary>
sealed class ClaudeTasks
{
    static string SessionsDir => Http.Home(".claude", "sessions");
    static string ProjectsDir => Http.Home(".claude", "projects");

    readonly Dictionary<string, ClaudeSession> _sessions = new();
    readonly Dictionary<int, (DateTimeOffset At, bool Alive)> _pids = new();

    public IEnumerable<AgentTask> Scan(DateTimeOffset now)
    {
        var seen = new HashSet<string>();
        var marks = ClaudeHooks.ReadMarks();
        string[] files;
        try
        {
            files = Directory.Exists(SessionsDir) ? Directory.GetFiles(SessionsDir, "*.json") : Array.Empty<string>();
        }
        catch (IOException)
        {
            files = Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            files = Array.Empty<string>();
        }

        var result = new List<AgentTask>();
        foreach (var file in files)
        {
            if (ReadSessionFile(file) is not { } info || !Alive(info.Pid, now)) continue;
            if (!seen.Add(info.SessionId)) continue;
            if (!_sessions.TryGetValue(info.SessionId, out var s))
                _sessions[info.SessionId] = s = new ClaudeSession(info.SessionId);
            s.Update(info);
            s.Read(now);
            marks.TryGetValue(info.SessionId, out var mark);
            if (s.ToTask(now, mark) is { } task) result.Add(task);
        }
        foreach (var gone in _sessions.Keys.Where(k => !seen.Contains(k)).ToList())
            _sessions.Remove(gone);
        return result;
    }

    bool Alive(int pid, DateTimeOffset now)
    {
        if (pid <= 0) return true;
        if (_pids.TryGetValue(pid, out var c) && now - c.At < TimeSpan.FromSeconds(10)) return c.Alive;
        bool alive;
        try
        {
            using var p = Process.GetProcessById(pid);
            alive = !p.HasExited;
        }
        catch (ArgumentException)
        {
            alive = false;
        }
        catch (InvalidOperationException)
        {
            alive = false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            alive = true; // existe, mas sem acesso: conta como viva
        }
        _pids[pid] = (now, alive);
        return alive;
    }

    internal sealed record SessionInfo(int Pid, string SessionId, string? Cwd, string? Name, string? Status,
        string? WaitingFor, DateTimeOffset? StatusAt);

    static SessionInfo? ReadSessionFile(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > 256 * 1024) return null;
            using var doc = JsonDocument.Parse(fs);
            var r = doc.RootElement;
            if (AgentText.Str(r, "sessionId") is not { Length: > 0 } id) return null;
            var statusAt = AgentText.Long(r, "statusUpdatedAt");
            return new SessionInfo(
                (int)AgentText.Long(r, "pid"), id, AgentText.Str(r, "cwd"), AgentText.Str(r, "name"),
                AgentText.Str(r, "status"), AgentText.Str(r, "waitingFor"),
                statusAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(statusAt).ToLocalTime() : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    sealed class ClaudeSession
    {
        readonly string _id;
        SessionInfo? _info;
        JsonlTail? _main;
        DateTimeOffset _nextLookup = DateTimeOffset.MinValue;
        DateTimeOffset _nextSubagents = DateTimeOffset.MinValue;
        readonly Dictionary<string, JsonlTail> _subagents = new();
        // Hora do último pedido à API (linha "api-request") de cada arquivo: início da próxima resposta.
        sealed class RequestClock { public DateTimeOffset? At; }
        readonly RequestClock _mainClock = new();
        readonly Dictionary<string, RequestClock> _clocks = new();
        // Respostas do pedido atual: do pedido à API até a última linha gravada, com a saída final.
        readonly Dictionary<string, (DateTimeOffset Start, DateTimeOffset End, long Output)> _spans = new();
        static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(90);
        // Custo de cada resposta (por message.id) no pedido atual e na conversa; sem preço do modelo: fora.
        readonly Dictionary<string, double> _costTurn = new();
        readonly Dictionary<string, double> _costAll = new();
        string? _priceModel;

        // Pedido atual (turno): do último prompt do usuário em diante.
        DateTimeOffset? _turnStart;
        string? _prompt;
        string? _lastText;
        DateTimeOffset? _lastProgressAt;
        readonly Dictionary<string, AgentTokens> _usage = new();
        // Conversa inteira: cada resposta (e de subagentes) uma vez, por message.id.
        readonly Dictionary<string, AgentTokens> _all = new();
        readonly Dictionary<string, (string Name, JsonElement Input, DateTimeOffset At)> _pending = new();
        string? _lastStatus;
        DateTimeOffset? _endedAt;

        public ClaudeSession(string id) => _id = id;

        public void Update(SessionInfo info)
        {
            // Fim visto ao vivo (busy → idle). Sessão já parada quando o app abriu: o statusUpdatedAt pode ser
            // de quando o processo reabriu, então vale a última resposta do transcript (ToTask).
            if (info.Status != _lastStatus)
            {
                if (info.Status == "idle" && _lastStatus != null)
                    _endedAt = info.StatusAt ?? DateTimeOffset.Now;
                else if (info.Status != "idle")
                    _endedAt = null;
                _lastStatus = info.Status;
            }
            _info = info;
        }

        public void Read(DateTimeOffset now)
        {
            if (_main == null && now >= _nextLookup)
            {
                _nextLookup = now.AddSeconds(10);
                // Lê a conversa inteira (até 64 MB) para somar os tokens de todos os pedidos.
                if (FindTranscript() is { } path) _main = new JsonlTail(path, 64 * 1024 * 1024);
            }
            if (_main == null) return;

            var docs = _main.ReadNew(out var restarted);
            if (restarted)
            {
                ResetTurn(null);
                _all.Clear();
                _costAll.Clear();
            }
            foreach (var doc in docs)
                using (doc)
                    Apply(doc.RootElement, subagent: false, _mainClock);

            if (now >= _nextSubagents)
            {
                _nextSubagents = now.AddSeconds(5);
                var dir = Path.Combine(Path.GetDirectoryName(_main.Path)!, _id, "subagents");
                try
                {
                    if (Directory.Exists(dir))
                        foreach (var f in Directory.GetFiles(dir, "*.jsonl"))
                            if (!_subagents.ContainsKey(f))
                                _subagents[f] = new JsonlTail(f, 32 * 1024 * 1024);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            foreach (var (file, tail) in _subagents)
            {
                if (!_clocks.TryGetValue(file, out var clock)) _clocks[file] = clock = new RequestClock();
                foreach (var doc in tail.ReadNew(out _))
                    using (doc)
                        Apply(doc.RootElement, subagent: true, clock);
            }
        }

        string? FindTranscript()
        {
            try
            {
                if (!Directory.Exists(ProjectsDir)) return null;
                foreach (var dir in Directory.EnumerateDirectories(ProjectsDir))
                {
                    var f = Path.Combine(dir, _id + ".jsonl");
                    if (File.Exists(f)) return f;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return null;
        }

        void ResetTurn(DateTimeOffset? start)
        {
            _turnStart = start;
            _prompt = null;
            _lastText = null;
            _usage.Clear();
            _pending.Clear();
            _spans.Clear();
            _costTurn.Clear();
        }

        void Apply(JsonElement r, bool subagent, RequestClock clock)
        {
            var type = AgentText.Str(r, "type");
            var at = AgentText.Time(r);
            var message = AgentText.Obj(r, "message");

            if (type == "api-request")
            {
                clock.At = at ?? clock.At;
                return;
            }

            if (subagent)
            {
                // Subagentes: só o consumo. Entra na conversa sempre e no pedido atual se for depois dele.
                if (type == "assistant")
                    AddUsage(message, turn: at is { } t && _turnStart is { } start && t >= start, at, clock);
                return;
            }

            if (type == "user")
            {
                var content = AgentText.Obj(message, "content");
                var results = false;
                if (content.ValueKind == JsonValueKind.Array)
                    foreach (var b in content.EnumerateArray())
                        if (AgentText.Str(b, "type") == "tool_result")
                        {
                            results = true;
                            if (AgentText.Str(b, "tool_use_id") is { } id) _pending.Remove(id);
                        }
                if (results)
                {
                    _lastProgressAt = at ?? _lastProgressAt;
                    return;
                }
                // Mensagens do próprio Claude Code (avisos, resumo após compactar a conversa) não são pedidos.
                if (r.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return;
                if (r.TryGetProperty("isCompactSummary", out var compact) && compact.ValueKind == JsonValueKind.True) return;
                // Aviso de tarefa em segundo plano que terminou: entra como "user", mas não é um pedido seu.
                if (AgentText.Str(AgentText.Obj(r, "origin"), "kind") == "task-notification") return;
                var text = AgentText.ContentText(content);
                if (string.IsNullOrWhiteSpace(text) || text.StartsWith("[Request interrupted", StringComparison.Ordinal)
                    || text.TrimStart().StartsWith("<task-notification>", StringComparison.Ordinal)) return;
                ResetTurn(at ?? DateTimeOffset.Now);
                _prompt = AgentText.Clip(text, 300);
                _lastProgressAt = at;
                return;
            }

            if (type == "assistant")
            {
                _lastProgressAt = at ?? _lastProgressAt;
                AddUsage(message, turn: _turnStart != null, at, clock);
                if (_turnStart == null) return;
                var content = AgentText.Obj(message, "content");
                if (content.ValueKind != JsonValueKind.Array) return;
                foreach (var b in content.EnumerateArray())
                {
                    var kind = AgentText.Str(b, "type");
                    if (kind == "text" && AgentText.Str(b, "text") is { Length: > 0 } text)
                        _lastText = text;
                    else if (kind == "tool_use" && AgentText.Str(b, "id") is { } id)
                        _pending[id] = (AgentText.Str(b, "name") ?? "ferramenta", AgentText.Obj(b, "input").Clone(), at ?? DateTimeOffset.Now);
                }
            }
        }

        void AddUsage(JsonElement message, bool turn, DateTimeOffset? at, RequestClock clock)
        {
            var usage = AgentText.Obj(message, "usage");
            if (usage.ValueKind != JsonValueKind.Object) return;
            // A mesma resposta aparece em várias linhas: conta uma vez por id (a última leitura vale).
            var id = AgentText.Str(message, "id") ?? Guid.NewGuid().ToString();
            var tokens = new AgentTokens(
                AgentText.Long(usage, "input_tokens"), AgentText.Long(usage, "cache_creation_input_tokens"),
                AgentText.Long(usage, "output_tokens"), AgentText.Long(usage, "cache_read_input_tokens"));
            _all[id] = tokens;
            // Custo pelo modelo da resposta; a gravação no cache se divide em 5 min e 1 h quando o detalhe vem.
            double? cost = null;
            if (ModelPrices.Find(AgentText.Str(message, "model")) is { } price)
            {
                var creation = AgentText.Obj(usage, "cache_creation");
                var write1h = AgentText.Long(creation, "ephemeral_1h_input_tokens");
                var write5m = Math.Max(0, tokens.CacheWrite - write1h);
                cost = price.Cost(tokens.Input, write5m, write1h, tokens.Output, tokens.CacheRead);
                _costAll[id] = cost.Value;
                _priceModel = price.Name;
            }
            if (!turn) return;
            _usage[id] = tokens;
            if (cost is { } c) _costTurn[id] = c;
            // Velocidade: a resposta só é gravada quando termina, já com a saída final. Mede-se do pedido à
            // API até a última linha dela (o total de saída não cresce enquanto o Claude gera).
            if (at is not { } end) return;
            if (_spans.TryGetValue(id, out var span))
                _spans[id] = (span.Start, end > span.End ? end : span.End, tokens.Output);
            else
                _spans[id] = (clock.At is { } start && start <= end ? start : end, end, tokens.Output);
        }

        /// <summary>
        /// Saída por segundo das respostas do pedido atual que terminaram nos últimos 90 s (soma da saída ÷
        /// soma do tempo de cada uma). Fica uma resposta atrasada, porque o Claude Code só grava a resposta
        /// pronta. Sem resposta recente: nulo.
        /// </summary>
        double? Speed(DateTimeOffset now)
        {
            long output = 0;
            double seconds = 0;
            foreach (var s in _spans.Values)
            {
                var duration = (s.End - s.Start).TotalSeconds;
                if (now - s.End > RateWindow || duration < 0.5 || s.Output <= 0) continue;
                output += s.Output;
                seconds += duration;
            }
            return seconds > 0 ? Math.Round(output / seconds, 1) : null;
        }

        public AgentTask? ToTask(DateTimeOffset now, HookMark? mark)
        {
            if (_info is not { } info || _turnStart is not { } start) return null;
            var tokens = _usage.Values.Aggregate(AgentTokens.Zero, (a, b) => a.Plus(b));
            var conversation = _all.Values.Aggregate(AgentTokens.Zero, (a, b) => a.Plus(b));
            var cost = _priceModel != null
                ? new AgentCost(Math.Round(_costTurn.Values.Sum(), 4), Math.Round(_costAll.Values.Sum(), 4), _priceModel)
                : null;
            var question = Question(info, mark);
            var idle = info.Status == "idle" && question == null;

            string state;
            DateTimeOffset since;
            if (question != null)
            {
                state = AgentStates.Waiting;
                since = question.At;
            }
            else if (idle)
            {
                var ended = _endedAt ?? _lastProgressAt ?? info.StatusAt ?? start;
                if (ended < start) ended = _lastProgressAt is { } p && p >= start ? p : start;
                if (now - ended > AgentTasksMonitor.DoneVisible) return null;
                state = AgentStates.Done;
                since = ended;
            }
            else
            {
                state = AgentStates.Running;
                since = start;
            }

            var rate = state == AgentStates.Done ? null : Speed(now);
            return new AgentTask(
                AgentText.Id("claude", _id),
                "claude",
                AgentText.Clip(info.Name, 200) ?? AgentText.Project(info.Cwd) ?? "Claude Code",
                AgentText.Project(info.Cwd),
                state,
                since,
                start,
                state == AgentStates.Done ? since : null,
                question is { } w && TabletAnswers.IsWaiting(AgentText.Id("claude", _id)) ? w.Question with { Answerable = true } : question?.Question,
                _prompt,
                state == AgentStates.Done ? AgentText.Summary(_lastText) : null,
                tokens,
                conversation,
                cost,
                rate,
                null);
        }

        sealed record Waiting(AgentQuestion Question, DateTimeOffset At);

        Waiting? Question(SessionInfo info, HookMark? mark)
        {
            // 1. Pergunta do AskUserQuestion ainda sem resposta (vale no terminal e no app).
            foreach (var p in _pending.Values.OrderByDescending(p => p.At))
                if (p.Name == "AskUserQuestion" && Ask(p.Input) is { } q)
                    return new Waiting(q, p.At);

            var tool = _pending.Values.OrderByDescending(p => p.At).Select(p => ((string, JsonElement)?)(p.Name, p.Input)).FirstOrDefault();

            // 2. Pedido de permissão avisado pelo hook, sem nada novo na conversa depois dele.
            if (mark != null && info.Status != "idle" && _turnStart is { } start && mark.At >= start
                && (_lastProgressAt == null || _lastProgressAt <= mark.At.AddSeconds(1.5)))
            {
                var name = mark.Tool ?? tool?.Item1;
                var text = mark.Text ?? (tool is { } t ? AgentText.ToolSummary(t.Item1, t.Item2) : "Aguardando você no Claude Code");
                return new Waiting(new AgentQuestion(name != null ? "permission" : "input", text, name, Permissions(name)), mark.At);
            }

            // 3. O próprio Claude Code marca a sessão como esperando (diálogo aberto no terminal).
            if (info.Status == "waiting")
            {
                var at = info.StatusAt ?? DateTimeOffset.Now;
                return tool is { } t
                    ? new Waiting(new AgentQuestion("permission", AgentText.ToolSummary(t.Item1, t.Item2), t.Item1, Permissions(t.Item1)), at)
                    : new Waiting(new AgentQuestion("input", WaitingText(info.WaitingFor), null, Array.Empty<string>()), at);
            }
            return null;
        }

        static IReadOnlyList<string> Permissions(string? tool) =>
            tool == null ? Array.Empty<string>() : new[] { "Permitir", "Permitir sempre", "Negar" };

        static string WaitingText(string? waitingFor) => waitingFor switch
        {
            "input needed" => "O Claude Code precisa de uma resposta",
            "dialog open" => "Há uma janela aberta esperando você no Claude Code",
            { Length: > 0 } w => "Aguardando você: " + w,
            _ => "Aguardando você no Claude Code",
        };

        /// <summary>Pergunta do AskUserQuestion: { questions: [{ question, options: [{ label }] }] }.</summary>
        static AgentQuestion? Ask(JsonElement input)
        {
            var list = AgentText.Obj(input, "questions");
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0) return null;
            var first = list[0];
            if (AgentText.Str(first, "question") is not { Length: > 0 } text) return null;
            var more = list.GetArrayLength() - 1;
            var options = new List<string>();
            var opts = AgentText.Obj(first, "options");
            if (opts.ValueKind == JsonValueKind.Array)
                foreach (var o in opts.EnumerateArray())
                    if ((AgentText.Str(o, "label") ?? (o.ValueKind == JsonValueKind.String ? o.GetString() : null)) is { Length: > 0 } label && options.Count < 6)
                        options.Add(AgentText.Clip(label, 80)!);
            return new AgentQuestion("question", AgentText.Clip(text + (more > 0 ? $" (+{more} pergunta{(more > 1 ? "s" : "")})" : ""), 600)!,
                null, options);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Codex
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Codex: cada conversa grava ~/.codex/sessions/**/rollout-*.jsonl com task_started, token_count
/// (total acumulado e percentual dos limites), agent_message e task_complete. O nome da conversa
/// fica em ~/.codex/session_index.jsonl. Pedidos de aprovação não ficam no arquivo; só as
/// ferramentas request_user_input e request_user_input_async (pergunta ao usuário) aparecem.
/// </summary>
sealed class CodexTasks
{
    static string SessionsDir => Http.Home(".codex", "sessions");
    static string IndexPath => Http.Home(".codex", "session_index.jsonl");
    /// <summary>Conversa "executando" sem gravar nada por este tempo: o Codex foi fechado no meio.</summary>
    static readonly TimeSpan RunningStale = TimeSpan.FromMinutes(30);

    readonly Dictionary<string, CodexThread> _threads = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    JsonlTail? _index;
    DateTimeOffset _nextScan = DateTimeOffset.MinValue;

    public IEnumerable<AgentTask> Scan(DateTimeOffset now)
    {
        ReadIndex();
        if (now >= _nextScan)
        {
            _nextScan = now.AddSeconds(5);
            Discover(now);
        }
        var result = new List<AgentTask>();
        foreach (var t in _threads.Values.ToList())
        {
            t.Read(now);
            if (now - t.LastWrite > AgentTasksMonitor.DoneVisible + TimeSpan.FromMinutes(10))
            {
                _threads.Remove(t.File);
                continue;
            }
            if (t.ToTask(now, _names) is { } task) result.Add(task);
        }
        return result;
    }

    void ReadIndex()
    {
        _index ??= new JsonlTail(IndexPath, 1024 * 1024);
        foreach (var doc in _index.ReadNew(out _))
            using (doc)
                if (AgentText.Str(doc.RootElement, "id") is { } id && AgentText.Str(doc.RootElement, "thread_name") is { Length: > 0 } name)
                    _names[id] = name;
    }

    void Discover(DateTimeOffset now)
    {
        try
        {
            if (!Directory.Exists(SessionsDir)) return;
            var since = now.UtcDateTime - AgentTasksMonitor.DoneVisible;
            foreach (var f in Directory.EnumerateFiles(SessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories))
                if (!_threads.ContainsKey(f) && File.GetLastWriteTimeUtc(f) >= since)
                    _threads[f] = new CodexThread(f);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    sealed class CodexThread
    {
        readonly JsonlTail _tail;
        readonly string _threadId;
        string? _cwd;
        string? _model;
        DateTimeOffset? _turnStart;
        DateTimeOffset? _endedAt;
        bool _active;
        string? _prompt;
        string? _firstPrompt;
        string? _lastText;
        AgentTokens _total = AgentTokens.Zero;
        AgentTokens? _base;
        double? _limit;
        double? _limitBase;
        readonly Dictionary<string, (AgentQuestion Question, DateTimeOffset At)> _questions = new();
        /// <summary>call_id das perguntas do request_user_input_async, respondidas por mensagem do usuário.</summary>
        readonly HashSet<string> _async = new(StringComparer.Ordinal);
        readonly RateMeter _rate = new();

        public CodexThread(string file)
        {
            File = file;
            _tail = new JsonlTail(file);
            // rollout-2026-09-03T07-15-40-<uuid>.jsonl
            var name = Path.GetFileNameWithoutExtension(file);
            _threadId = name.Length >= 36 ? name[^36..] : name;
        }

        public string File { get; }
        public DateTimeOffset LastWrite => new(_tail.LastWriteUtc == default ? System.IO.File.GetLastWriteTimeUtc(File) : _tail.LastWriteUtc, TimeSpan.Zero);

        public void Read(DateTimeOffset now)
        {
            foreach (var doc in _tail.ReadNew(out var restarted))
                using (doc)
                    Apply(doc.RootElement);
        }

        void Apply(JsonElement r)
        {
            var type = AgentText.Str(r, "type");
            var p = AgentText.Obj(r, "payload");
            var at = AgentText.Time(r) ?? DateTimeOffset.Now;

            if (type == "session_meta")
            {
                _cwd = AgentText.Str(p, "cwd") ?? _cwd;
                return;
            }
            if (type == "turn_context")
            {
                _cwd = AgentText.Str(p, "cwd") ?? _cwd;
                _model = AgentText.Str(p, "model") ?? _model;
                return;
            }
            if (type == "response_item")
            {
                var kind = AgentText.Str(p, "type");
                if (kind is "function_call" or "custom_tool_call" && AgentText.Str(p, "name") is { } tool and ("request_user_input" or "request_user_input_async")
                    && AgentText.Str(p, "call_id") is { } call && UserInput(p) is { } q)
                {
                    _questions[call] = (q, at);
                    if (tool == "request_user_input_async") _async.Add(call);
                }
                // A pergunta assíncrona recebe {"accepted":true} na hora; a resposta vem depois, como mensagem do usuário.
                else if (kind is "function_call_output" or "custom_tool_call_output" && AgentText.Str(p, "call_id") is { } done && !_async.Contains(done))
                    _questions.Remove(done);
                else if (kind == "message" && AgentText.Str(p, "role") == "user" && _async.Count > 0)
                    AnswerAsync(MessageText(AgentText.Obj(p, "content")));
                return;
            }
            if (type != "event_msg") return;

            switch (AgentText.Str(p, "type"))
            {
                case "task_started":
                    _active = true;
                    _turnStart = at;
                    _endedAt = null;
                    _prompt = null;
                    _lastText = null;
                    _base = _total;
                    // O limite de 5 h é da conta, não da conversa: entre um pedido e outro ele sobe com o uso
                    // de outras conversas. A base é a primeira leitura dentro deste pedido (aproximado).
                    _limit = null;
                    _limitBase = null;
                    _questions.Clear();
                    _async.Clear();
                    _rate.Reset();
                    break;
                case "user_message":
                    if (AgentText.Clip(AgentText.Str(p, "message"), 300) is { } prompt)
                    {
                        _prompt = prompt;
                        _firstPrompt ??= prompt;
                    }
                    break;
                case "agent_message":
                    _lastText = AgentText.Str(p, "message") ?? _lastText;
                    break;
                case "item_completed":
                    // Codex mais novo: o pedido do usuário vem como item UserMessage, sem o evento user_message.
                    var item = AgentText.Obj(p, "item");
                    if (AgentText.Str(item, "type") == "UserMessage" && MessageText(AgentText.Obj(item, "content")) is { } text
                        && !text.Contains(ReplyTag, StringComparison.Ordinal) && AgentText.Clip(text, 300) is { } asked)
                    {
                        _prompt = asked;
                        _firstPrompt ??= asked;
                    }
                    break;
                case "token_count":
                    var info = AgentText.Obj(p, "info");
                    var total = AgentText.Obj(info, "total_token_usage");
                    if (total.ValueKind == JsonValueKind.Object)
                    {
                        var input = AgentText.Long(total, "input_tokens");
                        var cached = AgentText.Long(total, "cached_input_tokens");
                        _total = new AgentTokens(Math.Max(0, input - cached), AgentText.Long(total, "cache_write_input_tokens"),
                            AgentText.Long(total, "output_tokens"), cached);
                        // Começou a ler no meio de um pedido: o primeiro total vira a base.
                        _base ??= _total;
                    }
                    var primary = AgentText.Obj(AgentText.Obj(p, "rate_limits"), "primary");
                    var window = AgentText.Long(primary, "window_minutes");
                    if ((window == 0 || window == 300) && AgentText.Double(primary, "used_percent") is { } used)
                    {
                        _limit = used;
                        _limitBase ??= used;
                    }
                    break;
                case "task_complete":
                    _active = false;
                    _endedAt = at;
                    _lastText = AgentText.Str(p, "last_agent_message") ?? _lastText;
                    // A pergunta assíncrona continua aberta no Codex depois que o pedido termina.
                    foreach (var call in _questions.Keys.Where(k => !_async.Contains(k)).ToList())
                        _questions.Remove(call);
                    break;
                case "turn_aborted":
                    _active = false;
                    _endedAt = at;
                    _questions.Clear();
                    _async.Clear();
                    break;
            }
        }

        const string ReplyTag = "<send_user_message_question_reply>";

        /// <summary>
        /// Mensagem do usuário com pergunta assíncrona aberta. A resposta pelo cartão do Codex vem como
        /// &lt;send_user_message_question_reply&gt; com o call_id da pergunta no questionItemId: tira só
        /// essas. Outra mensagem qualquer encerra as perguntas abertas (o usuário seguiu sem responder).
        /// </summary>
        void AnswerAsync(string? text)
        {
            var answered = text is not null && text.Contains(ReplyTag, StringComparison.Ordinal)
                ? _async.Where(call => text.Contains(call, StringComparison.Ordinal)).ToList()
                : _async.ToList();
            foreach (var call in answered)
            {
                _async.Remove(call);
                _questions.Remove(call);
            }
        }

        /// <summary>Texto de um content (lista de partes com "text") de mensagem do rollout.</summary>
        static string? MessageText(JsonElement content)
        {
            if (content.ValueKind != JsonValueKind.Array) return null;
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
                if (AgentText.Str(part, "text") is { Length: > 0 } t)
                    sb.Append(t);
            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>Argumentos do request_user_input (JSON em texto): question/questions/prompt e options.</summary>
        static AgentQuestion? UserInput(JsonElement p)
        {
            var raw = AgentText.Str(p, "arguments") ?? AgentText.Str(p, "input");
            if (string.IsNullOrWhiteSpace(raw)) return new AgentQuestion("question", "O Codex fez uma pergunta", null, Array.Empty<string>());
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var a = doc.RootElement;
                var q = a;
                var list = AgentText.Obj(a, "questions");
                if (list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0) q = list[0];
                var text = AgentText.Str(q, "question") ?? AgentText.Str(q, "title") ?? AgentText.Str(q, "prompt") ?? AgentText.Str(q, "message") ?? AgentText.Str(a, "question");
                var options = new List<string>();
                var opts = AgentText.Obj(q, "options");
                if (opts.ValueKind == JsonValueKind.Array)
                    foreach (var o in opts.EnumerateArray())
                        if ((o.ValueKind == JsonValueKind.String ? o.GetString() : AgentText.Str(o, "label")) is { Length: > 0 } label && options.Count < 6)
                            options.Add(AgentText.Clip(label, 80)!);
                return new AgentQuestion("question", AgentText.Clip(text, 600) ?? "O Codex fez uma pergunta", null, options);
            }
            catch (JsonException)
            {
                return new AgentQuestion("question", AgentText.Clip(raw, 600)!, null, Array.Empty<string>());
            }
        }

        public AgentTask? ToTask(DateTimeOffset now, Dictionary<string, string> names)
        {
            if (_turnStart is not { } start) return null;
            var tokens = _total.Minus(_base ?? AgentTokens.Zero);
            var limit = _limit is { } l && _limitBase is { } b ? Math.Round(Math.Max(0, l - b), 1) : (double?)null;

            string state;
            DateTimeOffset since;
            AgentQuestion? question = null;
            var id = AgentText.Id("codex", _threadId);
            if (_questions.Count > 0)
            {
                var q = _questions.OrderByDescending(x => x.Value.At).First();
                state = AgentStates.Waiting;
                since = q.Value.At;
                // Perguntas do Codex só são respondidas no PC: o Codex Desktop mostra a pergunta numa janela que
                // nenhum processo de fora consegue responder (uma mensagem na fila não responde a pergunta).
                question = q.Value.Question;
            }
            else if (TabletAnswers.Waiting(id) is { Kind: "permission" } wait)
            {
                // Pedido de aprovação avisado pelo hook PermissionRequest do Codex (só com o tablet ligado).
                state = AgentStates.Waiting;
                since = wait.At;
                question = new AgentQuestion("permission", wait.Text ?? "O Codex pede aprovação", wait.Tool,
                    new[] { "Permitir", "Negar" }, Answerable: true);
            }
            else if (_active)
            {
                if (now - LastWrite > RunningStale) return null;
                state = AgentStates.Running;
                since = start;
            }
            else
            {
                var ended = _endedAt ?? LastWrite;
                if (now - ended > AgentTasksMonitor.DoneVisible) return null;
                state = AgentStates.Done;
                since = ended;
            }

            var rate = state == AgentStates.Done ? null : _rate.Sample(now, tokens.Output);
            var name = names.TryGetValue(_threadId, out var n) ? n : null;
            AgentCost? cost = null;
            if (ModelPrices.Find(_model) is { } price)
            {
                double Of(AgentTokens k) => price.Cost(k.Input, k.CacheWrite, 0, k.Output, k.CacheRead);
                cost = new AgentCost(Math.Round(Of(tokens), 4), Math.Round(Of(_total), 4), price.Name);
            }
            return new AgentTask(
                id,
                "codex",
                AgentText.Clip(name, 200) ?? AgentText.Clip(_firstPrompt, 80) ?? AgentText.Project(_cwd) ?? "Codex",
                AgentText.Project(_cwd),
                state,
                since,
                start,
                state == AgentStates.Done ? since : null,
                question,
                _prompt,
                state == AgentStates.Done ? AgentText.Summary(_lastText) : null,
                tokens,
                _total,
                cost,
                rate,
                limit is > 0 ? limit : null);
        }
    }
}

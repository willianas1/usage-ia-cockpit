using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UsageCockpit.Core;

/// <summary>Aviso gravado pelo hook: a sessão pediu permissão ou está esperando o usuário.</summary>
public sealed record HookMark(string SessionId, DateTimeOffset At, string Event, string? Type, string? Tool, string? Text);

/// <summary>
/// Hooks do Claude Code para saber quando uma sessão para esperando permissão. O Claude Code roda
/// "UsageIACockpit.exe --claude-hook" nos eventos PermissionRequest e Notification e manda o evento
/// em JSON pela entrada padrão; o app grava um arquivo pequeno por sessão em %APPDATA%\UsageCockpit\hooks,
/// que o monitor de tarefas lê. Nada sai do PC por aqui.
///
/// A instalação acrescenta essas entradas ao ~/.claude/settings.json sem mexer nas demais (outros hooks
/// continuam lá) e guarda uma cópia do arquivo original na primeira vez. "--remove-hooks" tira só as
/// entradas deste app (o desinstalador roda isso).
/// </summary>
public static class ClaudeHooks
{
    public const string HookArg = "--claude-hook";
    public const string RemoveArg = "--remove-hooks";
    static readonly string[] Events = { "PermissionRequest", "Notification" };
    /// <summary>Notificações que indicam espera pelo usuário (idle_prompt é só a sessão parada).</summary>
    static readonly HashSet<string> WaitingTypes = new(StringComparer.Ordinal) { "permission_prompt", "elicitation_dialog" };

    // Fora do AppData: o hook pode rodar dentro do pacote do Claude Desktop (ver TabletAnswers.SharedFolder).
    static string MarksFolder => Path.Combine(TabletAnswers.SharedFolder, "hooks");
    static string SettingsPath => Http.Home(".claude", "settings.json");
    static readonly Regex SafeId = new("^[A-Za-z0-9-]{8,80}$", RegexOptions.CultureInvariant);

    // ---------- Chamado pelo Claude Code ----------

    /// <summary>
    /// Modo --claude-hook: lê o evento da entrada padrão e grava o aviso. Com o tablet ligado, um pedido de
    /// permissão (ou pergunta do AskUserQuestion) espera a resposta tocada no painel e a devolve ao Claude
    /// Code pela saída padrão; sem resposta, não decide nada e vale o diálogo do terminal. Nunca falha.
    /// </summary>
    public static void Handle()
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var ms = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int n;
            while ((n = stdin.Read(buffer, 0, buffer.Length)) > 0 && ms.Length < 512 * 1024)
                ms.Write(buffer, 0, n);
            using var doc = JsonDocument.Parse(ms.ToArray());
            var r = doc.RootElement;
            if (AgentText.Str(r, "session_id") is not { } session || !SafeId.IsMatch(session)) return;
            var evt = AgentText.Str(r, "hook_event_name") ?? "";
            var type = AgentText.Str(r, "notification_type");

            string? tool = null, text = null;
            if (evt == "PermissionRequest")
            {
                tool = AgentText.Str(r, "tool_name");
                text = AgentText.ToolSummary(tool, AgentText.Obj(r, "tool_input"));
            }
            else if (evt == "Notification")
            {
                var message = AgentText.Str(r, "message") ?? "";
                var waiting = type != null ? WaitingTypes.Contains(type) : message.Contains("permission", StringComparison.OrdinalIgnoreCase);
                if (!waiting) return;
                text = AgentText.Clip(message, 600);
            }
            else return;

            Directory.CreateDirectory(MarksFolder);
            var mark = new HookMark(session, DateTimeOffset.Now, evt, type, tool, text);
            var path = Path.Combine(MarksFolder, session + ".json");
            // PermissionRequest traz a ferramenta e o comando; a Notification que vem logo depois não apaga isso.
            if (evt == "Notification" && Read(path) is { Event: "PermissionRequest" } previous && mark.At - previous.At < TimeSpan.FromSeconds(10))
                return;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(mark), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);

            if (evt == "PermissionRequest" && TabletAnswers.TabletOn()
                && TabletAnswers.WaitForClaude(r, session) is { } decision)
                WriteStdout(decision);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
        }
    }

    /// <summary>Resposta do hook (JSON) para quem o chamou. O app é WinExe: escreve direto no handle herdado.</summary>
    public static void WriteStdout(string json)
    {
        using var stdout = Console.OpenStandardOutput();
        var bytes = new UTF8Encoding(false).GetBytes(json);
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
    }

    // ---------- Lido pelo monitor ----------

    /// <summary>Avisos por sessão. Avisos com mais de um dia são apagados.</summary>
    public static Dictionary<string, HookMark> ReadMarks()
    {
        var marks = new Dictionary<string, HookMark>(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(MarksFolder)) return marks;
            foreach (var f in Directory.GetFiles(MarksFolder, "*.json"))
            {
                if (Read(f) is not { } m) continue;
                if (DateTimeOffset.Now - m.At > TimeSpan.FromDays(1))
                {
                    try { File.Delete(f); }
                    catch (IOException) { }
                    continue;
                }
                marks[m.SessionId] = m;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return marks;
    }

    static HookMark? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<HookMark>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // ---------- Instalação no ~/.claude/settings.json ----------

    static string Command(string exe) => $"\"{exe}\" {HookArg}";

    static bool IsOurs(JsonNode? hook) => IsOurs(hook, HookArg);

    internal static bool IsOurs(JsonNode? hook, string arg) =>
        hook?["command"]?.GetValueKind() == JsonValueKind.String
        && hook["command"]!.GetValue<string>().Contains(arg, StringComparison.Ordinal);

    /// <summary>
    /// Garante as entradas deste app nos hooks PermissionRequest e Notification, apontando para o exe
    /// atual. Só grava se algo mudou. Sem a pasta ~/.claude (Claude Code não instalado), não faz nada.
    /// </summary>
    public static void Ensure()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !Directory.Exists(Path.GetDirectoryName(SettingsPath))) return;
        Edit(root =>
        {
            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            root["hooks"] = hooks;
            foreach (var evt in Events)
            {
                var list = hooks[evt] as JsonArray ?? new JsonArray();
                hooks[evt] = list;
                RemoveOurs(list);
                var entry = new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = Command(exe),
                        // O PermissionRequest pode esperar a resposta do tablet (até 590 s).
                        ["timeout"] = evt == "PermissionRequest" ? TabletAnswers.HookTimeoutSeconds : 5,
                    }),
                };
                if (evt == "PermissionRequest") entry["matcher"] = "*";
                list.Add(entry);
            }
        });
    }

    /// <summary>Tira só as entradas deste app (desinstalação ou envio desligado).</summary>
    public static void Remove()
    {
        if (!File.Exists(SettingsPath)) return;
        Edit(root =>
        {
            if (root["hooks"] is not JsonObject hooks) return;
            foreach (var evt in Events)
            {
                if (hooks[evt] is not JsonArray list) continue;
                RemoveOurs(list);
                if (list.Count == 0) hooks.Remove(evt);
            }
            if (hooks.Count == 0) root.Remove("hooks");
        });
        try
        {
            if (Directory.Exists(MarksFolder)) Directory.Delete(MarksFolder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Remove as entradas cujo hook é deste app; um grupo que ficou vazio sai inteiro.</summary>
    static void RemoveOurs(JsonArray list)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i]?["hooks"] is not JsonArray inner) continue;
            for (var j = inner.Count - 1; j >= 0; j--)
                if (IsOurs(inner[j])) inner.RemoveAt(j);
            if (inner.Count == 0) list.RemoveAt(i);
        }
    }

    static void Edit(Action<JsonObject> change) => EditJson(SettingsPath, change);

    /// <summary>Edita um JSON de configuração com cópia do original na primeira vez; só grava se mudou.</summary>
    internal static void EditJson(string path, Action<JsonObject> change)
    {
        try
        {
            var original = File.Exists(path) ? File.ReadAllText(path) : "";
            JsonObject root;
            if (string.IsNullOrWhiteSpace(original))
                root = new JsonObject();
            else if (JsonNode.Parse(original, documentOptions: new JsonDocumentOptions
                     {
                         AllowTrailingCommas = true,
                         CommentHandling = JsonCommentHandling.Skip,
                     }) is JsonObject parsed)
                root = parsed;
            else
                return; // arquivo inesperado: não mexe

            var before = root.ToJsonString();
            change(root);
            var after = root.ToJsonString();
            if (before == after) return;

            var backup = path + ".usage-ia-cockpit.bak";
            if (original.Length > 0 && !File.Exists(backup))
                File.WriteAllText(backup, original, new UTF8Encoding(false));
            var tmp = path + ".usage-ia-cockpit.tmp";
            // Sem escapar acentos: o arquivo continua legível para quem o edita à mão.
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(tmp, root.ToJsonString(options), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            AgentTasksMonitor.Log(e);
        }
    }
}

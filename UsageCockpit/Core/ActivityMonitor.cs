using System;
using System.IO;

namespace UsageCockpit.Core;

/// <summary>
/// Detecta se a ferramenta está trabalhando agora: o Claude Code grava a conversa em
/// ~/.claude/projects/**/*.jsonl e o Codex em ~/.codex/sessions/**/rollout-*.jsonl a cada passo.
/// Uma escrita nos últimos segundos significa "em uso".
/// </summary>
public sealed class ActivityMonitor : IDisposable
{
    static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(12);

    readonly FileSystemWatcher? _watcher;
    long _lastWriteTicks;

    public ActivityMonitor(string folder)
    {
        if (!Directory.Exists(folder))
            return;
        try
        {
            _watcher = new FileSystemWatcher(folder, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += OnWrite;
            _watcher.Created += OnWrite;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _watcher = null;
        }
    }

    void OnWrite(object sender, FileSystemEventArgs e) =>
        System.Threading.Interlocked.Exchange(ref _lastWriteTicks, DateTime.UtcNow.Ticks);

    public bool IsActive =>
        DateTime.UtcNow - new DateTime(System.Threading.Interlocked.Read(ref _lastWriteTicks), DateTimeKind.Utc) < ActiveWindow;

    public static ActivityMonitor ForClaude() => new(Http.Home(".claude", "projects"));
    public static ActivityMonitor ForCodex() => new(Http.Home(".codex", "sessions"));

    public void Dispose() => _watcher?.Dispose();
}

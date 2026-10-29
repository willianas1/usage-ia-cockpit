using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace UsageCockpit.Core;

/// <summary>Uma leitura completa da saúde do PC, já com os alertas calculados. Imutável.</summary>
public sealed record HealthReading(
    DateTimeOffset At,
    long UptimeSeconds,
    double CpuPercent,
    double? SpeedPercent,
    double? SpeedMHz,
    double? BaseMHz,
    HealthMemory Memory,
    HealthPower Power,
    IReadOnlyList<HealthDisk> Disks,
    HealthCounts Counts,
    IReadOnlyList<HealthProcess> TopProcesses,
    IReadOnlyList<HealthAlert> Alerts,
    string Status)
{
    public HealthDisk? SystemDisk => Disks.FirstOrDefault(d => d.IsSystem) ?? Disks.FirstOrDefault();
}

/// <summary>
/// Coleta indicadores de saúde do PC em segundo plano, só lendo: sem administrador, sem executar
/// outros programas e sem depender do idioma do Windows (nada de contadores pelo nome traduzido).
/// CPU, memória e energia a cada 5 s; processos, velocidade da CPU e discos a cada 30 s.
/// </summary>
public sealed class SystemHealthMonitor : IDisposable
{
    static readonly TimeSpan Every = TimeSpan.FromSeconds(5);
    const int SlowEveryTicks = 6; // 6 × 5 s = 30 s
    static readonly TimeSpan KeepCpu = TimeSpan.FromMinutes(6);

    readonly object _gate = new();
    readonly List<CpuSample> _cpu = new();
    CancellationTokenSource? _cts;
    SynchronizationContext? _ui;
    HealthReading? _latest;

    readonly record struct CpuSample(DateTimeOffset End, double Seconds, double Percent);

    /// <summary>A leitura mais recente, ou nulo antes da primeira (cerca de 5 s depois de ligar).</summary>
    public HealthReading? Latest
    {
        get { lock (_gate) return _latest; }
    }

    public bool Running => _cts != null;

    /// <summary>Nova leitura disponível. Disparado na thread que chamou <see cref="Start"/> (a da interface).</summary>
    public event Action? Sampled;

    public void Start()
    {
        if (_cts != null) return;
        _ui = SynchronizationContext.Current;
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(cts.Token));
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel(); // o laço termina sozinho na próxima espera
            _cts = null;
            _latest = null;
            _cpu.Clear();
        }
    }

    public void Dispose() => Stop();

    /// <summary>Uso médio da CPU (0 a 100) nas amostras da janela; sem amostra na janela, a última.</summary>
    public double CpuAverage(TimeSpan window)
    {
        lock (_gate)
        {
            if (_cpu.Count == 0) return 0;
            var from = DateTimeOffset.Now - window;
            double weight = 0, sum = 0;
            foreach (var s in _cpu)
            {
                if (s.End <= from) continue;
                weight += s.Seconds;
                sum += s.Percent * s.Seconds;
            }
            return Math.Round(weight > 0 ? sum / weight : _cpu[^1].Percent, 1);
        }
    }

    /// <summary>Média da janela inteira, ou nulo se as amostras ainda não cobrem a janela (app recém-aberto, PC voltando da suspensão).</summary>
    double? FullAverage(TimeSpan window)
    {
        var from = DateTimeOffset.Now - window;
        double weight = 0, sum = 0;
        foreach (var s in _cpu)
        {
            if (s.End <= from) continue;
            weight += s.Seconds;
            sum += s.Percent * s.Seconds;
        }
        return weight >= window.TotalSeconds * 0.9 ? sum / weight : null;
    }

    async Task RunAsync(CancellationToken ct)
    {
        using var collector = new Collector();
        try
        {
            collector.Prime();
            using var timer = new PeriodicTimer(Every);
            var tick = 0;
            while (await timer.WaitForNextTickAsync(ct))
            {
                // A primeira volta (5 s depois de ligar) já é completa, para a tela não ficar vazia.
                var slow = tick++ % SlowEveryTicks == 0;
                try
                {
                    Publish(collector.Sample(slow), ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Uma volta com problema não derruba o monitor: tenta de novo na próxima.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    void Publish(Collector.Data d, CancellationToken ct)
    {
        lock (_gate)
        {
            if (ct.IsCancellationRequested) return;
            var now = DateTimeOffset.Now;
            if (d.CpuSeconds > 0)
                _cpu.Add(new CpuSample(now, d.CpuSeconds, d.CpuPercent));
            _cpu.RemoveAll(s => s.End < now - KeepCpu);

            var alerts = HealthAlerts.Evaluate(FullAverage(HealthLimits.CpuWindow), d.Memory, d.Power, d.Disks,
                d.UptimeSeconds, d.Counts);
            _latest = new HealthReading(now, d.UptimeSeconds, Math.Round(d.CpuPercent, 1), d.SpeedPercent,
                d.SpeedMHz, d.BaseMHz, d.Memory, d.Power, d.Disks, d.Counts, d.TopProcesses, alerts,
                HealthAlerts.StatusOf(alerts));
        }

        if (_ui != null)
            _ui.Post(_ => { if (!ct.IsCancellationRequested) Sampled?.Invoke(); }, null);
        else
            Sampled?.Invoke();
    }

    /// <summary>Estado de uma execução do laço (deltas anteriores, consulta do PDH). Só a thread do laço usa.</summary>
    sealed class Collector : IDisposable
    {
        static readonly HashSet<string> HelperNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "node", "node_repl", "cmd", "powershell", "pwsh", "conhost", "bash",
        };

        public sealed record Data(
            double CpuPercent, double CpuSeconds, long UptimeSeconds,
            double? SpeedPercent, double? SpeedMHz, double? BaseMHz,
            HealthMemory Memory, HealthPower Power, IReadOnlyList<HealthDisk> Disks,
            HealthCounts Counts, IReadOnlyList<HealthProcess> TopProcesses);

        long _idle, _kernel, _user;
        long _cpuAt;

        readonly double? _baseMHz = Native.BaseMHz();
        IntPtr _pdhQuery, _pdhCounter;

        Dictionary<int, (string Name, TimeSpan Cpu)> _procTimes = new();
        long _procAt;

        IReadOnlyList<HealthDisk> _disks = Array.Empty<HealthDisk>();
        IReadOnlyList<HealthProcess> _top = Array.Empty<HealthProcess>();
        int _helpers;
        double? _speedPercent;

        /// <summary>Primeiras leituras, só para ter com o que comparar na próxima.</summary>
        public void Prime()
        {
            Native.GetSystemTimes(out _idle, out _kernel, out _user);
            _cpuAt = Stopwatch.GetTimestamp();
            OpenSpeedCounter();
            SampleProcesses();
        }

        public Data Sample(bool slow)
        {
            var (cpu, seconds) = SampleCpu();
            if (slow)
            {
                _speedPercent = SampleSpeed();
                _disks = SampleDisks();
                SampleProcesses();
            }

            var (memory, counts) = SampleMemory();
            var speedMHz = _speedPercent is { } sp && _baseMHz is { } b ? Math.Round(b * sp / 100) : (double?)null;
            return new Data(cpu, seconds, Environment.TickCount64 / 1000, _speedPercent, speedMHz, _baseMHz,
                memory, SamplePower(), _disks, counts with { LongRunningHelpers = _helpers }, _top);
        }

        (double Percent, double Seconds) SampleCpu()
        {
            if (!Native.GetSystemTimes(out var idle, out var kernel, out var user))
                return (0, 0);
            var now = Stopwatch.GetTimestamp();
            var seconds = Stopwatch.GetElapsedTime(_cpuAt, now).TotalSeconds;
            // O tempo do kernel inclui o ocioso.
            var total = (kernel - _kernel) + (user - _user);
            var busy = total - (idle - _idle);
            _idle = idle;
            _kernel = kernel;
            _user = user;
            _cpuAt = now;
            if (total <= 0) return (0, 0);
            // Peso limitado: um intervalo longo (suspensão) não vale por minutos de amostras.
            return (Math.Clamp(busy * 100.0 / total, 0, 100), Math.Min(seconds, Every.TotalSeconds * 2));
        }

        static (HealthMemory, HealthCounts) SampleMemory()
        {
            var status = new Native.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
            Native.GlobalMemoryStatusEx(ref status);
            var total = (long)status.TotalPhys;
            var available = (long)status.AvailPhys;

            var perf = new Native.PerformanceInformation { Size = (uint)Marshal.SizeOf<Native.PerformanceInformation>() };
            long commit, limit;
            long? kernelNonPaged = null;
            HealthCounts counts;
            if (Native.GetPerformanceInfo(ref perf, perf.Size))
            {
                var page = (long)perf.PageSize;
                commit = (long)perf.CommitTotal * page;
                limit = (long)perf.CommitLimit * page;
                kernelNonPaged = (long)perf.KernelNonpaged * page;
                counts = new HealthCounts((int)perf.ProcessCount, (int)perf.ThreadCount, (int)perf.HandleCount, 0);
            }
            else
            {
                // Sem o GetPerformanceInfo: o arquivo de paginação do GlobalMemoryStatusEx é o limite de commit.
                limit = (long)status.TotalPageFile;
                commit = limit - (long)status.AvailPageFile;
                counts = new HealthCounts(0, 0, 0, 0);
            }

            return (new HealthMemory(total, available, Percent(total - available, total), commit, limit,
                Percent(commit, limit), kernelNonPaged), counts);
        }

        static HealthPower SamplePower()
        {
            string source = "unknown";
            int? battery = null;
            if (Native.GetSystemPowerStatus(out var ps))
            {
                source = ps.ACLineStatus switch { 0 => "battery", 1 => "ac", _ => "unknown" };
                // 128 = sem bateria; 255 = desconhecido.
                if ((ps.BatteryFlag & 128) == 0 && ps.BatteryFlag != 255 && ps.BatteryLifePercent <= 100)
                    battery = ps.BatteryLifePercent;
            }
            return new HealthPower(source, Native.PowerMode(), battery);
        }

        static IReadOnlyList<HealthDisk> SampleDisks()
        {
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
            var list = new List<HealthDisk>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    var name = d.Name.TrimEnd('\\'); // só a letra: o rótulo do volume não sai daqui
                    var total = d.TotalSize;
                    var free = d.TotalFreeSpace;
                    list.Add(new HealthDisk(name, string.Equals(name, systemRoot, StringComparison.OrdinalIgnoreCase),
                        total, free, Percent(total - free, total)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
                if (list.Count == 26) break;
            }
            return list;
        }

        void OpenSpeedCounter()
        {
            // Nome em inglês: o PDH traduz sozinho, então funciona em qualquer idioma do Windows.
            if (Native.PdhOpenQuery(null, IntPtr.Zero, out _pdhQuery) != 0)
            {
                _pdhQuery = IntPtr.Zero;
                return;
            }
            if (Native.PdhAddEnglishCounter(_pdhQuery, @"\Processor Information(_Total)\% Processor Performance",
                    IntPtr.Zero, out _pdhCounter) != 0)
            {
                CloseSpeedCounter();
                return;
            }
            Native.PdhCollectQueryData(_pdhQuery); // primeira coleta: o contador é uma taxa e precisa de duas
        }

        double? SampleSpeed()
        {
            if (_pdhQuery == IntPtr.Zero) return null;
            if (Native.PdhCollectQueryData(_pdhQuery) != 0) return null;
            if (Native.PdhGetFormattedCounterValue(_pdhCounter, Native.PdhFmtDouble | Native.PdhFmtNoCap100,
                    out _, out var value) != 0 || value.CStatus != 0)
                return null;
            return value.DoubleValue >= 0 ? Math.Round(value.DoubleValue, 1) : null;
        }

        void CloseSpeedCounter()
        {
            if (_pdhQuery != IntPtr.Zero)
                Native.PdhCloseQuery(_pdhQuery);
            _pdhQuery = _pdhCounter = IntPtr.Zero;
        }

        /// <summary>
        /// Processos agrupados pelo nome do executável (sem caminho nem linha de comando), com o uso de
        /// CPU desde a volta anterior e a memória privada somada. Também conta terminais e Node antigos.
        /// </summary>
        void SampleProcesses()
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = _procAt == 0 ? 0 : Stopwatch.GetElapsedTime(_procAt, now).TotalSeconds;
            var cores = Math.Max(1, Environment.ProcessorCount);
            var times = new Dictionary<int, (string, TimeSpan)>(_procTimes.Count);
            var groups = new Dictionary<string, (int Instances, double CpuSeconds, long Memory)>(StringComparer.OrdinalIgnoreCase);
            var helpers = 0;
            var dayAgo = DateTime.Now - HealthLimits.HelperAge;

            var procs = Process.GetProcesses();
            try
            {
                foreach (var p in procs)
                {
                    if (p.Id == 0) continue; // "Idle": o tempo ocioso não é de um processo
                    string name;
                    long memory = 0;
                    try
                    {
                        name = p.ProcessName;
                        memory = p.PrivateMemorySize64;
                    }
                    catch (InvalidOperationException)
                    {
                        continue; // terminou no meio da leitura
                    }

                    double cpuSeconds = 0;
                    try
                    {
                        var t = p.TotalProcessorTime;
                        times[p.Id] = (name, t);
                        if (_procTimes.TryGetValue(p.Id, out var prev) && prev.Name == name && t >= prev.Cpu)
                            cpuSeconds = (t - prev.Cpu).TotalSeconds;
                    }
                    catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // Processo protegido ou do sistema: entra só com a memória.
                    }

                    if (HelperNames.Contains(name))
                    {
                        try
                        {
                            if (p.StartTime < dayAgo) helpers++;
                        }
                        catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
                        {
                        }
                    }

                    groups.TryGetValue(name, out var g);
                    groups[name] = (g.Instances + 1, g.CpuSeconds + cpuSeconds, g.Memory + memory);
                }
            }
            finally
            {
                foreach (var p in procs)
                    p.Dispose();
            }

            _procTimes = times;
            _procAt = now;
            _helpers = helpers;
            _top = groups
                .Select(kv => new HealthProcess(kv.Key, kv.Value.Instances,
                    elapsed > 0 ? Math.Round(Math.Clamp(kv.Value.CpuSeconds * 100 / (elapsed * cores), 0, 100), 1) : 0,
                    kv.Value.Memory))
                .OrderByDescending(p => p.CpuPercent)
                .ThenByDescending(p => p.MemoryBytes)
                .Take(HealthLimits.MaxTopProcesses)
                .ToList();
        }

        public void Dispose() => CloseSpeedCounter();
    }

    static double Percent(long part, long total) =>
        total > 0 ? Math.Round(Math.Clamp(part * 100.0 / total, 0, 100), 1) : 0;

    static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [StructLayout(LayoutKind.Sequential)]
        public struct PerformanceInformation
        {
            public uint Size;
            public nuint CommitTotal;
            public nuint CommitLimit;
            public nuint CommitPeak;
            public nuint PhysicalTotal;
            public nuint PhysicalAvailable;
            public nuint SystemCache;
            public nuint KernelTotal;
            public nuint KernelPaged;
            public nuint KernelNonpaged;
            public nuint PageSize;
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool GetPerformanceInfo(ref PerformanceInformation info, uint size);

        [StructLayout(LayoutKind.Sequential)]
        public struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        [DllImport("powrprof.dll")]
        static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

        static readonly Guid Balanced = Guid.Empty;
        static readonly Guid Performance = new("ded574b5-45a0-4f42-8737-46345c09c238");
        static readonly Guid Efficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
        static readonly Guid EfficiencyAlt = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");

        /// <summary>Modo de energia efetivo do Windows 10/11 (o "Modo de energia" das configurações).</summary>
        public static string PowerMode()
        {
            try
            {
                if (PowerGetEffectiveOverlayScheme(out var g) != 0) return "unknown";
                if (g == Efficiency || g == EfficiencyAlt) return "efficiency";
                if (g == Balanced) return "balanced";
                if (g == Performance) return "performance";
            }
            catch (EntryPointNotFoundException)
            {
                // Windows antigo, sem modo de energia sobreposto.
            }
            return "unknown";
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessorPowerInformation
        {
            public uint Number;
            public uint MaxMhz;
            public uint CurrentMhz;
            public uint MhzLimit;
            public uint MaxIdleState;
            public uint CurrentIdleState;
        }

        const int ProcessorInformation = 11;

        [DllImport("powrprof.dll")]
        static extern int CallNtPowerInformation(int level, IntPtr input, uint inputLength,
            [Out] ProcessorPowerInformation[] output, uint outputLength);

        /// <summary>Clock base do processador, em MHz (o mesmo do Win32_Processor.MaxClockSpeed).</summary>
        public static double? BaseMHz()
        {
            try
            {
                var info = new ProcessorPowerInformation[Math.Max(64, Environment.ProcessorCount)];
                var size = (uint)(info.Length * Marshal.SizeOf<ProcessorPowerInformation>());
                if (CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, info, size) == 0
                    && info[0].MaxMhz > 0)
                    return info[0].MaxMhz;
            }
            catch (EntryPointNotFoundException)
            {
            }

            // Plano B: o valor que o Windows mede no boot.
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("~MHz") is int mhz && mhz > 0 ? mhz : null;
        }

        public const uint PdhFmtDouble = 0x00000200;
        public const uint PdhFmtNoCap100 = 0x00008000; // o desempenho passa de 100% com turbo

        [StructLayout(LayoutKind.Explicit)]
        public struct PdhFmtCounterValue
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double DoubleValue;
        }

        [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
        public static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
        public static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        public static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll")]
        public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type,
            out PdhFmtCounterValue value);

        [DllImport("pdh.dll")]
        public static extern uint PdhCloseQuery(IntPtr query);
    }
}

/// <summary>Limites dos alertas da saúde do PC (contrato em docs/propostas/contrato-saude-pc.md). Mudar aqui muda em todo o app.</summary>
public static class HealthLimits
{
    public static readonly TimeSpan CpuWindow = TimeSpan.FromMinutes(5);
    public const double CpuWarn = 70, CpuDanger = 90;
    public const double MemoryWarn = 75, MemoryDanger = 90;
    public const double DiskWarn = 85, DiskDanger = 95;
    public const int UptimeWarnDays = 7, UptimeDangerDays = 14;
    public const long KernelPoolWarn = 1L << 30, KernelPoolDanger = 2L << 30; // 1 GB e 2 GB
    public const int ProcessesWarn = 500, ProcessesDanger = 800;
    public const int HelpersWarn = 20;
    public static readonly TimeSpan HelperAge = TimeSpan.FromHours(24);

    public const int MaxTopProcesses = 8;
    public const int MaxAlerts = 12;
}

/// <summary>Os alertas do contrato, com textos em português. Um por código; danger antes de warn, depois a ordem da tabela.</summary>
public static class HealthAlerts
{
    static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static IReadOnlyList<HealthAlert> Evaluate(double? cpu5Min, HealthMemory memory, HealthPower power,
        IReadOnlyList<HealthDisk> disks, long uptimeSeconds, HealthCounts counts)
    {
        var list = new List<HealthAlert>();

        if (cpu5Min is { } cpu && Level(cpu, HealthLimits.CpuWarn, HealthLimits.CpuDanger) is { } cpuLevel)
            list.Add(new("cpu_high", cpuLevel, $"CPU em {Floor(cpu)}% há 5 min", "Veja os apps mais pesados"));

        if (Level(memory.CommitPercent, HealthLimits.MemoryWarn, HealthLimits.MemoryDanger) is { } memLevel)
            list.Add(new("memory_high", memLevel, $"Memória comprometida em {Floor(memory.CommitPercent)}%",
                "Feche apps que não está usando"));

        var worstDisk = disks.OrderByDescending(d => d.UsedPercent).FirstOrDefault();
        if (worstDisk != null && Level(worstDisk.UsedPercent, HealthLimits.DiskWarn, HealthLimits.DiskDanger) is { } diskLevel)
        {
            var free = worstDisk.TotalBytes > 0 ? worstDisk.FreeBytes * 100.0 / worstDisk.TotalBytes : 0;
            list.Add(new("disk_low", diskLevel, $"Disco {worstDisk.Name} com {Whole(free)}% livre",
                "Limpe Temp e caches ou mova arquivos para outro disco"));
        }

        if (power.Mode == "efficiency" && power.Source == "ac")
            list.Add(new("power_efficiency", "warn", "Modo de energia em eficiência na tomada",
                "Mude para Equilibrado em Configurações › Energia"));

        var days = uptimeSeconds / 86400;
        if (Level(days, HealthLimits.UptimeWarnDays, HealthLimits.UptimeDangerDays) is { } upLevel)
            list.Add(new("uptime_long", upLevel, $"Ligado há {days} dias", "Reinicie para liberar memória"));

        if (memory.KernelNonPagedBytes is { } kernel
            && Level(kernel, HealthLimits.KernelPoolWarn, HealthLimits.KernelPoolDanger) is { } kLevel)
            list.Add(new("kernel_pool_high", kLevel,
                $"Memória do kernel em {(kernel / (double)(1L << 30)).ToString("0.0", PtBr)} GB",
                "Reinicie; se voltar a crescer, investigue drivers"));

        if (Level(counts.Processes, HealthLimits.ProcessesWarn, HealthLimits.ProcessesDanger) is { } pLevel)
            list.Add(new("processes_high", pLevel, $"{counts.Processes} processos abertos",
                "Feche apps e terminais esquecidos"));

        if (counts.LongRunningHelpers >= HealthLimits.HelpersWarn)
            list.Add(new("helpers_stale", "warn",
                $"{counts.LongRunningHelpers} processos auxiliares abertos há mais de 1 dia",
                "Feche terminais e reabra apps como o Codex"));

        // OrderBy é estável: dentro do mesmo nível fica a ordem da tabela.
        return list.OrderBy(a => a.Level == "danger" ? 0 : 1).Take(HealthLimits.MaxAlerts).ToList();
    }

    public static string StatusOf(IReadOnlyList<HealthAlert> alerts) =>
        alerts.Any(a => a.Level == "danger") ? "danger" : alerts.Count > 0 ? "warn" : "ok";

    static string? Level(double value, double warn, double danger) =>
        value >= danger ? "danger" : value >= warn ? "warn" : null;

    // Floor no valor usado (nunca mostra o limite antes de atingi-lo); arredondado no espaço livre.
    static string Floor(double v) => Math.Floor(v).ToString("0", PtBr);

    static string Whole(double v) => Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", PtBr);
}

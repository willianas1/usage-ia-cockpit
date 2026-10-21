using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsageCockpit.Controls;

/// <summary>
/// Relógio único das animações (~25 quadros/s). Um DispatcherTimer gasta bem menos CPU que
/// CompositionTarget.Rendering, que força o WPF a redesenhar a janela na taxa do monitor.
/// </summary>
public static class Ticker
{
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly DispatcherTimer Timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    static EventHandler? _tick;

    static Ticker() => Timer.Tick += (s, e) => { if (!Paused) _tick?.Invoke(s, e); };

    public static double Now => Clock.Elapsed.TotalSeconds;

    /// <summary>Com o painel oculto, nada precisa se mexer.</summary>
    public static bool Paused { get; set; }

    static readonly System.Collections.Generic.HashSet<object> SmoothRequests = new();

    /// <summary>
    /// Varredura e anel girando pedem ~25 quadros/s. Só o tremor em repouso fica em ~14 quadros/s,
    /// o que já parece vibração e gasta quase metade da CPU.
    /// </summary>
    public static void SetSmooth(object owner, bool smooth)
    {
        var changed = smooth ? SmoothRequests.Add(owner) : SmoothRequests.Remove(owner);
        if (changed)
            Timer.Interval = TimeSpan.FromMilliseconds(SmoothRequests.Count > 0 ? 40 : 70);
    }

    public static event EventHandler Tick
    {
        add
        {
            _tick += value;
            if (!Timer.IsEnabled) Timer.Start();
        }
        remove
        {
            _tick -= value;
            if (_tick == null) Timer.Stop();
        }
    }
}

/// <summary>Anel colorido que gira em volta do logo enquanto a ferramenta está trabalhando.</summary>
public static class ActivityRing
{
    /// <summary>Desenha o anel apontando para 0°; quem chama gira o visual para animar.</summary>
    public static void Draw(DrawingContext dc, Point center, double radius, double thickness, Brush brand)
    {
        var faint = brand.Clone();
        faint.Opacity = 0.18;
        faint.Freeze();
        dc.DrawEllipse(null, Pen(faint, thickness, PenLineCap.Flat), center, radius, radius);

        // "Cometa": cabeça forte e cauda mais clara, como um indicador de carregamento.
        var tail = brand.Clone();
        tail.Opacity = 0.45;
        tail.Freeze();
        dc.DrawGeometry(null, Pen(tail, thickness, PenLineCap.Round), Arc(center, radius, -150, -70));
        dc.DrawGeometry(null, Pen(brand, thickness, PenLineCap.Round), Arc(center, radius, -70, 0));
    }

    static Pen Pen(Brush b, double w, PenLineCap cap)
    {
        var p = new Pen(b, w) { StartLineCap = cap, EndLineCap = cap };
        p.Freeze();
        return p;
    }

    static Geometry Arc(Point c, double r, double a0, double a1)
    {
        Point P(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(a0), false, false);
            ctx.ArcTo(P(a1), new Size(r, r), 0, a1 - a0 > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Velocidade do giro: uma volta a cada 1,2 s.</summary>
    public static double AngleAt(double seconds) => seconds * 300 % 360;
}

/// <summary>Logo do modo compacto, com o anel girando em volta quando a ferramenta está em uso.</summary>
public sealed class LogoBadge : FrameworkElement
{
    public static readonly DependencyProperty LogoProperty = DependencyProperty.Register(
        nameof(Logo), typeof(Geometry), typeof(LogoBadge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LogoFillProperty = DependencyProperty.Register(
        nameof(LogoFill), typeof(Brush), typeof(LogoBadge),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrandProperty = DependencyProperty.Register(
        nameof(Brand), typeof(Brush), typeof(LogoBadge),
        new FrameworkPropertyMetadata(Palette.Clay, (d, _) => ((LogoBadge)d).DrawRing()));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(LogoBadge),
        new FrameworkPropertyMetadata(false, (d, _) => ((LogoBadge)d).UpdateActive()));

    public Geometry? Logo { get => (Geometry?)GetValue(LogoProperty); set => SetValue(LogoProperty, value); }
    public Brush LogoFill { get => (Brush)GetValue(LogoFillProperty); set => SetValue(LogoFillProperty, value); }
    public Brush Brand { get => (Brush)GetValue(BrandProperty); set => SetValue(BrandProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    const double Size = 28, LogoSize = 15;
    static readonly Point Center = new(Size / 2, Size / 2);

    readonly DrawingVisual _ring = new();
    readonly RotateTransform _spin = new(0, Size / 2, Size / 2);
    bool _subscribed;

    public LogoBadge()
    {
        _ring.Transform = _spin;
        _ring.Opacity = 0;
        AddVisualChild(_ring);
        DrawRing();
        Loaded += (_, _) => UpdateActive();
        Unloaded += (_, _) => Subscribe(false);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _ring;
    protected override System.Windows.Size MeasureOverride(System.Windows.Size available) => new(Size, Size);

    void DrawRing()
    {
        using var dc = _ring.RenderOpen();
        ActivityRing.Draw(dc, Center, Size / 2 - 1.5, 2, Brand);
    }

    void UpdateActive()
    {
        _ring.Opacity = IsActive ? 1 : 0;
        Subscribe(IsActive && IsLoaded);
    }

    void Subscribe(bool on)
    {
        if (on == _subscribed) return;
        _subscribed = on;
        Ticker.SetSmooth(this, on);
        if (on) Ticker.Tick += OnTick;
        else Ticker.Tick -= OnTick;
    }

    void OnTick(object? sender, EventArgs e) => _spin.Angle = ActivityRing.AngleAt(Ticker.Now);

    protected override void OnRender(DrawingContext dc)
    {
        if (Logo == null) return;
        var o = (Size - LogoSize) / 2;
        dc.PushTransform(new TranslateTransform(o, o));
        dc.PushTransform(new ScaleTransform(LogoSize / 24, LogoSize / 24));
        dc.DrawGeometry(LogoFill, null, Logo);
        dc.Pop();
        dc.Pop();
    }
}

using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace UsageCockpit.Controls;

/// <summary>
/// Mostrador com ponteiro (270°), faixa vermelha a partir de 85% e logo no centro.
/// O ponteiro e o miolo são visuais separados: tremer o ponteiro só gira uma transformação,
/// sem redesenhar o mostrador, o que mantém o consumo de CPU baixo.
/// </summary>
public sealed class Gauge : Instrument
{
    public static readonly DependencyProperty LogoProperty = DependencyProperty.Register(
        nameof(Logo), typeof(Geometry), typeof(Gauge),
        new FrameworkPropertyMetadata(null, (d, _) => ((Gauge)d).DrawHub()));

    public static readonly DependencyProperty LogoFillProperty = DependencyProperty.Register(
        nameof(LogoFill), typeof(Brush), typeof(Gauge),
        new FrameworkPropertyMetadata(null, (d, _) => ((Gauge)d).DrawHub()));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(Gauge), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty SweepTokenProperty = DependencyProperty.Register(
        nameof(SweepToken), typeof(int), typeof(Gauge),
        new FrameworkPropertyMetadata(0, (d, _) => ((Gauge)d).StartSweep()));

    public static readonly DependencyProperty ShowScaleLabelsProperty = DependencyProperty.Register(
        nameof(ShowScaleLabels), typeof(bool), typeof(Gauge),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InnerPercentProperty = DependencyProperty.Register(
        nameof(InnerPercent), typeof(double?), typeof(Gauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Segunda escala, num arco fino por dentro do principal (o uso semanal no modo compacto vertical).
    /// Nula, o arco não aparece.
    /// </summary>
    public double? InnerPercent { get => (double?)GetValue(InnerPercentProperty); set => SetValue(InnerPercentProperty, value); }

    /// <summary>Números 0/50/100 da escala. Desligados nos mini velocímetros, onde ficariam ilegíveis.</summary>
    public bool ShowScaleLabels { get => (bool)GetValue(ShowScaleLabelsProperty); set => SetValue(ShowScaleLabelsProperty, value); }

    public Geometry? Logo { get => (Geometry?)GetValue(LogoProperty); set => SetValue(LogoProperty, value); }
    public Brush? LogoFill { get => (Brush?)GetValue(LogoFillProperty); set => SetValue(LogoFillProperty, value); }

    /// <summary>Ferramenta em uso agora: o ponteiro treme levemente.</summary>
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    /// <summary>Cada mudança dispara a varredura: ponteiro desce a zero e volta ao valor.</summary>
    public int SweepToken { get => (int)GetValue(SweepTokenProperty); set => SetValue(SweepTokenProperty, value); }

    const double W = 160, H = 128, Cx = 80, Cy = 72, R = 56, Start = 135, Sweep = 270;
    const double DownSeconds = 0.45, UpSeconds = 0.9;

    static readonly Pen TrackPen = MakePen(Palette.Track, 8);
    static readonly Pen WarnZonePen = MakePen(Palette.WarnSoft, 8);
    static readonly Pen DangerZonePen = MakePen(Palette.DangerSoft, 8);
    static readonly Pen NeedlePen = MakePen(Palette.Needle, 2.5, PenLineCap.Round);
    static readonly Pen HubPen = MakePen(Palette.HubRim, 1);
    static readonly Pen WarnPen = MakePen(Palette.Warn, 8);
    static readonly Pen DangerPen = MakePen(Palette.Danger, 8);
    static readonly Geometry TrackArc = Arc(R, Start, Start + Sweep);
    static readonly Geometry WarnArc = Arc(R, AngleOf(Palette.WarnFrom), AngleOf(Palette.DangerFrom));
    static readonly Geometry DangerArc = Arc(R, AngleOf(Palette.DangerFrom), Start + Sweep);

    // Arco interno: mais fino e afastado do principal, para as duas escalas não se confundirem.
    const double InnerR = R - 15, InnerWidth = 4;
    static readonly Pen InnerTrackPen = MakePen(Palette.Track, InnerWidth);
    static readonly Pen InnerPen = MakePen(Palette.WeekBlue, InnerWidth);
    static readonly Pen InnerDangerPen = MakePen(Palette.WeekDanger, InnerWidth);
    static readonly Geometry InnerTrackArc = Arc(InnerR, Start, Start + Sweep);

    // Marcas da escala a cada 5%, coloridas pela faixa: normal, amarela (60–80%) e vermelha (80–100%).
    static readonly Geometry MinorTicks = Ticks(zone: 0, major: false);
    static readonly Geometry MajorTicks = Ticks(zone: 0, major: true);
    static readonly Geometry WarnMinorTicks = Ticks(zone: 1, major: false);
    static readonly Geometry WarnMajorTicks = Ticks(zone: 1, major: true);
    static readonly Geometry DangerMinorTicks = Ticks(zone: 2, major: false);
    static readonly Geometry DangerMajorTicks = Ticks(zone: 2, major: true);

    static double AngleOf(double pct) => Start + Sweep * pct / 100;
    static readonly Typeface LabelFace = new("Segoe UI");
    static readonly Typeface ReadoutFace = new(new FontFamily("Cascadia Mono, Consolas"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    static readonly Random Noise = new();

    enum Phase { Follow, Down, Up }

    // Miolo grande: o logo ocupa o centro até perto do arco interno e do número da leitura.
    const double HubRadius = 27, LogoSize = 40, RingRadius = HubRadius + 2.5;

    readonly DrawingVisual _needle = new();
    readonly DrawingVisual _hub = new();
    readonly DrawingVisual _ring = new();
    readonly RotateTransform _rotation = new(0, Cx, Cy);
    readonly RotateTransform _spin = new(0, Cx, Cy);
    readonly ScaleTransform _scale = new(1, 1);

    // Valor que o ponteiro mostra agora. Começa em zero: ao abrir, os ponteiros sobem até o valor.
    double _shown;
    double _drawnShown = double.NaN;
    Phase _phase;
    double _phaseStart, _phaseFrom, _lastFrame;
    bool _hooked;

    public Gauge()
    {
        var needleTransform = new TransformGroup();
        needleTransform.Children.Add(_rotation);
        needleTransform.Children.Add(_scale);
        _needle.Transform = needleTransform;
        var ringTransform = new TransformGroup();
        ringTransform.Children.Add(_spin);
        ringTransform.Children.Add(_scale);
        _ring.Transform = ringTransform;
        _ring.Opacity = 0;
        _hub.Transform = _scale;
        using (var dc = _needle.RenderOpen())
            dc.DrawLine(NeedlePen, Pt(-8, 0), Pt(R - 4, 0));
        AddVisualChild(_needle);
        AddVisualChild(_hub);
        AddVisualChild(_ring);
        DrawHub();
        DrawRing();
        UpdateNeedle();

        Loaded += (_, _) => { StaticNeedlesChanged += OnStaticChanged; UpdateHook(); };
        Unloaded += (_, _) => { StaticNeedlesChanged -= OnStaticChanged; UpdateHook(); };
    }

    protected override int VisualChildrenCount => 3;
    protected override Visual GetVisualChild(int index) => index switch { 0 => _needle, 1 => _hub, _ => _ring };

    // Respeita o espaço disponível mantendo a proporção: assim o mesmo mostrador serve de mini
    // velocímetro (Width/Height menores) sem o WPF recortá-lo no tamanho original.
    protected override Size MeasureOverride(Size available)
    {
        var s = Math.Min(1, Math.Min(available.Width / W, available.Height / H));
        return new(W * s, H * s);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var s = Math.Min(final.Width / W, final.Height / H);
        _scale.ScaleX = _scale.ScaleY = s;
        return final;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BrandProperty)
            DrawRing();
        if (e.Property == IsActiveProperty)
        {
            _ring.Opacity = IsActive ? 1 : 0;
            UpdateHook();
        }
        // Sem tremor o relógio fica parado em repouso: uma leitura nova precisa religá-lo para o ponteiro andar.
        if (e.Property == PercentProperty || e.Property == HasDataProperty)
            UpdateHook();
    }

    void OnStaticChanged(object? sender, EventArgs e)
    {
        UpdateHook();
        UpdateNeedle();
        InvalidateVisual();
    }

    // A varredura acontece mesmo com os ponteiros sem tremor: mostra que a atualização foi feita.
    void StartSweep()
    {
        _phase = Phase.Down;
        _phaseFrom = _shown;
        _phaseStart = Ticker.Now;
        UpdateHook();
    }

    // A animação roda sempre que o mostrador está na tela: o tremor constante do ponteiro mostra que
    // o app está vivo, e o anel em volta do logo mostra que a ferramenta está trabalhando.
    // Sem tremor, o relógio só é usado enquanto o anel gira ou o ponteiro está indo a um valor.
    void UpdateHook()
    {
        var on = IsLoaded && (!StaticNeedles || IsActive || Moving);
        if (on == _hooked) return;
        _hooked = on;
        if (on)
        {
            _lastFrame = Ticker.Now;
            Ticker.Tick += OnFrame;
        }
        else
        {
            Ticker.Tick -= OnFrame;
            Ticker.SetSmooth(this, false);
        }
    }

    double Target => HasData ? P : 0;

    bool Moving => _phase != Phase.Follow || Math.Abs(Target - _shown) > 0.05;

    void OnFrame(object? sender, EventArgs e)
    {
        var now = Ticker.Now;
        var dt = now - _lastFrame;
        _lastFrame = now;

        switch (_phase)
        {
            case Phase.Down:
            {
                var t = Math.Min(1, (now - _phaseStart) / DownSeconds);
                _shown = _phaseFrom * (1 - t * t * t);
                if (t >= 1) { _phase = Phase.Up; _phaseStart = now; }
                break;
            }
            case Phase.Up:
            {
                var t = Math.Min(1, (now - _phaseStart) / UpSeconds);
                _shown = Target * EaseOutBack(t);
                if (t >= 1) _phase = Phase.Follow;
                break;
            }
            default:
                _shown += (Target - _shown) * Math.Min(1, dt * 7);
                break;
        }

        var settled = _phase == Phase.Follow && Math.Abs(Target - _shown) <= 0.05;
        if (settled)
            _shown = Target;

        UpdateNeedle(now);
        if (IsActive)
            _spin.Angle = ActivityRing.AngleAt(now);
        Ticker.SetSmooth(this, IsActive || !settled);

        // O mostrador (arco colorido e número) só é redesenhado quando o valor exibido muda de verdade.
        if (double.IsNaN(_drawnShown) || Math.Abs(_drawnShown - _shown) > 0.2 || (settled && _drawnShown != _shown))
            InvalidateVisual();

        if (settled && StaticNeedles)
            UpdateHook();
    }

    void UpdateNeedle(double now = 0)
    {
        // Tremor constante: duas senoides fora de fase + um pouco de ruído, cerca de ±1,5°.
        var jitter = _hooked && HasData && !StaticNeedles
            ? 0.9 * Math.Sin(now * 19) + 0.45 * Math.Sin(now * 43 + 1.3) + 0.35 * (Noise.NextDouble() - 0.5)
            : 0;
        _rotation.Angle = Start + Sweep * Shown / 100 + jitter;
        _needle.Opacity = HasData ? 1 : 0;
    }

    void DrawRing()
    {
        using var dc = _ring.RenderOpen();
        ActivityRing.Draw(dc, new Point(Cx, Cy), RingRadius, 2.5, Brand);
    }

    // Fora da árvore visual (ex.: captura de tela de teste) não há animação: usa o valor final.
    double Shown => _hooked || IsLoaded ? Math.Clamp(_shown, 0, 104) : Target;

    static double EaseOutBack(double t)
    {
        const double c1 = 1.4, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!_hooked) UpdateNeedle();
        var shown = Shown;
        _drawnShown = shown;

        dc.PushTransform(_scale);
        dc.DrawGeometry(null, TrackPen, TrackArc);
        dc.DrawGeometry(null, WarnZonePen, WarnArc);
        dc.DrawGeometry(null, DangerZonePen, DangerArc);

        // Arco do valor: cor da marca até 60%, amarelo até 80% e vermelho daí em diante,
        // como a faixa de rotação de um conta-giros.
        if (HasData && shown > 0.5)
        {
            var value = Math.Min(shown, 100);
            dc.DrawGeometry(null, MakePen(Brand, 8), Arc(R, Start, AngleOf(Math.Min(value, Palette.WarnFrom))));
            if (value > Palette.WarnFrom)
                dc.DrawGeometry(null, WarnPen, Arc(R, AngleOf(Palette.WarnFrom), AngleOf(Math.Min(value, Palette.DangerFrom))));
            if (value > Palette.DangerFrom)
                dc.DrawGeometry(null, DangerPen, Arc(R, AngleOf(Palette.DangerFrom), AngleOf(value)));
        }

        if (InnerPercent is double inner)
            DrawInner(dc, Math.Clamp(inner, 0, 100));

        dc.DrawGeometry(null, MakePen(Palette.Tick, 1), MinorTicks);
        dc.DrawGeometry(null, MakePen(Palette.Tick, 2), MajorTicks);
        dc.DrawGeometry(null, MakePen(Palette.Warn, 1), WarnMinorTicks);
        dc.DrawGeometry(null, MakePen(Palette.Warn, 2), WarnMajorTicks);
        dc.DrawGeometry(null, MakePen(Palette.Danger, 1), DangerMinorTicks);
        dc.DrawGeometry(null, MakePen(Palette.Danger, 2), DangerMajorTicks);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var v in ShowScaleLabels ? new[] { 0, 50, 100 } : Array.Empty<int>())
            DrawText(dc, v.ToString(), LabelFace, 11, Palette.Muted, Pt(R - 17, Start + Sweep * v / 100.0), dpi);

        // O número acompanha o ponteiro durante a varredura.
        var readout = HasData ? $"{Math.Round(Math.Clamp(shown, 0, 100))}%" : "--";
        DrawText(dc, readout, ReadoutFace, 22, Palette.Level(P, HasData), new Point(Cx, Cy + 42), dpi);
        dc.Pop();
    }

    // Sem animação: o semanal muda devagar, e o ponteiro já mostra que o app está vivo.
    void DrawInner(DrawingContext dc, double value)
    {
        dc.DrawGeometry(null, InnerTrackPen, InnerTrackArc);
        if (!HasData || value <= 0.5) return;
        // Mesma cor nas duas contas: azul até 80%, depois o vermelho próprio do semanal.
        dc.DrawGeometry(null, InnerPen, Arc(InnerR, Start, AngleOf(Math.Min(value, Palette.DangerFrom))));
        if (value > Palette.DangerFrom)
            dc.DrawGeometry(null, InnerDangerPen, Arc(InnerR, AngleOf(Palette.DangerFrom), AngleOf(value)));
    }

    void DrawHub()
    {
        using var dc = _hub.RenderOpen();
        dc.DrawEllipse(Palette.Hub, HubPen, new Point(Cx, Cy), HubRadius, HubRadius);
        if (Logo == null) return;
        dc.PushTransform(new TranslateTransform(Cx - LogoSize / 2, Cy - LogoSize / 2));
        dc.PushTransform(new ScaleTransform(LogoSize / 24.0, LogoSize / 24.0));
        dc.DrawGeometry(LogoFill ?? Brand, null, Logo);
        dc.Pop();
        dc.Pop();
    }

    static Point Pt(double r, double deg)
    {
        var a = deg * Math.PI / 180;
        return new Point(Cx + r * Math.Cos(a), Cy + r * Math.Sin(a));
    }

    static Geometry Arc(double r, double a0, double a1)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(Pt(r, a0), false, false);
            ctx.ArcTo(Pt(r, a1), new Size(r, r), 0, a1 - a0 > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    static Geometry Ticks(int zone, bool major)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            for (var i = 0; i <= 20; i++)
            {
                var pct = i * 5;
                var z = pct >= Palette.DangerFrom ? 2 : pct >= Palette.WarnFrom ? 1 : 0;
                if (z != zone || (i % 5 == 0) != major) continue;
                var a = Start + Sweep * i / 20;
                ctx.BeginFigure(Pt(R + 7, a), false, false);
                ctx.LineTo(Pt(R + (major ? 15 : 11), a), true, false);
            }
        }
        g.Freeze();
        return g;
    }

    static void DrawText(DrawingContext dc, string text, Typeface face, double size, Brush brush, Point center, double dpi)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush, dpi);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }
}

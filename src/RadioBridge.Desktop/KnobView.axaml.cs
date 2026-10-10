using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace RadioBridge.Desktop;

/// <summary>Shows a row's concentric knobs turning: one pointer step per detent, ring lit while turning.</summary>
public partial class KnobView : UserControl
{
    public static readonly StyledProperty<int> InnerTurnsProperty = AvaloniaProperty.Register<KnobView, int>(nameof(InnerTurns));
    public static readonly StyledProperty<int> OuterTurnsProperty = AvaloniaProperty.Register<KnobView, int>(nameof(OuterTurns));

    private static readonly TimeSpan LitTime = TimeSpan.FromMilliseconds(250);

    private IDisposable? _innerLit;
    private IDisposable? _outerLit;

    public KnobView()
    {
        InitializeComponent();
    }

    /// <summary>Net inner-knob detents, clockwise positive.</summary>
    public int InnerTurns
    {
        get => GetValue(InnerTurnsProperty);
        set => SetValue(InnerTurnsProperty, value);
    }

    /// <summary>Net outer-knob detents, clockwise positive.</summary>
    public int OuterTurns
    {
        get => GetValue(OuterTurnsProperty);
        set => SetValue(OuterTurnsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == InnerTurnsProperty)
            Turn(InnerRotor, InnerRing, InnerTurns, degreesPerDetent: 24, ref _innerLit);
        else if (change.Property == OuterTurnsProperty)
            Turn(OuterRotor, OuterRing, OuterTurns, degreesPerDetent: 15, ref _outerLit);
    }

    private static void Turn(Control rotor, Control ring, int turns, int degreesPerDetent, ref IDisposable? lit)
    {
        rotor.RenderTransform = TransformOperations.Parse($"rotate({turns * degreesPerDetent}deg)");
        ring.Classes.Set("active", true);
        lit?.Dispose();
        lit = DispatcherTimer.RunOnce(() => ring.Classes.Set("active", false), LitTime);
    }
}

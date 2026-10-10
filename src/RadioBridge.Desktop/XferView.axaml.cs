using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace RadioBridge.Desktop;

/// <summary>Shows a row's ACT/STBY switch. Stays lit briefly after a release, so quick presses are visible too.</summary>
public partial class XferView : UserControl
{
    public static readonly StyledProperty<bool> PressedProperty = AvaloniaProperty.Register<XferView, bool>(nameof(Pressed));
    public static readonly StyledProperty<bool> HeldProperty = AvaloniaProperty.Register<XferView, bool>(nameof(Held));

    private static readonly TimeSpan MinLit = TimeSpan.FromMilliseconds(200);

    private IDisposable? _release;

    public XferView()
    {
        InitializeComponent();
    }

    public bool Pressed
    {
        get => GetValue(PressedProperty);
        set => SetValue(PressedProperty, value);
    }

    /// <summary>A long press has fired.</summary>
    public bool Held
    {
        get => GetValue(HeldProperty);
        set => SetValue(HeldProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PressedProperty)
        {
            _release?.Dispose();
            if (Pressed)
                Body.Classes.Set("down", true);
            else
                _release = DispatcherTimer.RunOnce(() => Body.Classes.Set("down", false), MinLit);
        }
        else if (change.Property == HeldProperty)
        {
            Body.Classes.Set("hold", Held);
            Label.Text = Held ? "HOLD" : "ACT/STBY";
        }
    }
}

namespace RadioBridge.Core.Panel;

/// <summary>The panel has two identical, independent rows.</summary>
public enum PanelRow
{
    Upper,
    Lower,
}

/// <summary>Seven-position function selector. Values follow the bit order of the input report.</summary>
public enum Selector
{
    Com1,
    Com2,
    Nav1,
    Nav2,
    Adf,
    Dme,
    Xpdr,
}

/// <summary>The two concentric tuning knobs of a row.</summary>
public enum Knob
{
    /// <summary>Small knob: fine steps.</summary>
    Inner,

    /// <summary>Large knob: coarse steps.</summary>
    Outer,
}

public abstract record PanelEvent(PanelRow Row);

public sealed record SelectorMoved(PanelRow Row, Selector Position) : PanelEvent(Row);

/// <param name="Direction">+1 clockwise, -1 counter-clockwise (one detent).</param>
public sealed record KnobTurned(PanelRow Row, Knob Knob, int Direction) : PanelEvent(Row);

/// <summary>The ACT/STBY momentary switch of a row was pressed or released.</summary>
public sealed record ButtonChanged(PanelRow Row, bool Pressed) : PanelEvent(Row);

/// <summary>Text for the four 5-digit displays. See <see cref="RadioPanelProtocol.EncodeDisplays"/> for the syntax.</summary>
public sealed record PanelDisplays(string UpperLeft, string UpperRight, string LowerLeft, string LowerRight)
{
    public static readonly PanelDisplays Blank = new("", "", "", "");
}

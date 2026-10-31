using System.Globalization;
using BridgeManager.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace BridgeManager.Modern;

/// <summary>Original, theme-aware line artwork driven by normalized input, not hit testing.</summary>
internal sealed class ControllerInputDiagram : Canvas
{
    private sealed record Control(Shape Highlight, FrameworkElement? Label,
        RectangleGeometry? Clip = null, Rect Bounds = default);
    private readonly Dictionary<int, Control> _controls = [];
    private readonly TranslateTransform _leftMotion = new();
    private readonly TranslateTransform _rightMotion = new();
    internal uint PressedButtons { get; private set; }
    internal double LeftTriggerLevel { get; private set; }
    internal double RightTriggerLevel { get; private set; }
    internal Point LeftStickOffset => new(_leftMotion.X, _leftMotion.Y);
    internal Point RightStickOffset => new(_rightMotion.X, _rightMotion.Y);
    internal bool IsHighlighted(int bit) => _controls.TryGetValue(bit, out var control) && control.Highlight.Opacity > 0;

    internal ControllerInputDiagram(string profile)
    {
        Width = 640;
        Height = 440;
        IsHitTestVisible = false;
        UseLayoutRounding = false;
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, Width, Height) };
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        var ns = profile == "ns2pro";
        var xbox = profile == "xbox";
        var anchors = ControllerInputGeometry.Anchors(profile);

        var shell = ShapePath(ControllerInputGeometry.Shell(profile), surface: true);
        shell.StrokeThickness = 2;
        ShapePath(ControllerInputGeometry.Seams(profile));

        if (!ns && !xbox)
        {
            AddButton(17, ControllerInputGeometry.Touchpad);
            AddButton(18, RectanglePath(307, 293, 26, 9, 4));
            // Quiet speaker slots establish scale without decorative textures.
            for (var i = 0; i < 5; i++)
                ShapePath(RectanglePath(304 + i * 7, 235, 3, 2, 1));
        }

        var faceKeys = new[] { "south", "east", "west", "north" };
        var faceLabels = ns ? new[] { "B", "A", "Y", "X" }
            : xbox ? new[] { "A", "B", "X", "Y" } : new[] { "×", "○", "□", "△" };
        for (var bit = 0; bit < 4; bit++)
        {
            var center = anchors[faceKeys[bit]];
            AddButton(bit, CirclePath(center, 19), center, ns || xbox ? faceLabels[bit] : null, 20);
            if (!ns && !xbox) FaceSymbol(bit, center);
        }

        var dpad = anchors["dpad"];
        if (xbox) ShapePath(CirclePath(dpad, 45), surface: true);
        if (ns) ShapePath(FormattableString.Invariant($"M {dpad.X-14},{dpad.Y-43} H {dpad.X+14} V {dpad.Y-14} H {dpad.X+43} V {dpad.Y+14} H {dpad.X+14} V {dpad.Y+43} H {dpad.X-14} V {dpad.Y+14} H {dpad.X-43} V {dpad.Y-14} H {dpad.X-14} Z"), surface: true);
        foreach (var (bit, angle, key, glyph) in new[]
        {
            (4, 0d, "dpad_up", "▲"), (5, 180d, "dpad_down", "▼"),
            (6, 270d, "dpad_left", "◀"), (7, 90d, "dpad_right", "▶")
        })
        {
            string P(double x, double y)
            {
                var p = angle switch
                {
                    90 => new Point(dpad.X - y, dpad.Y + x),
                    180 => new Point(dpad.X - x, dpad.Y - y),
                    270 => new Point(dpad.X + y, dpad.Y - x),
                    _ => new Point(dpad.X + x, dpad.Y + y)
                };
                return FormattableString.Invariant($"{p.X},{p.Y}");
            }
            var data = $"M {P(-14,-41)} Q {P(0,-45)} {P(14,-41)} L {P(14,-23)} L {P(0,-9)} L {P(-14,-23)} Z";
            AddButton(bit, data, anchors[key], glyph, 9);
        }
        AddButton(8, ShoulderPath(anchors["left_shoulder"]),
            anchors["left_shoulder"], ns ? "L" : xbox ? "LB" : "L1", 14);
        AddButton(9, ShoulderPath(anchors["right_shoulder"]),
            anchors["right_shoulder"], ns ? "R" : xbox ? "RB" : "R1", 14);
        Trigger(10, anchors["left_trigger"], ns ? "ZL" : xbox ? "LT" : "L2");
        Trigger(11, anchors["right_trigger"], ns ? "ZR" : xbox ? "RT" : "R2");
        if (ns || xbox)
        {
            AddButton(12, CirclePath(anchors["back"], 11), anchors["back"], ns ? "−" : "▣", 14);
            AddButton(13, CirclePath(anchors["start"], 11), anchors["start"], ns ? "+" : "≡", 14);
        }
        else
        {
            Small(12, anchors["back"], "⋮");
            Small(13, anchors["start"], "≡");
        }
        AddButton(16, CirclePath(anchors["guide"], 14), anchors["guide"], ns ? "⌂" : xbox ? "X" : "PS", 13);
        Stick(14, anchors["left_stick"], _leftMotion);
        Stick(15, anchors["right_stick"], _rightMotion);
        if (ns)
        {
            var capture = anchors["capture"];
            AddButton(19, RectanglePath(capture.X - 10, capture.Y - 10, 20, 20, 4), capture, "●", 13);
            AddButton(24, CirclePath(anchors["c"], 12), anchors["c"], "C", 14);
            // Rear buttons are identified as a separate strip, not falsely
            // placed on the front shell at the stick/face-button coordinates.
            Label(new Point(320, 424), "背键", 14);
            AddButton(20, RectanglePath(218, 412, 46, 24, 4), new Point(241, 424), "GL", 14);
            AddButton(21, RectanglePath(376, 412, 46, 24, 4), new Point(399, 424), "GR", 14);
        }
        else if (profile == "dse")
        {
            foreach (var (bit, x, text) in new[] { (20, 214d, "P1"), (22, 276d, "Fn L"),
                         (23, 364d, "Fn R"), (21, 426d, "P2") })
                AddButton(bit, RectanglePath(x - 24, 416, 48, 20, 6), new Point(x, 426), text, 10);
        }
        Reset();
    }

    internal void Update(ControllerInputSnapshot input)
    {
        PressedButtons = input.Buttons;
        LeftTriggerLevel = Math.Clamp(input.LeftTrigger / 65535d, 0, 1);
        RightTriggerLevel = Math.Clamp(input.RightTrigger / 65535d, 0, 1);
        foreach (var (bit, control) in _controls)
        {
            var down = (input.Buttons & (1u << bit)) != 0;
            var level = bit == 10 ? LeftTriggerLevel : bit == 11 ? RightTriggerLevel : down ? 1d : 0d;
            if (control.Clip is not null)
            {
                var b = control.Bounds;
                control.Clip.Rect = new Rect(b.X, b.Bottom - b.Height * level, b.Width, b.Height * level);
                // Trigger button bits may be asserted by a device before its
                // analog sample arrives; preserve digital press feedback too.
                if (down && level == 0) control.Clip.Rect = b;
            }
            var active = down || level > 0;
            control.Highlight.Opacity = active ? 1 : 0;
            if (control.Label is not null)
                control.Label.Opacity = active && (control.Clip is null || level >= 0.5 || (down && level == 0)) ? 1 : 0;
        }
        Move(_leftMotion, input.LeftX, input.LeftY);
        Move(_rightMotion, input.RightX, input.RightY);
    }

    internal void Reset()
    {
        PressedButtons = 0;
        LeftTriggerLevel = RightTriggerLevel = 0;
        foreach (var control in _controls.Values)
        {
            control.Highlight.Opacity = 0;
            if (control.Label is not null) control.Label.Opacity = 0;
        }
        Move(_leftMotion, 0, 0);
        Move(_rightMotion, 0, 0);
    }

    private static void Move(TranslateTransform motion, int x, int y)
    {
        var dx = Math.Clamp(x / 32768d, -1, 1);
        var dy = -Math.Clamp(y / 32768d, -1, 1);
        var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        motion.X = dx / length * 18;
        motion.Y = dy / length * 18;
    }

    private void Small(int bit, Point p, string text) =>
        AddButton(bit, RectanglePath(p.X - 8, p.Y - 13, 16, 26, 7), p, text, 13);

    private void Trigger(int bit, Point p, string text)
    {
        var data = FormattableString.Invariant($"M {p.X-35},{p.Y+18} L {p.X-32},{p.Y-9} Q {p.X-28},{p.Y-25} {p.X-9},{p.Y-24} L {p.X+13},{p.Y-22} Q {p.X+33},{p.Y-19} {p.X+35},{p.Y+18} Z");
        AddButton(bit, data, p, text, 14);
        var bounds = new Rect(p.X - 37, p.Y - 26, 74, 46);
        var clip = new RectangleGeometry { Rect = bounds };
        _controls[bit].Highlight.Clip = clip;
        _controls[bit] = _controls[bit] with { Clip = clip, Bounds = bounds };
    }

    private void Stick(int bit, Point p, TranslateTransform motion)
    {
        ShapePath(CirclePath(p, 45), surface: true);
        ShapePath(FormattableString.Invariant($"M {p.X-4},{p.Y} H {p.X+4} M {p.X},{p.Y-4} V {p.Y+4}"));
        var thumb = new Canvas { Width = Width, Height = Height, RenderTransform = motion };
        var baseCap = ShapePath(CirclePath(p, 27), surface: true);
        Children.Remove(baseCap);
        thumb.Children.Add(baseCap);
        var highlight = ShapePath(CirclePath(p, 27), highlight: true);
        Children.Remove(highlight);
        thumb.Children.Add(highlight);
        Children.Add(thumb);
        _controls[bit] = new Control(highlight, null);
    }

    private void FaceSymbol(int bit, Point p)
    {
        var data = bit switch
        {
            0 => FormattableString.Invariant($"M {p.X-8},{p.Y-8} L {p.X+8},{p.Y+8} M {p.X+8},{p.Y-8} L {p.X-8},{p.Y+8}"),
            1 => CirclePath(p, 10),
            2 => RectanglePath(p.X - 9, p.Y - 9, 18, 18, 0),
            _ => FormattableString.Invariant($"M {p.X},{p.Y-11} L {p.X+11},{p.Y+8} L {p.X-11},{p.Y+8} Z")
        };
        ShapePath(data).StrokeThickness = 2.2;
        var active = ShapePath(data, highlight: true, onAccent: true);
        active.Fill = null;
        active.StrokeThickness = 2.2;
        _controls[bit] = _controls[bit] with { Label = active };
    }

    private static string ShoulderPath(Point p) => FormattableString.Invariant(
        $"M {p.X-39},{p.Y+9} Q {p.X-40},{p.Y-4} {p.X-28},{p.Y-8} Q {p.X},{p.Y-17} {p.X+30},{p.Y-8} Q {p.X+41},{p.Y-3} {p.X+39},{p.Y+9} Z");

    private void AddButton(int bit, string data, Point? center = null, string? text = null,
        double size = 12)
    {
        ShapePath(data, surface: true);
        var active = ShapePath(data, highlight: true);
        FrameworkElement? activeLabel = null;
        if (center is Point p && text is not null)
        {
            Label(p, text, size);
            activeLabel = Label(p, text, size, active: true);
        }
        _controls[bit] = new Control(active, activeLabel);
    }

    private XamlPath ShapePath(string data, bool surface = false, bool highlight = false, bool onAccent = false)
    {
        // Only trusted, locally generated geometry reaches XamlReader. Theme
        // expressions remain attached to neutral shapes across theme switches.
        var shape = (XamlPath)XamlReader.Load(
            "<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            (onAccent ? "Stroke='{ThemeResource TextOnAccentFillColorPrimaryBrush}' " :
                highlight ? "Stroke='{ThemeResource AccentFillColorDefaultBrush}' Fill='{ThemeResource AccentFillColorDefaultBrush}' "
                : "Stroke='{ThemeResource BridgeSecondaryTextBrush}' " +
                  (surface ? "Fill='{ThemeResource BridgeSurfaceBrush}' " : "")) +
            "StrokeThickness='1.6' Data='" + data + "'/>");
        shape.Width = Width;
        shape.Height = Height;
        shape.Stretch = Stretch.None;
        shape.StrokeLineJoin = PenLineJoin.Round;
        shape.StrokeStartLineCap = shape.StrokeEndLineCap = PenLineCap.Round;
        if (highlight)
        {
            shape.Opacity = 0;
        }
        Children.Add(shape);
        return shape;
    }

    private TextBlock Label(Point p, string text, double size, bool active = false)
    {
        var label = (TextBlock)XamlReader.Load(
            "<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "Foreground='{ThemeResource " + (active ? "TextOnAccentFillColorPrimaryBrush" : "BridgeSecondaryTextBrush") + "}'/>");
        label.Text = text;
        label.FontSize = size;
        label.FontFamily = new FontFamily("Segoe UI");
        label.TextAlignment = TextAlignment.Center;
        label.Width = 64;
        label.Height = size + 10;
        if (active)
        {
            label.Opacity = 0;
        }
        Canvas.SetLeft(label, p.X - 32);
        Canvas.SetTop(label, p.Y - (size + 4) / 2);
        Children.Add(label);
        return label;
    }

    private static string CirclePath(Point p, double r) => FormattableString.Invariant(
        $"M {p.X-r},{p.Y} A {r},{r} 0 1 0 {p.X+r},{p.Y} A {r},{r} 0 1 0 {p.X-r},{p.Y} Z");

    private static string RectanglePath(double x, double y, double w, double h, double r) =>
        FormattableString.Invariant($"M {x+r},{y} L {x+w-r},{y} Q {x+w},{y} {x+w},{y+r} L {x+w},{y+h-r} Q {x+w},{y+h} {x+w-r},{y+h} L {x+r},{y+h} Q {x},{y+h} {x},{y+h-r} L {x},{y+r} Q {x},{y} {x+r},{y} Z");
}

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
    private readonly Brush _accent = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 47, 128, 237));
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
        var anchors = ControllerArtwork.Anchors(profile);

        // Keep mapping-page artwork and its anchor contract untouched. This
        // separate view is optimized for legible, live input at desktop sizes.
        ShapePath(ns || xbox ?
            "M 158,87 C 199,71 253,79 282,81 L 358,81 C 387,79 441,71 482,87 " +
            "C 523,101 536,143 545,190 C 557,254 577,343 561,383 " +
            "Q 552,407 530,405 C 505,403 487,361 469,323 Q 460,303 441,303 " +
            "L 199,303 Q 180,303 171,323 C 153,361 135,403 110,405 " +
            "Q 88,407 79,383 C 63,343 83,254 95,190 C 104,143 117,101 158,87 Z" :
            "M 145,90 C 182,76 219,82 243,86 Q 320,73 397,86 C 421,82 458,76 495,90 " +
            "C 529,102 545,155 560,210 C 578,275 589,351 575,389 Q 566,412 545,407 " +
            "C 521,402 499,351 480,313 Q 471,294 451,290 Q 320,321 189,290 " +
            "Q 169,294 160,313 C 141,351 119,402 95,407 Q 74,412 65,389 " +
            "C 51,351 62,275 80,210 C 95,155 111,102 145,90 Z", surface: true);

        if (!ns && !xbox)
        {
            // Faceplate seams and touchpad are outlines, not a dark center slab.
            ShapePath("M 228,86 C 241,121 240,164 231,191 Q 218,217 189,241 " +
                "C 150,278 128,347 100,400 M 412,86 C 399,121 400,164 409,191 " +
                "Q 422,217 451,241 C 490,278 512,347 540,400");
            AddButton(17, "M 246,96 Q 320,85 394,96 Q 403,98 401,110 L 391,170 " +
                "Q 388,188 373,189 L 267,189 Q 252,188 249,170 L 239,110 Q 237,98 246,96 Z");
            AddButton(18, RectanglePath(307, 273, 26, 10, 5));
            Label(new Point(320, 297), "MIC", 8);
        }

        var faceKeys = new[] { "south", "east", "west", "north" };
        var faceLabels = ns ? new[] { "B", "A", "Y", "X" }
            : xbox ? new[] { "A", "B", "X", "Y" } : new[] { "×", "○", "□", "△" };
        for (var bit = 0; bit < 4; bit++)
            AddButton(bit, CirclePath(anchors[faceKeys[bit]], 19), anchors[faceKeys[bit]], faceLabels[bit], 24);

        var dpad = ns || xbox ? new Point(244, 240) : new Point(167, 171);
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
            var data = $"M {P(-12,-39)} Q {P(0,-43)} {P(12,-39)} L {P(13,-23)} L {P(0,-10)} L {P(-13,-23)} Z";
            AddButton(bit, data, anchors[key], glyph, 9);
        }
        AddButton(8, RectanglePath(anchors["left_shoulder"].X - 36, 87, 72, 22, 7),
            anchors["left_shoulder"], ns ? "L" : xbox ? "LB" : "L1", 11);
        AddButton(9, RectanglePath(anchors["right_shoulder"].X - 36, 87, 72, 22, 7),
            anchors["right_shoulder"], ns ? "R" : xbox ? "RB" : "R1", 11);
        Trigger(10, anchors["left_trigger"], ns ? "ZL" : xbox ? "LT" : "L2");
        Trigger(11, anchors["right_trigger"], ns ? "ZR" : xbox ? "RT" : "R2");
        Small(12, anchors["back"], ns || xbox ? "−" : "⋮");
        Small(13, anchors["start"], ns ? "+" : "≡");
        AddButton(16, CirclePath(anchors["guide"], 14), anchors["guide"], ns ? "⌂" : xbox ? "X" : "PS", 11);
        Stick(14, anchors["left_stick"], _leftMotion);
        Stick(15, anchors["right_stick"], _rightMotion);
        if (ns)
        {
            Small(19, anchors["capture"], "●");
            Small(24, anchors["c"], "C");
            // Rear buttons are identified as a separate strip, not falsely
            // placed on the front shell at the stick/face-button coordinates.
            AddButton(20, RectanglePath(218, 416, 46, 20, 6), new Point(241, 426), "GL", 10);
            AddButton(21, RectanglePath(376, 416, 46, 20, 6), new Point(399, 426), "GR", 10);
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
        motion.X = Math.Clamp(x / 32768d, -1, 1) * 22;
        motion.Y = -Math.Clamp(y / 32768d, -1, 1) * 22;
    }

    private void Small(int bit, Point p, string text) =>
        AddButton(bit, RectanglePath(p.X - 8, p.Y - 13, 16, 26, 7), p, text, 13);

    private void Trigger(int bit, Point p, string text)
    {
        var data = FormattableString.Invariant($"M {p.X-35},{p.Y+18} L {p.X-32},{p.Y-9} Q {p.X-28},{p.Y-25} {p.X-9},{p.Y-24} L {p.X+13},{p.Y-22} Q {p.X+33},{p.Y-19} {p.X+35},{p.Y+18} Z");
        AddButton(bit, data, p, text, 12);
        var bounds = new Rect(p.X - 37, p.Y - 26, 74, 46);
        var clip = new RectangleGeometry { Rect = bounds };
        _controls[bit].Highlight.Clip = clip;
        _controls[bit] = _controls[bit] with { Clip = clip, Bounds = bounds };
    }

    private void Stick(int bit, Point p, TranslateTransform motion)
    {
        ShapePath(CirclePath(p, 40), surface: true);
        var thumb = new Canvas { Width = Width, Height = Height, RenderTransform = motion };
        var baseCap = ShapePath(CirclePath(p, 29), surface: true);
        Children.Remove(baseCap);
        thumb.Children.Add(baseCap);
        var highlight = ShapePath(CirclePath(p, 29), highlight: true);
        Children.Remove(highlight);
        thumb.Children.Add(highlight);
        Children.Add(thumb);
        _controls[bit] = new Control(highlight, null);
    }

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

    private XamlPath ShapePath(string data, bool surface = false, bool highlight = false)
    {
        // Only trusted, locally generated geometry reaches XamlReader. Theme
        // expressions remain attached to neutral shapes across theme switches.
        var shape = (XamlPath)XamlReader.Load(
            "<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "Stroke='{ThemeResource BridgeSecondaryTextBrush}' StrokeThickness='1.6' " +
            (surface ? "Fill='{ThemeResource BridgeSurfaceBrush}' " : "") + "Data='" + data + "'/>");
        shape.Width = Width;
        shape.Height = Height;
        shape.Stretch = Stretch.None;
        shape.StrokeLineJoin = PenLineJoin.Round;
        shape.StrokeStartLineCap = shape.StrokeEndLineCap = PenLineCap.Round;
        if (highlight)
        {
            shape.Fill = _accent;
            shape.Stroke = _accent;
            shape.Opacity = 0;
        }
        Children.Add(shape);
        return shape;
    }

    private TextBlock Label(Point p, string text, double size, bool active = false)
    {
        var label = (TextBlock)XamlReader.Load(
            "<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "Foreground='{ThemeResource BridgeSecondaryTextBrush}'/>");
        label.Text = text;
        label.FontSize = size;
        label.FontFamily = new FontFamily("Segoe UI");
        label.TextAlignment = TextAlignment.Center;
        label.Width = 64;
        label.Height = size + 10;
        if (active)
        {
            label.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
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

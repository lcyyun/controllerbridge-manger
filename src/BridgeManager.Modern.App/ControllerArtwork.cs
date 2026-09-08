using System.Collections.ObjectModel;
using System.Globalization;
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

/// <summary>Passive vector artwork. The host owns scaling, pins and accessibility.</summary>
internal static class ControllerArtwork
{
    public const double Width = 640;
    public const double Height = 420;
    private const string Paper = "#F2F4F3", White = "#FFFFFF", Mist = "#DEE3E2";
    private const string Ink = "#24282B", Charcoal = "#34383A", Graphite = "#4A5053";
    private const string Line = "#8D9699", Quiet = "#BBC3C5", Trim = "#969D9F";

    // Original geometry, with anatomy checked against Nintendo and Sony diagrams:
    // https://en-americas-support.nintendo.com/app/answers/detail/a_id/68527
    // https://controller.dl.playstation.net/controller/lang/en/2100003.html
    private static readonly IReadOnlyDictionary<string, Point> SonyEdgeFront = Map(
        ("south", 473, 205), ("east", 507, 171),
        ("west", 439, 171), ("north", 473, 137),
        ("dpad_up", 167, 145), ("dpad_down", 167, 197),
        ("dpad_left", 141, 171), ("dpad_right", 193, 171),
        ("left_shoulder", 170, 98), ("right_shoulder", 470, 98),
        ("left_trigger", 174, 62), ("right_trigger", 466, 62),
        ("back", 219, 124), ("start", 421, 124),
        ("left_stick", 246, 245), ("right_stick", 394, 245),
        ("guide", 320, 245), ("touchpad", 320, 139), ("mute", 320, 277),
        ("left_function", 246, 302), ("right_function", 394, 302));
    private static readonly IReadOnlyDictionary<string, Point> SonyFront =
        new ReadOnlyDictionary<string, Point>(SonyEdgeFront
            .Where(control => control.Key is not ("left_function" or "right_function"))
            .ToDictionary(control => control.Key, control => control.Value, StringComparer.Ordinal));
    private static readonly IReadOnlyDictionary<string, Point> NintendoFront = Map(
        ("south", 463, 195), ("east", 497, 161),
        ("west", 429, 161), ("north", 463, 127),
        ("dpad_up", 244, 216), ("dpad_down", 244, 264),
        ("dpad_left", 220, 240), ("dpad_right", 268, 240),
        ("left_shoulder", 177, 95), ("right_shoulder", 463, 95),
        ("left_trigger", 181, 60), ("right_trigger", 459, 60),
        ("back", 257, 135), ("start", 383, 135),
        ("left_stick", 179, 163), ("right_stick", 390, 240),
        ("guide", 353, 177), ("capture", 287, 177), ("c", 320, 278));
    // Physical left appears on image right in a rear view.
    private static readonly IReadOnlyDictionary<string, Point> SonyRear = Map(
        ("left_paddle", 403, 248), ("right_paddle", 237, 248));
    private static readonly IReadOnlyDictionary<string, Point> NintendoRear = Map(
        ("left_paddle", 469, 247), ("right_paddle", 171, 247));

    /// <summary>UI-thread factory. Null/unknown profiles use ds5; front Fn is opt-in.</summary>
    public static Canvas Create(string? profile, bool rear = false, bool edge = false)
    {
        var canvas = new Canvas
        {
            Width = Width, Height = Height, IsHitTestVisible = false, UseLayoutRounding = false,
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, Width, Height) }
        };
        AutomationProperties.SetAccessibilityView(canvas, AccessibilityView.Raw);
        var ns = IsNintendo(profile);
        Triggers(canvas, ns, rear);
        Shape(canvas, ns ? NintendoOutline : SonyOutline, ns ? Charcoal : Paper, Line, 1.5);
        if (rear) Rear(canvas, ns);
        else
        {
            if (ns) NintendoFace(canvas);
            else SonyFace(canvas, edge);
            Shoulders(canvas, ns);
        }
        return canvas;
    }

    /// <summary>Read-only intrinsic coordinates; rear includes only GL/GR or Edge paddles.</summary>
    public static IReadOnlyDictionary<string, Point> Anchors(
        string? profile, bool rear = false, bool edge = false) =>
        IsNintendo(profile) ? (rear ? NintendoRear : NintendoFront) :
        (rear ? SonyRear : edge ? SonyEdgeFront : SonyFront);

    private static bool IsNintendo(string? profile) =>
        string.Equals(profile, "ns2pro", StringComparison.OrdinalIgnoreCase);
    private static IReadOnlyDictionary<string, Point> Map(
        params (string Key, double X, double Y)[] controls) =>
        new ReadOnlyDictionary<string, Point>(controls.ToDictionary(
            control => control.Key, control => new Point(control.X, control.Y), StringComparer.Ordinal));

    private const string SonyOutline =
        "M 153,85 C 179,77 214,80 243,85 C 288,78 352,78 397,85 " +
        "C 426,80 461,77 487,85 C 516,96 530,128 547,174 " +
        "C 567,228 587,301 575,344 C 570,365 558,379 541,376 " +
        "C 519,373 500,341 479,303 C 467,281 459,273 443,275 " +
        "C 405,279 391,297 366,301 C 338,305 302,305 274,301 " +
        "C 249,297 235,279 197,275 C 181,273 173,281 161,303 " +
        "C 140,341 121,373 99,376 C 82,379 70,365 65,344 " +
        "C 53,301 73,228 93,174 C 110,128 124,96 153,85 Z";
    private const string NintendoOutline =
        "M 162,85 C 202,71 255,79 280,80 L 360,80 " +
        "C 385,79 438,71 478,85 C 515,98 530,130 539,176 " +
        "C 550,230 569,312 564,342 C 561,364 547,380 528,380 " +
        "C 501,380 487,350 473,320 L 457,304 C 453,297 447,296 437,296 " +
        "L 203,296 C 193,296 187,297 183,304 L 167,320 " +
        "C 153,350 139,380 112,380 C 93,380 79,364 76,342 " +
        "C 71,312 90,230 101,176 C 110,130 125,98 162,85 Z";

    private static void SonyFace(Canvas canvas, bool edge)
    {
        Pair(canvas, "M 98,366 C 116,323 142,280 179,262", null, Quiet);
        Shape(canvas,
            "M 235,89 Q 320,75 405,89 L 400,154 C 399,177 405,195 421,211 " +
            "Q 448,244 461,276 C 434,274 414,291 390,299 Q 320,318 250,299 " +
            "C 226,291 206,274 179,276 Q 192,244 219,211 " +
            "C 235,195 241,177 240,154 Z", Ink);
        Shape(canvas,
            "M 251,96 Q 320,87 389,96 Q 395,97 394,107 L 389,167 " +
            "Q 388,180 377,181 L 263,181 Q 252,180 251,167 L 246,107 Q 245,97 251,96 Z",
            edge ? Graphite : Mist, edge ? Line : Quiet);
        Dpad(canvas, false);
        FaceButtons(canvas, false);
        SmallButton(canvas, SonyFront["back"], "create", false, 8, 13);
        SmallButton(canvas, SonyFront["start"], "options", false, 8, 13);
        Stick(canvas, SonyFront["left_stick"]);
        Stick(canvas, SonyFront["right_stick"]);
        for (var row = 0; row < 2; row++)
        for (var col = 0; col < 5; col++)
            Oval(canvas, new Point(308 + col * 6, 206 + row * 5), 1, 1, Line);
        Glyph(canvas, SonyFront["guide"], "ps", Paper, 0.94);
        Box(canvas, 308, 272, 24, 10, Graphite, Line);
        Glyph(canvas, new Point(320, 292), "mic", Quiet, 0.65);
        if (!edge) return;
        foreach (var key in new[] { "left_function", "right_function" })
        {
            var x = SonyEdgeFront[key].X;
            Shape(canvas, FormattableString.Invariant(
                $"M {x - 13},287 L {x + 13},287 L {x + 12},305 Q {x},316 {x - 12},305 Z"),
                Graphite, Line);
            Shape(canvas, FormattableString.Invariant($"M {x - 7},303 L {x + 7},303"), null, Quiet);
        }
    }

    private static void NintendoFace(Canvas canvas)
    {
        Shape(canvas,
            "M 164,89 C 205,77 250,85 280,85 L 360,85 C 390,85 435,77 476,89 " +
            "C 508,101 523,131 525,164 C 526,204 505,246 478,263 " +
            "Q 465,290 437,290 L 203,290 Q 175,290 162,263 " +
            "C 135,246 114,204 115,164 C 117,131 132,101 164,89 Z", "#3C4143", Graphite);
        Stick(canvas, NintendoFront["left_stick"]);
        Stick(canvas, NintendoFront["right_stick"]);
        Dpad(canvas, true);
        FaceButtons(canvas, true);
        SmallButton(canvas, NintendoFront["back"], "minus", true, 12, 12);
        SmallButton(canvas, NintendoFront["start"], "plus", true, 12, 12);
        SmallButton(canvas, NintendoFront["capture"], "capture", true, 11, 11, true);
        SmallButton(canvas, NintendoFront["guide"], "home", true, 13, 13);
        SmallButton(canvas, NintendoFront["c"], "C", true, 11, 11, true);
    }

    private static void Rear(Canvas canvas, bool ns)
    {
        if (ns)
        {
            Pair(canvas,
                "M 147,212 Q 151,203 160,210 C 173,221 186,237 189,250 " +
                "Q 191,258 184,263 L 174,276 Q 170,281 164,277 L 139,258 Q 131,251 134,241 Z",
                Graphite, Line);
            Legend(canvas, NintendoRear["right_paddle"], "GR", Paper, 10);
            Legend(canvas, NintendoRear["left_paddle"], "GL", Paper, 10);
            Oval(canvas, new Point(320, 290), 5, 2, Ink, Line);
            Box(canvas, 305, 87, 30, 6, Ink, Line);
            return;
        }
        Pair(canvas, "M 98,367 C 115,315 143,237 175,207", null, Quiet);
        Shape(canvas,
            "M 218,182 Q 320,163 422,182 Q 443,187 451,211 L 438,271 " +
            "C 410,280 390,295 364,298 L 276,298 C 250,295 230,280 202,271 " +
            "L 189,211 Q 197,187 218,182 Z", Mist, Quiet);
        foreach (var x in new[] { 218d, 422d })
        {
            Box(canvas, x - 5, 123, 10, 27, Mist, Line);
            Box(canvas, x - 3, 127, 6, 11, Graphite, null, 2);
        }
        Oval(canvas, new Point(237, 220), 10, 10, Quiet, Line);
        Oval(canvas, new Point(403, 220), 10, 10, Quiet, Line);
        Pair(canvas,
            "M 230,215 Q 240,211 245,221 L 258,258 Q 260,267 252,272 " +
            "Q 243,277 237,268 L 219,238 Q 215,230 224,225 Z", Graphite, Line);
        Oval(canvas, new Point(320, 213), 2, 2, Line);
        Box(canvas, 306, 265, 28, 10, Paper, Line);
        Shape(canvas, "M 315,270 L 325,270", null, Line);
        Box(canvas, 306, 282, 28, 8, Graphite);
        Box(canvas, 305, 87, 30, 7, Ink, Line);
    }

    private static void Triggers(Canvas canvas, bool ns, bool rear)
    {
        Pair(canvas, ns
            ? "M 143,78 L 147,53 Q 149,43 167,42 L 199,44 Q 216,46 217,56 L 217,80 Z"
            : "M 137,76 L 140,55 Q 143,43 159,42 L 191,44 Q 208,47 209,59 L 209,84 Q 173,88 137,76 Z",
            ns ? Trim : Ink, Line);
        Legend(canvas, new Point(ns ? 181 : 174, ns ? 60 : 62),
            ns ? (rear ? "ZR" : "ZL") : (rear ? "R2" : "L2"), ns ? Ink : Paper, 11);
        Legend(canvas, new Point(ns ? 459 : 466, ns ? 60 : 62),
            ns ? (rear ? "ZL" : "ZR") : (rear ? "L2" : "R2"), ns ? Ink : Paper, 11);
        if (ns)
            Pair(canvas, "M 123,103 Q 132,82 159,76 Q 193,67 225,75 L 253,82 " +
                "Q 202,77 166,89 Q 143,96 123,112 Z", Trim);
    }

    private static void Shoulders(Canvas canvas, bool ns)
    {
        // Exposed caps must be above the shell at the shoulder anchor coordinates.
        Pair(canvas, ns
            ? "M 139,93 Q 168,80 204,86 L 215,99 Q 176,95 136,108 Z"
            : "M 133,96 Q 163,83 200,87 L 207,107 Q 169,100 130,113 Z", ns ? Trim : Ink, Line);
        var anchors = ns ? NintendoFront : SonyFront;
        Legend(canvas, anchors["left_shoulder"], ns ? "L" : "L1", ns ? Ink : Paper, 10);
        Legend(canvas, anchors["right_shoulder"], ns ? "R" : "R1", ns ? Ink : Paper, 10);
    }

    private static void Stick(Canvas canvas, Point center)
    {
        Oval(canvas, center, 40, 40, Ink, "#697276");
        Oval(canvas, center, 32, 32, Graphite, Line);
        Oval(canvas, center, 25, 25, Charcoal, "#707A7E");
    }

    private static void Dpad(Canvas canvas, bool ns)
    {
        if (ns)
            Shape(canvas, "M 234,203 Q 234,201 237,201 L 251,201 Q 254,201 254,204 " +
                "L 254,230 L 280,230 Q 283,230 283,233 L 283,247 Q 283,250 280,250 " +
                "L 254,250 L 254,276 Q 254,279 251,279 L 237,279 Q 234,279 234,276 " +
                "L 234,250 L 208,250 Q 205,250 205,247 L 205,233 Q 205,230 208,230 L 234,230 Z",
                Ink, Line);
        var center = ns ? new Point(244, 240) : new Point(167, 171);
        foreach (var angle in new[] { 0d, 90d, 180d, 270d })
        {
            if (!ns)
            {
                var cap = Shape(canvas,
                    "M 157,134 Q 167,131 177,134 L 178,150 L 167,160 L 156,150 Z",
                    White, Line);
                if (angle != 0) cap.Data.Transform = Rotation(center, angle);
            }
            var arrow = Shape(canvas, ns ? "M 241,217 L 244,213 L 247,217 Z" :
                "M 164,143 L 167,139 L 170,143 Z", ns ? Quiet : Graphite);
            if (angle != 0) arrow.Data.Transform = Rotation(center, angle);
        }
    }

    private static void FaceButtons(Canvas canvas, bool ns)
    {
        var anchors = ns ? NintendoFront : SonyFront;
        var keys = new[] { "north", "east", "south", "west" };
        var glyphs = ns ? new[] { "X", "A", "B", "Y" } : new[] { "triangle", "circle", "cross", "square" };
        for (var i = 0; i < keys.Length; i++)
        {
            var center = anchors[keys[i]];
            Oval(canvas, center, ns ? 18.5 : 17, ns ? 18.5 : 17, ns ? Ink : White, Line);
            Glyph(canvas, center, glyphs[i], ns ? Paper : Graphite, 0.96);
        }
    }

    private static void SmallButton(Canvas canvas, Point center, string glyph, bool dark,
        double rx, double ry, bool square = false)
    {
        if (square) Box(canvas, center.X - rx, center.Y - ry, rx * 2, ry * 2, Ink, Line, 3);
        else Oval(canvas, center, rx, ry, dark ? Ink : White, Line);
        Glyph(canvas, center, glyph, dark ? Paper : Graphite,
            glyph is "create" or "options" ? 0.64 : 0.76);
    }

    private static void Glyph(Canvas canvas, Point center, string glyph, string ink, double scale = 1)
    {
        var data = glyph switch
        {
            "triangle" => "M 0,-8 L 9,7 L -9,7 Z",
            "circle" => "M -8,0 A 8,8 0 1 0 8,0 A 8,8 0 1 0 -8,0 Z",
            "cross" or "X" => "M -6,-8 L 6,8 M 6,-8 L -6,8",
            "square" => "M -7,-7 L 7,-7 L 7,7 L -7,7 Z",
            "A" => "M -6,8 L 0,-8 L 6,8 M -3.6,2 L 3.6,2",
            "B" => "M -5,8 L -5,-8 L 1,-8 C 8,-8 8,0 1,0 L -5,0 M 1,0 C 9,0 9,8 1,8 Z",
            "Y" => "M -6,-8 L 0,0 L 6,-8 M 0,0 L 0,8",
            "C" => "M 5,-6 C -7,-13 -10,12 5,6",
            "minus" => "M -6,0 L 6,0",
            "plus" => "M -6,0 L 6,0 M 0,-6 L 0,6",
            "home" => "M -7,-1 L 0,-7 L 7,-1 M -5,-2 L -5,6 L 5,6 L 5,-2 M -1,6 L -1,1 L 2,1 L 2,6",
            "capture" => "M -6,0 A 6,6 0 1 0 6,0 A 6,6 0 1 0 -6,0 Z",
            "create" => "M 0,-7 L 0,-1 M -6,-4 L -3,1 M 6,-4 L 3,1",
            "options" => "M -5,-5 L 5,-5 M -5,0 L 5,0 M -5,5 L 5,5",
            "mic" => "M -2,-5 Q 0,-7 2,-5 L 2,0 Q 0,3 -2,0 Z " +
                "M -4,-1 Q -4,5 0,5 Q 4,5 4,-1 M 0,5 L 0,7 M -3,7 L 3,7",
            "ps" => "M -3,-10 L -3,9 L 1,10 L 1,-5 C 8,-3 8,1 4,2 L 4,5 C 14,5 12,-5 3,-8 Z " +
                "M -5,1 C -17,4 -14,9 -5,9 L -5,6 C -11,6 -10,5 -5,4 Z " +
                "M 4,5 L 4,8 L 9,6 C 14,5 16,7 10,9 L 3,12 L 9,12 C 26,6 15,1 4,5 Z",
            _ => throw new ArgumentOutOfRangeException(nameof(glyph))
        };
        var path = Shape(canvas, data, glyph == "ps" ? ink : null,
            glyph == "ps" ? null : ink, 1.8 * scale);
        // Transform geometry before Path layout clips negative local glyph coordinates.
        path.Data.Transform = new CompositeTransform
        {
            ScaleX = scale, ScaleY = scale, TranslateX = center.X, TranslateY = center.Y
        };
    }

    private static void Legend(Canvas canvas, Point center, string text, string color, double size)
    {
        var label = new TextBlock
        {
            Text = text, Width = 38, Height = 20, FontSize = size,
            FontFamily = new FontFamily("Segoe UI"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Solid(color), TextAlignment = TextAlignment.Center, IsHitTestVisible = false
        };
        AutomationProperties.SetAccessibilityView(label, AccessibilityView.Raw);
        Canvas.SetLeft(label, center.X - 19);
        Canvas.SetTop(label, center.Y - 9);
        canvas.Children.Add(label);
    }

    private static XamlPath Shape(Canvas canvas, string data, string? fill,
        string? stroke = null, double thickness = 1.2)
    {
        // XamlReader receives only trusted local geometry and invariant numbers.
        var path = (XamlPath)XamlReader.Load(
            "<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='" + data + "'/>");
        path.Width = Width;
        path.Height = Height;
        path.Stretch = Stretch.None;
        path.Fill = fill is null ? null : Solid(fill);
        path.Stroke = stroke is null ? null : Solid(stroke);
        path.StrokeThickness = thickness;
        path.StrokeLineJoin = PenLineJoin.Round;
        path.StrokeStartLineCap = PenLineCap.Round;
        path.StrokeEndLineCap = PenLineCap.Round;
        path.IsHitTestVisible = false;
        canvas.Children.Add(path);
        return path;
    }

    private static void Pair(Canvas canvas, string data, string? fill, string? stroke = null)
    {
        Shape(canvas, data, fill, stroke);
        Shape(canvas, data, fill, stroke).Data.Transform =
            new ScaleTransform { ScaleX = -1, ScaleY = 1, CenterX = Width / 2 };
    }

    private static RotateTransform Rotation(Point center, double angle) =>
        new() { Angle = angle, CenterX = center.X, CenterY = center.Y };

    private static void Oval(Canvas canvas, Point center, double rx, double ry,
        string fill, string? stroke = null)
    {
        var ellipse = new Ellipse
        {
            Width = rx * 2, Height = ry * 2, Fill = Solid(fill),
            Stroke = stroke is null ? null : Solid(stroke), StrokeThickness = 1.2, IsHitTestVisible = false
        };
        Canvas.SetLeft(ellipse, center.X - rx);
        Canvas.SetTop(ellipse, center.Y - ry);
        canvas.Children.Add(ellipse);
    }

    private static void Box(Canvas canvas, double x, double y, double width, double height,
        string fill, string? stroke = null, double radius = 5)
    {
        var rectangle = new Rectangle
        {
            Width = width, Height = height, RadiusX = Math.Min(radius, height / 2),
            RadiusY = Math.Min(radius, height / 2), Fill = Solid(fill),
            Stroke = stroke is null ? null : Solid(stroke), StrokeThickness = 1.2, IsHitTestVisible = false
        };
        Canvas.SetLeft(rectangle, x);
        Canvas.SetTop(rectangle, y);
        canvas.Children.Add(rectangle);
    }

    private static SolidColorBrush Solid(string hex)
    {
        var rgb = uint.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255,
            (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }
}

using Windows.Foundation;

namespace BridgeManager.Modern;

// Original front-view geometry for the live tester. Coordinates intentionally
// belong to this view; mapping pins keep their existing artwork contract.
internal static class ControllerInputGeometry
{
    internal const string SonyShell =
        "M 137,100 C 161,89 188,87 210,96 Q 320,80 430,96 C 452,87 479,89 503,100 " +
        "C 525,111 541,154 554,202 C 572,267 585,344 572,382 Q 565,403 547,400 " +
        "C 525,398 510,361 489,332 Q 478,315 456,314 Q 437,329 416,326 " +
        "L 224,326 Q 203,329 184,314 Q 162,315 151,332 C 130,361 115,398 93,400 " +
        "Q 75,403 68,382 C 55,344 68,267 86,202 C 99,154 115,111 137,100 Z";
    internal const string NintendoShell =
        "M 153,91 C 192,77 230,86 275,87 L 365,87 C 410,86 448,77 487,91 " +
        "C 519,103 533,135 542,178 C 554,230 575,326 563,363 " +
        "C 557,384 544,394 527,390 C 506,385 489,352 474,322 " +
        "Q 465,305 445,302 Q 320,320 195,302 Q 175,305 166,322 " +
        "C 151,352 134,385 113,390 C 96,394 83,384 77,363 " +
        "C 65,326 86,230 98,178 C 107,135 121,103 153,91 Z";
    internal const string XboxShell =
        "M 151,94 Q 183,79 228,88 Q 320,96 412,88 Q 457,79 489,94 " +
        "C 521,108 537,142 548,187 L 579,325 C 588,365 576,388 555,390 " +
        "C 534,393 519,368 501,343 L 467,295 Q 459,285 441,289 " +
        "Q 320,307 199,289 Q 181,285 173,295 L 139,343 " +
        "C 121,368 106,393 85,390 C 64,388 52,365 61,325 " +
        "L 92,187 C 103,142 119,108 151,94 Z";
    internal const string SonySeams =
        "M 210,96 C 217,123 220,166 222,192 Q 224,218 207,236 " +
        "C 153,290 120,359 111,394 M 430,96 C 423,123 420,166 418,192 " +
        "Q 416,218 433,236 C 487,290 520,359 529,394";
    internal const string NintendoSeams =
        "M 111,154 C 102,198 122,248 164,271 Q 176,285 195,302 " +
        "M 529,154 C 538,198 518,248 476,271 Q 464,285 445,302";
    internal const string XboxSeams =
        "M 102,194 C 126,223 147,246 173,295 M 538,194 C 514,223 493,246 467,295 " +
        "M 225,94 Q 320,110 415,94";
    internal const string Touchpad =
        "M 232,108 Q 320,96 408,108 Q 419,110 417,123 L 404,198 " +
        "Q 402,218 380,219 L 260,219 Q 238,218 236,198 L 223,123 Q 221,110 232,108 Z";

    internal static string Shell(string profile) => profile switch
    { "ns2pro" => NintendoShell, "xbox" => XboxShell, _ => SonyShell };
    internal static string Seams(string profile) => profile switch
    { "ns2pro" => NintendoSeams, "xbox" => XboxSeams, _ => SonySeams };

    internal static Dictionary<string, Point> Anchors(string profile)
    {
        var sony = profile is not ("ns2pro" or "xbox");
        var xbox = profile == "xbox";
        var face = sony ? new Point(485, 187) : xbox ? new Point(478, 177) : new Point(470, 169);
        var dpad = sony ? new Point(155, 187) : xbox ? new Point(250, 252) : new Point(246, 248);
        return new()
        {
            ["south"] = new(face.X, face.Y + 34), ["north"] = new(face.X, face.Y - 34),
            ["east"] = new(face.X + 34, face.Y), ["west"] = new(face.X - 34, face.Y),
            ["dpad"] = dpad,
            ["dpad_up"] = new(dpad.X, dpad.Y - 29), ["dpad_down"] = new(dpad.X, dpad.Y + 29),
            ["dpad_left"] = new(dpad.X - 29, dpad.Y), ["dpad_right"] = new(dpad.X + 29, dpad.Y),
            ["left_shoulder"] = new(165, 90), ["right_shoulder"] = new(475, 90),
            ["left_trigger"] = new(165, 52), ["right_trigger"] = new(475, 52),
            ["back"] = sony ? new(199, 128) : xbox ? new(282, 159) : new(260, 134),
            ["start"] = sony ? new(441, 128) : xbox ? new(358, 159) : new(380, 134),
            ["left_stick"] = sony ? new(229, 268) : new(179, 171),
            ["right_stick"] = sony ? new(411, 268) : new(391, 248),
            ["guide"] = sony ? new(320, 271) : xbox ? new(320, 123) : new(354, 179),
            ["capture"] = new(286, 179), ["c"] = new(320, 276)
        };
    }
}

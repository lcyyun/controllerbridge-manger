using System.Text.Json;
using System.Text.Json.Nodes;
using BridgeManager.Core;
using BridgeManager.Core.FirmwareModules;

internal static class MappingPairTests
{
    private static readonly string[] Profiles = ["ds5", "ns2pro"];
    private static readonly string[] Outputs = ["ds5", "ns2pro", "xbox"];
    private static readonly (string Action, string Verb)[] Actions =
        [("readAction", "get"), ("applyAction", "set"), ("resetAction", "reset"), ("saveAction", "save")];

    public static void Templates()
    {
        foreach (var profile in Profiles)
        foreach (var output in Outputs)
        {
            var control = Control(profile, output);
            var parameters = BridgeButtonMapping.Parameters(control, "south", "east");
            Equal(4, parameters.Count);
            Equal(profile, parameters["profile"]);
            Equal(output, parameters["output"]);
            foreach (var (_, verb) in Actions)
            {
                var suffix = verb == "set" ? " {target} {source}" : "";
                var request = new BridgeModuleOperationDefinition
                {
                    Request = $"mapping {verb} {{profile}} {{output}}{suffix}"
                };
                Equal($"mapping {verb} {profile} {output}" + (verb == "set" ? " south east" : ""),
                    BridgeModuleOperationEngine.BuildRequest(request, parameters));
                var missing = new Dictionary<string, string>(parameters);
                missing.Remove("output");
                Reject(() => BridgeModuleOperationEngine.BuildRequest(request, missing), "unresolved");
            }
            Equal($"mapping set {profile} {output} south none",
                BridgeModuleOperationEngine.BuildRequest(new BridgeModuleOperationDefinition
                {
                    Request = "mapping set {profile} {output} {target} {source}"
                }, BridgeButtonMapping.Parameters(control, "south", "none")));
        }
        Equal(0, BridgeButtonMapping.Parameters(Control(null, null)).Count);
        Equal(1, BridgeButtonMapping.Parameters(Control("ds5", null)).Count);
    }

    public static void Replies()
    {
        foreach (var profile in Profiles)
        foreach (var output in Outputs)
        {
            var control = Control(profile, output);
            var reply = Reply(profile, output);
            Equal(BridgeButtonMapping.ControlIds.Count, Read(control, reply).Count);
            reply["entries"]!["south"] = "none";
            Equal("none", Read(control, reply)["south"]);

            foreach (var field in new[] { "profile", "output", "mapping_schema" })
            {
                var missing = reply.DeepClone().AsObject();
                missing.Remove(field);
                Reject(() => Read(control, missing));
                var invalidValues = field == "mapping_schema"
                    ? new JsonNode?[] { null, JsonValue.Create("3"), JsonValue.Create(2),
                        JsonValue.Create(3), JsonValue.Create(4.5), JsonValue.Create(true),
                        JsonValue.Create(long.MaxValue), new JsonObject(), new JsonArray() }
                    : new JsonNode?[] { null, JsonValue.Create(3), JsonValue.Create(true),
                        JsonValue.Create("unknown"), JsonValue.Create("DS5"),
                        JsonValue.Create(field == "profile" ? Other(profile) : Other(output)),
                        new JsonObject(), new JsonArray() };
                foreach (var value in invalidValues)
                {
                    var wrong = reply.DeepClone().AsObject();
                    wrong[field] = value;
                    Reject(() => Read(control, wrong));
                }
            }
            var schemaFloat = reply.ToJsonString().Replace("\"mapping_schema\":4", "\"mapping_schema\":4.0");
            Reject(() => BridgeButtonMapping.ReadReply(control, Element(schemaFloat)));
            var legacy = Reply(profile, output);
            legacy.Remove("output");
            legacy.Remove("mapping_schema");
            Reject(() => Read(control, legacy), "mapping_schema 4");
            var rejected = Reply(profile, output);
            rejected["ok"] = false;
            Reject(() => Read(control, rejected));
            Reject(() => BridgeButtonMapping.ReadReply(control, Element("[]")));
            Reject(() => BridgeButtonMapping.ReadReply(control, Element("null")));

            foreach (var format in new[] { "object", "positional", "named" })
            {
                var formatted = Reply(profile, output);
                formatted["entries"] = Entries(format);
                Equal(25, Read(control, formatted).Count);
            }
            var partial = Reply(profile, output);
            partial["entries"]!.AsObject().Remove("c");
            Reject(() => Read(control, partial), "不完整");
            var unknown = Reply(profile, output);
            unknown["entries"]!["south"] = "not-a-control";
            Reject(() => Read(control, unknown));
            var unknownTarget = Reply(profile, output);
            unknownTarget["entries"]!.AsObject().Remove("c");
            unknownTarget["entries"]!["made-up"] = "south";
            Reject(() => Read(control, unknownTarget));
            var malformed = Reply(profile, output);
            malformed["entries"] = true;
            Reject(() => Read(control, malformed));
            var shortArray = Reply(profile, output);
            shortArray["entries"] = new JsonArray("south");
            Reject(() => Read(control, shortArray));
            var duplicateNamed = Reply(profile, output);
            var named = Entries("named").AsArray();
            named.Add(named[0]!.DeepClone());
            duplicateNamed["entries"] = named;
            Reject(() => Read(control, duplicateNamed));
            var duplicateObject = Reply(profile, output).ToJsonString().Replace(
                "\"south\":\"south\"", "\"south\":\"south\",\"south\":\"east\"");
            Reject(() => BridgeButtonMapping.ReadReply(control, Element(duplicateObject)));

            // Mutation acknowledgments can carry identity without a complete entries table.
            var acknowledgment = Reply(profile, output);
            acknowledgment.Remove("entries");
            BridgeButtonMapping.ValidateReplyIdentity(control, Element(acknowledgment));
            Reject(() => Read(control, acknowledgment));
            acknowledgment["output"] = Other(output);
            Reject(() => BridgeButtonMapping.ValidateReplyIdentity(control, Element(acknowledgment)));
            acknowledgment["output"] = output;
            acknowledgment["mapping_schema"] = 2;
            Reject(() => BridgeButtonMapping.ValidateReplyIdentity(control, Element(acknowledgment)));
        }
        foreach (var profile in new string?[] { null, "ds5", "ns2pro" })
        foreach (var format in new[] { "object", "positional", "named" })
        {
            var legacy = new JsonObject { ["entries"] = Entries(format) };
            if (profile is not null) legacy["profile"] = profile;
            Equal(25, Read(Control(profile, null), legacy).Count);
        }
        Reject(() => Read(Control(null, "ds5"), Reply("ds5", "ds5")));
        Reject(() => Read(Control("ds5", "invalid"), Reply("ds5", "invalid")));
    }

    public static void Capture()
    {
        var identity = BridgeButtonMapping.ControlIds.ToDictionary(id => id, id => id);
        foreach (var profile in Profiles)
        foreach (var output in Outputs)
        foreach (var input in new[] { BridgePhysicalInput.Unknown, BridgePhysicalInput.DualSense,
                     BridgePhysicalInput.NintendoNs2Pro })
        foreach (var report in new[] { "DS5 USB input", "NS2 USB input", "Xbox USB input", "" })
        {
            var native = profile == output &&
                ((profile == "ds5" && input == BridgePhysicalInput.DualSense && report == "DS5 USB input") ||
                 (profile == "ns2pro" && input == BridgePhysicalInput.NintendoNs2Pro && report == "NS2 USB input"));
            Equal(native, BridgeButtonMapping.CanCapture(profile, output, input, report, identity));
            if (profile == output)
                Equal(native, BridgeButtonMapping.CanCapture(profile, input, report, identity));
        }
        foreach (var profile in Profiles)
        {
            var input = profile == "ds5" ? BridgePhysicalInput.DualSense : BridgePhysicalInput.NintendoNs2Pro;
            var report = profile == "ds5" ? "DS5 USB input" : "NS2 USB input";
            foreach (var source in new[] { "east", "none" })
            {
                var remapped = new Dictionary<string, string>(identity) { ["south"] = source };
                Equal(false, BridgeButtonMapping.CanCapture(profile, profile, input, report, remapped));
            }
            var incomplete = new Dictionary<string, string>(identity);
            incomplete.Remove("c");
            Equal(false, BridgeButtonMapping.CanCapture(profile, profile, input, report, incomplete));
            incomplete["invented"] = "invented";
            Equal(false, BridgeButtonMapping.CanCapture(profile, profile, input, report, incomplete));
            Equal(false, BridgeButtonMapping.CanCapture(profile, input, report, incomplete));
        }
        // Retain compile compatibility for callers passing a null legacy profile.
        Equal(false, BridgeButtonMapping.CanCapture(null, BridgePhysicalInput.Unknown, "", identity));
        Equal(false, BridgeButtonMapping.CanCapture("ds5", null, BridgePhysicalInput.DualSense,
            "DS5 USB input", identity));
    }

    public static void Validation()
    {
        foreach (var profile in Profiles)
        foreach (var output in Outputs)
        {
            var fixture = PairManifest(profile, output);
            var loaded = Load(fixture);
            var control = loaded.Pages.Single().Sections.Single().Controls.Single();
            Equal(profile, control.MappingProfile);
            Equal(output, control.MappingOutput);
            foreach (var property in new[] { "mappingProfile", "mappingOutput" })
            {
                var missing = fixture.DeepClone().AsObject();
                Mapping(missing).Remove(property);
                Reject(() => Load(missing), property);
                var invalid = fixture.DeepClone().AsObject();
                Mapping(invalid)[property] = "unknown";
                Reject(() => Load(invalid), property);
                Mapping(invalid)[property] = "";
                Reject(() => Load(invalid), property);
            }
            foreach (var (action, _) in Actions)
            foreach (var placeholder in new[] { "{profile}", "{output}" })
            {
                var missing = fixture.DeepClone().AsObject();
                var operation = missing["operations"]![Mapping(missing)[action]!.GetValue<string>()]!;
                operation["request"] = operation["request"]!.GetValue<string>().Replace(placeholder, "");
                Reject(() => Load(missing), "requires {profile} and {output}");
            }
            foreach (var placeholder in new[] { "{target}", "{source}" })
            {
                var missing = fixture.DeepClone().AsObject();
                var operation = missing["operations"]!["mapping.set"]!;
                operation["request"] = operation["request"]!.GetValue<string>().Replace(placeholder, "");
                Reject(() => Load(missing), "requires {target} and {source}");
            }
            var notMapping = fixture.DeepClone().AsObject();
            Mapping(notMapping)["type"] = "Status";
            foreach (var property in new[] { "mappingProfile", "sourceCatalog", "targetCatalog",
                         "resetAction", "saveAction" })
                Mapping(notMapping).Remove(property);
            Reject(() => Load(notMapping), "MappingEditor-only");
        }
        foreach (var profile in new string?[] { null, "ds5", "ns2pro" })
        {
            var legacy = PairManifest("ds5", "ds5");
            Mapping(legacy).Remove("mappingOutput");
            if (profile is null) Mapping(legacy).Remove("mappingProfile");
            else Mapping(legacy)["mappingProfile"] = profile;
            foreach (var operation in legacy["operations"]!.AsObject())
            {
                var request = operation.Value!["request"]!.GetValue<string>().Replace(" {output}", "");
                operation.Value["request"] = profile is null ? request.Replace(" {profile}", "") : request;
            }
            Equal(null, Load(legacy).Pages[0].Sections[0].Controls[0].MappingOutput);
        }
    }

    public static void Manifest()
    {
        var root = FindRepository();
        var path = Path.Combine(root, "modules", "sf32-unified", "sf32-unified.bridge-module.json");
        var module = BridgeModulePackageLoader.LoadFile(path);
        Equal("0.7.0-dev", module.ModuleVersion);
        Equal("0.7.0-dev", module.Firmware.Single().Version);
        var pages = module.Pages.Where(page => page.Sections.SelectMany(section => section.Controls)
            .Any(control => control.Type == BridgeModuleControlType.MappingEditor)).ToArray();
        var expected = new[] { ("ps-mapping", "ds5", "ds5"), ("ps-ns-mapping", "ds5", "ns2pro"),
            ("ns-ps-mapping", "ns2pro", "ds5"), ("ps-xbox-mapping", "ds5", "xbox"),
            ("ns-xbox-mapping", "ns2pro", "xbox"), ("ns-mapping", "ns2pro", "ns2pro") };
        Equal(expected.Length, pages.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            var (id, profile, output) = expected[i];
            Equal(id, pages[i].Id);
            var control = pages[i].Sections.SelectMany(section => section.Controls).Single();
            Equal(profile, control.MappingProfile);
            Equal(output, control.MappingOutput);
            Equal(25, Read(control, Reply(profile, output)).Count);
            foreach (var (_, otherProfile, otherOutput) in expected)
            {
                if (otherProfile != profile || otherOutput != output)
                    Reject(() => Read(control, Reply(otherProfile, otherOutput)));
            }
            var actionIds = new[] { control.ReadAction!, control.ApplyAction!,
                control.ResetAction!, control.SaveAction! };
            for (var action = 0; action < Actions.Length; action++)
            {
                var verb = Actions[action].Verb;
                Equal($"mapping {verb} {profile} {output}" + (verb == "set" ? " south east" : ""),
                    BridgeModuleOperationEngine.BuildRequest(module.Operations[actionIds[action]],
                        BridgeButtonMapping.Parameters(control, "south", "east")));
            }
        }
        var schemaPath = Path.Combine(root, "schemas", "module-v2.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var definition = schema.RootElement.GetProperty("$defs").GetProperty("control");
        Equal("ds5,ns2pro,xbox", string.Join(',', definition.GetProperty("properties")
            .GetProperty("mappingOutput").GetProperty("enum").EnumerateArray().Select(item => item.GetString())));
        var rules = definition.GetProperty("allOf").EnumerateArray().ToArray();
        var outputRule = rules.Single(rule => rule.GetProperty("if").GetProperty("required")
            .EnumerateArray().Any(item => item.GetString() == "mappingOutput"));
        Equal("mappingProfile", outputRule.GetProperty("then").GetProperty("required")[0].GetString());
        var forbidden = rules[0].GetProperty("else").GetProperty("not").GetProperty("anyOf");
        Equal(true, forbidden.EnumerateArray().Any(rule =>
            rule.GetProperty("required")[0].GetString() == "mappingOutput"));
    }

    private static BridgeModuleControlDefinition Control(string? profile, string? output) =>
        new() { MappingProfile = profile, MappingOutput = output, Binding = "/entries" };
    private static string Other(string profile) => profile == "ds5" ? "ns2pro" : "ds5";
    private static JsonElement Element(JsonNode node) => Element(node.ToJsonString());
    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    private static Dictionary<string, string> Read(BridgeModuleControlDefinition control, JsonNode reply) =>
        BridgeButtonMapping.ReadReply(control, Element(reply));
    private static JsonObject Reply(string profile, string output) => new()
    {
        ["ok"] = true, ["profile"] = profile, ["output"] = output,
        ["mapping_schema"] = BridgeButtonMapping.PairMappingSchema, ["entries"] = Entries("object")
    };
    private static JsonNode Entries(string format) => format switch
    {
        "object" => JsonSerializer.SerializeToNode(BridgeButtonMapping.ControlIds.ToDictionary(id => id, id => id))!,
        "positional" => JsonSerializer.SerializeToNode(BridgeButtonMapping.ControlIds)!,
        "named" => JsonSerializer.SerializeToNode(BridgeButtonMapping.ControlIds.Select(id =>
            new { target = id, source = id }))!,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
    private static JsonObject Mapping(JsonObject manifest) =>
        manifest["pages"]![0]!["sections"]![0]!["controls"]![0]!.AsObject();
    private static JsonObject PairManifest(string profile, string output)
    {
        var operations = new JsonObject();
        foreach (var (_, verb) in Actions)
        {
            operations["mapping." + verb] = new JsonObject
            {
                ["transport"] = "ManagerCommand",
                ["request"] = $"mapping {verb} {{profile}} {{output}}" +
                    (verb == "set" ? " {target} {source}" : "")
            };
        }
        var manifest = JsonNode.Parse("""
            {
              "schemaVersion":1,"runtimeApiVersion":2,"id":"pair-test","moduleVersion":"1.0.0",
              "displayName":"Pair test","boardFamily":"Test","description":"Test","matchAny":[],
              "operations":{},"pages":[{"id":"pair","label":"Pair","sections":[
                {"id":"mapping","label":"Mapping","controls":[{
                  "id":"buttons","type":"MappingEditor","label":"Buttons","binding":"/entries",
                  "sourceCatalog":"controller.inputControls","targetCatalog":"bridge.outputControls",
                  "readAction":"mapping.get","applyAction":"mapping.set",
                  "resetAction":"mapping.reset","saveAction":"mapping.save"
                }]}
              ]}]
            }
            """)!.AsObject();
        manifest["operations"] = operations;
        Mapping(manifest)["mappingProfile"] = profile;
        Mapping(manifest)["mappingOutput"] = output;
        return manifest;
    }
    private static IBridgeFirmwareModule Load(JsonNode manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "bridge-mapping-pair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "module.json");
            File.WriteAllText(path, manifest.ToJsonString());
            return BridgeModulePackageLoader.LoadFile(path);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static string FindRepository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "schemas", "module-v2.schema.json")) &&
                Directory.Exists(Path.Combine(dir.FullName, "modules", "sf32-unified")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Cannot locate manager schema and SF32 manifest.");
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
    private static void Reject(Action action, string? message = null)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            if (message is not null && !ex.Message.Contains(message, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Expected rejection containing '{message}', got '{ex.Message}'.", ex);
            return;
        }
        throw new InvalidOperationException("Expected mapping rejection.");
    }
}

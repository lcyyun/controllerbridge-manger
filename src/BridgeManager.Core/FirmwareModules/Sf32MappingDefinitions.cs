namespace BridgeManager.Core.FirmwareModules;

// SF32 stores six input/output tables. Edge uses the DS5 output table.
internal static class Sf32MappingDefinitions
{
    public static readonly IReadOnlyDictionary<string, BridgeModuleOperationDefinition> Operations =
        new Dictionary<string, BridgeModuleOperationDefinition>
        {
            ["mapping.read"] = Command("mapping get {profile} {output}"),
            ["mapping.set"] = Command("mapping set {profile} {output} {target} {source}"),
            ["mapping.reset"] = Command("mapping reset {profile} {output}"),
            ["mapping.save"] = Command("mapping save {profile} {output}")
        };

    public static readonly IReadOnlyList<BridgeModulePageDefinition> Pages =
        (from profile in new[] { "ds5", "ns2pro" }
         from output in new[] { "ds5", "ns2pro", "xbox" }
         select Page(profile, output)).ToArray();

    private static BridgeModuleOperationDefinition Command(string request) => new()
    {
        Transport = BridgeModuleOperationTransport.ManagerCommand,
        Request = request
    };

    private static string Label(string value) => value switch
    {
        "ds5" => "DualSense / Edge", "ns2pro" => "NS2Pro", "xbox" => "XInput", _ => value
    };

    private static BridgeModulePageDefinition Page(string profile, string output) => new()
    {
        Id = $"sf32-{profile}-{output}-mapping",
        Label = $"{Label(profile)} → {Label(output)}",
        Description = "按物理输入和 USB 输出分别保存；DualSense Edge 共用 DualSense 输出映射。",
        Icon = "Game",
        Sections =
        [
            new()
            {
                Id = $"{profile}-{output}-buttons", Label = "按键映射",
                Controls =
                [
                    new()
                    {
                        Id = $"sf32-{profile}-{output}-button-map",
                        Type = BridgeModuleControlType.MappingEditor,
                        Label = "输出按键的物理来源", Binding = "/entries",
                        MappingProfile = profile, MappingOutput = output,
                        SourceCatalog = "controller.inputControls", TargetCatalog = "bridge.outputControls",
                        ReadAction = "mapping.read", ApplyAction = "mapping.set",
                        ResetAction = "mapping.reset", SaveAction = "mapping.save"
                    }
                ]
            }
        ]
    };
}

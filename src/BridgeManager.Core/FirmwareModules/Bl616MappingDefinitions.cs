namespace BridgeManager.Core.FirmwareModules;

// BL616 stores one table per physical input, independent of the emulated USB role.
internal static class Bl616MappingDefinitions
{
    public static readonly IReadOnlyDictionary<string, BridgeModuleOperationDefinition> Operations =
        new Dictionary<string, BridgeModuleOperationDefinition>
        {
            ["mapping.read"] = Command("mapping get {profile}"),
            ["mapping.set"] = Command("mapping set {profile} {target} {source}"),
            ["mapping.reset"] = Command("mapping reset {profile}"),
            ["mapping.save"] = Command("mapping save {profile}")
        };

    public static readonly IReadOnlyList<BridgeModulePageDefinition> Pages =
    [
        Page("ds5", "DualSense / DualSense Edge"),
        Page("ns2pro", "Nintendo NS2Pro")
    ];

    private static BridgeModuleOperationDefinition Command(string request) => new()
    {
        Transport = BridgeModuleOperationTransport.ManagerCommand,
        Request = request
    };

    private static BridgeModulePageDefinition Page(string profile, string label) => new()
    {
        Id = $"bl616-{profile}-mapping",
        Label = $"{label} 映射",
        Description = "按输入手柄保存，适用于该手柄的所有 USB 输出身份。",
        Icon = "Game",
        Sections =
        [
            new()
            {
                Id = $"{profile}-buttons", Label = "按键映射",
                Controls =
                [
                    new()
                    {
                        Id = $"bl616-{profile}-button-map",
                        Type = BridgeModuleControlType.MappingEditor,
                        Label = $"{label} 按键来源",
                        Binding = "/entries", MappingProfile = profile,
                        SourceCatalog = "controller.inputControls",
                        TargetCatalog = "bridge.outputControls",
                        ReadAction = "mapping.read", ApplyAction = "mapping.set",
                        ResetAction = "mapping.reset", SaveAction = "mapping.save"
                    }
                ]
            }
        ]
    };
}

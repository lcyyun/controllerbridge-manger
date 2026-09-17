using System.Text.Json;
using System.Text.Json.Serialization;

namespace BridgeManager.Core.FirmwareModules;

public enum BridgeModuleControlType
{
    Unknown,
    Toggle,
    Number,
    Slider,
    Select,
    Text,
    Color,
    ActionButton,
    Status,
    Table,
    MappingEditor,
    Group,
    Tabs
}

public enum BridgeModuleOperationTransport
{
    Unknown,
    ManagerCommand
}

public enum BridgeModuleConditionOperator
{
    Equals,
    NotEquals
}

public sealed class BridgeModulePageDefinition
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public BridgeModuleSectionDefinition[] Sections { get; init; } = [];
}

public sealed class BridgeModuleSectionDefinition
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string? Description { get; init; }
    public BridgeModuleControlDefinition[] Controls { get; init; } = [];
}

public sealed class BridgeModuleControlDefinition
{
    public string Id { get; init; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BridgeModuleControlType Type { get; init; }
    public string Label { get; init; } = "";
    public string? Description { get; init; }
    public string? Binding { get; init; }
    public JsonElement? Default { get; init; }
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public decimal? Step { get; init; }
    public string? Unit { get; init; }
    public BridgeModuleControlOptionDefinition[] Options { get; init; } = [];
    public string? ReadAction { get; init; }
    public string? ApplyAction { get; init; }
    public BridgeModuleVisibleWhenDefinition? VisibleWhen { get; init; }

    // MappingEditor uses catalogs supplied by the host or a future catalog provider.
    public string? MappingProfile { get; init; }
    // Physical source + USB output selects an independent schema-3 mapping.
    // Omit MappingOutput only for legacy global/per-source definitions.
    public string? MappingOutput { get; init; }
    public string? SourceCatalog { get; init; }
    public string? TargetCatalog { get; init; }
    public string? ResetAction { get; init; }
    public string? SaveAction { get; init; }
}

public sealed class BridgeModuleControlOptionDefinition
{
    public string Value { get; init; } = "";
    public string Label { get; init; } = "";
    public string? Description { get; init; }
}

public sealed class BridgeModuleVisibleWhenDefinition
{
    public string Binding { get; init; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BridgeModuleConditionOperator Operator { get; init; }
    public JsonElement? Value { get; init; }
}

public sealed class BridgeModuleOperationDefinition
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BridgeModuleOperationTransport Transport { get; init; }
    public string Request { get; init; } = "";
    public BridgeModuleOperationResponseDefinition? Response { get; init; }
}

public sealed class BridgeModuleOperationResponseDefinition
{
    public string Select { get; init; } = "";
}

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace BridgeManager.Core.FirmwareModules;

internal static class BridgeDynamicModuleValidator
{
    public static void Validate(BridgeModulePackageDefinition definition)
    {
        if (definition.Pages is null || definition.Operations is null)
        {
            Invalid("Runtime API 2 collections must not be null.");
        }

        if (definition.RuntimeApiVersion < 2)
        {
            if (definition.Pages.Length > 0 || definition.Operations.Count > 0)
            {
                Invalid("Dynamic pages and operations require runtimeApiVersion 2.");
            }
            return;
        }

        ValidateOperations(definition.Operations);
        ValidatePages(definition.Pages, definition.Operations);
    }

    private static void ValidateOperations(
        IReadOnlyDictionary<string, BridgeModuleOperationDefinition> operations)
    {
        var operationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, operation) in operations)
        {
            RequireId(id, "Operation id");
            if (!operationIds.Add(id))
            {
                Invalid($"Operation id '{id}' is duplicated.");
            }
            if (operation is null)
            {
                Invalid($"Operation '{id}' must not be null.");
            }
            if (operation.Transport != BridgeModuleOperationTransport.ManagerCommand)
            {
                Invalid($"Operation '{id}' must use the managerCommand transport.");
            }
            if (string.IsNullOrWhiteSpace(operation.Request))
            {
                Invalid($"Operation '{id}' requires a request.");
            }
            if (operation.Response is not null)
            {
                RequireJsonPointer(operation.Response.Select,
                    $"Operation '{id}' response select");
            }
        }
    }

    private static void ValidatePages(
        IReadOnlyList<BridgeModulePageDefinition> pages,
        IReadOnlyDictionary<string, BridgeModuleOperationDefinition> operations)
    {
        var pageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var controlIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            if (page is null)
            {
                Invalid("Page definitions must not be null.");
            }
            RequireId(page.Id, "Page id");
            RequireText(page.Label, $"Page '{page.Id}' label");
            if (!pageIds.Add(page.Id))
            {
                Invalid($"Page id '{page.Id}' is duplicated.");
            }
            if (page.Sections is null)
            {
                Invalid($"Page '{page.Id}' sections must not be null.");
            }

            var sectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in page.Sections)
            {
                if (section is null)
                {
                    Invalid($"Page '{page.Id}' contains a null section.");
                }
                RequireId(section.Id, $"Page '{page.Id}' section id");
                RequireText(section.Label,
                    $"Section '{page.Id}/{section.Id}' label");
                if (!sectionIds.Add(section.Id))
                {
                    Invalid($"Section id '{page.Id}/{section.Id}' is duplicated.");
                }
                if (section.Controls is null)
                {
                    Invalid($"Section '{page.Id}/{section.Id}' controls must not be null.");
                }

                foreach (var control in section.Controls)
                {
                    ValidateControl(page.Id, section.Id, control, controlIds,
                        operations);
                }
            }
        }
    }

    private static void ValidateControl(
        string pageId,
        string sectionId,
        BridgeModuleControlDefinition? control,
        ISet<string> controlIds,
        IReadOnlyDictionary<string, BridgeModuleOperationDefinition> operations)
    {
        var location = $"{pageId}/{sectionId}";
        if (control is null)
        {
            Invalid($"Section '{location}' contains a null control.");
        }
        RequireId(control.Id, $"Section '{location}' control id");
        RequireText(control.Label, $"Control '{control.Id}' label");
        if (!controlIds.Add(control.Id))
        {
            Invalid($"Control id '{control.Id}' is duplicated.");
        }
        if (control.Type == BridgeModuleControlType.Unknown ||
            !Enum.IsDefined(control.Type))
        {
            Invalid($"Control '{control.Id}' has an unsupported type.");
        }
        if (control.Options is null)
        {
            Invalid($"Control '{control.Id}' options must not be null.");
        }

        var bindsValue = control.Type is BridgeModuleControlType.Toggle or
            BridgeModuleControlType.Number or BridgeModuleControlType.Slider or
            BridgeModuleControlType.Select or BridgeModuleControlType.Text or
            BridgeModuleControlType.Color or BridgeModuleControlType.MappingEditor;
        if (bindsValue)
        {
            RequireJsonPointer(control.Binding, $"Control '{control.Id}' binding");
        }
        else if (!string.IsNullOrWhiteSpace(control.Binding))
        {
            RequireJsonPointer(control.Binding, $"Control '{control.Id}' binding");
        }

        ValidateNumericFields(control);
        ValidateOptions(control);
        ValidateDefault(control);
        ValidateActionReference(control.Id, "readAction", control.ReadAction,
            operations);
        ValidateActionReference(control.Id, "applyAction", control.ApplyAction,
            operations);

        if (control.Type == BridgeModuleControlType.ActionButton &&
            string.IsNullOrWhiteSpace(control.ApplyAction))
        {
            Invalid($"ActionButton control '{control.Id}' requires applyAction.");
        }

        if (control.VisibleWhen is not null)
        {
            RequireJsonPointer(control.VisibleWhen.Binding,
                $"Control '{control.Id}' visibleWhen binding");
            if (!Enum.IsDefined(control.VisibleWhen.Operator))
            {
                Invalid($"Control '{control.Id}' visibleWhen operator is unsupported.");
            }
            if (control.VisibleWhen.Value is null ||
                control.VisibleWhen.Value.Value.ValueKind == JsonValueKind.Undefined)
            {
                Invalid($"Control '{control.Id}' visibleWhen requires a value.");
            }
        }

        if (control.Type == BridgeModuleControlType.MappingEditor)
        {
            if (control.MappingProfile is not null &&
                control.MappingProfile is not ("ds5" or "ns2pro"))
            {
                Invalid($"MappingEditor '{control.Id}' mappingProfile must be 'ds5' or 'ns2pro'.");
            }
            RequireId(control.SourceCatalog,
                $"MappingEditor '{control.Id}' sourceCatalog");
            RequireId(control.TargetCatalog,
                $"MappingEditor '{control.Id}' targetCatalog");
            RequireAction(control.Id, "readAction", control.ReadAction, operations);
            RequireAction(control.Id, "applyAction", control.ApplyAction, operations);
            RequireAction(control.Id, "resetAction", control.ResetAction, operations);
            RequireAction(control.Id, "saveAction", control.SaveAction, operations);
        }
        else if (!string.IsNullOrWhiteSpace(control.SourceCatalog) ||
                 !string.IsNullOrWhiteSpace(control.TargetCatalog) ||
                 control.MappingProfile is not null ||
                 !string.IsNullOrWhiteSpace(control.ResetAction) ||
                 !string.IsNullOrWhiteSpace(control.SaveAction))
        {
            Invalid($"Control '{control.Id}' uses MappingEditor-only properties.");
        }
    }

    private static void ValidateNumericFields(BridgeModuleControlDefinition control)
    {
        var numeric = control.Type is BridgeModuleControlType.Number or
            BridgeModuleControlType.Slider;
        if (!numeric && (control.Min.HasValue || control.Max.HasValue ||
                         control.Step.HasValue))
        {
            Invalid($"Control '{control.Id}' uses numeric bounds with a non-numeric type.");
        }
        if (!numeric)
        {
            return;
        }
        if (control.Min.HasValue && control.Max.HasValue &&
            control.Min.Value > control.Max.Value)
        {
            Invalid($"Control '{control.Id}' min must not exceed max.");
        }
        if (control.Step.HasValue && control.Step.Value <= 0)
        {
            Invalid($"Control '{control.Id}' step must be greater than zero.");
        }
        if (control.Default is { ValueKind: not JsonValueKind.Number })
        {
            Invalid($"Control '{control.Id}' default must be numeric.");
        }
        if (control.Default is { } defaultValue &&
            defaultValue.TryGetDecimal(out var number) &&
            ((control.Min.HasValue && number < control.Min.Value) ||
             (control.Max.HasValue && number > control.Max.Value)))
        {
            Invalid($"Control '{control.Id}' default is outside its numeric range.");
        }
    }

    private static void ValidateOptions(BridgeModuleControlDefinition control)
    {
        if (control.Type != BridgeModuleControlType.Select)
        {
            if (control.Options.Length > 0)
            {
                Invalid($"Control '{control.Id}' options require type Select.");
            }
            return;
        }
        if (control.Options.Length == 0)
        {
            Invalid($"Select control '{control.Id}' requires at least one option.");
        }
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in control.Options)
        {
            if (option is null)
            {
                Invalid($"Select control '{control.Id}' contains a null option.");
            }
            RequireText(option.Value, $"Select control '{control.Id}' option value");
            RequireText(option.Label, $"Select control '{control.Id}' option label");
            if (!values.Add(option.Value))
            {
                Invalid($"Select control '{control.Id}' has duplicate option '{option.Value}'.");
            }
        }
    }

    private static void ValidateDefault(BridgeModuleControlDefinition control)
    {
        if (control.Default is not { } value)
        {
            return;
        }

        var valid = control.Type switch
        {
            BridgeModuleControlType.Toggle =>
                value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            BridgeModuleControlType.Number or BridgeModuleControlType.Slider =>
                value.ValueKind == JsonValueKind.Number,
            BridgeModuleControlType.Select or BridgeModuleControlType.Text or
                BridgeModuleControlType.Color =>
                value.ValueKind == JsonValueKind.String,
            BridgeModuleControlType.MappingEditor =>
                value.ValueKind is JsonValueKind.Array or JsonValueKind.Object,
            _ => false
        };
        if (!valid)
        {
            Invalid($"Control '{control.Id}' default does not match its type.");
        }

        if (control.Type == BridgeModuleControlType.Select &&
            !control.Options.Any(option => string.Equals(option.Value,
                value.GetString(), StringComparison.OrdinalIgnoreCase)))
        {
            Invalid($"Select control '{control.Id}' default is not one of its options.");
        }
    }

    private static void RequireAction(
        string controlId,
        string property,
        string? actionId,
        IReadOnlyDictionary<string, BridgeModuleOperationDefinition> operations)
    {
        if (string.IsNullOrWhiteSpace(actionId))
        {
            Invalid($"MappingEditor '{controlId}' requires {property}.");
        }
        ValidateActionReference(controlId, property, actionId, operations);
    }

    private static void ValidateActionReference(
        string controlId,
        string property,
        string? actionId,
        IReadOnlyDictionary<string, BridgeModuleOperationDefinition> operations)
    {
        if (string.IsNullOrWhiteSpace(actionId))
        {
            return;
        }
        if (!operations.ContainsKey(actionId))
        {
            Invalid($"Control '{controlId}' {property} references unknown operation '{actionId}'.");
        }
    }

    private static void RequireJsonPointer(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] != '/')
        {
            Invalid($"{name} must be an absolute JSON Pointer starting with '/'.");
        }
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '~')
            {
                continue;
            }
            if (++index >= value.Length || value[index] is not ('0' or '1'))
            {
                Invalid($"{name} contains an invalid JSON Pointer escape.");
            }
        }
    }

    private static void RequireId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(ch =>
                !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            Invalid($"{name} is invalid.");
        }
    }

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Invalid($"{name} is required.");
        }
    }

    [DoesNotReturn]
    private static void Invalid(string message) =>
        throw new InvalidDataException(message);
}

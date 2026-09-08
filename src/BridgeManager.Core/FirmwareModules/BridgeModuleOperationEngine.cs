using System.Text.Json;

namespace BridgeManager.Core.FirmwareModules;

public static class BridgeModuleOperationEngine
{
    public static string BuildRequest(
        BridgeModuleOperationDefinition operation,
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(parameters);

        var request = operation.Request;
        foreach (var (name, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(name) || value.Length > 128 ||
                value.Any(character =>
                    character is '\r' or '\n' or '\0' or '{' or '}'))
            {
                throw new InvalidDataException(
                    $"Operation parameter '{name}' contains unsafe characters.");
            }
            request = request.Replace($"{{{name}}}", value,
                StringComparison.Ordinal);
        }
        if (request.Contains('{') || request.Contains('}'))
        {
            throw new InvalidDataException(
                "Operation request has unresolved template parameters.");
        }
        return request;
    }

    public static JsonElement SelectSuccessfulResponse(
        string operationId,
        BridgeModuleOperationDefinition operation,
        JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("ok", out var ok) &&
            ok.ValueKind == JsonValueKind.False)
        {
            var detail = response.TryGetProperty("error", out var error) &&
                         error.ValueKind == JsonValueKind.String
                ? error.GetString() : "device_rejected_operation";
            throw new InvalidOperationException(
                $"Device rejected operation {operationId}: {detail}");
        }

        var pointer = operation.Response?.Select;
        if (string.IsNullOrWhiteSpace(pointer)) return response;
        if (!TrySelectJsonPointer(response, pointer, out var selected))
        {
            throw new InvalidDataException(
                $"Operation {operationId} response does not contain {pointer}.");
        }
        return selected;
    }

    public static bool TrySelectJsonPointer(JsonElement root, string pointer,
                                            out JsonElement value)
    {
        value = root;
        if (pointer.Length == 0) return true;
        if (pointer[0] != '/') return false;

        foreach (var escaped in pointer.Split('/', StringSplitOptions.None)
                     .Skip(1))
        {
            var token = escaped.Replace("~1", "/").Replace("~0", "~");
            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty(token, out var property))
            {
                value = property;
                continue;
            }
            if (value.ValueKind == JsonValueKind.Array &&
                int.TryParse(token, out var index) && index >= 0 &&
                index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }
            value = default;
            return false;
        }
        return true;
    }
}

using System.Globalization;
using System.Text.Json;

namespace LoadingAI.Tools;

/// <summary>
/// Validates tool-call JSON against the JSON Schema subset used by tool definitions.
/// Unknown object properties are rejected when a schema declares properties so a
/// malformed write cannot silently fall back to different defaults.
/// </summary>
public static class ToolArgumentValidator
{
    public static bool Validate(
        object? schema,
        string argsJson,
        out string normalizedJson,
        out string error)
    {
        normalizedJson = "{}";
        error = "";

        JsonElement args;
        try
        {
            if (string.IsNullOrWhiteSpace(argsJson))
                throw new JsonException("arguments are empty");
            using var argsDoc = JsonDocument.Parse(argsJson);
            if (argsDoc.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("arguments are not an object");
            args = argsDoc.RootElement.Clone();
        }
        catch
        {
            error = "Invalid JSON: expected an argument object.";
            return false;
        }

        if (!TryGetSchemaElement(schema, out var schemaElement, out error))
            return false;

        if (!ValidateValue(args, schemaElement, "", out error))
            return false;

        normalizedJson = JsonSerializer.Serialize(args);
        return true;
    }

    private static bool TryGetSchemaElement(object? schema, out JsonElement element, out string error)
    {
        error = "";
        switch (schema)
        {
            case null:
                element = default;
                error = "Invalid tool schema: schema is missing.";
                return false;
            case JsonElement jsonElement:
                element = jsonElement;
                break;
            default:
                try
                {
                    element = JsonSerializer.SerializeToElement(schema);
                }
                catch (Exception ex)
                {
                    element = default;
                    error = $"Invalid tool schema: {ex.Message}";
                    return false;
                }
                break;
        }

        if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
            return true;

        if (element.ValueKind == JsonValueKind.Object)
        {
            var hasType = element.TryGetProperty("type", out _);
            var hasShape = element.TryGetProperty("properties", out _)
                || element.TryGetProperty("required", out _)
                || element.TryGetProperty("items", out _);
            if (hasType || hasShape)
                return true;
        }

        // Empty or shapeless schemas accept the root argument object.
        element = JsonSerializer.SerializeToElement(new { type = "object" });
        return true;
    }

    private static bool ValidateValue(JsonElement value, JsonElement schema, string path, out string error)
    {
        error = "";
        if (schema.ValueKind != JsonValueKind.Object)
            return true;

        var expectedTypes = ReadTypes(schema);
        foreach (var expected in expectedTypes)
        {
            if (!MatchesType(value, expected))
            {
                error = $"{PathLabel(path)}: expected {expected}.";
                return false;
            }
        }

        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array)
        {
            var allowed = enumValues.EnumerateArray().ToList();
            if (!allowed.Any(candidate => JsonEquals(candidate, value)))
            {
                var names = string.Join(", ", allowed.Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()));
                error = $"{PathLabel(path)}: must be one of {names}.";
                return false;
            }
        }

        if (value.ValueKind == JsonValueKind.Number
            && (schema.TryGetProperty("minimum", out var minimum) || schema.TryGetProperty("maximum", out var maximum)))
        {
            if (!TryGetDecimal(value, out var number))
            {
                error = $"{PathLabel(path)}: expected number.";
                return false;
            }

            if (schema.TryGetProperty("minimum", out minimum) && TryGetDecimal(minimum, out var min) && number < min)
            {
                error = $"{PathLabel(path)}: must be >= {Format(min)}.";
                return false;
            }

            if (schema.TryGetProperty("maximum", out maximum) && TryGetDecimal(maximum, out var max) && number > max)
            {
                error = $"{PathLabel(path)}: must be <= {Format(max)}.";
                return false;
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!ValidateObject(value, schema, path, out error))
                return false;
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (!ValidateValue(item, itemSchema, $"{path}[{index}]", out error))
                    return false;
                index++;
            }
        }

        return true;
    }

    private static bool ValidateObject(JsonElement value, JsonElement schema, string path, out string error)
    {
        error = "";
        var properties = schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
            ? props
            : default;
        var hasDeclaredProperties = properties.ValueKind == JsonValueKind.Object;
        var allowAdditional = schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind == JsonValueKind.True;

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                var name = item.GetString();
                if (string.IsNullOrEmpty(name))
                    continue;
                if (!value.TryGetProperty(name!, out _))
                {
                    error = $"{ChildPath(path, name!)}: required property is missing.";
                    return false;
                }
            }
        }

        foreach (var property in value.EnumerateObject())
        {
            if (!hasDeclaredProperties)
                continue;

            if (!properties.TryGetProperty(property.Name, out var propertySchema))
            {
                if (!allowAdditional)
                {
                    error = $"{ChildPath(path, property.Name)}: unknown property.";
                    return false;
                }
                continue;
            }

            if (!ValidateValue(property.Value, propertySchema, ChildPath(path, property.Name), out error))
                return false;
        }

        return true;
    }

    private static IReadOnlyList<string> ReadTypes(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type))
            return Array.Empty<string>();

        if (type.ValueKind == JsonValueKind.String)
            return new[] { type.GetString() ?? "" };

        if (type.ValueKind == JsonValueKind.Array)
            return type.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString() ?? "")
                .Where(v => v.Length > 0)
                .ToList();

        return Array.Empty<string>();
    }

    private static bool MatchesType(JsonElement value, string expected)
        => expected.ToLowerInvariant() switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number
                && TryGetDecimal(value, out var number)
                && decimal.Truncate(number) == number,
            "number" => value.ValueKind == JsonValueKind.Number,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true
        };

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;
        switch (left.ValueKind)
        {
            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return TryGetDecimal(left, out var a) && TryGetDecimal(right, out var b) && a == b;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            default:
                return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
        }
    }

    private static bool TryGetDecimal(JsonElement value, out decimal number)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out number))
            return true;
        number = 0;
        return false;
    }

    private static string Format(decimal value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static string ChildPath(string path, string name)
        => string.IsNullOrEmpty(path) ? name : $"{path}.{name}";

    private static string PathLabel(string path)
        => string.IsNullOrEmpty(path) ? "arguments" : path;
}

using System.Text.Json;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class ToolArgumentValidatorTests
{
    private static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void RejectsMalformedJson()
    {
        var schema = Schema("""{"type":"object","properties":{"hostIds":{"type":"array","items":{"type":"string"}}}}""");
        var ok = ToolArgumentValidator.Validate(schema, "{bad", out _, out var error);
        Assert.False(ok);
        Assert.Contains("Invalid JSON", error);
    }

    [Fact]
    public void RejectsWrongArrayType()
    {
        var schema = Schema("""{"type":"object","properties":{"hostIds":{"type":"array","items":{"type":"string"}}}}""");
        var ok = ToolArgumentValidator.Validate(schema, """{"hostIds":"123"}""", out _, out var error);
        Assert.False(ok);
        Assert.Equal("hostIds: expected array.", error);
    }

    [Fact]
    public void AcceptsValidArgumentsAndReturnsCanonicalJson()
    {
        var schema = Schema("""{"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":200},"shape":{"type":"string","enum":["rectangle","circle","auto"]},"dryRun":{"type":"boolean"}},"required":["limit"]}""");
        var ok = ToolArgumentValidator.Validate(schema, """{"limit":50,"shape":"auto","dryRun":true}""", out var json, out var error);
        Assert.True(ok);
        Assert.True(string.IsNullOrEmpty(error));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(50, doc.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact]
    public void ValidatesNestedArrayItems()
    {
        var schema = Schema("""{"type":"object","properties":{"groups":{"type":"array","items":{"type":"array","items":{"type":"string"}}}}}""");
        Assert.True(ToolArgumentValidator.Validate(schema, """{"groups":[["a","b"],[]]}""", out _, out _));

        var ok = ToolArgumentValidator.Validate(schema, """{"groups":[["a",42]]}""", out _, out var error);
        Assert.False(ok);
        Assert.Equal("groups[0][1]: expected string.", error);
    }

    [Theory]
    [InlineData("""{"limit":0}""", "limit: must be >= 1.")]
    [InlineData("""{"limit":201}""", "limit: must be <= 200.")]
    [InlineData("""{"limit":1.5}""", "limit: expected integer.")]
    public void EnforcesIntegerTypeAndBounds(string args, string expected)
    {
        var schema = Schema("""{"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":200}}}""");
        Assert.False(ToolArgumentValidator.Validate(schema, args, out _, out var error));
        Assert.Equal(expected, error);
    }

    [Fact]
    public void RejectsUnknownObjectProperties()
    {
        var schema = Schema("""{"type":"object","properties":{"limit":{"type":"integer"}}}""");
        Assert.False(ToolArgumentValidator.Validate(schema, """{"limit":5,"unexpected":true}""", out _, out var error));
        Assert.Equal("unexpected: unknown property.", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    public void RejectsMissingOrNonObjectArguments(string args)
    {
        var schema = Schema("""{"type":"object","properties":{}}""");
        Assert.False(ToolArgumentValidator.Validate(schema, args, out _, out var error));
        Assert.Equal("Invalid JSON: expected an argument object.", error);
    }

    [Fact]
    public void RequiresDeclaredRequiredProperties()
    {
        var schema = Schema("""{"type":"object","properties":{"hostIds":{"type":"array"}},"required":["hostIds"]}""");
        Assert.False(ToolArgumentValidator.Validate(schema, "{}", out _, out var error));
        Assert.Equal("hostIds: required property is missing.", error);
    }

    [Fact]
    public void RejectsInvalidEnumValue()
    {
        var schema = Schema("""{"type":"object","properties":{"shape":{"type":"string","enum":["rectangle","circle","auto"]}}}""");
        Assert.False(ToolArgumentValidator.Validate(schema, """{"shape":"triangle"}""", out _, out var error));
        Assert.Equal("shape: must be one of rectangle, circle, auto.", error);
    }

    [Fact]
    public void AcceptsAnonymousSchemaObject()
    {
        object schema = new
        {
            type = "object",
            properties = new
            {
                dryRun = new { type = "boolean" },
                names = new { type = "array", items = new { type = "string" } }
            }
        };
        Assert.True(ToolArgumentValidator.Validate(schema, """{"dryRun":false,"names":["a","b"]}""", out _, out var error));
        Assert.True(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void SchemaWithoutPropertiesAllowsArguments()
    {
        Assert.True(ToolArgumentValidator.Validate(new { type = "object" }, """{"anything":1}""", out _, out _));
    }

    [Fact]
    public void RejectsWrongScalarTypes()
    {
        var schema = Schema("""{"type":"object","properties":{"enabled":{"type":"boolean"},"name":{"type":"string"},"count":{"type":"number"}}}""");
        Assert.False(ToolArgumentValidator.Validate(schema, """{"enabled":"true"}""", out _, out var error));
        Assert.Equal("enabled: expected boolean.", error);
        Assert.False(ToolArgumentValidator.Validate(schema, """{"name":1}""", out _, out error));
        Assert.Equal("name: expected string.", error);
        Assert.True(ToolArgumentValidator.Validate(schema, """{"count":2}""", out _, out _));
    }
}

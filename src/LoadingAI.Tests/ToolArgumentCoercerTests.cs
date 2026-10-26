using System.IO;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class ToolArgumentCoercerTests
{
    private static readonly object Schema = new
    {
        type = "object",
        properties = new
        {
            elementIds = new { type = "array", items = new { type = "string" } },
            elementId = new { type = "string" },
            name = new { type = "string" },
            limit = new { type = "integer" },
            ratio = new { type = "number" },
            enabled = new { type = "boolean" },
            scope = new { type = "string", @enum = new[] { "selection", "visible", "all" } },
            categories = new { type = "array", items = new { type = "string", @enum = new[] { "walls", "columns" } } }
        },
        required = new[] { "elementIds" }
    };

    private static string Coerce(string json) => ToolArgumentCoercer.Coerce(Schema, json);

    [Theory]
    [InlineData("""{"elementIds":[123,"456"]}""", """{"elementIds":["123","456"]}""")]
    [InlineData("""{"elementIds":123}""", """{"elementIds":["123"]}""")]
    [InlineData("""{"elementIds":"789"}""", """{"elementIds":["789"]}""")]
    [InlineData("""{"elementIds":["1"],"elementId":42}""", """{"elementIds":["1"],"elementId":"42"}""")]
    [InlineData("""{"elementIds":["1"],"limit":"50"}""", """{"elementIds":["1"],"limit":50}""")]
    [InlineData("""{"elementIds":["1"],"ratio":" 1.5 "}""", """{"elementIds":["1"],"ratio":1.5}""")]
    [InlineData("""{"elementIds":["1"],"enabled":"TRUE"}""", """{"elementIds":["1"],"enabled":true}""")]
    [InlineData("""{"elementIds":["1"],"scope":"Visible"}""", """{"elementIds":["1"],"scope":"visible"}""")]
    [InlineData("""{"elementIds":["1"],"categories":"Walls"}""", """{"elementIds":["1"],"categories":["walls"]}""")]
    [InlineData("""{"elementIds":["1"],"categories":["Walls","columns"]}""", """{"elementIds":["1"],"categories":["walls","columns"]}""")]
    public void FixesWhatCannotChangeMeaning(string input, string expected)
        => Assert.Equal(expected, Coerce(input));

    [Theory]
    [InlineData("""{"elementIds":["1"],"limit":""}""", """{"elementIds":["1"]}""")]
    [InlineData("""{"elementIds":["1"],"enabled":"  "}""", """{"elementIds":["1"]}""")]
    public void DropsABlankOptionalNumberOrBoolean(string input, string expected)
        => Assert.Equal(expected, Coerce(input));

    [Theory]
    [InlineData("""{"elementIds":""}""")]
    [InlineData("""{"elementIds":["1"],"name":"007"}""")]
    [InlineData("""{"elementIds":["1"],"elementId":"123"}""")]
    [InlineData("""{"elementIds":["1"],"limit":"many"}""")]
    [InlineData("""{"elementIds":["1"],"enabled":"maybe"}""")]
    [InlineData("""{"elementIds":["1"],"scope":"nowhere"}""")]
    [InlineData("""{"elementIds":["1"],"unknown":5}""")]
    public void LeavesTheRestToTheValidator(string input)
        => Assert.Equal(input, Coerce(input));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void ReturnsAnythingItCannotParseUnchanged(string input)
        => Assert.Equal(input, Coerce(input));

    [Fact]
    public void IsIdempotent()
    {
        var once = Coerce("""{"elementIds":123,"limit":"5"}""");
        Assert.Equal(once, Coerce(once));
    }

    [Fact]
    public void WhatTheValidatorRejectedNowPasses()
    {
        const string raw = """{"elementIds":[1,2],"limit":"5","categories":"Walls"}""";
        Assert.False(ToolArgumentValidator.Validate(Schema, raw, out _, out _));
        Assert.True(ToolArgumentValidator.Validate(Schema, Coerce(raw), out _, out var error), error);
    }

    [Fact]
    public void Source_ChatSessionCoercesBeforeEveryValidation()
    {
        var src = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));
        var method = src.Substring(src.IndexOf("private async Task<string> ExecuteToolCoreAsync", StringComparison.Ordinal));

        var firstCoerce = method.IndexOf("argsJson = ToolArgumentCoercer.Coerce(", StringComparison.Ordinal);
        var firstValidate = method.IndexOf("ToolArgumentValidator.Validate(", StringComparison.Ordinal);
        Assert.True(firstCoerce >= 0 && firstCoerce < firstValidate);

        // After the review the form values or edited inputs are coerced, then validated again.
        var review = method.IndexOf("_approvals.ReviewAsync", StringComparison.Ordinal);
        var secondCoerce = method.IndexOf("argsJson = ToolArgumentCoercer.Coerce(", review, StringComparison.Ordinal);
        var secondValidate = method.IndexOf("ToolArgumentValidator.Validate(", review, StringComparison.Ordinal);
        Assert.True(review >= 0 && secondCoerce > review && secondCoerce < secondValidate);
    }
}

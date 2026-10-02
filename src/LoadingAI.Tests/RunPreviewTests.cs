using System.Text.Json;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class RunPreviewTests
{
    private static PresetField Field(string id, string label, PresetFieldKind kind = PresetFieldKind.String,
        object? @default = null, IReadOnlyList<string>? options = null, string? warning = null)
        => new() { Id = id, Label = label, Kind = kind, Default = @default, Options = options, Warning = warning };

    [Fact]
    public void BuildPreview_SummaryNamesToolShapeNotJson()
    {
        var fields = new[]
        {
            Field("a", "A"), Field("b", "B"), Field("c", "C")
        };

        var preview = ToolPresets.BuildPreview(fields, null);

        Assert.Equal("3 settings to confirm", preview.Summary);
        Assert.DoesNotContain("{", preview.Summary);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData("yes", true)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void BuildPreview_WarningOnlyWhenTruthy(object? value, bool shouldWarn)
    {
        const string warning = "Existing panels will be deleted first.";
        var fields = new[] { Field("deleteExisting", "Delete existing", PresetFieldKind.Boolean, warning: warning) };
        var args = JsonSerializer.Serialize(new Dictionary<string, object?> { ["deleteExisting"] = value });

        var preview = ToolPresets.BuildPreview(fields, args);

        if (shouldWarn)
            Assert.Equal(new[] { warning }, preview.Warnings);
        else
            Assert.Empty(preview.Warnings);
    }

    [Fact]
    public void BuildPreview_RowPerField_InDeclarationOrder()
    {
        var fields = new[]
        {
            Field("elementIds", "Elements", PresetFieldKind.PickElements),
            Field("thicknessMm", "Sheet thickness (mm)", PresetFieldKind.Number, (int)18),
            Field("collision", "Skip panels that collide", PresetFieldKind.Boolean, true)
        };

        var preview = ToolPresets.BuildPreview(fields, null);

        Assert.Equal(
            new[] { "Elements", "Sheet thickness (mm)", "Skip panels that collide" },
            preview.Rows.Select(r => r.Label));
    }

    [Fact]
    public void BuildPreview_UsesResolvedArgsOverFieldDefault()
    {
        var fields = new[] { Field("thicknessMm", "Sheet thickness (mm)", PresetFieldKind.Number, (int)18) };

        var preview = ToolPresets.BuildPreview(fields, """{"thicknessMm":22}""");

        var detail = preview.Rows.Single(r => r.Label == "Sheet thickness (mm)").Detail;
        Assert.Contains("22", detail);
        Assert.DoesNotContain("18", detail);
    }

    [Fact]
    public void BuildPreview_UnknownValueRendersAsDashNotThrow()
    {
        var fields = new[] { Field("elementIds", "Elements", PresetFieldKind.PickElements) };

        var preview = ToolPresets.BuildPreview(fields, null);

        Assert.Equal("-", preview.Rows.Single().Detail);
    }

    [Fact]
    public void BuildPreview_SelectShowsChosenOption()
    {
        var fields = new[]
        {
            Field("action", "Action", PresetFieldKind.Select, options: new[] { "stats", "clear" })
        };

        var preview = ToolPresets.BuildPreview(fields, """{"action":"clear"}""");

        Assert.Contains("clear", preview.Rows.Single().Detail);
    }

    [Fact]
    public void BuildPreview_BooleanRendersOnOff()
    {
        var fields = new[]
        {
            Field("collision", "Skip panels that collide", PresetFieldKind.Boolean, true)
        };

        Assert.Equal("on", ToolPresets.BuildPreview(fields, null).Rows.Single().Detail);
        Assert.Equal("off", ToolPresets.BuildPreview(fields, """{"collision":false}""").Rows.Single().Detail);
    }

    [Fact]
    public void BuildPreview_WarningIgnoredForBoxedZeroDefault()
    {
        // A field Default is a boxed object, not a JsonElement, so it misses the Number arm.
        // Zero is a safe value: warning on it would tell the user a delete is coming when it is not.
        const string warning = "Existing panels will be deleted first.";
        var fields = new[]
        {
            Field("continueFrom", "Continue from", PresetFieldKind.Number, (int)0, warning: warning)
        };

        Assert.Empty(ToolPresets.BuildPreview(fields, null).Warnings);
    }
}

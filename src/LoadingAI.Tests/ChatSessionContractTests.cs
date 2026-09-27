using System.IO;

namespace LoadingAI.Tests;

public class ChatSessionContractTests
{
    [Fact]
    public void ExecuteTool_ValidatesArgumentsBeforeApprovalOrExecution()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));
        var start = src.IndexOf("public async Task<string> ExecuteToolAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);

        var validation = src.IndexOf("ToolArgumentValidator.Validate(", start, StringComparison.Ordinal);
        var preset = src.IndexOf("if (ToolPresets.HasPresets", start, StringComparison.Ordinal);
        var webApproval = src.IndexOf("AwaitL3WebAsync", start, StringComparison.Ordinal);
        var execute = src.IndexOf("await entry.ExecuteAsync", start, StringComparison.Ordinal);
        var turnGroup = src.IndexOf("EnsureTurnGroupAsync", start, StringComparison.Ordinal);

        Assert.True(validation >= 0);
        Assert.True(validation < preset);
        Assert.True(validation < webApproval);
        Assert.True(validation < execute);
        Assert.True(validation < turnGroup);
    }

    [Fact]
    public void ExecuteTool_RevalidatesArgumentsAfterPresetMerge()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));
        var start = src.IndexOf("public async Task<string> ExecuteToolAsync", StringComparison.Ordinal);
        var merge = src.IndexOf("argsJson = ToolPresets.Merge", start, StringComparison.Ordinal);
        var secondValidation = src.IndexOf("ToolArgumentValidator.Validate(", merge, StringComparison.Ordinal);
        var execution = src.IndexOf("await entry.ExecuteAsync", merge, StringComparison.Ordinal);

        Assert.True(merge >= 0);
        Assert.True(secondValidation > merge);
        Assert.True(secondValidation < execution);
    }

    [Fact]
    public void ExecuteTool_InvalidArgumentsNeverExecuteOrOpenTurnGroup()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));
        var start = src.IndexOf("public async Task<string> ExecuteToolAsync", StringComparison.Ordinal);
        var method = src.Substring(start);
        var validationBlockEnd = method.IndexOf("if (ToolPresets.HasPresets", StringComparison.Ordinal);
        var validationBlock = method.Substring(0, validationBlockEnd);

        Assert.Contains("return $\"Tool argument error: {validationError}\"", validationBlock);
        Assert.DoesNotContain("EnsureTurnGroupAsync", validationBlock);
        Assert.DoesNotContain("entry.ExecuteAsync", validationBlock);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LoadingAI.sln")))
            dir = dir.Parent!;
        return dir!.FullName;
    }
}

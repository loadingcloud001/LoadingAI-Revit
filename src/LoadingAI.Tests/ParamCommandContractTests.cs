using System;
using System.Linq;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class ParamCommandContractTests
{
    [Fact]
    public void IRevitCommand_DeclaresPhase3ParamMethods()
    {
        var names = typeof(IRevitCommand).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("SetParameterAsync", names);
        Assert.Contains("SetParametersAsync", names);
        Assert.DoesNotContain("RunBatchAsync", names);
    }
}

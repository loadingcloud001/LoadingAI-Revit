using System;
using System.Linq;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class L3CommandContractTests
{
    [Fact]
    public void IRevitElementCommand_DeclaresPhase5L3Methods()
    {
        var names = typeof(IRevitElementCommand).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("DeleteElementsAsync", names);
        Assert.Contains("MoveElementsAsync", names);
        Assert.DoesNotContain("IsolateInViewAsync", names);
        Assert.DoesNotContain("SetParameterAsync", names);
    }
}

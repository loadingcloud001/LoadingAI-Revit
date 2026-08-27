using System;
using System.Linq;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class L3CommandContractTests
{
    [Fact]
    public void IRevitCommand_DeclaresPhase5L3Methods()
    {
        var names = typeof(IRevitCommand).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("DeleteElementsAsync", names);
        Assert.Contains("MoveElementsAsync", names);
    }
}

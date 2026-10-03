using LoadingAI.Llm;

namespace LoadingAI.Tests;

public class CheckpointStoreTests
{
    [Theory]
    [InlineData("20261003120000_ab12", true)]
    [InlineData("20261003120000", true)]
    [InlineData("..\\..\\settings", false)]
    [InlineData("../x", false)]
    [InlineData("a*", false)]
    [InlineData("abc\n", false)]
    [InlineData("", false)]
    public void IsValidId(string id, bool ok) => Assert.Equal(ok, CheckpointStore.IsValidId(id));

    [Fact]
    public void Load_RefusesTraversal() => Assert.Null(CheckpointStore.Load("..\\..\\settings"));
}

using RevitAiPoc.Llm;

namespace RevitAiPoc.Tests;

public class StreamToolCallAccumulatorTests
{
    [Fact]
    public void AccumulatesIndexedDeltas()
    {
        var acc = new StreamToolCallAccumulator();
        acc.ApplyChunk("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"get_","arguments":"{"}}]}}]}""");
        acc.ApplyChunk("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"model_summary","arguments":"}"}}]}}]}""");
        var list = acc.ToList();
        Assert.Single(list);
        Assert.Equal("get_model_summary", list[0].Function.Name);
        Assert.Equal("{}", list[0].Function.Arguments);
        Assert.Equal("call_1", list[0].Id);
    }

    [Fact]
    public void AllocatesIndexWhenOmitted()
    {
        var acc = new StreamToolCallAccumulator();
        acc.ApplyChunk("""{"choices":[{"delta":{"tool_calls":[{"id":"a","function":{"name":"one","arguments":"1"}}]}}]}""");
        acc.ApplyChunk("""{"choices":[{"delta":{"tool_calls":[{"id":"b","function":{"name":"two","arguments":"2"}}]}}]}""");
        var list = acc.ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal("one", list[0].Function.Name);
        Assert.Equal("two", list[1].Function.Name);
    }
}

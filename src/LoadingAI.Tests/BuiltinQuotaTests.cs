using System.IO;
using System.Text.Json;
using LoadingAI.Llm;
using Xunit;

namespace LoadingAI.Tests;

[Collection("builtin-provider")]
public class BuiltinQuotaTests
{
    [Theory]
    [InlineData("abcdefgh", true)]
    [InlineData("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6", true)]
    [InlineData("device-id_with-allowed", true)]
    [InlineData("short", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("has space in it", false)]
    [InlineData("has/slash", false)]
    public void IsValidDeviceId_AcceptsOnlyOpaqueTokens(string? id, bool expected)
        => Assert.Equal(expected, BuiltinQuota.IsValidDeviceId(id));

    [Fact]
    public void DeviceId_IsValidAndStableAcrossReads()
    {
        var id = BuiltinQuota.DeviceId;
        Assert.True(BuiltinQuota.IsValidDeviceId(id));
        Assert.Equal(id, BuiltinQuota.DeviceId);

        var path = BuiltinQuota.DeviceFilePath;
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(id, doc.RootElement.GetProperty("id").GetString());
        }
    }

    [Fact]
    public void Ticket_IsHeldInMemoryOnly()
    {
        BuiltinQuota.ClearTicket();
        Assert.Null(BuiltinQuota.Ticket);
        BuiltinQuota.SetTicket("ticket-abc123");
        Assert.Equal("ticket-abc123", BuiltinQuota.Ticket);
        BuiltinQuota.SetTicket("  ");
        Assert.Null(BuiltinQuota.Ticket);

        BuiltinQuota.SetTicket("ticket-abc123");
        var payload = JsonSerializer.Serialize(ProviderSettings.RedactForWeb(ProviderSettings.CreateDefault()));
        Assert.DoesNotContain("ticket-abc123", payload);
        Assert.DoesNotContain(BuiltinQuota.DeviceId, payload);
        BuiltinQuota.ClearTicket();
    }

    [Fact]
    public void LimitMessage_PrefersTheGatewayText()
    {
        var fromGateway = new QuotaStatus { Ok = false, Exhausted = true, Limit = 100, Message = "try later" };
        Assert.Equal("try later", BuiltinQuota.LimitMessage(fromGateway));

        var fallback = BuiltinQuota.LimitMessage(new QuotaStatus { Ok = false, Exhausted = true, Limit = 100 });
        Assert.Contains("100", fallback);
        Assert.Contains(DefaultLlm.DisplayName, fallback);
        Assert.DoesNotContain("minimax", fallback, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Describe_ReportsTheRemainingAllowance()
    {
        var status = new QuotaStatus { Limit = 100, Used = 7, Remaining = 93 };
        Assert.Contains("93", status.Describe());
        Assert.Contains("100", status.Describe());
        Assert.Equal(DefaultLlm.DisplayName, status.Describe().Split('·')[0].Trim());
    }

    [Fact]
    public void QuotaStatus_ReadsTheGatewayPayload()
    {
        const string json = """
            {"limit":100,"used":100,"remaining":0,"resetUtc":"2026-01-02T00:00:00Z",
             "error":{"code":"daily_limit","message":"LoadingAI daily limit reached (100 requests/day)."}}
            """;
        var status = JsonSerializer.Deserialize<QuotaStatus>(json)!;
        Assert.Equal(100, status.Limit);
        Assert.Equal(0, status.Remaining);
        Assert.Equal("daily_limit", status.Error!.Code);
        Assert.Equal("2026-01-02T00:00:00Z", status.ResetUtc);
    }
}

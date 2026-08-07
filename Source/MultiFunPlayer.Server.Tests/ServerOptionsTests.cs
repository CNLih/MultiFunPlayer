using Microsoft.Extensions.Configuration;

namespace MultiFunPlayer.Server.Tests;

public sealed class ServerOptionsTests
{
    [Theory]
    [InlineData(null, 90)]
    [InlineData("14", 14)]
    [InlineData("1", 7)]
    [InlineData("9999", 3650)]
    [InlineData("not-a-number", 90)]
    public void DataProtectionKeyLifetimeIsBounded(string? value, int expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MultiFunPlayer:DataProtectionKeyLifetimeDays"] = value
            })
            .Build();

        Assert.Equal(expected,
            ServerOptions.ResolveDataProtectionKeyLifetimeDays(configuration));
    }
}

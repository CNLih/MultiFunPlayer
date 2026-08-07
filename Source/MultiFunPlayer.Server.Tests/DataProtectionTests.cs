using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MultiFunPlayer.Server.Tests;

[Collection("Server environment")]
public sealed class DataProtectionTests
{
    [Fact]
    public void ExplicitKeyRootUsesConfiguredKeyLifetime()
    {
        var previousKeys = Environment.GetEnvironmentVariable("MFP_KEYS_ROOT");
        var previousLifetime = Environment.GetEnvironmentVariable(
            "MFP_DATA_PROTECTION_KEY_LIFETIME_DAYS");
        var root = Path.Combine(Path.GetTempPath(), "mfp-key-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("MFP_KEYS_ROOT", root);
        Environment.SetEnvironmentVariable("MFP_DATA_PROTECTION_KEY_LIFETIME_DAYS", "14");

        try
        {
            using var factory = new WebApplicationFactory<MultiFunPlayer.Server.Program>();
            var options = factory.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
            Assert.Equal(TimeSpan.FromDays(14), options.NewKeyLifetime);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFP_KEYS_ROOT", previousKeys);
            Environment.SetEnvironmentVariable("MFP_DATA_PROTECTION_KEY_LIFETIME_DAYS", previousLifetime);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}

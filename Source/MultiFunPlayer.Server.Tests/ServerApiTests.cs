using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MultiFunPlayer.Server.Tests;

public sealed class ServerApiTests : IClassFixture<WebApplicationFactory<MultiFunPlayer.Server.Program>>
{
    private readonly WebApplicationFactory<MultiFunPlayer.Server.Program> _factory;

    public ServerApiTests(WebApplicationFactory<MultiFunPlayer.Server.Program> factory)
    {
        _factory = factory;
        Environment.SetEnvironmentVariable("MFP_AUTH_TOKEN", null);
    }

    [Fact]
    public async Task Health_is_anonymous_and_ready()
    {
        using var client = _factory.CreateClient();
        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Status_and_capabilities_are_available()
    {
        using var client = _factory.CreateClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/status");
        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/v1/capabilities");

        Assert.Equal("empty", status.GetProperty("state").GetString(), ignoreCase: true);
        Assert.Equal("v1", capabilities.GetProperty("apiVersion").GetString());
        Assert.InRange(capabilities.GetProperty("tickHz").GetInt32(), 1, 20);
    }

    [Fact]
    public async Task OpenApi_document_describes_versioned_surface()
    {
        using var client = _factory.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");

        Assert.Equal("3.0.3", document.GetProperty("openapi").GetString());
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/status", out _));
        Assert.True(document.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("bearerAuth", out _));
    }

    [Fact]
    public async Task Load_play_pause_seek_and_axes_are_operational()
    {
        using var client = _factory.CreateClient();
        using var content = new StringContent(
            "{\"actions\":[{\"at\":0,\"pos\":0},{\"at\":1000,\"pos\":100}]}" , Encoding.UTF8, "application/json");
        var load = await client.PostAsync("/api/v1/scripts/load", new StringContent(
            "{\"name\":\"test.funscript\",\"content\":{\"actions\":[{\"at\":0,\"pos\":0},{\"at\":1000,\"pos\":100}]}}",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, load.StatusCode);

        var play = await client.PostAsync("/api/v1/player/play", null);
        Assert.Equal(HttpStatusCode.OK, play.StatusCode);
        var pause = await client.PostAsync("/api/v1/player/pause", null);
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        var seek = await client.PostAsJsonAsync("/api/v1/player/seek", new { positionSeconds = 0.5 });
        Assert.Equal(HttpStatusCode.OK, seek.StatusCode);
        var axes = await client.GetAsync("/api/v1/axes");
        Assert.Equal(HttpStatusCode.OK, axes.StatusCode);
        Assert.Contains("default", await axes.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Multi_axis_funscript_uses_portable_default_axis_catalog()
    {
        using var client = _factory.CreateClient();
        var load = await client.PostAsync("/api/v1/scripts/load", new StringContent(
            "{\"name\":\"multi.funscript\",\"content\":{\"actions\":[{\"at\":0,\"pos\":25}],\"axes\":[{\"id\":\"R2\",\"actions\":[{\"at\":1000,\"pos\":75}]}]}}",
            Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, load.StatusCode);
        var status = await load.Content.ReadFromJsonAsync<JsonElement>();
        var axes = status.GetProperty("axes");
        Assert.True(axes.TryGetProperty("L0", out _));
        Assert.True(axes.TryGetProperty("R2", out _));
    }

    [Fact]
    public async Task Token_auth_keeps_health_public_and_protects_api()
    {
        var previous = Environment.GetEnvironmentVariable("MFP_AUTH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("MFP_AUTH_TOKEN", "test-only-token");
            using var client = _factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/status")).StatusCode);
            client.DefaultRequestHeaders.Add("X-MFP-Token", "test-only-token");
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/status")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFP_AUTH_TOKEN", previous);
        }
    }
}

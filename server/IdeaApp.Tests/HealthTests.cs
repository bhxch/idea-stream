using System.Net;

namespace IdeaApp.Tests;

public class HealthTests
{
    [Fact]
    public async Task Health_returns_ok()
    {
        var factory = new TestAppFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", body);
    }
}

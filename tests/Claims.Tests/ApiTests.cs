using System.Net;
using System.Net.Http.Json;
using Claims.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Claims.Tests;

public sealed class ApiTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "claims-api-" + Guid.NewGuid());
    private readonly WebApplicationFactory<Program> app;
    private readonly HttpClient client;

    public ApiTests()
    {
        app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> {
                    ["Claims:DatabasePath"] = Path.Combine(directory, "claims.db"),
                    ["Claims:WorkerEnabled"] = "false"
                })));
        client = app.CreateClient();
    }

    public void Dispose()
    {
        client.Dispose(); app.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private Task<HttpResponseMessage> Submit(string? key = "api-1", decimal amount = 25)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/claims") {
            Content = JsonContent.Create(new ClaimRequest("api", "REF-1", "SYN-200", "2026-08-01", amount)) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task CreatesReplaysDuplicateAndRejectsConflict()
    {
        var created = await Submit();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, (await Submit()).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Submit(amount: 30)).StatusCode);
        var detail = await client.GetFromJsonAsync<ClaimDetail>(created.Headers.Location);
        Assert.Equal("Pending", detail!.Event.State);
    }

    [Fact]
    public async Task RejectsMissingKeyAndBadAmount()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Submit(key: null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Submit(amount: 0)).StatusCode);
    }

    [Fact]
    public async Task ReplayEndpointChecksStateAndReason()
    {
        var created = await (await Submit()).Content.ReadFromJsonAsync<Submission>();
        var path = $"/api/claims/{created!.Claim.Id}/replay";
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync(path, new ReplayRequest("Investigated"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(path, new ReplayRequest(""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/claims/missing")).StatusCode);
    }

    [Fact]
    public async Task DashboardAndOpenApiAreAvailable()
    {
        Assert.Contains("Claims Integration", await client.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }
}

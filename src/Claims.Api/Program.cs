using Claims.Core;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16_384);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(_ => new ClaimStore(
    builder.Configuration["Claims:DatabasePath"] ?? "data/claims.db"));
if (builder.Configuration.GetValue("Claims:WorkerEnabled", true))
    builder.Services.AddHostedService<ClaimsWorker>();
var app = builder.Build();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok", data = "synthetic", gateway = "simulated" }));
app.MapGet("/api/claims", (ClaimStore store) => Results.Ok(store.List()));
app.MapGet("/api/claims/{id}", IResult (string id, ClaimStore store) =>
    store.Get(id) is { } claim ? Results.Ok(claim) : Results.NotFound());

app.MapPost("/api/claims", IResult (ClaimRequest request, HttpRequest http, ClaimStore store) =>
{
    var errors = ClaimValidation.Errors(request);
    var key = http.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key) || key.Length > 120)
        errors["idempotencyKey"] = ["Supply an Idempotency-Key header of 1 to 120 characters."];
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    try
    {
        var result = store.Submit(request, key);
        return result.Duplicate ? Results.Ok(result)
            : Results.Created($"/api/claims/{result.Claim.Id}", result);
    }
    catch (ClaimConflictException e) { return Results.Conflict(new { error = e.Message }); }
});

app.MapPost("/api/claims/{id}/replay", IResult (string id, ReplayRequest request, ClaimStore store) =>
{
    try
    {
        var claim = store.Replay(id, request.Reason);
        return claim is null ? Results.NotFound() : Results.Ok(claim);
    }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
    catch (ReplayConflictException e) { return Results.Conflict(new { error = e.Message }); }
});
app.Run();

public partial class Program { }

public sealed class ClaimsWorker(ClaimStore store, ILogger<ClaimsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Bounded batches allow cancellation even when a large queue is present.
                for (var i = 0; i < 25 && !stoppingToken.IsCancellationRequested; i++)
                    if (!store.ProcessNext()) break;
            }
            catch (Exception e)
            {
                logger.LogError("Processing cycle failed ({ErrorType}); retrying next cycle.", e.GetType().Name);
            }
            try { await Task.Delay(500, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

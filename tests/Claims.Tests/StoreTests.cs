using Claims.Core;
using Xunit;

namespace Claims.Tests;

public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(int seconds) => now = now.AddSeconds(seconds);
}

public sealed class StoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "claims-tests-" + Guid.NewGuid());
    private readonly FakeClock clock = new();
    private readonly ClaimStore store;
    private string Database => Path.Combine(directory, "claims.db");
    private static ClaimRequest Sample(string scenario = "Normal") =>
        new("demo", "CLM-001", "SYN-100", "2026-08-20", 125.50m, "USD", scenario);

    public StoreTests() => store = new ClaimStore(Database, clock);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void SubmissionCommitsClaimEventAndAuditTogether()
    {
        var result = store.Submit(Sample(), "key-1");
        var detail = store.Get(result.Claim.Id)!;
        Assert.False(result.Duplicate);
        Assert.Equal("Queued", detail.Claim.Status);
        Assert.Equal("Pending", detail.Event.State);
        Assert.Equal("ClaimReceived", Assert.Single(detail.Audit).Action);
    }

    [Fact]
    public void RepeatedKeyAndBusinessIdProduceOneClaimAndEvent()
    {
        var a = store.Submit(Sample(), "key-1");
        var b = store.Submit(Sample(), "key-1");
        var c = store.Submit(Sample(), "key-2");
        Assert.Equal(a.Claim.Id, b.Claim.Id);
        Assert.Equal(a.Claim.Id, c.Claim.Id);
        Assert.True(b.Duplicate && c.Duplicate);
        Assert.Single(store.List());
        Assert.True(store.ProcessNext());
        Assert.False(store.ProcessNext());
        Assert.Equal(2, store.Get(a.Claim.Id)!.Audit.Count(e => e.Action == "DuplicateIgnored"));
    }

    [Fact]
    public void DecimalScaleDoesNotChangeIdempotency()
    {
        store.Submit(Sample() with { Amount = 125.5m }, "same");
        Assert.True(store.Submit(Sample() with { Amount = 125.50m }, "same").Duplicate);
    }

    [Theory]
    [InlineData("key-1")]
    [InlineData("key-2")]
    public void ConflictingPayloadIsRejectedWithoutChangingClaim(string key)
    {
        store.Submit(Sample(), "key-1");
        Assert.Throws<ClaimConflictException>(() => store.Submit(Sample() with { Amount = 999 }, key));
        Assert.Equal(125.50m, Assert.Single(store.List()).Amount);
    }

    [Fact]
    public async Task ConcurrentDuplicatesRemainUnique()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            Task.Run(() => store.Submit(Sample(), "parallel-" + i))));
        Assert.Single(results.Select(x => x.Claim.Id).Distinct());
        Assert.Single(results, x => !x.Duplicate);
        Assert.Single(store.List());
    }

    [Fact]
    public void RetriesRespectPersistedScheduleAndSurviveRestart()
    {
        var id = store.Submit(Sample("Transient"), "key").Claim.Id;
        Assert.True(store.ProcessNext());
        Assert.Equal("RetryScheduled", store.Get(id)!.Claim.Status);
        Assert.False(store.ProcessNext());
        clock.Advance(2);
        var restarted = new ClaimStore(Database, clock);
        Assert.True(restarted.ProcessNext());
        Assert.False(restarted.ProcessNext());
        clock.Advance(4);
        Assert.True(restarted.ProcessNext());
        var detail = restarted.Get(id)!;
        Assert.Equal("Integrated", detail.Claim.Status);
        Assert.Equal(3, detail.Claim.Attempts);
        Assert.Equal(3, detail.Audit.Count(a => a.Action == "ProcessingAttempt"));
        Assert.False(restarted.ProcessNext());
    }

    [Fact]
    public async Task ConcurrentConsumersDoNotProcessEventTwice()
    {
        var id = store.Submit(Sample(), "key").Claim.Id;
        var processed = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(store.ProcessNext)));
        Assert.Single(processed, x => x);
        Assert.Single(store.Get(id)!.Audit, a => a.Action == "ProcessingAttempt");
    }

    [Fact]
    public void ExhaustedRetriesGoToDeadLetterAndReplayRetainsHistory()
    {
        var id = store.Submit(Sample("Exhaust"), "key").Claim.Id;
        store.ProcessNext(); clock.Advance(2);
        store.ProcessNext(); clock.Advance(4);
        store.ProcessNext();
        var failed = store.Get(id)!;
        Assert.Equal("DeadLetter", failed.Claim.Status);
        Assert.Equal(3, failed.Event.Attempts);
        clock.Advance(100);
        Assert.False(store.ProcessNext());
        var replayed = store.Replay(id, "Testing recovery after investigation")!;
        Assert.Equal("Queued", replayed.Claim.Status);
        Assert.Equal(0, replayed.Event.Attempts);
        Assert.Equal(failed.Audit.Count + 1, replayed.Audit.Count);
        Assert.Equal("ReplayRequested", replayed.Audit.Last().Action);
        Assert.Equal(failed.Event.Id, replayed.Event.Id);
    }

    [Fact]
    public void PermanentFailureDoesNotRetryAutomatically()
    {
        var id = store.Submit(Sample("Permanent"), "key").Claim.Id;
        store.ProcessNext(); clock.Advance(100);
        Assert.Equal("DeadLetter", store.Get(id)!.Claim.Status);
        Assert.False(store.ProcessNext());
    }

    [Theory]
    [InlineData(5000, "Integrated")]
    [InlineData(5000.01, "ManualReview")]
    public void FictionalThresholdRoutesToHumanReview(decimal amount, string status)
    {
        var id = store.Submit(Sample() with { Amount = amount }, "key").Claim.Id;
        store.ProcessNext();
        Assert.Equal(status, store.Get(id)!.Claim.Status);
    }

    [Fact]
    public void ReplayRequiresDeadLetterAndReason()
    {
        var id = store.Submit(Sample(), "key").Claim.Id;
        Assert.Throws<ReplayConflictException>(() => store.Replay(id, "Investigated"));
        Assert.Throws<ArgumentException>(() => store.Replay(id, ""));
    }

    [Fact]
    public void ValidationRejectsRealMemberIdsAndInvalidMoneyDates()
    {
        var invalid = Sample() with { MemberId = "12345", Amount = -10, ServiceDate = "2026-02-30" };
        Assert.Equal(3, ClaimValidation.Errors(invalid).Count);
        Assert.Throws<ArgumentException>(() => store.Submit(invalid, "key"));
        Assert.Empty(store.List());
    }
}

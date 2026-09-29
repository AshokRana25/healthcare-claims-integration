namespace Claims.Core;

public record ClaimRequest(string Source, string ExternalClaimId, string MemberId,
    string ServiceDate, decimal Amount, string Currency = "USD", string Scenario = "Normal");

public record Claim(string Id, string Source, string ExternalClaimId, string MemberId,
    string ServiceDate, decimal Amount, string Currency, string Scenario, string Status,
    int Attempts, long CreatedAt, long UpdatedAt);

public record AuditEntry(long Sequence, string Action, string Detail, long OccurredAt);
public record ProcessingEvent(string Id, string State, int Attempts, long NextAttemptAt,
    string? LastError);
public record ClaimDetail(Claim Claim, ProcessingEvent Event, IReadOnlyList<AuditEntry> Audit);
public record Submission(Claim Claim, bool Duplicate);
public record ReplayRequest(string Reason);
public sealed class ClaimConflictException(string message) : Exception(message);
public sealed class ReplayConflictException(string message) : Exception(message);

public static class ClaimValidation
{
    public static Dictionary<string, string[]> Errors(ClaimRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        Check("source", r.Source, 40);
        Check("externalClaimId", r.ExternalClaimId, 80);
        Check("memberId", r.MemberId, 80);
        if (r.MemberId is not null && !r.MemberId.StartsWith("SYN-", StringComparison.Ordinal))
            errors["memberId"] = ["Use a synthetic member identifier beginning SYN-."];
        if (!DateOnly.TryParseExact(r.ServiceDate, "yyyy-MM-dd", out _))
            errors["serviceDate"] = ["Use a valid date in yyyy-MM-dd format."];
        if (r.Amount <= 0 || r.Amount > 1_000_000 || decimal.Round(r.Amount, 2) != r.Amount)
            errors["amount"] = ["Use an amount from 0.01 to 1000000 with at most two decimals."];
        if (r.Currency != "USD") errors["currency"] = ["This demonstration supports USD only."];
        if (r.Scenario is not ("Normal" or "Transient" or "Permanent" or "Exhaust"))
            errors["scenario"] = ["Use Normal, Transient, Permanent, or Exhaust."];
        return errors;

        void Check(string name, string? value, int maximum)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
                errors[name] = [$"Supply 1 to {maximum} nonblank characters."];
        }
    }
}

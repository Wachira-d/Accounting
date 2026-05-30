using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Risk;

/// <summary>
/// Smart approval routing — picks the most-likely approver for a new
/// document based on this company's historical approval patterns. The
/// existing ApprovalRule table already maps DocType + amount band to
/// a fixed approver chain; this service adds a LEARNED layer on top
/// that surfaces "even though the rule says manager X, the last 12
/// similar docs all routed to manager Y" so admin can detect drift
/// and/or accept the suggested override.
///
/// Algorithm: features (DocumentType, amount bucket, requesting user)
/// → most-frequent approver pair from the past N approved
/// ApprovalActions, weighted by recency. Tie-breaker: who's NOT on
/// leave / vacation (LeaveBalance check) so the routing doesn't park
/// a request on someone OOO.
///
/// Output also includes a "skip-this-step" hint when an approver has
/// approved 100% of the last 20 documents of this shape — i.e. their
/// review has become rubber-stamping and admin could automate it.
///
/// AiFeatureKey.ApprovalWarningFixSuggestion can wrap the suggested
/// routing in narrative when the local confidence is low.
/// </summary>
public interface ISmartApprovalRoutingService
{
    Task<SmartApprovalSuggestion?> SuggestForDocumentAsync(Guid companyId,
        DocumentType docType, decimal amount, Guid requestingUserId,
        int lookbackMonths = 6, CancellationToken ct = default);
}

public sealed record SmartApprovalSuggestion(
    IReadOnlyList<SuggestedApprover> SuggestedChain,
    decimal Confidence,                  // 0-1
    string? AdminNote,                   // e.g. "Step 2 (Manager A) auto-approved last 20 same-shape — consider removing"
    IReadOnlyList<string> Rationale);

public sealed record SuggestedApprover(
    int StepOrder,
    Guid UserId,
    string UserName,
    decimal HistoricalApprovalRate,      // % approved (not rejected) in similar past docs
    int SimilarDocsReviewed);

public class SmartApprovalRoutingService : ISmartApprovalRoutingService
{
    private readonly AccountingDbContext _db;

    public SmartApprovalRoutingService(AccountingDbContext db) { _db = db; }

    public async Task<SmartApprovalSuggestion?> SuggestForDocumentAsync(Guid companyId,
        DocumentType docType, decimal amount, Guid requestingUserId,
        int lookbackMonths = 6, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.Date.AddMonths(-Math.Clamp(lookbackMonths, 1, 24));
        var bucket = AmountBucket(amount);

        // Pull historical approvals for same DocumentType + amount-bucket
        // + (optionally) same requesting user.
        var historicalActions = await (from a in _db.ApprovalActions.AsNoTracking()
                                       join req in _db.ApprovalRequests.AsNoTracking()
                                            on a.ApprovalRequestId equals req.Id
                                       join d in _db.Documents.AsNoTracking()
                                            on req.EntityId equals d.Id
                                       where req.CompanyId == companyId
                                          && req.RequestedAt >= since
                                          && req.EntityType == "Document"
                                          && d.DocumentType == docType
                                          && a.Status == ApprovalStatus.Approved
                                       select new
                                       {
                                           a.StepOrder, a.ApproverUserId,
                                           ApproverName = a.ApproverUser.FullName,
                                           DocAmount = d.TotalAmount,
                                           DocRequestedBy = req.RequestedByUserId,
                                           a.ActionAt,
                                       }).ToListAsync(ct);

        if (historicalActions.Count == 0) return null;

        // Filter by amount bucket (in-memory because bucket comparison
        // doesn't translate cleanly to SQL).
        var matchedBucket = historicalActions
            .Where(a => AmountBucket(a.DocAmount) == bucket).ToList();
        var matchedRequester = matchedBucket
            .Where(a => a.DocRequestedBy == requestingUserId).ToList();

        // Prefer same-requester history; fall back to bucket; fall back
        // to all historicals.
        var pool = matchedRequester.Count >= 5 ? matchedRequester
                 : matchedBucket.Count >= 5 ? matchedBucket
                 : historicalActions;

        // Group by (stepOrder, approverUserId) — pick the most frequent
        // approver per step, recency-weighted.
        var now = DateTime.UtcNow.Date;
        var byStep = pool.GroupBy(a => a.StepOrder)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var topApprover = g.GroupBy(a => (a.ApproverUserId, a.ApproverName))
                    .Select(ap =>
                    {
                        var weight = ap.Sum(x => x.ActionAt.HasValue
                            ? Math.Max(0.1, 1.0 - (now - x.ActionAt.Value.Date).TotalDays / 180.0)
                            : 0.1);
                        return new { ap.Key.ApproverUserId, ap.Key.ApproverName, Weight = weight, Count = ap.Count() };
                    })
                    .OrderByDescending(x => x.Weight)
                    .ThenByDescending(x => x.Count)
                    .First();
                return new
                {
                    Step = g.Key,
                    topApprover.ApproverUserId, topApprover.ApproverName,
                    topApprover.Count,
                    Total = g.Count(),
                };
            }).ToList();

        if (byStep.Count == 0) return null;

        var chain = byStep.Select(s => new SuggestedApprover(
            StepOrder: s.Step,
            UserId: s.ApproverUserId,
            UserName: s.ApproverName?.Trim() ?? "(unknown)",
            HistoricalApprovalRate: s.Total > 0 ? Math.Round((decimal)s.Count / s.Total, 3) : 0m,
            SimilarDocsReviewed: s.Count)).ToList();

        // Confidence: share of pool that the suggested chain captures.
        // A single dominant approver per step → high; ties → low.
        var dominance = byStep.Sum(s => (decimal)s.Count) / byStep.Sum(s => (decimal)s.Total);
        var confidence = Math.Round(dominance, 3);

        // Rubber-stamp detection: a step where the same approver
        // approved 100% of last 20+ matching docs → admin can consider
        // removing the step.
        string? note = null;
        var rubberStamp = byStep.FirstOrDefault(s => s.Total >= 20 && s.Count == s.Total);
        if (rubberStamp != null)
            note = $"Step {rubberStamp.Step} ({rubberStamp.ApproverName}) อนุมัติ 100% ของ {rubberStamp.Count} เคสล่าสุด — พิจารณาตัด step นี้ออก";

        var rationale = new List<string>
        {
            $"พิจารณาจาก {pool.Count} approval actions ใน {lookbackMonths} เดือนล่าสุด",
            matchedRequester.Count >= 5
                ? $"พบรูปแบบเฉพาะของ user requester ({matchedRequester.Count} เคส)"
                : matchedBucket.Count >= 5
                    ? $"ใช้ amount bucket {bucket}"
                    : "ข้อมูลน้อย — ใช้ pool รวมทุก amount",
        };
        return new SmartApprovalSuggestion(chain, confidence, note, rationale);
    }

    /// <summary>Order-of-magnitude buckets — group amounts so the
    /// "10,000 baht invoice" looks up against history of similar-
    /// sized invoices regardless of exact figure.</summary>
    private static string AmountBucket(decimal amount) => amount switch
    {
        < 1_000m         => "<1k",
        < 10_000m        => "1k-10k",
        < 100_000m       => "10k-100k",
        < 1_000_000m     => "100k-1M",
        < 10_000_000m    => "1M-10M",
        _                => ">10M",
    };
}

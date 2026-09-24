using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Rank pending OcrScanResults by review priority so the user spends
/// their correction time where it does the most good. Higher priority =
/// "fixing this one teaches the system the most".
///
/// Priority signals (combined into a single score):
///   • Uncertainty: 1 − max(scan.fieldConfidence.values). High = system
///                  is unsure, so the user's correction is high-value
///                  ground truth.
///   • Novelty:    1 / (1 + vendor_seen_count). Brand-new vendors
///                  benefit most from labels — once we have ≥10 docs
///                  the patterns plateau.
///   • Recency:    fresh scans first (within 7 days = 1.0, older = 0.5).
///                  Stale scans are less likely to ever get reviewed.
///   • Auto-create suppressed: scans where HasPotentialFixedAsset = true
///                  get a boost — these are deliberately held in Draft
///                  awaiting decision.
///
/// final_score = uncertainty × (1 + novelty) × recency_factor × boost
/// (สูตรจริง + เหตุผลภาษาไทยอยู่ที่ <see cref="Accounting.Helpers.OcrReviewQueuePriority"/>)
///
/// Sample output (one company, 6 pending scans):
///   1. PEA bill #1   uncertainty=0.4 novelty=1.0 recency=1.0 → 0.80
///   2. HomePro recpt uncertainty=0.5 novelty=0.5 recency=1.0 → 0.75 (asset alert)
///   3. AIS bill #3   uncertainty=0.2 novelty=0.3 recency=1.0 → 0.26
///   ...
///
/// Pure read — no side effects. Use from any UI ("Review Queue" page).
/// </summary>
public class ActiveLearningRanker
{
    private readonly AccountingDbContext _db;

    public ActiveLearningRanker(AccountingDbContext db)
    {
        _db = db;
    }

    public record RankedScan(
        Guid ScanId,
        string? VendorName,
        string? OriginalFileName,
        DateTime ProcessedAt,
        decimal Confidence,
        decimal UncertaintyScore,
        decimal NoveltyScore,
        decimal RecencyFactor,
        bool HasPotentialFixedAsset,
        decimal PriorityScore,
        string ReasonHint);

    public async Task<List<RankedScan>> RankAsync(Guid companyId, int limit = 20)
    {
        var now = DateTime.UtcNow;
        var sevenDaysAgo = now.AddDays(-7);
        var thirtyDaysAgo = now.AddDays(-30);

        // Pull pending scans (Completed but not yet linked to a created doc, OR
        // flagged as Needs-Review by HasPotentialFixedAsset). Skip very-old
        // scans that the user is unlikely to ever come back to.
        var scans = await _db.Set<Models.Entities.OcrScanResult>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted
                && r.ScanStatus == "Completed"
                && r.ProcessedAt >= thirtyDaysAgo
                // ⚠️ เส้น "บันทึกเป็น JE อย่างเดียว" (OcrService.CreateJournalEntryFromScanAsync) ตั้ง
                // CreatedJournalEntryId แต่ไม่ตั้ง CreatedDocumentId ⇒ เดิมใบที่ลงบัญชีไปแล้ว
                // ค้างในคิวตลอด 30 วัน ("แก้เสร็จแล้วไม่หายจากคิว" — รอบ 190 ข้อ 1)
                && ((r.CreatedDocumentId == null && r.CreatedJournalEntryId == null)
                    || r.HasPotentialFixedAsset))
            .OrderByDescending(r => r.ProcessedAt)
            .Take(200)   // cap candidate pool; rank in memory
            .ToListAsync();

        if (scans.Count == 0) return new();

        // Vendor history: count of approved docs per vendor in last 12 months
        // for the novelty score.
        var twelveMonthsAgo = now.AddMonths(-12);
        var vendorCounts = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.DocumentDate >= twelveMonthsAgo
                && (d.Status == Models.Enums.DocumentStatus.Approved
                    || d.Status == Models.Enums.DocumentStatus.Paid))
            .GroupBy(d => d.ContactId)
            .Select(g => new { ContactId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ContactId, x => x.Count);

        var ranked = new List<RankedScan>();
        foreach (var r in scans)
        {
            // สูตร + เหตุผลภาษาไทยอยู่ที่ Helpers/OcrReviewQueuePriority ตัวเดียว (มีเทสต์)
            int seen = r.MatchedContactId.HasValue && vendorCounts.TryGetValue(r.MatchedContactId.Value, out var c) ? c : 0;
            var score = Accounting.Helpers.OcrReviewQueuePriority.Score(new Accounting.Helpers.OcrReviewQueueSignals(
                Confidence: r.Confidence,
                HasMatchedContact: r.MatchedContactId.HasValue,
                VendorSeenCount: seen,
                IsRecent: r.ProcessedAt.HasValue && r.ProcessedAt.Value >= sevenDaysAgo,
                HasPotentialFixedAsset: r.HasPotentialFixedAsset,
                HasHandwriting: r.HasHandwriting,
                IsDuplicate: r.IsDuplicate,
                UserCorrected: r.UserCorrectedAt.HasValue));

            ranked.Add(new RankedScan(
                r.Id, r.ExtractedVendorName, r.OriginalFileName,
                r.ProcessedAt ?? r.CreatedAt, r.Confidence,
                score.Uncertainty, score.Novelty, score.RecencyFactor,
                r.HasPotentialFixedAsset, score.Priority,
                string.Join(" · ", score.Reasons)));
        }

        return ranked.OrderByDescending(x => x.PriorityScore).Take(limit).ToList();
    }

    /// <summary>ตัวเลขประกอบคิว — ให้หน้าเว็บบอกได้ว่า "ว่างเพราะอะไร" แทนจอเปล่า
    /// (เดิมหน้าเขียนว่า "ระบบมั่นใจกับทุก scan" ทุกครั้งที่ว่าง ซึ่ง<b>ไม่เคยถูกตรวจจริง</b> —
    /// ส่วนใหญ่ว่างเพราะสแกนทุกใบถูกสร้างเป็นเอกสารแล้ว หรือเก่ากว่า 30 วัน)</summary>
    /// <param name="WindowDays">ช่วงเวลาที่นับ (วัน)</param>
    /// <param name="Pending">ใบที่อยู่ในคิวจริง (ไม่ตัดตาม limit)</param>
    /// <param name="ScannedInWindow">สแกนทั้งหมดในช่วง (ทุกสถานะ)</param>
    /// <param name="DocumentCreated">สร้างเป็นเอกสารแล้ว</param>
    /// <param name="DocumentStillDraft">ในนั้นยังเป็นร่าง (รออนุมัติในหน้าเอกสาร ไม่ใช่ในคิวนี้)</param>
    /// <param name="JournalOnly">บันทึกเป็นสมุดรายวันอย่างเดียวแล้ว</param>
    /// <param name="Failed">อ่านไม่สำเร็จ</param>
    /// <param name="InProgress">กำลังอ่าน/รอคิวอ่าน</param>
    /// <param name="OlderPending">ยังไม่ได้สร้างเอกสารแต่เก่ากว่าช่วงที่นับ (ไม่แสดงในคิว)</param>
    public record QueueSummary(
        int WindowDays,
        int Pending,
        int ScannedInWindow,
        int DocumentCreated,
        int DocumentStillDraft,
        int JournalOnly,
        int Failed,
        int InProgress,
        int OlderPending);

    public async Task<QueueSummary> SummarizeAsync(Guid companyId)
    {
        const int windowDays = 30;
        var since = DateTime.UtcNow.AddDays(-windowDays);
        var scans = _db.Set<Models.Entities.OcrScanResult>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted);
        // เงื่อนไข "อยู่ในคิว" ต้องตรงกับ RankAsync ทุกตัว (ห้ามมีสองนิยาม)
        var pending = await scans.CountAsync(r => r.ScanStatus == "Completed"
            && r.ProcessedAt >= since
            && ((r.CreatedDocumentId == null && r.CreatedJournalEntryId == null) || r.HasPotentialFixedAsset));
        var inWindow = scans.Where(r => r.CreatedAt >= since);
        var scanned = await inWindow.CountAsync();
        var created = await inWindow.CountAsync(r => r.CreatedDocumentId != null);
        var draft = await _db.Documents.AsNoTracking()
            .CountAsync(d => d.CompanyId == companyId && !d.IsDeleted
                && d.Status == Models.Enums.DocumentStatus.Draft
                && inWindow.Any(r => r.CreatedDocumentId == d.Id));
        var journalOnly = await inWindow.CountAsync(r => r.CreatedDocumentId == null && r.CreatedJournalEntryId != null);
        var failed = await inWindow.CountAsync(r => r.ScanStatus == "Failed");
        var inProgress = await inWindow.CountAsync(r => r.ScanStatus == "Pending" || r.ScanStatus == "Processing");
        var older = await scans.CountAsync(r => r.ScanStatus == "Completed"
            && r.ProcessedAt < since
            && r.CreatedDocumentId == null && r.CreatedJournalEntryId == null);
        return new QueueSummary(windowDays, pending, scanned, created, draft, journalOnly, failed, inProgress, older);
    }
}

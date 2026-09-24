using System.Linq.Expressions;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
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

    /// <summary>นิยาม "สแกนที่อยู่ในคิว" <b>ตัวเดียว</b> — ใช้ทั้ง <see cref="RankAsync"/> และ <see cref="SummarizeAsync"/>
    /// (ห้ามมีสองนิยาม) · อ่านเสร็จแล้ว และยังไม่มีผลลัพธ์ทางบัญชี หรือมีบรรทัดที่อาจเป็นสินทรัพย์ถาวรรอตัดสิน</summary>
    /// <remarks>⚠️ เส้น "บันทึกเป็น JE อย่างเดียว" (OcrService.CreateJournalEntryFromScanAsync) ตั้ง CreatedJournalEntryId
    /// แต่ไม่ตั้ง CreatedDocumentId ⇒ ต้องดูทั้งสองช่อง ("แก้เสร็จแล้วไม่หายจากคิว" — รอบ 190 ข้อ 1)</remarks>
    private static readonly Expression<Func<OcrScanResult, bool>> QueuedScan = r =>
        r.ScanStatus == "Completed"
        && ((r.CreatedDocumentId == null && r.CreatedJournalEntryId == null) || r.HasPotentialFixedAsset);

    /// <summary>วันที่ทางบัญชีของสแกน = วันที่บนกระดาษ · อ่านวันที่ไม่ได้ ⇒ วันที่อ่านเสร็จ/วันที่อัปโหลด</summary>
    private static readonly Expression<Func<OcrScanResult, DateTime>> ScanAccountingDate =
        r => r.ExtractedDate ?? r.ProcessedAt ?? r.CreatedAt;

    /// <summary>งวดที่ปิดแล้วของบริษัท (Closed/Locked) — ขอบเขตของคิวคือ "ทุกใบในงวดที่ยังไม่ปิด"
    /// (คำตัดสินเจ้าของ รอบ 193 ข้อ 32 · เดิม 30 วันล่าสุด ⇒ สแกนของงวดที่ยังเปิดแต่เก่ากว่า 30 วันหายจากคิว)</summary>
    public async Task<IReadOnlyList<ClosedDateRange>> ClosedRangesAsync(Guid companyId)
    {
        var rows = await _db.FiscalPeriods.AsNoTracking()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted && f.Status != FiscalPeriodStatus.Open)
            .Select(f => new { f.StartDate, f.EndDate, f.Status })
            .ToListAsync();
        return ClosedPeriodRanges.Build(rows.Select(r => (r.StartDate, r.EndDate, r.Status)));
    }

    private IQueryable<OcrScanResult> ScansInOpenPeriods(Guid companyId, IReadOnlyList<ClosedDateRange> closed)
        => _db.Set<OcrScanResult>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted)
            .Where(ClosedPeriodRanges.NotInClosed(ScanAccountingDate, closed));

    public async Task<List<RankedScan>> RankAsync(Guid companyId, int limit = 20)
    {
        var now = DateTime.UtcNow;
        var sevenDaysAgo = now.AddDays(-7);
        var closed = await ClosedRangesAsync(companyId);

        // ทุกสแกนที่รอตัดสินในงวดที่ยังไม่ปิด (ไม่ตัดที่ 30 วันแล้ว) · projection เฉพาะช่องที่ใช้จัดอันดับ ·
        // เพดาน 1,000 ใบเป็นแค่กันหน่วยความจำ — จำนวนจริงอยู่ใน SummarizeAsync (หน้าเว็บบอก "แสดง N จาก M")
        var scans = await ScansInOpenPeriods(companyId, closed)
            .Where(QueuedScan)
            .OrderByDescending(r => r.ProcessedAt)
            .Take(1000)
            .Select(r => new
            {
                r.Id, r.ExtractedVendorName, r.OriginalFileName, r.ProcessedAt, r.CreatedAt, r.Confidence,
                r.MatchedContactId, r.HasPotentialFixedAsset, r.HasHandwriting, r.IsDuplicate, r.UserCorrectedAt,
            })
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

    /// <summary>เอกสาร<b>ร่าง</b>ที่สร้างจากสแกนแล้วแต่ยังไม่อนุมัติ (คำตัดสินเจ้าของ รอบ 193 ข้อ 31) — เดิมสแกนออกจากคิว
    /// ทันทีที่สร้างร่าง ⇒ ร่างที่ลืมอนุมัติไม่มีใครเห็น · กดแถวแล้วไปหน้ารายละเอียดเอกสาร (มีปุ่มอนุมัติ/แก้ไข)</summary>
    public record DraftFromScan(
        Guid DocumentId,
        string DocumentNumber,
        DocumentType DocumentType,
        SensitivityKind Sensitivity,
        string? ContactName,
        DateTime DocumentDate,
        decimal TotalAmount,
        Guid ScanId,
        string? OriginalFileName,
        DateTime ScannedAt);

    /// <summary>ร่างจากสแกนในงวดที่ยังไม่ปิด (วันที่เอกสารเป็นตัวตัดสินงวด) เรียงจากวันที่เอกสารเก่าสุด —
    /// ร่างที่ค้างนานควรถูกเห็นก่อน · ผู้เรียกกรองสิทธิ์มองเห็นเอง (ฝั่งเอกสาร + ชั้นความลับ)</summary>
    public async Task<List<DraftFromScan>> DraftsFromScansAsync(Guid companyId, IReadOnlyList<ClosedDateRange>? closed = null)
    {
        closed ??= await ClosedRangesAsync(companyId);
        var fromScan = _db.Set<OcrScanResult>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted && r.CreatedDocumentId != null);
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.Status == DocumentStatus.Draft
                && fromScan.Any(r => r.CreatedDocumentId == d.Id))
            .Where(ClosedPeriodRanges.NotInClosed<Document>(d => d.DocumentDate, closed))
            .OrderBy(d => d.DocumentDate)
            .Take(1000)
            .Select(d => new
            {
                d.Id, d.DocumentNumber, d.DocumentType, d.Sensitivity, ContactName = d.Contact.Name,
                d.DocumentDate, d.TotalAmount,
            })
            .ToListAsync();
        if (docs.Count == 0) return new();

        // สแกนต้นทางของแต่ละร่าง (ถ้ามีหลายใบชี้เอกสารเดียว — ใช้ใบล่าสุด) · สองคำสั่งแทน subquery ซ้อน
        var docIds = docs.Select(d => d.Id).ToList();
        var scanRows = await fromScan
            .Where(r => docIds.Contains(r.CreatedDocumentId!.Value))
            .Select(r => new { DocId = r.CreatedDocumentId!.Value, r.Id, r.OriginalFileName, r.CreatedAt })
            .ToListAsync();
        var scanByDoc = scanRows
            .GroupBy(r => r.DocId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CreatedAt).First());

        var result = new List<DraftFromScan>();
        foreach (var d in docs)
        {
            if (!scanByDoc.TryGetValue(d.Id, out var sc)) continue;
            result.Add(new DraftFromScan(d.Id, d.DocumentNumber, d.DocumentType, d.Sensitivity, d.ContactName,
                d.DocumentDate, d.TotalAmount, sc.Id, sc.OriginalFileName, sc.CreatedAt));
        }
        return result;
    }

    /// <summary>ตัวเลขประกอบคิว — ให้หน้าเว็บบอกได้ว่า "ว่างเพราะอะไร" แทนจอเปล่า
    /// (เดิมหน้าเขียนว่า "ระบบมั่นใจกับทุก scan" ทุกครั้งที่ว่าง ซึ่ง<b>ไม่เคยถูกตรวจจริง</b>)
    /// · ขอบเขต = งวดบัญชีที่ยังไม่ปิด (รอบ 193 ข้อ 32 — เดิม 30 วันล่าสุด)</summary>
    /// <param name="ClosedPeriodCount">จำนวนงวดที่ปิด/ล็อกแล้ว (0 = ยังไม่เคยปิดงวด ⇒ คิวครอบทุกใบ)</param>
    /// <param name="Pending">สแกนที่อยู่ในคิวจริง (ไม่ตัดตาม limit)</param>
    /// <param name="DraftPending">ร่างจากสแกนที่ยังไม่อนุมัติ (เติมโดยผู้เรียกหลังกรองสิทธิ์มองเห็น)</param>
    /// <param name="ScannedInScope">สแกนทั้งหมดในงวดที่ยังไม่ปิด (ทุกสถานะ)</param>
    /// <param name="DocumentCreated">สร้างเป็นเอกสารแล้ว</param>
    /// <param name="JournalOnly">บันทึกเป็นสมุดรายวันอย่างเดียวแล้ว</param>
    /// <param name="Failed">อ่านไม่สำเร็จ</param>
    /// <param name="InProgress">กำลังอ่าน/รอคิวอ่าน</param>
    /// <param name="InClosedPeriods">ยังรอตัดสินแต่วันที่อยู่ในงวดที่ปิดแล้ว (ไม่แสดงในคิว — ลงบัญชีในงวดที่ปิดไม่ได้)</param>
    public record QueueSummary(
        int ClosedPeriodCount,
        int Pending,
        int DraftPending,
        int ScannedInScope,
        int DocumentCreated,
        int JournalOnly,
        int Failed,
        int InProgress,
        int InClosedPeriods);

    public async Task<QueueSummary> SummarizeAsync(Guid companyId, IReadOnlyList<ClosedDateRange>? closed = null)
    {
        closed ??= await ClosedRangesAsync(companyId);
        var inScope = ScansInOpenPeriods(companyId, closed);
        // เงื่อนไข "อยู่ในคิว" = QueuedScan ตัวเดียวกับ RankAsync
        var pending = await inScope.Where(QueuedScan).CountAsync();
        var scanned = await inScope.CountAsync();
        var created = await inScope.CountAsync(r => r.CreatedDocumentId != null);
        var journalOnly = await inScope.CountAsync(r => r.CreatedDocumentId == null && r.CreatedJournalEntryId != null);
        var failed = await inScope.CountAsync(r => r.ScanStatus == "Failed");
        var inProgress = await inScope.CountAsync(r => r.ScanStatus == "Pending" || r.ScanStatus == "Processing");
        var allQueued = await _db.Set<OcrScanResult>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted)
            .Where(QueuedScan)
            .CountAsync();
        var closedCount = await _db.FiscalPeriods.AsNoTracking()
            .CountAsync(f => f.CompanyId == companyId && !f.IsDeleted && f.Status != FiscalPeriodStatus.Open);
        return new QueueSummary(closedCount, pending, 0, scanned, created, journalOnly, failed, inProgress,
            Math.Max(0, allQueued - pending));
    }
}

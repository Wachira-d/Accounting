using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Periodic OCR learning maintenance:
///  • Prunes negative patterns whose FailureCount has stabilized (model has learned)
///  • Caps TimesConfirmed to prevent score inflation
///  • Computes per-company accuracy metrics for monitoring
///  • Garbage-collects very old learned patterns nobody confirms anymore
/// </summary>
public class OcrSelfCorrectionService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<OcrSelfCorrectionService> _logger;

    public OcrSelfCorrectionService(AccountingDbContext db, ILogger<OcrSelfCorrectionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <param name="CorrectedScans">สแกนที่ <b>คน</b> แก้จริงอย่างน้อยหนึ่งช่อง
    /// (จาก <c>UserCorrectedAt</c> — ไม่ใช่ <c>UpdatedAt</c> ที่ระบบเองก็ขยับ)</param>
    /// <param name="AiBilledScans">สแกนที่เรียก provider จริง (เสียเงิน)</param>
    /// <param name="DocumentsCreated">สแกนที่กลายเป็นเอกสาร</param>
    /// <param name="AiCallRate">KPI ตัวที่ 1 — ควรลดลง</param>
    /// <param name="FirstPassAcceptRate">KPI ตัวที่ 2 — ต้องอ่านคู่กันเสมอ (ควรคงที่/สูงขึ้น)</param>
    /// <param name="Verdict">คำตัดสินภาษาคนจาก <c>Helpers/OcrQualityKpi</c></param>
    /// <param name="IsRegression">ตัวเลขบอกว่ากำลัง "ประหยัดโดยโง่ลง"</param>
    /// <param name="TopCorrectedFields">ช่องที่ถูกแก้บ่อยสุด (ชื่อ:จำนวน) — บอกว่าควรปรับปรุงตรงไหนก่อน</param>
    public record AccuracyReport(
        Guid CompanyId,
        int TotalScans,
        int CorrectedScans,
        decimal CorrectionRate,
        int TotalLearnedPatterns,
        int NegativeExamples,
        int HighConfidencePatterns,
        DateTime GeneratedAt,
        int AiBilledScans = 0,
        int DocumentsCreated = 0,
        decimal AiCallRate = 0m,
        decimal FirstPassAcceptRate = 0m,
        string Verdict = "",
        bool IsRegression = false,
        IReadOnlyList<string>? TopCorrectedFields = null);

    /// <summary>ตัวชี้วัดคุณภาพไปป์ไลน์ OCR ต่อบริษัท (30 วันล่าสุด)
    ///
    /// <para>⚠️ สองอย่างที่แก้จากรุ่นเดิม (ผลตรวจ 2026-09-06 · T5):</para>
    /// <list type="number">
    /// <item><b>"ใบที่ถูกแก้" เคยนับจาก <c>UpdatedAt != null</c></b> ซึ่งขยับทุกครั้งที่
    ///   ระบบเองบันทึกแถว (จบการสแกน · ผูกเอกสารที่สร้าง · sync ตอนอนุมัติ) ⇒ อัตราการแก้
    ///   ≈ 100% ทุก tenant ตลอดกาล = ตัวเลขที่<b>อ่านไม่ได้</b> · ตอนนี้ใช้
    ///   <c>UserCorrectedAt</c> ที่ถูกตั้งเฉพาะตอนคนแก้จริง</item>
    /// <item><b>N+1</b> — เดิมยิง 3 query ต่อบริษัทใน <c>foreach</c> (บริษัท 200 ราย =
    ///   601 query ต่อการเปิดหน้าแอดมินหนึ่งครั้ง) · ตอนนี้ group ทั้งหมดเป็น 3 query</item>
    /// </list>
    ///
    /// <para>คืน <b>KPI คู่</b> ตาม <c>Helpers/OcrQualityKpi</c>: อัตราเรียก AI อย่างเดียว
    /// แยกไม่ออกว่า "นักเรียนเก่งขึ้น" หรือ "โค้ดหยุดใช้คำตอบของโมเดล" — ต้องอ่านคู่กับ
    /// first-pass accept rate เสมอ</para></summary>
    public async Task<List<AccuracyReport>> ComputeAccuracyAsync(CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-30);
        var now = DateTime.UtcNow;

        var scanStats = await _db.Set<Models.Entities.OcrScanResult>().AsNoTracking()
            .Where(r => r.CreatedAt >= since && !r.IsDeleted)
            .GroupBy(r => r.CompanyId)
            .Select(g => new
            {
                CompanyId = g.Key,
                Total = g.Count(),
                Corrected = g.Count(r => r.UserCorrectedAt != null),
                WithDocument = g.Count(r => r.CreatedDocumentId != null),
                // "เสียเงินจริง" = feature ใดก็ได้บนใบนี้ที่เรียก provider
                // (ธง UsedAi ถูกตั้งเฉพาะตอนจ่ายเงินจริง — ดู AiResponse.UsedAi)
                AiBilled = g.Count(r => r.GlAccountUsedAi || r.TargetDocTypeUsedAi || r.LineSplitUsedAi),
            })
            .ToListAsync(ct);

        var companyIds = scanStats.Select(s => s.CompanyId).ToList();
        if (companyIds.Count == 0) return new List<AccuracyReport>();

        var patternStats = (await _db.OcrLearnedPatterns.AsNoTracking()
            .Where(p => companyIds.Contains(p.CompanyId))
            .GroupBy(p => p.CompanyId)
            .Select(g => new
            {
                CompanyId = g.Key,
                Positive = g.Count(p => !p.IsNegativeExample),
                Negative = g.Count(p => p.IsNegativeExample),
                HighConfidence = g.Count(p => !p.IsNegativeExample && p.TimesConfirmed >= 5),
            })
            .ToListAsync(ct))
            .ToDictionary(x => x.CompanyId);

        // ช่องที่ถูกแก้บ่อยสุด — บอกว่าควรไปปรับปรุงตัวสกัดช่องไหนก่อน
        // (เก็บเป็น CSV บนแถว จึงต้องดึงมานับฝั่งแอป — จำนวนแถวถูกจำกัดด้วยช่วง 30 วันแล้ว)
        var correctedRows = await _db.Set<Models.Entities.OcrScanResult>().AsNoTracking()
            .Where(r => r.CreatedAt >= since && !r.IsDeleted && r.UserCorrectedFields != null)
            .Select(r => new { r.CompanyId, r.UserCorrectedFields })
            .ToListAsync(ct);
        var fieldCounts = new Dictionary<Guid, Dictionary<string, int>>();
        foreach (var row in correctedRows)
        {
            if (!fieldCounts.TryGetValue(row.CompanyId, out var map))
                fieldCounts[row.CompanyId] = map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var f in (row.UserCorrectedFields ?? "").Split(
                         ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                map[f] = map.TryGetValue(f, out var n) ? n + 1 : 1;
        }

        var reports = new List<AccuracyReport>(scanStats.Count);
        foreach (var s in scanStats)
        {
            patternStats.TryGetValue(s.CompanyId, out var pat);
            var kpi = Accounting.Helpers.OcrQualityKpi.Read(
                s.Total, s.AiBilled, s.WithDocument, s.Corrected);
            var top = fieldCounts.TryGetValue(s.CompanyId, out var fc)
                ? fc.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Take(5).Select(kv => $"{kv.Key}:{kv.Value}").ToList()
                : new List<string>();

            reports.Add(new AccuracyReport(
                s.CompanyId,
                TotalScans: s.Total,
                CorrectedScans: s.Corrected,
                CorrectionRate: s.Total == 0 ? 0m : Math.Round((decimal)s.Corrected / s.Total, 4, MidpointRounding.AwayFromZero),
                TotalLearnedPatterns: pat?.Positive ?? 0,
                NegativeExamples: pat?.Negative ?? 0,
                HighConfidencePatterns: pat?.HighConfidence ?? 0,
                GeneratedAt: now,
                AiBilledScans: s.AiBilled,
                DocumentsCreated: s.WithDocument,
                AiCallRate: kpi.AiCallRate,
                FirstPassAcceptRate: kpi.FirstPassAcceptRate,
                Verdict: kpi.Verdict,
                IsRegression: kpi.IsRegression,
                TopCorrectedFields: top));
        }

        return reports;
    }

    public async Task RunMaintenanceAsync(CancellationToken ct = default)
    {
        var maintenanceStart = DateTime.UtcNow;

        // Direct UPDATE/DELETE — no entity materialization, no change tracking.
        // For 10k+ patterns this is ~30ms instead of 200-500ms with .ToListAsync + RemoveRange.

        // 1. Cap TimesConfirmed at 100
        var capped = await _db.OcrLearnedPatterns
            .Where(p => p.TimesConfirmed > 100)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TimesConfirmed, 100), ct);

        // 2. Prune stable negative examples (60+ days, FailureCount <= 2)
        var staleCutoff = DateTime.UtcNow.AddDays(-60);
        var stale = await _db.OcrLearnedPatterns
            .Where(p => p.IsNegativeExample && p.LastConfirmedAt < staleCutoff && p.FailureCount <= 2)
            .ExecuteDeleteAsync(ct);

        // 3. GC abandoned positive patterns (180+ days, TimesConfirmed <= 1)
        var abandonedCutoff = DateTime.UtcNow.AddDays(-180);
        var abandoned = await _db.OcrLearnedPatterns
            .Where(p => !p.IsNegativeExample && p.LastConfirmedAt < abandonedCutoff && p.TimesConfirmed <= 1)
            .ExecuteDeleteAsync(ct);

        // 4. Cleanup orphaned scan files
        //    Two cohorts targeted:
        //      a) Failed scans older than 7 days — likely won't be retried
        //      b) Completed scans without CreatedDocumentId older than 30 days
        //         — user reviewed but never converted to document; cold storage is wasteful
        var orphanFilesPurged = 0;
        var orphanRowsDeleted = 0;
        try
        {
            var failedCutoff = DateTime.UtcNow.AddDays(-7);
            var abandonedCutoff2 = DateTime.UtcNow.AddDays(-30);

            // Identify scan ids and their file ids in one query
            // ฝ่ายค้านรอบสอง (R2-C4): สแกนที่ลง JE ตรง (ไฟล์ยังเป็น OcrScan) คือหลักฐานของ JE — ห้ามลบ (ม.10 · §87/3) ·
            // ตัดสินด้วย AttachmentRetention.ScanFilePurgeable ตัวเดียวกับเส้นลบสแกน · กรองที่ query ด้วยเพื่อไม่ให้แถวที่ข้ามแน่ ๆ
            // (และแถวที่ไฟล์ถูกลบไปแล้ว) กินโควตา 500 แถวทุกรอบจนแถวจริงไม่ถึงคิว
            var orphans = await _db.Set<Models.Entities.OcrScanResult>()
                .Where(s =>
                    (s.ScanStatus == "Failed" && s.CreatedAt < failedCutoff) ||
                    (s.ScanStatus == "Completed" && s.CreatedDocumentId == null && s.CreatedAt < abandonedCutoff2))
                .Where(s => s.FileAttachmentId != null && s.CreatedJournalEntryId == null)
                .Where(s => _db.FileAttachments.Any(f => f.Id == s.FileAttachmentId && f.CompanyId == s.CompanyId
                    && f.EntityType == "OcrScan"))
                .Select(s => new { s.Id, s.CompanyId, FileId = s.FileAttachmentId!.Value })
                .Take(500)  // throttled per cycle
                .ToListAsync(ct);

            // สแกนพี่น้อง (retry · สแกนซ้ำ) ที่ชี้ไฟล์เดียวกันและผูกเอกสาร/JE แล้ว ⇒ ไฟล์นั้นเป็นหลักฐานของรายการนั้น
            var orphanFileIds = orphans.Select(o => o.FileId).Distinct().ToList();
            var linkedFiles = orphanFileIds.Count == 0
                ? new HashSet<(Guid CompanyId, Guid FileId)>()
                : (await _db.Set<Models.Entities.OcrScanResult>().AsNoTracking()
                    .Where(s => s.FileAttachmentId != null && orphanFileIds.Contains(s.FileAttachmentId.Value)
                        && (s.CreatedDocumentId != null || s.CreatedJournalEntryId != null))
                    .Select(s => new { s.CompanyId, FileId = s.FileAttachmentId!.Value })
                    .ToListAsync(ct))
                    .Select(x => (x.CompanyId, x.FileId)).ToHashSet();

            foreach (var orphan in orphans)
            {
                var attachment = await _db.FileAttachments
                    .FirstOrDefaultAsync(f => f.Id == orphan.FileId && f.CompanyId == orphan.CompanyId, ct);
                if (attachment == null) continue;
                if (!Accounting.Helpers.AttachmentRetention.ScanFilePurgeable(attachment.EntityType,
                        linkedToEntry: linkedFiles.Contains((orphan.CompanyId, orphan.FileId))))
                    continue;
                try
                {
                    if (System.IO.File.Exists(attachment.StoragePath))
                    {
                        System.IO.File.Delete(attachment.StoragePath);
                        orphanFilesPurged++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete orphan file {Path}", attachment.StoragePath);
                }
                _db.FileAttachments.Remove(attachment);
                orphanRowsDeleted++;
            }

            // ฝ่ายค้านรอบสอง (R2-C4): ไฟล์ของสแกนที่ถูกถอด (soft-delete) แล้วไม่มีสแกนแถวใดชี้อยู่ — เดิมไม่มีงานไหนเก็บกวาด ⇒
            // ค้างดิสก์ตลอดไป (ข้อมูลส่วนบุคคลบนใบเสร็จอยู่เกินวัตถุประสงค์ · PDPA retention by purpose) · ครบระยะผ่อนแล้วลบจริง
            var sweepNow = DateTime.UtcNow;
            var sweepCutoff = sweepNow.AddDays(-Accounting.Helpers.AttachmentRetention.DeletedScanFileGraceDays);
            var deletedScanFiles = await _db.FileAttachments.IgnoreQueryFilters()
                .Where(f => f.IsDeleted && f.EntityType == "OcrScan" && (f.UpdatedAt ?? f.CreatedAt) < sweepCutoff)
                .Take(500)
                .ToListAsync(ct);
            var sweepIds = deletedScanFiles.Select(f => f.Id).ToList();
            var stillReferenced = sweepIds.Count == 0
                ? new HashSet<(Guid CompanyId, Guid FileId)>()
                : (await _db.Set<Models.Entities.OcrScanResult>().IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.FileAttachmentId != null && sweepIds.Contains(s.FileAttachmentId.Value))
                    .Select(s => new { s.CompanyId, FileId = s.FileAttachmentId!.Value })
                    .ToListAsync(ct))
                    .Select(x => (x.CompanyId, x.FileId)).ToHashSet();
            foreach (var f in deletedScanFiles)
            {
                if (!Accounting.Helpers.AttachmentRetention.DeletedScanFileSweepable(f.EntityType,
                        referencedByAnyScan: stillReferenced.Contains((f.CompanyId, f.Id)),
                        deletedAtUtc: f.UpdatedAt ?? f.CreatedAt, nowUtc: sweepNow))
                    continue;
                try
                {
                    if (!string.IsNullOrEmpty(f.StoragePath) && System.IO.File.Exists(f.StoragePath))
                    {
                        System.IO.File.Delete(f.StoragePath);
                        orphanFilesPurged++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ลบไฟล์สแกนที่ถูกถอดแล้วไม่สำเร็จ {Path}", f.StoragePath);
                    continue;   // ไฟล์จริงยังอยู่ — เก็บแถวไว้ให้รอบหน้าลองใหม่ (ไม่ทิ้งไฟล์กำพร้าที่ไม่มีแถวชี้)
                }
                _db.FileAttachments.Remove(f);
                orphanRowsDeleted++;
            }
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orphan-file cleanup pass failed (non-fatal)");
        }

        _logger.LogInformation(
            "OCR self-correction maintenance: capped {Capped}, pruned {Stale} stale negatives, removed {Abandoned} abandoned positives, purged {Files} orphan files ({Rows} rows) in {Elapsed}ms",
            capped, stale, abandoned, orphanFilesPurged, orphanRowsDeleted,
            (int)(DateTime.UtcNow - maintenanceStart).TotalMilliseconds);
    }

    /// <summary>Run maintenance scoped to a single company (admin debug tool).</summary>
    public async Task RunMaintenanceForCompanyAsync(Guid companyId, CancellationToken ct = default)
    {
        var capped = await _db.OcrLearnedPatterns
            .Where(p => p.CompanyId == companyId && p.TimesConfirmed > 100)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TimesConfirmed, 100), ct);

        var staleCutoff = DateTime.UtcNow.AddDays(-60);
        var stale = await _db.OcrLearnedPatterns
            .Where(p => p.CompanyId == companyId
                && p.IsNegativeExample && p.LastConfirmedAt < staleCutoff && p.FailureCount <= 2)
            .ExecuteDeleteAsync(ct);

        var abandonedCutoff = DateTime.UtcNow.AddDays(-180);
        var abandoned = await _db.OcrLearnedPatterns
            .Where(p => p.CompanyId == companyId
                && !p.IsNegativeExample && p.LastConfirmedAt < abandonedCutoff && p.TimesConfirmed <= 1)
            .ExecuteDeleteAsync(ct);

        _logger.LogInformation("Per-company OCR maintenance for {Company}: {Capped}/{Stale}/{Abandoned}",
            companyId, capped, stale, abandoned);
    }
}

using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// รอบ 232 — งานซ่อมข้อมูลเก่า: ใบลูกที่ผูกหัว (<c>RelatedDocumentId</c>) กับใบต้นทางชนิดแปลงต่อ (ใบเสนอราคา/ใบขอซื้อ/ใบสั่งซื้อ/ใบรับสินค้า/ใบส่งของ)
/// แต่<b>ไม่มีบรรทัดใดชี้บรรทัดต้นทางที่ยังมีอยู่</b> (ไม่เคยมี · หรือ dangling เพราะใบต้นทางถูกแก้ไขแล้วบรรทัดเดิมถูกลบ) ⇒ ป้าย lifecycle ของใบต้นทาง
/// ขึ้น "ไม่ทราบสัดส่วน" ตลอดกาลทั้งที่ยอดเท่ากันเป๊ะ (บั๊กเจ้าของ: PO-20261007-0001 / PI-20261007-0004 · 12,164.72)
///
/// <para>กติกา: ประทับ <b>เฉพาะเมื่อจับคู่ได้แน่นอน</b> (<see cref="SourceLineBackfill.Stamp"/> — จำนวนบรรทัดเท่า + รหัสสินค้า/คำอธิบาย + จำนวนตรงทุกบรรทัด) ·
/// จับไม่ได้ = ไม่แต่ง (หลักการ F2 ข้อ 3) · ใบมัดจำ/ใบออกแทน/ใบที่ยกเลิก-ปฏิเสธ ไม่แตะ · เขียนแถว audit ต่อใบ (hash chain) · นับผลลง <c>JobRunLog</c>
/// ("SourceLineBackfill") · ไม่ใช่ SQL ใน <c>DatabaseMigrationHelper</c> เพราะกติกาจับคู่ต้องเป็น helper ตัวเดียวกับทางเข้าสด (ห้ามสูตรที่สอง)</para>
///
/// <para><b>watermark ต่อบริษัท</b> (ฝ่ายค้าน S4 · กฎ #4 D): <c>Company.SourceLineBackfillCheckedAt</c> — ใบลูกที่ "แตะล่าสุด" (max ของ <c>CreatedAt</c>/<c>UpdatedAt</c> ของใบลูก และ <c>UpdatedAt</c> ของใบต้นทาง) ≤ watermark ถือว่าตรวจแล้ว
/// (ซ่อมได้หรือจับคู่ไม่ได้ก็ตาม — ใบที่จับไม่ได้ไม่ถูกแตะ จึงใช้เวลาแตะล่าสุดเป็นตัวบอก "ตรวจแล้ว" แทนการประทับบนใบ) ⇒ บูตรอบถัดไปตรวจเฉพาะใบที่
/// ถูกสร้าง/แก้ (หรือใบต้นทางถูกแก้ ⇒ Remap สดจับไม่ได้ ⇒ dangling) หลัง watermark (ทางเข้าสดประทับให้เองอยู่แล้ว — ที่เหลือคือใบจากทางเข้าที่ยังหลุด) · ต่อบริษัทวนเป็น batch จนหมดแล้วค่อยเลื่อน watermark
/// (บริษัทหนึ่งล้มไม่ลากบริษัทอื่น · ล้มกลางคัน watermark ไม่เลื่อน รอบหน้าทำต่อ) · รันหนึ่งรอบหลังเริ่มระบบ ใต้ <see cref="JobLock"/></para>
/// </summary>
public class SourceLineBackfillJob : BackgroundService
{
    public const string JobName = "SourceLineBackfill";
    public const int BatchSize = 500;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SourceLineBackfillJob> _logger;

    public SourceLineBackfillJob(IServiceScopeFactory scopes, ILogger<SourceLineBackfillJob> logger) { _scopes = scopes; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch { return; }
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var rec = scope.ServiceProvider.GetService<IJobRunRecorder>();
            await JobLock.RunExclusiveAsync(db, AdvisoryLockKey.BackgroundJob, JobName, async () =>
            {
                var started = DateTime.UtcNow;
                Outcome o;
                try { o = await RunOnceAsync(db, started, stoppingToken, _logger); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SourceLineBackfill ล้ม");
                    if (rec != null) await rec.RecordAsync(JobName, false, ex.Message, 0, started);
                    return;
                }
                _logger.LogInformation("SourceLineBackfill: ตรวจ {Companies} บริษัท {Candidates} ใบ · ซ่อม {Docs} ใบ ({Lines} บรรทัด) · จับคู่ไม่ได้ {Unmatched} ใบ (คงป้ายไม่ทราบสัดส่วน) · ล้ม {Failed} บริษัท",
                    o.Companies, o.Candidates, o.RepairedDocuments, o.RepairedLines, o.Unmatched, o.FailedCompanies);
                if (rec != null)
                    await rec.RecordAsync(JobName, o.FailedCompanies == 0,
                        $"ตรวจ {o.Companies} บริษัท {o.Candidates} ใบ · ซ่อม {o.RepairedDocuments} ใบ ({o.RepairedLines} บรรทัด) · จับคู่ไม่ได้ {o.Unmatched} ใบ · ล้ม {o.FailedCompanies} บริษัท",
                        o.RepairedDocuments, started);
            }, _logger, ct: stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _logger.LogError(ex, "SourceLineBackfill ขอล็อกไม่สำเร็จ"); }
    }

    public readonly record struct Outcome(int Companies, int Candidates, int RepairedDocuments, int RepairedLines, int Unmatched, int FailedCompanies);

    /// <summary>หนึ่งรอบทุกบริษัท — public static เพื่อเทสต์ Db (ต้องถือ JobLock ก่อนเรียกจากงานจริง) · <paramref name="nowUtc"/> = watermark ใหม่ของบริษัทที่ตรวจครบ</summary>
    public static async Task<Outcome> RunOnceAsync(AccountingDbContext db, DateTime nowUtc, CancellationToken ct, ILogger? logger = null)
    {
        var companyIds = await db.Companies.AsNoTracking()
            .Where(c => !c.IsDeleted && (c.SourceLineBackfillCheckedAt == null || c.SourceLineBackfillCheckedAt < nowUtc))
            .OrderBy(c => c.SourceLineBackfillCheckedAt).ThenBy(c => c.CreatedAt)
            .Select(c => c.Id).ToListAsync(ct);
        int candidates = 0, repairedDocs = 0, repairedLines = 0, unmatched = 0, failed = 0;
        foreach (var companyId in companyIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var r = await RunCompanyAsync(db, companyId, nowUtc, ct);
                candidates += r.Candidates; repairedDocs += r.RepairedDocuments; repairedLines += r.RepairedLines; unmatched += r.Unmatched;
            }
            catch (Exception ex)
            {
                failed++;
                logger?.LogError(ex, "SourceLineBackfill ของบริษัท {Company} ล้ม (watermark ไม่เลื่อน — รอบหน้าทำต่อ)", companyId);
                db.ChangeTracker.Clear();
            }
        }
        return new Outcome(companyIds.Count, candidates, repairedDocs, repairedLines, unmatched, failed);
    }

    /// <summary>บริษัทเดียว: วน batch จนไม่เหลือใบที่ "แตะล่าสุด" ∈ (watermark, nowUtc] แล้วเลื่อน watermark = nowUtc</summary>
    public static async Task<Outcome> RunCompanyAsync(AccountingDbContext db, Guid companyId, DateTime nowUtc, CancellationToken ct)
    {
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted, ct);
        if (company == null) return default;
        var sourceTypes = DocumentConversionProgress.SourceTypes;
        var inactive = DocumentConversionProgress.InactiveChildStatuses;
        // Npgsql timestamptz รับเฉพาะ Kind=Utc — MinValue ดิบเป็น Unspecified ⇒ throw ตอน query
        var since = company.SourceLineBackfillCheckedAt ?? DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        int candidates = 0, repairedDocs = 0, repairedLines = 0, unmatched = 0;
        // cursor = "เวลาแตะล่าสุด" ของใบท้าย batch ก่อน + Id ที่ตรวจแล้วซึ่งเวลาเท่า cursor (กันใบเวลาซ้ำตกหล่นที่รอยต่อ batch) —
        // ใบที่จับไม่ได้ไม่ถูกแตะ จึงต้องเลื่อน cursor เอง ไม่ใช่ query ซ้ำจนวนไม่จบ
        var cursor = since;
        var tieIds = new List<Guid>();
        while (!ct.IsCancellationRequested)
        {
            // ใบลูกที่หัวผูกใบต้นทางชนิดแปลงต่อ (บริษัทเดียวกัน) แต่ไม่มีบรรทัดที่ชี้บรรทัดต้นทางซึ่งยังมีอยู่จริง
            // ฝ่ายค้าน N4: "ยังมีอยู่จริง" พึ่ง global query filter !IsDeleted ของ DocumentLines (บรรทัดที่ลบแบบ soft = ไม่มี) —
            // และ UpdateDocumentAsync ลบบรรทัดเดิม **จริง** (RemoveRange · Id ใหม่) ⇒ ตัวชี้ของใบลูกชี้แถวที่หายไปจากตาราง (dangling) ไม่ใช่แค่ถูกซ่อน
            // ฝ่ายค้าน S4: "แตะล่าสุด" = max(ใบลูก CreatedAt/UpdatedAt, ใบต้นทาง UpdatedAt) — ใบต้นทางถูกแก้หลัง watermark แล้ว Remap จับไม่ได้ (dangling)
            // ⇒ รอบหน้าเห็นใบลูกเดิมอีกครั้งและลองด้วยกติกาจำนวน · ใบที่ไม่มีใครแตะตั้งแต่ watermark ไม่ถูกกวาดซ้ำ
            var batch = await (
                from c in db.Documents.AsNoTracking()
                join s in db.Documents.AsNoTracking() on c.RelatedDocumentId equals (Guid?)s.Id
                let childTouched = c.UpdatedAt != null && c.UpdatedAt > c.CreatedAt ? c.UpdatedAt.Value : c.CreatedAt
                let touched = s.UpdatedAt != null && s.UpdatedAt > childTouched ? s.UpdatedAt.Value : childTouched
                where c.CompanyId == companyId && s.CompanyId == companyId
                      && !c.IsDeleted && !s.IsDeleted
                      && (touched > cursor || (touched == cursor && !tieIds.Contains(c.Id))) && touched <= nowUtc
                      && sourceTypes.Contains(s.DocumentType)
                      && !inactive.Contains(c.Status)
                      && !c.IsDeposit && c.ReplacesDocumentId == null
                      && !db.DocumentLines.Any(l => l.DocumentId == c.Id && l.SourceLineId != null
                            && db.DocumentLines.Any(x => x.Id == l.SourceLineId))
                orderby touched, c.Id
                select new { ChildId = c.Id, SourceId = s.Id, c.DocumentNumber, SourceNumber = s.DocumentNumber, Touched = touched })
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;
            candidates += batch.Count;
            foreach (var cand in batch)
            {
                if (ct.IsCancellationRequested) return new Outcome(1, candidates, repairedDocs, repairedLines, unmatched, 0);
                var childLines = await db.DocumentLines
                    .Where(l => l.DocumentId == cand.ChildId && l.Document.CompanyId == companyId)
                    .ToListAsync(ct);
                var sourceLines = await db.DocumentLines.AsNoTracking()
                    .Where(l => l.DocumentId == cand.SourceId && l.Document.CompanyId == companyId)
                    .ToListAsync(ct);
                var before = childLines.Select(l => new { l.Id, l.SourceLineId }).ToList();
                // dangling (ชี้บรรทัดที่ไม่มีแล้ว) พิสูจน์จาก query ข้างบนแล้ว ⇒ ทับได้ · จับไม่ได้ = ไม่แตะ
                var n = SourceLineBackfill.Stamp(childLines, sourceLines, matchQuantity: true, overwriteForeign: true);
                if (n == 0) { unmatched++; continue; }
                db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId,
                    Action = AuditAction.Update,
                    EntityType = "Document",
                    EntityId = cand.ChildId.ToString(),
                    OldValues = System.Text.Json.JsonSerializer.Serialize(before),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = JobName,
                        ruleCode = SourceLineBackfill.RuleCode,
                        documentNumber = cand.DocumentNumber,
                        source = cand.SourceNumber,
                        lines = childLines.Select(l => new { childLineId = l.Id, sourceLineId = l.SourceLineId }),
                    }),
                });
                await db.SaveChangesAsync(ct);
                repairedDocs++;
                repairedLines += n;
            }
            if (batch.Count < BatchSize) break;
            var last = batch[^1].Touched;
            if (last != cursor) tieIds.Clear();
            tieIds.AddRange(batch.Where(b => b.Touched == last).Select(b => b.ChildId));
            cursor = last;
        }
        if (ct.IsCancellationRequested) return new Outcome(1, candidates, repairedDocs, repairedLines, unmatched, 0);
        company.SourceLineBackfillCheckedAt = nowUtc;
        await db.SaveChangesAsync(ct);
        return new Outcome(1, candidates, repairedDocs, repairedLines, unmatched, 0);
    }
}

using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>ทางคืนของแถวค่าบริการสแกน API v1 หนึ่งแถว — ผลของ <see cref="OcrFailedScanRefund.Decide"/></summary>
public enum OcrFailedScanRefundAction
{
    /// <summary>ไม่แตะ (สแกนใช้เครื่องอ่านจริงและสำเร็จ · ไม่พบสแกน · sandbox · เข้าใบแล้วแต่ไม่ได้คิดเงิน)</summary>
    None,
    /// <summary>ยังไม่เข้าใบแจ้งหนี้ (หลังจ่าย) ⇒ ถอดแถวออกจากรอบบิล (soft-delete — ไม่ถึงใบ ไม่กินโควตาฟรีของเดือน)</summary>
    RemoveUnbilled,
    /// <summary>เติมเงิน (prepaid) แถวยังไม่มีใบ (ยังไม่ปิดรอบ หรือปิดรอบแบบไม่มีใบ) ⇒ คืนเครดิต + ถอดแถวออกจากรอบ</summary>
    CreditPrepaidAndRemove,
    /// <summary>เติมเงิน แถวอยู่ในใบแล้ว ⇒ คืนเครดิต (แถวคงไว้เป็นประวัติ)</summary>
    CreditPrepaid,
    /// <summary>หลังจ่าย แถวเข้าใบแจ้งหนี้/ใบกำกับไปแล้ว ⇒ ระบบคืนเองไม่ได้ (ใบที่ออกแล้วแก้ไม่ได้ — ต้องออกใบลดหนี้ ม.86/10) ⇒ บันทึก + บอกเจ้าของ + คิวผู้ให้บริการ · <b>ไม่ประทับว่าคืนแล้ว</b></summary>
    NeedsCreditNote,
}

/// <summary>
/// **คืนค่าบริการสแกน API v1 ที่ถูกคิดก่อนรอบ 258 ทั้งที่สแกนไม่ได้ใช้เครื่องอ่านจริง/ล้ม — คำตัดสิน 260-5-2 · แก้ตามฝ่ายค้าน 260-5-6**
///
/// <para>═══ ที่มา ═══ team-C C-H3 · รอบ 258 (คำตัดสิน 258-2-8 ข้อ 2) หยุดคิดเงินสแกนที่ <see cref="OcrQuotaRefundRule.ShouldRefund"/> ตอบว่าไม่ได้ใช้เครื่องอ่าน
/// (ล้ม · คัดลอกผลเดิม · อ่านจาก e-Tax XML) แต่แถว <c>UsageEvents</c> ที่คิดไปแล้วยังอยู่ (F2 ข้อ 9) · เงินที่เก็บโดยไม่มีบริการ = ลาภมิควรได้ (ป.พ.พ. ม.406) ⇒ ต้องคืน</para>
///
/// <para>═══ กติกา ═══ ตัวตัดสินสแกนตัวเดียวกับเส้นเว็บ/LINE/v1 (<see cref="OcrQuotaRefundRule.ShouldRefund"/>) · <c>UsageEvent</c> เป็นหลักฐานบิล append-only:
/// แถวที่ยังไม่มีใบถอดออกจากรอบได้ (แบบเดียวกับ <c>NotifyQuotaService</c>) · แถวที่อยู่ในใบแล้วไม่แก้ — เติมเงินคืนเครดิต · หลังจ่ายต้องออกใบลดหนี้โดยผู้ให้บริการ
/// (บอกเจ้าของบริษัทว่า "ยังไม่ได้คืน" + คิวของแอดมินแพลตฟอร์ม <see cref="AwaitingCreditNotesAsync"/> · ไม่ประทับว่าคืนแล้ว · F2 ข้อ 3)</para>
///
/// <para>═══ ไม่คืนซ้ำ ═══ (1) marker <see cref="MarkerKey"/> + <c>pg_advisory_xact_lock</c> ⇒ ทั้งงานครั้งเดียว · (2) ต่อแถว: แถว audit <see cref="Actor"/> ของแถวนั้นมีแล้ว = ข้าม ·
/// (3) ล็อกงวดเดียวกับงานออกบิล (<c>UsageInvoicing</c>) + ถอดด้วย UPDATE ที่มีเงื่อนไข "ยังไม่มีใบ" (0 แถว ⇒ ถือว่าเข้าใบแล้ว) + เครดิตแบบบวกเพิ่มในคำสั่งเดียว (ฝ่ายค้าน M5)</para>
///
/// <para>═══ ขอบเขตที่ตั้งใจ ═══ แถวที่สำเร็จจริงซึ่งถูกคิดเงินเพราะโควตาฟรีถูกแถวล้มกินไปก่อน — ไม่คำนวณราคาย้อนหลัง (ราคาถูก snapshot ต่อแถว) แต่<b>นับให้เห็น</b>
/// รวมเดือนที่ปิดรอบแล้ว (ฝ่ายค้าน M2 · audit <c>freeSlotOnly</c> + ข้อความเจ้าของ + คิวผู้ให้บริการ)</para>
/// </summary>
public static class OcrFailedScanRefund
{
    /// <summary>คีย์ marker ใน <c>OneShotDataFixes</c></summary>
    public const string MarkerKey = "r260-5-6-ocr-v1-failed-scan-refund";   // 260-5-6: คีย์ใหม่ ⇒ ถ้ารุ่นแรกเคยรันแล้ว รุ่นนี้รันอีกครั้ง (แถวที่รุ่นแรกจัดการแล้วถูกข้ามด้วยกุญแจ audit ต่อแถว)
    /// <summary>ผู้กระทำบนแถว audit (ใช้เป็นกุญแจกันคืนซ้ำต่อแถวด้วย)</summary>
    public const string Actor = "system:ocr-v1-refund";
    /// <summary>รหัสฟีเจอร์ของสแกน API v1 (ตัวเดียวกับ <c>OcrV1Controller.Feature</c>)</summary>
    public const string FeatureCode = "ocr.scan";
    /// <summary>ชนิดอ้างอิงของแถวสแกน (ตัวเดียวกับที่ <c>OcrV1Controller</c> ส่งให้ <c>MeterAsync</c>)</summary>
    public const string RefEntityType = "OcrScanResult";
    public const string RuleCode = "OCR-V1-REFUND";
    public const string LegalReference = "ป.พ.พ. ม.406 (ลาภมิควรได้) · ป.รัษฎากร ม.86/10 (ใบลดหนี้) · คำตัดสิน 258-2-8 · 260-5-2 · 260-5-6";
    /// <summary>ชนิดเรื่องของกระดิ่งถึงแอดมินแพลตฟอร์ม (กันซ้ำ)</summary>
    public const string AdminNotificationEntityType = "OcrV1CreditNoteQueue";

    /// <summary>ตัดสินทางคืนของแถวหนึ่ง (pure)</summary>
    /// <param name="scanFound">พบแถวสแกนของบริษัทเดียวกัน — ไม่พบ = ไม่รู้ว่าสแกนล้มไหม ⇒ ไม่แตะ (ไม่เดา)</param>
    /// <param name="billed">แถวปิดรอบบิลแล้ว (<c>BilledPeriod</c> หรือ <c>BilledDocumentId</c> มีค่า)</param>
    /// <param name="invoiced">แถวอยู่ในใบแจ้งหนี้จริง (<c>BilledDocumentId</c> มีค่า)</param>
    /// <param name="prepaid">กลุ่มผู้จ่ายเป็นแบบเติมเงิน (ตัดเครดิตตอนบันทึก)</param>
    public static OcrFailedScanRefundAction Decide(bool scanFound, string? scanStatus, string? ocrEngine,
        decimal chargedAmount, bool isSandbox, bool billed, bool invoiced, bool prepaid)
    {
        if (!scanFound || isSandbox) return OcrFailedScanRefundAction.None;
        if (!OcrQuotaRefundRule.ShouldRefund(scanStatus, ocrEngine)) return OcrFailedScanRefundAction.None;
        // M3: เติมเงินที่ปิดรอบแบบไม่มีใบ ⇒ ถอดด้วย (ไม่ค้างเป็น "ยอดค้างเก็บ" ของรายงาน prepaid-accounts) · มีใบจริงเท่านั้นที่แถวคงไว้
        if (prepaid && chargedAmount > 0m)
            return invoiced ? OcrFailedScanRefundAction.CreditPrepaid : OcrFailedScanRefundAction.CreditPrepaidAndRemove;
        if (!billed) return OcrFailedScanRefundAction.RemoveUnbilled;
        return chargedAmount > 0m ? OcrFailedScanRefundAction.NeedsCreditNote : OcrFailedScanRefundAction.None;
    }

    /// <summary>ผลของงานต่อทั้งฐาน (ตัวเลขสำหรับ log)</summary>
    public sealed record Result(int Removed, int Credited, int NeedsCreditNote, decimal CreditedAmount, decimal CreditNoteAmount, int Companies);

    /// <summary>แถวในคิวใบลดหนี้ของผู้ให้บริการ (M4) — จัดกลุ่มตามใบแจ้งหนี้ค่าบริการ/บริษัท/งวด · <c>Amount</c> 0 = สแกนล้มที่กินโควตาฟรี (ให้ผู้ให้บริการพิจารณา)</summary>
    public sealed record AwaitingCreditNote(Guid? BilledDocumentId, Guid CompanyId, string? CompanyName, string? BilledPeriod, int Scans, decimal Amount);

    private sealed record Agg(int Removed, int Credited, decimal CreditedAmt, int Cn, decimal CnAmt, int FreeSlots);

    private static readonly Agg ZeroAgg = new(0, 0, 0m, 0, 0m, 0);

    private static Agg Plus(Agg x, Agg o)
        => new(x.Removed + o.Removed, x.Credited + o.Credited, x.CreditedAmt + o.CreditedAmt, x.Cn + o.Cn, x.CnAmt + o.CnAmt, x.FreeSlots + o.FreeSlots);

    /// <summary>ข้อความถึงเจ้าของ (ไม่อ้างว่าคืนส่วนที่ยังไม่ได้คืน · เติมเงิน = ล้างยอดใช้เครดิตของกลุ่มบิล ไม่ใช่คืนเงินสด — M3/L4)</summary>
    private static string OwnerMessage(Agg a, int companies)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var parts = new List<string>();
        if (a.Removed > 0) parts.Add($"ถอดค่าสแกนที่ยังไม่เข้าใบแจ้งหนี้ {a.Removed} ครั้งออกจากรอบบิลแล้ว (ไม่ถูกเรียกเก็บ)");
        if (a.Credited > 0)
            parts.Add($"ล้างค่าสแกนที่ไม่ควรถูกคิด {a.CreditedAmt.ToString("N2", inv)} บาท ({a.Credited} ครั้ง) ออกจากยอดใช้เครดิตของกลุ่มบิลแล้ว (เครดิตกลับเข้ากลุ่มบิล — ไม่ใช่การคืนเงินสด)");
        if (a.Cn > 0)
            parts.Add($"ค่าสแกน {a.Cn} ครั้ง {a.CnAmt.ToString("N2", inv)} บาท อยู่ในใบแจ้งหนี้ที่ออกไปแล้ว — ยังไม่ได้คืน ผู้ให้บริการต้องออกใบลดหนี้ให้ (ส่งเรื่องให้ผู้ให้บริการแล้ว)");
        if (a.FreeSlots > 0)
            parts.Add($"สแกนที่ล้ม {a.FreeSlots} ครั้งเคยกินโควตาฟรีของเดือนนั้น — ผู้ให้บริการจะตรวจว่าค่าสแกนที่สำเร็จในเดือนเดียวกันต้องลดหนี้เพิ่มหรือไม่");
        var scope = companies > 1 ? $" (รวม {companies} บริษัทที่คุณเป็นเจ้าของ)" : "";
        return "สแกนผ่าน API ที่อ่านไม่สำเร็จ/ใช้ผลเดิม/อ่านจาก e-Tax XML ไม่ควรถูกคิดค่าบริการ (แก้แล้วตั้งแต่รอบ 258)" + scope + ": " + string.Join(" · ", parts);
    }

    /// <summary>M4 — คิวใบลดหนี้ที่ผู้ให้บริการต้องพิจารณา (อ่านอย่างเดียว · แอดมินแพลตฟอร์ม · ข้ามบริษัทโดยตั้งใจ) — จากแถว audit ของงานนี้ที่
    /// <c>awaitingCreditNote</c> หรือ <c>freeSlotOnly</c></summary>
    public static async Task<List<AwaitingCreditNote>> AwaitingCreditNotesAsync(AccountingDbContext db, CancellationToken ct = default)
    {
        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.UserEmail == Actor && a.EntityType == nameof(UsageEvent) && a.NewValues != null
                && (a.NewValues.Contains("\"awaitingCreditNote\":true") || a.NewValues.Contains("\"freeSlotOnly\":true")))
            .Select(a => a.EntityId).ToListAsync(ct);
        var ids = rows.Select(r => Guid.TryParse(r, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        var events = await db.UsageEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.CompanyId, u.BilledDocumentId, u.BilledPeriod, u.ChargedAmount }).ToListAsync(ct);
        var companyIds = events.Select(e => e.CompanyId).Distinct().ToList();
        var names = await db.Companies.IgnoreQueryFilters().AsNoTracking().Where(c => companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return events
            .GroupBy(e => (e.BilledDocumentId, e.CompanyId, e.BilledPeriod))
            .Select(g => new AwaitingCreditNote(g.Key.BilledDocumentId, g.Key.CompanyId, names.GetValueOrDefault(g.Key.CompanyId), g.Key.BilledPeriod,
                g.Count(), g.Sum(e => e.ChargedAmount)))
            .OrderByDescending(x => x.Amount).ToList();
    }

    /// <summary>งานครั้งเดียวตอนบูต — marker มีแล้ว = ไม่ทำอะไร · ล้ม ⇒ ธุรกรรมย้อน ไม่มี marker ⇒ บูตถัดไปทำใหม่</summary>
    public static async Task<Result> RunAsync(AccountingDbContext db, CancellationToken ct = default)
    {
        try { return await RunCoreAsync(db, ct); }
        finally { db.ChangeTracker.Clear(); }
    }

    private static async Task<Result> RunCoreAsync(AccountingDbContext db, CancellationToken ct)
    {
        var empty = new Result(0, 0, 0, 0m, 0m, 0);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(AdvisoryLockKey.OneShotDataFix, MarkerKey) }, ct);
        await db.Database.ExecuteSqlRawAsync(Accounting.Data.DatabaseMigrationHelper.OneShotDataFixesTableSql, ct);
        var markerKey = MarkerKey;
        var done = await db.Database
            .SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "OneShotDataFixes" WHERE "Key" = {markerKey}""")
            .SingleAsync(ct);
        if (done > 0) { await tx.CommitAsync(ct); return empty; }
        // M5: ล็อกงวดเดียวกับ UsageInvoicingJob — เครื่องอื่นออกบิลไม่ได้ระหว่างถอด/คืน
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(AdvisoryLockKey.UsageInvoicing, AddOnBilling.PeriodOf(DateTime.UtcNow)) }, ct);

        // L3: กรองฝั่ง SQL ก่อนดึง — เฉพาะแถวที่สแกน (บริษัทเดียวกัน) ไม่สำเร็จ/ใช้ผลเดิม/อ่านจาก XML · ตัวตัดสินจริงยังเป็น Decide (ShouldRefund) ข้างล่าง
        var completed = OcrQuotaRefundRule.CompletedStatus;
        var cached = OcrDuplicateReusePolicy.CachedEngine;
        var xml = OcrDuplicateReusePolicy.EtaxXmlEngine;
        var events = await db.UsageEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.FeatureCode == FeatureCode && u.RefEntityType == RefEntityType && u.RefEntityId != null && !u.IsDeleted
                && db.OcrScanResults.Any(s => s.Id == u.RefEntityId && s.CompanyId == u.CompanyId
                    && (s.ScanStatus != completed || s.OcrEngine == cached || s.OcrEngine == xml)))
            .ToListAsync(ct);
        var scanIds = events.Select(e => e.RefEntityId!.Value).Distinct().ToList();
        var scans = (await db.OcrScanResults.IgnoreQueryFilters().AsNoTracking()
                .Where(s => scanIds.Contains(s.Id))
                .Select(s => new { s.Id, s.CompanyId, s.ScanStatus, s.OcrEngine })
                .ToListAsync(ct))
            .ToDictionary(s => s.Id);
        var eventIds = events.Select(e => e.Id.ToString()).ToList();
        var alreadyDone = (await db.AuditLogs.AsNoTracking()
                .Where(a => a.EntityType == nameof(UsageEvent) && a.UserEmail == Actor && eventIds.Contains(a.EntityId))
                .Select(a => a.EntityId).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accountIds = events.Where(e => e.BillingAccountId != null).Select(e => e.BillingAccountId!.Value).Distinct().ToList();
        var prepaidAccounts = (await db.BillingAccounts.IgnoreQueryFilters().AsNoTracking()
                .Where(a => accountIds.Contains(a.Id) && a.PaymentModel == PaymentModel.Prepaid).Select(a => a.Id).ToListAsync(ct))
            .ToHashSet();

        var now = DateTime.UtcNow;
        int removed = 0, credited = 0, needsCn = 0; decimal creditedAmt = 0m, cnAmt = 0m;
        var perCompany = new Dictionary<Guid, Agg>();
        foreach (var ev in events)
        {
            if (alreadyDone.Contains(ev.Id.ToString())) continue;
            // กันข้ามบริษัท: สแกนต้องเป็นของบริษัทเดียวกับแถวค่าบริการ (กฎ M)
            var found = scans.TryGetValue(ev.RefEntityId!.Value, out var scan) && scan!.CompanyId == ev.CompanyId;
            var billed = ev.BilledPeriod != null || ev.BilledDocumentId != null;
            var prepaid = ev.BillingAccountId is Guid aid && prepaidAccounts.Contains(aid);
            var action = Decide(found, found ? scan!.ScanStatus : null, found ? scan!.OcrEngine : null, ev.ChargedAmount, ev.IsSandbox,
                billed, ev.BilledDocumentId != null, prepaid);
            // M2: แถวล้มที่กินโควตาฟรีของเดือนที่ปิดรอบแล้ว (ไม่มีอะไรให้ถอด/คืน) — ยังต้องนับให้เห็น
            var freeSlotOnly = action == OcrFailedScanRefundAction.None && ev.CoveredByFreeQuota && found && !ev.IsSandbox
                && OcrQuotaRefundRule.ShouldRefund(scan!.ScanStatus, scan.OcrEngine);
            if (action == OcrFailedScanRefundAction.None && !freeSlotOnly) continue;

            var agg = perCompany.GetValueOrDefault(ev.CompanyId) ?? ZeroAgg;
            if (ev.CoveredByFreeQuota) agg = agg with { FreeSlots = agg.FreeSlots + 1 };
            var removedNow = false;
            if (action is OcrFailedScanRefundAction.RemoveUnbilled or OcrFailedScanRefundAction.CreditPrepaidAndRemove)
            {
                // M5: ถอดเฉพาะแถวที่ยังไม่มีใบ (เงื่อนไขอยู่ในคำสั่งเดียว) · หลังจ่ายต้องยังไม่ปิดรอบด้วย · 0 แถว = ออกบิลไปแล้ว ⇒ ไม่อ้างว่า "ไม่ถูกเรียกเก็บ"
                var allowClosedPeriod = action == OcrFailedScanRefundAction.CreditPrepaidAndRemove;
                var rows = await db.Database.ExecuteSqlRawAsync(
                    """UPDATE "UsageEvents" SET "IsDeleted" = true, "UpdatedAt" = {0}, "UpdatedBy" = {1} WHERE "Id" = {2} AND "CompanyId" = {3} AND "IsDeleted" = false AND "BilledDocumentId" IS NULL AND ("BilledPeriod" IS NULL OR {4})""",
                    new object[] { now, Actor, ev.Id, ev.CompanyId, allowClosedPeriod }, ct);
                removedNow = rows == 1;
                if (!removedNow)
                    action = action == OcrFailedScanRefundAction.CreditPrepaidAndRemove ? OcrFailedScanRefundAction.CreditPrepaid
                        : ev.ChargedAmount > 0m ? OcrFailedScanRefundAction.NeedsCreditNote : OcrFailedScanRefundAction.None;
            }
            switch (action)
            {
                case OcrFailedScanRefundAction.RemoveUnbilled:
                    removed++; agg = agg with { Removed = agg.Removed + 1 };
                    break;
                case OcrFailedScanRefundAction.CreditPrepaidAndRemove:
                case OcrFailedScanRefundAction.CreditPrepaid:
                    // M5: บวกเพิ่มในคำสั่งเดียว (ไม่ทับค่าที่เครื่องอื่นตัดเครดิตระหว่างนี้)
                    await db.Database.ExecuteSqlRawAsync("""UPDATE "BillingAccounts" SET "CreditBalance" = "CreditBalance" + {0} WHERE "Id" = {1}""",
                        new object[] { ev.ChargedAmount, ev.BillingAccountId!.Value }, ct);
                    credited++; creditedAmt += ev.ChargedAmount;
                    agg = agg with { Credited = agg.Credited + 1, CreditedAmt = agg.CreditedAmt + ev.ChargedAmount };
                    break;
                case OcrFailedScanRefundAction.NeedsCreditNote:
                    needsCn++; cnAmt += ev.ChargedAmount;
                    agg = agg with { Cn = agg.Cn + 1, CnAmt = agg.CnAmt + ev.ChargedAmount };
                    break;
            }
            perCompany[ev.CompanyId] = agg;
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = ev.CompanyId, UserEmail = Actor, Action = AuditAction.Update, EntityType = nameof(UsageEvent), EntityId = ev.Id.ToString(),
                OldValues = System.Text.Json.JsonSerializer.Serialize(new { chargedAmount = ev.ChargedAmount, billedPeriod = ev.BilledPeriod, billedDocumentId = ev.BilledDocumentId, coveredByFreeQuota = ev.CoveredByFreeQuota }),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    @event = MarkerKey, action = action.ToString(), scanId = ev.RefEntityId, scanStatus = scan?.ScanStatus, ocrEngine = scan?.OcrEngine,
                    refunded = action is OcrFailedScanRefundAction.CreditPrepaid or OcrFailedScanRefundAction.CreditPrepaidAndRemove ? ev.ChargedAmount : 0m,
                    removedFromBilling = removedNow,
                    awaitingCreditNote = action == OcrFailedScanRefundAction.NeedsCreditNote,
                    freeSlotOnly = action == OcrFailedScanRefundAction.None,
                    ruleCode = RuleCode, legalReference = LegalReference,
                }),
                Timestamp = now,
            });
        }

        // L4: กระดิ่งหนึ่งครั้งต่อเจ้าของ (รวมทุกบริษัทที่เป็นเจ้าของ)
        var companyIds = perCompany.Keys.ToList();
        var owners = await db.CompanyUsers.AsNoTracking()
            .Where(cu => companyIds.Contains(cu.CompanyId) && cu.Role == UserRole.Owner).Select(cu => new { cu.UserId, cu.CompanyId }).ToListAsync(ct);
        foreach (var g in owners.GroupBy(o => o.UserId))
        {
            var mine = g.Select(o => o.CompanyId).Distinct().OrderBy(c => c).ToList();
            var sum = mine.Select(c => perCompany[c]).Aggregate(ZeroAgg, Plus);
            db.Set<Notification>().Add(new Notification
            {
                UserId = g.Key, CompanyId = mine[0], Type = NotificationType.System, Channel = NotificationChannel.InApp,
                Title = sum.Cn > 0 ? "ค่าสแกน API ที่ไม่ควรถูกคิด — จัดการแล้วบางส่วน ส่วนที่อยู่ในใบแจ้งหนี้รอใบลดหนี้" : "ล้างค่าสแกน API ที่ไม่ควรถูกคิดแล้ว",
                Message = OwnerMessage(sum, mine.Count), ActionUrl = "/pages/usage.html", EntityType = nameof(UsageEvent),
            });
        }
        // M4: ผู้ให้บริการต้องเห็น — กระดิ่งถึงแอดมินแพลตฟอร์ม (กันซ้ำต่อคนด้วยชนิดเรื่อง)
        var allCn = perCompany.Values.Sum(a => a.Cn);
        var allFree = perCompany.Values.Sum(a => a.FreeSlots);
        if (allCn > 0 || allFree > 0)
        {
            var admins = await db.Users.AsNoTracking().Where(u => u.IsSystemAdmin && !u.IsDeleted).Select(u => u.Id).ToListAsync(ct);
            var notified = (await db.Notifications.AsNoTracking()
                    .Where(n => n.EntityType == AdminNotificationEntityType && admins.Contains(n.UserId)).Select(n => n.UserId).ToListAsync(ct))
                .ToHashSet();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var admin in admins.Where(a => !notified.Contains(a)))
                db.Set<Notification>().Add(new Notification
                {
                    UserId = admin, Type = NotificationType.System, Channel = NotificationChannel.InApp,
                    Title = "ค่าสแกน API ที่ไม่ควรถูกคิด — รอออกใบลดหนี้",
                    Message = $"ค่าสแกนที่อยู่ในใบแจ้งหนี้แล้ว {allCn} ครั้ง {cnAmt.ToString("N2", inv)} บาท · สแกนล้มที่กินโควตาฟรี {allFree} ครั้ง — "
                        + "ดูรายการที่ api/admin/platform-billing/ocr-v1-credit-notes แล้วออกใบลดหนี้ (ม.86/10)",
                    EntityType = AdminNotificationEntityType,
                });
        }
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            """INSERT INTO "OneShotDataFixes" ("Key", "AppliedAt") VALUES ({0}, (now() at time zone 'utc')) ON CONFLICT DO NOTHING""",
            new object[] { MarkerKey }, ct);
        await tx.CommitAsync(ct);
        return new Result(removed, credited, needsCn, creditedAmt, cnAmt, perCompany.Count);
    }
}

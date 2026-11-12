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
    /// <summary>เติมเงิน (prepaid) แถวยังไม่ปิดรอบ ⇒ คืนเครดิต + ถอดแถวออกจากรอบ</summary>
    CreditPrepaidAndRemove,
    /// <summary>เติมเงิน แถวปิดรอบแล้ว ⇒ คืนเครดิต (แถวคงไว้เป็นประวัติ)</summary>
    CreditPrepaid,
    /// <summary>หลังจ่าย แถวเข้าใบแจ้งหนี้/ใบกำกับไปแล้ว ⇒ ระบบคืนเองไม่ได้ (ใบที่ออกแล้วแก้ไม่ได้ — ต้องออกใบลดหนี้ ม.86/10) ⇒ บันทึก + บอกเจ้าของ · <b>ไม่ประทับว่าคืนแล้ว</b></summary>
    NeedsCreditNote,
}

/// <summary>
/// **คืนค่าบริการสแกน API v1 ที่ถูกคิดก่อนรอบ 258 ทั้งที่สแกนไม่ได้ใช้เครื่องอ่านจริง/ล้ม — คำตัดสิน 260-5-2**
///
/// <para>═══ ที่มา ═══ team-C C-H3 (erp-review/2026-11-11/team-C-ocr.md) · รอบ 258 (คำตัดสิน 258-2-8 ข้อ 2) หยุดคิดเงินสแกนที่
/// <see cref="OcrQuotaRefundRule.ShouldRefund"/> ตอบว่าไม่ได้ใช้เครื่องอ่าน (ล้ม · คัดลอกผลเดิม · อ่านจาก e-Tax XML) แต่แถว <c>UsageEvents</c>
/// ที่คิดไปแล้วยังอยู่ (F2 ข้อ 9 — แก้โค้ดอย่างเดียวไม่พอเมื่อของเสียถูกเก็บไว้แล้ว) · เงินที่เก็บโดยไม่มีบริการ = ลาภมิควรได้ (ป.พ.พ. ม.406)
/// ⇒ ต้องคืน</para>
///
/// <para>═══ กติกา ═══ ตัวตัดสินสแกนตัวเดียวกับเส้นเว็บ/LINE/v1 (<see cref="OcrQuotaRefundRule.ShouldRefund"/> — ไม่มีสูตรที่สอง) ·
/// <c>UsageEvent</c> เป็นหลักฐานบิล append-only: แถวที่ยังไม่เข้าใบถอดออกจากรอบได้ (แบบเดียวกับ <c>NotifyQuotaService</c>) · แถวที่เข้าใบแล้ว
/// ไม่แก้ — เติมเงินคืนเครดิต · หลังจ่ายต้องออกใบลดหนี้โดยผู้ให้บริการ (บอกเจ้าของบริษัทว่า "ยังไม่ได้คืน" ไม่ประทับว่าคืนแล้ว · F2 ข้อ 3)</para>
///
/// <para>═══ ไม่คืนซ้ำ ═══ (1) marker <see cref="MarkerKey"/> + <c>pg_advisory_xact_lock</c> ⇒ ทั้งงานครั้งเดียว ·
/// (2) ต่อแถว: แถว audit <see cref="Actor"/> ของแถวนั้นมีแล้ว = ข้าม (marker ถูกลบ/รันซ้ำก็ไม่คืนเครดิตสองครั้ง) · แถวที่ถอดแล้ว (IsDeleted) ไม่ถูกเลือก</para>
///
/// <para>═══ ขอบเขตที่ตั้งใจ ═══ แถวที่สำเร็จจริงซึ่งถูกคิดเงินเพราะโควตาฟรีของเดือนถูกแถวล้มกินไปก่อน — ไม่คำนวณราคาย้อนหลัง (ราคาถูก snapshot ต่อแถว ·
/// ใบที่ออกแล้วแก้ไม่ได้) แต่<b>นับให้เห็น</b> (<c>freeSlotsUsedByFailed</c> ใน audit + ข้อความถึงเจ้าของ) ให้ผู้ให้บริการพิจารณาลดหนี้</para>
/// </summary>
public static class OcrFailedScanRefund
{
    /// <summary>คีย์ marker ใน <c>OneShotDataFixes</c></summary>
    public const string MarkerKey = "r260-5-ocr-v1-failed-scan-refund";
    /// <summary>ผู้กระทำบนแถว audit (ใช้เป็นกุญแจกันคืนซ้ำต่อแถวด้วย)</summary>
    public const string Actor = "system:ocr-v1-refund";
    /// <summary>รหัสฟีเจอร์ของสแกน API v1 (ตัวเดียวกับ <c>OcrV1Controller.Feature</c>)</summary>
    public const string FeatureCode = "ocr.scan";
    /// <summary>ชนิดอ้างอิงของแถวสแกน (ตัวเดียวกับที่ <c>OcrV1Controller</c> ส่งให้ <c>MeterAsync</c>)</summary>
    public const string RefEntityType = "OcrScanResult";
    public const string RuleCode = "OCR-V1-REFUND";
    public const string LegalReference = "ป.พ.พ. ม.406 (ลาภมิควรได้) · ป.รัษฎากร ม.86/10 (ใบลดหนี้) · คำตัดสิน 258-2-8 · 260-5-2";

    /// <summary>ตัดสินทางคืนของแถวหนึ่ง (pure)</summary>
    /// <param name="scanFound">พบแถวสแกนของบริษัทเดียวกัน — ไม่พบ = ไม่รู้ว่าสแกนล้มไหม ⇒ ไม่แตะ (ไม่เดา)</param>
    /// <param name="billed">แถวปิดรอบบิลแล้ว (<c>BilledPeriod</c> หรือ <c>BilledDocumentId</c> มีค่า)</param>
    /// <param name="prepaid">กลุ่มผู้จ่ายเป็นแบบเติมเงิน (ตัดเครดิตตอนบันทึก)</param>
    public static OcrFailedScanRefundAction Decide(bool scanFound, string? scanStatus, string? ocrEngine,
        decimal chargedAmount, bool isSandbox, bool billed, bool prepaid)
    {
        if (!scanFound || isSandbox) return OcrFailedScanRefundAction.None;
        if (!OcrQuotaRefundRule.ShouldRefund(scanStatus, ocrEngine)) return OcrFailedScanRefundAction.None;
        if (prepaid && chargedAmount > 0m)
            return billed ? OcrFailedScanRefundAction.CreditPrepaid : OcrFailedScanRefundAction.CreditPrepaidAndRemove;
        if (!billed) return OcrFailedScanRefundAction.RemoveUnbilled;
        return chargedAmount > 0m ? OcrFailedScanRefundAction.NeedsCreditNote : OcrFailedScanRefundAction.None;
    }

    /// <summary>ผลของงานต่อทั้งฐาน (ตัวเลขสำหรับ log)</summary>
    public sealed record Result(int Removed, int Credited, int NeedsCreditNote, decimal CreditedAmount, decimal CreditNoteAmount, int Companies);

    /// <summary>ข้อความถึงเจ้าของบริษัท (pure — เทสต์ล็อกว่าไม่อ้างว่าคืนส่วนที่ยังไม่ได้คืน)</summary>
    private static string OwnerMessage(int removed, int credited, decimal creditedAmount, int needsCreditNote, decimal creditNoteAmount, int freeSlotsUsedByFailed)
    {
        var parts = new List<string>();
        if (removed > 0) parts.Add($"ถอดค่าสแกนที่ยังไม่เข้าใบแจ้งหนี้ {removed} ครั้งออกจากรอบบิลแล้ว (ไม่ถูกเรียกเก็บ)");
        if (credited > 0) parts.Add($"คืนเครดิตเติมเงิน {creditedAmount.ToString("N2", System.Globalization.CultureInfo.InvariantCulture)} บาท ({credited} ครั้ง) แล้ว");
        if (needsCreditNote > 0)
            parts.Add($"ค่าสแกน {needsCreditNote} ครั้ง {creditNoteAmount.ToString("N2", System.Globalization.CultureInfo.InvariantCulture)} บาท อยู่ในใบแจ้งหนี้ที่ออกไปแล้ว — ยังไม่ได้คืน ผู้ให้บริการต้องออกใบลดหนี้ให้");
        if (freeSlotsUsedByFailed > 0)
            parts.Add($"สแกนที่ล้ม {freeSlotsUsedByFailed} ครั้งเคยกินโควตาฟรีของเดือนนั้น — ผู้ให้บริการจะตรวจว่าค่าสแกนที่สำเร็จในเดือนเดียวกันต้องลดหนี้เพิ่มหรือไม่");
        return "สแกนผ่าน API ที่อ่านไม่สำเร็จ/ใช้ผลเดิม/อ่านจาก e-Tax XML ไม่ควรถูกคิดค่าบริการ (แก้แล้วตั้งแต่รอบ 258): " + string.Join(" · ", parts);
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

        var events = await db.UsageEvents.IgnoreQueryFilters()
            .Where(u => u.FeatureCode == FeatureCode && u.RefEntityType == RefEntityType && u.RefEntityId != null && !u.IsDeleted)
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
        var accounts = await db.BillingAccounts.IgnoreQueryFilters()
            .Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

        var now = DateTime.UtcNow;
        int removed = 0, credited = 0, needsCn = 0; decimal creditedAmt = 0m, cnAmt = 0m;
        var perCompany = new Dictionary<Guid, (int Removed, int Credited, decimal CreditedAmt, int Cn, decimal CnAmt, int FreeSlots)>();
        foreach (var ev in events)
        {
            if (alreadyDone.Contains(ev.Id.ToString())) continue;
            // กันข้ามบริษัท: สแกนต้องเป็นของบริษัทเดียวกับแถวค่าบริการ (กฎ M)
            var found = scans.TryGetValue(ev.RefEntityId!.Value, out var scan) && scan!.CompanyId == ev.CompanyId;
            var account = ev.BillingAccountId is Guid aid && accounts.TryGetValue(aid, out var acc) ? acc : null;
            var billed = ev.BilledPeriod != null || ev.BilledDocumentId != null;
            var prepaid = account is { PaymentModel: PaymentModel.Prepaid };
            var action = Decide(found, found ? scan!.ScanStatus : null, found ? scan!.OcrEngine : null, ev.ChargedAmount, ev.IsSandbox, billed, prepaid);
            if (action == OcrFailedScanRefundAction.None) continue;

            perCompany.TryGetValue(ev.CompanyId, out var agg);
            if (ev.CoveredByFreeQuota) agg.FreeSlots++;
            switch (action)
            {
                case OcrFailedScanRefundAction.RemoveUnbilled:
                    ev.IsDeleted = true; ev.UpdatedAt = now; ev.UpdatedBy = Actor;
                    removed++; agg.Removed++;
                    break;
                case OcrFailedScanRefundAction.CreditPrepaidAndRemove:
                case OcrFailedScanRefundAction.CreditPrepaid:
                    account!.CreditBalance += ev.ChargedAmount;
                    if (action == OcrFailedScanRefundAction.CreditPrepaidAndRemove) { ev.IsDeleted = true; ev.UpdatedAt = now; ev.UpdatedBy = Actor; }
                    credited++; creditedAmt += ev.ChargedAmount; agg.Credited++; agg.CreditedAmt += ev.ChargedAmount;
                    break;
                case OcrFailedScanRefundAction.NeedsCreditNote:
                    needsCn++; cnAmt += ev.ChargedAmount; agg.Cn++; agg.CnAmt += ev.ChargedAmount;
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
                    removedFromBilling = action is OcrFailedScanRefundAction.RemoveUnbilled or OcrFailedScanRefundAction.CreditPrepaidAndRemove,
                    awaitingCreditNote = action == OcrFailedScanRefundAction.NeedsCreditNote,
                    ruleCode = RuleCode, legalReference = LegalReference,
                }),
                Timestamp = now,
            });
        }

        foreach (var (companyId, agg) in perCompany)
        {
            var owners = await db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == companyId && cu.Role == UserRole.Owner).Select(cu => cu.UserId).ToListAsync(ct);
            var message = OwnerMessage(agg.Removed, agg.Credited, agg.CreditedAmt, agg.Cn, agg.CnAmt, agg.FreeSlots);
            foreach (var owner in owners.Distinct())
                db.Set<Notification>().Add(new Notification
                {
                    UserId = owner, CompanyId = companyId, Type = NotificationType.System, Channel = NotificationChannel.InApp,
                    Title = agg.Cn > 0 ? "ค่าสแกน API ที่ไม่ควรถูกคิด — คืนแล้วบางส่วน ส่วนที่อยู่ในใบแจ้งหนี้รอใบลดหนี้" : "คืนค่าสแกน API ที่ไม่ควรถูกคิดแล้ว",
                    Message = message, ActionUrl = "/pages/usage.html", EntityType = nameof(UsageEvent),
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

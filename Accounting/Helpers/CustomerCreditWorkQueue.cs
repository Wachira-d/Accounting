using System.Globalization;
using Accounting.Data;
using Accounting.Models.DTOs.Autopilot;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **คิวงานของเงินรอจัดสรร — ตัวอ่านตัวเดียว** (รอบ 258 H2 + รอบ 259 P6) ที่หน้า "เงินรอจัดสรร" และ Autopilot (เรื่องที่ระบบทำต่อไม่ได้) ใช้ร่วมกัน
/// (ตัวเลขเดียวกันทั้งสองหน้า — ไม่มีสำเนา query)
/// <list type="bullet">
/// <item><see cref="ReceiptPendingAsync"/> — ใบที่ปิดยอดด้วยการรับชำระที่จัดสรรแล้ว<b>ออกใบเสร็จอัตโนมัติไม่ได้</b> (ป้าย <see cref="PaymentExcessPlan.ReceiptPendingMarker"/>)</item>
/// <item><see cref="LegacyMissingReceiptsAsync"/> — ใบที่ปิดยอดด้วยการรับชำระหลายใบ/เงินเกิน<b>ก่อนรอบ 258</b> (ไม่มีป้าย เพราะตอนนั้นยังไม่ออกใบเสร็จรายใบ)
/// ที่ยังไม่มีใบเสร็จ — หาจาก "แถวจัดสรรที่ไม่มีใบเสร็จลูก" (ไม่มี migration ประทับ: ให้คนกดออกทีละใบผ่านเส้นเดิม)</item>
/// <item><see cref="AgedAsync"/> — เงินรอจัดสรรค้างเกิน <see cref="AgingDays"/> วัน</item>
/// </list>
/// <para>อ่านอย่างเดียว · ทุก query ผูก CompanyId</para>
/// </summary>
public static class CustomerCreditWorkQueue
{
    /// <summary>เงินรอจัดสรรค้างนานเกินนี้ ⇒ ขึ้นเรื่องที่ต้องจัดการใน Autopilot</summary>
    public const int AgingDays = 30;
    public const int Cap = 500;

    /// <summary>ใบที่มีป้าย [RECEIPT-PENDING] และคู่ (การชำระ, ใบ) ยังไม่มีใบเสร็จที่มีผล</summary>
    public static async Task<List<CustomerCreditReceiptPendingRow>> ReceiptPendingAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var marker = PaymentExcessPlan.ReceiptPendingMarker;
        var flagged = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.Status != DocumentStatus.Voided
                && d.InternalNotes != null && d.InternalNotes.Contains(marker))
            .OrderBy(d => d.DocumentDate)
            .Select(d => new { d.Id, d.DocumentNumber, d.ContactId })
            .Take(Cap).ToListAsync(ct);
        if (flagged.Count == 0) return new List<CustomerCreditReceiptPendingRow>();
        return await PairsWithoutReceiptAsync(db, companyId, flagged.Select(d => (d.Id, d.DocumentNumber, d.ContactId)).ToList(), lateOnly: false, ct);
    }

    /// <summary>
    /// รอบ 259 P6 (ครั้งเดียว — อ่านอย่างเดียว): ใบฝั่งขายที่ถูกปิดยอดด้วย "แถวจัดสรร" ของการรับชำระหลายใบ/เงินเกินที่จัดสรรพร้อมรับเงิน
    /// และยังไม่มีใบเสร็จรายใบ · ไม่รวมใบที่มีป้าย [RECEIPT-PENDING] (อยู่ใน <see cref="ReceiptPendingAsync"/> แล้ว) · ใบที่สถานะยังไม่ปิดยอดไม่รวม
    /// </summary>
    public static async Task<List<CustomerCreditReceiptPendingRow>> LegacyMissingReceiptsAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var marker = PaymentExcessPlan.ReceiptPendingMarker;
        var cutover = await Round258CutoverAsync(db, ct);
        var types = new[] { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.DebitNote }.Where(SettlementReceiptPolicy.IsReceivableSource).ToList();
        var docs = await (
            from a in db.PaymentAllocations.AsNoTracking()
            join p in db.Payments.AsNoTracking() on a.PaymentId equals p.Id
            join d in db.Documents.AsNoTracking() on a.DocumentId equals d.Id
            where a.CompanyId == companyId && p.CompanyId == companyId && d.CompanyId == companyId
                  && !a.IsDeleted && !p.IsDeleted && !d.IsDeleted && !a.IsLateAllocation && p.SettlementBatchId == null && p.CreatedAt < cutover
                  && types.Contains(d.DocumentType) && d.Status == DocumentStatus.Paid
                  && (d.InternalNotes == null || !d.InternalNotes.Contains(marker))
            select new { d.Id, d.DocumentNumber, d.ContactId, d.DocumentDate })
            .Distinct().OrderBy(x => x.DocumentDate).ThenBy(x => x.DocumentNumber).Take(Cap).ToListAsync(ct);
        if (docs.Count == 0) return new List<CustomerCreditReceiptPendingRow>();
        return await PairsWithoutReceiptAsync(db, companyId, docs.Select(d => (d.Id, d.DocumentNumber, d.ContactId)).ToList(), lateOnly: false, ct);
    }

    /// <summary>วันที่ใช้รอบ 258 จริงของฐานนี้ (เวลาที่ backfill รอบ 258 รัน — marker <see cref="CustomerCreditMigration.BackfillMarkerKey"/>) ·
    /// ฐานที่ไม่มีตาราง marker (ฐานเทสต์ที่สร้างจากโมเดล) ⇒ <see cref="Round258FallbackCutoverUtc"/> · การรับชำระหลังจากนี้ออกใบเสร็จรายใบ/ติดป้ายเองแล้ว</summary>
    private static async Task<DateTime> Round258CutoverAsync(AccountingDbContext db, CancellationToken ct)
    {
        try
        {
            var at = await db.Database.SqlQueryRaw<DateTime>(
                    "SELECT \"AppliedAt\" AS \"Value\" FROM \"OneShotDataFixes\" WHERE \"Key\" = {0}", CustomerCreditMigration.BackfillMarkerKey)
                .ToListAsync(ct);
            if (at.Count > 0) return at[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* ไม่มีตาราง marker (ฐานจากโมเดล) — ใช้วันที่ตั้งต้นด้านล่าง */ }
        return Round258FallbackCutoverUtc;
    }

    /// <summary>วันส่งรอบ 258 (เมื่อไม่มี marker)</summary>
    public static readonly DateTime Round258FallbackCutoverUtc = new(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>คู่ (การชำระ, ใบ) ของใบที่ให้มา ที่ยังไม่มีใบเสร็จที่มีผล (ใบเสร็จรายใบของการชำระ หรือใบเสร็จหลักของการชำระที่ชี้ใบนั้น)</summary>
    private static async Task<List<CustomerCreditReceiptPendingRow>> PairsWithoutReceiptAsync(AccountingDbContext db, Guid companyId,
        List<(Guid Id, string Number, Guid ContactId)> docs, bool lateOnly, CancellationToken ct)
    {
        var docIds = docs.Select(d => d.Id).ToList();
        var pairs = await (
            from a in db.PaymentAllocations.AsNoTracking()
            join p in db.Payments.AsNoTracking() on a.PaymentId equals p.Id
            where a.CompanyId == companyId && p.CompanyId == companyId && !a.IsDeleted && !p.IsDeleted && docIds.Contains(a.DocumentId)
                  && (!lateOnly || a.IsLateAllocation)
            select new { a.PaymentId, p.PaymentNumber, a.DocumentId, a.AllocatedAmount, p.ReceiptDocumentId }).ToListAsync(ct);
        var payIds = pairs.Select(x => x.PaymentId).Distinct().ToList();
        var receipts = await db.Documents.AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted && r.Status != DocumentStatus.Voided && r.Status != DocumentStatus.Rejected
                && r.SettlementPaymentId != null && payIds.Contains(r.SettlementPaymentId.Value) && r.RelatedDocumentId != null)
            .Select(r => new { r.Id, PaymentId = r.SettlementPaymentId!.Value, DocumentId = r.RelatedDocumentId!.Value })
            .ToListAsync(ct);
        var done = receipts.Select(r => (r.PaymentId, r.DocumentId)).ToHashSet();
        var primaryReceiptIds = pairs.Where(x => x.ReceiptDocumentId != null).Select(x => x.ReceiptDocumentId!.Value).Distinct().ToList();
        var primary = primaryReceiptIds.Count == 0 ? new Dictionary<Guid, Guid?>() : await db.Documents.AsNoTracking()
            .Where(r => r.CompanyId == companyId && primaryReceiptIds.Contains(r.Id) && !r.IsDeleted && r.Status != DocumentStatus.Voided)
            .ToDictionaryAsync(r => r.Id, r => r.RelatedDocumentId, ct);
        var contactIds = docs.Select(d => d.ContactId).Distinct().ToList();
        var names = await db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var byId = docs.ToDictionary(d => d.Id);
        return pairs.GroupBy(x => (x.PaymentId, x.PaymentNumber, x.DocumentId, x.ReceiptDocumentId))
            .Where(g => !done.Contains((g.Key.PaymentId, g.Key.DocumentId))
                && !(g.Key.ReceiptDocumentId is Guid rid && primary.TryGetValue(rid, out var rel) && rel == g.Key.DocumentId))
            .Select(g =>
            {
                var d = byId[g.Key.DocumentId];
                return new CustomerCreditReceiptPendingRow(d.Id, d.Number, names.TryGetValue(d.ContactId, out var n) ? n : null,
                    g.Key.PaymentId, g.Key.PaymentNumber, g.Sum(x => x.AllocatedAmount));
            }).ToList();
    }

    /// <summary>เงินรอจัดสรรที่ค้างนานกว่า <see cref="AgingDays"/> วัน (เก่าสุดก่อน)</summary>
    public static async Task<List<(Guid PaymentId, string PaymentNumber, DateTime PaymentDate, decimal Unapplied, string? Reason)>> AgedAsync(
        AccountingDbContext db, Guid companyId, DateTime todayThai, CancellationToken ct = default)
    {
        var cut = todayThai.Date.AddDays(-AgingDays);
        var rows = await db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.UnappliedAccountId != null && !p.UnappliedNeedsReview
                && p.UnappliedAmount > UnappliedCreditBalance.Tolerance && p.PaymentDate < cut)
            .OrderBy(p => p.PaymentDate).Take(Cap)
            .Select(p => new { p.Id, p.PaymentNumber, p.PaymentDate, p.UnappliedAmount, p.UnappliedReason })
            .ToListAsync(ct);
        return rows.Select(r => (r.Id, r.PaymentNumber, r.PaymentDate, r.UnappliedAmount, r.UnappliedReason)).ToList();
    }

    /// <summary>เรื่องที่ระบบทำต่อไม่ได้ของ Autopilot: ใบที่ต้องออกใบเสร็จ · เงินรอจัดสรรค้างเกิน 30 วัน — ลิงก์ไปหน้า "เงินรอจัดสรร"</summary>
    public static async Task<List<AutopilotIssueDto>> AutopilotIssuesAsync(AccountingDbContext db, Guid companyId, DateTime nowUtc, CancellationToken ct = default)
    {
        var list = new List<AutopilotIssueDto>();
        const string page = "/pages/customer-credits.html";
        foreach (var r in (await ReceiptPendingAsync(db, companyId, ct)).Take(50))
            list.Add(new AutopilotIssueDto("ReceiptPending", $"{r.DocumentNumber}: ปิดยอดแล้วแต่ยังไม่ได้ออกใบเสร็จรับเงิน/ใบกำกับภาษี",
                $"{r.ContactName ?? ""} · การรับชำระ {r.PaymentNumber} · {r.Amount.ToString("#,##0.00", CultureInfo.InvariantCulture)} บาท — ออกใบเสร็จที่หน้า “เงินรอจัดสรร”",
                nowUtc, r.DocumentId, "เปิดเงินรอจัดสรร", page, null, "เปิดเอกสาร", "/pages/documents.html?openDoc=" + r.DocumentId));
        var today = ThaiDate.CalendarDateUtc(nowUtc).Date;
        foreach (var a in (await AgedAsync(db, companyId, today, ct)).Take(50))
            list.Add(new AutopilotIssueDto("CustomerCreditAged",
                $"เงินรอจัดสรร {a.PaymentNumber} ค้างเกิน {AgingDays} วัน ({a.Unapplied.ToString("#,##0.00", CultureInfo.InvariantCulture)} บาท)",
                $"{PaymentExcessPlan.LabelOf(a.Reason)} · รับเมื่อ {a.PaymentDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} — จัดสรรให้ใบ · ออกใบส่วนที่ขาด · ย้ายเป็นมัดจำ · หรือคืนเงิน",
                DateTime.SpecifyKind(a.PaymentDate, DateTimeKind.Utc), null, "เปิดเงินรอจัดสรร", page, null, null, null));
        return list;
    }
}

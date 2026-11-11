using Accounting.Data;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวอ่าน "เงินรอจัดสรรคงเหลือต่อลูกค้า" — ตัวเดียว** (รอบ 258) · ใช้โดยทุกหน้าที่ต้องรู้ว่าลูกค้ามีเงินที่รับแล้วแต่ยังไม่ผูกกับใบ
/// (ระงับทวงหนี้ · รายงานอายุลูกหนี้ · ใบแจ้งยอดลูกค้า · ปุ่ม "ใช้เงินรอจัดสรร" บนใบ) — นิยามเดียว:
/// การรับชำระที่ยังมีผล (ไม่ถูกยกเลิก) · มีผังพัก (<c>UnappliedAccountId</c>) · รู้เจ้าของ (<c>ContactId</c>) · ยอดคงเหลือ &gt; ครึ่งสตางค์ · tenant เสมอ
/// </summary>
public static class CustomerCreditQuery
{
    /// <summary>ยอดเงินรอจัดสรรคงเหลือรวมต่อผู้ติดต่อของบริษัท (ผู้ติดต่อที่ไม่มี = ไม่อยู่ในผล)</summary>
    public static async Task<Dictionary<Guid, decimal>> UnappliedByContactAsync(AccountingDbContext db, Guid companyId,
        CancellationToken ct = default)
    {
        var rows = await db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted
                && p.UnappliedAccountId != null && !p.UnappliedNeedsReview && p.ContactId != null && p.UnappliedAmount > UnappliedCreditBalance.Tolerance)
            .GroupBy(p => p.ContactId!.Value)
            .Select(g => new { ContactId = g.Key, Total = g.Sum(p => p.UnappliedAmount) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.ContactId, r => r.Total);
    }

    /// <summary>
    /// ในบรรดา <paramref name="references"/> (เลขอ้างอิงของสมุดรายวัน) ตัวไหนเป็น<b>เลขการรับชำระ</b>ของบริษัทนี้ — JE ที่ระบบสร้างและอ้างเลขการชำระตรง ๆ
    /// โดยไม่มีเอกสารต้นทาง = JE "เงินส่วนเกิน" ของการรับชำระหลายใบ (P-2: ห้ามถูกหยิบเป็น JV มัดจำ) · นับการชำระที่ถูกยกเลิกแล้วด้วย · tenant
    /// </summary>
    public static async Task<HashSet<string>> PaymentExcessReferencesAsync(AccountingDbContext db, Guid companyId,
        IEnumerable<string?> references, CancellationToken ct = default)
    {
        var refs = references.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!).Distinct().ToList();
        if (refs.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        var hits = await db.Payments.AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.CompanyId == companyId && refs.Contains(p.PaymentNumber))
            .Select(p => p.PaymentNumber)
            .ToListAsync(ct);
        return new HashSet<string>(hits, StringComparer.Ordinal);
    }

    /// <summary>ยอดเงินรอจัดสรรคงเหลือของผู้ติดต่อรายเดียว (0 = ไม่มี)</summary>
    public static async Task<decimal> UnappliedForContactAsync(AccountingDbContext db, Guid companyId, Guid contactId,
        CancellationToken ct = default)
        => await db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.ContactId == contactId
                && p.UnappliedAccountId != null && !p.UnappliedNeedsReview && p.UnappliedAmount > UnappliedCreditBalance.Tolerance)
            .SumAsync(p => (decimal?)p.UnappliedAmount, ct) ?? 0m;

    /// <summary>
    /// ฝ่ายค้านรอบ 258 M1 — ใบค้างของบริษัทที่ "เงินรอจัดสรรของลูกค้าพอปิด" (ใบ → ข้อความถึงทีมงาน) สำหรับการระงับเตือน/ทวง:
    /// ต่อผู้ติดต่อ เดินใบเก่าสุดก่อนแล้วหักเงินสะสม (<see cref="DunningSuppression.Plan"/>) · นับเฉพาะใบที่การจัดสรรผ่านตัวตัดสิน
    /// (<see cref="CustomerCreditAllocation.Check"/> — ชนิด/สถานะ/สกุลบาท/ด่านใบแจ้งหนี้มี VAT) <b>และ</b>มีสมุดรายวันตั้งลูกหนี้ (258-1-18) ·
    /// ตัวอ่านเดียวของ <c>EmailScheduleService</c> และ <c>OverdueDunningJob</c> · tenant
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> DunningHoldsAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var held = new Dictionary<Guid, string>();
        var credits = await db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.UnappliedAccountId != null && !p.UnappliedNeedsReview
                && p.ContactId != null && p.SettlementBatchId == null && p.UnappliedAmount > UnappliedCreditBalance.Tolerance)
            .Select(p => new { ContactId = p.ContactId!.Value, p.PaymentNumber, p.PaymentDate, p.UnappliedAmount })
            .ToListAsync(ct);
        if (credits.Count == 0) return held;
        var contactIds = credits.Select(c => c.ContactId).Distinct().ToList();
        var types = CustomerCreditAllocation.TargetTypes.ToList();
        var statuses = CustomerCreditAllocation.OpenStatuses.ToList();
        var docs = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && contactIds.Contains(d.ContactId)
                && types.Contains(d.DocumentType) && statuses.Contains(d.Status) && d.BalanceDue > 0.005m)
            .Select(d => new { d.Id, d.ContactId, d.DocumentNumber, d.DocumentType, d.Status, d.DocumentDate, d.DueDate, d.BalanceDue, d.Currency, d.ExchangeRate, d.VatAmount })
            .ToListAsync(ct);
        if (docs.Count == 0) return held;
        var docIds = docs.Select(d => d.Id).ToList();
        // มีสมุดรายวันตอนออกใบ (ตัวตั้งลูกหนี้) ไหม — ไม่มี = จัดสรรไม่ได้ (CustomerCreditService ปฏิเสธ) ⇒ ห้ามระงับทวง
        var withIssueJournal = (await db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && j.SourceDocumentId != null && docIds.Contains(j.SourceDocumentId.Value) && !j.IsDeleted
                && j.Status == JournalEntryStatus.Posted && j.ReversedByEntryId == null && j.OriginalEntryId == null)
            .Select(j => j.SourceDocumentId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow).Date;
        foreach (var group in credits.GroupBy(c => c.ContactId))
        {
            var total = group.Sum(c => c.UnappliedAmount);
            // ข้อเท็จจริงรวมของเงินลูกค้ารายนี้ (วันรับเงินล่าสุด — วันที่จัดสรร "วันนี้" ไม่ก่อนวันรับเงินเสมอ)
            var facts = new CustomerCreditFacts(companyId, group.Key, group.First().PaymentNumber, group.Max(c => c.PaymentDate), total,
                HasUnappliedAccount: true, IsVoided: false, IsSettlementBatchPayment: false, IsThb: true);
            var open = docs.Where(d => d.ContactId == group.Key)
                .OrderBy(d => d.DueDate ?? d.DocumentDate).ThenBy(d => d.DocumentDate).ThenBy(d => d.DocumentNumber)
                .Select(d => new DunningSuppression.OpenDocument(d.Id, d.BalanceDue,
                    withIssueJournal.Contains(d.Id)
                    && d.DocumentDate.Date <= today
                    && CustomerCreditAllocation.Check(facts with { UnappliedAmount = Math.Max(total, d.BalanceDue) },
                        new CustomerCreditTargetFacts(companyId, d.ContactId, d.DocumentNumber, d.DocumentType, d.Status, d.DocumentDate, d.BalanceDue,
                            d.Currency, d.ExchangeRate, d.VatAmount), d.BalanceDue, today, today) == null));
            foreach (var kv in DunningSuppression.Plan(total, open)) held[kv.Key] = kv.Value;
        }
        return held;
    }

    /// <summary>
    /// ฝ่ายค้านรอบ 258 M4 — รายการ "เงินส่วนเกินของการรับ/จ่ายชำระก่อนรอบ 258" ที่ต้องให้คนตรวจ (หน้าตรวจความถูกต้องของข้อมูล · อ่านอย่างเดียว):
    /// (ก) ฝั่งรับที่ backfill ติดธงต้องตรวจ · (ข) <b>ฝั่งจ่าย</b>: JE "เงินส่วนเกินยังไม่จัดสรร" ของสมุดรายวันจ่ายที่ยังมีผล — เดิมลงผังแรกใต้ 114 (เช่น 11400 เงินให้กู้ยืมระยะสั้น)
    /// ไม่มีรายการ/รายงานใดติดตาม · ระบบไม่ย้ายให้ (ไม่รู้ว่าถูกล้างแล้วหรือยัง)
    /// </summary>
    public static async Task<List<DataIntegritySuspect>> LegacyExcessSuspectsAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var result = new List<DataIntegritySuspect>();
        var receiveSide = await db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.UnappliedNeedsReview && p.UnappliedAmount > UnappliedCreditBalance.Tolerance)
            .OrderBy(p => p.PaymentDate)
            .Select(p => new { p.Id, p.PaymentNumber, p.PaymentDate, p.UnappliedAmount })
            .Take(500).ToListAsync(ct);
        result.AddRange(receiveSide.Select(p => new DataIntegritySuspect("LegacyPaymentExcess", p.Id, p.PaymentNumber, p.PaymentDate,
            "เงินรับส่วนเกินก่อนรอบ 258 (ลงผังรับล่วงหน้าเดิม) — ระบบไม่รู้ว่าถูกล้างด้วยสมุดรายวันไปแล้วหรือยัง · ที่หน้า “เงินรอจัดสรร” เลือก “ยืนยันว่ายังค้างจริง” หรือ “ปิดรายการ”",
            p.UnappliedAmount, null, "/pages/customer-credits.html", "เปิดเงินรอจัดสรร")));
        var prefix = CustomerCreditMigration.ExcessJournalDescriptionPrefix;
        var paySide = await db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && !j.IsDeleted && j.Status == JournalEntryStatus.Posted && j.JournalType == JournalType.CashPayments
                && j.OriginalEntryId == null && j.ReversedByEntryId == null && j.SourceDocumentId == null
                && j.Description != null && j.Description.StartsWith(prefix))
            .OrderBy(j => j.EntryDate)
            .Select(j => new { j.Id, j.EntryNumber, j.EntryDate, j.TotalDebit, j.Reference })
            .Take(500).ToListAsync(ct);
        result.AddRange(paySide.Select(j => new DataIntegritySuspect("LegacyPaymentExcess", j.Id, j.EntryNumber, j.EntryDate,
            $"เงินจ่ายเจ้าหนี้ส่วนเกินก่อนรอบ 258 (การจ่ายชำระ {j.Reference}) ลงผังกลุ่ม 114 (เช่น 11400 เงินให้กู้ยืมระยะสั้น) — ไม่ใช่เงินให้กู้ · "
            + "ตรวจว่ายังค้างจริงไหม แล้วให้ผู้ทำบัญชีลงสมุดรายวันย้ายไปผังเงินจ่ายล่วงหน้า/ตัดกับเจ้าหนี้ (ระบบไม่ย้ายให้)",
            j.TotalDebit, null, "/pages/general-ledger.html", "เปิดสมุดบัญชี")));
        return result;
    }
}

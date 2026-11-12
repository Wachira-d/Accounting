using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ยอดคงเหลือของเงินรอจัดสรร — ตัวคิดใหม่จากแถวจริงตัวเดียว** (รอบ 259 P4) + ด่านที่เส้นเดิมของ <c>DocumentService</c> เรียกบรรทัดเดียว
/// <para>คงเหลือ = ยอดรับ − Σ การจัดสรรที่มีผล − Σ ขาออกที่มีผลซึ่งตัดยอด (<see cref="CustomerCreditMovementRules.ReducesBalance"/>) — สูตรเดียว
/// <see cref="UnappliedCreditBalance.Compute"/> · การจองให้ใบร่าง (ToInvoice) ไม่อยู่ในยอดคงเหลือ แต่ลด "ยอดที่ใช้ได้" (<see cref="HeldForDraftsAsync"/>)</para>
/// <para>ทุก query มี <c>CompanyId == companyId</c> · ผู้เรียกถือธุรกรรม + ล็อกแถวการชำระก่อนเรียก <see cref="RecomputeAsync"/></para>
/// </summary>
public static class CustomerCreditLedger
{
    private static readonly string[] ReducingKinds = Enum.GetValues<PaymentCreditMovementKind>()
        .Where(CustomerCreditMovementRules.ReducesBalance).Select(k => k.ToString()).ToArray();
    private static readonly string ToInvoiceKind = nameof(PaymentCreditMovementKind.ToInvoice);
    private static readonly string ToDepositKind = nameof(PaymentCreditMovementKind.ToDeposit);
    private static readonly string ToReceiptKind = nameof(PaymentCreditMovementKind.ToReceipt);

    /// <summary>Σ ขาออกที่มีผลซึ่งตัดยอดของการชำระนี้</summary>
    private static async Task<decimal> LiveReducingSumAsync(AccountingDbContext db, Guid companyId, Guid paymentId, CancellationToken ct = default)
        => await db.PaymentCreditMovements.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.PaymentId == paymentId && !m.IsDeleted && m.ReleasedAt == null
                && ReducingKinds.Contains(m.Kind))
            .SumAsync(m => (decimal?)m.Amount, ct) ?? 0m;

    /// <summary>คิดยอดคงเหลือใหม่จากแถวจริง (ไม่บวก/ลบสะสม) — ผู้เรียกถือล็อกแถวการชำระ · ไม่ SaveChanges</summary>
    public static async Task RecomputeAsync(AccountingDbContext db, Guid companyId, Payment payment, CancellationToken ct = default)
    {
        var allocated = await db.PaymentAllocations.AsNoTracking()
            .Where(a => a.PaymentId == payment.Id && a.CompanyId == companyId && !a.IsDeleted)
            .SumAsync(a => (decimal?)a.AllocatedAmount, ct) ?? 0m;
        var moved = await LiveReducingSumAsync(db, companyId, payment.Id, ct);
        payment.UnappliedAmount = UnappliedCreditBalance.Compute(payment.Amount, allocated, moved);
        payment.UnappliedResolvedAt = UnappliedCreditBalance.HasBalance(payment.UnappliedAmount) ? null : DateTime.UtcNow;
        payment.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>ยอดที่จองให้ใบร่าง (ToInvoice ที่ใบยังเป็นร่าง/รออนุมัติ) ต่อการชำระ — <paramref name="exceptDocumentId"/> = ไม่นับการจองของใบนี้ (ใบที่กำลังรับการจัดสรร)</summary>
    public static async Task<Dictionary<Guid, decimal>> HeldForDraftsAsync(AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> paymentIds,
        Guid? exceptDocumentId = null, CancellationToken ct = default)
    {
        if (paymentIds.Count == 0) return new Dictionary<Guid, decimal>();
        var ids = paymentIds.Distinct().ToList();
        var rows = await (
            from m in db.PaymentCreditMovements.AsNoTracking()
            join d in db.Documents.AsNoTracking() on m.TargetDocumentId equals (Guid?)d.Id
            where m.CompanyId == companyId && d.CompanyId == companyId && ids.Contains(m.PaymentId)
                  && m.Kind == ToInvoiceKind && !m.IsDeleted && m.ReleasedAt == null && !d.IsDeleted
                  && (d.Status == DocumentStatus.Draft || d.Status == DocumentStatus.WaitingApproval)
                  && (exceptDocumentId == null || d.Id != exceptDocumentId)
            select new { m.PaymentId, m.Amount })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.PaymentId).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
    }

    /// <summary>ด่านของ <c>VoidPaymentAsync</c>: การชำระที่ยังมีขาออกที่ตัดยอดอยู่ (เงินคืนไปแล้ว · ใบมัดจำ · ตัดเศษ) ⇒ ข้อความปฏิเสธ · null = ยกเลิกได้</summary>
    public static async Task<string?> VoidBlockAsync(AccountingDbContext db, Guid companyId, Guid paymentId, string paymentNumber, CancellationToken ct = default)
    {
        var kinds = await db.PaymentCreditMovements.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.PaymentId == paymentId && !m.IsDeleted && m.ReleasedAt == null
                && ReducingKinds.Contains(m.Kind))
            .Select(m => m.Kind).Distinct().ToListAsync(ct);
        if (kinds.Count == 0) return null;
        return CustomerCreditMovementRules.VoidBlockMessage(paymentNumber,
            kinds.Select(CustomerCreditMovementRules.Parse).Where(k => k != null).Select(k => k!.Value));
    }

    /// <summary>ฝ่ายค้านรอบ 259 H1 — ด่านของ <c>VoidDocumentAsync</c> ก่อน cascade ยกเลิกการรับชำระของใบ: การชำระใดยังมีขาออกที่ตัดยอด ⇒ ข้อความปฏิเสธ (ตัวเดียวกับ
    /// <see cref="VoidBlockAsync"/>) · null = ผ่าน · tenant</summary>
    public static async Task<string?> VoidBlockForPaymentsAsync(AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> paymentIds,
        CancellationToken ct = default)
    {
        if (paymentIds.Count == 0) return null;
        var ids = paymentIds.Distinct().ToList();
        var hit = await (
            from m in db.PaymentCreditMovements.AsNoTracking()
            join p in db.Payments.AsNoTracking().IgnoreQueryFilters() on m.PaymentId equals p.Id
            where m.CompanyId == companyId && p.CompanyId == companyId && ids.Contains(m.PaymentId)
                  && !m.IsDeleted && m.ReleasedAt == null && ReducingKinds.Contains(m.Kind)
            orderby p.PaymentNumber
            select new { m.PaymentId, p.PaymentNumber }).FirstOrDefaultAsync(ct);
        return hit == null ? null : await VoidBlockAsync(db, companyId, hit.PaymentId, hit.PaymentNumber, ct);
    }

    /// <summary>ฝ่ายค้านรอบ 259 M3 — กลับ JE ของขาออก (คืนเงิน/ตัดเศษ) ด้วยมือที่หน้าสมุดรายวัน = ปฏิเสธพร้อมทางไปต่อ (ยอดคงเหลือ/ยอดธนาคารต้องกลับพร้อมกัน) · null = ไม่ใช่ JE ของขาออกที่มีผล</summary>
    public static async Task<string?> ManualReverseBlockAsync(AccountingDbContext db, Guid companyId, Guid journalEntryId, CancellationToken ct = default)
    {
        var hit = await db.PaymentCreditMovements.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.JournalEntryId == journalEntryId && !m.IsDeleted && m.ReleasedAt == null)
            .Select(m => m.Kind).FirstOrDefaultAsync(ct);
        if (hit == null) return null;
        var label = CustomerCreditMovementRules.Parse(hit) is { } k ? CustomerCreditMovementRules.LabelOf(k) : hit;
        return $"รายการบัญชีนี้เป็น “{label}” ของเงินรอจัดสรร — กลับรายการที่หน้าสมุดรายวันไม่ได้ (ยอดคงเหลือ/ยอดธนาคารต้องกลับพร้อมกัน) · "
            + "ไปที่หน้า “เงินรอจัดสรร” แล้วกด “ยกเลิกรายการ”";
    }

    /// <summary>
    /// ด่านของ <c>VoidDocumentAsync</c> (ในธุรกรรมของการยกเลิก ก่อน commit): ใบที่ถูกยกเลิกเป็นใบมัดจำที่ย้ายจากเงินรอจัดสรร ⇒ ปล่อยขาออก
    /// (ReleasedAt) + คิดยอดคงเหลือใหม่ — JE ของใบมัดจำ (Dr ผังพัก) ถูกกลับโดยการยกเลิกใบแล้ว เงินจึงกลับเป็นเงินรอจัดสรร · ล็อกแถวการชำระ
    /// (ลำดับ ใบ → การชำระ เดียวกับเส้นจัดสรร) · คืนจำนวนแถวที่ปล่อย · audit ทุกแถว
    /// </summary>
    public static async Task<int> ReleaseForVoidedDocumentAsync(AccountingDbContext db, Guid companyId, Guid documentId, string? actor,
        CancellationToken ct = default)
    {
        var rows = await db.PaymentCreditMovements
            .Where(m => m.CompanyId == companyId && m.TargetDocumentId == documentId && !m.IsDeleted && m.ReleasedAt == null
                && (m.Kind == ToDepositKind || m.Kind == ToInvoiceKind || m.Kind == ToReceiptKind))
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;
        // รอบ 265 (คำตัดสิน 265-1-4): ใบเสร็จจากเงินรอจัดสรรที่ปิดงวดรายการประจำถูกยกเลิก ⇒ ปล่อยกุญแจงวด (รอบถัดไป/“สร้างทันที” ออกใบงวดนั้นได้อีก ·
        // งวดที่รอบอัตโนมัติประทับผ่านไปแล้วไม่ถอยเอง — ผู้ใช้กด “สร้างทันที” ที่รายการประจำ)
        if (rows.Any(r => r.Kind == ToReceiptKind))
            await db.Documents.Where(d => d.Id == documentId && d.CompanyId == companyId && d.RecurringRunKey != null)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.RecurringRunKey, (string?)null), ct);
        foreach (var paymentId in rows.Select(r => r.PaymentId).Distinct().OrderBy(id => id))
        {
            var payment = await db.Payments
                .FromSqlRaw("""SELECT * FROM "Payments" WHERE "Id" = {0} AND "CompanyId" = {1} FOR UPDATE""", paymentId, companyId)
                .FirstOrDefaultAsync(ct);
            if (payment == null) continue;
            await db.Entry(payment).ReloadAsync(ct);
            foreach (var m in rows.Where(r => r.PaymentId == paymentId))
            {
                m.ReleasedAt = DateTime.UtcNow;
                m.ReleaseNote = "เอกสารปลายทางถูกยกเลิก — เงินกลับเป็นเงินรอจัดสรร";
                m.UpdatedAt = DateTime.UtcNow;
                m.UpdatedBy = actor;
                db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId,
                    UserId = Guid.TryParse(actor, out var uid) ? uid : null,
                    Action = AuditAction.Update,
                    EntityType = "PaymentCreditMovement",
                    EntityId = m.Id.ToString(),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        RuleCode = CustomerCreditMovementRules.UndoRuleCode,
                        LegalReference = CustomerCreditMovementRules.LegalReference,
                        payment.PaymentNumber,
                        m.Kind,
                        m.Amount,
                        TargetDocumentId = documentId,
                        Reason = m.ReleaseNote,
                    }),
                });
            }
            await db.SaveChangesAsync(ct);
            await RecomputeAsync(db, companyId, payment, ct);
            await db.SaveChangesAsync(ct);
        }
        return rows.Count;
    }
}

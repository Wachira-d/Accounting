using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// จุดต่อบาง ๆ ระหว่าง <c>DocumentService</c> (ยกเลิกใบ · อนุมัติใบลดหนี้) กับบัตรคอร์ส (ทีม L · ฝ่ายค้านรอบ 1 ข้อ 1) —
/// ตัวตัดสินอยู่ที่ <see cref="CreditPassDocumentRule"/> ตัวเดียว · ทำงาน<b>ในธุรกรรมของผู้เรียก</b> (ล็อกแถวบัตร · แก้บัตร + สมุด + audit
/// ถูกบันทึกพร้อม SaveChanges ของผู้เรียก) · บล็อก = <see cref="BusinessRuleException"/> ข้อความไทยพร้อมทางไปต่อ
/// </summary>
public static class CreditPassDocumentHook
{
    /// <summary>เรียกจาก <c>DocumentService.VoidDocumentAsync</c> ใต้ล็อกใบ ก่อนกลับรายการ</summary>
    public static async Task OnVoidAsync(AccountingDbContext db, Guid companyId, Document doc, string actor, CancellationToken ct = default)
    {
        if (doc.DocumentType == DocumentType.CreditNote)
        {
            var closed = await db.CreditPasses.AsNoTracking()
                .Where(p => p.CompanyId == companyId && p.CancelledByDocumentId == doc.Id)
                .Select(p => p.PassNumber).FirstOrDefaultAsync(ct);
            var cnDecision = CreditPassDocumentRule.OnVoidCreditNote(closed, doc.DocumentNumber);
            if (cnDecision.Action == CreditPassDocumentRule.DocAction.Block)
                throw new BusinessRuleException(cnDecision.Message!, CreditPassDocumentRule.RuleCode, 409);
            return;
        }
        var pass = await LockLivePassAsync(db, companyId, doc.Id, ct);
        var decision = CreditPassDocumentRule.OnVoidSale(pass == null ? null : Facts(pass), doc.DocumentNumber);
        Apply(db, decision, pass, doc, actor, refunded: 0m, $"ยกเลิกเอกสารขาย {doc.DocumentNumber} ก่อนใช้บัตร — ปิดบัตร");
    }

    /// <summary>เรียกจาก <c>DocumentService.ApplySourceDocumentAdjustmentsAsync</c> (อนุมัติใบลดหนี้ที่อ้างใบต้นทาง) ใต้ล็อกใบต้นทาง</summary>
    public static async Task OnCreditNoteApprovedAsync(AccountingDbContext db, Guid companyId, Document creditNote, Document source,
        string actor, CancellationToken ct = default)
    {
        var pass = await LockLivePassAsync(db, companyId, source.Id, ct);
        if (pass == null) return;
        List<(decimal Amount, Guid? AccountId)> lines;
        if (creditNote.Lines is { Count: > 0 })
            lines = creditNote.Lines.Where(l => !l.IsDeleted).Select(l => (l.Amount, l.AccountId)).ToList();
        else
            lines = (await db.DocumentLines.AsNoTracking()
                    .Where(l => l.DocumentId == creditNote.Id && !l.IsDeleted && l.Document.CompanyId == companyId)
                    .Select(l => new { l.Amount, l.AccountId }).ToListAsync(ct))
                .Select(l => (l.Amount, l.AccountId)).ToList();
        var net = lines.Sum(l => l.Amount);
        var allUnearned = lines.All(l => l.AccountId == null || l.AccountId == pass.UnearnedAccountId);
        var decision = CreditPassDocumentRule.OnCreditNote(Facts(pass), source.DocumentNumber, net, allUnearned);
        if (decision.Action == CreditPassDocumentRule.DocAction.ReduceCredits)
        {
            ReduceCredits(db, decision.CreditsToRemove, pass, creditNote, actor, net);
            return;
        }
        Apply(db, decision, pass, creditNote, actor, refunded: net,
            $"ใบลดหนี้ {creditNote.DocumentNumber} คืนเงินส่วนที่ยังไม่ใช้ {net:N2} — ปิดบัตร");
    }

    /// <summary>คืนเงินบางส่วน (R-38): ลดเครดิตทั้งหมดของบัตร + บวกยอดคืน · บัตรยัง Active · แถว Adjust ในสมุด (ราคาต่อเครดิตหลังจากนี้ = (สัญญา − คืนแล้ว) ÷ เครดิตที่เหลือ)</summary>
    private static void ReduceCredits(AccountingDbContext db, decimal credits, CreditPass pass, Document creditNote, string actor, decimal refunded)
    {
        if (credits <= 0m) throw new BusinessRuleException("จำนวนเครดิตที่ลดต้องมากกว่า 0", CreditPassDocumentRule.RuleCode, 409);
        var before = pass.CreditsTotal;
        pass.CreditsTotal -= credits;
        pass.RefundedAmount += refunded;
        pass.UpdatedAt = DateTime.UtcNow; pass.UpdatedBy = actor;
        var note = $"ใบลดหนี้ {creditNote.DocumentNumber} คืนเงินบางส่วน {refunded:N2} — ลดเครดิต {credits:0.##} (ทั้งหมด {before:0.##} → {pass.CreditsTotal:0.##}) · บัตรใช้ต่อได้";
        db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            CompanyId = pass.CompanyId, CreatedBy = actor, PassId = pass.Id, Kind = CreditLedgerKind.Adjust,
            Credits = credits, Note = note,
        });
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = pass.CompanyId, UserEmail = actor, Action = AuditAction.Update, EntityType = "CreditPass", EntityId = pass.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                action = "PartialRefundByCreditNote", pass = pass.PassNumber, document = creditNote.DocumentNumber,
                refunded, creditsRemoved = credits, creditsTotalBefore = before, creditsTotalAfter = pass.CreditsTotal,
                ruleCode = CreditPassDocumentRule.RuleCode,
            }),
        });
    }

    private static async Task<CreditPass?> LockLivePassAsync(AccountingDbContext db, Guid companyId, Guid saleDocumentId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"CreditPasses\" WHERE \"SaleDocumentId\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", saleDocumentId, companyId);
        var pass = await db.CreditPasses
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.SaleDocumentId == saleDocumentId && p.Status != CreditPassStatus.Cancelled, ct);
        if (pass != null && db.Entry(pass).State == EntityState.Unchanged) await db.Entry(pass).ReloadAsync(ct);
        return pass;
    }

    internal static CreditPassDocumentRule.PassFacts Facts(CreditPass p)
        => new(p.PassNumber, p.Status, p.CreditsUsed, p.CreditsReserved, p.ContractAmount, p.RecognizedAmount, p.RefundedAmount,
            p.CreditsTotal, p.Unit);

    private static void Apply(AccountingDbContext db, CreditPassDocumentRule.Decision decision, CreditPass? pass, Document doc,
        string actor, decimal refunded, string note)
    {
        if (decision.Action == CreditPassDocumentRule.DocAction.Block)
            throw new BusinessRuleException(decision.Message!, CreditPassDocumentRule.RuleCode, 409);
        if (decision.Action != CreditPassDocumentRule.DocAction.CancelPass || pass == null) return;
        var remainingCredits = Math.Max(0m, pass.CreditsTotal - pass.CreditsUsed);
        pass.Status = CreditPassStatus.Cancelled;
        pass.RefundedAmount += refunded;
        pass.CancelledByDocumentId = doc.Id;
        pass.ClosedAt = DateTime.UtcNow; pass.UpdatedAt = DateTime.UtcNow; pass.UpdatedBy = actor;
        db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            CompanyId = pass.CompanyId, CreatedBy = actor, PassId = pass.Id, Kind = CreditLedgerKind.Cancel,
            Credits = remainingCredits, Note = note,
        });
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = pass.CompanyId, UserEmail = actor, Action = AuditAction.Update, EntityType = "CreditPass", EntityId = pass.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                action = "CancelPassByDocument", pass = pass.PassNumber, document = doc.DocumentNumber, documentType = doc.DocumentType.ToString(),
                refunded, remainingCredits, ruleCode = CreditPassDocumentRule.RuleCode,
            }),
        });
    }
}

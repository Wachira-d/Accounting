using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 275 แพ็ก E ฝ่ายค้าน MED-3 — รายการค้าง "งวดนี้ต้องยื่น ภ.ง.ด.1 เพิ่มเติม" (<see cref="Pnd1AmendmentRequired"/>) ที่เกิดจากการออกใบแทน 50 ทวิ
/// ของงวดที่ยื่นแล้ว (<see cref="PayrollJunkPayee.RepairAsync"/>) · ค้างจนกว่าผู้ใช้<b>บันทึกว่ายื่นเพิ่มเติมแล้ว</b> (เลขอ้างอิง/หมายเหตุบังคับ · audit) ·
/// ระบบไม่ประทับ "ยื่นแล้ว" เอง (F2 ข้อ 3) · ทุก query กรอง <c>CompanyId</c>
/// </summary>
public static class Pnd1AmendmentQueue
{
    public const string RuleCode = "RD-PND1-AMENDMENT-REQUIRED";

    public sealed record Item(Guid Id, int TaxYear, int TaxMonth, string PeriodText, string Reason, string? CertificateNumbers,
        DateTime CreatedAt, DateTime? ResolvedAt, string? ResolvedReference, bool Pending, string Action);

    public static async Task<IReadOnlyList<Item>> ListAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-90);
        var rows = await db.Pnd1AmendmentsRequired.AsNoTracking()
            .Where(a => a.CompanyId == companyId && (a.ResolvedAt == null || a.ResolvedAt > cutoff))
            .OrderBy(a => a.ResolvedAt != null).ThenBy(a => a.TaxYear).ThenBy(a => a.TaxMonth)
            .ToListAsync(ct);
        return rows.Select(a => new Item(a.Id, a.TaxYear, a.TaxMonth, $"{a.TaxMonth:D2}/{a.TaxYear + 543}", a.Reason, a.CertificateNumbers,
            a.CreatedAt, a.ResolvedAt, a.ResolvedReference, a.ResolvedAt == null,
            a.ResolvedAt == null
                ? $"ดาวน์โหลดไฟล์ ภ.ง.ด.1 เดือน {a.TaxMonth:D2}/{a.TaxYear + 543} ใหม่ (ใช้เลขที่ถูก) แล้วยื่นเป็นแบบยื่นเพิ่มเติม · พิมพ์ใบแทนให้ผู้ถูกหัก · แล้วกด “บันทึกว่ายื่นเพิ่มเติมแล้ว”"
                : "บันทึกว่ายื่นเพิ่มเติมแล้ว")).ToList();
    }

    /// <summary>บันทึกว่ายื่นเพิ่มเติมแล้ว — เลขอ้างอิง/หมายเหตุบังคับ (≥ 3 ตัวอักษร) · audit chain</summary>
    public static async Task ResolveAsync(AccountingDbContext db, Guid companyId, Guid id, string? reference, Guid? userId, CancellationToken ct = default)
    {
        var refText = (reference ?? "").Trim();
        if (refText.Length < 3) throw new BusinessRuleException("ระบุเลขอ้างอิงการยื่นเพิ่มเติม (หรือหมายเหตุ) อย่างน้อย 3 ตัวอักษร", RuleCode, 400);
        if (refText.Length > 500) refText = refText[..500];
        var a = await db.Pnd1AmendmentsRequired.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบรายการ");
        if (a.ResolvedAt != null) throw new BusinessRuleException("รายการนี้บันทึกว่ายื่นเพิ่มเติมแล้ว", RuleCode, 409);
        a.ResolvedAt = DateTime.UtcNow;
        a.ResolvedByUserId = userId;
        a.ResolvedReference = refText;
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "Pnd1AmendmentRequired", EntityId = a.Id.ToString(),
            OldValues = JsonSerializer.Serialize(new { a.TaxYear, a.TaxMonth, resolved = false }),
            NewValues = JsonSerializer.Serialize(new { resolved = true, reference = refText, ruleCode = RuleCode, legalReference = PayrollJunkPayee.LegalReference }),
            Timestamp = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}

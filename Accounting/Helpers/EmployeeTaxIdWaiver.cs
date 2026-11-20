using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 275 แพ็ก E (T4 H-3 · คำตัดสิน 4.2) — <b>การรับทราบรายคน</b> ว่าพนักงานไม่มีเลขประจำตัว 13 หลัก (เหตุ · ผู้กด · เวลา · audit chain) ·
/// ทางเขียนเดียวของ <c>Employee.TaxIdWaiver*</c> · ผลของการรับทราบตัดสินที่ <see cref="EmployeeTaxIdentity.ForFiling(Employee)"/> ตัวเดียว
/// (50 ทวิ รายเดือน/รายปี · ไฟล์ ภ.ง.ด.1/1ก · ด่านนำส่งผ่านใบที่ออก) ·
/// รับทราบได้<b>เฉพาะคนที่ไม่มีเลขเลย</b> — เลข checksum ผิด/ถอดรหัสไม่ได้ปฏิเสธพร้อมเหตุ (<see cref="EmployeeTaxIdentity.WaiverRefusal"/>) · ทุก query กรอง <c>CompanyId</c>
/// </summary>
public static class EmployeeTaxIdWaiver
{
    public const string RuleCode = "RD-PND1-NO-TAXID-ACK";
    public const string LegalReference = "ป.รัษฎากร ม.50 ทวิ · ม.58 · คำตัดสินเจ้าของ 4.2 (รอบ 275)";

    public sealed record Row(Guid EmployeeId, string EmployeeCode, string Name, string? Nationality, string Kind, string Status,
        string? PassportMasked, DateTime? AcknowledgedAt, string? Reason, bool CanAcknowledge, bool CanRevoke);

    /// <summary>พนักงานที่ยังทำงานอยู่ที่ไม่มีเลข 13 หลัก (ยังไม่รับทราบ / รับทราบแล้ว) — ชื่อสถานะเป็นชื่อ enum (<see cref="EmployeeTaxIdentity.FilingKind"/>) ·
    /// พาสปอร์ตปิดบังเสมอ</summary>
    public static async Task<IReadOnlyList<Row>> ListAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var emps = await db.Set<Employee>().AsNoTracking()
            .Where(e => e.CompanyId == companyId && (e.IsActive || e.TaxIdWaiverAcknowledgedAt != null))
            .OrderBy(e => e.EmployeeCode)
            .ToListAsync(ct);
        var rows = new List<Row>();
        foreach (var e in emps)
        {
            var f = EmployeeTaxIdentity.ForFiling(e);
            if (f.Kind is not (EmployeeTaxIdentity.FilingKind.NeedsAcknowledgement or EmployeeTaxIdentity.FilingKind.WaivedNoId)) continue;
            var passport = (e.PassportNumber ?? "").Trim();
            var waived = f.Kind == EmployeeTaxIdentity.FilingKind.WaivedNoId;
            rows.Add(new Row(e.Id, e.EmployeeCode, PayrollPnd1Certs.PayeeName(e), e.Nationality, f.Kind.ToString(),
                waived ? "รับทราบแล้ว — 50 ทวิ ออกได้ (กล่องเลขว่าง + เลขหนังสือเดินทางประกอบ) · ไฟล์ ภ.ง.ด.1 ลงเลข 0 ตามสเปก"
                       : "ยังไม่รับทราบ — 50 ทวิ ไม่ออกและนำส่ง ภ.ง.ด.1 ของงวดที่คนนี้มีภาษีหักถูกบล็อก",
                passport.Length == 0 ? null : EncryptedColumnConverter.IsUnreadable(passport) ? "(ถอดรหัสไม่ได้)" : PiiMask.Passport(passport),
                e.TaxIdWaiverAcknowledgedAt, e.TaxIdWaiverReason, !waived, waived));
        }
        return rows;
    }

    /// <summary>รับทราบ — เหตุบังคับ (≥ 5 ตัวอักษร) · ปฏิเสธคนที่มีเลข/เลขผิด/ถอดรหัสไม่ได้ (ต้องแก้เลข) · audit chain (ไม่มีเลขเต็ม)</summary>
    public static async Task AcknowledgeAsync(AccountingDbContext db, Guid companyId, Guid employeeId, string? reason, Guid? userId, CancellationToken ct = default)
    {
        var why = (reason ?? "").Trim();
        if (why.Length < 5) throw new BusinessRuleException("กรุณาระบุเหตุที่พนักงานไม่มีเลขประจำตัว 13 หลัก (อย่างน้อย 5 ตัวอักษร) เช่น “ต่างชาติ รอเลขจากกรมสรรพากร”", RuleCode, 400);
        if (why.Length > 500) why = why[..500];
        var e = await db.Set<Employee>().FirstOrDefaultAsync(x => x.Id == employeeId && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");
        if (EmployeeTaxIdentity.WaiverRefusal(e.TaxId, e.CitizenId) is string refused) throw new BusinessRuleException(refused, RuleCode, 409);
        var before = new { e.TaxIdWaiverAcknowledgedAt, e.TaxIdWaiverReason };
        e.TaxIdWaiverAcknowledgedAt = DateTime.UtcNow;
        e.TaxIdWaiverAcknowledgedBy = userId;
        e.TaxIdWaiverReason = why;
        Audit(db, companyId, e, userId, before, new { e.TaxIdWaiverAcknowledgedAt, e.TaxIdWaiverReason, ruleCode = RuleCode, legalReference = LegalReference });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>ถอนการรับทราบ (เช่น รับทราบผิดคน) — ใบที่ออกไปแล้วไม่ถูกแตะ · งวดถัดไปกลับไปบล็อกเหมือนเดิม · audit chain</summary>
    public static async Task RevokeAsync(AccountingDbContext db, Guid companyId, Guid employeeId, string? reason, Guid? userId, CancellationToken ct = default)
    {
        var e = await db.Set<Employee>().FirstOrDefaultAsync(x => x.Id == employeeId && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");
        if (e.TaxIdWaiverAcknowledgedAt == null) throw new BusinessRuleException("พนักงานคนนี้ยังไม่ได้รับทราบ — ไม่มีอะไรให้ถอน", RuleCode, 409);
        var before = new { e.TaxIdWaiverAcknowledgedAt, e.TaxIdWaiverReason };
        e.TaxIdWaiverAcknowledgedAt = null;
        e.TaxIdWaiverAcknowledgedBy = null;
        e.TaxIdWaiverReason = null;
        Audit(db, companyId, e, userId, before, new { revoked = true, reason = (reason ?? "").Trim(), ruleCode = RuleCode, legalReference = LegalReference });
        await db.SaveChangesAsync(ct);
    }

    private static void Audit(AccountingDbContext db, Guid companyId, Employee e, Guid? userId, object before, object after)
    {
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "Employee.TaxIdWaiver", EntityId = e.Id.ToString(),
            OldValues = JsonSerializer.Serialize(before), NewValues = JsonSerializer.Serialize(after), Timestamp = DateTime.UtcNow,
        });
    }
}

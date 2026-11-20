using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 275 แพ็ก E (T4 H-3 · คำตัดสิน 4.2 · ฝ่ายค้าน MED-2) — <b>การรับทราบรายคน</b> ว่าผู้มีเงินได้ใน ภ.ง.ด.1 ไม่มีเลขประจำตัว 13 หลัก
/// (เหตุ · ผู้กด · เวลา · audit chain) · ทางเขียนเดียวของ <c>Employee.TaxIdWaiver*</c> และ <c>Contact.TaxIdWaiver*</c> (ผู้รับใบ 50 ทวิ ภ.ง.ด.1 ที่ออกด้วยมือ) ·
/// ผลของการรับทราบตัดสินที่ <see cref="EmployeeTaxIdentity.ForFiling(string?, string?, DateTime?)"/> ตัวเดียว (50 ทวิ · ไฟล์ ภ.ง.ด.1/1ก · ด่านนำส่ง · รายงานสุขภาพ) ·
/// รับทราบได้<b>เฉพาะคนที่ไม่มีเลขเลย</b> — เลข checksum ผิด/ถอดรหัสไม่ได้ปฏิเสธพร้อมเหตุ (<see cref="EmployeeTaxIdentity.WaiverRefusal"/>) ·
/// ได้เลขที่ถูกต้องภายหลัง ⇒ <c>AccountingDbContext</c> ล้างการรับทราบเอง (ไม่กลับมามีผลถ้าช่องว่างภายหลัง — LOW (ง)) · ทุก query กรอง <c>CompanyId</c>
/// </summary>
public static class EmployeeTaxIdWaiver
{
    public const string RuleCode = "RD-PND1-NO-TAXID-ACK";
    public const string LegalReference = "ป.รัษฎากร ม.50 ทวิ · ม.58 · คำตัดสินเจ้าของ 4.2 (รอบ 275)";
    public const string SubjectEmployee = "Employee";
    public const string SubjectContact = "Contact";

    public sealed record Row(string SubjectType, Guid SubjectId, string Code, string Name, string? Nationality, string Kind, string Status,
        string? PassportMasked, DateTime? AcknowledgedAt, string? Reason, bool CanAcknowledge, bool CanRevoke, string? AcknowledgedBy = null)
    {
        /// <summary>ชื่อเดิมของช่อง (พนักงาน) — คงไว้ให้ผู้อ่านเดิม</summary>
        public Guid EmployeeId => SubjectId;
        public string EmployeeCode => Code;
    }

    /// <summary>พนักงานที่ยังทำงานอยู่ + ผู้รับใบ 50 ทวิ ภ.ง.ด.1 ที่ออกด้วยมือ (ยังไม่ยกเลิก) ที่ไม่มีเลข 13 หลัก — สถานะเป็นชื่อ enum · พาสปอร์ตปิดบังเสมอ</summary>
    public static async Task<IReadOnlyList<Row>> ListAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var emps = await db.Set<Employee>().AsNoTracking()
            .Where(e => e.CompanyId == companyId && (e.IsActive || e.TaxIdWaiverAcknowledgedAt != null))
            .OrderBy(e => e.EmployeeCode)
            .ToListAsync(ct);
        var rows = new List<Row>();
        var ackIds = emps.Where(e => e.TaxIdWaiverAcknowledgedBy != null).Select(e => e.TaxIdWaiverAcknowledgedBy!.Value).ToList();
        ackIds.AddRange(await db.Contacts.AsNoTracking().Where(c => c.CompanyId == companyId && c.TaxIdWaiverAcknowledgedBy != null)
            .Select(c => c.TaxIdWaiverAcknowledgedBy!.Value).ToListAsync(ct));
        var users = await db.Users.AsNoTracking().Where(u => ackIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        // ผู้กดรับทราบ (หลักฐาน "ผู้กด" ของคำตัดสิน 4.2 — แสดงในรายการ)
        string? ByName(Guid? userId) => userId is Guid u && users.TryGetValue(u, out var n) ? n : null;
        foreach (var e in emps)
        {
            var f = EmployeeTaxIdentity.ForFiling(e);
            if (f.Kind is not (EmployeeTaxIdentity.FilingKind.NeedsAcknowledgement or EmployeeTaxIdentity.FilingKind.WaivedNoId)) continue;
            var passport = (e.PassportNumber ?? "").Trim();
            var waived = f.Kind == EmployeeTaxIdentity.FilingKind.WaivedNoId;
            rows.Add(new Row(SubjectEmployee, e.Id, e.EmployeeCode, PayrollPnd1Certs.PayeeName(e), e.Nationality, f.Kind.ToString(), StatusText(waived),
                passport.Length == 0 ? null : EncryptedColumnConverter.IsUnreadable(passport) ? "(ถอดรหัสไม่ได้)" : PiiMask.Passport(passport),
                e.TaxIdWaiverAcknowledgedAt, e.TaxIdWaiverReason, !waived, waived, ByName(e.TaxIdWaiverAcknowledgedBy)));
        }
        // ผู้รับใบมือ ภ.ง.ด.1 (ไม่ใช่ใบเงินเดือน) — ผู้ติดต่อที่เป็นผู้ติดต่อของพนักงานอยู่แล้วแสดงในแถวพนักงาน
        var empContactIds = emps.Where(e => e.ContactId != null).Select(e => e.ContactId!.Value).ToHashSet();
        var payeeIds = await db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.TaxFormType == TaxType.WithholdingTax1 && w.SourcePayrollRunId == null
                && w.Status != WithholdingTaxCertStatus.Voided)
            .Select(w => w.PayeeContactId).Distinct().ToListAsync(ct);
        var contacts = await db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && payeeIds.Contains(c.Id))
            .OrderBy(c => c.Name).ToListAsync(ct);
        foreach (var c in contacts.Where(c => !empContactIds.Contains(c.Id)))
        {
            var f = EmployeeTaxIdentity.ForFiling(c);
            if (f.Kind is not (EmployeeTaxIdentity.FilingKind.NeedsAcknowledgement or EmployeeTaxIdentity.FilingKind.WaivedNoId)) continue;
            var waived = f.Kind == EmployeeTaxIdentity.FilingKind.WaivedNoId;
            rows.Add(new Row(SubjectContact, c.Id, "ผู้ติดต่อ", c.Name, c.CountryCode, f.Kind.ToString(), StatusText(waived), null,
                c.TaxIdWaiverAcknowledgedAt, c.TaxIdWaiverReason, !waived, waived, ByName(c.TaxIdWaiverAcknowledgedBy)));
        }
        return rows;
    }

    private static string StatusText(bool waived) => waived
        ? "รับทราบแล้ว — 50 ทวิ ออกได้ (กล่องเลขว่าง + ข้อความประกอบ) · ไฟล์ ภ.ง.ด.1 ลงเลข 0 (ยังไม่ยืนยันกับสเปกกรมสรรพากร — ตรวจกับนักบัญชี)"
        : "ยังไม่รับทราบ — 50 ทวิ ไม่ออก/ไฟล์ ภ.ง.ด.1 กันแถว และนำส่ง ภ.ง.ด.1 ของงวดที่มีภาษีหักถูกบล็อก";

    private static string RequireReason(string? reason, string what)
    {
        var why = (reason ?? "").Trim();
        if (why.Length < 5) throw new BusinessRuleException($"กรุณาระบุเหตุที่{what} (อย่างน้อย 5 ตัวอักษร)", RuleCode, 400);
        return why.Length > 500 ? why[..500] : why;
    }

    /// <summary>รับทราบ — เหตุบังคับ · ปฏิเสธคนที่มีเลข/เลขผิด/ถอดรหัสไม่ได้ (ต้องแก้เลข) · audit chain (ไม่มีเลขเต็ม)</summary>
    public static async Task AcknowledgeAsync(AccountingDbContext db, Guid companyId, Guid employeeId, string? reason, Guid? userId, CancellationToken ct = default)
    {
        var why = RequireReason(reason, "พนักงานไม่มีเลขประจำตัว 13 หลัก เช่น “ต่างชาติ รอเลขจากกรมสรรพากร”");
        var e = await db.Set<Employee>().FirstOrDefaultAsync(x => x.Id == employeeId && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");
        if (EmployeeTaxIdentity.WaiverRefusal(e.TaxId, e.CitizenId) is string refused) throw new BusinessRuleException(refused, RuleCode, 409);
        var before = new { e.TaxIdWaiverAcknowledgedAt, e.TaxIdWaiverReason };
        e.TaxIdWaiverAcknowledgedAt = DateTime.UtcNow;
        e.TaxIdWaiverAcknowledgedBy = userId;
        e.TaxIdWaiverReason = why;
        Audit(db, companyId, SubjectEmployee, e.Id, userId, before, new { e.TaxIdWaiverAcknowledgedAt, e.TaxIdWaiverReason, ruleCode = RuleCode, legalReference = LegalReference });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>ถอนการรับทราบ — เหตุบังคับ (LOW (จ)) · ใบที่ออกไปแล้วไม่ถูกแตะ · งวดถัดไปกลับไปบล็อกเหมือนเดิม · audit chain</summary>
    public static async Task RevokeAsync(AccountingDbContext db, Guid companyId, Guid employeeId, string? reason, Guid? userId, CancellationToken ct = default)
    {
        var why = RequireReason(reason, "ถอนการรับทราบ");
        var e = await db.Set<Employee>().FirstOrDefaultAsync(x => x.Id == employeeId && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");
        if (e.TaxIdWaiverAcknowledgedAt == null) throw new BusinessRuleException("พนักงานคนนี้ยังไม่ได้รับทราบ — ไม่มีอะไรให้ถอน", RuleCode, 409);
        var before = new { e.TaxIdWaiverAcknowledgedAt, e.TaxIdWaiverReason };
        e.TaxIdWaiverAcknowledgedAt = null;
        e.TaxIdWaiverAcknowledgedBy = null;
        e.TaxIdWaiverReason = null;
        Audit(db, companyId, SubjectEmployee, e.Id, userId, before, new { revoked = true, reason = why, ruleCode = RuleCode, legalReference = LegalReference });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>ฝ่ายค้าน MED-2: รับทราบผู้รับใบ 50 ทวิ ภ.ง.ด.1 ที่ไม่ใช่พนักงาน — กติกาเดียวกัน (ไม่มีเลขเลยเท่านั้น)</summary>
    public static async Task AcknowledgeContactAsync(AccountingDbContext db, Guid companyId, Guid contactId, string? reason, Guid? userId, CancellationToken ct = default)
    {
        var why = RequireReason(reason, "ผู้รับเงินได้ไม่มีเลขประจำตัว 13 หลัก");
        var c = await db.Contacts.FirstOrDefaultAsync(x => x.Id == contactId && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        if (EmployeeTaxIdentity.WaiverRefusal(c.TaxId, null) is string refused) throw new BusinessRuleException(refused, RuleCode, 409);
        var before = new { c.TaxIdWaiverAcknowledgedAt, c.TaxIdWaiverReason };
        c.TaxIdWaiverAcknowledgedAt = DateTime.UtcNow;
        c.TaxIdWaiverAcknowledgedBy = userId;
        c.TaxIdWaiverReason = why;
        Audit(db, companyId, SubjectContact, c.Id, userId, before, new { c.TaxIdWaiverAcknowledgedAt, c.TaxIdWaiverReason, ruleCode = RuleCode, legalReference = LegalReference });
        await db.SaveChangesAsync(ct);
    }

    public static async Task RevokeContactAsync(AccountingDbContext db, Guid companyId, Guid contactId, string? reason, Guid? userId, CancellationToken ct = default)
    {
        var why = RequireReason(reason, "ถอนการรับทราบ");
        var c = await db.Contacts.FirstOrDefaultAsync(x => x.Id == contactId && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        if (c.TaxIdWaiverAcknowledgedAt == null) throw new BusinessRuleException("ผู้ติดต่อนี้ยังไม่ได้รับทราบ — ไม่มีอะไรให้ถอน", RuleCode, 409);
        var before = new { c.TaxIdWaiverAcknowledgedAt, c.TaxIdWaiverReason };
        c.TaxIdWaiverAcknowledgedAt = null;
        c.TaxIdWaiverAcknowledgedBy = null;
        c.TaxIdWaiverReason = null;
        Audit(db, companyId, SubjectContact, c.Id, userId, before, new { revoked = true, reason = why, ruleCode = RuleCode, legalReference = LegalReference });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>LOW (ง) pure: การรับทราบหมดผลเมื่อมีเลข 13 หลักที่ถูกต้องแล้ว (ล้างทิ้ง — ไม่กลับมามีผลถ้าช่องถูกล้างภายหลัง)</summary>
    public static bool ShouldClear(DateTime? acknowledgedAt, string? taxId, string? citizenId)
        => acknowledgedAt != null && EmployeeTaxIdentity.Inspect(taxId, citizenId).Problem == TaxIdProblem.None;

    internal static void Audit(AccountingDbContext db, Guid companyId, string subjectType, Guid subjectId, Guid? userId, object before, object after)
    {
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = subjectType + ".TaxIdWaiver", EntityId = subjectId.ToString(),
            OldValues = JsonSerializer.Serialize(before), NewValues = JsonSerializer.Serialize(after), Timestamp = DateTime.UtcNow,
        });
    }
}

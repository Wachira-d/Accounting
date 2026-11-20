using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 275 แพ็ก E (T4 H-2 · F9 + F10) — <b>ผู้ติดต่อ "เลขขยะ" ที่เส้นเงินเดือนสร้างไว้ก่อนรอบ 274</b>: รายงานอ่านอย่างเดียว → ซ่อมเฉพาะแถวที่ผู้ใช้เลือก
/// (รูปแบบเดียวกับ <c>StockLedger.FindProductTotalMismatchesAsync</c>/<c>RepairProductTotalsAsync</c>) + ด่านกันไม่ให้ถูกเลือกเป็นผู้ถูกหักใน ภ.ง.ด.3/53 ระหว่างรอซ่อม
///
/// <para><b>ที่มา</b>: ก่อนรอบ 274 ตัวตัดสินเลขพนักงานตัดตัวเลขจาก ciphertext ⇒ 50 ทวิ รายเดือนสร้าง <c>Contact</c> (IsSupplier) ด้วยเลข 5–14 หลัก ·
/// ออกใบใหม่หลังแก้ ⇒ ได้ผู้ติดต่อใหม่ แต่ตัวขยะค้างในรายชื่อผู้ขาย (ชื่อพนักงานซ้ำสองแถว) และใบของงวดที่ยื่น ภ.ง.ด.1 แล้วยังพิมพ์เลขขยะ (สำเนา ณ วันออก) ·
/// รายงานเดิมสั่ง "สร้างเอกสารใหม่" ซึ่งระบบปฏิเสธในงวดที่ยื่นแล้ว (ทางตัน)</para>
///
/// <para><b>"มาจากเงินเดือน"</b> = ผูกเป็นผู้ติดต่อของพนักงาน (<c>Employee.ContactId</c>) หรือถูกอ้างโดย 50 ทวิ ที่ออกจากรอบเงินเดือน (<c>SourcePayrollRunId</c>) ·
/// <b>"เลขขยะ"</b> = มีตัวเลขแต่ไม่ใช่ 13 หลักที่ผ่าน mod-11 (<see cref="EmployeeTaxIdentity.InspectValue"/>) — ผู้ขายจริงที่เลขไม่ครบ (ไม่ได้มาจากเงินเดือน) ไม่ถูกแตะ ·
/// รายงานยังชี้ผู้ติดต่อจากเงินเดือนที่เลข 13 หลักผ่าน checksum <b>แต่ไม่ตรงกับเลขในทะเบียนพนักงาน</b> (ขยะที่ผ่าน checksum โดยบังเอิญ ~1/11)</para>
///
/// <para><b>ซ่อม</b> (เซิร์ฟเวอร์คำนวณแผนเอง — ผู้ใช้เลือกแถวเท่านั้น): ไม่มีผู้ติดต่ออื่นถือเลขที่ถูก ⇒ แก้เลขของผู้ติดต่อนี้ · มีแล้ว ⇒ ย้ายใบ 50 ทวิ + การผูกพนักงาน
/// ไปผู้ติดต่อที่ถูกแล้วปิดใช้ตัวขยะ · ใบที่อ้างถูกประทับสำเนาผู้ถูกหักใหม่ (เลขที่ใบ/ยอดไม่เปลี่ยน) · ใบที่อยู่ใน ภ.ง.ด.1 ที่ยื่นแล้ว
/// (<see cref="WhtCertVoidGuard"/>) <b>ต้องยืนยันว่าจะยื่น ภ.ง.ด.1 เพิ่มเติม</b> — ไม่แก้ใบที่ยื่นแล้วเงียบ ๆ · audit chain ทุกแถว (เลขปิดบัง)</para>
/// <para>ทุก query กรอง <c>CompanyId</c> (กฎ M)</para>
/// </summary>
public static class PayrollJunkPayee
{
    public const string QuarantineRuleCode = "RD-50TWI-PAYROLL-JUNK-PAYEE";
    public const string RepairRuleCode = "RD-50TWI-PAYROLL-JUNK-PAYEE-REPAIR";
    public const string LegalReference = "ป.รัษฎากร ม.50 ทวิ · ม.58 (ยื่นรายการเพิ่มเติม)";
    public const string ToolName = "หัวข้อ “ผู้ติดต่อเลขผิดจากเงินเดือน” ใน " + EncryptionHealthText.PageName;

    public enum Plan
    {
        /// <summary>แก้เลขของผู้ติดต่อนี้เป็นเลขของพนักงาน (ไม่มีผู้ติดต่ออื่นถือเลขนั้น) — รับทราบไม่มีเลขแล้ว ⇒ ล้างเลข</summary>
        FixTaxId,
        /// <summary>มีผู้ติดต่อที่ถือเลขที่ถูกอยู่แล้ว ⇒ ย้ายใบ + การผูกพนักงานไปตัวนั้น แล้วปิดใช้ตัวนี้</summary>
        MoveToExistingContact,
        /// <summary>เลขในทะเบียนพนักงานยังใช้ไม่ได้ — แก้ที่ทะเบียน (หรือรับทราบไม่มีเลข) ก่อน</summary>
        NeedsEmployeeFix,
        /// <summary>หาพนักงานเจ้าของไม่ได้/ได้หลายคน — ไม่เดา</summary>
        NoEmployeeMatch,
    }

    public sealed record Row(Guid ContactId, string Name, string Masked, string Problem, string? EmployeeCode, string? EmployeeName,
        Plan Plan, string Action, int CertCount, IReadOnlyList<string> FiledPeriods, bool Repairable,
        string? TargetTaxId = null, Guid? TargetContactId = null, Guid? EmployeeId = null);

    public sealed record RepairOutcome(Guid ContactId, bool Repaired, string Message);

    /// <summary>pure — ค่าใน <c>Contact.TaxId</c> เป็น "เลขขยะ" ไหม (มีตัวเลข/ciphertext แต่ไม่ใช่ 13 หลัก + mod-11)</summary>
    public static bool IsJunkTaxId(string? taxId)
        => EmployeeTaxIdentity.InspectValue(taxId).Problem is TaxIdProblem.Invalid or TaxIdProblem.Undecryptable;

    /// <summary>ข้อความด่านกัน (ภ.ง.ด.3/53) — ชื่อ + เลขปิดบัง + ทางไปต่อ</summary>
    public static string QuarantineMessage(string? name, string? taxId)
        => $"ผู้ติดต่อ “{name}” เป็นผู้ติดต่อที่ระบบเงินเดือนเคยสร้างด้วยเลขประจำตัวที่ผิด ({EmployeeTaxIdentity.InspectValue(taxId).Masked}) — "
            + "ใช้เป็นผู้ถูกหักภาษีใน ภ.ง.ด.3/53 ไม่ได้ระหว่างรอซ่อม · ซ่อมที่ " + ToolName + " หรือเลือกผู้ติดต่อที่ถูกต้อง";

    /// <summary>pure — รหัสพนักงานที่เป็นไปได้จากเลขที่ใบ <c>PND1-yyyymm-รหัส[-n]</c> (<see cref="PayrollPnd1Certs.NextNumber"/>) · ไม่ใช่รูปนี้ = ว่าง</summary>
    internal static IReadOnlyList<string> EmployeeCodeCandidates(string? certificateNumber)
    {
        var n = certificateNumber ?? "";
        if (!n.StartsWith("PND1-", StringComparison.Ordinal) || n.Length < 13 || n[11] != '-'
            || !n.AsSpan(5, 6).ToString().All(char.IsDigit)) return Array.Empty<string>();
        var rest = n[12..];
        var list = new List<string> { rest };
        var dash = rest.LastIndexOf('-');
        if (dash > 0 && rest[(dash + 1)..] is { Length: > 0 } tail && tail.All(char.IsDigit)) list.Add(rest[..dash]);
        return list;
    }

    /// <summary>ผู้ติดต่อนี้ "มาจากเงินเดือน" ไหม (รวมพนักงาน/ใบที่ถูกลบ — ที่มาไม่หายเพราะลบ)</summary>
    public static bool IsPayrollOrigin(AccountingDbContext db, Guid companyId, Guid contactId)
        => db.Set<Employee>().IgnoreQueryFilters().AsNoTracking().Any(e => e.CompanyId == companyId && e.ContactId == contactId)
           || db.WithholdingTaxCerts.IgnoreQueryFilters().AsNoTracking()
               .Any(w => w.CompanyId == companyId && w.PayeeContactId == contactId && w.SourcePayrollRunId != null);

    /// <summary>รายงานอ่านอย่างเดียว — ผู้ติดต่อจากเงินเดือนที่เลขเป็นขยะ (หรือไม่ตรงกับทะเบียนพนักงาน) พร้อมแผนซ่อมที่เซิร์ฟเวอร์คำนวณ</summary>
    public static async Task<IReadOnlyList<Row>> FindAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var employees = await db.Set<Employee>().AsNoTracking()
            .Where(e => e.CompanyId == companyId)
            .Select(e => new { e.Id, e.EmployeeCode, e.FirstNameTh, e.LastNameTh, e.FirstNameEn, e.LastNameEn, e.ContactId,
                e.TaxId, e.CitizenId, e.TaxIdWaiverAcknowledgedAt })
            .ToListAsync(ct);
        var payrollCerts = await db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.SourcePayrollRunId != null)
            .Select(w => new { w.Id, w.PayeeContactId, w.CertificateNumber, w.Status })
            .ToListAsync(ct);
        var originIds = employees.Where(e => e.ContactId != null).Select(e => e.ContactId!.Value)
            .Concat(payrollCerts.Select(c => c.PayeeContactId)).Distinct().ToList();
        if (originIds.Count == 0) return Array.Empty<Row>();
        var contacts = await db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && originIds.Contains(c.Id) && c.IsActive && c.TaxId != null)
            .Select(c => new { c.Id, c.Name, c.TaxId })
            .ToListAsync(ct);
        var byCode = employees.GroupBy(e => (e.EmployeeCode ?? "").Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var rows = new List<Row>();
        foreach (var c in contacts.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            // ── หาพนักงานเจ้าของ: ผูกตรง (ContactId) ก่อน · ไม่งั้นจากเลขที่ใบเงินเดือน ──
            var owners = employees.Where(e => e.ContactId == c.Id).Select(e => e.Id).ToHashSet();
            var certs = payrollCerts.Where(w => w.PayeeContactId == c.Id).ToList();
            if (owners.Count == 0)
                foreach (var w in certs)
                    foreach (var code in EmployeeCodeCandidates(w.CertificateNumber))
                        if (byCode.TryGetValue(code, out var hit)) { foreach (var e in hit) owners.Add(e.Id); break; }
            var owner = owners.Count == 1 ? employees.First(e => e.Id == owners.First()) : null;
            var ownerId = owner == null ? null : EmployeeTaxIdentity.ForFiling(owner.TaxId, owner.CitizenId, owner.TaxIdWaiverAcknowledgedAt);

            var cid = EmployeeTaxIdentity.InspectValue(c.TaxId);
            string problem;
            if (IsJunkTaxId(c.TaxId)) problem = EmployeeTaxIdentity.InvalidText + $" ({cid.Masked})";
            // ผ่าน checksum แต่ไม่ตรงกับทะเบียน — เฉพาะผู้ติดต่อที่ใบเงินเดือนอ้าง (ผูกตรงอย่างเดียวอาจเป็นผู้ติดต่อจริงที่ HR ผูกไว้ — ไม่แตะ)
            else if (cid.Ok && certs.Count > 0 && ownerId?.Kind == EmployeeTaxIdentity.FilingKind.Ok && ownerId.Value != cid.Value)
                problem = "เลข 13 หลักไม่ตรงกับเลขในทะเบียนพนักงาน";
            else continue;   // เลขถูกและตรง/ว่าง — ไม่ใช่ของเสีย

            var liveCerts = certs.Where(w => w.Status != WithholdingTaxCertStatus.Voided).ToList();
            var filed = new List<string>();
            foreach (var w in liveCerts)
                if (await WhtCertVoidGuard.CheckAsync(db, companyId, new[] { w.Id }, ct) != null) filed.Add(w.CertificateNumber);

            Plan plan; string? target = null; Guid? targetContact = null; string action;
            if (owner == null)
            {
                plan = Plan.NoEmployeeMatch;
                action = owners.Count > 1 ? "ใบของผู้ติดต่อนี้ชี้ไปพนักงานหลายคน — ระบบไม่เดา · แก้ผู้ถูกหักในใบด้วยมือ"
                    : "หาพนักงานเจ้าของไม่พบ — ตรวจผู้ติดต่อนี้ด้วยมือ (ห้ามใช้ออก 50 ทวิ ภ.ง.ด.3/53 จนกว่าจะแก้เลข)";
            }
            else if (ownerId!.Kind is EmployeeTaxIdentity.FilingKind.Ok or EmployeeTaxIdentity.FilingKind.WaivedNoId)
            {
                target = ownerId.Value;   // null = รับทราบไม่มีเลข ⇒ ล้างเลขขยะ
                // ตัวจับคู่เดียวของระบบ (เลขภาษี + สาขา — บุคคลธรรมดา = สำนักงานใหญ่) · ไม่นับตัวเอง/ที่ปิดใช้
                Guid? other = null;
                if (target != null)
                {
                    var m = await ContactTaxBranchKey.FindAsync(db.Contacts.AsNoTracking().Where(x => x.Id != c.Id && x.IsActive),
                        companyId, target, "00000", ct);
                    other = m.ContactId;
                }
                plan = other != null ? Plan.MoveToExistingContact : Plan.FixTaxId;
                targetContact = other;
                action = (plan == Plan.MoveToExistingContact
                        ? $"ย้ายใบ 50 ทวิ {liveCerts.Count} ใบและการผูกพนักงานไปผู้ติดต่อที่มีเลขถูกอยู่แล้ว แล้วปิดใช้ผู้ติดต่อนี้"
                        : target == null ? "ล้างเลขที่ผิด (พนักงานรับทราบแล้วว่าไม่มีเลข 13 หลัก)" : "แก้เลขของผู้ติดต่อนี้เป็นเลขในทะเบียนพนักงาน")
                    + (liveCerts.Count > 0 ? " · ใบที่อ้างพิมพ์ใหม่ได้เลขที่ถูก (เลขที่ใบ/ยอดเดิม)" : "")
                    + (filed.Count > 0 ? $" · ⚠️ {filed.Count} ใบอยู่ใน ภ.ง.ด.1 ที่ยื่นแล้ว — ต้องยืนยันว่าจะยื่น ภ.ง.ด.1 เพิ่มเติมของเดือนนั้น (ดาวน์โหลดไฟล์ใหม่ที่ใช้เลขที่ถูก) และพิมพ์ใบให้พนักงานใหม่" : "");
            }
            else
            {
                plan = Plan.NeedsEmployeeFix;
                action = "แก้เลขที่ทะเบียนพนักงานก่อน (" + ownerId.Reason + ") แล้วกลับมาซ่อมแถวนี้";
            }
            rows.Add(new Row(c.Id, c.Name, cid.Masked ?? "", problem, owner?.EmployeeCode,
                owner == null ? null : $"{owner.FirstNameTh} {owner.LastNameTh}".Trim() is { Length: > 0 } th ? th : $"{owner.FirstNameEn} {owner.LastNameEn}".Trim(),
                plan, action, liveCerts.Count, filed, plan is Plan.FixTaxId or Plan.MoveToExistingContact, target, targetContact, owner?.Id));
        }
        return rows;
    }

    /// <summary>ซ่อมเฉพาะแถวที่เลือก — แผนคำนวณใหม่จากข้อมูลปัจจุบัน (ไม่เชื่อแผนจากหน้าจอ) · ใบในงวดที่ยื่นแล้วต้อง <paramref name="confirmFiledAmendment"/> ·
    /// audit chain ต่อแถว · บันทึกครั้งเดียว (ล้ม = ไม่มีแถวครึ่ง ๆ)</summary>
    public static async Task<IReadOnlyList<RepairOutcome>> RepairAsync(AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> contactIds,
        bool confirmFiledAmendment, Guid? userId, CancellationToken ct = default)
    {
        var wanted = contactIds.Distinct().ToHashSet();
        var plans = (await FindAsync(db, companyId, ct)).Where(r => wanted.Contains(r.ContactId)).ToDictionary(r => r.ContactId);
        var outcomes = new List<RepairOutcome>();
        foreach (var id in wanted)
        {
            if (!plans.TryGetValue(id, out var r)) { outcomes.Add(new(id, false, "ไม่อยู่ในรายการที่ต้องซ่อมแล้ว (อาจถูกซ่อมไปแล้ว)")); continue; }
            if (!r.Repairable) { outcomes.Add(new(id, false, $"{r.Name}: {r.Action}")); continue; }
            if (r.FiledPeriods.Count > 0 && !confirmFiledAmendment)
            {
                outcomes.Add(new(id, false, $"{r.Name}: ใบ {string.Join(", ", r.FiledPeriods)} อยู่ใน ภ.ง.ด.1 ที่ยื่นแล้ว — ติ๊ก “ยืนยันว่าจะยื่น ภ.ง.ด.1 เพิ่มเติม” แล้วซ่อมใหม่"));
                continue;
            }
            var contact = await db.Contacts.FirstAsync(c => c.Id == id && c.CompanyId == companyId, ct);
            var certs = await db.WithholdingTaxCerts
                .Where(w => w.CompanyId == companyId && w.PayeeContactId == id && w.Status != WithholdingTaxCertStatus.Voided)
                .ToListAsync(ct);
            var oldMasked = r.Masked;
            if (r.Plan == Plan.MoveToExistingContact)
            {
                foreach (var w in certs) w.PayeeContactId = r.TargetContactId!.Value;   // เปลี่ยนผู้ถูกหัก ⇒ ประทับสำเนาใหม่ที่ SaveChanges
                var linked = await db.Set<Employee>().Where(e => e.CompanyId == companyId && e.ContactId == id).ToListAsync(ct);
                foreach (var e in linked) e.ContactId = r.TargetContactId!.Value;
                contact.IsActive = false;
            }
            else
            {
                contact.TaxId = r.TargetTaxId;
                foreach (var w in certs) w.PayeeSnapshotJson = null;   // ประทับสำเนาใหม่จากผู้ติดต่อที่แก้แล้ว (SaveChanges · ใบที่ออกแล้วเท่านั้น)
            }
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "Contact", EntityId = id.ToString(),
                OldValues = JsonSerializer.Serialize(new { taxIdMasked = oldMasked, problem = r.Problem, isActive = true }),
                NewValues = JsonSerializer.Serialize(new
                {
                    plan = r.Plan.ToString(), taxIdMasked = r.TargetTaxId == null ? null : PiiMask.CitizenId(r.TargetTaxId),
                    movedToContactId = r.TargetContactId, employeeCode = r.EmployeeCode,
                    certificates = certs.Select(w => w.CertificateNumber).ToList(), filedCertificates = r.FiledPeriods,
                    filedAmendmentConfirmed = r.FiledPeriods.Count > 0, ruleCode = RepairRuleCode, legalReference = LegalReference,
                }),
                Timestamp = DateTime.UtcNow,
            });
            outcomes.Add(new(id, true, $"{r.Name}: ซ่อมแล้ว ({r.Plan})"
                + (r.FiledPeriods.Count > 0 ? $" — ยื่น ภ.ง.ด.1 เพิ่มเติมของงวดที่มีใบ {string.Join(", ", r.FiledPeriods)} และพิมพ์ใบให้พนักงานใหม่" : "")));
        }
        await db.SaveChangesAsync(ct);
        return outcomes;
    }
}

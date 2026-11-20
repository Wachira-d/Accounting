using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 275 แพ็ก E (T4 H-2 · F9 + F10 · ฝ่ายค้าน MED-3 + LOW (ก)) — <b>ผู้ติดต่อ "เลขขยะ" ที่เส้นเงินเดือนสร้างไว้ก่อนรอบ 274</b>: รายงานอ่านอย่างเดียว →
/// ซ่อมเฉพาะแถวที่เลือก (รูปแบบเดียวกับ <c>StockLedger.FindProductTotalMismatchesAsync</c>/<c>RepairProductTotalsAsync</c>) + ด่านกันไม่ให้ถูกเลือกเป็นผู้ถูกหักใน
/// ภ.ง.ด.3/53 ระหว่างรอซ่อม (<c>AccountingDbContext.GuardPayrollJunkPayees</c> — กุญแจเดียวกัน <see cref="IsPayrollCreated"/>)
///
/// <para><b>"สร้างโดยเส้นเงินเดือน"</b> (LOW (ก)) = <c>CreatedBy</c> เป็นเครื่องหมายของระบบเงินเดือน (<see cref="PayrollCreatedMarkers"/>) <b>หรือ</b>
/// (ข้อมูลก่อนมีเครื่องหมาย) ถูกอ้างโดย 50 ทวิ ที่ออกจากรอบเงินเดือน + ไม่มีผู้สร้าง + ไม่ใช่ลูกค้า + ไม่มีเอกสาร/ใบ 50 ทวิ อื่นอ้าง + HR ไม่ได้ผูกกับพนักงาน ·
/// ผู้ติดต่อที่ HR ผูกเอง/ผู้ขายจริงไม่ถูกจัดเป็นของเสีย · <b>"เลขขยะ"</b> = มีตัวเลขแต่ไม่ใช่ 13 หลักที่ผ่าน mod-11 · รายงานยังชี้ตัวที่ผ่าน checksum แต่ไม่ตรงทะเบียนพนักงาน</para>
///
/// <para><b>ซ่อม</b> (แผนจากเซิร์ฟเวอร์): แก้เลขของผู้ติดต่อ หรือย้ายไปผู้ติดต่อที่ถือเลขถูกแล้วปิดใช้ตัวขยะ ·
/// ใบของงวดที่<b>ยังไม่ยื่น</b> ⇒ ประทับสำเนาผู้ถูกหักใหม่ · ใบของงวดที่<b>ยื่น ภ.ง.ด.1 แล้ว</b> (MED-3) ⇒ <b>ไม่เขียนทับใบเดิม</b> — ออกใบแทน (เลขที่ใหม่ ยอด/งวด/บรรทัดเดิม ·
/// ใบเดิมคงอยู่พร้อมสำเนาเดิม สถานะยกเลิก + <c>SupersededByCertId</c> แบบกลไกรอบ 271 — ยอดรวมของงวดไม่เปลี่ยนเพราะใบแทนยอดเท่ากัน) + รายการค้าง
/// <see cref="Pnd1AmendmentRequired"/> "ต้องยื่น ภ.ง.ด.1 เพิ่มเติม" จนผู้ใช้บันทึกว่ายื่นแล้ว · ต้องยืนยัน<b>ทีละงวด</b> · audit chain ทุกแถว (เลขปิดบัง)</para>
/// <para>ทุก query กรอง <c>CompanyId</c> (กฎ M)</para>
/// </summary>
public static class PayrollJunkPayee
{
    public const string QuarantineRuleCode = "RD-50TWI-PAYROLL-JUNK-PAYEE";
    public const string RepairRuleCode = "RD-50TWI-PAYROLL-JUNK-PAYEE-REPAIR";
    public const string ReplaceRuleCode = "RD-50TWI-REPLACED-FILED";
    public const string LegalReference = "ป.รัษฎากร ม.50 ทวิ · ม.58 (ยื่นรายการเพิ่มเติม)";
    public const string ToolName = "หัวข้อ “ผู้ติดต่อเลขผิดจากเงินเดือน” ใน " + EncryptionHealthText.PageName;

    public const string CreatedByPayrollPnd1 = "system:payroll-pnd1";
    public const string CreatedByPayrollNoTaxId = "system:payroll-no-taxid";
    /// <summary>เครื่องหมาย <c>CreatedBy</c> ของผู้ติดต่อที่ระบบเงินเดือน/HR สร้างเอง</summary>
    public static readonly string[] PayrollCreatedMarkers = { CreatedByPayrollPnd1, CreatedByPayrollNoTaxId, "system:hr-sync", "system:expense-claim" };

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

    /// <param name="FiledPeriods">งวดที่ใบของผู้ติดต่อนี้อยู่ใน ภ.ง.ด.1 ที่ยื่นแล้ว (รูป <c>yyyy-MM</c>) — ต้องยืนยันทีละงวด</param>
    /// <param name="FiledCertificates">เลขที่ใบในงวดที่ยื่นแล้ว (จะได้ใบแทน)</param>
    public sealed record Row(Guid ContactId, string Name, string Masked, string Problem, string? EmployeeCode, string? EmployeeName,
        Plan Plan, string Action, int CertCount, IReadOnlyList<string> FiledPeriods, IReadOnlyList<string> FiledCertificates, bool Repairable,
        string? TargetTaxId = null, Guid? TargetContactId = null, Guid? EmployeeId = null);

    public sealed record RepairOutcome(Guid ContactId, bool Repaired, string Message, IReadOnlyList<string>? AmendmentPeriods = null);

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

    /// <summary>pure — ส่วนตัดสินของ <see cref="IsPayrollCreated"/> (ข้อมูลที่โหลดแล้ว)</summary>
    internal static bool IsPayrollCreated(string? createdBy, bool isCustomer, bool referencedByPayrollCert, bool referencedElsewhere, bool linkedByEmployee)
    {
        if (createdBy != null && PayrollCreatedMarkers.Contains(createdBy, StringComparer.Ordinal)) return true;
        return string.IsNullOrEmpty(createdBy) && referencedByPayrollCert && !isCustomer && !referencedElsewhere && !linkedByEmployee;
    }

    /// <summary>LOW (ก) กุญแจเดียวของรายงาน + ด่าน SaveChanges — ผู้ติดต่อนี้ "สร้างโดยเส้นเงินเดือน" ไหม</summary>
    public static bool IsPayrollCreated(AccountingDbContext db, Guid companyId, Contact c)
    {
        if (c.CreatedBy != null && PayrollCreatedMarkers.Contains(c.CreatedBy, StringComparer.Ordinal)) return true;
        if (!string.IsNullOrEmpty(c.CreatedBy) || c.IsCustomer) return false;
        var certs = db.WithholdingTaxCerts.IgnoreQueryFilters().AsNoTracking().Where(w => w.CompanyId == companyId && w.PayeeContactId == c.Id);
        var byPayroll = certs.Any(w => w.SourcePayrollRunId != null);
        if (!byPayroll) return false;
        var elsewhere = certs.Any(w => w.SourcePayrollRunId == null)
            || db.Documents.IgnoreQueryFilters().AsNoTracking().Any(d => d.CompanyId == companyId && (d.ContactId == c.Id || d.WhtPayeeContactId == c.Id));
        var linked = db.Set<Employee>().IgnoreQueryFilters().AsNoTracking().Any(e => e.CompanyId == companyId && e.ContactId == c.Id);
        return IsPayrollCreated(c.CreatedBy, c.IsCustomer, byPayroll, elsewhere, linked);
    }

    private static string PeriodKey(int year, int month) => $"{year:D4}-{month:D2}";

    /// <summary>รายงานอ่านอย่างเดียว — ผู้ติดต่อที่สร้างโดยเส้นเงินเดือนที่เลขเป็นขยะ (หรือไม่ตรงกับทะเบียนพนักงาน) พร้อมแผนซ่อมที่เซิร์ฟเวอร์คำนวณ</summary>
    public static async Task<IReadOnlyList<Row>> FindAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var employees = await db.Set<Employee>().AsNoTracking()
            .Where(e => e.CompanyId == companyId)
            .Select(e => new { e.Id, e.EmployeeCode, e.FirstNameTh, e.LastNameTh, e.FirstNameEn, e.LastNameEn, e.ContactId,
                e.TaxId, e.CitizenId, e.TaxIdWaiverAcknowledgedAt })
            .ToListAsync(ct);
        var payrollCerts = await db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.SourcePayrollRunId != null)
            .Select(w => new { w.Id, w.PayeeContactId, w.CertificateNumber, w.Status, w.TaxYear, w.TaxMonth })
            .ToListAsync(ct);
        var payrollCertContacts = payrollCerts.Select(c => c.PayeeContactId).ToHashSet();
        var contacts = await db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.IsActive && c.TaxId != null
                && (payrollCertContacts.Contains(c.Id) || (c.CreatedBy != null && PayrollCreatedMarkers.Contains(c.CreatedBy))))
            .Select(c => new { c.Id, c.Name, c.TaxId, c.CreatedBy, c.IsCustomer })
            .ToListAsync(ct);
        if (contacts.Count == 0) return Array.Empty<Row>();
        var ids = contacts.Select(c => c.Id).ToList();
        var otherCertRefs = (await db.WithholdingTaxCerts.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.SourcePayrollRunId == null && ids.Contains(w.PayeeContactId))
            .Select(w => w.PayeeContactId).Distinct().ToListAsync(ct)).ToHashSet();
        var docRefs = (await db.Documents.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.CompanyId == companyId && (ids.Contains(d.ContactId) || (d.WhtPayeeContactId != null && ids.Contains(d.WhtPayeeContactId.Value))))
            .Select(d => new { d.ContactId, d.WhtPayeeContactId }).ToListAsync(ct))
            .SelectMany(d => new[] { d.ContactId, d.WhtPayeeContactId ?? Guid.Empty }).ToHashSet();
        var linkedIds = employees.Where(e => e.ContactId != null).Select(e => e.ContactId!.Value).ToHashSet();
        var byCode = employees.GroupBy(e => (e.EmployeeCode ?? "").Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var rows = new List<Row>();
        foreach (var c in contacts.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var certs = payrollCerts.Where(w => w.PayeeContactId == c.Id).ToList();
            if (!IsPayrollCreated(c.CreatedBy, c.IsCustomer, certs.Count > 0, otherCertRefs.Contains(c.Id) || docRefs.Contains(c.Id), linkedIds.Contains(c.Id)))
                continue;
            // ── หาพนักงานเจ้าของ: ผูกตรง (ContactId) ก่อน · ไม่งั้นจากเลขที่ใบเงินเดือน ──
            var owners = employees.Where(e => e.ContactId == c.Id).Select(e => e.Id).ToHashSet();
            if (owners.Count == 0)
                foreach (var w in certs)
                    foreach (var code in EmployeeCodeCandidates(w.CertificateNumber))
                        if (byCode.TryGetValue(code, out var hit)) { foreach (var e in hit) owners.Add(e.Id); break; }
            var owner = owners.Count == 1 ? employees.First(e => e.Id == owners.First()) : null;
            var ownerId = owner == null ? null : EmployeeTaxIdentity.ForFiling(owner.TaxId, owner.CitizenId, owner.TaxIdWaiverAcknowledgedAt);

            var cid = EmployeeTaxIdentity.InspectValue(c.TaxId);
            string problem;
            if (IsJunkTaxId(c.TaxId)) problem = EmployeeTaxIdentity.InvalidText + $" ({cid.Masked})";
            // ผ่าน checksum แต่ไม่ตรงกับทะเบียน — เฉพาะผู้ติดต่อที่ใบเงินเดือนอ้าง
            else if (cid.Ok && certs.Count > 0 && ownerId?.Kind == EmployeeTaxIdentity.FilingKind.Ok && ownerId.Value != cid.Value)
                problem = "เลข 13 หลักไม่ตรงกับเลขในทะเบียนพนักงาน";
            else continue;   // เลขถูกและตรง/ว่าง — ไม่ใช่ของเสีย

            var liveCerts = certs.Where(w => w.Status != WithholdingTaxCertStatus.Voided).ToList();
            var filedCerts = new List<string>();
            var filedPeriods = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var w in liveCerts)
                if (await WhtCertVoidGuard.CheckAsync(db, companyId, new[] { w.Id }, ct) != null)
                {
                    filedCerts.Add(w.CertificateNumber);
                    filedPeriods.Add(PeriodKey(w.TaxYear, w.TaxMonth));
                }

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
                Guid? other = null;
                if (target != null)
                {
                    // ตัวจับคู่เดียวของระบบ (เลขภาษี + สาขา — บุคคลธรรมดา = สำนักงานใหญ่) · ไม่นับตัวเอง/ที่ปิดใช้
                    var m = await ContactTaxBranchKey.FindAsync(db.Contacts.AsNoTracking().Where(x => x.Id != c.Id && x.IsActive),
                        companyId, target, "00000", ct);
                    other = m.ContactId;
                }
                plan = other != null ? Plan.MoveToExistingContact : Plan.FixTaxId;
                targetContact = other;
                var open = liveCerts.Count - filedCerts.Count;
                action = (plan == Plan.MoveToExistingContact
                        ? "ย้ายการผูกพนักงานไปผู้ติดต่อที่มีเลขถูกอยู่แล้ว แล้วปิดใช้ผู้ติดต่อนี้"
                        : target == null ? "ล้างเลขที่ผิด (พนักงานรับทราบแล้วว่าไม่มีเลข 13 หลัก)" : "แก้เลขของผู้ติดต่อนี้เป็นเลขในทะเบียนพนักงาน")
                    + (open > 0 ? $" · ใบงวดที่ยังไม่ยื่น {open} ใบพิมพ์ใหม่ได้เลขที่ถูก (เลขที่ใบ/ยอดเดิม)" : "")
                    + (filedCerts.Count > 0
                        ? $" · ⚠️ {filedCerts.Count} ใบอยู่ใน ภ.ง.ด.1 ที่ยื่นแล้ว (งวด {string.Join(", ", filedPeriods)}) — ระบบออก<b>ใบแทน</b>เลขที่ใหม่ (ใบเดิมคงอยู่ สถานะยกเลิก-แทนด้วยใบใหม่) "
                          + "และขึ้นรายการค้าง “ต้องยื่น ภ.ง.ด.1 เพิ่มเติม” ของงวดนั้น — ต้องติ๊กยืนยันทีละงวด" : "");
            }
            else
            {
                plan = Plan.NeedsEmployeeFix;
                action = "แก้เลขที่ทะเบียนพนักงานก่อน (" + ownerId.Reason + ") แล้วกลับมาซ่อมแถวนี้";
            }
            rows.Add(new Row(c.Id, c.Name, cid.Masked ?? "", problem, owner?.EmployeeCode,
                owner == null ? null : $"{owner.FirstNameTh} {owner.LastNameTh}".Trim() is { Length: > 0 } th ? th : $"{owner.FirstNameEn} {owner.LastNameEn}".Trim(),
                plan, action.Replace("<b>", "").Replace("</b>", ""), liveCerts.Count, filedPeriods.ToList(), filedCerts,
                plan is Plan.FixTaxId or Plan.MoveToExistingContact, target, targetContact, owner?.Id));
        }
        return rows;
    }

    /// <summary>ซ่อมเฉพาะแถวที่เลือก — แผนคำนวณใหม่จากข้อมูลปัจจุบัน (ไม่เชื่อแผนจากหน้าจอ) · ใบในงวดที่ยื่นแล้วต้องอยู่ใน <paramref name="confirmedPeriods"/>
    /// (ยืนยันทีละงวด <c>yyyy-MM</c>) · ใบที่ยื่นแล้วได้ใบแทน (ไม่เขียนทับ) + รายการค้างยื่นเพิ่มเติม · audit chain · บันทึกครั้งเดียว (ล้ม = ไม่มีแถวครึ่ง ๆ)</summary>
    public static async Task<IReadOnlyList<RepairOutcome>> RepairAsync(AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> contactIds,
        IReadOnlyCollection<string>? confirmedPeriods, Guid? userId, CancellationToken ct = default)
    {
        var wanted = contactIds.Distinct().ToHashSet();
        var confirmed = (confirmedPeriods ?? Array.Empty<string>()).Select(p => (p ?? "").Trim()).ToHashSet(StringComparer.Ordinal);
        var plans = (await FindAsync(db, companyId, ct)).Where(r => wanted.Contains(r.ContactId)).ToDictionary(r => r.ContactId);
        var outcomes = new List<RepairOutcome>();
        var amendments = new Dictionary<string, Pnd1AmendmentRequired>(StringComparer.Ordinal);
        foreach (var id in wanted)
        {
            if (!plans.TryGetValue(id, out var r)) { outcomes.Add(new(id, false, "ไม่อยู่ในรายการที่ต้องซ่อมแล้ว (อาจถูกซ่อมไปแล้ว)")); continue; }
            if (!r.Repairable) { outcomes.Add(new(id, false, $"{r.Name}: {r.Action}")); continue; }
            var missing = r.FiledPeriods.Where(p => !confirmed.Contains(p)).ToList();
            if (missing.Count > 0)
            {
                outcomes.Add(new(id, false, $"{r.Name}: ใบ {string.Join(", ", r.FiledCertificates)} อยู่ใน ภ.ง.ด.1 ที่ยื่นแล้ว — ยืนยันว่าจะยื่น ภ.ง.ด.1 เพิ่มเติมของงวด "
                    + string.Join(", ", missing) + " ก่อนซ่อม (ยืนยันทีละงวด)"));
                continue;
            }
            var contact = await db.Contacts.FirstAsync(c => c.Id == id && c.CompanyId == companyId, ct);
            var certs = await db.WithholdingTaxCerts.Include(w => w.Lines)
                .Where(w => w.CompanyId == companyId && w.PayeeContactId == id && w.Status != WithholdingTaxCertStatus.Voided)
                .ToListAsync(ct);
            var emp = r.EmployeeId is Guid eid ? await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == eid && e.CompanyId == companyId, ct) : null;
            var payeeContactId = r.Plan == Plan.MoveToExistingContact ? r.TargetContactId!.Value : id;
            var replaced = new List<string>();
            var touchedPeriods = new SortedSet<string>(StringComparer.Ordinal);
            if (r.Plan == Plan.MoveToExistingContact)
            {
                var linked = await db.Set<Employee>().Where(e => e.CompanyId == companyId && e.ContactId == id).ToListAsync(ct);
                foreach (var e in linked) e.ContactId = payeeContactId;
                contact.IsActive = false;
            }
            else contact.TaxId = r.TargetTaxId;
            var target = r.Plan == Plan.MoveToExistingContact
                ? await db.Contacts.FirstAsync(c => c.Id == payeeContactId && c.CompanyId == companyId, ct) : contact;

            foreach (var w in certs)
            {
                if (!r.FiledCertificates.Contains(w.CertificateNumber))
                {
                    // งวดที่ยังไม่ยื่น — ใบเดิมพิมพ์ใหม่ได้เลขที่ถูก (เปลี่ยนผู้ถูกหัก ⇒ ประทับใหม่ · แก้ผู้ติดต่อ ⇒ ล้างสำเนาให้ประทับใหม่)
                    if (r.Plan == Plan.MoveToExistingContact) w.PayeeContactId = payeeContactId;
                    else w.PayeeSnapshotJson = null;
                    if (r.TargetTaxId == null) w.PayeeSnapshotJson = WhtPayeeSnapshot.Serialize(target, emp?.PassportNumber, employeeWaived: true);
                    continue;
                }
                // ── MED-3: งวดที่ยื่นแล้ว — ออกใบแทน ไม่เขียนทับใบเดิม ──
                var prefix = PayrollPnd1Certs.Prefix(w.TaxYear, w.TaxMonth);
                var taken = (await db.WithholdingTaxCerts.IgnoreQueryFilters().AsNoTracking()
                        .Where(x => x.CompanyId == companyId && x.CertificateNumber.StartsWith(prefix)).Select(x => x.CertificateNumber).ToListAsync(ct))
                    .Concat(replaced).ToHashSet(StringComparer.Ordinal);
                var neo = new WithholdingTaxCert
                {
                    CompanyId = companyId, CertificateNumber = PayrollPnd1Certs.NextNumber(w.TaxYear, w.TaxMonth, r.EmployeeCode ?? "", taken),
                    PayeeContactId = payeeContactId, TaxFormType = w.TaxFormType, TaxYear = w.TaxYear, TaxMonth = w.TaxMonth,
                    CertificateType = w.CertificateType, Status = WithholdingTaxCertStatus.Issued, TotalIncomeAmount = w.TotalIncomeAmount,
                    TotalTaxAmount = w.TotalTaxAmount, IssuedDate = DateTime.UtcNow, SourcePayrollRunId = w.SourcePayrollRunId,
                    PayeeSnapshotJson = r.TargetTaxId == null ? WhtPayeeSnapshot.Serialize(target, emp?.PassportNumber, employeeWaived: true) : null,
                };
                foreach (var l in w.Lines.OrderBy(l => l.LineOrder))
                    neo.Lines.Add(new WithholdingTaxCertLine
                    {
                        LineOrder = l.LineOrder, IncomeTypeCode = l.IncomeTypeCode, IncomeDescription = l.IncomeDescription, PaymentDate = l.PaymentDate,
                        IncomeAmount = l.IncomeAmount, TaxRate = l.TaxRate, TaxAmount = l.TaxAmount, Condition = l.Condition,
                    });
                db.WithholdingTaxCerts.Add(neo);
                replaced.Add(neo.CertificateNumber);
                // ใบเดิมคงอยู่ (สำเนาผู้ถูกหักเดิมไม่ถูกแตะ) · ยกเลิก-แทนด้วยใบใหม่ ยอดเท่ากัน ⇒ ยอดของงวดไม่เปลี่ยน — ไม่ผ่าน WhtCertVoidGuard โดยตั้งใจ
                // (ด่านนั้นกันใบที่ยื่นแล้วหายจากยอด · ที่นี่ใบแทนรับยอดเดิมทั้งหมดในธุรกรรมเดียว)
                w.Status = WithholdingTaxCertStatus.Voided;
                w.SupersededByCertId = neo.Id;
                w.UpdatedAt = DateTime.UtcNow;
                var key = PeriodKey(w.TaxYear, w.TaxMonth);
                touchedPeriods.Add(key);
                if (!amendments.TryGetValue(key, out var am))
                {
                    am = await db.Pnd1AmendmentsRequired.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.TaxYear == w.TaxYear
                        && a.TaxMonth == w.TaxMonth && a.ResolvedAt == null, ct);
                    if (am == null)
                    {
                        am = new Pnd1AmendmentRequired { CompanyId = companyId, TaxYear = w.TaxYear, TaxMonth = w.TaxMonth,
                            Reason = "ออกใบแทน 50 ทวิ แก้เลขผู้ถูกหักภาษี (ผู้ติดต่อเลขผิดจากเงินเดือน)" };
                        db.Pnd1AmendmentsRequired.Add(am);
                    }
                    amendments[key] = am;
                }
                am.CertificateNumbers = string.Join(" · ", new[] { am.CertificateNumbers, $"{w.CertificateNumber} → {neo.CertificateNumber}" }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
                db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "WithholdingTaxCert", EntityId = w.Id.ToString(),
                    OldValues = JsonSerializer.Serialize(new { w.CertificateNumber, Status = "Issued" }),
                    NewValues = JsonSerializer.Serialize(new { Status = "Voided", SupersededByCertId = neo.Id, SupersededBy = neo.CertificateNumber,
                        Period = key, ruleCode = ReplaceRuleCode, legalReference = LegalReference }),
                    Timestamp = DateTime.UtcNow,
                });
            }
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "Contact", EntityId = id.ToString(),
                OldValues = JsonSerializer.Serialize(new { taxIdMasked = r.Masked, problem = r.Problem, isActive = true }),
                NewValues = JsonSerializer.Serialize(new
                {
                    plan = r.Plan.ToString(), taxIdMasked = r.TargetTaxId == null ? null : PiiMask.CitizenId(r.TargetTaxId),
                    movedToContactId = r.TargetContactId, employeeCode = r.EmployeeCode,
                    certificates = certs.Select(w => w.CertificateNumber).ToList(), replacements = replaced, confirmedPeriods = r.FiledPeriods,
                    ruleCode = RepairRuleCode, legalReference = LegalReference,
                }),
                Timestamp = DateTime.UtcNow,
            });
            outcomes.Add(new(id, true, $"{r.Name}: ซ่อมแล้ว ({r.Plan})"
                + (replaced.Count > 0 ? $" — ออกใบแทน {string.Join(", ", replaced)} · ยื่น ภ.ง.ด.1 เพิ่มเติมของงวด {string.Join(", ", touchedPeriods)} แล้วบันทึกที่รายการค้าง" : ""),
                touchedPeriods.ToList()));
        }
        await db.SaveChangesAsync(ct);
        return outcomes;
    }
}

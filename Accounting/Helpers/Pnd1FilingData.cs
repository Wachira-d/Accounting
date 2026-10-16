using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวโหลดเดียวของ "รายการที่อยู่ในแบบ ภ.ง.ด.1 / ภ.ง.ด.1ก"** — ไฟล์ .txt/CSV/Excel (<c>TaxFilingExportService</c>) ·
/// หน้ารายงาน ภ.ง.ด.1 ในหน้าเงินเดือน (<c>PayrollService.GeneratePnd1Async</c>) และ Db test เรียกตัวนี้ตัวเดียว ⇒
/// "ยอดบนจอ = ยอดในไฟล์" โดยโครงสร้าง ไม่ใช่โดยความระวัง (รอบ 2026-10-14 ทีม X1)
///
/// <para><b>แหล่งข้อมูล</b>
/// <list type="number">
/// <item><b>รอบเงินเดือน</b> ที่ยื่นได้ (<see cref="PayrollRunFilingScope.FilingStatuses"/> = อนุมัติ/จ่ายแล้ว) — หนึ่งแถวต่อ
///   พนักงานต่อรอบ (= ต่อการจ่าย · วันที่จ่ายของรอบนั้น) · รวมคนที่ภาษี 0 (ภ.ง.ด.1 แสดงผู้มีเงินได้ทุกคน) · เงินได้ =
///   <see cref="IncomeOf"/> (ฐานภาษี TaxableGross — ไม่รวมสวัสดิการยกเว้น) · ประเภท (1) 40(1) กรณีทั่วไปเสมอ
///   (ระบบเงินเดือนไม่มีแนวคิด "อนุมัติ 3%" หรือ "ออกภาษีแทน" — เงื่อนไข = 1)</item>
/// <item><b>หนังสือรับรอง 50 ทวิ แบบ ภ.ง.ด.1 ที่ออกด้วยมือ</b> (<c>SourcePayrollRunId == null</c>) ที่<b>ไม่ซ้ำกับแถวเงินเดือน</b>
///   (ดูย่อหน้าถัดไป) — เช่น ค่าตำแหน่ง/ค่านายหน้า 40(2) ที่จ่ายนอกรอบเงินเดือน · เดิม <b>หายจากไฟล์ยื่นเงียบ ๆ</b>
///   ทั้งที่หน้านำส่งนับภาษีของใบเหล่านี้ · ประเภทจากรหัสเงินได้ของบรรทัด (<see cref="CategoryOfCertLine"/>) ·
///   เงื่อนไขจากชนิดใบ (<see cref="WhtGrossUp.FilingCondition"/>)</item>
/// </list>
/// ใบ 50 ทวิ ที่ระบบออกจากรอบเงินเดือนเอง (<c>SourcePayrollRunId != null</c>) <b>ไม่</b>นับซ้ำ (แถวเงินเดือนคือตัวตั้ง) ·
/// บรรทัด<b> 40(1)</b> ของใบมือที่ผู้รับมีแถวเงินเดือนลงไฟล์ใน<b>งวดยื่นเดียวกัน</b> (HR ออกแทนใบอัตโนมัติ) ไม่นับซ้ำ + แจ้งใน Notes
/// และ <b>เงื่อนไขของใบ</b> (ออกให้ตลอดไป/ครั้งเดียว) ถูกใช้กับแถวเงินเดือนของคนนั้น · บรรทัด 40(2) ไม่ถูกตัดเสมอ ·
/// นิยามต่อเดือน ⇒ ภ.ง.ด.1ก = ผลรวม ภ.ง.ด.1 ทุกเดือน (ฝ่ายค้านรอบส่งออก 2026-10-15 P1-3)</para>
///
/// <para><b>ไม่เดา</b>: บรรทัด 50 ทวิ ที่รหัสเงินได้ไม่ใช่ 40(1)/40(2) หรือว่าง ⇒ ไม่ลงไฟล์ + <see cref="Result.Notes"/> บอกเลขที่ใบ
/// และยอดภาษี (ล้มดัง) · เลขผู้เสียภาษีว่าง ⇒ แถวเก็บ null · ไฟล์ลงเลข 0 13 หลักตามสเปก WHTSVS ช่อง 5 (ช่อง <c>*</c>) + Notes บอกจำนวนแถว ·
/// ไม่มีรหัสไปรษณีย์ (ช่อง 12 <c>***</c>) / ไม่มีชื่อ (ช่อง 8 <c>**</c>) / ข้อความยาวเกินสเปก ⇒ Notes (รอบ 2026-10-16) ·
/// ประเภท (2) อนุมัติ 3% และ (3) เงินชดเชยออกจากงาน <b>ระบบไม่มีข้อมูลแยก</b> (เงินชดเชยที่โพสต์จากหน้าเลิกจ้างไม่ได้หักภาษี
/// ผ่านเงินเดือน) — บันทึกไว้ใน EXPORT_AUDIT ให้เจ้าของตัดสิน ไม่ใส่ประเภทแทนเอง</para>
///
/// <para>ทุก query กรอง <c>CompanyId</c> (กฎ M)</para>
/// </summary>
public static class Pnd1FilingData
{
    /// <summary>เงินได้ที่ลงแบบ ภ.ง.ด.1 ของแถวเงินเดือนหนึ่งแถว — ฐานภาษี (TaxableGross · ไม่รวมสวัสดิการที่ได้รับยกเว้น) ·
    /// 0 = ข้อมูลเก่าก่อนมีช่องนี้ ⇒ ใช้ GrossIncome (นิยามของช่องใน <c>PayrollDetail.TaxableGross</c>) ·
    /// ตัวตัดสินเดียว — เดิมเขียน <c>TaxableGross &gt; 0 ? … : …</c> สี่ที่ และหน้ารายงานใช้ GrossIncome ตรง ๆ (จอ ≠ ไฟล์)</summary>
    public static decimal IncomeOf(decimal taxableGross, decimal grossIncome)
        => taxableGross > 0 ? taxableGross : grossIncome;

    /// <summary>ผลโหลด — <paramref name="Rows"/> เรียงตามประเภท แล้วตามรหัสพนักงาน/เลขที่ใบ</summary>
    /// <param name="PayrollRowCount">จำนวนแถวที่มาจากรอบเงินเดือน</param>
    /// <param name="CertRows">แถวที่มาจาก 50 ทวิ ที่ออกด้วยมือ (อยู่ใน <paramref name="Rows"/> ด้วย) — หน้ารายงานใช้แสดงต่อท้ายรายชื่อพนักงาน</param>
    /// <param name="Notes">คำเตือนที่ต้องแสดงในสรุปผลส่งออก/หน้ารายงาน (ว่าง = ไม่มีอะไรต้องแก้)</param>
    /// <param name="HasPayrollRuns">งวดนี้มีรอบเงินเดือนที่ไม่ถูกยกเลิกไหม (ใช้ตัดสินว่าจะเรียกด่าน "ยังไม่อนุมัติรอบ")</param>
    public sealed record Result(
        IReadOnlyList<Pnd1FileFormat.Row> Rows,
        int PayrollRowCount,
        IReadOnlyList<Pnd1FileFormat.Row> CertRows,
        IReadOnlyList<string> Notes,
        bool HasPayrollRuns)
    {
        public int CertRowCount => CertRows.Count;
        public decimal TotalIncome => Rows.Sum(r => Pnd1FileFormat.Money(r.IncomeAmount));
        public decimal TotalTax => Rows.Sum(r => Pnd1FileFormat.Money(r.TaxAmount));
    }

    /// <summary>ประเภทเงินได้ของบรรทัด 50 ทวิ แบบ ภ.ง.ด.1 — 40(1) ⇒ (1) · 40(2) ⇒ (4) ผู้รับอยู่ในไทย / (5) ไม่อยู่ในไทย
    /// (ตัดสินจากประเทศของผู้ติดต่อ — <c>TH</c>/ว่าง = อยู่ในไทย) · อื่น/ไม่รู้ ⇒ 0 (ผู้เรียกต้องเตือน ห้ามเดา) ·
    /// รหัสผ่านตารางกลาง <see cref="ThaiWhtRateTable.Resolve"/> ตัวเดียว</summary>
    private static int CategoryOfCertLine(string? incomeTypeCode, string? payeeCountryCode)
    {
        var t = ThaiWhtRateTable.Resolve(incomeTypeCode);
        if (t == null) return 0;
        if (t.TaxSection.StartsWith("40(1)", StringComparison.Ordinal)) return Pnd1FileFormat.GeneralSalary;
        if (t.TaxSection.StartsWith("40(2)", StringComparison.Ordinal))
            return IsThaiResident(payeeCountryCode) ? Pnd1FileFormat.Section40_2Resident : Pnd1FileFormat.Section40_2NonResident;
        return 0;
    }

    private static bool IsThaiResident(string? countryCode)
    {
        var c = (countryCode ?? "").Trim();
        return c.Length == 0 || string.Equals(c, "TH", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ภ.ง.ด.1 รายเดือน — หนึ่งแถวต่อการจ่ายหนึ่งครั้ง</summary>
    public static async Task<Result> LoadMonthAsync(AccountingDbContext db, Guid companyId, int year, int month)
    {
        var details = await db.PayrollDetails.AsNoTracking()
            .Include(d => d.Employee)
            .Include(d => d.PayrollRun)
            .Where(d => d.CompanyId == companyId && d.PayrollRun.CompanyId == companyId
                && d.PayrollRun.Year == year && d.PayrollRun.Month == month
                && d.PayrollRun.Status != PayrollRunFilingScope.Voided)
            .ToListAsync();
        var certs = await LoadPnd1CertsAsync(db, companyId, year, month);
        var result = Build(details, certs, annual: false);
        var periodNote = PayDateOutsidePeriodNote(result.Rows, year, month);
        return periodNote == null ? result : result with { Notes = result.Notes.Append(periodNote).ToList() };
    }

    /// <summary>
    /// <b>วันที่จ่าย (ช่อง 16) อยู่นอกเดือน/ปีภาษีของไฟล์ (ช่อง 13–14)</b> — คำเตือน · null = ทุกแถวอยู่ในงวด (ฝ่ายค้าน ภ.ง.ด.1 P2-6)
    ///
    /// <para>สเปก WHTSVS ช่อง 13 "ต้องบันทึก<b>เดือนที่จ่ายภาษี</b>" และ ภ.ง.ด.1 ยื่นตามเดือนที่จ่ายเงินได้ (ป.รัษฎากร ม.50 หักเมื่อจ่าย ·
    /// ม.52 นำส่งภายใน 7 วันของเดือนถัดจากเดือนที่จ่าย) แต่ระบบจัดงวดตาม<b>เดือนของรอบเงินเดือน</b> (รอบ ก.ย. ที่จ่าย 1 ต.ค. ⇒ ไฟล์ ก.ย.
    /// ช่อง 13 = 09 · ช่อง 16 = 01102569) · ไม่ย้ายงวดเอง: การย้ายต้องเปลี่ยนการจัดงวดของหน้านำส่ง/ปฏิทินภาษี/หนี้ 21914 พร้อมกัน
    /// (ส่งเจ้าของตัดสิน — EXPORT_AUDIT) ⇒ ล้มดังให้ผู้ยื่นเห็นก่อนยื่น</para>
    /// </summary>
    internal static string? PayDateOutsidePeriodNote(IEnumerable<Pnd1FileFormat.Row> rows, int year, int month)
    {
        var outside = rows.Where(r => r.PayDate.Year != year || r.PayDate.Month != month).ToList();
        if (outside.Count == 0) return null;
        var ex = outside[0];
        return $"⚠️ {outside.Count} รายการวันที่จ่ายเงินได้ (ช่องที่ 16) อยู่นอกเดือนภาษี {month:D2}/{year + 543} ของไฟล์ (ช่องที่ 13–14) — "
            + $"เช่น {ex.SourceRef ?? ex.FirstName} จ่าย {ex.PayDate:dd/MM}/{ex.PayDate.Year + 543} · "
            + "ภ.ง.ด.1 ยื่นตามเดือนที่จ่ายเงินได้ (ม.50 · ม.52) แต่ระบบจัดงวดตามเดือนของรอบเงินเดือน — "
            + "ตรวจกับผู้ทำบัญชีว่ารายการนี้ต้องยื่นในแบบของเดือนที่จ่ายจริงหรือไม่ก่อนยื่น";
    }

    /// <summary>ภ.ง.ด.1ก รายปี — หนึ่งแถวต่อผู้มีเงินได้ต่อประเภท/เงื่อนไข (ยอดทั้งปี)</summary>
    public static async Task<Result> LoadYearAsync(AccountingDbContext db, Guid companyId, int year)
    {
        var details = await db.PayrollDetails.AsNoTracking()
            .Include(d => d.Employee)
            .Include(d => d.PayrollRun)
            .Where(d => d.CompanyId == companyId && d.PayrollRun.CompanyId == companyId
                && d.PayrollRun.Year == year
                && d.PayrollRun.Status != PayrollRunFilingScope.Voided)
            .ToListAsync();
        var certs = await LoadPnd1CertsAsync(db, companyId, year, month: null);
        return Build(details, certs, annual: true);
    }

    private static async Task<List<WithholdingTaxCert>> LoadPnd1CertsAsync(
        AccountingDbContext db, Guid companyId, int year, int? month)
    {
        var filed = WhtCertFilingScope.Filed;
        var certs = await db.WithholdingTaxCerts
            .Include(w => w.Lines)   // ไม่ Include PayeeContact — hydrate แยก (กัน INNER JOIN ตัดผู้รับที่ถูกลบ)
            .Where(w => w.CompanyId == companyId
                && w.TaxFormType == TaxType.WithholdingTax1
                && w.TaxYear == year && (month == null || w.TaxMonth == month.Value)
                && filed.Contains(w.Status))
            .ToListAsync();
        await db.HydratePayeeContactsAsync(companyId, certs);
        return certs;
    }

    /// <summary>ประกอบแถวจากข้อมูลที่โหลดแล้ว — <b>pure</b> (เทสต์ยิงตรงได้โดยไม่ต้องมีฐาน)</summary>
    public static Result Build(IReadOnlyCollection<PayrollDetail> details, IReadOnlyCollection<WithholdingTaxCert> certs, bool annual)
    {
        var notes = new List<string>();
        var hasRuns = details.Count > 0;

        // ── 1. รอบเงินเดือน ──
        var filedDetails = details
            .Where(d => PayrollRunFilingScope.FilingStatuses.Contains(d.PayrollRun.Status)
                && IncomeOf(d.TaxableGross, d.GrossIncome) > 0)
            .ToList();

        // ฝ่ายค้านรอบส่งออก 2026-10-15 P1-3: ใบมือ "ซ้ำกับเงินเดือน" = ผู้รับคนเดียวกัน (เลขผู้เสียภาษี) + งวดยื่นเดียวกัน (ปี/เดือนของรอบ ·
        // ปี/เดือนภาษีของใบ) + บรรทัด 40(1) และคนนั้นมีแถวเงินเดือน<b>ที่ลงไฟล์จริง</b>ในงวดนั้น · เดิมตัดใบมือทุกใบของใครก็ตามที่อยู่ในรอบใด ๆ
        // ของงวด/ปี ⇒ ค่านายหน้า 40(2) ของพนักงาน หรือ 40(1) ของเดือนที่ไม่มีรอบเงินเดือน หายจากแบบเงียบ ๆ · นิยามต่อเดือนเดียวกันทั้งรายเดือน
        // และรายปี ⇒ ภ.ง.ด.1ก = ผลรวมของ ภ.ง.ด.1 ทุกเดือน
        var payrollPeriods = filedDetails
            .Select(d => (TaxId: EmployeeTaxIdentity.Resolve(d.Employee.TaxId, d.Employee.CitizenId), d.PayrollRun.Year, d.PayrollRun.Month))
            .Where(x => x.TaxId != null)
            .Select(x => (x.TaxId!, x.Year, x.Month))
            .ToHashSet();
        bool DuplicatesPayroll(WithholdingTaxCert c, string tid, int category)
            => category == Pnd1FileFormat.GeneralSalary && tid.Length > 0 && payrollPeriods.Contains((tid, c.TaxYear, c.TaxMonth));

        // เงื่อนไขจากใบมือที่ซ้ำกับเงินเดือน (HR ออกแทนใบอัตโนมัติ เช่น "ออกภาษีให้ตลอดไป") ⇒ ใช้กับแถวเงินเดือนของคนนั้น
        var conditionByTaxId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in certs.Where(c => c.SourcePayrollRunId == null))
        {
            var tid = ThaiTaxId.Normalize(c.PayeeContact?.TaxId);
            var anyDup = c.Lines?.Any(l => DuplicatesPayroll(c, tid, CategoryOfCertLine(l.IncomeTypeCode, c.PayeeContact?.CountryCode))) == true;
            if (!anyDup) continue;
            var cond = WhtGrossUp.FilingCondition(c.CertificateType);
            if (cond != 1) conditionByTaxId[tid] = cond;
        }
        var payrollRows = new List<Pnd1FileFormat.Row>();
        if (!annual)
        {
            foreach (var d in filedDetails
                .OrderBy(d => d.Employee.EmployeeCode, StringComparer.Ordinal)
                .ThenBy(d => d.PayrollRun.PayDate))
                payrollRows.Add(PayrollRow(d.Employee, d.PayrollRun.PayDate,
                    IncomeOf(d.TaxableGross, d.GrossIncome), d.WithholdingTax, conditionByTaxId));
        }
        else
        {
            foreach (var g in filedDetails
                .GroupBy(d => d.EmployeeId)
                .Select(g => (Emp: g.First().Employee,
                    Income: g.Sum(d => IncomeOf(d.TaxableGross, d.GrossIncome)),
                    Tax: g.Sum(d => d.WithholdingTax),
                    LastPay: g.Max(d => d.PayrollRun.PayDate)))
                .OrderBy(x => x.Emp.EmployeeCode, StringComparer.Ordinal))
                payrollRows.Add(PayrollRow(g.Emp, g.LastPay, g.Income, g.Tax, conditionByTaxId));
        }

        // ── 2. 50 ทวิ ที่ออกด้วยมือของผู้รับนอกรอบเงินเดือน ──
        var certRows = new List<Pnd1FileFormat.Row>();
        var unknown = new List<(string CertNo, decimal Tax)>();
        var duplicates = new List<(string CertNo, decimal Tax)>();
        foreach (var c in certs
            .Where(c => c.SourcePayrollRunId == null)
            .OrderBy(c => c.CertificateNumber, StringComparer.Ordinal))
        {
            var contact = c.PayeeContact;
            var tid = ThaiTaxId.Normalize(contact?.TaxId);

            var cond = WhtGrossUp.FilingCondition(c.CertificateType);
            // รอบ 2026-10-16 (สเปก WHTSVS): นิติบุคคล = ชื่อเต็มในช่องชื่อ · นามสกุล/คำนำหน้าว่าง (ไม่ผ่า "บริษัท ก จำกัด" เป็นชื่อ/สกุล) ·
            // ตัวตัดสินชนิดผู้รับตัวเดียวกับ ภ.ง.ด.3/53 (WhtPayeeKind) · บุคคล = คำนำหน้าที่ยืนยันไว้ก่อน แล้วค่อยเดาจากชื่อ (PndTextFileFormat)
            var juristic = contact != null && WhtPayeeKind.IsJuristic(contact.TaxId, contact.ContactType, contact.Name);
            var (first, last) = PndTextFileFormat.SplitName(contact?.Name, isJuristic: juristic, contact?.TitleTh);
            var title = juristic ? "" : PndTextFileFormat.TitleOf(contact?.Name, contact?.TitleTh);
            var address = contact == null ? null : Accounting.Services.Implementations.ThaiAddressFormatter.Format(
                contact.Address, contact.BuildingNumber, contact.BuildingName, contact.Moo, contact.StreetName,
                contact.SubDistrict, contact.District, contact.Province, contact.PostalCode);
            var lines = c.Lines?.OrderBy(l => l.LineOrder).ToList() ?? new List<WithholdingTaxCertLine>();
            if (lines.Count == 0)
            {
                unknown.Add((c.CertificateNumber, c.TotalTaxAmount));
                continue;
            }
            foreach (var l in lines)
            {
                var cat = CategoryOfCertLine(l.IncomeTypeCode, contact?.CountryCode);
                if (cat == 0) { unknown.Add((c.CertificateNumber, l.TaxAmount)); continue; }
                // 40(1) ของเดือนที่คนนี้มีแถวเงินเดือนลงไฟล์แล้ว = แถวเงินเดือนคือตัวตั้ง (ไม่ลงซ้ำ — แจ้งใน Notes) · 40(2) ไม่ถูกตัดเสมอ
                if (DuplicatesPayroll(c, tid, cat)) { duplicates.Add((c.CertificateNumber, l.TaxAmount)); continue; }
                certRows.Add(new Pnd1FileFormat.Row(
                    Category: cat, PayeeTaxId: tid, Title: title, FirstName: first, LastName: last,
                    PayDate: l.PaymentDate, IncomeAmount: l.IncomeAmount, TaxAmount: l.TaxAmount,
                    Condition: cond, Address: address, SourceRef: c.CertificateNumber,
                    PostalCode: contact?.PostalCode, TaxRate: l.TaxRate));
            }
        }
        if (annual)
        {
            // รายปี: รวมต่อผู้รับ × ประเภท × เงื่อนไข (ใบแนบ ภ.ง.ด.1ก เป็นยอดทั้งปีต่อราย)
            certRows = certRows
                .GroupBy(r => (Key: r.PayeeTaxId is { Length: > 0 } t ? t : $"{r.FirstName} {r.LastName}", r.Category, r.Condition))
                .Select(g => g.First() with
                {
                    IncomeAmount = g.Sum(r => r.IncomeAmount),
                    TaxAmount = g.Sum(r => r.TaxAmount),
                    PayDate = g.Max(r => r.PayDate),
                    // อัตราช่อง 17: ทุกใบอัตราเดียวกัน = อัตรานั้น · ต่างกัน = หาค่าเดียวไม่ได้ ⇒ null (ไฟล์ลง 0 ตามข้อ 9 ของสเปก)
                    TaxRate = g.Select(r => r.TaxRate).Distinct().Count() == 1 ? g.First().TaxRate : null,
                    SourceRef = string.Join(" ", g.Select(r => r.SourceRef).Distinct()),
                })
                .ToList();
        }

        var rows = payrollRows.Concat(certRows)
            .OrderBy(r => r.Category)
            .ToList();   // OrderBy เสถียร — ลำดับภายในประเภทคงตามที่เรียงไว้ (เงินเดือนก่อน แล้ว 50 ทวิ)

        // ── คำเตือน (ล้มดัง ไม่หายเงียบ) ──
        if (duplicates.Count > 0)
        {
            var nos = duplicates.Select(u => u.CertNo).Distinct().ToList();
            notes.Add($"ℹ️ 50 ทวิ แบบ ภ.ง.ด.1 ที่ออกด้วยมือ {nos.Count} ใบ (ภาษี {duplicates.Sum(u => u.Tax):N2} บาท) ไม่ลงซ้ำ "
                + "เพราะเป็นเงินได้ 40(1) ของพนักงานที่มีแถวเงินเดือนในเดือนเดียวกันแล้ว (แถวเงินเดือนคือตัวตั้ง) — "
                + string.Join(", ", nos.Take(3)) + (nos.Count > 3 ? ", …" : "")
                + " · ถ้าเป็นเงินได้คนละก้อนกับเงินเดือน ให้จ่ายผ่านรอบเงินเดือน หรือแก้ประเภทเงินได้ในใบให้ตรง");
        }
        if (unknown.Count > 0)
            notes.Add($"⚠️ 50 ทวิ แบบ ภ.ง.ด.1 {unknown.Select(u => u.CertNo).Distinct().Count()} ใบ "
                + $"(ภาษี {unknown.Sum(u => u.Tax):N2} บาท) ไม่ได้ลงไฟล์ เพราะประเภทเงินได้ไม่ใช่ 40(1)/40(2) หรือว่าง — "
                + string.Join(", ", unknown.Select(u => u.CertNo).Distinct().Take(3))
                + (unknown.Select(u => u.CertNo).Distinct().Count() > 3 ? ", …" : "")
                + " · แก้ประเภทเงินได้ในหนังสือรับรอง (หรือย้ายไปแบบ ภ.ง.ด.3) แล้วสร้างไฟล์ใหม่");
        var noTaxId = rows.Count(r => ThaiTaxId.Normalize(r.PayeeTaxId).Length == 0);
        if (noTaxId > 0)
            notes.Add($"⚠️ {noTaxId} รายการไม่มีเลขประจำตัวผู้เสียภาษี (ไฟล์ลงเลข 0 13 หลักตามสเปกช่องที่ 5 — สรรพากรจะไม่รับเป็นเลขของผู้มีเงินได้) — "
                + "กรอกเลขบัตรประชาชนที่ทะเบียนพนักงาน/ผู้ติดต่อ แล้วสร้างไฟล์ใหม่");
        var badChecksum = rows.Count(r => ThaiTaxId.Normalize(r.PayeeTaxId).Length > 0 && !ThaiTaxId.IsValid(r.PayeeTaxId));
        if (badChecksum > 0)
            notes.Add($"⚠️ {badChecksum} รายการเลขประจำตัวผู้เสียภาษีไม่ผ่านการตรวจหลักสุดท้าย (13 หลัก + checksum) — ตรวจเลขบัตรประชาชน");
        // สเปก WHTSVS (2026-10-16): ช่อง 12 รหัสไปรษณีย์ *** (ต้องถูกต้อง) ทั้ง ภ.ง.ด.1 และ 1ก · ช่อง 8 ชื่อ ** (ต้องมีค่า) ·
        // ข้อความยาวเกินสเปกถูกตัด — ล้มดังที่สรุปผลส่งออก/หน้ารายงาน ไม่แต่งค่าแทน
        var noPostal = rows.Count(r => Pnd1FileFormat.AddressParts(r).PostalCode.Length != 5);
        if (noPostal > 0)
            notes.Add($"⚠️ {noPostal} รายการไม่มีรหัสไปรษณีย์ 5 หลัก (ช่องที่ 12 ของไฟล์ — สรรพากรจะไม่รับแถวนี้) — "
                + "กรอกที่อยู่พร้อมรหัสไปรษณีย์ที่ทะเบียนพนักงาน/ผู้ติดต่อ แล้วสร้างไฟล์ใหม่");
        var noName = rows.Count(r => string.IsNullOrWhiteSpace(r.FirstName));
        if (noName > 0)
            notes.Add($"⚠️ {noName} รายการไม่มีชื่อผู้มีเงินได้ (ช่องที่ 8 ต้องมีค่า) — กรอกชื่อที่ทะเบียนพนักงาน/ผู้ติดต่อ");
        var overflow = rows.Count(Pnd1FileFormat.Overflows);
        if (overflow > 0)
            notes.Add($"⚠️ {overflow} รายการมีคำนำหน้า/ชื่อ/นามสกุล/ที่อยู่ยาวเกินที่สเปกกำหนด (40/80/80/2×80 ตัวอักษร) ไฟล์ตัดส่วนเกินออก — "
                + "ตรวจในรายงานตรวจทาน หรือย่อข้อความที่ทะเบียน");
        if (annual)
        {
            var noAddress = rows.Count(r => string.IsNullOrWhiteSpace(r.Address));
            if (noAddress > 0)
                notes.Add($"⚠️ {noAddress} รายไม่มีที่อยู่ (ใบแนบ ภ.ง.ด.1ก ต้องระบุที่อยู่ผู้มีเงินได้) — กรอกที่อยู่ที่ทะเบียนพนักงาน/ผู้ติดต่อ");
        }

        return new Result(rows, payrollRows.Count, certRows, notes, hasRuns);
    }

    private static Pnd1FileFormat.Row PayrollRow(Employee e, DateTime payDate, decimal income, decimal tax,
        IReadOnlyDictionary<string, int> conditionByTaxId)
    {
        var tid = EmployeeTaxIdentity.Resolve(e.TaxId, e.CitizenId);
        var cond = tid != null && conditionByTaxId.TryGetValue(tid, out var c) ? c : 1;
        // ชื่อ (สเปกช่อง 8 ต้องมีค่า): ชื่อไทยก่อน · ไม่มีชื่อไทยเลย (พนักงานต่างชาติ) ⇒ ชื่อ/สกุลอังกฤษคู่กัน (ไม่ผสมสองภาษา)
        var thai = !string.IsNullOrWhiteSpace(e.FirstNameTh);
        return new Pnd1FileFormat.Row(
            Category: Pnd1FileFormat.GeneralSalary,
            PayeeTaxId: tid,
            Title: ThaiTitleHelper.Normalize(e.TitleTh),
            FirstName: thai ? e.FirstNameTh : e.FirstNameEn,
            LastName: thai ? e.LastNameTh : e.LastNameEn,
            PayDate: payDate,
            IncomeAmount: income,
            TaxAmount: tax,
            Condition: cond,
            // ที่อยู่ข้อความเดียวผ่านตัวประกอบกลาง (เติม ต./อ./จ. ⇒ แยกที่อยู่ 1/2 ได้ตรงจุด) · รหัสไปรษณีย์หาจากท้ายที่อยู่
            Address: string.IsNullOrWhiteSpace(e.Address) ? null : Accounting.Services.Implementations.ThaiAddressFormatter.Format(
                e.Address, null, null, null, null, null, null, null, null),
            SourceRef: e.EmployeeCode,
            TaxRate: null);   // 40(1) อัตราก้าวหน้า — หาอัตราเดียวไม่ได้ ⇒ ช่อง 17 = "0" (ข้อ 9 ของสเปก)
    }
}

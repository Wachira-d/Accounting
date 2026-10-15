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
/// <item><b>หนังสือรับรอง 50 ทวิ แบบ ภ.ง.ด.1 ที่ออกด้วยมือ</b> (<c>SourcePayrollRunId == null</c>) ของผู้รับที่<b>ไม่อยู่ในรอบ
///   เงินเดือนของงวด</b> — เช่น ค่าตำแหน่ง/ค่านายหน้า 40(2) ที่จ่ายนอกรอบเงินเดือน · เดิม <b>หายจากไฟล์ยื่นเงียบ ๆ</b>
///   ทั้งที่หน้านำส่งนับภาษีของใบเหล่านี้ · ประเภทจากรหัสเงินได้ของบรรทัด (<see cref="CategoryOfCertLine"/>) ·
///   เงื่อนไขจากชนิดใบ (<see cref="WhtGrossUp.FilingCondition"/>)</item>
/// </list>
/// ใบ 50 ทวิ ที่ระบบออกจากรอบเงินเดือนเอง (<c>SourcePayrollRunId != null</c>) <b>ไม่</b>นับซ้ำ (แถวเงินเดือนคือตัวตั้ง) ·
/// ใบมือของพนักงานที่อยู่ในรอบอยู่แล้ว (HR ออกแทนใบอัตโนมัติ) ไม่นับซ้ำเช่นกัน แต่ <b>เงื่อนไขของใบ</b> (ออกให้ตลอดไป/ครั้งเดียว)
/// ถูกใช้กับแถวเงินเดือนของคนนั้น</para>
///
/// <para><b>ไม่เดา</b>: บรรทัด 50 ทวิ ที่รหัสเงินได้ไม่ใช่ 40(1)/40(2) หรือว่าง ⇒ ไม่ลงไฟล์ + <see cref="Result.Notes"/> บอกเลขที่ใบ
/// และยอดภาษี (ล้มดัง) · เลขผู้เสียภาษีว่าง ⇒ ไฟล์ปล่อยว่าง (ไม่แต่งเลข 0) + Notes บอกจำนวนแถว ·
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
        return Build(details, certs, annual: false);
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

        // ผู้มีเงินได้ที่อยู่ในรอบเงินเดือนของงวด (ทุกสถานะที่ไม่ถูกยกเลิก) — ใบมือของคนกลุ่มนี้ไม่ลงแถวซ้ำ
        var payrollTaxIds = details
            .Select(d => EmployeeTaxIdentity.Resolve(d.Employee.TaxId, d.Employee.CitizenId))
            .Where(t => t != null).Select(t => t!).ToHashSet(StringComparer.Ordinal);

        // เงื่อนไขจากใบมือของพนักงานที่อยู่ในรอบ (HR ออกแทนใบอัตโนมัติ เช่น "ออกภาษีให้ตลอดไป")
        var conditionByTaxId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in certs.Where(c => c.SourcePayrollRunId == null))
        {
            var tid = ThaiTaxId.Normalize(c.PayeeContact?.TaxId);
            if (tid.Length == 0 || !payrollTaxIds.Contains(tid)) continue;
            var cond = WhtGrossUp.FilingCondition(c.CertificateType);
            if (cond != 1) conditionByTaxId[tid] = cond;
        }

        // ── 1. รอบเงินเดือน ──
        var filedDetails = details
            .Where(d => PayrollRunFilingScope.FilingStatuses.Contains(d.PayrollRun.Status)
                && IncomeOf(d.TaxableGross, d.GrossIncome) > 0)
            .ToList();
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
        foreach (var c in certs
            .Where(c => c.SourcePayrollRunId == null)
            .OrderBy(c => c.CertificateNumber, StringComparer.Ordinal))
        {
            var contact = c.PayeeContact;
            var tid = ThaiTaxId.Normalize(contact?.TaxId);
            if (tid.Length > 0 && payrollTaxIds.Contains(tid)) continue;   // แถวเงินเดือนคือตัวตั้ง

            var cond = WhtGrossUp.FilingCondition(c.CertificateType);
            var (first, last) = PndTextFileFormat.SplitName(contact?.Name, isJuristic: false, contact?.TitleTh);
            var title = PndTextFileFormat.TitleOf(contact?.Name, contact?.TitleTh);
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
                certRows.Add(new Pnd1FileFormat.Row(
                    Category: cat, PayeeTaxId: tid, Title: title, FirstName: first, LastName: last,
                    PayDate: l.PaymentDate, IncomeAmount: l.IncomeAmount, TaxAmount: l.TaxAmount,
                    Condition: cond, Address: contact?.Address, SourceRef: c.CertificateNumber));
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
                    SourceRef = string.Join(" ", g.Select(r => r.SourceRef).Distinct()),
                })
                .ToList();
        }

        var rows = payrollRows.Concat(certRows)
            .OrderBy(r => r.Category)
            .ToList();   // OrderBy เสถียร — ลำดับภายในประเภทคงตามที่เรียงไว้ (เงินเดือนก่อน แล้ว 50 ทวิ)

        // ── คำเตือน (ล้มดัง ไม่หายเงียบ) ──
        if (unknown.Count > 0)
            notes.Add($"⚠️ 50 ทวิ แบบ ภ.ง.ด.1 {unknown.Select(u => u.CertNo).Distinct().Count()} ใบ "
                + $"(ภาษี {unknown.Sum(u => u.Tax):N2} บาท) ไม่ได้ลงไฟล์ เพราะประเภทเงินได้ไม่ใช่ 40(1)/40(2) หรือว่าง — "
                + string.Join(", ", unknown.Select(u => u.CertNo).Distinct().Take(3))
                + (unknown.Select(u => u.CertNo).Distinct().Count() > 3 ? ", …" : "")
                + " · แก้ประเภทเงินได้ในหนังสือรับรอง (หรือย้ายไปแบบ ภ.ง.ด.3) แล้วสร้างไฟล์ใหม่");
        var noTaxId = rows.Count(r => ThaiTaxId.Normalize(r.PayeeTaxId).Length == 0);
        if (noTaxId > 0)
            notes.Add($"⚠️ {noTaxId} รายการไม่มีเลขประจำตัวผู้เสียภาษี (ช่องว่างในไฟล์ — สรรพากรจะไม่รับแถวนี้) — "
                + "กรอกเลขบัตรประชาชนที่ทะเบียนพนักงาน/ผู้ติดต่อ แล้วสร้างไฟล์ใหม่");
        var badChecksum = rows.Count(r => ThaiTaxId.Normalize(r.PayeeTaxId).Length > 0 && !ThaiTaxId.IsValid(r.PayeeTaxId));
        if (badChecksum > 0)
            notes.Add($"⚠️ {badChecksum} รายการเลขประจำตัวผู้เสียภาษีไม่ผ่านการตรวจหลักสุดท้าย (13 หลัก + checksum) — ตรวจเลขบัตรประชาชน");
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
        return new Pnd1FileFormat.Row(
            Category: Pnd1FileFormat.GeneralSalary,
            PayeeTaxId: tid,
            Title: ThaiTitleHelper.Normalize(e.TitleTh),
            FirstName: e.FirstNameTh,
            LastName: e.LastNameTh,
            PayDate: payDate,
            IncomeAmount: income,
            TaxAmount: tax,
            Condition: cond,
            Address: e.Address,
            SourceRef: e.EmployeeCode);
    }
}

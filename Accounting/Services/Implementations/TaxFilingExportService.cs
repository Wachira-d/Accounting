using System.Text;
using Accounting.Data;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class TaxFilingExportService : ITaxFilingExportService
{
    private readonly AccountingDbContext _db;

    public TaxFilingExportService(AccountingDbContext db)
    {
        _db = db;
    }

    // ภาษาไทยใน RD/SSO e-Filing portal ใช้ TIS-620 หรือ UTF-8 ไม่มี BOM —
    // BOM บางครั้ง trip parser ของพวกเขาให้เห็น phantom char ในคอลัมน์แรก.
    private static byte[] AsBytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>คำนำหน้าตามรหัสกรมสรรพากร: 1=นาย, 2=นาง, 3=น.ส., 4=บริษัท,
    /// 5=ห้างหุ้นส่วน, 6=คณะบุคคล, 7=มูลนิธิ/สมาคม, 9=อื่นๆ.</summary>
    private static string TitleCode(string? title) => (title ?? "").Trim() switch
    {
        "นาย" or "Mr." or "Mr" => "1",
        "นาง" or "Mrs." or "Mrs" => "2",
        "นางสาว" or "น.ส." or "Miss" or "Ms." or "Ms" => "3",
        "บริษัท" or "บจก." or "บมจ." or "Co., Ltd." or "Co.,Ltd." => "4",
        "หจก." or "ห้างหุ้นส่วนจำกัด" => "5",
        "คณะบุคคล" => "6",
        "มูลนิธิ" or "สมาคม" => "7",
        _ => "9",
    };

    // =====================================================================
    // ภ.ง.ด.1 — Monthly Salary Withholding Tax (e-Filing text format)
    // Format: H + D… + T (trailer with totals); pipe-delimited
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPnd1Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var payrollRuns = await _db.PayrollRuns
            .Include(p => p.Details).ThenInclude(d => d.Employee)
            .Where(p => p.CompanyId == companyId && p.Year == year && p.Month == month
                && p.Status != "Draft" && p.Status != "Voided")
            .ToListAsync();

        var sb = new StringBuilder();
        var thaiYear = year + 543;
        var period = $"{thaiYear:D4}{month:D2}";

        var allDetails = payrollRuns.SelectMany(p => p.Details)
            .Where(d => d.WithholdingTax > 0).ToList();
        var totalIncome = allDetails.Sum(d => d.GrossIncome);
        var totalTax = allDetails.Sum(d => d.WithholdingTax);

        // Header: H|TaxId|BranchCode|FormCode|Period(YYYYMM)|TotalRecords|TotalIncome|TotalTax
        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.ง.ด.1|{period}|{allDetails.Count}|{totalIncome:F2}|{totalTax:F2}");

        int seq = 1;
        foreach (var detail in allDetails.OrderBy(d => d.Employee.EmployeeCode))
        {
            var emp = detail.Employee;
            var payDate = payrollRuns.First(p => p.Details.Contains(detail)).PayDate;
            var payDateThai = $"{payDate.Day:D2}/{payDate.Month:D2}/{payDate.Year + 543}";
            // D|Seq|TitleCode|FirstName|LastName|CitizenId|PaymentDate|IncomeType(1=§40(1))|Income|Tax|Condition(1=หักภาษี ณ ที่จ่าย)
            sb.AppendLine($"D|{seq++}|{TitleCode(emp.TitleTh)}|{emp.FirstNameTh}|{emp.LastNameTh}|{emp.CitizenId ?? emp.TaxId}|{payDateThai}|1|{detail.GrossIncome:F2}|{detail.WithholdingTax:F2}|1");
        }

        // Trailer: T|TotalRecords|TotalIncome|TotalTax — required by RD parser
        sb.AppendLine($"T|{allDetails.Count}|{totalIncome:F2}|{totalTax:F2}");

        return new TaxFilingExportResult(
            "PND1", "ภ.ง.ด.1", $"PND1_{year}{month:D2}.txt", "text/plain", AsBytes(sb.ToString()),
            allDetails.Count, totalIncome, totalTax,
            $"ภ.ง.ด.1 เดือน {month}/{year} จำนวน {allDetails.Count} คน ภาษีรวม {totalTax:N2} บาท");
    }

    // =====================================================================
    // ภ.ง.ด.3 — Service WHT for individuals
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPnd3Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var period = $"{thaiYear:D4}{month:D2}";

        var certs = await _db.WithholdingTaxCerts
            .Include(w => w.Lines)
            .Include(w => w.PayeeContact)
            .Where(w => w.CompanyId == companyId
                && w.TaxFormType == TaxType.WithholdingTax3
                && w.TaxYear == year && w.TaxMonth == month
                && w.Status != WithholdingTaxCertStatus.Voided)
            .ToListAsync();

        var sb = new StringBuilder();
        var totalIncome = certs.Sum(c => c.TotalIncomeAmount);
        var totalTax = certs.Sum(c => c.TotalTaxAmount);
        var totalDetailRows = certs.Sum(c => c.Lines.Count);

        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.ง.ด.3|{period}|{totalDetailRows}|{totalIncome:F2}|{totalTax:F2}");

        int seq = 1;
        foreach (var cert in certs.OrderBy(c => c.CertificateNumber))
        {
            var contact = cert.PayeeContact;
            // §40(3) บุคคลธรรมดา → คำนำหน้าจาก contact (default "9" อื่นๆ
            // เพราะ supplier-side ส่วนใหญ่ไม่ได้บันทึก title แยก)
            var titleCode = TitleCode(null);
            foreach (var line in cert.Lines.OrderBy(l => l.LineOrder))
            {
                var payDateThai = $"{line.PaymentDate.Day:D2}/{line.PaymentDate.Month:D2}/{line.PaymentDate.Year + 543}";
                sb.AppendLine($"D|{seq++}|{titleCode}|{contact.Name}||{contact.TaxId}|{payDateThai}|{MapIncomeTypeCode(line.IncomeTypeCode)}|{line.IncomeAmount:F2}|{line.TaxRate:F2}|{line.TaxAmount:F2}|{(int)cert.CertificateType}");
            }
        }
        sb.AppendLine($"T|{totalDetailRows}|{totalIncome:F2}|{totalTax:F2}");

        return new TaxFilingExportResult(
            "PND3", "ภ.ง.ด.3", $"PND3_{year}{month:D2}.txt", "text/plain", AsBytes(sb.ToString()),
            certs.Count, totalIncome, totalTax,
            $"ภ.ง.ด.3 เดือน {month}/{year} จำนวน {certs.Count} ราย ภาษีรวม {totalTax:N2} บาท");
    }

    // =====================================================================
    // ภ.ง.ด.53 — Service WHT for companies
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPnd53Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var period = $"{thaiYear:D4}{month:D2}";

        var certs = await _db.WithholdingTaxCerts
            .Include(w => w.Lines)
            .Include(w => w.PayeeContact)
            .Where(w => w.CompanyId == companyId
                && w.TaxFormType == TaxType.WithholdingTax53
                && w.TaxYear == year && w.TaxMonth == month
                && w.Status != WithholdingTaxCertStatus.Voided)
            .ToListAsync();

        var sb = new StringBuilder();
        var totalIncome = certs.Sum(c => c.TotalIncomeAmount);
        var totalTax = certs.Sum(c => c.TotalTaxAmount);
        var totalDetailRows = certs.Sum(c => c.Lines.Count);

        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.ง.ด.53|{period}|{totalDetailRows}|{totalIncome:F2}|{totalTax:F2}");

        int seq = 1;
        foreach (var cert in certs.OrderBy(c => c.CertificateNumber))
        {
            var contact = cert.PayeeContact;
            foreach (var line in cert.Lines.OrderBy(l => l.LineOrder))
            {
                var payDateThai = $"{line.PaymentDate.Day:D2}/{line.PaymentDate.Month:D2}/{line.PaymentDate.Year + 543}";
                // นิติบุคคล: title code 4 (บริษัท)
                sb.AppendLine($"D|{seq++}|4|{contact.Name}||{contact.TaxId}|{contact.BranchCode ?? "00000"}|{payDateThai}|{MapIncomeTypeCode(line.IncomeTypeCode)}|{line.IncomeAmount:F2}|{line.TaxRate:F2}|{line.TaxAmount:F2}|{(int)cert.CertificateType}");
            }
        }
        sb.AppendLine($"T|{totalDetailRows}|{totalIncome:F2}|{totalTax:F2}");

        return new TaxFilingExportResult(
            "PND53", "ภ.ง.ด.53", $"PND53_{year}{month:D2}.txt", "text/plain", AsBytes(sb.ToString()),
            certs.Count, totalIncome, totalTax,
            $"ภ.ง.ด.53 เดือน {month}/{year} จำนวน {certs.Count} ราย ภาษีรวม {totalTax:N2} บาท");
    }

    // =====================================================================
    // ภ.ง.ด.1ก — Annual Salary Summary (พร้อมวันเริ่ม/สิ้นสุดงาน)
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPnd1kAsync(Guid companyId, int year)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var yearStart = new DateTime(year, 1, 1);
        var yearEnd = new DateTime(year, 12, 31);

        var payrollDetails = await _db.PayrollDetails
            .Include(d => d.Employee)
            .Include(d => d.PayrollRun)
            .Where(d => d.CompanyId == companyId && d.PayrollRun.Year == year
                && d.PayrollRun.Status != "Draft" && d.PayrollRun.Status != "Voided")
            .ToListAsync();

        var empGroups = payrollDetails
            .GroupBy(d => d.EmployeeId)
            .Select(g => new
            {
                Employee = g.First().Employee,
                TotalIncome = g.Sum(d => d.GrossIncome),
                TotalTax = g.Sum(d => d.WithholdingTax),
                TotalSSO = g.Sum(d => d.SocialSecurityEmployee),
                TotalPVD = g.Sum(d => d.ProvidentFundEmployee),
                MonthCount = g.Select(d => d.PayrollRun.Month).Distinct().Count(),
            })
            .Where(e => e.TotalIncome > 0)
            .OrderBy(e => e.Employee.EmployeeCode)
            .ToList();

        var sb = new StringBuilder();
        var totalIncome = empGroups.Sum(e => e.TotalIncome);
        var totalTax = empGroups.Sum(e => e.TotalTax);

        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.ง.ด.1ก|{thaiYear:D4}|{empGroups.Count}|{totalIncome:F2}|{totalTax:F2}");

        int seq = 1;
        foreach (var emp in empGroups)
        {
            var e = emp.Employee;
            // คอลัมน์ครบตาม RD template 2566: TitleCode, FirstName, LastName,
            // CitizenId, StartDate, EndDate, MonthsEmployed, Income, PVD, SSO,
            // OtherExempt(ตามมาตรา 42), TaxableBase, Tax, Condition
            var start = e.StartDate < yearStart ? yearStart : e.StartDate;
            var end = e.EndDate.HasValue && e.EndDate.Value < yearEnd ? e.EndDate.Value : yearEnd;
            var startThai = $"{start.Day:D2}/{start.Month:D2}/{start.Year + 543}";
            var endThai = $"{end.Day:D2}/{end.Month:D2}/{end.Year + 543}";
            var taxableBase = Math.Max(0, emp.TotalIncome - emp.TotalSSO - emp.TotalPVD);
            sb.AppendLine($"D|{seq++}|{TitleCode(e.TitleTh)}|{e.FirstNameTh}|{e.LastNameTh}|{e.CitizenId ?? e.TaxId}|{startThai}|{endThai}|{emp.MonthCount}|1|{emp.TotalIncome:F2}|{emp.TotalPVD:F2}|{emp.TotalSSO:F2}|0.00|{taxableBase:F2}|{emp.TotalTax:F2}|1");
        }
        sb.AppendLine($"T|{empGroups.Count}|{totalIncome:F2}|{totalTax:F2}");

        return new TaxFilingExportResult(
            "PND1K", "ภ.ง.ด.1ก", $"PND1K_{year}.txt", "text/plain", AsBytes(sb.ToString()),
            empGroups.Count, totalIncome, totalTax,
            $"ภ.ง.ด.1ก ปี {year} จำนวน {empGroups.Count} คน รายได้รวม {totalIncome:N2} ภาษีรวม {totalTax:N2} บาท");
    }

    // =====================================================================
    // ภ.พ.30 — VAT Filing → คืน 2 CSV (Sale + Purchase) ในไฟล์ Zip
    // RD ภ.พ.30 upload format = strict CSV ไม่ใช่ narrative report.
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPp30Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var startDate = new DateTime(year, month, 1);
        var endDate = startDate.AddMonths(1).AddDays(-1);
        var thaiYear = year + 543;

        var salesDocs = await _db.Documents
            .Include(d => d.Lines).Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= startDate && d.DocumentDate <= endDate
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                && d.VatAmount > 0)
            .OrderBy(d => d.DocumentDate).ToListAsync();

        var purchaseDocs = await _db.Documents
            .Include(d => d.Lines).Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= startDate && d.DocumentDate <= endDate
                && (d.DocumentType == DocumentType.PurchaseInvoice
                    || d.DocumentType == DocumentType.Expense
                    || d.DocumentType == DocumentType.CertificateInLieu)
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                && d.VatAmount > 0)
            .OrderBy(d => d.DocumentDate).ToListAsync();

        decimal totalOutputBase = 0, totalOutputVat = 0, totalInputBase = 0, totalInputVat = 0;

        // CSV escape: ห่อ "..." ถ้ามี , หรือ " หรือขึ้นบรรทัด — RD parser ปฏิบัติตาม RFC 4180.
        static string Csv(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        var sale = new StringBuilder();
        sale.AppendLine("ลำดับ,วันที่,เลขที่ใบกำกับ,ชื่อผู้ซื้อ,เลขประจำตัวผู้เสียภาษี,สาขา,มูลค่าสินค้า/บริการ,จำนวนภาษี");
        int s1 = 1;
        foreach (var doc in salesDocs)
        {
            var b = doc.SubTotal - doc.DiscountAmount;
            var d = $"{doc.DocumentDate.Day:D2}/{doc.DocumentDate.Month:D2}/{thaiYear}";
            sale.AppendLine($"{s1++},{d},{Csv(doc.DocumentNumber)},{Csv(doc.Contact?.Name ?? "")},{doc.Contact?.TaxId ?? ""},{doc.Contact?.BranchCode ?? "00000"},{b:F2},{doc.VatAmount:F2}");
            totalOutputBase += b; totalOutputVat += doc.VatAmount;
        }

        var purchase = new StringBuilder();
        purchase.AppendLine("ลำดับ,วันที่,เลขที่ใบกำกับ,ชื่อผู้ขาย,เลขประจำตัวผู้เสียภาษี,สาขา,มูลค่าสินค้า/บริการ,จำนวนภาษี");
        int p1 = 1;
        foreach (var doc in purchaseDocs)
        {
            var b = doc.SubTotal - doc.DiscountAmount;
            var d = $"{doc.DocumentDate.Day:D2}/{doc.DocumentDate.Month:D2}/{thaiYear}";
            purchase.AppendLine($"{p1++},{d},{Csv(doc.DocumentNumber)},{Csv(doc.Contact?.Name ?? "")},{doc.Contact?.TaxId ?? ""},{doc.Contact?.BranchCode ?? "00000"},{b:F2},{doc.VatAmount:F2}");
            totalInputBase += b; totalInputVat += doc.VatAmount;
        }

        var summary = new StringBuilder();
        summary.AppendLine("รายการ,จำนวน");
        summary.AppendLine($"ภาษีขาย (Output VAT),{totalOutputVat:F2}");
        summary.AppendLine($"ภาษีซื้อ (Input VAT),{totalInputVat:F2}");
        summary.AppendLine($"ภาษีที่ต้องชำระ (Net VAT),{(totalOutputVat - totalInputVat):F2}");

        // Bundle 3 CSV in a zip — RD's tooling can pull each separately.
        using var zipStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteZipEntryAsync(archive, "Sale.csv", sale.ToString());
            await WriteZipEntryAsync(archive, "Purchase.csv", purchase.ToString());
            await WriteZipEntryAsync(archive, "Summary.csv", summary.ToString());
        }

        var netVat = totalOutputVat - totalInputVat;
        return new TaxFilingExportResult(
            "PP30", "ภ.พ.30", $"PP30_{year}{month:D2}.zip", "application/zip", zipStream.ToArray(),
            salesDocs.Count + purchaseDocs.Count, totalOutputBase + totalInputBase, netVat,
            $"ภ.พ.30 เดือน {month}/{year} ภาษีขาย {totalOutputVat:N2} ภาษีซื้อ {totalInputVat:N2} สุทธิ {netVat:N2} บาท (Sale.csv + Purchase.csv + Summary.csv)");
    }

    private static async Task WriteZipEntryAsync(System.IO.Compression.ZipArchive archive, string name, string content)
    {
        var e = archive.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
        await using var stream = e.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
    }

    // =====================================================================
    // สปส.1-10 — SSO Monthly Contribution Report
    // RD spec: H|TaxId|BranchCode|CompanyName|BranchSeq|RatePercent|Period|TotalRecords
    // D|Seq|CitizenId|SSNumber|TitleCode|FirstName|LastName|WageBase|EmpContrib|ErContrib
    // T|TotalRecords|TotalWages|TotalEmpContrib|TotalErContrib|TotalContrib|RatePercent
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportSso110Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var period = $"{thaiYear:D4}{month:D2}";

        var payrollRuns = await _db.PayrollRuns
            .Include(p => p.Details).ThenInclude(d => d.Employee)
            .Where(p => p.CompanyId == companyId && p.Year == year && p.Month == month
                && p.Status != "Draft" && p.Status != "Voided")
            .ToListAsync();

        var allDetails = payrollRuns
            .SelectMany(p => p.Details)
            .Where(d => d.Employee.IsSubjectToSocialSecurity && d.SocialSecurityEmployee > 0)
            .OrderBy(d => d.Employee.EmployeeCode)
            .ToList();

        // SSO wage ceiling/rate per year (15,000 → 17,500 ปี 2026 → ...)
        var ssoCfg = await _db.SsoYearConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Year == year && !c.IsDeleted);
        var wageCeiling = ssoCfg?.WageCeiling
            ?? Accounting.Helpers.SsoRateSchedule.GetDefault(year).WageCeiling;
        var ratePercent = ssoCfg?.RatePercent
            ?? (Accounting.Helpers.SsoRateSchedule.GetDefault(year).Rate * 100m);

        var sb = new StringBuilder();
        var totalWages = allDetails.Sum(d => Math.Min(d.GrossIncome, wageCeiling));
        var totalEmpContrib = allDetails.Sum(d => d.SocialSecurityEmployee);
        var totalErContrib = allDetails.Sum(d => d.SocialSecurityEmployer);
        var branchSeq = company.BranchCode ?? "00000";

        // Header (SSO portal spec): TaxId | BranchCode | CompanyName | BranchSeq | RatePercent | Period(YYYYMM) | TotalRecords
        sb.AppendLine($"H|{company.TaxId}|{branchSeq}|{company.Name}|{branchSeq}|{ratePercent:F2}|{period}|{allDetails.Count}");

        int seq = 1;
        foreach (var detail in allDetails)
        {
            var emp = detail.Employee;
            var wageBase = Math.Min(detail.GrossIncome, wageCeiling);
            sb.AppendLine($"D|{seq++}|{emp.CitizenId}|{emp.SocialSecurityNumber}|{TitleCode(emp.TitleTh)}|{emp.FirstNameTh}|{emp.LastNameTh}|{wageBase:F2}|{detail.SocialSecurityEmployee:F2}|{detail.SocialSecurityEmployer:F2}");
        }

        // Trailer with rate echo + total contribution (เดิมขาด rate echo)
        sb.AppendLine($"T|{allDetails.Count}|{totalWages:F2}|{totalEmpContrib:F2}|{totalErContrib:F2}|{totalEmpContrib + totalErContrib:F2}|{ratePercent:F2}");

        return new TaxFilingExportResult(
            "SSO110", "สปส.1-10", $"SSO110_{year}{month:D2}.txt", "text/plain", AsBytes(sb.ToString()),
            allDetails.Count, totalWages, totalEmpContrib + totalErContrib,
            $"สปส.1-10 เดือน {month}/{year} จำนวน {allDetails.Count} คน สมทบรวม {totalEmpContrib + totalErContrib:N2} บาท (ลูกจ้าง {totalEmpContrib:N2} + นายจ้าง {totalErContrib:N2}) อัตรา {ratePercent:F2}%");
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private async Task<Company> GetCompanyAsync(Guid companyId)
    {
        return await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");
    }

    /// <summary>Map internal income type code to กรมสรรพากร standard code.
    /// §40(7) ค่ารับเหมา / §40(8) ค่าบริการ → "6" (ค่าจ้างทำของ/รับเหมา
    /// ภายใต้มาตรา 3 เตรส). §40(5) ค่าเช่าทรัพย์สิน → "5". เดิมรวมทั้ง 3
    /// เป็น "5" ทำให้ RD audit mismatch.</summary>
    private static string MapIncomeTypeCode(string? code) => code switch
    {
        "1" or "40(1)" => "1",
        "2" or "40(2)" => "2",
        "3" or "40(3)" => "3",
        "4a" or "40(4)a" or "40(4)(a)" => "4A",
        "4b" or "40(4)b" or "40(4)(b)" => "4B",
        "5" or "40(5)" => "5",
        "6" or "40(6)" => "6",
        "7" or "40(7)" => "6",  // ค่ารับเหมา → §3เตรส ค่าจ้างทำของ
        "8" or "40(8)" => "6",  // ค่าบริการ → §3เตรส ค่าจ้างทำของ
        _ => "6"
    };

    // =====================================================================
    // ภ.ง.ด.91 — Annual personal income tax summary per employee
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPnd91Async(Guid companyId, int year)
    {
        var company = await GetCompanyAsync(companyId);
        var runs = await _db.PayrollRuns
            .Include(p => p.Details).ThenInclude(d => d.Employee)
            .Where(p => p.CompanyId == companyId && p.Year == year
                        && p.Status != "Draft" && p.Status != "Voided")
            .ToListAsync();
        if (runs.Count == 0)
            throw new InvalidOperationException($"ไม่มี PayrollRun สำหรับปี {year}");

        var sb = new StringBuilder();
        var thaiYear = year + 543;

        var byEmployee = runs.SelectMany(r => r.Details)
            .GroupBy(d => d.EmployeeId)
            .Select(g =>
            {
                var emp = g.First().Employee;
                return new
                {
                    Employee = emp,
                    YtdGross = g.Sum(d => d.GrossIncome),
                    YtdWht = g.Sum(d => d.WithholdingTax),
                    YtdSso = g.Sum(d => d.SocialSecurityEmployee),
                    YtdPf = g.Sum(d => d.ProvidentFundEmployee),
                    Months = g.Count(),
                };
            })
            .Where(x => x.YtdGross > 0)
            .OrderBy(x => x.Employee.EmployeeCode)
            .ToList();

        var totalIncome = byEmployee.Sum(x => x.YtdGross);
        var totalWht = byEmployee.Sum(x => x.YtdWht);

        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.ง.ด.91|{thaiYear:D4}|{byEmployee.Count}|{totalIncome:F2}|{totalWht:F2}");

        int seq = 1;
        foreach (var x in byEmployee)
        {
            var emp = x.Employee;
            sb.AppendLine($"D|{seq++}|{TitleCode(emp.TitleTh)}|{emp.FirstNameTh}|{emp.LastNameTh}|{emp.CitizenId ?? emp.TaxId}|{x.Months}|{x.YtdGross:F2}|{x.YtdSso:F2}|{x.YtdPf:F2}|{x.YtdWht:F2}");
        }
        sb.AppendLine($"T|{byEmployee.Count}|{totalIncome:F2}|{totalWht:F2}");

        return new TaxFilingExportResult(
            "PND91", "ภ.ง.ด.91", $"PND91_{year}.txt", "text/plain", AsBytes(sb.ToString()),
            byEmployee.Count, totalIncome, totalWht,
            $"ภ.ง.ด.91 ประจำปี {year} จำนวน {byEmployee.Count} คน รายได้รวม {totalIncome:N2} บาท ภาษีรวม {totalWht:N2} บาท");
    }
}

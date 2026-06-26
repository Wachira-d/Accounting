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
    private readonly ITaxService _taxService;

    public TaxFilingExportService(AccountingDbContext db, ITaxService taxService)
    {
        _db = db;
        _taxService = taxService;
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
        // ใช้ TaxableGross ถ้ามี (รายได้ที่นำมาคำนวณ WHT — Gross −
        // สวัสดิการยกเว้นภาษี). Fallback GrossIncome สำหรับข้อมูลเก่า
        // ที่ยังไม่ migrate.
        decimal IncomeForTax(PayrollDetail d) => d.TaxableGross > 0 ? d.TaxableGross : d.GrossIncome;
        var totalIncome = allDetails.Sum(IncomeForTax);
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
            var income = IncomeForTax(detail);
            sb.AppendLine($"D|{seq++}|{TitleCode(emp.TitleTh)}|{emp.FirstNameTh}|{emp.LastNameTh}|{emp.CitizenId ?? emp.TaxId}|{payDateThai}|1|{income:F2}|{detail.WithholdingTax:F2}|1");
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

        // ใช้ TaxableGross ถ้ามี (รายได้ที่ใช้คำนวณ WHT จริง — Gross
        // หัก สวัสดิการยกเว้นภาษี). Fallback GrossIncome สำหรับข้อมูลเก่า.
        var empGroups = payrollDetails
            .GroupBy(d => d.EmployeeId)
            .Select(g => new
            {
                Employee = g.First().Employee,
                TotalIncome = g.Sum(d => d.TaxableGross > 0 ? d.TaxableGross : d.GrossIncome),
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
        var thaiYear = year + 543;

        // ⬇️ Single source of truth: ใช้ตรรกะการคำนวณ ภ.พ.30 ตัวเดียวกับหน้าจอ +
        // Excel export (TaxService.ComputeVatReportAsync) แทนการ re-implement
        // การคัดเอกสารเองในนี้ — เดิม 2 ทางต่างกัน (CSV นับ Invoice/ไม่หัก §82/5/
        // ไม่รวม CN-DN/filter VatAmount>0) ทำให้ "ไฟล์ที่ยื่น ≠ ที่ผู้ใช้เห็น".
        // ตอนนี้ทั้งคู่ดึงจาก TaxReportLine ชุดเดียวกัน → ตรงกัน 100%.
        var report = await _taxService.ComputeVatReportAsync(companyId, year, month);
        var lines = report.Lines.OrderBy(l => l.LineOrder).ToList();

        // เติมเลขใบกำกับผู้ขาย + สาขา จาก Document (TaxReportLine เก็บแต่ DocumentId).
        var docIds = lines.Where(l => l.DocumentId.HasValue).Select(l => l.DocumentId!.Value).Distinct().ToList();
        var docInfo = docIds.Count == 0
            ? new Dictionary<Guid, (string? SupplierInvoiceNo, string? BranchCode)>()
            : await (from d in _db.Documents.AsNoTracking()
                     where d.CompanyId == companyId && docIds.Contains(d.Id)
                     join c in _db.Contacts.AsNoTracking() on d.ContactId equals c.Id into cj
                     from c in cj.DefaultIfEmpty()
                     select new { d.Id, d.SupplierInvoiceNumber, Snapshot = d.SupplierBranchCode, ContactBranch = c != null ? c.BranchCode : null })
                .ToDictionaryAsync(x => x.Id, x => (
                    SupplierInvoiceNo: (string?)x.SupplierInvoiceNumber,
                    BranchCode: (string?)(x.Snapshot ?? x.ContactBranch)));

        // CSV escape: ห่อ "..." ถ้ามี , หรือ " หรือขึ้นบรรทัด — RD parser ปฏิบัติตาม RFC 4180.
        static string Csv(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        string Date(DateTime t) => $"{t.Day:D2}/{t.Month:D2}/{thaiYear}";

        // แยกฝั่งด้วยตัวจัด side ตัวเดียวกับ Excel (TaxService.LineSide) — กัน
        // CN/DN ฝั่งซื้อหลุดไปฝั่งขาย. ตัด §82/5 (IsExcluded) ออกจากไฟล์ยื่น.
        var saleLines = lines.Where(l => TaxService.LineSide(l) == "output").ToList();
        var purchaseLines = lines.Where(l => TaxService.LineSide(l) == "input" && !l.IsExcluded).ToList();

        var sale = new StringBuilder();
        sale.AppendLine("ลำดับ,วันที่,เลขที่ใบกำกับ,ชื่อผู้ซื้อ,เลขประจำตัวผู้เสียภาษี,สาขา,มูลค่าสินค้า/บริการ,จำนวนภาษี");
        int s1 = 1;
        foreach (var l in saleLines)
        {
            var branch = (l.DocumentId.HasValue && docInfo.TryGetValue(l.DocumentId.Value, out var di) ? di.BranchCode : null) ?? "00000";
            // เลขที่ใบกำกับ = Description (มีเลขเอกสาร + ป้าย CN/DN) เพื่อให้ตรงกับจอ
            sale.AppendLine($"{s1++},{Date(l.TransactionDate)},{Csv(l.Description ?? "")},{Csv(l.TaxPayerName ?? "")},{l.TaxPayerId ?? ""},{branch},{l.IncomeAmount:F2},{l.TaxAmount:F2}");
        }

        var purchase = new StringBuilder();
        purchase.AppendLine("ลำดับ,วันที่,เลขที่ใบกำกับ,ชื่อผู้ขาย,เลขประจำตัวผู้เสียภาษี,สาขา,มูลค่าสินค้า/บริการ,จำนวนภาษี");
        int p1 = 1;
        foreach (var l in purchaseLines)
        {
            string? supplierNo = null, branch = null;
            if (l.DocumentId.HasValue && docInfo.TryGetValue(l.DocumentId.Value, out var di))
            { supplierNo = di.SupplierInvoiceNo; branch = di.BranchCode; }
            // เลขที่ใบกำกับ = เลขบนใบผู้ขาย (RD ต้องการเลขจริง) — fallback Description
            var invNo = !string.IsNullOrWhiteSpace(supplierNo) ? supplierNo! : (l.Description ?? "");
            purchase.AppendLine($"{p1++},{Date(l.TransactionDate)},{Csv(invNo)},{Csv(l.TaxPayerName ?? "")},{l.TaxPayerId ?? ""},{branch ?? "00000"},{l.IncomeAmount:F2},{l.TaxAmount:F2}");
        }

        // ยอดรวม = ค่าจาก report (รวม CN/DN, หัก §82/5, รวมเครดิตยกมา) → ตรงกับจอ.
        var summary = new StringBuilder();
        summary.AppendLine("รายการ,จำนวน");
        summary.AppendLine($"ภาษีขาย (Output VAT),{report.OutputVat:F2}");
        summary.AppendLine($"ภาษีซื้อ (Input VAT),{report.InputVat:F2}");
        summary.AppendLine($"ภาษีที่ต้องชำระ/ขอคืน (Net VAT),{report.NetVat:F2}");

        // §87(3) Chronological — flag เอกสารที่ tax point ย้อนกลับ
        DateTime? prevDate = null;
        int outOfOrderCount = 0;
        foreach (var l in saleLines.Concat(purchaseLines))
        {
            if (prevDate.HasValue && l.TransactionDate < prevDate.Value) outOfOrderCount++;
            prevDate = l.TransactionDate;
        }
        summary.AppendLine($"เอกสารเรียงเวลาย้อนกลับ (§87(3) chronological),{outOfOrderCount}");

        using var zipStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteZipEntryAsync(archive, "Sale.csv", sale.ToString());
            await WriteZipEntryAsync(archive, "Purchase.csv", purchase.ToString());
            await WriteZipEntryAsync(archive, "Summary.csv", summary.ToString());
        }

        var netVat = report.NetVat;
        var totalBase = saleLines.Sum(l => l.IncomeAmount) + purchaseLines.Sum(l => l.IncomeAmount);
        return new TaxFilingExportResult(
            "PP30", "ภ.พ.30", $"PP30_{year}{month:D2}.zip", "application/zip", zipStream.ToArray(),
            saleLines.Count + purchaseLines.Count, totalBase, netVat,
            $"ภ.พ.30 เดือน {month}/{year} ภาษีขาย {report.OutputVat:N2} ภาษีซื้อ {report.InputVat:N2} สุทธิ {netVat:N2} บาท (Sale.csv + Purchase.csv + Summary.csv)");
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
    // สปส.1-03 — ขึ้นทะเบียนผู้ประกันตน (พนักงานเข้าใหม่ภายในเดือนนั้น)
    // กฎหมาย: นายจ้างต้องแจ้งภายใน 30 วันนับจากวันเริ่มงาน (พ.ร.บ.ประกันสังคม §34)
    // Layout (pipe-delimited, อ้างอิงโครงสร้าง portal e-Service):
    //   H|TaxId|BranchCode|CompanyName|Period(YYYYMM)|TotalRecords
    //   D|Seq|CitizenId|TitleCode|FirstName|LastName|StartDate(yyyyMMdd)|Salary|HospitalPref
    //   T|TotalRecords
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportSps103Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var period = $"{thaiYear:D4}{month:D2}";
        var periodStart = new DateTime(year, month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);

        // พนักงานที่ "เริ่มงาน" ภายในเดือนนั้น + อยู่ในระบบประกันสังคม
        var newEmployees = await _db.Set<Employee>().AsNoTracking()
            .Where(e => e.CompanyId == companyId && !e.IsDeleted
                && e.IsSubjectToSocialSecurity
                && e.StartDate >= periodStart && e.StartDate <= periodEnd)
            .OrderBy(e => e.StartDate).ThenBy(e => e.EmployeeCode)
            .ToListAsync();

        var sb = new StringBuilder();
        var branchSeq = company.BranchCode ?? "00000";
        sb.AppendLine($"H|{company.TaxId}|{branchSeq}|{company.Name}|{period}|{newEmployees.Count}");
        int seq = 1;
        foreach (var emp in newEmployees)
        {
            sb.AppendLine($"D|{seq++}|{emp.CitizenId}|{TitleCode(emp.TitleTh)}|{emp.FirstNameTh}|{emp.LastNameTh}" +
                $"|{emp.StartDate:yyyyMMdd}|{emp.BaseSalary:F2}|{emp.SocialSecurityHospital}");
        }
        sb.AppendLine($"T|{newEmployees.Count}");

        return new TaxFilingExportResult(
            "SPS103", "สปส.1-03", $"SPS103_{year}{month:D2}.txt", "text/plain", AsBytes(sb.ToString()),
            newEmployees.Count, newEmployees.Sum(e => e.BaseSalary), 0,
            $"สปส.1-03 (ขึ้นทะเบียนผู้ประกันตน) เดือน {month}/{year} จำนวน {newEmployees.Count} คน" +
            (newEmployees.Count > 0 ? $" — แจ้งภายใน 30 วันนับจากวันเริ่มงาน (§34)" : " — ไม่มีพนักงานเข้าใหม่"));
    }

    // =====================================================================
    // สปส.6-09 — แจ้งสิ้นสุดความเป็นผู้ประกันตน (พนักงานออกภายในเดือนนั้น)
    // กฎหมาย: นายจ้างต้องแจ้งภายในวันที่ 15 ของเดือนถัดจากเดือนที่ลาออก
    // Layout:
    //   H|TaxId|BranchCode|CompanyName|Period(YYYYMM)|TotalRecords
    //   D|Seq|CitizenId|SSNumber|TitleCode|FirstName|LastName|EndDate(yyyyMMdd)|ReasonCode
    //   T|TotalRecords
    // ReasonCode: 1=ลาออก, 2=เลิกจ้าง, 3=เกษียณ, 4=เสียชีวิต, 9=อื่นๆ (default 1)
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportSps609Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var period = $"{thaiYear:D4}{month:D2}";
        var periodStart = new DateTime(year, month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);

        // พนักงานที่ "สิ้นสุดการจ้าง" (EndDate) ภายในเดือนนั้น + เคยอยู่ในระบบ สปส.
        var leavers = await _db.Set<Employee>().AsNoTracking()
            .Where(e => e.CompanyId == companyId && !e.IsDeleted
                && e.IsSubjectToSocialSecurity
                && e.EndDate != null && e.EndDate >= periodStart && e.EndDate <= periodEnd)
            .OrderBy(e => e.EndDate).ThenBy(e => e.EmployeeCode)
            .ToListAsync();

        var sb = new StringBuilder();
        var branchSeq = company.BranchCode ?? "00000";
        sb.AppendLine($"H|{company.TaxId}|{branchSeq}|{company.Name}|{period}|{leavers.Count}");
        int seq = 1;
        foreach (var emp in leavers)
        {
            sb.AppendLine($"D|{seq++}|{emp.CitizenId}|{emp.SocialSecurityNumber}|{TitleCode(emp.TitleTh)}" +
                $"|{emp.FirstNameTh}|{emp.LastNameTh}|{emp.EndDate:yyyyMMdd}|1");
        }
        sb.AppendLine($"T|{leavers.Count}");

        return new TaxFilingExportResult(
            "SPS609", "สปส.6-09", $"SPS609_{year}{month:D2}.txt", "text/plain", AsBytes(sb.ToString()),
            leavers.Count, 0, 0,
            $"สปส.6-09 (แจ้งออก) เดือน {month}/{year} จำนวน {leavers.Count} คน" +
            (leavers.Count > 0 ? " — แจ้งภายในวันที่ 15 ของเดือนถัดไป" : " — ไม่มีพนักงานออก"));
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

    /// <summary>ภ.ง.ด.2 — Monthly dividend WHT. Multi-tier detection:
    ///   (1) JournalEntry.Tags ที่มี "DIVIDEND" / "PND2" / "ปันผล"
    ///       (CSV tag-based — แม่นที่สุด, ผู้ใช้/system ตั้งได้)
    ///   (2) JournalEntry.Description มีคำว่า "เงินปันผล" (heuristic)
    ///   (3) Account.AccountCode "21915" หรือ AccountName มี "ปันผล"
    ///       (ผังบัญชี-based — รองรับ custom code)
    /// รวม union ของทั้ง 3 → ลด miss สำหรับบริษัทที่ใช้ผังบัญชี custom.</summary>
    public async Task<TaxFilingExportResult> ExportPnd2Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var period = $"{thaiYear:D4}{month:D2}";
        var monthStart = new DateTime(year, month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        // หาบรรทัด JE ที่เกี่ยวกับเงินปันผล — multi-tier detection
        var lines = await _db.JournalEntryLines.AsNoTracking()
            .Include(l => l.JournalEntry).Include(l => l.Account)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == Models.Enums.JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= monthStart && l.JournalEntry.EntryDate <= monthEnd
                && (
                    // (1) JE Tags
                    (l.JournalEntry.Tags != null && (
                        l.JournalEntry.Tags.Contains("DIVIDEND")
                        || l.JournalEntry.Tags.Contains("PND2")
                        || l.JournalEntry.Tags.Contains("ปันผล")))
                    // (2) Description heuristic
                    || (l.JournalEntry.Description != null && l.JournalEntry.Description.Contains("เงินปันผล"))
                    // (3) Account code/name
                    || (l.Account!.AccountCode == "21915"
                        || (l.Account.AccountName != null && l.Account.AccountName.Contains("ปันผล")))
                ))
            .ToListAsync();

        // Group by JE → 1 dividend payment per shareholder
        var byJe = lines.GroupBy(l => l.JournalEntryId)
            .Select(g => new {
                EntryId = g.Key,
                Date = g.First().JournalEntry.EntryDate,
                Description = g.First().JournalEntry.Description ?? "เงินปันผล",
                // WHT line = บัญชี WHT (21915 หรือ name มี "ปันผล" / "หัก ณ ที่จ่าย")
                WhtAmount = g.Where(x => x.Account != null && (
                        x.Account.AccountCode == "21915"
                        || (x.Account.AccountName != null && (x.Account.AccountName.Contains("ปันผล")
                            || x.Account.AccountName.Contains("หัก ณ ที่จ่าย")))))
                    .Sum(x => x.CreditAmount),
                // Dividend amount = expense/equity side (ไม่ใช่ WHT, ไม่ใช่ cash 111)
                DividendAmount = g.Where(x => x.Account != null
                        && x.Account.AccountCode != "21915"
                        && !x.Account.AccountCode.StartsWith("111")
                        && (x.Account.AccountName == null || !x.Account.AccountName.Contains("หัก ณ ที่จ่าย")))
                                  .Sum(x => Math.Abs(x.DebitAmount - x.CreditAmount))
            })
            .Where(x => x.WhtAmount > 0)
            .ToList();

        var sb = new System.Text.StringBuilder();
        var totalIncome = byJe.Sum(x => x.DividendAmount);
        var totalWht = byJe.Sum(x => x.WhtAmount);
        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.ง.ด.2|{period}|{byJe.Count}|{totalIncome:F2}|{totalWht:F2}");
        int seq = 1;
        foreach (var je in byJe)
        {
            var payDate = $"{je.Date.Day:D2}/{je.Date.Month:D2}/{je.Date.Year + 543}";
            // §40(4)(ข) เงินปันผล — IncomeType=4, Condition=1 (หัก ณ ที่จ่าย)
            sb.AppendLine($"D|{seq++}|—|—|—|—|{payDate}|4|{je.DividendAmount:F2}|{je.WhtAmount:F2}|1|{Esc(je.Description)}");
        }
        sb.AppendLine($"T|{byJe.Count}|{totalIncome:F2}|{totalWht:F2}");

        return new TaxFilingExportResult(
            "PND2", "ภ.ง.ด.2", $"PND2_{year}{month:D2}.txt", "text/plain", AsBytes(sb.ToString()),
            byJe.Count, totalIncome, totalWht,
            $"ภ.ง.ด.2 เดือน {month}/{year} เงินปันผล {byJe.Count} รายการ ภาษีรวม {totalWht:N2} บาท");
    }

    /// <summary>ภ.พ.36 — Foreign service VAT self-assessment per §83/6.
    /// ผู้รับบริการในไทยที่ซื้อจาก supplier ต่างประเทศ (ที่ไม่ได้จด VAT
    /// ในไทย) ต้องนำส่ง VAT 7% เอง. รวมจาก PurchaseInvoice/Expense ที่
    /// IsForeignService=true. กำหนดยื่นภายในวันที่ 7 ของเดือนถัดไป.</summary>
    public async Task<TaxFilingExportResult> ExportPp36Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var period = $"{thaiYear:D4}{month:D2}";
        var monthStart = new DateTime(year, month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var docs = await _db.Documents.AsNoTracking()
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.IsForeignService
                && d.DocumentDate >= monthStart && d.DocumentDate <= monthEnd
                && (d.DocumentType == Models.Enums.DocumentType.PurchaseInvoice
                    || d.DocumentType == Models.Enums.DocumentType.Expense)
                && d.Status != Models.Enums.DocumentStatus.Voided
                && d.Status != Models.Enums.DocumentStatus.Draft)
            .OrderBy(d => d.DocumentDate)
            .ToListAsync();

        var sb = new System.Text.StringBuilder();
        var totalServiceAmount = docs.Sum(d => d.SubTotal);
        // §83/6: self-assessed VAT = อัตรามาตรฐาน × ฐานบริการ. ใช้ Company.VatRate
        // (default 7%) แทน hardcode 0.07 เพื่อให้สอดคล้องกับการคำนวณ VAT ทั้งระบบ
        // (gross-up หากในเอกสารมี VAT แล้ว → ใช้ d.VatAmount โดยตรง)
        var vatRate = (company.VatRate > 0 ? company.VatRate : 7m) / 100m;
        var totalSelfVat = docs.Sum(d => d.VatAmount > 0 ? d.VatAmount : Math.Round(d.SubTotal * vatRate, 2));

        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.พ.36|{period}|{docs.Count}|{totalServiceAmount:F2}|{totalSelfVat:F2}");
        int seq = 1;
        foreach (var d in docs)
        {
            var docDate = $"{d.DocumentDate.Day:D2}/{d.DocumentDate.Month:D2}/{d.DocumentDate.Year + 543}";
            var serviceAmt = d.SubTotal;
            var vatAmt = d.VatAmount > 0 ? d.VatAmount : Math.Round(serviceAmt * vatRate, 2);
            var supplierName = d.Contact?.Name ?? "—";
            var country = d.Contact?.Province ?? "Foreign";
            // D|Seq|SupplierName|SupplierCountry|InvoiceDate|InvoiceNumber|ServiceAmount|VatAmount|Description
            sb.AppendLine($"D|{seq++}|{Esc(supplierName)}|{Esc(country)}|{docDate}|{Esc(d.DocumentNumber)}|{serviceAmt:F2}|{vatAmt:F2}|{Esc(d.Notes ?? d.Reference ?? "")}");
        }
        sb.AppendLine($"T|{docs.Count}|{totalServiceAmount:F2}|{totalSelfVat:F2}");

        return new TaxFilingExportResult(
            "PP36", "ภ.พ.36", $"PP36_{year}{month:D2}.txt", "text/plain", AsBytes(sb.ToString()),
            docs.Count, totalServiceAmount, totalSelfVat,
            $"ภ.พ.36 เดือน {month}/{year} ซื้อบริการต่างประเทศ {docs.Count} รายการ VAT self-assess {totalSelfVat:N2} บาท");
    }

    /// <summary>ภ.ง.ด.54 — Foreign-vendor WHT. รวม PI/Expense ที่
    /// IsForeignService=true + WithholdingTaxAmount > 0 ในเดือน → text format
    /// ตาม RD spec. Income type 6 = §40(3)(4) ค่าสิทธิ์/ดอกเบี้ย/ปันผล.</summary>
    public async Task<TaxFilingExportResult> ExportPnd54Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;
        var period = $"{thaiYear:D4}{month:D2}";
        var monthStart = new DateTime(year, month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var docs = await _db.Documents.AsNoTracking()
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.IsForeignService
                && d.WithholdingTaxAmount > 0
                && d.DocumentDate >= monthStart && d.DocumentDate <= monthEnd
                && (d.DocumentType == Models.Enums.DocumentType.PurchaseInvoice
                    || d.DocumentType == Models.Enums.DocumentType.Expense
                    || d.DocumentType == Models.Enums.DocumentType.PaymentVoucher)
                && d.Status != Models.Enums.DocumentStatus.Voided
                && d.Status != Models.Enums.DocumentStatus.Draft)
            .OrderBy(d => d.DocumentDate)
            .ToListAsync();

        var sb = new System.Text.StringBuilder();
        var totalIncome = docs.Sum(d => d.SubTotal);
        var totalWht = docs.Sum(d => d.WithholdingTaxAmount);
        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.ง.ด.54|{period}|{docs.Count}|{totalIncome:F2}|{totalWht:F2}");
        int seq = 1;
        foreach (var d in docs)
        {
            var docDate = $"{d.DocumentDate.Day:D2}/{d.DocumentDate.Month:D2}/{d.DocumentDate.Year + 543}";
            var supplierName = d.Contact?.Name ?? "—";
            var country = d.Contact?.Province ?? "Foreign";
            var taxId = d.Contact?.TaxId ?? "—";
            var rate = d.SubTotal > 0 ? Math.Round(d.WithholdingTaxAmount / d.SubTotal * 100, 2) : 0;
            sb.AppendLine($"D|{seq++}|{Esc(supplierName)}|{Esc(taxId)}|{Esc(country)}|{docDate}|6|{d.SubTotal:F2}|{rate:F2}|{d.WithholdingTaxAmount:F2}");
        }
        sb.AppendLine($"T|{docs.Count}|{totalIncome:F2}|{totalWht:F2}");

        return new TaxFilingExportResult(
            "PND54", "ภ.ง.ด.54", $"PND54_{year}{month:D2}.txt", "text/plain", AsBytes(sb.ToString()),
            docs.Count, totalIncome, totalWht,
            $"ภ.ง.ด.54 เดือน {month}/{year} จ่ายต่างประเทศ {docs.Count} รายการ WHT {totalWht:N2} บาท");
    }

    private static string Esc(string? s) => s == null ? "" : s.Replace("|", "/").Replace("\n", " ").Trim();
}

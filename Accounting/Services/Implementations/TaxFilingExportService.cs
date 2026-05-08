using System.Globalization;
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

    // =====================================================================
    // ภ.ง.ด.1 — Monthly Salary Withholding Tax (e-Filing text format)
    // Format: กรมสรรพากร e-Filing pipe-delimited text
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

        // Header: H|TaxId|BranchCode|FormCode|Year|Month|TotalRecords|TotalIncome|TotalTax
        var allDetails = payrollRuns.SelectMany(p => p.Details).Where(d => d.WithholdingTax > 0).ToList();
        var totalIncome = allDetails.Sum(d => d.GrossIncome);
        var totalTax = allDetails.Sum(d => d.WithholdingTax);

        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.ง.ด.1|{thaiYear}|{month:D2}|{allDetails.Count}|{totalIncome:F2}|{totalTax:F2}");

        int seq = 1;
        foreach (var detail in allDetails.OrderBy(d => d.Employee.EmployeeCode))
        {
            var emp = detail.Employee;
            // D|Seq|TitleTh|FirstName|LastName|CitizenId|PaymentDate|IncomeType|IncomeAmount|TaxAmount|Condition
            var payDate = payrollRuns.First(p => p.Details.Contains(detail)).PayDate;
            sb.AppendLine($"D|{seq++}|{emp.TitleTh}|{emp.FirstNameTh}|{emp.LastNameTh}|{emp.CitizenId ?? emp.TaxId}|{payDate:dd/MM}/{thaiYear}|1|{detail.GrossIncome:F2}|{detail.WithholdingTax:F2}|1");
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return new TaxFilingExportResult(
            "PND1", "ภ.ง.ด.1", $"PND1_{year}{month:D2}.txt", "text/plain", bytes,
            allDetails.Count, totalIncome, totalTax,
            $"ภ.ง.ด.1 เดือน {month}/{year} จำนวน {allDetails.Count} คน ภาษีรวม {totalTax:N2} บาท");
    }

    // =====================================================================
    // ภ.ง.ด.3 — Service WHT for individuals (e-Filing text format)
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPnd3Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var startDate = new DateTime(year, month, 1);
        var endDate = startDate.AddMonths(1).AddDays(-1);
        var thaiYear = year + 543;

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
        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.ง.ด.3|{thaiYear}|{month:D2}|{totalDetailRows}|{totalIncome:F2}|{totalTax:F2}");

        int seq = 1;
        foreach (var cert in certs.OrderBy(c => c.CertificateNumber))
        {
            var contact = cert.PayeeContact;
            foreach (var line in cert.Lines.OrderBy(l => l.LineOrder))
            {
                sb.AppendLine($"D|{seq++}||{contact.Name}|{contact.TaxId}|{line.PaymentDate:dd/MM}/{thaiYear}|{MapIncomeTypeCode(line.IncomeTypeCode)}|{line.IncomeAmount:F2}|{line.TaxRate:F2}|{line.TaxAmount:F2}|{(int)cert.CertificateType}");
            }
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return new TaxFilingExportResult(
            "PND3", "ภ.ง.ด.3", $"PND3_{year}{month:D2}.txt", "text/plain", bytes,
            certs.Count, totalIncome, totalTax,
            $"ภ.ง.ด.3 เดือน {month}/{year} จำนวน {certs.Count} ราย ภาษีรวม {totalTax:N2} บาท");
    }

    // =====================================================================
    // ภ.ง.ด.53 — Service WHT for companies (e-Filing text format)
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPnd53Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;

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

        var totalDetailRows53 = certs.Sum(c => c.Lines.Count);
        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.ง.ด.53|{thaiYear}|{month:D2}|{totalDetailRows53}|{totalIncome:F2}|{totalTax:F2}");

        int seq = 1;
        foreach (var cert in certs.OrderBy(c => c.CertificateNumber))
        {
            var contact = cert.PayeeContact;
            foreach (var line in cert.Lines.OrderBy(l => l.LineOrder))
            {
                sb.AppendLine($"D|{seq++}||{contact.Name}|{contact.TaxId}|{contact.BranchCode ?? "00000"}|{line.PaymentDate:dd/MM}/{thaiYear}|{MapIncomeTypeCode(line.IncomeTypeCode)}|{line.IncomeAmount:F2}|{line.TaxRate:F2}|{line.TaxAmount:F2}|{(int)cert.CertificateType}");
            }
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return new TaxFilingExportResult(
            "PND53", "ภ.ง.ด.53", $"PND53_{year}{month:D2}.txt", "text/plain", bytes,
            certs.Count, totalIncome, totalTax,
            $"ภ.ง.ด.53 เดือน {month}/{year} จำนวน {certs.Count} ราย ภาษีรวม {totalTax:N2} บาท");
    }

    // =====================================================================
    // ภ.ง.ด.1ก — Annual Salary Summary
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPnd1kAsync(Guid companyId, int year)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;

        var payrollDetails = await _db.PayrollDetails
            .Include(d => d.Employee)
            .Include(d => d.PayrollRun)
            .Where(d => d.CompanyId == companyId && d.PayrollRun.Year == year
                && d.PayrollRun.Status != "Draft" && d.PayrollRun.Status != "Voided")
            .ToListAsync();

        // Group by employee, sum annual totals
        var empGroups = payrollDetails
            .GroupBy(d => d.EmployeeId)
            .Select(g => new
            {
                Employee = g.First().Employee,
                TotalIncome = g.Sum(d => d.GrossIncome),
                TotalTax = g.Sum(d => d.WithholdingTax),
                TotalSSO = g.Sum(d => d.SocialSecurityEmployee),
                TotalPVD = g.Sum(d => d.ProvidentFundEmployee)
            })
            .Where(e => e.TotalIncome > 0)
            .OrderBy(e => e.Employee.EmployeeCode)
            .ToList();

        var sb = new StringBuilder();
        var totalIncome = empGroups.Sum(e => e.TotalIncome);
        var totalTax = empGroups.Sum(e => e.TotalTax);

        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|ภ.ง.ด.1ก|{thaiYear}|{empGroups.Count}|{totalIncome:F2}|{totalTax:F2}");

        int seq = 1;
        foreach (var emp in empGroups)
        {
            var e = emp.Employee;
            // D|Seq|Title|FirstName|LastName|CitizenId|IncomeType|AnnualIncome|AnnualTax|SSO|PVD|Condition
            sb.AppendLine($"D|{seq++}|{e.TitleTh}|{e.FirstNameTh}|{e.LastNameTh}|{e.CitizenId ?? e.TaxId}|1|{emp.TotalIncome:F2}|{emp.TotalTax:F2}|{emp.TotalSSO:F2}|{emp.TotalPVD:F2}|1");
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return new TaxFilingExportResult(
            "PND1K", "ภ.ง.ด.1ก", $"PND1K_{year}.txt", "text/plain", bytes,
            empGroups.Count, totalIncome, totalTax,
            $"ภ.ง.ด.1ก ปี {year} จำนวน {empGroups.Count} คน รายได้รวม {totalIncome:N2} ภาษีรวม {totalTax:N2} บาท");
    }

    // =====================================================================
    // ภ.พ.30 — VAT Filing (CSV format for summary)
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportPp30Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var startDate = new DateTime(year, month, 1);
        var endDate = startDate.AddMonths(1).AddDays(-1);
        var thaiYear = year + 543;

        // Sales documents (output VAT)
        var salesDocs = await _db.Documents
            .Include(d => d.Lines)
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= startDate && d.DocumentDate <= endDate
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                && d.VatAmount > 0)
            .OrderBy(d => d.DocumentDate)
            .ToListAsync();

        // Purchase documents (input VAT)
        var purchaseDocs = await _db.Documents
            .Include(d => d.Lines)
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= startDate && d.DocumentDate <= endDate
                && (d.DocumentType == DocumentType.PurchaseInvoice || d.DocumentType == DocumentType.Expense || d.DocumentType == DocumentType.CertificateInLieu)
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                && d.VatAmount > 0)
            .OrderBy(d => d.DocumentDate)
            .ToListAsync();

        var sb = new StringBuilder();

        // === รายงานภาษีขาย (Output VAT Report) ===
        sb.AppendLine("=== รายงานภาษีขาย (Output VAT) ===");
        sb.AppendLine("ลำดับ|วันที่|เลขที่เอกสาร|ชื่อผู้ซื้อ|เลขผู้เสียภาษี|สาขา|มูลค่าสินค้า/บริการ|จำนวนภาษี");
        int salesSeq = 1;
        decimal totalOutputBase = 0, totalOutputVat = 0;
        foreach (var doc in salesDocs)
        {
            var baseAmount = doc.SubTotal - doc.DiscountAmount;
            sb.AppendLine($"{salesSeq++}|{doc.DocumentDate:dd/MM}/{thaiYear}|{doc.DocumentNumber}|{doc.Contact.Name}|{doc.Contact.TaxId}|{doc.Contact.BranchCode ?? "00000"}|{baseAmount:F2}|{doc.VatAmount:F2}");
            totalOutputBase += baseAmount;
            totalOutputVat += doc.VatAmount;
        }
        sb.AppendLine($"รวม|||||| {totalOutputBase:F2}|{totalOutputVat:F2}");
        sb.AppendLine();

        // === รายงานภาษีซื้อ (Input VAT Report) ===
        sb.AppendLine("=== รายงานภาษีซื้อ (Input VAT) ===");
        sb.AppendLine("ลำดับ|วันที่|เลขที่เอกสาร|ชื่อผู้ขาย|เลขผู้เสียภาษี|สาขา|มูลค่าสินค้า/บริการ|จำนวนภาษี");
        int purchaseSeq = 1;
        decimal totalInputBase = 0, totalInputVat = 0;
        foreach (var doc in purchaseDocs)
        {
            var baseAmount = doc.SubTotal - doc.DiscountAmount;
            sb.AppendLine($"{purchaseSeq++}|{doc.DocumentDate:dd/MM}/{thaiYear}|{doc.DocumentNumber}|{doc.Contact.Name}|{doc.Contact.TaxId}|{doc.Contact.BranchCode ?? "00000"}|{baseAmount:F2}|{doc.VatAmount:F2}");
            totalInputBase += baseAmount;
            totalInputVat += doc.VatAmount;
        }
        sb.AppendLine($"รวม|||||| {totalInputBase:F2}|{totalInputVat:F2}");
        sb.AppendLine();

        // === Summary ===
        var netVat = totalOutputVat - totalInputVat;
        sb.AppendLine("=== สรุป ภ.พ.30 ===");
        sb.AppendLine($"ภาษีขาย (Output VAT): {totalOutputVat:F2}");
        sb.AppendLine($"ภาษีซื้อ (Input VAT): {totalInputVat:F2}");
        sb.AppendLine($"ภาษีที่ต้องชำระ (Net VAT): {netVat:F2}");

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return new TaxFilingExportResult(
            "PP30", "ภ.พ.30", $"PP30_{year}{month:D2}.txt", "text/plain", bytes,
            salesDocs.Count + purchaseDocs.Count, totalOutputBase + totalInputBase, netVat,
            $"ภ.พ.30 เดือน {month}/{year} ภาษีขาย {totalOutputVat:N2} ภาษีซื้อ {totalInputVat:N2} สุทธิ {netVat:N2} บาท");
    }

    // =====================================================================
    // สปส.1-10 — SSO Monthly Contribution Report
    // Format: สำนักงานประกันสังคม text format
    // =====================================================================
    public async Task<TaxFilingExportResult> ExportSso110Async(Guid companyId, int year, int month)
    {
        var company = await GetCompanyAsync(companyId);
        var thaiYear = year + 543;

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

        var sb = new StringBuilder();
        var totalWages = allDetails.Sum(d => Math.Min(d.GrossIncome, 15000)); // SSO max wage base
        var totalEmpContrib = allDetails.Sum(d => d.SocialSecurityEmployee);
        var totalErContrib = allDetails.Sum(d => d.SocialSecurityEmployer);

        // Header
        sb.AppendLine($"H|{company.TaxId}|{company.BranchCode ?? "00000"}|{company.Name}|สปส.1-10|{thaiYear}|{month:D2}|{allDetails.Count}");

        int seq = 1;
        foreach (var detail in allDetails)
        {
            var emp = detail.Employee;
            var wageBase = Math.Min(detail.GrossIncome, 15000m); // SSO max 15,000 baht wage base
            // D|Seq|CitizenId|SSNumber|Title|FirstName|LastName|WageBase|EmployeeContrib|EmployerContrib
            sb.AppendLine($"D|{seq++}|{emp.CitizenId}|{emp.SocialSecurityNumber}|{emp.TitleTh}|{emp.FirstNameTh}|{emp.LastNameTh}|{wageBase:F2}|{detail.SocialSecurityEmployee:F2}|{detail.SocialSecurityEmployer:F2}");
        }

        // Footer
        sb.AppendLine($"T|{allDetails.Count}|{totalWages:F2}|{totalEmpContrib:F2}|{totalErContrib:F2}|{totalEmpContrib + totalErContrib:F2}");

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return new TaxFilingExportResult(
            "SSO110", "สปส.1-10", $"SSO110_{year}{month:D2}.txt", "text/plain", bytes,
            allDetails.Count, totalWages, totalEmpContrib + totalErContrib,
            $"สปส.1-10 เดือน {month}/{year} จำนวน {allDetails.Count} คน สมทบรวม {totalEmpContrib + totalErContrib:N2} บาท (ลูกจ้าง {totalEmpContrib:N2} + นายจ้าง {totalErContrib:N2})");
    }

    // =====================================================================
    // Private Helpers
    // =====================================================================

    private async Task<Company> GetCompanyAsync(Guid companyId)
    {
        return await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");
    }

    /// <summary>Map internal income type code to กรมสรรพากร standard code</summary>
    private static string MapIncomeTypeCode(string? code) => code switch
    {
        "1" or "40(1)" => "1",
        "2" or "40(2)" => "2",
        "3" or "40(3)" => "3",
        "4a" or "40(4)a" or "40(4)(a)" => "4A",
        "4b" or "40(4)b" or "40(4)(b)" => "4B",
        "5" or "40(5)" => "5",
        "6" or "40(6)" => "6",
        "7" or "40(7)" => "5",  // ค่ารับเหมา → มาตรา 3 เตรส
        "8" or "40(8)" => "5",  // ค่าบริการ → มาตรา 3 เตรส
        _ => "5" // default to มาตรา 3 เตรส
    };
}

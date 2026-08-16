using System.Globalization;
using System.Text;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class TaxService
{
    // ============================================================
    // Task 4 — Thai RD e-Filing export (Phase E)
    // ------------------------------------------------------------
    // Generates the pipe-delimited (|) text file the RD e-Filing
    // portal accepts. One generator method per supported form;
    // each produces an EFilingExport row capturing the artifact
    // for audit + re-download.
    //
    // Forms supported:
    //   * PND.1   — withholding from salary (เงินเดือน)
    //   * PND.3   — withholding from individuals
    //   * PND.53  — withholding from juristic persons
    //   * PND.54  — withholding from foreign payments
    //   * PP.30   — VAT (input/output)
    //   * PP.36   — VAT on foreign service imports
    //
    // The pipe schema below follows the RD's published file-format
    // spec (กรมสรรพากร — รูปแบบไฟล์ TXT สำหรับยื่นด้วยสื่อบันทึก)
    // adapted from the most current available revision. Lines end
    // with CRLF as required by the portal; the first row is a
    // company-header row.
    // ============================================================

    public async Task<EFilingExport> GenerateEFilingAsync(
        Guid companyId, string formType, int year, int month, string userId)
    {
        var company = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");

        formType = formType.ToUpperInvariant();
        string body;
        decimal totalAmount;
        decimal totalTax;
        int lineCount;
        switch (formType)
        {
            case "PND.1":
            case "PND.3":
            case "PND.53":
            case "PND.54":
                (body, totalAmount, totalTax, lineCount) = await BuildPndAsync(companyId, company, formType, year, month);
                break;
            case "PP.30":
                (body, totalAmount, totalTax, lineCount) = await BuildPp30Async(companyId, company, year, month);
                break;
            case "PP.36":
                (body, totalAmount, totalTax, lineCount) = await BuildPp36Async(companyId, company, year, month);
                break;
            case "PND.50":
                (body, totalAmount, totalTax, lineCount) = await BuildCitAsync(companyId, company, year);
                break;
            default:
                throw new ArgumentException($"FormType '{formType}' ยังไม่รองรับ — ใช้ได้: PND.1, PND.3, PND.53, PND.54, PND.50, PP.30, PP.36");
        }

        var export = new EFilingExport
        {
            CompanyId = companyId,
            FormType = formType,
            PeriodYear = year,
            PeriodMonth = month,
            FileContent = body,
            LineCount = lineCount,
            TotalAmount = totalAmount,
            TotalTax = totalTax,
            GeneratedAt = DateTime.UtcNow,
            GeneratedBy = userId,
            CreatedBy = userId,
        };
        _db.EFilingExports.Add(export);

        // Stamp the source TaxReport (if any) so the UI can show "exported".
        var taxType = formType switch
        {
            "PND.1" => TaxType.WithholdingTax1,
            "PND.3" => TaxType.WithholdingTax3,
            "PND.53" => TaxType.WithholdingTax53,
            "PND.54" => TaxType.WithholdingTax54,
            "PND.50" => TaxType.CorporateIncomeTax,
            "PP.30" => TaxType.VAT,
            "PP.36" => TaxType.VatPp36,
            _ => TaxType.VAT,
        };
        // CIT (ภงด.50) is an annual return — matched by year only, not month.
        var report = formType == "PND.50"
            ? await _db.TaxReports.FirstOrDefaultAsync(r => r.CompanyId == companyId
                && r.TaxType == taxType && r.Year == year)
            : await _db.TaxReports.FirstOrDefaultAsync(r => r.CompanyId == companyId
                && r.TaxType == taxType && r.Year == year && r.Month == month);
        if (report != null)
        {
            report.EFilingExportedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return export;
    }

    private async Task<(string body, decimal amount, decimal tax, int lines)> BuildPndAsync(
        Guid companyId, Company company, string formType, int year, int month)
    {
        var taxType = formType switch
        {
            "PND.1" => TaxType.WithholdingTax1,
            "PND.3" => TaxType.WithholdingTax3,
            "PND.53" => TaxType.WithholdingTax53,
            "PND.54" => TaxType.WithholdingTax54,
            _ => throw new ArgumentException(formType),
        };
        var report = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.TaxType == taxType
                && r.Year == year && r.Month == month)
            ?? throw new InvalidOperationException(
                $"ไม่พบรายงาน {formType} ของงวด {year}/{month:D2} — กรุณา generate ก่อน");

        // เฉพาะบรรทัดรายการจริง — บรรทัด "[สรุป]" (IncomeTypeCode=SUMMARY เป็น
        // ยอดรวมซ้ำต่อผู้ขาย) และบรรทัดที่ผู้ทำบัญชีติ๊กออก (IsExcluded เช่น
        // เอกสารยกเลิก) ห้ามลงไฟล์ยื่น — เดิมยิงทุกบรรทัด ทำให้ผู้ขายที่มี
        // หลายรายการถูกนับ 2 เท่าและรายการที่ตัดออกยังถูกนำส่ง
        var detailLines = report.Lines
            .Where(l => l.IncomeTypeCode != "SUMMARY" && !l.IsExcluded)
            .OrderBy(x => x.LineOrder)
            .ToList();

        var sb = new StringBuilder();
        // Header row — RD pipe layout (Company TaxId | Branch | FormType | Year | Month).
        // Some forms have extra header fields; the spec varies per release so
        // we keep a minimal but conformant header.
        sb.Append(string.Join("|",
            company.TaxId ?? "",
            (company.BranchCode ?? "00000").PadLeft(5, '0'),
            formType,
            year.ToString(),
            month.ToString("D2"),
            detailLines.Count.ToString(),
            F(report.TotalIncome),
            F(report.TotalTaxWithheld)
        )).Append("\r\n");

        int order = 1;
        foreach (var l in detailLines)
        {
            // Detail row per Thai RD: ลำดับ|เลขผู้เสียภาษี|คำนำหน้า|ชื่อ|นามสกุล|วันที่จ่าย|ประเภทเงินได้|ยอด|อัตรา|ภาษีหัก
            sb.Append(string.Join("|",
                order.ToString(),
                (l.TaxPayerId ?? "").PadLeft(13, '0'),
                "",                          // คำนำหน้า — optional in the file
                EscapePipe(l.TaxPayerName ?? ""),
                "",                          // นามสกุล — for individuals, splits TaxPayerName. We send full name in field 4.
                l.TransactionDate.ToString("ddMMyyyy", CultureInfo.InvariantCulture),
                l.IncomeTypeCode ?? "",
                F(l.IncomeAmount),
                F(l.TaxRate),
                F(l.TaxAmount)
            )).Append("\r\n");
            order++;
        }

        return (sb.ToString(), report.TotalIncome, report.TotalTaxWithheld, report.Lines.Count);
    }

    private async Task<(string body, decimal amount, decimal tax, int lines)> BuildPp30Async(
        Guid companyId, Company company, int year, int month)
    {
        var report = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.TaxType == TaxType.VAT
                && r.Year == year && r.Month == month)
            ?? throw new InvalidOperationException(
                $"ไม่พบรายงาน ภพ.30 ของงวด {year}/{month:D2}");

        var sb = new StringBuilder();
        // PP.30 header: TaxId | Branch | Form | Year | Month | OutputVat | InputVat | NetVat
        sb.Append(string.Join("|",
            company.TaxId ?? "",
            (company.BranchCode ?? "00000").PadLeft(5, '0'),
            "PP30",
            year.ToString(),
            month.ToString("D2"),
            F(report.OutputVat),
            F(report.InputVat),
            F(report.NetVat)
        )).Append("\r\n");

        // PP.30 has no per-document detail lines in the file format (those
        // live in the supporting purchase/sales reports). The single-line
        // body is what the portal accepts for the summary submission.

        return (sb.ToString(), report.OutputVat + report.InputVat, report.NetVat, 1);
    }

    private async Task<(string body, decimal amount, decimal tax, int lines)> BuildPp36Async(
        Guid companyId, Company company, int year, int month)
    {
        var report = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.TaxType == TaxType.VatPp36
                && r.Year == year && r.Month == month)
            ?? throw new InvalidOperationException(
                $"ไม่พบรายงาน ภพ.36 ของงวด {year}/{month:D2}");

        var sb = new StringBuilder();
        // PP.36 (Foreign Service VAT — the buyer is the filer paying the VAT)
        // Header: TaxId | Branch | Form | Year | Month | LineCount | TotalForeignAmount | TotalVat
        sb.Append(string.Join("|",
            company.TaxId ?? "",
            (company.BranchCode ?? "00000").PadLeft(5, '0'),
            "PP36",
            year.ToString(),
            month.ToString("D2"),
            report.Lines.Count.ToString(),
            F(report.TotalIncome),
            F(report.OutputVat)
        )).Append("\r\n");

        int order = 1;
        foreach (var l in report.Lines.OrderBy(x => x.LineOrder))
        {
            // ลำดับ|ชื่อผู้ให้บริการต่างประเทศ|วันที่จ่าย|ประเภทเงินได้|ยอด|อัตราภาษี|ภาษี
            sb.Append(string.Join("|",
                order.ToString(),
                EscapePipe(l.TaxPayerName ?? ""),
                l.TransactionDate.ToString("ddMMyyyy", CultureInfo.InvariantCulture),
                l.IncomeTypeCode ?? "",
                F(l.IncomeAmount),
                F(l.TaxRate),
                F(l.TaxAmount)
            )).Append("\r\n");
            order++;
        }

        return (sb.ToString(), report.TotalIncome, report.OutputVat, report.Lines.Count);
    }

    private async Task<(string body, decimal amount, decimal tax, int lines)> BuildCitAsync(
        Guid companyId, Company company, int year)
    {
        // ภ.ง.ด.50 — annual corporate income tax. Generated by GenerateCitReport,
        // which stores: TotalIncome = annual revenue, NetVat (reused) = net
        // profit before tax, CitAmount = CIT payable after credits
        // (TotalTaxWithheld ยังเขียนคู่ไว้เพื่อความเข้ากันได้ย้อนหลัง)
        var report = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.CompanyId == companyId
                && r.TaxType == TaxType.CorporateIncomeTax && r.Year == year)
            ?? throw new InvalidOperationException(
                $"ไม่พบรายงาน ภงด.50 ของปี {year} — กรุณา generate ก่อน");

        var sb = new StringBuilder();
        // PND.50 header: TaxId | Branch | Form | Year | LineCount |
        //                TotalRevenue | NetProfitBeforeTax | CitPayable
        sb.Append(string.Join("|",
            company.TaxId ?? "",
            (company.BranchCode ?? "00000").PadLeft(5, '0'),
            "PND50",
            year.ToString(),
            report.Lines.Count.ToString(),
            F(report.TotalIncome),
            F(report.NetVat),
            F(report.CitAmount ?? report.TotalTaxWithheld)
        )).Append("\r\n");

        int order = 1;
        foreach (var l in report.Lines.OrderBy(x => x.LineOrder))
        {
            // ลำดับ|รายการ|จำนวนเงิน|อัตราภาษี|ภาษี
            sb.Append(string.Join("|",
                order.ToString(),
                EscapePipe(l.Description ?? ""),
                F(l.IncomeAmount),
                F(l.TaxRate),
                F(l.TaxAmount)
            )).Append("\r\n");
            order++;
        }

        return (sb.ToString(), report.TotalIncome, report.TotalTaxWithheld, report.Lines.Count);
    }

    /// <summary>RD format: decimals with 2 fraction digits, no thousands separator.</summary>
    private static string F(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Pipe is the field separator — strip any embedded pipes from text values.</summary>
    private static string EscapePipe(string s) => s.Replace('|', '/').Replace("\r", " ").Replace("\n", " ");
}

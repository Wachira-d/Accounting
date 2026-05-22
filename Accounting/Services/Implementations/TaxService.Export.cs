using System.Globalization;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using MiniExcelLibs;

namespace Accounting.Services.Implementations;

public partial class TaxService
{
    // ============================================================
    // Excel (.xlsx) export of a tax report — VAT returns are split
    // into separate ภาษีขาย / ภาษีซื้อ sheets (matching the standard
    // RD per-document report layout), WHT/CIT into a single sheet.
    // Uses MiniExcel (already referenced) for a one-DLL writer.
    // ============================================================

    public async Task<(byte[] Content, string FileName)> ExportTaxReportXlsxAsync(Guid companyId, Guid reportId)
    {
        var report = await _db.TaxReports
            .Include(r => r.Lines)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");

        var company = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == companyId);

        var lines = report.Lines.OrderBy(l => l.LineOrder).ToList();
        var isVat = report.TaxType is TaxType.VAT or TaxType.VatPp36;
        var periodLabel = $"{report.Month:D2}/{report.Year}";

        var sheets = new Dictionary<string, object>
        {
            ["สรุปรายงาน"] = new List<Dictionary<string, object?>>
            {
                new() { ["รายการ"] = "ผู้ประกอบการ", ["ค่า"] = company?.Name ?? "" },
                new() { ["รายการ"] = "เลขประจำตัวผู้เสียภาษี", ["ค่า"] = company?.TaxId ?? "" },
                new() { ["รายการ"] = "สำนักงาน/สาขา", ["ค่า"] = company?.BranchName
                    ?? (company?.BranchCode == "00000" ? "สำนักงานใหญ่" : company?.BranchCode) },
                new() { ["รายการ"] = "ประเภทรายงาน", ["ค่า"] = report.TaxType.ToString() },
                new() { ["รายการ"] = "งวดภาษี", ["ค่า"] = periodLabel },
                new() { ["รายการ"] = "สถานะ", ["ค่า"] = report.Status.ToString() },
                new() { ["รายการ"] = "ภาษีขาย (Output VAT)", ["ค่า"] = report.OutputVat },
                new() { ["รายการ"] = "ภาษีซื้อ (Input VAT)", ["ค่า"] = report.InputVat },
                new() { ["รายการ"] = "ภาษีสุทธิ", ["ค่า"] = report.NetVat },
            },
        };

        if (isVat)
        {
            sheets["ภาษีขาย"] = BuildTaxLineSheet(lines.Where(l => LineSide(l) == "output"));
            sheets["ภาษีซื้อ"] = BuildTaxLineSheet(lines.Where(l => LineSide(l) == "input"));
            var summary = lines.Where(l => LineSide(l) == "summary").ToList();
            if (summary.Count > 0)
                sheets["รายการอื่น"] = BuildTaxLineSheet(summary);
        }
        else
        {
            sheets["รายการ"] = BuildTaxLineSheet(lines);
        }

        using var ms = new MemoryStream();
        ms.SaveAs(sheets);
        var fileName = $"TaxReport_{report.TaxType}_{report.Year}{report.Month:D2}.xlsx";
        return (ms.ToArray(), fileName);
    }

    /// <summary>VAT side of a report line — matches the server-side recalc
    /// grouping (INPUT = ภาษีซื้อ, EXEMPT/carry-forward = summary, else output).</summary>
    private static string LineSide(TaxReportLine l) => l.IncomeTypeCode switch
    {
        "INPUT" or "JE_INPUT" => "input",
        "EXEMPT" or "VAT_CREDIT_CF" => "summary",
        _ => "output",
    };

    private static List<Dictionary<string, object?>> BuildTaxLineSheet(IEnumerable<TaxReportLine> lines)
    {
        var rows = new List<Dictionary<string, object?>>();
        var i = 1;
        foreach (var l in lines)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["ลำดับ"] = i++,
                ["วันที่"] = l.TransactionDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                ["รายการ / เลขที่เอกสาร"] = l.Description ?? "",
                ["ชื่อผู้เสียภาษี"] = l.TaxPayerName ?? "",
                ["เลขประจำตัวผู้เสียภาษี"] = l.TaxPayerId ?? "",
                ["มูลค่า"] = l.IncomeAmount,
                ["อัตราภาษี (%)"] = l.TaxRate,
                ["ภาษีมูลค่าเพิ่ม"] = l.TaxAmount,
                ["สถานะ"] = l.IsExcluded ? "ไม่นำมาคิด" : "ใช้",
            });
        }
        if (rows.Count == 0)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["ลำดับ"] = "", ["วันที่"] = "", ["รายการ / เลขที่เอกสาร"] = "(ไม่มีรายการ)",
                ["ชื่อผู้เสียภาษี"] = "", ["เลขประจำตัวผู้เสียภาษี"] = "",
                ["มูลค่า"] = "", ["อัตราภาษี (%)"] = "", ["ภาษีมูลค่าเพิ่ม"] = "", ["สถานะ"] = "",
            });
        }
        return rows;
    }
}

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
    // into separate รายงานภาษีขาย / รายงานภาษีซื้อ sheets matching
    // the RD ภพ.30 attachment layout (ประกาศอธิบดี ฉบับที่ 104):
    //   · ชื่อผู้ประกอบการ + เลขผู้เสียภาษี + สาขา + งวด เป็น header rows
    //   · ตารางรายเอกสาร แล้วต่อด้วยแถว "รวม"
    // ใช้ MiniExcel (zero-dep) + printHeader:false เพื่อให้ layout
    // อิสระจาก property→column mapping.
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

        // Resolve DocumentNumber + Contact.BranchCode + RelatedDocument.DocumentNumber
        // (Remark column ของ template) ในคิวรีเดียว — TaxReportLine มีแต่
        // DocumentId, ส่วน BranchCode/สาขาเก็บใน Contact, ส่วน "Remark"
        // ของ Receipt มักจะเป็นเลข Invoice ต้นทาง (RelatedDocumentId).
        var docIds = lines.Where(l => l.DocumentId.HasValue).Select(l => l.DocumentId!.Value).Distinct().ToList();
        var docInfo = docIds.Count == 0
            ? new Dictionary<Guid, DocInfo>()
            : await (from d in _db.Documents.AsNoTracking()
                     where d.CompanyId == companyId && docIds.Contains(d.Id)
                     join c in _db.Contacts.AsNoTracking() on d.ContactId equals c.Id into cj
                     from c in cj.DefaultIfEmpty()
                     join rd in _db.Documents.AsNoTracking() on d.RelatedDocumentId equals rd.Id into rdj
                     from rd in rdj.DefaultIfEmpty()
                     select new { d.Id, d.DocumentNumber, ContactBranch = c != null ? c.BranchCode : null, RelatedNumber = rd != null ? rd.DocumentNumber : null })
                .ToDictionaryAsync(x => x.Id, x => new DocInfo(x.DocumentNumber, x.ContactBranch, x.RelatedNumber));

        var sheets = new Dictionary<string, object>();

        if (isVat)
        {
            sheets["รายงานภาษีขาย"] = BuildSalesVatSheet(report, company, lines.Where(l => LineSide(l) == "output"), docInfo);
            sheets["รายงานภาษีซื้อ"] = BuildPurchaseVatSheet(report, company, lines.Where(l => LineSide(l) == "input"), docInfo);
            sheets["สรุปรายงาน"] = BuildSummarySheet(report, company);
        }
        else
        {
            sheets["รายการ"] = BuildGenericLineSheet(lines);
            sheets["สรุปรายงาน"] = BuildSummarySheet(report, company);
        }

        using var ms = new MemoryStream();
        // printHeader:false — เราใส่หัวตารางเป็น row เองทุก sheet ให้ตรง
        // layout RD; ถ้าเปิด printHeader จะได้ A,B,C,D… โผล่บนสุด
        ms.SaveAs(sheets, printHeader: false);

        var typeCode = report.TaxType switch
        {
            TaxType.VAT => "PP30",
            TaxType.VatPp36 => "PP36",
            TaxType.WithholdingTax1 => "PND1",
            TaxType.WithholdingTax3 => "PND3",
            TaxType.WithholdingTax53 => "PND53",
            TaxType.WithholdingTax54 => "PND54",
            TaxType.CorporateIncomeTax => "PND50",
            TaxType.PersonalIncomeTax91 => "PND91",
            TaxType.SocialSecurity => "SSO",
            TaxType.StampDuty => "Stamp",
            _ => report.TaxType.ToString()
        };
        var fileName = $"{typeCode}_{report.Year:D4}-{report.Month:D2}.xlsx";
        return (ms.ToArray(), fileName);
    }

    private record DocInfo(string DocumentNumber, string? ContactBranch, string? RelatedNumber);

    private static readonly string[] ThaiMonths = {
        "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน", "พฤษภาคม", "มิถุนายน",
        "กรกฎาคม", "สิงหาคม", "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม"
    };

    /// <summary>"A".."N" — MiniExcel printHeader:false ใช้ key เป็น
    /// ลำดับ column. pad ทุก row ให้มี keys เท่ากันเพื่อไม่ให้ column shift.</summary>
    private static Dictionary<string, object?> Row(int width, params object?[] cells)
    {
        var d = new Dictionary<string, object?>(width);
        for (int i = 0; i < width; i++)
            d[((char)('A' + i)).ToString()] = i < cells.Length ? cells[i] : null;
        return d;
    }

    private static List<Dictionary<string, object?>> BuildSalesVatSheet(
        TaxReport report, Company? company, IEnumerable<TaxReportLine> lines,
        Dictionary<Guid, DocInfo> docInfo)
    {
        const int W = 10; // ลำดับ, ใบกำกับภาษี, วันที่, ชื่อผู้ซื้อ, เลขผู้เสียภาษี, สาขา, ฐานภาษี, ภาษี, รวม, Remark
        var rows = new List<Dictionary<string, object?>>
        {
            Row(W, "รายงานภาษีขาย"),
            Row(W, $"เดือน {ThaiMonths[report.Month]} ปี {report.Year + 543}"),
            Row(W, $"ชื่อผู้ประกอบการ {company?.Name ?? ""}", null, null, null,
                   "เลขประจำตัวผู้เสียภาษี", company?.TaxId ?? ""),
            Row(W, $"ชื่อสถานประกอบการ {company?.Name ?? ""}", null, null, null,
                   "สาขา", BranchLabel(company)),
            Row(W),
            Row(W, "ลำดับ", "ใบกำกับภาษี", "วันที่", "ชื่อผู้ซื้อสินค้า/บริการ",
                   "เลขประจำตัวผู้เสียภาษี", "สาขา", "มูลค่าที่คิดภาษี", "ภาษี",
                   "รวมทั้งสิ้น", "Remark"),
        };

        decimal sumBase = 0, sumTax = 0, sumTotal = 0;
        int i = 1;
        foreach (var l in lines)
        {
            var info = l.DocumentId.HasValue && docInfo.TryGetValue(l.DocumentId.Value, out var di) ? di : null;
            var total = l.IncomeAmount + l.TaxAmount;
            rows.Add(Row(W,
                i++,
                info?.DocumentNumber ?? l.Description ?? "",
                l.TransactionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                l.TaxPayerName ?? "",
                string.IsNullOrWhiteSpace(l.TaxPayerId) ? "00000000000000" : l.TaxPayerId,
                BranchCodeOrDefault(info?.ContactBranch),
                Round2(l.IncomeAmount),
                Round2(l.TaxAmount),
                Round2(total),
                info?.RelatedNumber ?? ""
            ));
            if (!l.IsExcluded)
            {
                sumBase += l.IncomeAmount;
                sumTax += l.TaxAmount;
                sumTotal += total;
            }
        }
        if (i == 1) rows.Add(Row(W, "", "", "", "(ไม่มีรายการ)"));
        rows.Add(Row(W, "", "", "", "", "", "รวม",
            Round2(sumBase), Round2(sumTax), Round2(sumTotal), ""));
        return rows;
    }

    private static List<Dictionary<string, object?>> BuildPurchaseVatSheet(
        TaxReport report, Company? company, IEnumerable<TaxReportLine> lines,
        Dictionary<Guid, DocInfo> docInfo)
    {
        const int W = 8; // ลำดับ, วัน เดือน ปี, เล่มที่/เลขที่, เลขผู้เสียภาษี, สาขา, ชื่อผู้ขาย, มูลค่าสินค้า/บริการ, VAT
        var rows = new List<Dictionary<string, object?>>
        {
            Row(W, "รายงานภาษีซื้อ"),
            Row(W, "ตามประกาศอธิบดีกรมสรรพากร เกี่ยวกับภาษีมูลค่าเพิ่ม (ฉบับที่ 104)"),
            Row(W, "สำหรับผู้ประกอบการรถเข็น/ล้อเลื่อน/ลักษณะอื่น ที่ได้รับอนุมัติให้จัดทำรายงานรวมกันกับสำนักงานใหญ่"),
            Row(W, $"เดือนภาษี {ThaiMonths[report.Month]}    ปี {report.Year + 543}"),
            Row(W),
            Row(W, $"ชื่อผู้ประกอบการที่ได้รับอนุมัติให้จัดทำรายงานรวม  {company?.Name ?? ""}",
                   null, null, null, "เลขประจำตัวผู้เสียภาษีอากร", company?.TaxId ?? ""),
            Row(W, $"ชื่อสถานประกอบการ  {company?.Name ?? ""}", null, null, null,
                   null, null, "สำนักงานใหญ่ที่จัดทำรายงานรวม"),
            Row(W),
            Row(W, "ลำดับที่", "วัน เดือน ปี", "เล่มที่/เลขที่", "เลขประจำตัวผู้เสียภาษี",
                   "สาขา", "ชื่อผู้ขายสินค้า/ผู้ให้บริการ", "มูลค่าสินค้าหรือบริการ",
                   "ภาษีมูลค่าเพิ่ม"),
        };

        decimal sumBase = 0, sumTax = 0;
        int i = 1;
        foreach (var l in lines)
        {
            var info = l.DocumentId.HasValue && docInfo.TryGetValue(l.DocumentId.Value, out var di) ? di : null;
            rows.Add(Row(W,
                i++,
                $"{(l.TransactionDate.Year + 543):D4}-{l.TransactionDate.Month:D2}-{l.TransactionDate.Day:D2}",
                info?.DocumentNumber ?? l.Description ?? "",
                string.IsNullOrWhiteSpace(l.TaxPayerId) ? "" : l.TaxPayerId,
                BranchCodeOrDefault(info?.ContactBranch),
                l.TaxPayerName ?? "",
                Round2(l.IncomeAmount),
                Round2(l.TaxAmount)
            ));
            if (!l.IsExcluded)
            {
                sumBase += l.IncomeAmount;
                sumTax += l.TaxAmount;
            }
        }
        if (i == 1) rows.Add(Row(W, "", "", "(ไม่มีรายการ)"));
        rows.Add(Row(W, "", "", "", "", "", "รวม", Round2(sumBase), Round2(sumTax)));
        return rows;
    }

    private static List<Dictionary<string, object?>> BuildSummarySheet(TaxReport report, Company? company)
    {
        const int W = 2;
        return new List<Dictionary<string, object?>>
        {
            Row(W, "รายการ", "ค่า"),
            Row(W, "ผู้ประกอบการ", company?.Name ?? ""),
            Row(W, "เลขประจำตัวผู้เสียภาษี", company?.TaxId ?? ""),
            Row(W, "สำนักงาน/สาขา", BranchLabel(company)),
            Row(W, "ประเภทรายงาน", report.TaxType.ToString()),
            Row(W, "งวดภาษี", $"{report.Month:D2}/{report.Year}"),
            Row(W, "สถานะ", report.Status.ToString()),
            Row(W, "ภาษีขาย (Output VAT)", Round2(report.OutputVat)),
            Row(W, "ภาษีซื้อ (Input VAT)", Round2(report.InputVat)),
            Row(W, "ภาษีสุทธิ", Round2(report.NetVat)),
        };
    }

    private static List<Dictionary<string, object?>> BuildGenericLineSheet(IEnumerable<TaxReportLine> lines)
    {
        const int W = 9;
        var rows = new List<Dictionary<string, object?>>
        {
            Row(W, "ลำดับ", "วันที่", "รายการ / เลขที่เอกสาร", "ชื่อผู้เสียภาษี",
                   "เลขประจำตัวผู้เสียภาษี", "มูลค่า", "อัตราภาษี (%)",
                   "ภาษีมูลค่าเพิ่ม", "สถานะ"),
        };
        int i = 1;
        foreach (var l in lines)
        {
            rows.Add(Row(W,
                i++,
                l.TransactionDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                l.Description ?? "",
                l.TaxPayerName ?? "",
                l.TaxPayerId ?? "",
                Round2(l.IncomeAmount),
                l.TaxRate,
                Round2(l.TaxAmount),
                l.IsExcluded ? "ไม่นำมาคิด" : "ใช้"
            ));
        }
        if (i == 1) rows.Add(Row(W, "", "", "(ไม่มีรายการ)"));
        return rows;
    }

    /// <summary>VAT side of a report line — matches the server-side recalc
    /// grouping (INPUT = ภาษีซื้อ, EXEMPT/carry-forward = summary, else output).</summary>
    private static string LineSide(TaxReportLine l) => l.IncomeTypeCode switch
    {
        "INPUT" or "JE_INPUT" => "input",
        "EXEMPT" or "VAT_CREDIT_CF" => "summary",
        _ => "output",
    };

    private static string BranchLabel(Company? c) => c?.BranchName
        ?? (c?.BranchCode == "00000" || string.IsNullOrEmpty(c?.BranchCode) ? "สำนักงานใหญ่" : c.BranchCode);

    /// <summary>RD template เคยเขียน "00000" สำหรับสำนักงานใหญ่/ไม่ระบุ —
    /// keep the same convention so the printed report matches the form.</summary>
    private static string BranchCodeOrDefault(string? code)
        => string.IsNullOrWhiteSpace(code) ? "00000" : code;

    private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

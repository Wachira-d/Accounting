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
    //   * PND.1   — ปิดแล้ว: ชี้ไปเมนู Export ยื่นภาษี (ตัวสร้างไฟล์เดียว TaxFilingExportService.ExportPnd1Async)
    //   * PND.2   — withholding on ม.40(4) interest/dividends paid to individuals (รอบ 204 ข้อ 148)
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
                // ⚠️ ทางออกที่สองของไฟล์ ภ.ง.ด.1 ถูกปิด (รอบ 2026-10-14 ทีม X1) — เดิมปุ่มนี้สร้างไฟล์จาก TaxReport ชนิด
                // ภ.ง.ด.1 ซึ่ง (ก) สร้างใหม่ไม่ได้แล้ว (CreateTaxReport ปฏิเสธ — ภ.ง.ด.1 มาจากเงินเดือนโดยตรง) ⇒ ใช้ได้แต่กับ
                // รายงานเก่าที่ค้างอยู่ = ยอดเก่า/ไม่ตรงรอบเงินเดือนปัจจุบัน (ข) ไม่แยกประเภทเงินได้ 5 ข้อ (ค) ด่านสิทธิ์คือ
                // Tax.Export ไม่ใช่ Payroll.View ⇒ ไฟล์ที่มีเลขบัตร+เงินเดือนรายคนหลุดถึงผู้ไม่มีสิทธิ์เงินเดือน.
                // ตัวสร้างไฟล์ ภ.ง.ด.1 มีที่เดียว: เมนู "Export ยื่นภาษี" (TaxFilingExportService.ExportPnd1Async →
                // Helpers/Pnd1FileFormat + Pnd1FilingData) — สองปุ่มจึงไม่มีทางได้ไฟล์ต่างกัน
                throw new Accounting.Helpers.BusinessRuleException(
                    "ไฟล์ ภ.ง.ด.1 สร้างจากรอบเงินเดือนโดยตรง — ไปที่เมนู \"Export ยื่นภาษี / SSO\" (การ์ด ภ.ง.ด.1) "
                    + "เพื่อดาวน์โหลด .txt (นำเข้าเว็บสรรพากร) / CSV / Excel · ต้องมีสิทธิ์ดูเงินเดือน (Payroll.View)",
                    "PND1-USE-PAYROLL-EXPORT");
            case "PND.2":
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
                throw new ArgumentException($"FormType '{formType}' ยังไม่รองรับ — ใช้ได้: PND.1, PND.2, PND.3, PND.53, PND.54, PND.50, PP.30, PP.36");
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
            "PND.2" => TaxType.WithholdingTax2,
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
        // แผนที่รหัสแบบ → ชนิดรายงาน อยู่ใน Helpers/WhtUnissuedCertGate ตัวเดียว
        // (ตัวที่หาคำเตือนมาแปะบนหน้าจอใช้แผนที่เดียวกันนี้ — ห้ามพิมพ์ซ้ำ)
        var taxType = Accounting.Helpers.WhtUnissuedCertGate.TaxTypeForPndForm(formType)
            ?? throw new ArgumentException(formType);
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
        //
        // ⚠️ การ "ตัดออก" ต้องไม่เงียบ (D2-B1a): แถวที่ตัดเพราะยังไม่มีใบ 50 ทวิ
        // ถูกสรุปเป็น reviewNote แล้วส่งขึ้นหน้าจอผ่าน header ของ endpoint
        // e-Filing (แบบเดียวกับ ExportPnd3Async ที่มี reviewNote อยู่แล้ว) —
        // ตัวสรุปคือ Helpers/WhtUnissuedCertGate ตัวเดียวกับด่านตอนยื่น
        var detailLines = PndDetailLines(report);

        // ⬇️ layout เดียวกับเมนู "ส่งออกไฟล์ยื่นภาษี" (PndTextFileFormat) —
        // detail rows ล้วน ไม่มี header/trailer เพราะหน้า import ของสรรพากร
        // ให้ map คอลัมน์เอง (บรรทัด H|/T| เดิมกลายเป็นแถวขยะที่ผู้ใช้ต้องลบ).
        // สองปุ่ม (ปุ่มนี้ กับเมนูส่งออก) ต้องได้ไฟล์หน้าตาเดียวกันเสมอ
        var isJuristicForm = IsJuristicPndForm(taxType);
        // ⚠️ สี่ช่องนี้เคยต่างจากเมนู "ส่งออกไฟล์ยื่นภาษี" ทั้งที่เป็นงวดเดียวกัน:
        //   Col3 สาขา     — hardcode null ⇒ "00000" ทุกแถว
        //   Col7 ประเภท   — ส่งรหัสภายในดิบ ("40(8)"/"8ad") แทนรหัสของ RD
        //   Col11 เงื่อนไข — ไม่ส่งเลย ⇒ default 1 ⇒ ใบ "ออกให้ตลอดไป" ยื่นผิดเงื่อนไข
        //   Col12 คำนำหน้า — ไม่มีที่เก็บ
        // ตอนนี้ TaxReportLine พาทั้งสี่มาจาก cert แล้ว (TaxService.GenerateWhtReport)
        // แถวเก่าที่สร้างก่อน migration ยังเป็น null → ตกไปใช้ค่าเดิมอย่างปลอดภัย
        var body = Accounting.Helpers.PndTextFileFormat.Build(detailLines.Select(l => PndRowOf(l, isJuristicForm)));

        var totalIncome = detailLines.Sum(l => l.IncomeAmount);
        var totalTax = detailLines.Sum(l => l.TaxAmount);
        return (body, totalIncome, totalTax, detailLines.Count);
    }

    /// <summary>บรรทัดรายงาน ภ.ง.ด. ที่ลงไฟล์ยื่น — ไม่เอาบรรทัด "[สรุป]" (SUMMARY) และบรรทัดที่ผู้ทำบัญชีติ๊กออก ·
    /// ตัวเดียวของปุ่ม e-Filing (.txt) และชีต "ใบแนบ" ใน Excel ของรายงาน (รอบ 2026-10-14 ทีม X1 — สองไฟล์ห้าม drift)</summary>
    internal static List<TaxReportLine> PndDetailLines(TaxReport report)
        => report.Lines
            .Where(l => l.IncomeTypeCode != "SUMMARY" && !l.IsExcluded)
            .OrderBy(x => x.LineOrder)
            .ToList();

    /// <summary>บรรทัดรายงาน → แถวไฟล์ ภ.ง.ด. (<see cref="Accounting.Helpers.PndTextFileFormat"/>) — ตัวแปลงเดียวของปุ่ม e-Filing และชีต Excel</summary>
    internal static Accounting.Helpers.PndTextFileFormat.Row PndRowOf(TaxReportLine l, bool isJuristicForm)
        => new(
            PayeeTaxId: l.TaxPayerId,
            BranchCode: l.TaxPayerBranchCode,
            PayeeName: l.TaxPayerName,
            IsJuristic: isJuristicForm,
            PayDate: l.TransactionDate,
            IncomeTypeCode: Accounting.Helpers.PndIncomeTypeCode.ForFile(l.IncomeTypeCode),
            IncomeAmount: l.IncomeAmount,
            TaxRate: l.TaxRate,
            TaxAmount: l.TaxAmount,
            Condition: l.WhtCondition ?? 1,
            PayeeTitle: l.TaxPayerTitle);

    /// <summary>แบบ ภ.ง.ด. ที่ผู้ถูกหักเป็นนิติบุคคล (ชื่อเต็มช่องเดียว) — ตัวเดียวของ e-Filing และ Excel</summary>
    internal static bool IsJuristicPndForm(TaxType taxType)
        => taxType is TaxType.WithholdingTax53 or TaxType.WithholdingTax54;

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

        // เฉพาะบรรทัดที่ผู้ทำบัญชียัง "ใช้" — เดิมวนทุกบรรทัด + header ใช้ยอด
        // persist ⇒ บรรทัดที่ติ๊กออกยังถูกส่ง RD และยอด H-row ไม่ตรงรายละเอียด.
        // recompute H-row จากบรรทัดที่ใช้จริง (กันยอดหัว stale จากรายงานเก่า)
        var pp36Lines = report.Lines.Where(l => !l.IsExcluded).OrderBy(x => x.LineOrder).ToList();
        var pp36Base = pp36Lines.Sum(l => l.IncomeAmount);
        var pp36Vat = pp36Lines.Sum(l => l.TaxAmount);

        // รอบ 2026-10-14 ทีม X1: รูปแบบไฟล์ผ่าน Helpers/Pp36FileFormat ตัวเดียวกับเมนู Export ยื่นภาษี (เดิมสองปุ่มให้ไฟล์
        // คนละหน้าตา — ที่นี่มีบรรทัดหัวสรุป + รหัสประเภทภายใน · เมนูโน้นเป็น H|D|T + dd/MM/พ.ศ.)
        var docInfo = await LoadPp36DocInfoAsync(_db, companyId, pp36Lines);
        var body = Accounting.Helpers.Pp36FileFormat.Build(pp36Lines.Select(l => Pp36RowOf(l, docInfo)));
        return (body, pp36Base, pp36Vat, pp36Lines.Count);
    }

    /// <summary>เลขที่เอกสาร + ประเทศผู้ให้บริการ (รหัส ISO ของผู้ติดต่อ) + หมายเหตุ ของบรรทัดรายงาน ภ.พ.36 —
    /// ตัวโหลดเดียวของสองทางออกไฟล์ (ปุ่ม e-Filing + เมนู Export) · กรอง CompanyId (กฎ M)</summary>
    internal static async Task<Dictionary<Guid, (string No, string? Country, string? Note)>> LoadPp36DocInfoAsync(
        Accounting.Data.AccountingDbContext db, Guid companyId, IReadOnlyCollection<TaxReportLine> lines)
    {
        var ids = lines.Where(l => l.DocumentId.HasValue).Select(l => l.DocumentId!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, (string No, string? Country, string? Note)>();
        return await (from d in db.Documents.AsNoTracking()
                      where d.CompanyId == companyId && ids.Contains(d.Id)
                      join c in db.Contacts.AsNoTracking().IgnoreQueryFilters() on d.ContactId equals c.Id into cj
                      from c in cj.DefaultIfEmpty()
                      select new { d.Id, d.DocumentNumber, Country = c != null ? c.CountryCode : null, d.Notes, d.Reference })
            .ToDictionaryAsync(x => x.Id, x => (
                No: x.DocumentNumber, Country: (string?)x.Country,
                Note: (string?)(x.Notes ?? x.Reference)));
    }

    /// <summary>บรรทัดรายงาน ภ.พ.36 → แถวไฟล์ (<see cref="Accounting.Helpers.Pp36FileFormat"/>) — ตัวแปลงเดียวของสองทางออก</summary>
    internal static Accounting.Helpers.Pp36FileFormat.Row Pp36RowOf(
        TaxReportLine l, IReadOnlyDictionary<Guid, (string No, string? Country, string? Note)> docInfo)
    {
        var info = l.DocumentId.HasValue && docInfo.TryGetValue(l.DocumentId.Value, out var x)
            ? x : (No: "", Country: (string?)null, Note: (string?)null);
        return new Accounting.Helpers.Pp36FileFormat.Row(
            SupplierName: l.TaxPayerName,
            CountryCode: info.Country,
            PayDate: l.TransactionDate,
            DocumentNo: info.No,
            Description: info.Note,
            BaseAmount: l.IncomeAmount,
            VatRate: l.TaxRate,
            VatAmount: l.TaxAmount);
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

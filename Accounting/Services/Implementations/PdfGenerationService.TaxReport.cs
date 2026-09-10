using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>PDF รายงานภาษีซื้อ/ขาย (§87 ประกาศ 104) + ฟอร์มสรุป ภ.พ.30 —
/// compose ด้วย QuestPDF โดยตรง (ไม่ผ่าน Chromium) สวย/คงที่ทุก server.
/// kind: "purchase" = รายงานภาษีซื้อ · "sales" = รายงานภาษีขาย · "pp30" = แบบ ภ.พ.30.</summary>
public partial class PdfGenerationService
{
    private static readonly string[] _taxMonthsTh =
        { "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน", "พฤษภาคม", "มิถุนายน",
          "กรกฎาคม", "สิงหาคม", "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม" };

    public async Task<byte[]> GenerateVatReportPdfAsync(Guid companyId, TaxReportResponse report, string kind)
    {
        EnsureThaiFontsRegistered();
        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);
        var settings = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId);
        var tmpl = await _db.Set<DocumentTemplate>().AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.DocumentType == DocumentType.Invoice)
            .OrderByDescending(t => t.IsDefault).FirstOrDefaultAsync();
        var theme = SanitizeHex(tmpl?.AccentColor) ?? SanitizeHex(tmpl?.TableHeaderColor)
            ?? SanitizeHex(settings?.PrimaryColor) ?? "#2563EB";
        var logo = TryReadImage(settings?.LogoPath) ?? TryReadImage(settings?.LogoUrl);

        var fontChain = new[] { "Sarabun", "TH Sarabun New", "Noto Sans Thai", Fonts.Calibri, Fonts.Arial };
        var coName = company?.Name ?? report.CompanyName ?? "บริษัท";
        var coTax = company?.TaxId ?? report.CompanyTaxId ?? "";
        var branch = string.IsNullOrWhiteSpace(report.CompanyBranchCode) || report.CompanyBranchCode == "00000"
            ? "สำนักงานใหญ่" : $"สาขาที่ {report.CompanyBranchCode}";
        var periodTh = $"{_taxMonthsTh[Math.Clamp(report.Month, 1, 12)]} {report.Year + 543}";

        var pdf = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(kind == "pp30" ? PageSizes.A4 : PageSizes.A4.Landscape());
                page.Margin(24);
                page.DefaultTextStyle(t => t.FontFamily(fontChain).FontSize(9).FontColor("#0F172A"));

                page.Header().Column(col =>
                {
                    col.Item().Row(r =>
                    {
                        if (logo != null) r.ConstantItem(46).Height(46).Image(logo);
                        r.RelativeItem().PaddingLeft(logo != null ? 10 : 0).Column(c =>
                        {
                            c.Item().Text(coName).FontSize(13).Bold().FontColor(theme);
                            if (!string.IsNullOrWhiteSpace(coTax))
                                c.Item().Text($"เลขประจำตัวผู้เสียภาษี {coTax}  ·  {branch}").FontSize(8).FontColor("#64748B");
                        });
                        r.ConstantItem(160).AlignRight().Column(c =>
                        {
                            var title = kind switch
                            {
                                "sales" => "รายงานภาษีขาย",
                                "pp30" => "แบบ ภ.พ.30 (สรุป)",
                                _ => "รายงานภาษีซื้อ"
                            };
                            c.Item().Text(title).FontSize(14).Bold().FontColor(theme);
                            c.Item().Text($"เดือนภาษี {periodTh}").FontSize(9);
                        });
                    });
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor(theme);
                });

                if (kind == "pp30")
                    ComposePp30(page, report, theme);
                else
                    ComposeVatDetail(page, report, kind, theme);

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("พิมพ์จากระบบ NextAcc · ").FontSize(7).FontColor("#94A3B8");
                    t.CurrentPageNumber().FontSize(7).FontColor("#94A3B8");
                    t.Span(" / ").FontSize(7).FontColor("#94A3B8");
                    t.TotalPages().FontSize(7).FontColor("#94A3B8");
                });
            });
        });

        using var ms = new MemoryStream();
        pdf.GeneratePdf(ms);
        return ms.ToArray();
    }

    private static void ComposeVatDetail(QuestPDF.Fluent.PageDescriptor page, TaxReportResponse report,
        string kind, string theme)
    {
        var isSales = kind == "sales";
        // ⚠️ เดิมฝั่งขายใช้ **deny-list** ("ไม่ใช่ INPUT/JE_INPUT = ขาย") ⇒ แถว
        // **ยอดรวมของแบบ ภ.พ.30** ที่ไม่มีวันที่/เลขที่ใบ (ยอดซื้อยกเว้น §81 ·
        // เครดิตภาษีซื้อยกมา) ตกเข้ารายงานภาษี**ขาย** เป็นรายการที่ 1 ลงวันที่
        // 01/01/0544 (= `default(DateTime)` + 543) และถูกบวกเข้ายอดรวมท้ายตาราง
        // ⇒ ยอดขายในรายงาน §87 เกินจริง (ผู้ใช้รายงาน 2026-09-10).
        // ตัวจำแนกอยู่ที่ Helpers/VatReportLineKind ตัวเดียว — ห้ามเทียบรหัสเองที่นี่
        var lines = report.Lines
            .Where(l => !l.IsExcluded)
            .Where(l => Accounting.Helpers.VatReportLineKind
                .BelongsToDetailReport(l.IncomeTypeCode, isSales))
            .OrderBy(l => l.TransactionDate).ThenBy(l => l.LineOrder)
            .ToList();
        // ยอดรวมของแบบที่ตัดออกจากตาราง ต้องยัง**หาเจอ**ในรายงานฝั่งของมัน —
        // ห้ามหายเงียบ (กฎเหล็ก #4 A "ห้าม silent no-op")
        var notes = report.Lines
            .Where(l => !l.IsExcluded)
            .Where(l => Accounting.Helpers.VatReportLineKind.IsNoteFor(l.IncomeTypeCode, isSales))
            .OrderBy(l => l.LineOrder)
            .ToList();
        var sumBase = lines.Sum(l => l.IncomeAmount);
        var sumVat = lines.Sum(l => l.TaxAmount);

        string ThDate(DateTime d) => $"{d.Day:D2}/{d.Month:D2}/{(d.Year + 543):D4}";
        string M(decimal v) => v.ToString("N2");

        page.Content().PaddingTop(8).Column(col =>
        {
            col.Item().Table(tbl =>
            {
                tbl.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(28);   // ลำดับ
                    c.ConstantColumn(70);   // วันที่
                    c.ConstantColumn(90);   // เลขที่ใบกำกับ
                    c.ConstantColumn(95);   // เลขผู้เสียภาษี
                    c.ConstantColumn(45);   // สาขา
                    c.RelativeColumn();     // ชื่อผู้ขาย/ผู้ซื้อ
                    c.ConstantColumn(85);   // มูลค่า
                    c.ConstantColumn(75);   // VAT
                });

                void HeadCell(string s) => tbl.Cell().Background(theme).Padding(4)
                    .Text(s).FontColor("#FFFFFF").FontSize(8).Bold();
                HeadCell("ลำดับ");
                HeadCell("วัน/เดือน/ปี");
                HeadCell("เลขที่ใบกำกับ");
                HeadCell("เลขผู้เสียภาษี");
                HeadCell("สาขา");
                HeadCell(isSales ? "ชื่อผู้ซื้อสินค้า/บริการ" : "ชื่อผู้ขายสินค้า/บริการ");
                tbl.Cell().Background(theme).Padding(4).AlignRight().Text("มูลค่า").FontColor("#FFFFFF").FontSize(8).Bold();
                tbl.Cell().Background(theme).Padding(4).AlignRight().Text("ภาษีมูลค่าเพิ่ม").FontColor("#FFFFFF").FontSize(8).Bold();

                int i = 1;
                foreach (var l in lines)
                {
                    var bg = i % 2 == 0 ? "#F8FAFC" : "#FFFFFF";
                    void Cell(string s, bool right = false)
                    {
                        var cc = tbl.Cell().Background(bg).PaddingVertical(3).PaddingHorizontal(4);
                        (right ? cc.AlignRight() : cc).Text(s).FontSize(8);
                    }
                    Cell(i.ToString());
                    Cell(ThDate(l.TransactionDate));
                    Cell(l.InvoiceNumber ?? l.Description ?? "");
                    Cell(l.TaxPayerId ?? "");
                    Cell(string.IsNullOrWhiteSpace(l.BranchCode) ? "00000" : l.BranchCode);
                    Cell(l.TaxPayerName ?? "");
                    Cell(M(l.IncomeAmount), true);
                    Cell(M(l.TaxAmount), true);
                    i++;
                }
                if (lines.Count == 0)
                    tbl.Cell().ColumnSpan(8).Padding(8).AlignCenter().Text("— ไม่มีรายการ —").FontColor("#94A3B8");

                // แถวรวม
                tbl.Cell().ColumnSpan(6).Background("#EEF2FF").Padding(5).AlignRight().Text("รวม").Bold();
                tbl.Cell().Background("#EEF2FF").Padding(5).AlignRight().Text(M(sumBase)).Bold();
                tbl.Cell().Background("#EEF2FF").Padding(5).AlignRight().Text(M(sumVat)).Bold().FontColor(theme);
            });

            col.Item().PaddingTop(10).AlignRight().Text($"จำนวน {lines.Count} รายการ").FontSize(8).FontColor("#64748B");

            // หมายเหตุท้ายรายงาน — ยอดที่ไม่ใช่ "รายการใบกำกับ" แต่ต้องกระทบยอด
            // กับแบบ ภ.พ.30 ได้ (ยอดขาย 0%/ยกเว้นในรายงานขาย · ยอดซื้อยกเว้นใน
            // รายงานซื้อ). ไม่รวมในยอดรวมของตารางข้างบนโดยตั้งใจ
            if (notes.Count > 0)
            {
                col.Item().PaddingTop(6).Text("หมายเหตุ (ไม่นับรวมในตาราง §87 — ใช้กระทบยอดกับแบบ ภ.พ.30)")
                    .FontSize(8).Bold().FontColor("#64748B");
                foreach (var n in notes)
                    col.Item().PaddingLeft(8).Text($"• {n.Description}  {M(n.IncomeAmount)}")
                        .FontSize(8).FontColor("#64748B");
            }
        });
    }

    private static void ComposePp30(QuestPDF.Fluent.PageDescriptor page, TaxReportResponse report, string theme)
    {
        // สรุปยอดตามช่องหลักของแบบ ภ.พ.30 — ใช้ทบทวน/เก็บแฟ้ม (การยื่นจริงใช้
        // ไฟล์ e-Filing ที่ระบบสร้างแยก). แยก 7% / 0% / ยกเว้น จาก TaxRate ของ line.
        // ต้องตัด VAT_CREDIT_CF (เครดิตภาษีซื้อยกมา — TaxRate=0 แต่ไม่ใช่ยอดขาย)
        // และ JE_INPUT ออกด้วย ไม่งั้นเครดิตยกมาถูกนับเป็น "ยอดขายที่ได้รับยกเว้น"
        // ในช่อง 1/§81. อีกทั้งขายอัตรา 0% (§80/1) เก็บ TaxRate = 0 เหมือนยกเว้น →
        // แยกไม่ได้ด้วยอัตรา ต้องดู IncomeTypeCode ("EXEMPT" = §81, ที่เหลือ 0% = ส่งออก)
        // ⚠️ เดิมแยกช่องด้วย `TaxRate < 6.5 && IncomeTypeCode == "EXEMPT"` ซึ่งเขียน
        // ไว้ตอนที่ "EXEMPT" ยังหมายถึงยอดยกเว้นของ**ทั้งสองฝั่ง** — หลังแยกเป็น
        // EXEMPT_SALES (ขาย) / EXEMPT (ซื้อ) รหัสเดิมกลายเป็นฝั่ง**ซื้อ** ⇒
        // ช่อง 8 ได้ยอด**ซื้อ**ยกเว้น และยอด**ขาย**ยกเว้นตกไปอยู่ช่อง 7 แทน
        // (ช่อง 7/8 สลับกัน) · ตัวแมปช่องอยู่ที่ Helpers/VatReportLineKind ตัวเดียว
        var active = report.Lines.Where(l => !l.IsExcluded).ToList();
        decimal BoxOf(Accounting.Helpers.Pp30SalesBox box) => active
            .Where(l => Accounting.Helpers.VatReportLineKind.SalesBoxOf(l.IncomeTypeCode) == box)
            .Sum(l => l.IncomeAmount);
        var zero = BoxOf(Accounting.Helpers.Pp30SalesBox.ZeroRated);
        var exempt = BoxOf(Accounting.Helpers.Pp30SalesBox.Exempt);
        // แถวรายการเอกสารเก็บฐานที่ "ไม่ยกเว้น" (7% + 0%) ⇒ ช่อง 5 ต้องหักยอด 0%
        // ออก ไม่ใช่คัดแถวตามอัตรา (ใบผสม 7%+0% เก็บ TaxRate = อัตราสูงสุดของใบ
        // ⇒ ฐาน 0% ของใบนั้นจะถูกนับเข้าช่อง 5 แล้วซ้ำกับแถวยอดรวมช่อง 7 อีกที)
        var std = Accounting.Helpers.VatReportLineKind.StandardBase(
            BoxOf(Accounting.Helpers.Pp30SalesBox.Standard7), zero);
        var totalSales = std + zero + exempt;
        var purchaseBase = active.Where(l => Accounting.Helpers.VatReportLineKind
            .SideOf(l.IncomeTypeCode) == Accounting.Helpers.VatReportSide.Input).Sum(l => l.IncomeAmount);
        // ยอดซื้อยกเว้น §81 ไม่มีช่องในแบบ — แสดงเป็นหมายเหตุ (เดิมถูกนับเป็น
        // ยอดขายยกเว้นช่อง 8). รายงานที่สร้าง**ก่อน**การแยกช่อง 7/8 รหัสนี้รวม
        // ยอดขายยกเว้นไว้ด้วย → บอกให้กดสร้างรายงานใหม่ ไม่ใช่เดาแทนผู้ใช้
        var exemptPurchaseNote = active
            .Where(l => l.IncomeTypeCode == Accounting.Helpers.VatReportLineKind.ExemptPurchases)
            .Sum(l => l.IncomeAmount);

        string M(decimal v) => v.ToString("N2");
        var payable = report.NetVat >= 0 ? report.NetVat : 0;
        var credit = report.NetVat < 0 ? -report.NetVat : 0;

        page.Content().PaddingTop(12).Column(col =>
        {
            col.Spacing(0);
            void Box(string no, string label, string value, bool strong = false, string? color = null)
            {
                col.Item().Border(0.6f).BorderColor("#CBD5E1").Row(r =>
                {
                    r.ConstantItem(34).Background("#F1F5F9").Padding(6).AlignCenter().Text(no).FontSize(9).Bold();
                    var lbl = r.RelativeItem().Padding(6).Text(label).FontSize(strong ? 11 : 10);
                    if (strong) lbl.Bold();
                    var val = r.ConstantItem(150).Padding(6).AlignRight().Text(value + " บาท")
                        .FontSize(strong ? 12 : 10).FontColor(color ?? "#0F172A");
                    if (strong) val.Bold();
                });
            }

            col.Item().PaddingBottom(6).Text("การคำนวณภาษีมูลค่าเพิ่ม").FontSize(11).Bold().FontColor(theme);
            Box("1", "ยอดขายในเดือนภาษีนี้ (รวมทุกอัตรา)", M(totalSales));
            Box("2", "  • ยอดขายที่ต้องเสียภาษี (อัตรา 7%)", M(std));
            Box("3", "  • ยอดขายที่เสียภาษีอัตรา 0% (ส่งออก §80/1)", M(zero));
            Box("4", "  • ยอดขายที่ได้รับยกเว้น (§81)", M(exempt));
            Box("5", "ภาษีขาย (Output VAT)", M(report.OutputVat), color: theme);
            col.Item().PaddingVertical(6);
            Box("6", "ยอดซื้อที่มีสิทธินำภาษีซื้อมาหัก", M(purchaseBase));
            Box("7", "ภาษีซื้อ (Input VAT)", M(report.InputVat), color: theme);
            col.Item().PaddingVertical(6);
            Box("8", "ภาษีที่ต้องชำระในเดือนนี้", M(payable), strong: payable > 0, color: payable > 0 ? "#DC2626" : "#0F172A");
            Box("9", "ภาษีที่ชำระเกิน (ยกไปเดือนถัดไป/ขอคืน)", M(credit), strong: credit > 0, color: credit > 0 ? "#16A34A" : "#0F172A");

            if (exemptPurchaseNote != 0)
            {
                col.Item().PaddingTop(10).Text(
                        $"ยอดซื้อที่ได้รับยกเว้นภาษี (§81) {M(exemptPurchaseNote)} บาท — ไม่เข้าช่องใดของแบบ ภ.พ.30 "
                        + "(ภาษีซื้อของยอดนี้ลงเป็นต้นทุน ไม่ใช่เครดิต)")
                    .FontSize(8).FontColor("#64748B");
            }

            col.Item().PaddingTop(16).Text(t =>
            {
                t.Span("หมายเหตุ: ").FontSize(8).Bold().FontColor("#64748B");
                t.Span("เอกสารสรุปนี้ใช้ทบทวน/เก็บแฟ้มภายใน การยื่นจริงต่อกรมสรรพากรให้ใช้ไฟล์ e-Filing "
                    + "ที่ระบบสร้าง หรือกรอกในระบบ RD e-Filing ตามยอดข้างต้น (กำหนดยื่นภายในวันที่ 15 ของเดือนถัดไป).")
                    .FontSize(8).FontColor("#64748B");
            });
        });
    }
}

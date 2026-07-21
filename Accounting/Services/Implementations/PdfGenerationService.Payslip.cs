using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Accounting.Models.DTOs.Payroll;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// สลิปเงินเดือนดีไซน์ใหม่ — compose ด้วย QuestPDF โดยตรง (ไม่ผ่าน HTML→Chromium
/// ที่ต้องเปิด Puppeteer) จึงสวยคงที่ทุก server. ใช้โลโก้ + สีธีมจากเทมเพลต
/// ใบกำกับ (AccentColor/TableHeaderColor) ให้เอกสารพนักงานเข้าชุดกับเอกสารขาย.
/// โครง: แถบหัวสีธีม (โลโก้+ชื่อบริษัท / ชื่อสลิป+งวด) · การ์ดข้อมูลพนักงาน ·
/// 2 คอลัมน์ รายได้/รายการหัก · แถบเงินสุทธิเด่น · YTD · footnote.
/// </summary>
public partial class PdfGenerationService
{
    private static readonly string[] _payslipMonthsTh =
        { "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน", "พฤษภาคม", "มิถุนายน",
          "กรกฎาคม", "สิงหาคม", "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม" };

    public async Task<byte[]> GeneratePayslipPdfAsync(Guid companyId, PayslipPdfData d)
    {
        EnsureThaiFontsRegistered();

        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);
        var settings = await _db.CompanySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId);
        // สีธีมจากเทมเพลตใบกำกับ (accent/header) → fallback ตั้งค่า → default น้ำเงิน
        var tmpl = await _db.Set<DocumentTemplate>().AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.DocumentType == DocumentType.Invoice)
            .OrderByDescending(t => t.IsDefault)
            .FirstOrDefaultAsync();
        var theme = SanitizeHex(tmpl?.AccentColor) ?? SanitizeHex(tmpl?.TableHeaderColor)
            ?? SanitizeHex(settings?.PrimaryColor) ?? "#2563EB";
        var logo = TryReadImage(settings?.LogoPath) ?? TryReadImage(settings?.LogoUrl);

        var fontChain = new[] { "Sarabun", "TH Sarabun New", "Noto Sans Thai", Fonts.Calibri, Fonts.Arial };
        const string White = "#FFFFFF", Dark = "#0F172A", Sub = "#64748B",
                     Border = "#E2E8F0", HairLine = "#F1F5F9", TintBg = "#F8FAFC";
        string M(decimal v) => v.ToString("N2");
        var periodTh = $"{_payslipMonthsTh[Math.Clamp(d.Month, 1, 12)]} {d.Year + 543}";

        var coSubParts = new[]
        {
            string.IsNullOrWhiteSpace(company?.TaxId) ? null : $"เลขประจำตัวผู้เสียภาษี {company!.TaxId}",
            string.IsNullOrWhiteSpace(company?.Address) ? null : company!.Address,
        }.Where(x => !string.IsNullOrWhiteSpace(x));
        var coSub = string.Join("  ·  ", coSubParts);

        var earnings = new (string Label, decimal Val)[]
        {
            ("เงินเดือน", d.BaseSalary), ("ค่าล่วงเวลา (OT)", d.OvertimePay),
            ("เบี้ยเลี้ยง / ค่าครองชีพ", d.Allowances), ("คอมมิชชัน", d.Commission),
            ("โบนัส", d.Bonus), ("รายได้อื่น", d.OtherIncome),
        };
        var deductions = new (string Label, decimal Val)[]
        {
            ("ประกันสังคม", d.SocialSecurityEmployee), ("ภาษีหัก ณ ที่จ่าย", d.WithholdingTax),
            ("กองทุนสำรองเลี้ยงชีพ", d.ProvidentFundEmployee), ("หักเงินกู้", d.LoanDeduction),
            ("หักอื่น ๆ", d.OtherDeductions),
        };

        var pdf = QuestPDF.Fluent.Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(14, Unit.Millimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(t => t.FontFamily(fontChain).FontSize(11).FontColor(Dark));

                page.Content().Column(col =>
                {
                    // ── Header band ──
                    col.Item().Background(theme).Padding(16).Row(r =>
                    {
                        r.RelativeItem().Row(left =>
                        {
                            if (logo != null)
                            {
                                left.ConstantItem(48).Height(48).Background(White).Padding(3)
                                    .Image(logo).FitArea();
                                left.ConstantItem(12);
                            }
                            left.RelativeItem().AlignMiddle().Column(cc =>
                            {
                                cc.Item().Text(company?.Name ?? "บริษัท").FontSize(16).Bold().FontColor(White);
                                if (!string.IsNullOrWhiteSpace(coSub))
                                    cc.Item().PaddingTop(2).Text(coSub).FontSize(9).FontColor(White);
                            });
                        });
                        r.ConstantItem(180).AlignMiddle().Column(cc =>
                        {
                            cc.Item().AlignRight().Text("สลิปเงินเดือน").FontSize(15).Bold().FontColor(White);
                            cc.Item().AlignRight().Text($"Payslip · {periodTh}").FontSize(10).FontColor(White);
                        });
                    });

                    // ── Employee meta strip ──
                    col.Item().Border(1).BorderColor(Border).Background(TintBg).Row(r =>
                    {
                        void Cell(string k, string v)
                        {
                            r.RelativeItem().Padding(9).Column(cc =>
                            {
                                cc.Item().Text(k).FontSize(8).FontColor(Sub);
                                cc.Item().PaddingTop(1).Text(string.IsNullOrWhiteSpace(v) ? "-" : v)
                                    .FontSize(11).SemiBold().FontColor(Dark);
                            });
                        }
                        Cell("พนักงาน", d.EmployeeName);
                        Cell("รหัส", d.EmployeeCode);
                        Cell("แผนก / ตำแหน่ง", $"{d.Department} · {d.Position}");
                        Cell("วันที่จ่าย", $"{d.PayDate:dd/MM/}{d.PayDate.Year + 543}");
                    });

                    // ── Earnings / Deductions ──
                    col.Item().Row(r =>
                    {
                        r.RelativeItem().Border(1).BorderColor(Border)
                            .Column(cc => PayslipSection(cc, "รายได้ (Earnings)", theme,
                                earnings, "รวมรายได้", d.GrossIncome, M, TintBg, HairLine, Sub));
                        r.ConstantItem(12);
                        r.RelativeItem().Border(1).BorderColor(Border)
                            .Column(cc => PayslipSection(cc, "รายการหัก (Deductions)", "#B91C1C",
                                deductions, "รวมรายการหัก", d.TotalDeductions, M, TintBg, HairLine, Sub));
                    });

                    // ── Net pay band ──
                    col.Item().PaddingTop(12).Background(theme).Padding(16).Row(r =>
                    {
                        r.RelativeItem().AlignMiddle().Text("เงินได้สุทธิ (Net Pay)")
                            .FontSize(13).Bold().FontColor(White);
                        r.ConstantItem(220).AlignRight().AlignMiddle().Text($"{M(d.NetPay)} ฿")
                            .FontSize(22).Bold().FontColor(White);
                    });

                    // ── YTD ──
                    col.Item().PaddingTop(10).AlignRight().Text(t =>
                    {
                        t.Span("รายได้สะสมทั้งปี (YTD): ").FontSize(9).FontColor(Sub);
                        t.Span(M(d.YtdIncome)).FontSize(9).SemiBold().FontColor(Dark);
                        t.Span("        ภาษีสะสมทั้งปี (YTD): ").FontSize(9).FontColor(Sub);
                        t.Span(M(d.YtdTax)).FontSize(9).SemiBold().FontColor(Dark);
                    });

                    col.Item().PaddingTop(14).AlignCenter()
                        .Text($"เอกสารนี้ออกโดยระบบ NextAcc · งวด {periodTh} · พิมพ์เพื่อเก็บเป็นหลักฐานการรับเงินเดือน")
                        .FontSize(8).FontColor("#94A3B8");
                });
            });
        });

        return pdf.GeneratePdf();
    }

    /// <summary>1 คอลัมน์ในตารางรายได้/รายการหัก: หัวข้อ + แถว (ซ่อนแถวที่เป็น 0
    /// ยกเว้นแถวแรก) + แถวรวม.</summary>
    private static void PayslipSection(QuestPDF.Fluent.ColumnDescriptor col, string title,
        string titleColor, (string Label, decimal Val)[] items, string subLabel, decimal subTotal,
        Func<decimal, string> M, string subBg, string hair, string sub)
    {
        col.Item().Background("#F8FAFC").BorderBottom(1).BorderColor("#E2E8F0").Padding(9)
            .Text(title).Bold().FontSize(11).FontColor(titleColor);

        var shown = items.Where((x, i) => i == 0 || x.Val != 0).ToList();
        foreach (var it in shown)
        {
            col.Item().BorderBottom(1).BorderColor(hair).PaddingVertical(6).PaddingHorizontal(12).Row(r =>
            {
                r.RelativeItem().AlignMiddle().Text(it.Label).FontSize(10.5f);
                r.ConstantItem(92).AlignRight().AlignMiddle().Text(M(it.Val)).FontSize(10.5f);
            });
        }

        col.Item().Background(subBg).PaddingVertical(8).PaddingHorizontal(12).Row(r =>
        {
            r.RelativeItem().AlignMiddle().Text(subLabel).Bold().FontSize(11);
            r.ConstantItem(92).AlignRight().AlignMiddle().Text(M(subTotal)).Bold().FontSize(11);
        });
    }
}

using System.Net;
using System.Text;
using Accounting.Helpers;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 246 QR — <b>กล่อง QR ชำระเงินของทั้งสอง renderer</b> (HTML <see cref="PaymentQrHtml"/> · QuestPDF <see cref="ComposePaymentQr"/>) ·
/// ข้อมูลมาจาก <see cref="PaymentQrBlock"/> ที่ <c>PaymentQrService.ResolveForDocumentAsync</c> สร้างตัวเดียว · ข้อความบนกระดาษผ่าน
/// <see cref="PaymentQrCaption"/> ตัวเดียว (ป้ายจาก DocumentLabels — สอง renderer ห้าม drift · กฎเหล็ก #4 A)
/// <para>รูปฝังเป็นไบต์ PNG ที่เซิร์ฟเวอร์สร้าง/เข้ารหัสใหม่เท่านั้น (data URI จากไบต์ — ไม่มีสตริงจากผู้ใช้ใน src) · ข้อความทุกช่อง HtmlEncode</para>
/// </summary>
public partial class PdfGenerationService
{
    /// <summary>หัวข้อ + บรรทัดใต้ QR ของแต่ละกล่อง (ลำดับเดียวกันทั้งสอง renderer)</summary>
    internal sealed record QrCaption(string Title, IReadOnlyList<string> Lines);

    internal static QrCaption PaymentQrCaption(BankPaymentQr b, Pdf.DocumentLabels L)
    {
        // ฝ่ายค้าน S2(ข): ธนาคาร / ชื่อบัญชี / เลขบัญชี ของบัญชีที่ QR ใช้จริงเสมอ (ข้อความอิสระของเทมเพลตอาจเป็นบัญชีอื่น)
        var lines = new List<string>();
        if (b.Kind == PaymentQrPolicy.Kind.PromptPay && b.PromptPayId != null)
            lines.Add($"{L.PayQrPromptPay} {PaymentQrPolicy.DisplayPromptPayId(b.PromptPayId)}");
        if (!string.IsNullOrWhiteSpace(b.BankName)) lines.Add(L.PayQrBank(b.BankName!));
        if (!string.IsNullOrWhiteSpace(b.AccountName)) lines.Add(L.PayQrAccountName(b.AccountName!));
        if (!string.IsNullOrWhiteSpace(b.AccountNumber)) lines.Add(L.PayQrAccountNo(b.AccountNumber!));
        lines.Add(b.Amount is decimal amt ? L.PayQrAmount(amt) : L.PayQrNoAmount);
        // ฝ่ายค้าน N1: ยอดที่ฝังคือยอด ณ วันที่พิมพ์ (ไฟล์ e-Tax/สำเนาเก่าที่ถูกเปิดภายหลังอาจไม่ตรงยอดค้างปัจจุบัน — คำตัดสิน 246-QR-5)
        if (b.Amount != null && b.AmountAsOf is DateTime asOf) lines.Add(L.PayQrAmountAsOf(asOf));
        return new QrCaption(b.Kind == PaymentQrPolicy.Kind.PromptPay ? L.PayQrPromptPayTitle : L.PayQrUploadTitle, lines);
    }

    internal static QrCaption PayLinkQrCaption(PayLinkQr p, Pdf.DocumentLabels L)
        => new(L.PayQrLinkTitle, p.ExpiresAt is DateTime e ? new[] { L.PayQrLinkExpires(e) } : Array.Empty<string>());

    /// <summary>HTML — วางต่อจากข้อมูลบัญชีธนาคาร · null/ว่าง = สตริงว่าง</summary>
    internal static string PaymentQrHtml(PaymentQrBlock? qr, Pdf.DocumentLabels L)
    {
        if (qr == null || qr.IsEmpty) return "";
        var sb = new StringBuilder();
        sb.Append("<div class='payment-qr' style='display:flex;gap:18px;flex-wrap:wrap;margin-top:10px;page-break-inside:avoid;break-inside:avoid'>");
        void Box(byte[] png, QrCaption c)
        {
            sb.Append("<div class='payment-qr-box' style='width:150px;text-align:center;font-size:11px;line-height:1.35'>");
            sb.Append("<img src='data:image/png;base64,").Append(Convert.ToBase64String(png)).Append("' alt='")
              .Append(WebUtility.HtmlEncode(c.Title)).Append("' style='width:128px;height:128px;object-fit:contain;display:block;margin:0 auto 4px'/>");
            sb.Append("<div><strong>").Append(WebUtility.HtmlEncode(c.Title)).Append("</strong></div>");
            foreach (var line in c.Lines) sb.Append("<div>").Append(WebUtility.HtmlEncode(line)).Append("</div>");
            sb.Append("</div>");
        }
        if (qr.Bank is { } b) Box(b.Png, PaymentQrCaption(b, L));
        if (qr.PayLink is { } p) Box(p.Png, PayLinkQrCaption(p, L));
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>QuestPDF — คู่ของ <see cref="PaymentQrHtml"/> (ข้อความ/ลำดับเดียวกัน) · null/ว่าง = ไม่วาด</summary>
    internal static void ComposePaymentQr(ColumnDescriptor col, PaymentQrBlock? qr, Pdf.DocumentLabels L)
    {
        if (qr == null || qr.IsEmpty) return;
        col.Item().PaddingTop(10).ShowEntire().Row(r =>
        {
            void Box(byte[] png, QrCaption c) => r.ConstantItem(46, Unit.Millimetre).PaddingRight(4).Column(cc =>
            {
                cc.Item().AlignCenter().Width(32, Unit.Millimetre).Height(32, Unit.Millimetre).Image(png).FitArea();
                cc.Item().AlignCenter().Text(c.Title).Bold().FontSize(9);
                foreach (var line in c.Lines) cc.Item().AlignCenter().Text(line).FontSize(8);
            });
            if (qr.Bank is { } b) Box(b.Png, PaymentQrCaption(b, L));
            if (qr.PayLink is { } p) Box(p.Png, PayLinkQrCaption(p, L));
            r.RelativeItem();
        });
    }

    /// <summary>ตัวเรียกตัวเดียวของ resolver จากเส้นพิมพ์เอกสารจริง (PDF · HTML · e-Tax) — ไม่มี service (เทสต์เก่า) = ไม่มี QR ·
    /// PDF/A-3 ของ e-Tax ตัด QR หน้าชำระ (มี token) ทิ้ง (คำตัดสิน 246-QR-5)</summary>
    private async Task<PaymentQrBlock?> ResolvePaymentQrAsync(Models.Entities.Document doc, Models.Entities.DocumentTemplate template, bool pdfA)
    {
        if (_paymentQr == null) return null;
        var block = await _paymentQr.ResolveForDocumentAsync(doc, template);
        return pdfA ? block?.WithoutPayLink() : block;
    }
}

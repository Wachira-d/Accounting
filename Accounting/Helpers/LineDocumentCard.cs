using System.Globalization;
using System.Text;
using Accounting.Services.Implementations.Pdf;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 254 WP-11 L2 (LINE_MULTICHANNEL_DESIGN §3.f · §6.2 · LINE_OA_TERMS_CHECK C2/C5) — <b>ตัวประกอบการ์ด Flex ของเอกสารตัวเดียว</b> (pure)
/// <list type="bullet">
/// <item><b>ส่งให้น้อยที่สุด</b> (PDPA ม.22 · §6.3): ชื่อเอกสาร · เลขที่ · ยอด (ซ่อนได้ต่อกลุ่ม — Q5) · วันครบกำหนด · ชื่อผู้รับ — <b>ไม่มี</b>เลขผู้เสียภาษี/ที่อยู่/รายการสินค้า ·
/// ไม่แนบไฟล์ (LINE ไม่มีข้อความไฟล์สำหรับ push) · ปุ่มเฉพาะลิงก์ https ที่ผู้เรียกได้จากตัวตัดสินลิงก์ชำระ (<c>DocumentPayLinkService</c>)</item>
/// <item><b>ป้ายผู้ส่ง</b> (C2): OA ของบริษัท ⇒ "จาก {บริษัท}" · OA กลาง ⇒ "ในนาม {บริษัท} · ผ่าน NextAcc" (ทั้งในการ์ดและ altText) · <b>ช่องทางยกเลิก</b> (C5) ทุกการ์ด</item>
/// <item>ภาษา/หัวเอกสาร = ตัวกลางเดียวกับ PDF (<c>PdfGenerationService.ResolveDocumentHeadingAsync</c> ⇒ <see cref="Input.Language"/>/<see cref="Input.Title"/>) ·
/// ป้ายผ่าน <see cref="DocumentLabels"/> (ห้าม literal — กฎ #4 A) · วันที่: ไทย = พ.ศ. (<see cref="ThaiDate.ToThaiDisplayString"/>) · อังกฤษ = ค.ศ.</item>
/// <item>Flex เป็น JSON (ไม่ใช่ HTML) ⇒ ไม่ HtmlEncode แต่ตัดอักขระควบคุม + จำกัดความยาว (<see cref="Clean"/>) · altText ≤ 400 ตัวอักษร</item>
/// </list>
/// </summary>
public static class LineDocumentCard
{
    public const int MaxAltText = 400;
    /// <summary>ป้ายปุ่มของ LINE ได้ไม่เกิน 20 ตัวอักษร (⚠ DESIGN §7.4)</summary>
    public const int MaxButtonLabel = 20;

    /// <param name="Amount">ยอดที่แสดง (null = ซ่อน — กลุ่มที่ปิดแสดงยอด)</param>
    /// <param name="PayUrl">ลิงก์ชำระ (https เท่านั้น · null = ไม่มีปุ่ม)</param>
    /// <param name="AmountIsOutstanding">true = ยอดค้างชำระ ("ยอดชำระ") · false = ยอดรวมของใบที่ไม่มียอดค้าง ("ยอดรวมทั้งสิ้น")</param>
    /// <param name="ViewUrl">รอบ 258-3 (F-H1 · A-L8): ลิงก์ดู/ดาวน์โหลด PDF (<see cref="LineDocumentViewLink"/> · https เท่านั้น · null = ไม่มีปุ่ม)</param>
    /// <param name="PaymentReceived">รอบ 258-3 (F-L2): การ์ดใบเสร็จหลังรับชำระ ⇒ บรรทัด "ได้รับชำระเงินแล้ว ขอบคุณ"</param>
    /// <param name="PageUrl">รอบ 267 P3: ลิงก์หน้าเอกสาร (https · null = ไม่มี) — มีค่า ⇒ ปุ่ม <paramref name="PageButton"/> (ป้ายจาก <c>CustomerShareRule.PageButtonLabel</c>) +
    /// ปุ่มชำระเมื่อผู้ใช้ติ๊ก (<paramref name="PayUrl"/>) · ไม่ซ้อนปุ่มดู PDF (หน้ามีดาวน์โหลด) · ผู้เรียกตัดสินสิทธิ์ของปลายทางด้วย <c>DocumentLineSendRule.PageLinkAllowed</c> ก่อนส่งค่ามา</param>
    /// <param name="SignedCopy">รอบ 267 P3: การ์ด "ฉบับเซ็น" กลับไป LINE เดิมหลังลูกค้าลงนาม (บรรทัดขอบคุณ + ปุ่ม "ฉบับเซ็น" ไปหน้าเอกสาร)</param>
    public sealed record Input(string CompanyName, bool ViaNextAcc, string Language, string Title, string DocNumber,
        decimal? Amount, string? Currency, DateTime? DueDate, string? ContactName, string? PayUrl, bool IsGroup, bool AmountIsOutstanding = true,
        string? ViewUrl = null, bool PaymentReceived = false, string? PageUrl = null, string? PageButton = null, bool SignedCopy = false);

    public sealed record Card(string AltText, object Contents);

    public static Card Build(Input i)
    {
        var l = DocumentLabels.For(i.Language);
        var company = Clean(i.CompanyName, 120);
        var brand = i.ViaNextAcc ? l.LineCardOnBehalf(company) : l.LineCardFrom(company);
        var title = Clean(i.Title, 80);
        var number = Clean(i.DocNumber, 60);
        var due = i.DueDate is DateTime d ? FormatDate(d, l) : null;
        var amount = i.Amount is decimal a ? FormatAmount(a, i.Currency) : null;

        var body = new List<object>
        {
            new { type = "text", text = brand, size = "xs", color = "#64748b", wrap = true },
            new { type = "text", text = title, weight = "bold", size = "lg", wrap = true },
            new { type = "text", text = number, size = "sm", color = "#334155", wrap = true },
        };
        if (i.PaymentReceived) body.Add(new { type = "text", text = l.LineCardPaymentReceived, size = "sm", color = "#15803d", weight = "bold", wrap = true });
        if (i.SignedCopy) body.Add(new { type = "text", text = l.LineCardSignedReceived, size = "sm", color = "#15803d", weight = "bold", wrap = true });
        if (amount != null) body.Add(Row(i.AmountIsOutstanding ? l.LineCardAmountDue : l.TotalGrand, amount, bold: true));
        if (due != null) body.Add(Row(l.DueDate, due, bold: false));
        if (!string.IsNullOrWhiteSpace(i.ContactName)) body.Add(Row(l.LineCardTo, Clean(i.ContactName!, 120), bold: false));

        var footer = new List<object>();
        var page = SafeUrl(i.PageUrl);
        if (page != null)
            footer.Add(new { type = "button", style = "primary", height = "sm",
                action = new { type = "uri", label = Clean(i.SignedCopy ? l.LineCardSignedCopy : i.PageButton ?? l.LineCardOpen, MaxButtonLabel), uri = page } });
        // ฝ่ายค้าน 267-P3 MED-2: ผู้ใช้ติ๊ก "ใส่ปุ่มชำระ" ⇒ ปุ่มชำระอยู่คู่กับปุ่มหน้าเอกสาร (ไม่ทิ้งค่าที่ติ๊กเงียบ ๆ)
        if (SafeUrl(i.PayUrl) is string pay)
            footer.Add(new { type = "button", style = footer.Count == 0 ? "primary" : "secondary", height = "sm",
                action = new { type = "uri", label = Clean(l.LineCardPay, MaxButtonLabel), uri = pay } });
        if (page == null && SafeUrl(i.ViewUrl) is string view)
            footer.Add(new { type = "button", style = footer.Count == 0 ? "primary" : "secondary", height = "sm",
                action = new { type = "uri", label = Clean(l.LineCardViewPdf, MaxButtonLabel), uri = view } });
        footer.Add(new { type = "text", text = i.IsGroup ? l.LineCardStopGroup : l.LineCardStopUser, size = "xxs", color = "#94a3b8", wrap = true });

        var contents = new
        {
            type = "bubble",
            body = new { type = "box", layout = "vertical", spacing = "sm", contents = body },
            footer = new { type = "box", layout = "vertical", spacing = "sm", contents = footer },
        };
        var alt = new StringBuilder();
        alt.Append(title).Append(' ').Append(number).Append(" — ").Append(brand);
        if (i.PaymentReceived) alt.Append(" · ").Append(l.LineCardPaymentReceived);
        if (i.SignedCopy) alt.Append(" · ").Append(l.LineCardSignedCopy);
        if (due != null) alt.Append(" · ").Append(l.DueDate).Append(' ').Append(due);
        if (amount != null) alt.Append(" · ").Append(amount);
        return new Card(LineMessageLimits.Truncate(alt.ToString(), MaxAltText), contents);
    }

    private static object Row(string label, string value, bool bold) => new
    {
        type = "box",
        layout = "baseline",
        spacing = "sm",
        contents = new object[]
        {
            new { type = "text", text = label, size = "sm", color = "#64748b", flex = 2, wrap = true },
            new { type = "text", text = value, size = "sm", weight = bold ? "bold" : "regular", align = "end", flex = 3, wrap = true },
        },
    };

    /// <summary>วันที่ตามภาษาเอกสาร — ไทย = dd/MM/พ.ศ. · อังกฤษ = ตามป้ายภาษาอังกฤษ (ค.ศ.)</summary>
    internal static string FormatDate(DateTime d, DocumentLabels l)
        => l.IsEnglish ? l.Date(ThaiDate.CalendarDateUtc(d)) : ThaiDate.ToThaiDisplayString(d);

    internal static string FormatAmount(decimal amount, string? currency)
    {
        var cur = string.IsNullOrWhiteSpace(currency) ? "THB" : currency!.Trim().ToUpperInvariant();
        return amount.ToString("N2", CultureInfo.InvariantCulture) + " " + Clean(cur, 3);
    }

    /// <summary>ข้อความในการ์ด — ตัดอักขระควบคุม (รวมขึ้นบรรทัด) แล้วจำกัดความยาว · ว่าง ⇒ "-" (LINE ปฏิเสธ text ว่าง)</summary>
    internal static string Clean(string? text, int max)
    {
        var sb = new StringBuilder();
        foreach (var ch in text ?? "")
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        var t = sb.ToString().Trim();
        if (t.Length == 0) return "-";
        return LineMessageLimits.Truncate(t, max);
    }

    /// <summary>ปุ่มรับเฉพาะ URL เต็ม https (LINE ปฏิเสธ http/relative ทั้งการ์ด) · ไม่ใช่ ⇒ ไม่มีปุ่ม</summary>
    internal static string? SafeUrl(string? url)
    {
        var u = (url ?? "").Trim();
        return u.Length > 0 && u.Length <= 1000 && Uri.TryCreate(u, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps ? u : null;
    }
}

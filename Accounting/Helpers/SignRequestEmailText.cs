using System.Globalization;
using System.Net;

namespace Accounting.Helpers;

/// <summary>
/// **หัว/เนื้ออีเมล "ขอให้ลูกค้าเซ็น/ยอมรับเอกสารออนไลน์" — ตัวประกอบเดียว (pure · server-side)** (รอบ 240 · คำตัดสิน 240-4/240-6)
///
/// <para>ภาษา + ชื่อเอกสารมาจาก <c>PdfGenerationService.ResolveDocumentHeadingAsync</c> (ตัวเดียวกับ PDF — S-12) · ทุกค่าที่ผู้ใช้/ลูกค้าคุมได้ถูก
/// HtmlEncode (กฎ #4 C) · ส่งผ่านแพลตฟอร์ม (<see cref="OnBehalfEmailSender.Plan.OnBehalf"/>) ⇒ <b>บรรทัดเปิดเผยตัวตนหัวอีเมล</b> ("ส่งในนาม {บริษัท}
/// (เลขผู้เสียภาษี …) ผ่านระบบ NextAcc") + ท้ายอีเมลบอกเหตุที่ได้รับ · NextAcc เป็นผู้ให้บริการระบบ · ติดต่อบริษัทโดยตรง · ลิงก์แจ้งการใช้ในทางที่ผิด ·
/// <b>ไม่มีเนื้อหาโฆษณา</b> (อีเมลธุรกรรมของเอกสารฉบับเดียวถึงคู่สัญญาของเอกสารนั้น — ไม่ใช่สแปมตาม พ.ร.บ.คอมพิวเตอร์ ม.11)</para>
/// </summary>
public static class SignRequestEmailText
{
    public const int MessageMax = 1000;

    public sealed record Input(
        SignRequestKinds.Kind Kind,
        bool English,
        string DocumentTitle,
        string? DocumentNumber,
        DateTime DocumentDate,
        decimal TotalAmount,
        string? Currency,
        string? CustomerName,
        string? CompanyName,
        string? CompanyTaxId,
        string? CompanyReplyTo,
        string Url,
        DateTime? ExpiresAtUtc,
        string? Message,
        bool Reminder,
        bool OnBehalf,
        string? AbuseReportUrl);

    public sealed record Output(string Subject, string Html, string Text);

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>บรรทัดเปิดเผยตัวตนหัวอีเมลที่ส่งผ่านแพลตฟอร์มในนามบริษัท — ตัวเดียว (อีเมลขอเซ็น + อีเมลเอกสาร <c>DocumentEmailService</c>)</summary>
    private static string OnBehalfBanner(bool english, string? companyName, string? companyTaxId)
    {
        var company = OnBehalfEmailSender.SafeDisplayName(companyName);
        var taxId = string.IsNullOrWhiteSpace(companyTaxId) ? null : companyTaxId.Trim();
        return "<p style='background:#f1f5f9;border-radius:8px;padding:10px 12px;font-size:13px;color:#334155;margin:0 0 16px'>"
            + (english
                ? $"This e-mail is sent on behalf of <strong>{E(company)}</strong>{(taxId != null ? $" (Tax ID {E(taxId)})" : "")} via the NextAcc system. NextAcc is only the system provider — please contact {E(company)} directly with any questions."
                : $"อีเมลนี้ส่งในนาม <strong>{E(company)}</strong>{(taxId != null ? $" (เลขประจำตัวผู้เสียภาษี {E(taxId)})" : "")} ผ่านระบบ NextAcc · NextAcc เป็นผู้ให้บริการระบบเท่านั้น หากมีข้อสงสัยโปรดติดต่อ {E(company)} โดยตรง")
            + "</p>";
    }

    public static Output Compose(Input i)
    {
        var en = i.English;
        var company = OnBehalfEmailSender.SafeDisplayName(i.CompanyName);
        var title = string.IsNullOrWhiteSpace(i.DocumentTitle) ? (en ? "Document" : "เอกสาร") : i.DocumentTitle.Trim();
        var no = i.DocumentNumber ?? "";
        var action = en ? i.Kind.ActionEn : i.Kind.ActionTh;
        var subject = en
            ? $"{(i.Reminder ? "Reminder: " : "")}Please {action} — {title} No. {no} from {company}"
            : $"{(i.Reminder ? "เตือน: " : "")}ขอให้{action} — {title} เลขที่ {no} จาก {company}";
        subject = subject.Replace('\r', ' ').Replace('\n', ' ');

        var money = (i.TotalAmount).ToString("N2", CultureInfo.InvariantCulture);
        var cur = string.IsNullOrWhiteSpace(i.Currency) || i.Currency == "THB" ? (en ? "THB" : "บาท") : i.Currency!.Trim();
        var amount = en ? $"{cur} {money}" : $"{money} {cur}";
        var date = i.DocumentDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var expires = i.ExpiresAtUtc is { } x ? QuotationAcceptance.BangkokLocal(x).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : null;
        var greeting = en ? $"Dear {E(i.CustomerName ?? "Customer")}," : $"เรียน คุณ{E(i.CustomerName ?? "ลูกค้า")}";
        var msg = QuotationAcceptance.CleanText(i.Message, MessageMax);
        var taxId = string.IsNullOrWhiteSpace(i.CompanyTaxId) ? null : i.CompanyTaxId.Trim();

        var disclosure = !i.OnBehalf ? "" : OnBehalfBanner(en, i.CompanyName, taxId);

        var intro = en
            ? $"{E(company)} has sent you {E(title)} No. <strong>{E(no)}</strong> dated {date} for <strong>{E(amount)}</strong> and asks you to {E(action)} online."
            : $"{E(company)} ส่ง{E(title)} เลขที่ <strong>{E(no)}</strong> ลงวันที่ {date} มูลค่า <strong>{E(amount)}</strong> ถึงท่าน และขอให้ท่าน{E(action)}ออนไลน์";
        var note = msg == null ? ""
            : $"<blockquote style='border-left:3px solid #c7d2fe;margin:12px 0;padding:6px 12px;color:#334155;white-space:pre-wrap'>{E(msg)}</blockquote>";
        var button = $"<p style='text-align:center;margin:24px 0'><a href='{E(i.Url)}' style='background:#4F46E5;color:#fff;padding:12px 28px;border-radius:8px;text-decoration:none;font-weight:600'>{E(en ? i.Kind.ButtonEn : i.Kind.ButtonTh)}</a></p>";
        var linkLine = en
            ? $"<p style='font-size:12px;color:#64748b'>If the button does not work, copy this link into your browser:<br>{E(i.Url)}</p>"
            : $"<p style='font-size:12px;color:#64748b'>หากกดปุ่มไม่ได้ คัดลอกลิงก์นี้ไปเปิดในเบราว์เซอร์:<br>{E(i.Url)}</p>";
        var expiry = expires == null ? ""
            : en ? $"<p style='font-size:13px;color:#64748b'>This link expires on {expires} (Bangkok time). You can view and download the PDF before deciding.</p>"
                 : $"<p style='font-size:13px;color:#64748b'>ลิงก์นี้ใช้ได้ถึง {expires} (เวลาประเทศไทย) · เปิดดูและดาวน์โหลด PDF ได้ก่อนตัดสินใจ</p>";

        var html =
            "<div style='font-family:sans-serif;max-width:600px;margin:0 auto;color:#1e293b'>"
            + disclosure
            + $"<h2 style='color:#4F46E5;border-bottom:2px solid #4F46E5;padding-bottom:8px'>{E(title)} {E(no)}</h2>"
            + $"<p>{greeting}</p><p>{intro}</p>{note}{button}{linkLine}{expiry}"
            + Footer(en, i.CompanyName, title, no, i.CompanyReplyTo, i.OnBehalf, i.AbuseReportUrl)
            + "</div>";

        var text = (i.OnBehalf
                ? (en ? $"Sent on behalf of {company}{(taxId != null ? $" (Tax ID {taxId})" : "")} via NextAcc.\n\n" : $"ส่งในนาม {company}{(taxId != null ? $" (เลขประจำตัวผู้เสียภาษี {taxId})" : "")} ผ่านระบบ NextAcc\n\n")
                : "")
            + (en ? $"{title} No. {no} — please {action}: {i.Url}" : $"{title} เลขที่ {no} — ขอให้{action}: {i.Url}")
            + (msg == null ? "" : "\n\n" + msg);
        return new Output(subject, html, text);
    }

    /// <summary>ท้ายอีเมลธุรกรรม — ตัวเดียว (อีเมลขอเซ็น + อีเมลเอกสารที่ส่งผ่านแพลตฟอร์ม): เหตุที่ได้รับ · ไม่ใช่โฆษณา · ติดต่อบริษัท/บอกไม่รับ ·
    /// โหมดแพลตฟอร์มเพิ่ม "NextAcc เป็นผู้ให้บริการ" + ลิงก์แจ้งใช้ผิด</summary>
    private static string Footer(bool en, string? companyName, string title, string no, string? replyTo, bool onBehalf, string? abuseUrl)
    {
        var company = OnBehalfEmailSender.SafeDisplayName(companyName);
        var contact = string.IsNullOrWhiteSpace(replyTo) ? (en ? "by replying to this e-mail" : "โดยตอบกลับอีเมลนี้") : (en ? $"at {E(replyTo)}" : $"ที่ {E(replyTo)}");
        var footer = en
            ? $"You received this e-mail because {E(company)} issued {E(title)} No. {E(no)} to you as the counterparty of this document. This is a transactional message about this document only — not advertising. For questions, or if you do not wish to receive such e-mails, please contact {E(company)} directly {contact}."
            : $"ท่านได้รับอีเมลนี้เพราะ {E(company)} ออก{E(title)} เลขที่ {E(no)} ถึงท่านในฐานะคู่ค้าของเอกสารฉบับนี้ อีเมลนี้เป็นการแจ้งธุรกรรมของเอกสารฉบับนี้เท่านั้น ไม่ใช่โฆษณา · หากมีข้อสงสัย หรือไม่ประสงค์รับอีเมลลักษณะนี้ โปรดติดต่อ {E(company)} โดยตรง{contact}";
        var provider = !onBehalf ? ""
            : en
                ? $"<br>NextAcc is only the system provider and is not a party to this document.{(string.IsNullOrWhiteSpace(abuseUrl) ? "" : $" If you believe this e-mail is unrelated to you or misused, please report it at <a href='{E(abuseUrl)}'>{E(abuseUrl)}</a>.")}"
                : $"<br>NextAcc เป็นเพียงผู้ให้บริการระบบ ไม่ได้เป็นคู่สัญญาของเอกสารนี้{(string.IsNullOrWhiteSpace(abuseUrl) ? "" : $" · หากเห็นว่าอีเมลนี้ไม่เกี่ยวข้องกับท่านหรือถูกใช้ในทางที่ผิด แจ้งได้ที่ <a href='{E(abuseUrl)}'>{E(abuseUrl)}</a>")}";
        return $"<hr style='border:none;border-top:1px solid #e2e8f0;margin:24px 0'><p style='font-size:12px;color:#64748b'>{footer}{provider}</p>";
    }

    public const int DocumentMessageMax = 5000;

    /// <summary>ข้อความที่ผู้ใช้พิมพ์ = ข้อความล้วนเสมอ (ไม่รับ HTML) — หนีทุกตัว · ขึ้นบรรทัดเป็น &lt;br&gt; · ตัดอักขระควบคุม/ความยาว (คำตัดสิน 240-9)</summary>
    public static string PlainTextToHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (t.Length > DocumentMessageMax) t = t[..DocumentMessageMax];
        var lines = t.Split('\n').Select(l => E(QuotationAcceptance.CleanText(l, DocumentMessageMax) ?? ""));
        return "<div style='color:#1e293b;line-height:1.6'>" + string.Join("<br>", lines) + "</div>";
    }

    /// <summary>คำตัดสิน 240-9 (ฝ่ายค้าน B1): อีเมลเอกสารที่ส่ง<b>ผ่านแพลตฟอร์ม</b> = เทมเพลตคงที่ — ส่วนหัวเปิดเผยตัวตน + เนื้อ (ค่าตั้งต้นของเซิร์ฟเวอร์ที่หนีแล้ว
    /// หรือข้อความล้วนของผู้ใช้ผ่าน <see cref="PlainTextToHtml"/>) + ท้ายอีเมลแบบเดียวกับลิงก์เซ็น ⇒ ไม่มีทางใช้ SMTP ของ NextAcc ส่ง HTML อะไรก็ได้ (open relay)</summary>
    public static string WrapPlatformDocumentEmail(bool english, string? companyName, string? companyTaxId, string? replyTo,
        string documentTitle, string? documentNumber, string innerHtml, string? abuseUrl)
        => "<div style='font-family:sans-serif;max-width:600px;margin:0 auto;color:#1e293b'>"
           + OnBehalfBanner(english, companyName, companyTaxId)
           + innerHtml
           + Footer(english, companyName, documentTitle, documentNumber ?? "", replyTo, onBehalf: true, abuseUrl)
           + "</div>";

    /// <summary>หัวเรื่องอีเมล = บรรทัดเดียว (ตัด CR/LF · ความยาว ≤ 200)</summary>
    public static string SafeSubject(string? subject)
    {
        var s = (subject ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 200 ? s : s[..200];
    }
}

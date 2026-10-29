using System.Net;
using System.Text.Json;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ช่องมาตรฐานที่ดึงจากคำตอบฟอร์ม (ที่เหลือเป็นช่องเพิ่มเติม)</summary>
public sealed record CmsFormFields(string? Name, string? Email, string? Phone, string? Company, string? TaxId, string? Message,
    Dictionary<string, object?> Extras);

/// <summary>
/// **ฟอร์มกำหนดเองของเว็บ (<c>SiteForm</c>) → เส้นมาตรฐาน lead + อีเมลแจ้ง** — รอบ 209 P6 · R-209-D7
///
/// <para><b>ที่มา</b>: ฟอร์มที่ติ๊ก "สร้างเอกสาร ERP" เขียน <c>_db.Documents.Add</c> ตรง — ข้าม <c>IDocumentService</c> (ไม่มีเลขรัน/ไม่มีผู้ติดต่อ/
/// ไม่ผ่านด่านใด ๆ) · <c>NotifyEmails</c>/<c>SendAutoReply</c> มีช่องบนจอแต่ไม่มีผู้อ่าน (F2 #2)</para>
///
/// <para><b>กติกา</b>: ติ๊ก "ส่งเข้าระบบ" ⇒ สร้าง <c>CmsLead</c> (ชนิดตามฟอร์ม) แล้วพนักงานแปลงเป็นใบเสนอราคาที่หน้า lead
/// (ตัวหาผู้ติดต่อเดียว + <c>IDocumentService</c>) · อีเมลแจ้งร้าน/ตอบกลับ ทุกค่าที่ผู้กรอกคุมได้ผ่าน HtmlEncode (CLAUDE.md #4 C) ·
/// ส่งล้ม/ไม่ได้ตั้ง SMTP ⇒ ประทับผลบนแถวคำตอบ (ไม่ล้มการส่งฟอร์ม · ไม่เงียบ)</para>
/// </summary>
public static class CmsFormLeadMapper
{
    /// <summary>อีเมลผู้กรอกสำหรับตอบกลับอัตโนมัติ — <b>ที่อยู่เดียว</b>เท่านั้น (ฝ่ายค้านรอบ 209 security ข้อ 4: "a@x,b@y" ⇒ MailAddressCollection รับหลายปลายทาง
    /// ⇒ ใช้ร้านส่งสแปม) · มี , ; ช่องว่าง หรือ parse ไม่ผ่าน ⇒ null</summary>
    public static string? SingleReplyAddress(string? raw)
    {
        var e = raw?.Trim();
        if (string.IsNullOrEmpty(e) || e.IndexOfAny(new[] { ',', ';', ' ', '\t', '\r', '\n', '<', '>' }) >= 0) return null;
        try
        {
            var a = new System.Net.Mail.MailAddress(e);
            return a.Address == e && e.Contains('@') ? e.ToLowerInvariant() : null;
        }
        catch (FormatException) { return null; }
    }

    public const string RuleCode = "CMS-FORM-LEAD";

    private static readonly string[] NameKeys = { "name", "fullname", "full_name", "ชื่อ", "ชื่อ-นามสกุล" };
    private static readonly string[] EmailKeys = { "email", "e-mail", "อีเมล" };
    private static readonly string[] PhoneKeys = { "phone", "tel", "mobile", "เบอร์โทร", "โทรศัพท์" };
    private static readonly string[] CompanyKeys = { "company", "companyname", "บริษัท" };
    private static readonly string[] TaxKeys = { "taxid", "tax_id", "เลขผู้เสียภาษี" };
    private static readonly string[] MessageKeys = { "message", "detail", "details", "ข้อความ", "รายละเอียด" };

    /// <summary>แยกคำตอบ JSON เป็นช่องมาตรฐาน + ช่องเพิ่มเติม (คีย์ไม่สนตัวพิมพ์) · JSON เสีย/ไม่ใช่ object ⇒ ว่างทั้งหมด</summary>
    public static CmsFormFields Map(string? dataJson)
    {
        var all = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(dataJson) ? "{}" : dataJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    all[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
        }
        catch (JsonException) { /* คำตอบเสีย — ไม่มีช่องมาตรฐาน (ข้อมูลดิบยังอยู่บนแถวคำตอบ) */ }

        string? Take(string[] keys)
        {
            foreach (var k in keys)
                if (all.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v!.Trim();
            return null;
        }
        var used = NameKeys.Concat(EmailKeys).Concat(PhoneKeys).Concat(CompanyKeys).Concat(TaxKeys).Concat(MessageKeys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extras = all.Where(kv => !used.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        return new CmsFormFields(Take(NameKeys), Take(EmailKeys), Take(PhoneKeys), Take(CompanyKeys), Take(TaxKeys), Take(MessageKeys), extras);
    }

    /// <summary>ชนิด lead ตามฟอร์ม — RFQ ⇒ Rfq · ตั้งเอกสารปลายทางเป็นใบเสนอราคา ⇒ Quote · อื่น ๆ ⇒ Contact</summary>
    public static LeadType LeadTypeFor(SiteFormType formType, DocumentType? erpDocumentType)
        => formType == SiteFormType.Rfq ? LeadType.Rfq
            : erpDocumentType == DocumentType.Quotation ? LeadType.Quote
            : LeadType.Contact;

    /// <summary>ผู้รับแจ้งเตือนร้าน — คั่นด้วย , ; หรือขึ้นบรรทัด · เฉพาะรูปอีเมลที่ใช้ได้ · ไม่ซ้ำ</summary>
    public static List<string> Recipients(string? notifyEmails)
        => (notifyEmails ?? "").Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(CmsContactResolver.NormalizeEmail).Where(e => e != null).Select(e => e!).Distinct().ToList();

    /// <summary>เนื้ออีเมลแจ้งร้าน — ทุกค่าผ่าน HtmlEncode</summary>
    public static string NotifyHtml(string formName, string siteName, CmsFormFields f)
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? "-");
        var rows = new List<(string, string?)> { ("ชื่อ", f.Name), ("อีเมล", f.Email), ("โทร", f.Phone), ("บริษัท", f.Company), ("เลขผู้เสียภาษี", f.TaxId), ("ข้อความ", f.Message) };
        rows.AddRange(f.Extras.Select(kv => (kv.Key, kv.Value?.ToString())));
        var tr = string.Join("", rows.Where(r => !string.IsNullOrWhiteSpace(r.Item2)).Select(r => $"<tr><td><b>{E(r.Item1)}</b></td><td>{E(r.Item2)}</td></tr>"));
        return $"<p>มีคำตอบใหม่จากฟอร์ม <b>{E(formName)}</b> บนเว็บ {E(siteName)}</p><table cellpadding=\"6\" border=\"1\" style=\"border-collapse:collapse\">{tr}</table>";
    }

    /// <summary>เนื้ออีเมลตอบกลับอัตโนมัติ — ข้อความร้านเป็นข้อความล้วน (encode แล้วแปลงขึ้นบรรทัดเป็น &lt;br&gt;)</summary>
    public static string AutoReplyHtml(string? body, string siteName)
        => "<p>" + WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(body) ? $"ขอบคุณที่ติดต่อ {siteName} — เราได้รับข้อความของคุณแล้ว และจะติดต่อกลับโดยเร็ว" : body!.Trim())
               .Replace("\n", "<br>") + "</p>";
}

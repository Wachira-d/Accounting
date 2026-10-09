using System.Net;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **ข้อความของ e-Tax XML ที่ "อ่านเป็นคำบนกระดาษได้"** — ตัวเดียวของไปป์ไลน์ OCR (pure)
///
/// <para>═══ ที่มา (สแกนจริง f1690d11 · 2026-10-09 · ซีอาร์ซี ไทวัสดุ SRCIE26100075384) ═══
/// เส้น Tier 0 (e-Tax XML) ใช้ <b>XML ดิบทั้งก้อน</b>เป็น <c>RawTextContent</c> ⇒ ตัวตัดสินที่ค้น "คำ" บนหน้า
/// (ตัวจับใบมัดจำ · ตัวจัดหมวดรายจ่าย) เห็น<b>ชื่อแท็ก/ชื่อช่องของแม่แบบ</b>เป็นคำบนกระดาษ:
/// <c>&lt;ram:Subject&gt;DepositAllowanceChargeInd1&lt;/ram:Subject&gt;</c> = คำว่า "DEPOSIT" + เลข 1 บนบรรทัดเดียวกัน
/// ⇒ <c>[DEPOSIT-BUY]</c> บนใบซื้อของธรรมดา · <c>SpecifiedLineTradeDelivery</c> = คำว่า "delivery"
/// ⇒ หมวด "ค่าขนส่ง" ได้คะแนนจากชื่อแท็กที่ขึ้น<b>ทุกบรรทัดของทุกใบ e-Tax</b></para>
///
/// <para>กติกา: ตัดชื่อแท็กทิ้ง · ตัดลายมือชื่อดิจิทัล (<c>ds:Signature</c>) · ตัด <c>ram:Subject</c> (ชื่อช่องของแม่แบบ ไม่ใช่ข้อความที่ผู้ขายพิมพ์) ·
/// ตัดค่าว่างของแม่แบบ ("-") · คงเนื้อหาข้อความทุกช่องตามลำดับเดิม หนึ่งค่าต่อหนึ่งบรรทัด</para>
/// </summary>
public static class OcrEtaxXmlText
{
    private static readonly Regex SubjectElement = new(
        @"<(?:\w+:)?Subject>[^<]*</(?:\w+:)?Subject>", RegexOptions.Compiled);

    /// <summary>ลายมือชื่อดิจิทัล (XAdES) — base64 + ชื่อ CA ("Internet Thailand …") ไม่ใช่ข้อความของผู้ขาย
    /// (สแกนจริง f1690d11: คำว่า "internet" ของ CA ให้คะแนนหมวด "ค่าโทรศัพท์/อินเทอร์เน็ต")</summary>
    private static readonly Regex SignatureElement = new(
        @"<(?:\w+:)?Signature\b[\s\S]*?</(?:\w+:)?Signature>", RegexOptions.Compiled);

    private static readonly Regex AnyTag = new(@"<[^>]*>", RegexOptions.Compiled);

    /// <summary>ข้อความนี้คือ e-Tax XML (ETDA Cross Industry Invoice) ไม่ใช่ข้อความที่อ่านจากภาพ</summary>
    public static bool IsEtaxXml(string? text)
        => !string.IsNullOrEmpty(text)
           && text.Contains("CrossIndustryInvoice", StringComparison.Ordinal)
           && text.Contains("<ram:", StringComparison.Ordinal);

    /// <summary>เนื้อหาข้อความของทุกช่อง (ไม่มีชื่อแท็ก/ชื่อช่องของแม่แบบ) หนึ่งค่าต่อบรรทัด —
    /// ข้อความที่ไม่ใช่ e-Tax XML คืนตามเดิม (ผู้เรียกไม่ต้องแยกเส้นเอง)</summary>
    public static string ContentOnly(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (!IsEtaxXml(text)) return text;
        var noSignature = SignatureElement.Replace(text, "\n");
        var noSubject = SubjectElement.Replace(noSignature, "\n");
        var noTags = AnyTag.Replace(noSubject, "\n");
        var lines = noTags.Split('\n')
            .Select(l => WebUtility.HtmlDecode(l).Trim())
            .Where(l => l.Length > 0 && l != "-");
        return string.Join("\n", lines);
    }
}

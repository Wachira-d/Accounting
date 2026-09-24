using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// "เนื้อหาที่ลูกค้าเซ็น" ของเอกสาร — ตัวตั้ง canonical ตัวเดียวของทั้งฝั่งเขียน (ตอนบันทึกลายเซ็น) และฝั่งตรวจ
/// (ตอนจะใช้ลายเซ็นเดิมซ้ำ) · รอบ 193 ฝ่ายค้านรอบสาม R3-3 (คำตัดสิน main agent)
/// <para>ที่มา: ลูกค้าเซ็นใบเสนอราคา → อนุมัติไม่ผ่านด่านบัญชี (422) → ใบยังเป็นร่าง ผู้ใช้แก้ราคา → เรียกซ้ำ ⇒ เดิมระบบ
/// "ใช้ลายเซ็นเดิม" ⇒ ใบถูกอนุมัติด้วยลายเซ็นที่ลูกค้าให้กับ<b>ราคาเดิม</b> และลายเซ็น/ชื่อในคำขอใหม่ถูกทิ้งเงียบ</para>
/// <para>กติกา: ลายเซ็นเดิมใช้ซ้ำได้เฉพาะเมื่อ (1) hash เนื้อหาตอนเซ็นเท่ากับตอนนี้ และ (2) คำขอเป็นการเรียกซ้ำของผู้เซ็นคนเดิม
/// ด้วยลายเซ็นเดิม — อย่างอื่นทั้งหมดต้องบันทึกลายเซ็นใหม่ (ไม่ทิ้งเงียบ) · แถวเก่าที่ไม่มี hash (ก่อนรอบนี้) = "ไม่รู้ว่าเซ็นเนื้อหาอะไร"
/// ⇒ ใช้ซ้ำไม่ได้ (ทิศปลอดภัย: ขอลายเซ็นใหม่หนึ่งครั้ง ดีกว่าอนุมัติราคาที่ลูกค้าไม่เคยเห็น)</para>
/// <para>ครอบ: ผู้ซื้อ · สกุลเงิน/อัตรา · ราคารวม VAT ไหม · ส่วนลดท้ายบิล · ยอดหัวเอกสารทุกช่อง · เงื่อนไข (ครบกำหนด · เครดิต ·
/// ข้อความเงื่อนไขชำระ · หมายเหตุ) · ทุกบรรทัด (สินค้า · คำอธิบาย · จำนวน · หน่วย · ราคา · ส่วนลด · ยอด · VAT · WHT) ·
/// <b>ไม่ครอบ</b> ของภายในที่ลูกค้าไม่เห็น (ผังบัญชี · โปรเจกต์ · feedback id) — จัดผังใหม่ไม่ต้องขอลายเซ็นใหม่</para>
/// <para>รอบสี่ R4-2 (<c>v2</c>): เพิ่มทุกช่องที่พิมพ์บนใบและลูกค้าเห็น — วันที่เอกสาร · วันส่งมอบ · เงื่อนไขพิเศษ/ภาคผนวก/ท้ายกระดาษ ·
/// บัญชีรับโอน · ภาษาเอกสาร · แบรนด์/สาขาผู้ออก · เลขอ้างอิง/เลขจอง · อ้างใบมัดจำ + ยอดหัก · วิธีชำระ (ไล่จากทุกช่องที่ renderer HTML/QuestPDF อ่าน)
/// · ลายเซ็นที่บันทึกด้วย <c>v1</c> ถือเป็น "แถวเก่า" (เทียบกับ v2 ไม่ได้ ⇒ ไม่เท่า)</para>
/// <para>รอบสี่ R4-1: <b>ทุกเส้นที่อ่านลายเซ็นลูกค้า</b> (ด่านเซ็นครบใน <c>ApproveDocumentAsync</c> · ขั้นเซ็นครบใน
/// <c>CheckAllApprovedAndProcessAsync</c> · PDF ช่องลูกค้า (ใช้ร่วมทั้งสอง renderer) · API สถานะเซ็น) ตัดสินผ่าน
/// <see cref="IsSignatureCurrent"/> ตัวเดียว</para>
/// </summary>
public static class DocumentSignedContent
{
    /// <summary>เวอร์ชันของสูตร — เปลี่ยนสูตรเมื่อไร hash เก่าไม่เท่าทันที ⇒ ขอเซ็นใหม่ (ทิศปลอดภัย)</summary>
    public const string Version = "v2";

    /// <summary>hash เนื้อหาที่ลูกค้าเซ็น (<c>v2:</c> + SHA-256 hex) — เรียกที่เดียวทั้งตอนบันทึกและตอนตรวจ</summary>
    public static string Hash(Document doc, IEnumerable<DocumentLine> lines)
    {
        var sb = new StringBuilder();
        Field(sb, Version);
        Field(sb, doc.ContactId.ToString("D"));
        Field(sb, (doc.Currency ?? "").Trim().ToUpperInvariant());
        Field(sb, Num(doc.ExchangeRate));
        Field(sb, doc.PricesIncludeVat ? "1" : "0");
        Field(sb, Num(doc.BillDiscountPercent));
        Field(sb, Num(doc.BillDiscountAmount));
        Field(sb, Num(doc.DiscountAmount));
        Field(sb, Num(doc.SubTotal));
        Field(sb, Num(doc.VatAmount));
        Field(sb, Num(doc.WithholdingTaxAmount));
        Field(sb, Num(doc.RoundingAdjustment));
        Field(sb, Num(doc.TotalAmount));
        Field(sb, doc.DueDate.HasValue ? doc.DueDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "");
        Field(sb, doc.CreditDays.HasValue ? doc.CreditDays.Value.ToString(CultureInfo.InvariantCulture) : "");
        Field(sb, Text(doc.PaymentTerms));
        Field(sb, Text(doc.Notes));
        // ── v2 (R4-2): ช่องที่พิมพ์บนใบและลูกค้าเห็น ──
        Field(sb, doc.DocumentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Field(sb, doc.DeliveryDate.HasValue ? doc.DeliveryDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "");
        Field(sb, Text(doc.CustomTermsAndConditions));
        Field(sb, Text(doc.CustomAppendix));
        Field(sb, Text(doc.CustomFooterNotes));
        Field(sb, doc.BankAccountId.HasValue ? doc.BankAccountId.Value.ToString("D") : "");
        Field(sb, Text(doc.DocumentLanguage).ToLowerInvariant());
        Field(sb, doc.BrandId.HasValue ? doc.BrandId.Value.ToString("D") : "");
        Field(sb, Text(doc.IssuerBranchCode));
        Field(sb, Text(doc.Reference));
        Field(sb, Text(doc.BookingNumber));
        Field(sb, Text(doc.DepositAppliedRef));
        Field(sb, Num(doc.DepositAppliedAmount));
        Field(sb, Num(doc.DepositBaseDeducted));
        Field(sb, doc.PaymentType.HasValue ? doc.PaymentType.Value.ToString() : "");

        var live = lines.Where(l => !l.IsDeleted)
            .Select(LineKey)
            .OrderBy(k => k.Order)
            .ThenBy(k => k.Canonical, StringComparer.Ordinal)   // LineOrder ซ้ำกัน ⇒ ลำดับยังคงที่ ไม่ขึ้นกับลำดับที่ DB คืน
            .ToList();
        Field(sb, live.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var k in live) Field(sb, k.Canonical);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Version + ":" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>ใช้ลายเซ็นลูกค้าที่เก็บไว้แล้วซ้ำได้ไหม — ต้องเป็นเนื้อหาเดียวกับตอนเซ็น (hash ตรง · hash ว่าง = ไม่รู้ = ไม่ได้)
    /// <b>และ</b> คำขอเป็นการเรียกซ้ำของผู้เซ็นคนเดิมด้วยลายเซ็นเดิม (ลายเซ็น/ชื่อต่าง = การเซ็นครั้งใหม่ ต้องบันทึก ห้ามทิ้งเงียบ)</summary>
    public static bool CanReuseSignature(
        string? signedHash, string currentHash,
        string? storedSignatureData, string? storedSignerName,
        string? requestSignatureData, string? requestSignerName)
    {
        if (string.IsNullOrWhiteSpace(signedHash) || string.IsNullOrWhiteSpace(currentHash)) return false;
        if (!string.Equals(signedHash, currentHash, StringComparison.Ordinal)) return false;
        if (!string.Equals(storedSignatureData ?? "", requestSignatureData ?? "", StringComparison.Ordinal)) return false;
        return string.Equals(Text(storedSignerName), Text(requestSignerName), StringComparison.Ordinal);
    }

    /// <summary>ข้อความที่ทุกเส้นใช้บอกผู้ใช้เมื่อลายเซ็นลูกค้าไม่นับ (ด่านอนุมัติ · ขั้นเซ็นครบ · API สถานะเซ็น)</summary>
    public const string StaleReason =
        "ต้องให้ลูกค้าเซ็นใหม่เพราะเนื้อหาเอกสารเปลี่ยนหลังลูกค้าเซ็น (หรือเป็นลายเซ็นรุ่นก่อนที่ระบบไม่รู้ว่าลูกค้าเซ็นเนื้อหาใด)";

    /// <summary>แถวอนุมัติเป็น "ลายเซ็นฝั่งลูกค้า/คู่ค้า" ไหม — ชุดเดียวกับที่ PDF ช่องลูกค้าอ่าน (ApprovalType Customer/External) +
    /// ขั้นที่ตั้งบทบาท Customer · ขั้นภายในไม่อยู่ในกติกานี้</summary>
    private static bool IsCustomerSignature(DocumentApproval approval) =>
        approval.ApproverRole == "Customer" || approval.ApprovalType is "Customer" or "External";

    /// <summary>ตัวตัดสินตัวเดียว (R4-1): ลายเซ็นในแถวอนุมัตินี้ยัง "มีผล" กับเนื้อหาเอกสารตอนนี้ไหม
    /// <list type="bullet">
    /// <item>ขั้นภายใน ⇒ ไม่อยู่ในกติกานี้ (true)</item>
    /// <item>เอกสารออกแล้ว (อนุมัติ/ส่ง/ชำระ/ยกเลิก — เนื้อหาถูกล็อก) ⇒ true: ลายเซ็นที่ไม่ตรงเนื้อหาถูก "แทนที่" ไปแล้วตอนอนุมัติ
    /// และแถวเก่าไม่มี hash ของใบที่อนุมัติไปก่อนรอบนี้ต้องยังพิมพ์ลายเซ็นเดิม (§H ห้ามทำให้ใบเก่าที่ถูกเสียลายเซ็น)</item>
    /// <item>ร่าง/รออนุมัติ/ถูกปฏิเสธ ⇒ นับเฉพาะเมื่อ hash ตอนเซ็นเท่ากับเนื้อหาตอนนี้ (ไม่มี hash / hash รุ่นเก่า = ไม่นับ)</item>
    /// </list></summary>
    public static bool IsSignatureCurrent(DocumentApproval approval, Document doc, IEnumerable<DocumentLine> lines)
    {
        if (!IsCustomerSignature(approval)) return true;
        if (DocumentStatusRules.IsIssued(doc.Status)) return true;
        var signed = approval.SignedContentHash;
        return !string.IsNullOrWhiteSpace(signed)
            && string.Equals(signed, Hash(doc, lines), StringComparison.Ordinal);
    }

    /// <summary>ลายเซ็นลูกค้าที่ใช้ต่อไม่ได้ — ไม่ลบจริง: soft-delete (ไม่ขึ้น PDF · ไม่นับเป็นขั้นที่ผ่าน) + หมายเหตุเหตุผลไว้ตามรอย ·
    /// ผู้เรียกต้อง soft-delete <c>DocumentSignature</c> ของแถวนี้ด้วย</summary>
    public static void Supersede(DocumentApproval stale, string reason, DateTime nowUtc)
    {
        stale.IsDeleted = true;
        stale.UpdatedAt = nowUtc;
        var note = $"[ถูกแทนที่ {nowUtc:yyyy-MM-dd HH:mm} UTC] {reason}";
        stale.Comments = string.IsNullOrWhiteSpace(stale.Comments) ? note : stale.Comments + "\n" + note;
    }

    private readonly record struct LineCanon(int Order, string Canonical);

    private static LineCanon LineKey(DocumentLine l)
    {
        var sb = new StringBuilder();
        Field(sb, Text(l.ProductCode));
        Field(sb, Text(l.Description));
        Field(sb, Num(l.Quantity));
        Field(sb, Text(l.Unit));
        Field(sb, Num(l.UnitPrice));
        Field(sb, Num(l.DiscountPercent));
        Field(sb, Num(l.DiscountAmount));
        Field(sb, Num(l.Amount));
        Field(sb, Num(l.VatRate));
        Field(sb, Num(l.VatAmount));
        Field(sb, Num(l.WithholdingTaxRate));
        Field(sb, Num(l.WithholdingTaxAmount));
        return new LineCanon(l.LineOrder, sb.ToString());
    }

    // ความยาวนำหน้าทุกช่อง ⇒ ค่าที่มีตัวคั่นอยู่ข้างในเลื่อนช่องไม่ได้ ("a|b"+"c" ≠ "a"+"b|c")
    private static void Field(StringBuilder sb, string value) =>
        sb.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');

    // 100 กับ 100.00 (scale ต่างกันจาก DB/ฟอร์ม) ต้องได้ค่าเดียวกัน
    private static string Num(decimal d) =>
        decimal.Round(d, 6, MidpointRounding.AwayFromZero).ToString("0.######", CultureInfo.InvariantCulture);

    // ช่องว่างหัวท้าย/ขึ้นบรรทัดแบบ Windows ไม่ใช่การแก้เนื้อหา
    private static string Text(string? s) => (s ?? "").Replace("\r\n", "\n").Trim();
}

using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ข้อความของการลงนามบนหน้าเอกสารสำหรับลูกค้า — ตัวตั้งเดียว** (รอบ 267 แพ็ก P2 · คำตัดสิน 267-1-5 · 267-1-18 · DOCUMENT_FLOW §2.12n-P2)
///
/// <para>ข้อความรับรอง ("ข้าพเจ้าเป็นบุคคลของ {ผู้ซื้อ} และได้รับมอบหมาย…") · ข้อความยินยอม · ประกาศความเป็นส่วนตัว · ป้ายของฟอร์ม ·
/// รายการเหตุผลปฏิเสธ (รายการปิด) — หน้าเว็บไม่มีข้อความของตัวเอง (F2 ข้อ 5) · ผู้ลงนามติ๊กข้อความรุ่น <see cref="Version"/> และระบบเก็บ
/// SHA-256 ของข้อความที่ติ๊กจริงลงแถวหลักฐาน (<see cref="TextHash"/>) ⇒ แก้ข้อความ = ต้องขยับ <see cref="Version"/></para>
///
/// <para>ฐานกฎหมายที่อ้างได้: พ.ร.บ.ว่าด้วยธุรกรรมทางอิเล็กทรอนิกส์ พ.ศ. 2544 มาตรา 9 (ลายมือชื่ออิเล็กทรอนิกส์) และมาตรา 11 (น้ำหนักพยานหลักฐาน)
/// <b>เท่านั้น</b> — ลิงก์ที่ส่งต่อได้ + ข้อมูลที่ผู้ลงนามแจ้งเอง ไม่ถึงระดับลายมือชื่อที่กฎหมายให้ข้อสันนิษฐานพิเศษ จึงห้ามอ้างเกินนี้ทุกที่
/// (ล็อกด้วย <c>CustomerSignRound267Tests.CSR267_LegalBasisOnlySection9And11</c>)</para>
/// </summary>
public static class CustomerSignText
{
    /// <summary>รุ่นของชุดข้อความ — หน้าแสดงรุ่นนี้ · คำตอบที่ส่งรุ่นอื่นถูกปฏิเสธ (ผู้ลงนามต้องติ๊กข้อความปัจจุบัน)</summary>
    public const string Version = "267-1";
    /// <summary>รุ่นของแถวที่มาจากลิงก์ยอมรับ/เซ็นรับก่อนรอบ 267 (พิมพ์ชื่ออย่างเดียว — ไม่มีการติ๊กรับรอง)</summary>
    public const string LegacyVersion = "239-name-only";

    public const string RuleCode = "ETA-9";
    public const string LegalReference = "พ.ร.บ.ว่าด้วยธุรกรรมทางอิเล็กทรอนิกส์ พ.ศ. 2544 ม.9 · ม.11";

    /// <summary>ตำแหน่งที่ใช้เมื่อผู้ซื้อเป็นบุคคลธรรมดาและไม่กรอกตำแหน่ง (267-1-5)</summary>
    public static string IndividualTitle(bool en) => en ? "Buyer (individual)" : "ผู้ซื้อ (บุคคลธรรมดา)";

    private static string T(bool en, string th, string enText) => en ? enText : th;

    private static string Verb(bool en, CustomerSignRule.Kind kind) => kind switch
    {
        CustomerSignRule.Kind.Accepted => T(en, "ยอมรับใบเสนอราคา", "accept this quotation"),
        CustomerSignRule.Kind.Declined => T(en, "แจ้งไม่รับใบเสนอราคา", "decline this quotation"),
        CustomerSignRule.Kind.ChangeRequested => T(en, "ขอแก้ไขใบเสนอราคา", "request changes to this quotation"),
        _ => T(en, "รับสินค้า", "receive the goods"),
    };

    /// <summary>ข้อความที่ผู้ลงนามต้องติ๊ก — นิติบุคคล: ยืนยันทั้ง "สังกัด" และ "อำนาจ" (267-1-5) · บุคคลธรรมดา: ยืนยันว่าเป็นผู้ซื้อเองหรือผู้รับมอบอำนาจ ·
    /// <paramref name="buyerName"/> = ชื่อผู้ซื้อตามเอกสาร (ไม่มี = "ผู้ซื้อตามเอกสารนี้")</summary>
    public static string Attestation(bool en, CustomerSignRule.Kind kind, string? buyerName, bool buyerIsJuristic)
    {
        var who = string.IsNullOrWhiteSpace(buyerName) ? T(en, "ผู้ซื้อตามเอกสารนี้", "the buyer named in this document") : buyerName.Trim();
        var verb = Verb(en, kind);
        return buyerIsJuristic
            ? T(en, $"ข้าพเจ้าเป็นบุคคลของ {who} และมีอำนาจหรือได้รับมอบหมายให้{verb}ตามเอกสารนี้ในนามของ {who}",
                $"I am a person of {who} and am authorised or assigned to {verb} on behalf of {who}")
            : T(en, $"ข้าพเจ้าคือ {who} หรือผู้ได้รับมอบอำนาจจาก {who} ให้{verb}ตามเอกสารนี้",
                $"I am {who}, or a person authorised by {who}, to {verb}");
    }

    /// <summary>SHA-256 ของข้อความที่ผู้ลงนามติ๊ก (เก็บลงแถวหลักฐาน — พิสูจน์ได้ว่าติ๊กข้อความใด แม้ข้อความรุ่นถัดไปจะเปลี่ยน)</summary>
    public static string TextHash(string text) => QuotationAcceptance.Sha256Hex(text)!;

    /// <summary>ข้อความยินยอม/แจ้งผลของการกดปุ่ม — บอกตรง ๆ ว่าเก็บอะไรเป็นหลักฐาน (PDPA ม.23) และอ้างกฎหมายเท่าที่อ้างได้</summary>
    public static string Consent(bool en, CustomerSignRule.Kind kind)
    {
        var verb = Verb(en, kind);
        return T(en,
            $"การกดปุ่มเพื่อ{verb} พร้อมชื่อ นามสกุล ตำแหน่ง และการติ๊กรับรองข้างต้น ถือเป็นการลงลายมือชื่ออิเล็กทรอนิกส์ของท่าน "
            + "(พ.ร.บ.ว่าด้วยธุรกรรมทางอิเล็กทรอนิกส์ พ.ศ. 2544 มาตรา 9) — ระบบบันทึกชื่อ นามสกุล ตำแหน่ง เวลา ที่อยู่ IP ข้อมูลอุปกรณ์ (แบบแฮช) "
            + "ลายนิ้วมือของเนื้อหาเอกสาร และสำเนาเอกสารฉบับที่ท่านเห็น ไว้เป็นพยานหลักฐาน (มาตรา 11) ชื่อ ตำแหน่ง และวันเวลาจะพิมพ์บนเอกสารฉบับนี้",
            $"Pressing the button to {verb}, together with your name, position and the confirmation above, is your electronic signature "
            + "(Electronic Transactions Act B.E. 2544, section 9). The system records your name, position, time, IP address, device fingerprint (hashed), "
            + "a fingerprint of the document content and a copy of the document as you saw it, as evidence (section 11). Your name, position and the time are printed on this document.");
    }

    /// <summary>แจ้งการเก็บข้อมูล (PDPA ม.23) — ใครเก็บ · เพื่ออะไร · นานเท่าไร · สิทธิ</summary>
    public static string PrivacyNotice(bool en, string sellerName)
    {
        var seller = string.IsNullOrWhiteSpace(sellerName) ? T(en, "ผู้ขาย", "the seller") : sellerName.Trim();
        return T(en,
            $"{seller} เป็นผู้ควบคุมข้อมูลส่วนบุคคลของข้อมูลนี้ ใช้เพื่อพิสูจน์การตกลงตามเอกสาร (ฐานสัญญา/ประโยชน์โดยชอบด้วยกฎหมาย) "
            + "เก็บตามอายุเก็บเอกสารบัญชี (อย่างน้อย 5 ปี) ท่านขอเข้าถึง แก้ไข หรือลบข้อมูลได้โดยติดต่อผู้ขาย — การลบทำได้เมื่อพ้นอายุเก็บตามกฎหมาย",
            $"{seller} is the data controller. The data is used to prove the agreement under this document (contract / legitimate interest) "
            + "and kept for the accounting retention period (at least 5 years). Contact the seller to access, correct or erase it — erasure is possible once the legal retention period has passed.");
    }

    /// <summary>เหตุผลปฏิเสธ — รายการปิด (รหัสที่เก็บ = <c>Code</c>) · "other" ต้องมีข้อความ</summary>
    public sealed record Reason(string Code, string Th, string En);

    public static readonly IReadOnlyList<Reason> DeclineReasons = new[]
    {
        new Reason("price", "ราคาสูงเกินงบประมาณ", "Price is over budget"),
        new Reason("scope", "รายการ/ขอบเขตงานไม่ตรงกับที่ต้องการ", "Items or scope do not match the requirement"),
        new Reason("timing", "ระยะเวลาส่งมอบไม่ตรงกับที่ต้องการ", "Delivery time does not fit"),
        new Reason("chose-other", "เลือกผู้ขายรายอื่นแล้ว", "Another supplier was chosen"),
        new Reason("cancelled", "ยกเลิก/เลื่อนโครงการ", "The project was cancelled or postponed"),
        new Reason(OtherReasonCode, "อื่น ๆ (โปรดระบุ)", "Other (please specify)"),
    };

    public const string OtherReasonCode = "other";

    public static string? ReasonLabel(bool en, string? code)
        => DeclineReasons.FirstOrDefault(x => x.Code == code) is { } reason ? (en ? reason.En : reason.Th) : null;

    /// <summary>ป้ายคำตอบของลูกค้า (ประวัติบนหน้า · แจ้งเตือนผู้ขาย)</summary>
    public static string KindLabel(bool en, CustomerSignRule.Kind kind) => kind switch
    {
        CustomerSignRule.Kind.Accepted => T(en, "ยอมรับใบเสนอราคา", "Quotation accepted"),
        CustomerSignRule.Kind.Declined => T(en, "ไม่รับใบเสนอราคา", "Quotation declined"),
        CustomerSignRule.Kind.ChangeRequested => T(en, "ขอแก้ไขใบเสนอราคา", "Changes requested"),
        _ => T(en, "เซ็นรับสินค้า", "Delivery signed for"),
    };

    /// <summary>ข้อความตอบผู้ลงนามเมื่อบันทึกสำเร็จ</summary>
    public static string Done(bool en, CustomerSignRule.Kind kind) => kind switch
    {
        CustomerSignRule.Kind.Accepted => T(en, "ยอมรับใบเสนอราคาเรียบร้อย — ผู้ขายได้รับแจ้งแล้ว และท่านดาวน์โหลดฉบับที่ลงนามได้จากหน้านี้",
            "Quotation accepted — the seller has been notified and you can download the signed copy from this page"),
        CustomerSignRule.Kind.Declined => T(en, "บันทึกการไม่รับใบเสนอราคาแล้ว — ผู้ขายได้รับแจ้งพร้อมเหตุผลของท่าน", "Your decline has been recorded — the seller has been notified with your reason"),
        CustomerSignRule.Kind.ChangeRequested => T(en, "ส่งคำขอแก้ไขให้ผู้ขายแล้ว — ผู้ขายจะส่งใบเสนอราคาฉบับแก้ไขให้ท่าน", "Your change request was sent — the seller will send you a revised quotation"),
        _ => T(en, "เซ็นรับสินค้าเรียบร้อย — ท่านดาวน์โหลดฉบับที่ลงนามได้จากหน้านี้", "Delivery signed for — you can download the signed copy from this page"),
    };

    /// <summary>ป้ายของฟอร์มลงนามบนหน้า (ไทย/อังกฤษตามภาษาเอกสาร)</summary>
    public static IReadOnlyDictionary<string, string> FormLabels(bool en) => new Dictionary<string, string>
    {
        ["heading"] = T(en, "ผู้ลงนาม", "Signer"),
        ["firstName"] = T(en, "ชื่อ", "First name"),
        ["lastName"] = T(en, "นามสกุล", "Last name"),
        ["title"] = T(en, "ตำแหน่ง", "Position"),
        ["email"] = T(en, "อีเมลรับสำเนาฉบับที่ลงนาม (ไม่บังคับ)", "Email for the signed copy (optional)"),
        ["emailRequired"] = T(en, "อีเมล (ใช้รับรหัสยืนยันและสำเนาฉบับที่ลงนาม)", "Email (for the verification code and the signed copy)"),
        ["signature"] = T(en, "ลายเซ็น (วาดในกรอบ)", "Signature (draw in the box)"),
        ["signatureOptional"] = T(en, "ลายเซ็น (ไม่บังคับ — วาดในกรอบ)", "Signature (optional — draw in the box)"),
        ["clear"] = T(en, "ล้างลายเซ็น", "Clear"),
        ["note"] = T(en, "หมายเหตุถึงผู้ขาย (ไม่บังคับ)", "Note to the seller (optional)"),
        ["reason"] = T(en, "เหตุผล", "Reason"),
        ["message"] = T(en, "รายละเอียด", "Details"),
        ["changeMessage"] = T(en, "สิ่งที่ต้องการให้แก้ไข", "What should be changed"),
        ["remember"] = T(en, "จำชื่อ นามสกุล ตำแหน่ง และอีเมลไว้บนเครื่องนี้ (ไม่ส่งไปเก็บที่ระบบเพิ่ม)", "Remember my name, position and email on this device only"),
        ["decline"] = T(en, "ไม่รับใบเสนอราคา", "Decline the quotation"),
        ["requestChange"] = T(en, "ขอแก้ไขใบเสนอราคา", "Request changes"),
        ["otpSend"] = T(en, "ส่งรหัสยืนยันไปที่อีเมล", "Send a verification code to my email"),
        ["otpCode"] = T(en, "รหัสยืนยัน 6 หลัก", "6-digit verification code"),
        ["otpNote"] = T(en, "ผู้ขายกำหนดให้ยืนยันอีเมลด้วยรหัสก่อนยอมรับใบเสนอราคาฉบับนี้", "The seller requires email verification before this quotation can be accepted"),
        ["otpSent"] = T(en, "ส่งรหัสแล้ว — กรุณาตรวจอีเมล (รหัสใช้ได้ 10 นาที)", "Code sent — please check your email (valid for 10 minutes)"),
        ["submit"] = T(en, "ยืนยัน", "Confirm"),
        ["cancel"] = T(en, "ยกเลิก", "Cancel"),
        ["history"] = T(en, "ประวัติการตอบรับของท่าน", "Your responses"),
        ["signedCopy"] = T(en, "ดาวน์โหลดฉบับที่ลงนาม", "Download the signed copy"),
        ["signedCopyPending"] = T(en, "กำลังเตรียมฉบับที่ลงนาม — เปิดหน้านี้อีกครั้งในอีกสักครู่", "Preparing the signed copy — please reopen this page shortly"),
        ["responded"] = T(en, "ผู้ขายได้รับคำตอบของท่านแล้ว — หากต้องการเปลี่ยนคำตอบ กรุณาติดต่อผู้ขายให้ส่งลิงก์ตอบรับใหม่",
            "The seller has received your response — to change it, ask the seller to reopen the document for a response"),
        ["connection"] = T(en, "เชื่อมต่อไม่ได้ — ลองใหม่อีกครั้ง", "Connection failed — please try again"),
    };

    /// <summary>ข้อความของอีเมลรหัสยืนยัน (หัวเรื่อง · เนื้อ HTML — ค่าที่ผู้ใช้คุมได้ถูก encode ที่ผู้เรียก)</summary>
    public static string OtpSubject(bool en, string documentNumber)
        => T(en, $"รหัสยืนยันสำหรับใบเสนอราคา {documentNumber}", $"Verification code for quotation {documentNumber}");

    /// <summary>หัวเรื่องอีเมลส่งสำเนาฉบับที่ลงนาม</summary>
    public static string SignedCopySubject(bool en, string documentTitle, string documentNumber)
        => T(en, $"สำเนา{documentTitle} {documentNumber} ฉบับที่ท่านลงนาม", $"Your signed copy of {documentTitle} {documentNumber}");

    /// <summary>คำเตือนอากรแสตมป์ถึงพนักงานเมื่อลูกค้ายอมรับใบเสนอราคาที่มีงานบริการ (267-1-14 — เตือนเท่านั้น ระบบไม่ตัดสินว่าเป็นตราสาร)</summary>
    public const string StampDutyWarning =
        "ใบเสนอราคาที่ลูกค้ายอมรับแล้วและมีงานบริการ/จ้างทำของ อาจถือเป็นตราสารที่ต้องเสียอากรแสตมป์ (ป.รัษฎากร หมวด 6 · บัญชีอัตราอากรแสตมป์ ลักษณะ 4 จ้างทำของ) "
        + "ภายใน 15 วันนับแต่วันทำตราสาร — ระบบไม่ได้ตัดสินให้ กรุณาตรวจกับผู้ทำบัญชี แล้วบันทึกที่หน้าอากรแสตมป์";
    public const string StampDutyRuleCode = "RD-STAMP-4";
    public const string StampDutyPage = "/pages/stamp-duty.html";

    /// <summary>ชนิดเอกสารที่แนะนำให้พนักงานแปลงต่อหลังลูกค้ายอมรับ (267-1-15 — ระบบไม่มีชนิด "ใบสั่งขาย") · มัดจำ = ใบแจ้งหนี้/ใบกำกับที่ตั้งประเภทมัดจำ</summary>
    public static readonly IReadOnlyList<DocumentType> ConvertSuggestions = new[]
    {
        DocumentType.DeliveryNote, DocumentType.Invoice, DocumentType.TaxInvoice,
    };

    public const string ConvertSuggestionText = "ขั้นต่อไป: เปิดใบเสนอราคาแล้วกด \"แปลงเอกสาร\" เป็น ใบส่งของ · ใบแจ้งหนี้ · ใบกำกับภาษี หรือออกใบแจ้งหนี้มัดจำ (ระบบไม่แปลงให้เองนอกจากที่ตั้ง Autopilot ไว้)";

    // ── RoPA (PDPA ม.39) — แถว "ลงนามเอกสารออนไลน์" ของบริษัท สร้างครั้งแรกที่มีผู้ลงนาม (ไม่ทับที่บริษัทแก้ไว้) ──
    public const string RopaPurpose = "ลงนาม/ตอบรับเอกสารออนไลน์ของคู่ค้า (ยอมรับ · ไม่รับ · ขอแก้ใบเสนอราคา · เซ็นรับสินค้า)";
    public const string RopaLegalBasis = "Contract";
    public const string RopaDataCategories = "ชื่อ นามสกุล ตำแหน่ง ของผู้ลงนามฝั่งคู่ค้า · อีเมล (ถ้าให้) · ลายเซ็นที่วาด (ถ้ามี) · เวลา · ที่อยู่ IP · ลายนิ้วมือของเบราว์เซอร์ (แฮช) · สำเนาเอกสารฉบับที่ลงนาม";
    public const string RopaRetention = "ตามอายุเก็บเอกสารบัญชี: MAX(RetentionUntil ของเอกสาร, วันที่เอกสาร + 5 ปี) — พ.ร.บ.การบัญชี พ.ศ. 2543 ม.14 · ป.รัษฎากร §87/3 · พ้นแล้วลบได้ตามคำขอ";
    public const string RopaRecipients = "พนักงานของบริษัทที่มีสิทธิ์อ่านเอกสาร · ผู้ลงนามเอง (สำเนาฉบับที่ลงนาม)";
    public const string RopaNote = "ฐานการประมวลผล: สัญญา (PDPA ม.24(3)) และประโยชน์โดยชอบด้วยกฎหมายในการพิสูจน์การตกลง (ม.24(5)) · น้ำหนักทางกฎหมายของการลงนาม: "
        + LegalReference + " · ข้อมูลไม่พิมพ์ IP บนเอกสาร";
}

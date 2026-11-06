using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>โหมด "ลูกค้ายอมรับใบเสนอราคาออนไลน์แล้ว ระบบทำอะไรต่อ" (AUTOPILOT §7) — ToDeposit/ToRecurring อยู่ backlog (ไม่แต่งโหมดที่ไม่มีผู้ทำ)</summary>
public enum QuotationAcceptMode
{
    /// <summary>ไม่แปลง (ค่าตั้งต้น — คำตัดสิน §2 #36 Q5)</summary>
    Off,
    /// <summary>แปลงเป็น<b>ร่าง</b>ใบแจ้งหนี้ (ทั้งใบ ผ่าน <c>ConvertDocumentAsync</c> ตัวเดียว)</summary>
    ToInvoice,
    /// <summary>แปลงเป็น<b>ร่าง</b>ใบกำกับภาษี/ใบแจ้งหนี้ (บริษัทต้องจด VAT)</summary>
    ToTaxInvoice,
}

/// <summary>
/// **ตัวตัดสิน "ใบเสนอราคาที่ลูกค้ายอมรับ → แปลงอัตโนมัติ" — ตัวเดียว** (รอบ 252 WP-12A · AUTOPILOT §7 · A-7) · pure
/// <list type="bullet">
/// <item>ค่าที่มีผล = ใบ (ตั้งต่อใบ) › บริษัท › <see cref="QuotationAcceptMode.Off"/> — <see cref="Effective"/></item>
/// <item>ผลแปลง = <b>ร่าง</b> (ไม่อนุมัติ/ไม่ออกเลข/ไม่ส่งเอง · คนอนุมัติตามเส้นเดิม) — สูตรแปลงตัวเดียว <c>IDocumentService.ConvertDocumentAsync</c></item>
/// <item>หยุด (ไม่แปลง + แจ้งทันที E-01c): เนื้อหาเปลี่ยนหลังลูกค้ายอมรับ (<c>QuotationAcceptance.ContentMatches</c> = false) · ใบถูกยกเลิก/ปฏิเสธ ·
/// ใบกำกับภาษีแต่บริษัทไม่จด VAT · ใบมีเอกสารลูกที่ใช้อยู่แล้ว ⇒ <see cref="Outcome.AlreadyConverted"/> (คนแปลงเองแล้ว · ไม่แปลงซ้ำ)</item>
/// </list>
/// </summary>
public static class QuotationAcceptAutopilot
{
    public const string RuleCode = "QT-ACCEPT-AUTOPILOT";

    /// <summary>ผลของการตัดสินหนึ่งใบ (ชื่อเก็บใน <c>Document.QuotationAutopilotOutcome</c>)</summary>
    public enum Outcome
    {
        /// <summary>ไม่เกี่ยว (ไม่ใช่ใบเสนอราคา · ยังไม่ยอมรับ · โหมดปิด) — ไม่ประทับอะไร</summary>
        NotApplicable,
        /// <summary>มีเอกสารลูกแล้ว (คนแปลงเอง) — ประทับว่าตัดสินแล้ว ไม่แปลงซ้ำ</summary>
        AlreadyConverted,
        /// <summary>หยุดพร้อมเหตุ — แจ้งทันที</summary>
        Held,
        /// <summary>แปลงได้</summary>
        Convert,
        /// <summary>(ผู้เรียกประทับ) แปลงสำเร็จ</summary>
        Converted,
        /// <summary>(ผู้เรียกประทับ) แปลงล้ม — ข้อความจากตัวแปลง · แจ้งทันที</summary>
        Failed,
    }

    public sealed record Option(string Code, string Label);

    /// <summary>ตัวเลือกบนหน้า (เซิร์ฟเวอร์ส่ง · หน้าแสดงอย่างเดียว) — "" = ตามค่าบริษัท (เฉพาะระดับใบ)</summary>
    public static readonly IReadOnlyList<Option> Modes = new[]
    {
        new Option(nameof(QuotationAcceptMode.Off), "ไม่แปลง (คนแปลงเอง)"),
        new Option(nameof(QuotationAcceptMode.ToInvoice), "แปลงเป็นร่างใบแจ้งหนี้"),
        new Option(nameof(QuotationAcceptMode.ToTaxInvoice), "แปลงเป็นร่างใบกำกับภาษี/ใบแจ้งหนี้"),
    };

    /// <summary>อ่านค่าที่เก็บ — ว่าง/"Inherit" ⇒ null (ไปชั้นถัดไป) · ชื่อที่ไม่รู้จัก ⇒ null (ไม่เดา)</summary>
    public static QuotationAcceptMode? Parse(string? stored)
    {
        var s = (stored ?? "").Trim();
        if (s.Length == 0 || s.Equals("Inherit", StringComparison.OrdinalIgnoreCase)) return null;
        return Enum.TryParse<QuotationAcceptMode>(s, true, out var m) && Enum.IsDefined(typeof(QuotationAcceptMode), m) ? m : null;
    }

    /// <summary>ค่าที่ผู้ใช้ส่งมาตั้ง — null = ล้าง (ตามชั้นถัดไป) · ปัญหา = ข้อความไทย (ไม่บันทึกค่าที่อ่านไม่ออกเงียบ ๆ)</summary>
    public static (string? Stored, string? Problem) Normalize(string? input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0 || s.Equals("Inherit", StringComparison.OrdinalIgnoreCase)) return (null, null);
        return Parse(s) is { } m ? (m.ToString(), null) : (null, "โหมดแปลงใบเสนอราคาไม่ถูกต้อง — เลือก: ไม่แปลง · แปลงเป็นร่างใบแจ้งหนี้ · แปลงเป็นร่างใบกำกับภาษี");
    }

    /// <summary>ค่าที่มีผล: ใบ › บริษัท › ปิด</summary>
    public static QuotationAcceptMode Effective(string? documentSetting, string? companyDefault)
        => Parse(documentSetting) ?? Parse(companyDefault) ?? QuotationAcceptMode.Off;

    public static DocumentType? Target(QuotationAcceptMode mode) => mode switch
    {
        QuotationAcceptMode.ToInvoice => DocumentType.Invoice,
        QuotationAcceptMode.ToTaxInvoice => DocumentType.TaxInvoice,
        _ => null,
    };

    /// <param name="ContentMatches">ผลของ <c>QuotationAcceptance.ContentMatches</c> — false = เนื้อหาเปลี่ยนหลังยอมรับ · null = ใบก่อนรอบ 239 (ไม่มี hash)</param>
    /// <param name="HasLiveChild">มีเอกสารลูก (RelatedDocumentId = ใบนี้) ที่ไม่ถูกยกเลิก/ลบ</param>
    public sealed record Facts(DocumentType Type, DocumentStatus Status, DateTime? AcceptedAt, bool? ContentMatches, bool HasLiveChild, bool CompanyVatRegistered);

    public sealed record Decision(Outcome Kind, DocumentType? Target, string? Reason);

    public const string ContentChangedReason = "เนื้อหาใบเสนอราคาถูกแก้หลังลูกค้ายอมรับ (ยอด/รายการไม่ตรงฉบับที่ลูกค้ายอมรับ) — ไม่แปลงอัตโนมัติ · ตรวจกับลูกค้าแล้วแปลงเอง หรือส่งลิงก์ให้ยอมรับใหม่";
    public const string NotVatRegisteredReason = "ตั้งให้แปลงเป็นใบกำกับภาษีแต่บริษัทยังไม่จดทะเบียนภาษีมูลค่าเพิ่ม — ออกใบกำกับภาษีไม่ได้ · เปลี่ยนโหมดเป็น \"ร่างใบแจ้งหนี้\" หรือแปลงเอง";

    public static Decision Decide(QuotationAcceptMode mode, Facts f)
    {
        if (f.Type != DocumentType.Quotation || f.AcceptedAt == null || mode == QuotationAcceptMode.Off)
            return new Decision(Outcome.NotApplicable, null, null);
        if (f.HasLiveChild)
            return new Decision(Outcome.AlreadyConverted, null, "ใบเสนอราคานี้มีเอกสารที่แปลงแล้ว (คนแปลงเอง) — ระบบไม่แปลงซ้ำ");
        if (f.Status is DocumentStatus.Voided or DocumentStatus.Rejected)
            return new Decision(Outcome.Held, null, "ใบเสนอราคาถูกยกเลิก/ปฏิเสธหลังลูกค้ายอมรับ — ไม่แปลงอัตโนมัติ");
        if (f.ContentMatches == false)
            return new Decision(Outcome.Held, null, ContentChangedReason);
        var target = Target(mode)!.Value;
        if (target == DocumentType.TaxInvoice && !f.CompanyVatRegistered)
            return new Decision(Outcome.Held, null, NotVatRegisteredReason);
        return new Decision(Outcome.Convert, target, null);
    }
}

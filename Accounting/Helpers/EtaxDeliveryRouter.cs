using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ช่องทางส่งเอกสารทางอีเมลของใบหนึ่งใบ (ผลของ <see cref="EtaxDeliveryRouter.Decide"/>)</summary>
public enum EtaxEmailChannel
{
    /// <summary>อีเมลเอกสารธรรมดา (พฤติกรรมเดิม — ผู้ส่งจาก <c>OnBehalfEmailSender</c>)</summary>
    PlainEmail = 0,
    /// <summary>ต้องส่งแบบ e-Tax by Email (<c>DocumentEmailService.SendEtaxByEmailAsync</c>) — ด่านผู้ส่งปฏิเสธ ⇒ ล้มดัง ห้ามถอยเป็นอีเมลธรรมดา</summary>
    EtaxByEmail = 1,
    /// <summary>ใบนี้ส่งแบบ e-Tax by Email ไปแล้ว — การส่งครั้งต่อไป (ทวง/สำเนา) เป็นอีเมลธรรมดา ไม่ CC ETDA ซ้ำ (คำตัดสิน 244-3)</summary>
    PlainCopyAfterEtax = 2,
}

/// <param name="Channel">ช่องทาง</param>
/// <param name="Reason">เหตุผล (ไทย) — หน้าเว็บแสดงคู่ตัวอย่างอีเมล</param>
public sealed record EtaxEmailRoute(EtaxEmailChannel Channel, string Reason);

/// <summary>
/// **ทางเลือกช่องทางส่งเอกสารทางอีเมล — ตัวตัดสินตัวเดียว** (รอบ 244 · ETAX_BY_EMAIL_AUDIT E5/E6)
///
/// <para>เดิมมีทาง e-Tax by Email ทางเดียว (กดเอง) ส่วนทางอัตโนมัติทุกทาง (หลังรับชำระ · คิวตามตาราง · ตอนอนุมัติ · เอกสารประจำ) ส่งใบกำกับของบริษัทโหมด
/// by Email เป็นอีเมลธรรมดา ⇒ ลูกค้าได้ PDF/A-3 ที่ไม่มีวันถูกประทับเวลา · ตอนนี้ทุกทางส่งเอกสารเรียก <c>DocumentEmailService.SendDocumentRoutedAsync</c>
/// ซึ่งถามตัวนี้ก่อน</para>
/// <para>กติกา: (1) บริษัทไม่ได้เปิด e-Tax หรือโหมดไม่ใช่ by Email/ทั้งสอง ⇒ อีเมลธรรมดา (ทิศตรงข้าม — พฤติกรรมเดิมทุกตัวอักษร) ·
/// (2) ใบนี้ "ไม่ใช่ใบกำกับโดยเจตนา" ตามตัวตัดสินขอบเขต e-Tax เดียวกับ hook ออก e-Tax อัตโนมัติ (<see cref="EtaxAutoIssueScope.Judge"/> — ใบเสนอราคา/ใบแจ้งหนี้ ·
/// ใบกำกับอย่างย่อ · walk-in · ใบเสร็จรับชำระใบกำกับ · มัดจำ VAT พักรอ · ใบลด/เพิ่มหนี้ฝั่งซื้อ · ยังไม่ออก/ยกเลิก) ⇒ อีเมลธรรมดา ·
/// (3) ส่งพร้อมสำเนาถึง ETDA สำเร็จแล้ว ⇒ สำเนาเป็นอีเมลธรรมดา · (4) ที่เหลือ ⇒ e-Tax by Email · G6: pure</para>
/// </summary>
public static class EtaxDeliveryRouter
{
    /// <summary>บริษัทใช้ e-Tax by Email อยู่ไหม — เปิด e-Tax + โหมด by Email (หรือ "ทั้งสอง" ที่เก็บไว้ก่อนรอบ 244 · คำตัดสิน 244-1)</summary>
    public static bool ByEmailActive(bool etaxEnabled, EtaxMode mode) => etaxEnabled && (mode is EtaxMode.ByEmail or EtaxMode.Both);

    public static EtaxEmailRoute Decide(bool etaxEnabled, EtaxMode mode, EtaxAutoSkip scope, bool alreadySentToEtda)
    {
        if (!ByEmailActive(etaxEnabled, mode))
            return new EtaxEmailRoute(EtaxEmailChannel.PlainEmail, "บริษัทไม่ได้ใช้ e-Tax by Email — ส่งเป็นอีเมลเอกสารธรรมดา");
        if (scope != EtaxAutoSkip.None)
            return new EtaxEmailRoute(EtaxEmailChannel.PlainEmail, ScopeReason(scope));
        if (alreadySentToEtda)
            return new EtaxEmailRoute(EtaxEmailChannel.PlainCopyAfterEtax,
                "ใบนี้ส่งแบบ e-Tax by Email พร้อมสำเนาถึง ETDA แล้ว — ครั้งนี้ส่งเป็นอีเมลธรรมดา (ไม่ส่งให้ ETDA ประทับเวลาซ้ำ) · ส่ง e-Tax ซ้ำได้ที่หน้า e-Tax (ต้องยืนยัน)");
        return new EtaxEmailRoute(EtaxEmailChannel.EtaxByEmail,
            "บริษัทใช้ e-Tax by Email และใบนี้เป็นใบกำกับภาษี/ใบเพิ่มหนี้/ใบลดหนี้ — ส่งแบบ e-Tax by Email (สำเนาถึง ETDA เพื่อประทับเวลา) เท่านั้น");
    }

    private static string ScopeReason(EtaxAutoSkip s) => s switch
    {
        EtaxAutoSkip.NotEtaxType => "เอกสารชนิดนี้ไม่ใช่ใบกำกับภาษี — ส่งเป็นอีเมลเอกสารธรรมดา",
        EtaxAutoSkip.NotIssued => "เอกสารยังไม่ได้อนุมัติ",
        EtaxAutoSkip.Voided => "เอกสารถูกยกเลิกแล้ว",
        EtaxAutoSkip.DeferredDepositReceipt => "ใบรับมัดจำที่ VAT ยังพักรอ ยังไม่ใช่ใบกำกับภาษี — ส่งเป็นอีเมลเอกสารธรรมดา",
        EtaxAutoSkip.ReceiptSettlesTaxInvoice => "ใบเสร็จนี้รับชำระใบกำกับภาษีที่ออกไปแล้ว (ใบกำกับตัวจริงคือใบต้นทาง) — ส่งเป็นอีเมลเอกสารธรรมดา",
        EtaxAutoSkip.PurchaseSideAdjustment => "ใบลด/เพิ่มหนี้ฝั่งซื้อ (ผู้ขายเป็นผู้ออก) — ส่งเป็นอีเมลเอกสารธรรมดา",
        EtaxAutoSkip.NotTaxInvoiceByLaw => "เอกสารนี้ออกเป็นเอกสารที่ไม่ใช่ใบกำกับภาษี — ส่งเป็นอีเมลเอกสารธรรมดา",
        EtaxAutoSkip.NotFullTaxInvoice => "ใบนี้ไม่ใช่ใบกำกับภาษีเต็มรูป (อย่างย่อ/ผู้ซื้อไม่ประสงค์รับ/walk-in) — ส่งเป็นอีเมลเอกสารธรรมดา",
        _ => "ส่งเป็นอีเมลเอกสารธรรมดา",
    };
}

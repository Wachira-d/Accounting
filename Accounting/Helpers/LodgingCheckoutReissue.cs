using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ออกใบเช็คเอาต์ใหม่หลังใบเดิมถูกยกเลิก — ตัวตัดสินเดียวว่า "เปิดปุ่มได้ไหม · ออกได้ไหม" (รอบ 201 ทีม IN · A-IN5 · คำตัดสินข้อ 36)
///
/// ═══ ที่มา ═══
/// ใบเช็คเอาต์ (ใบกำกับ/ใบแจ้งหนี้สุดท้ายของการเข้าพัก) ถูกยกเลิกที่หน้าเอกสาร ⇒ มัดจำกลับเป็นยอดคงค้าง แต่การจองยัง "เช็คเอาต์แล้ว"
/// ⇒ โมดูลที่พักบอกได้แค่ "ไปออกใบเองที่หน้าเอกสาร ติ๊กขายเงินสดใบเดียว แล้วเลือกหักมัดจำ…" (ทางไปต่อที่ผู้ใช้ต้องประกอบเองทุกช่อง
/// และพลาดได้ทุกช่อง — โดยเฉพาะฐานมัดจำที่ต้องหักจากฐานภาษี)
///
/// ═══ กติกา (แบบเดียวกับคำตัดสินข้อ 9 · ยกเลิก-ออกใหม่) ═══
/// • ออกได้เฉพาะการจองที่<b>เช็คเอาต์แล้ว</b> · ใบสุดท้าย<b>ถูกยกเลิก</b> · ที่พักออกเอกสาร (ไม่ใช่โหมดปิดบัญชี)
/// • ใบใหม่สร้างจาก<b>ตัวสร้างรายการของที่พักตัวเดียว</b>กับตอนเช็คเอาต์ (ค่าห้อง · บริการเสริม · รายการ folio ที่ออกเอกสารแล้ว ·
///   service charge) + แผนมัดจำจากสถานะมัดจำปัจจุบัน — <b>ยอดรวมต้องเท่าใบเดิม</b> และชนิดเอกสารต้องเท่าเดิม
///   ต่าง = ปฏิเสธพร้อมทางไปต่อ (ออกใบใหม่ที่หน้าเอกสารด้วยยอดเดิม แล้วออกใบลด/เพิ่มหนี้ส่วนต่าง) — ห้ามออกยอดใหม่เงียบ ๆ
/// • ใบใหม่อ้างเลขใบเดิมในหมายเหตุ + audit · ไม่นับมิเตอร์ซ้ำ · ไม่สร้างงานแม่บ้านซ้ำ · ไม่รับเงินซ้ำ (การรับชำระเดิมถูกยกเลิกไปพร้อมใบ)
/// </summary>
public static class LodgingCheckoutReissue
{
    public const string RuleCode = "LODGING-CHECKOUT-REISSUE";

    /// <summary>ชนิดเอกสารขายที่ถือว่า "ใบแทนที่ผู้ใช้ออกเองแล้ว" ได้ (ใบรับชำระ/ใบมัดจำ/ใบลดหนี้ไม่นับ — ผู้เรียกกรองเพิ่ม)</summary>
    public static readonly DocumentType[] SaleTypes = { DocumentType.TaxInvoice, DocumentType.Invoice, DocumentType.Receipt };

    /// <summary>ข้อความยืนยันที่ผู้ใช้ต้องติ๊ก — ระบบไม่เก็บวันที่ยกเลิกเอกสาร ⇒ แยกไม่ได้ว่าใบเดิมถูกยกเลิกสมัยที่ระบบบอกให้ออกใบแทนเอง
    /// ที่หน้าเอกสาร (ก่อนรอบ 201) หรือไม่ (ฝ่ายค้าน X2 · "ไม่รู้" ห้ามตกเป็น "ผ่าน")</summary>
    public const string ConfirmNoManualReissueLabel =
        "ยืนยันว่ายังไม่ได้ออกใบกำกับ/ใบแจ้งหนี้แทนใบที่ยกเลิกเองที่หน้า “เอกสาร” (ถ้าออกไปแล้ว อย่ากดปุ่มนี้ — จะเป็นใบซ้ำ)";

    /// <summary>เปิดปุ่ม “ออกใบเช็คเอาต์ใหม่” ได้ไหม (เซิร์ฟเวอร์ตัดสิน · หน้าเว็บแสดงตามธง) ·
    /// <paramref name="liveReplacementNumber"/> = เลขเอกสารขายที่ยังมีผลซึ่งอ้างเลขจองนี้ (ผู้ใช้ออกใบแทนเองแล้ว) ⇒ ไม่เปิดปุ่ม</summary>
    public static bool CanOffer(LodgingReservationStatus status, DocumentStatus? finalDocumentStatus, bool accountingOff,
        string? liveReplacementNumber = null)
        => status == LodgingReservationStatus.CheckedOut && finalDocumentStatus == DocumentStatus.Voided && !accountingOff
           && liveReplacementNumber == null;

    /// <summary>null = ออกได้ · ข้อความ = ปฏิเสธ (ไทย พร้อมทางไปต่อ)</summary>
    /// <param name="liveReplacementNumber">เอกสารขาย (ไม่ใช่ใบเดิม · ไม่ใช่ใบมัดจำ/ใบรับชำระ) ที่ออกแล้วยังไม่ยกเลิกและอ้างเลขจองนี้ — มี = ผู้ใช้ออกใบแทนเองแล้ว</param>
    /// <param name="confirmedNoManualReissue">ผู้ใช้ติ๊กยืนยันว่ายังไม่ได้ออกใบแทนเอง (ใบที่ออกเองโดยไม่อ้างเลขจองตรวจจากข้อมูลไม่ได้)</param>
    public static string? Problem(LodgingReservationStatus status, DocumentStatus? finalDocumentStatus, bool accountingOff,
        DocumentType originalType, decimal originalTotal, DocumentType rebuiltType, decimal rebuiltTotal, string? originalNumber,
        string? liveReplacementNumber = null, bool confirmedNoManualReissue = true)
    {
        if (accountingOff)
            return "ที่พักนี้ตั้งเป็นโหมดไม่ออกเอกสารบัญชี — ออกใบเช็คเอาต์ใหม่จากโมดูลที่พักไม่ได้";
        if (status != LodgingReservationStatus.CheckedOut)
            return "ออกใบเช็คเอาต์ใหม่ได้เฉพาะการจองที่เช็คเอาต์แล้ว";
        if (finalDocumentStatus != DocumentStatus.Voided)
            return "ใบเช็คเอาต์ของการจองนี้ยังไม่ถูกยกเลิก — ออกใบใหม่ซ้อนไม่ได้ (การขายครั้งเดียวมีใบกำกับได้ใบเดียว) · "
                + "ถ้ายอดผิด ให้ออกใบลด/เพิ่มหนี้อ้างใบเดิมที่หน้าเอกสาร";
        if (liveReplacementNumber != null)
            return $"มีเอกสารขาย {liveReplacementNumber} ที่ยังมีผลและอ้างเลขจองนี้อยู่แล้ว (ออกแทนใบ {originalNumber} ที่หน้าเอกสาร) — ออกใบใหม่อีกใบ = ใบกำกับซ้ำ "
                + "(ภาษีขายสองรอบ) · ถ้าใบนั้นคือใบแทนจริง ไม่ต้องทำอะไรเพิ่ม · ถ้าออกผิด ให้ยกเลิกใบนั้นที่หน้าเอกสารก่อน";
        if (!confirmedNoManualReissue)
            return "ต้องติ๊กยืนยันก่อน: " + ConfirmNoManualReissueLabel;
        if (originalType != rebuiltType)
            return $"ชนิดเอกสารเปลี่ยนจากใบเดิม {originalNumber} (การตั้งค่า VAT ของที่พักเปลี่ยนหลังเช็คเอาต์) — ออกใบใหม่ด้วยชนิดเดิมไม่ได้จากโมดูลที่พัก · "
                + "ให้นักบัญชีออกเอกสารที่หน้า “เอกสาร” ด้วยชนิดและยอดเดิม";
        if (Round(originalTotal) != Round(rebuiltTotal))
            return $"ยอดที่สร้างใหม่ {Round(rebuiltTotal):N2} ไม่เท่าใบเดิม {originalNumber} ({Round(originalTotal):N2}) — รายการหรือมัดจำของการจองเปลี่ยนหลังเช็คเอาต์ · "
                + "ใบแทนต้องยอดเท่าเดิม: ให้นักบัญชีออกใบใหม่ที่หน้า “เอกสาร” ด้วยยอดเดิม แล้วออกใบลด/เพิ่มหนี้ส่วนต่าง";
        return null;
    }

    /// <summary>หมายเหตุบนใบใหม่ที่อ้างใบเดิม (พิมพ์บนเอกสาร — ผู้ตรวจย้อนรอยได้)</summary>
    public static string ReferenceNote(string? originalNumber)
        => $"ออกแทนใบ {originalNumber} ที่ยกเลิก (ยอดเท่าใบเดิม)";

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

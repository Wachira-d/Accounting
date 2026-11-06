namespace Accounting.Helpers;

/// <summary>
/// รอบ 250 WP-04 ข้อ 7 (AUDIT_249 · C F7 §3): ด่านเลขประจำตัวผู้เสียภาษีของผู้ขาย/ผู้ซื้อก่อนสร้าง XML e-Tax (ETDA ขมธอ.3-2560) — <b>ตัวตัดสินเดียว</b>
/// (ย้ายจาก <c>EtaxInvoiceService</c> เพื่อให้ทดสอบสองทิศได้แบบ pure · ข้อความเดิมทุกตัวอักษร)
/// <para>ทำไมต้องบล็อก: ตัวประกอบ TXID ของ XML (<c>ComposeTxId</c>) <b>เติมศูนย์</b>ให้ครบ 13 หลัก ⇒ ถ้าปล่อยเลขสั้นผ่าน
/// จะยื่นเลขผู้เสียภาษีปลอมถึงกรมสรรพากรซึ่งแก้ย้อนหลังไม่ได้ (§86/4 · F2 ข้อ 3 "ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ")</para>
/// </summary>
public static class EtaxPartyTaxIdGuard
{
    /// <summary>ผู้ขาย (บริษัท) — ต้องเป็นเลข 13 หลักหลังตัดขีด/ช่องว่าง · null = ผ่าน</summary>
    public static string? Seller(string? taxId)
    {
        if (string.IsNullOrWhiteSpace(taxId))
            return "กรุณาตั้งค่าเลขประจำตัวผู้เสียภาษีของบริษัทก่อน";
        var clean = ThaiTaxId.Normalize(taxId);
        return clean.Length != 13
            ? $"เลขประจำตัวผู้เสียภาษีของบริษัทต้องเป็นตัวเลข 13 หลัก (ปัจจุบัน: \"{taxId}\" → {clean.Length} หลัก)"
            : null;
    }

    /// <summary>ผู้ซื้อ (รอบ 250 WP-04 ต่อยอด · ฝ่ายค้าน) — null = ผ่าน:
    /// <list type="bullet">
    /// <item>บุคคลธรรมดา: ไม่มีเลข = ผ่าน (ผู้ซื้อที่ไม่จด VAT · ETDA ยอม scheme OTHR "N/A" · §86/4 บังคับเลขเมื่อผู้ซื้อจด VAT — Contact ไม่มีธงจด VAT
    /// จึงใช้ชนิดผู้ติดต่อเป็นข้อเท็จจริงที่มี) · มีเลข = 13 หลัก + checksum (18 หลักไม่ใช่รูปของบุคคล)</item>
    /// <item>นิติบุคคล/หน่วยงานรัฐ/ไม่รู้ชนิด: ต้องมีเลข · 13 หลัก หรือ 18 หลักที่ 5 หลักท้าย = รหัสสาขาของผู้ติดต่อ (normalize) · checksum ของ 13 หลักแรก</item>
    /// </list>
    /// ตัวประกอบ TXID ของ XML ใช้ 13 หลักแรก + สาขาของผู้ติดต่อเสมอ ⇒ PDF (สาขาจากผู้ติดต่อ) กับ XML ตรงกัน</summary>
    public static string? Buyer(string? buyerName, string? taxId, Accounting.Models.Enums.ContactType contactType, string? branchCode)
    {
        var individual = contactType == Accounting.Models.Enums.ContactType.Individual;
        var unknown = contactType == Accounting.Models.Enums.ContactType.Unknown;
        var clean = ThaiTaxId.Normalize(taxId);
        // ฝ่ายค้าน M6: "-" / "N/A" (ไม่มีตัวเลขเลยหลังตัด) = ไม่มีเลข
        if (clean.Length == 0)
            return individual ? null
                : unknown
                    ? $"กรุณาระบุเลขประจำตัวผู้เสียภาษีของผู้ซื้อ \"{buyerName}\" — ถ้าผู้ซื้อเป็นบุคคลธรรมดาที่ไม่จด VAT ให้ตั้งชนิดผู้ติดต่อเป็น \"บุคคลธรรมดา\" (ออก e-Tax โดยไม่มีเลขได้)"
                    : "กรุณาระบุเลขประจำตัวผู้เสียภาษีของผู้ซื้อ";
        if (clean.Length != 13 && (individual || clean.Length != 18))
            return $"เลขประจำตัวผู้เสียภาษีของผู้ซื้อ \"{buyerName}\" ต้องเป็นตัวเลข 13 หลัก "
                   + $"(ปัจจุบัน {clean.Length} หลัก: \"{taxId}\") — แก้ที่ข้อมูลผู้ติดต่อก่อนออก e-Tax";
        if (!ThaiTaxId.HasValidChecksum(clean[..13]))
            return $"เลขประจำตัวผู้เสียภาษีของผู้ซื้อ \"{buyerName}\" ไม่ถูกต้อง (ตรวจหลักสุดท้ายไม่ผ่าน: \"{taxId}\") — "
                   + "แก้ที่ข้อมูลผู้ติดต่อก่อนออก e-Tax (ห้ามส่งเลขที่ไม่มีอยู่จริงถึงกรมสรรพากร)"
                   + (individual || unknown ? " · ถ้าผู้ซื้อเป็นบุคคลธรรมดาที่ไม่จด VAT ให้ลบเลขออก (ออก e-Tax โดยไม่มีเลขได้)" : "");
        if (clean.Length == 18 && clean[13..] != TaxBranchCode.Normalize(branchCode))
            return $"เลขผู้เสียภาษี 18 หลักของผู้ซื้อ \"{buyerName}\" ลงท้ายสาขา {clean[13..]} แต่รหัสสาขาของผู้ติดต่อคือ {TaxBranchCode.Normalize(branchCode)} — "
                   + "ใส่เลข 13 หลักในช่องเลขผู้เสียภาษี และรหัสสาขาในช่องสาขา (PDF กับ XML ต้องเป็นสาขาเดียวกัน)";
        return null;
    }

    public const string NoBuyerTaxIdWarning =
        "ผู้ซื้อไม่มีเลขประจำตัวผู้เสียภาษี (ส่งเป็น OTHR \"N/A\") — ถ้าผู้ซื้อจด VAT ต้องใส่เลขผู้เสียภาษีของผู้ซื้อ (ป.รัษฎากร §86/4) แล้วออกใบแทน";

    /// <summary>ฝ่ายค้าน M5: คำเตือนบนแถว e-Tax เมื่อออกโดยไม่มีเลขผู้ซื้อ (บุคคลธรรมดา) — null = มีเลข · ระบบไม่มีธง "ผู้ซื้อจด VAT" (backlog: Contact.IsVatRegistrant)</summary>
    public static string? BuyerWarning(string? storedBuyerTaxId)
        => ThaiTaxId.Normalize(storedBuyerTaxId).Length == 0 ? NoBuyerTaxIdWarning : null;
}

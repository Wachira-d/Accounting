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

    /// <summary>ผู้ซื้อ — 13 หลัก หรือ 18 หลัก (เลข 13 + สาขา 5 ที่ผู้ใช้ต่อมาเอง) · ว่าง = ต้องระบุ · null = ผ่าน</summary>
    public static string? Buyer(string? buyerName, string? taxId)
    {
        if (string.IsNullOrWhiteSpace(taxId))
            return "กรุณาระบุเลขประจำตัวผู้เสียภาษีของผู้ซื้อ";
        var clean = ThaiTaxId.Normalize(taxId);
        return clean.Length != 13 && clean.Length != 18
            ? $"เลขประจำตัวผู้เสียภาษีของผู้ซื้อ \"{buyerName}\" ต้องเป็นตัวเลข 13 หลัก "
              + $"(ปัจจุบัน {clean.Length} หลัก: \"{taxId}\") — แก้ที่ข้อมูลผู้ติดต่อก่อนออก e-Tax"
            : null;
    }
}

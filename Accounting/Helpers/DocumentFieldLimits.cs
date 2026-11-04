namespace Accounting.Helpers;

/// <summary>
/// รอบ 235 (ผู้ใช้รายงาน 2026-11-04 · 500 <c>22001 value too long for type character varying(100)</c> ตอนสร้างเอกสาร) —
/// <b>ตัวตั้งตัวเดียว</b>ของความยาวคอลัมน์ข้อความอิสระบน <c>Documents</c> ที่รับจากฟอร์ม/API ตรง ๆ ·
/// <c>DatabaseMigrationHelper</c> ขยายคอลัมน์ตามค่านี้ และ <c>DocumentService</c> ตรวจก่อนบันทึกผ่าน <see cref="Require"/> ⇒
/// ผู้ใช้เห็นข้อความไทยระบุช่อง (400) แทน 500 ·
/// ที่มา: เลขอ้างอิงมัดจำต่อกันหลายใบ (<c>"DP-…, DP-…, …"</c>) / หน้าเว็บส่ง id ยาว 36 ตัวเมื่อหาเลขใบไม่เจอ ⇒ เกิน 100 ตัวเร็วมาก
/// </summary>
public static class DocumentFieldLimits
{
    /// <summary>เลขใบกำกับของผู้ขาย — ตัวระบุ ไม่ใช่ข้อความยาว ⇒ คง 100</summary>
    public const int SupplierInvoiceNumber = 100;
    /// <summary>เงื่อนไขการชำระ (ข้อความ) — ขยายจาก 100</summary>
    public const int PaymentTerms = 500;
    /// <summary>เลขอ้างอิงมัดจำที่หัก (คั่นด้วย ", " หลายใบ) — ขยายจาก 100</summary>
    public const int DepositAppliedRef = 1000;

    public const string RuleCode = "DOC-FIELD-TOO-LONG";

    /// <summary>ค่าว่าง/ไม่ส่ง ⇒ ผ่าน · ยาวเกิน ⇒ <see cref="BusinessRuleException"/> (400) ระบุชื่อช่องภาษาไทย</summary>
    public static void Require(string? value, int max, string label)
    {
        if (value != null && value.Length > max)
            throw new BusinessRuleException(
                $"ช่อง \"{label}\" ยาว {value.Length} ตัวอักษร เกินที่ระบบรองรับ ({max} ตัวอักษร) — ย่อข้อความแล้วบันทึกใหม่", RuleCode);
    }
}

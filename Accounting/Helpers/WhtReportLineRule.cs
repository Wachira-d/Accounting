using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ชนิดบรรทัดของรายงาน ภ.ง.ด. ที่ระบบสร้าง — ตัดสินจากคำนำหน้าที่ระบบเขียน (ค่าคงที่เป็นของ <see cref="WhtUnissuedCertGate"/>/
/// <see cref="EWithholdingScope"/>/ไฟล์นี้ ห้ามพิมพ์ซ้ำ)</summary>
public enum WhtReportLineKind
{
    /// <summary>บรรทัดที่นับเข้ายอดตั้งแต่สร้าง (50 ทวิ ที่ออกแล้ว · JE ล้วนของ ภ.ง.ด.3/53)</summary>
    Counted,
    /// <summary>"⚠️ ยังไม่ออกหนังสือรับรอง" — เอกสารหักภาษีแต่ไม่มี 50 ทวิ</summary>
    Unissued,
    /// <summary>"⚠️ หนังสือรับรองยังเป็นร่าง"</summary>
    DraftCert,
    /// <summary>"ℹ️ รายการบัญชี 21915 ไม่มีเอกสาร" (ภ.ง.ด.2)</summary>
    JournalOnly,
    /// <summary>"ℹ️ หักผ่าน e-Withholding" — ธนาคารนำส่งแล้ว</summary>
    EWithholding,
    /// <summary>"⚠️ [เอกสารถูกยกเลิก]" — ประทับตอนยกเลิกเอกสาร</summary>
    VoidedDocument,
}

/// <summary>
/// **กติกาตัวเดียวของ "บรรทัดรายงาน ภ.ง.ด. แก้อะไรได้ / ติ๊กเข้าต้องมีเหตุผลไหม / สร้างใหม่คืนติ๊กให้ไหม"** (รอบ 275 แพ็ก B · T2 H1 · M5)
///
/// <para>ที่มา: (1) H1 — "สร้างใหม่" เก็บ <c>DocumentId</c> ของ<b>ทุก</b>บรรทัดที่นับ แล้วติ๊กเข้าให้ทุกบรรทัดในฉบับใหม่ที่ใช้เอกสารเดียวกัน
/// ⇒ PV จ่ายสองงวด (ใบงวด 1 ออกแล้ว · ใบงวด 2 ร่าง) แถวใบร่างถูกติ๊กเข้าเองโดยไม่มีใครสั่ง ด่าน "ยังไม่ออก 50 ทวิ" ไม่บล็อก และไฟล์ยื่นมีใบร่าง ·
/// (2) M5 — ด่านบล็อกเฉพาะแถวเตือนที่ติ๊กออก แต่ API แก้คำอธิบายลบคำนำหน้าได้ ⇒ ด่านมองไม่เห็นแถว · ติ๊กเข้าไม่มีร่องรอยว่าใครตัดสินใจและเพราะอะไร ·
/// (3) H2 — บรรทัดที่มาจาก 50 ทวิ แก้ยอดได้บนรายงาน ⇒ รายงาน ≠ ทะเบียน (แก้ที่ใบแล้วสร้างใหม่แทน)</para>
/// </summary>
public static class WhtReportLineRule
{
    /// <summary>คำนำหน้าที่ <c>DocumentService</c> ประทับบนบรรทัดรายงานร่างเมื่อเอกสารถูกยกเลิก — เจ้าของคือไฟล์นี้</summary>
    public const string VoidedDocumentPrefix = "⚠️ [เอกสารถูกยกเลิก] ";

    /// <summary>รหัสกติกาของ "ติ๊กบรรทัดที่ระบบตัดออกกลับเข้ายอด" (audit + ข้อความ)</summary>
    public const string IncludeRuleCode = "WHT-REPORT-INCLUDE-275";
    /// <summary>รหัสกติกาของ "แก้ช่องที่ระบบเป็นเจ้าของ"</summary>
    public const string EditLockRuleCode = "WHT-REPORT-LOCKED-275";
    public const string LegalReference = "ท.ป.4/2528 · ป.รัษฎากร ม.50 ทวิ/ม.59";

    /// <summary>ชนิดรายงานที่กติกานี้ใช้ (แบบ ภ.ง.ด. ที่บรรทัดมาจาก 50 ทวิ/เอกสาร)</summary>
    public static bool Applies(TaxType t)
        => t is TaxType.WithholdingTax2 or TaxType.WithholdingTax3 or TaxType.WithholdingTax53 or TaxType.WithholdingTax54;

    public static WhtReportLineKind KindOf(string? description)
    {
        var d = description ?? "";
        if (d.StartsWith(VoidedDocumentPrefix, StringComparison.Ordinal)) return WhtReportLineKind.VoidedDocument;
        if (d.StartsWith(WhtUnissuedCertGate.UnissuedCertMarker, StringComparison.Ordinal)) return WhtReportLineKind.Unissued;
        if (d.StartsWith(WhtUnissuedCertGate.DraftCertMarker, StringComparison.Ordinal)) return WhtReportLineKind.DraftCert;
        if (d.StartsWith(WhtUnissuedCertGate.JournalOnlyPnd2Marker, StringComparison.Ordinal)) return WhtReportLineKind.JournalOnly;
        if (d.StartsWith(EWithholdingScope.ReportMarker, StringComparison.Ordinal)) return WhtReportLineKind.EWithholding;
        return WhtReportLineKind.Counted;
    }

    /// <summary>บรรทัดที่ระบบตัดออกจากยอดตั้งแต่สร้าง — ติ๊กกลับเข้า = ตัดสินใจนำส่งโดยไม่มี 50 ทวิ ที่ออกแล้ว/ซ้ำกับธนาคาร/ของเอกสารที่ยกเลิก
    /// ⇒ ต้องมีเหตุผล (audit) · และเป็นบรรทัดชนิดเดียวที่ "สร้างใหม่" คืนติ๊กให้ (เฉพาะที่ผู้ใช้ติ๊กเข้าเอง)</summary>
    public static bool IncludeNeedsReason(TaxType t, string? description)
        => Applies(t) && KindOf(description) != WhtReportLineKind.Counted;

    /// <summary>คำอธิบายล็อก — คำนำหน้าของระบบคือสิ่งที่ด่านยื่นใช้มองหาแถว (แก้ = ด่านมองไม่เห็น) · บรรทัดจาก 50 ทวิ คำอธิบายมาจากใบ</summary>
    public static bool DescriptionLocked(TaxType t, string? description, Guid? whtCertId)
        => Applies(t) && (whtCertId.HasValue || KindOf(description) != WhtReportLineKind.Counted);

    /// <summary>ยอดเงินได้/อัตรา/ภาษีล็อก — บรรทัดที่มาจากทะเบียน 50 ทวิ (แก้ที่ใบ แล้วกด "สร้างใหม่" · ด่านยื่นเทียบกับทะเบียน)</summary>
    public static bool AmountsLocked(TaxType t, Guid? whtCertId) => Applies(t) && whtCertId.HasValue;

    /// <summary>ข้อความเหตุผลของช่องที่ล็อก (หน้าเว็บแสดงเป็น tooltip · เซิร์ฟเวอร์ใช้ในข้อความปฏิเสธ) — null = ไม่ล็อก</summary>
    public static string? LockReason(TaxType t, string? description, Guid? whtCertId)
    {
        if (AmountsLocked(t, whtCertId))
            return "บรรทัดนี้มาจากหนังสือรับรอง 50 ทวิ — แก้ยอด/ประเภทเงินได้ที่หนังสือรับรอง (ยกเลิกแล้วออกใหม่) แล้วกด \"สร้างใหม่\" "
                + "(ตอนยื่นระบบเทียบรายงานกับทะเบียน 50 ทวิ)";
        if (DescriptionLocked(t, description, whtCertId))
            return "คำนำหน้าบรรทัดนี้ระบบเป็นผู้เขียน (ด่านยื่นใช้หาแถวที่ยังไม่มี 50 ทวิ) — แก้คำอธิบายไม่ได้ · ติ๊ก \"ใช้\" ได้พร้อมระบุเหตุผล";
        return null;
    }

    /// <summary>ข้อความเมื่อติ๊กเข้าโดยไม่มีเหตุผล — บอกบรรทัดและทางไปต่อ</summary>
    public static string IncludeReasonRequired(int lineOrder, string? description)
        => $"บรรทัดที่ {lineOrder}: \"{Short(description)}\" ระบบตัดออกจากยอดไว้ (ยังไม่มีหนังสือรับรอง 50 ทวิ ที่ออกแล้ว / ธนาคารนำส่งแล้ว / เอกสารถูกยกเลิก) — "
           + "ติ๊กกลับเข้ายอดได้เมื่อระบุเหตุผล (บันทึก audit) · ถ้าเป็นการจ่ายจริงที่ยังไม่ออกใบ ให้ออก 50 ทวิ แล้วกด \"สร้างใหม่\" แทน";

    public static string EditLocked(int lineOrder, string field, string reason)
        => $"บรรทัดที่ {lineOrder}: แก้{field}ไม่ได้ — {reason}";

    /// <summary>คีย์คืนติ๊กตอน "สร้างใหม่" (H1): เฉพาะบรรทัดที่ระบบตัดออกและผู้ใช้ติ๊กเข้าเอง · คีย์ = เอกสาร + ใบ + คำอธิบาย (คำอธิบายของแถวระบบมีเลขใบ/รายการ)
    /// ⇒ ใบร่างของงวดจ่ายที่สองของเอกสารเดียวกันไม่ถูกติ๊กตามใบงวดแรกที่ออกแล้ว · null = บรรทัดนี้ไม่ต้องคืน</summary>
    public static (Guid? DocumentId, Guid? WhtCertId, string Description)? RestoreKey(
        TaxType t, Guid? documentId, Guid? whtCertId, string? description, bool isExcluded)
        => !isExcluded && IncludeNeedsReason(t, description) ? (documentId, whtCertId, description ?? "") : null;

    private static string Short(string? d)
    {
        var s = (d ?? "").Trim();
        return s.Length <= 60 ? s : s[..60] + "…";
    }
}

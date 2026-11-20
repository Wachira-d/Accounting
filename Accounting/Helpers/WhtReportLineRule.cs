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

    /// <summary>คำอธิบายล็อก — คำนำหน้าของระบบคือสิ่งที่ด่านยื่นใช้มองหาแถว (แก้ = ด่านมองไม่เห็น) · บรรทัดจาก 50 ทวิ/เอกสาร คำอธิบายมาจากต้นทาง
    /// (ชนิดบรรทัดในลายเซ็นตัดสินจากคำอธิบาย)</summary>
    public static bool DescriptionLocked(TaxType t, string? description, Guid? whtCertId, Guid? documentId = null)
        => Applies(t) && (whtCertId.HasValue || documentId.HasValue || KindOf(description) != WhtReportLineKind.Counted);

    /// <summary>ยอดเงินได้/อัตรา/ภาษีล็อก — <b>บรรทัดที่อยู่ในลายเซ็นร่างล้าสมัย</b> (ฝ่ายค้าน MED-1/MED-2: ลายเซ็นต้องเทียบข้อมูลต้นทาง
    /// ไม่ใช่ค่าที่ผู้ใช้แก้ในร่าง — เดิมแก้ยอดแถว "ยังไม่ออกหนังสือรับรอง"/บรรทัด ภ.พ.36 แล้วร่างล้าสมัยถาวร สร้างใหม่ก็ล้างค่าที่แก้ = วนไม่จบ) ·
    /// ภ.ง.ด.: บรรทัดจาก 50 ทวิ หรือจากเอกสาร · ภ.พ.36: บรรทัดจากเอกสาร · แก้ที่ต้นทาง (ใบ/เอกสาร) แล้วกด "สร้างใหม่" ·
    /// บรรทัด JE ล้วน (ไม่มีทั้งใบและเอกสาร · ไม่อยู่ในลายเซ็น) แก้ยอดได้เหมือนเดิม</summary>
    public static bool AmountsLocked(TaxType t, Guid? whtCertId, Guid? documentId = null)
        => (Applies(t) && (whtCertId.HasValue || documentId.HasValue))
           || (t == TaxType.VatPp36 && documentId.HasValue);

    /// <summary>ข้อความเหตุผลของช่องที่ล็อก (หน้าเว็บแสดงเป็น tooltip · เซิร์ฟเวอร์ใช้ในข้อความปฏิเสธ) — null = ไม่ล็อก</summary>
    public static string? LockReason(TaxType t, string? description, Guid? whtCertId, Guid? documentId = null)
    {
        if (whtCertId.HasValue && AmountsLocked(t, whtCertId, documentId))
            return "บรรทัดนี้มาจากหนังสือรับรอง 50 ทวิ — แก้ยอด/ประเภทเงินได้ที่หนังสือรับรอง (ยกเลิกแล้วออกใหม่) แล้วกด \"สร้างใหม่\" "
                + "(ตอนยื่นระบบเทียบรายงานกับทะเบียน 50 ทวิ)";
        if (AmountsLocked(t, whtCertId, documentId))
            return "บรรทัดนี้คำนวณจากเอกสาร — แก้ยอดที่เอกสาร แล้วกด \"สร้างใหม่\" (ตอนยื่นระบบเทียบรายงานกับเอกสารปัจจุบัน)"
                + (Applies(t) && KindOf(description) != WhtReportLineKind.Counted ? " · ติ๊ก \"ใช้\" ได้พร้อมระบุเหตุผล" : "");
        if (DescriptionLocked(t, description, whtCertId, documentId))
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

/// <summary>รอบ 275 แพ็ก B (ฝ่ายค้าน MED-4): "สร้างใหม่" คืนการติ๊ก<b>ออก</b>ของผู้ใช้ — ตัวจับคู่ตัวเดียว (pure)</summary>
public static class RegenerateUntick
{
    public readonly record struct UntickKey(Guid? DocumentId, Guid? WhtCertId, string? IncomeTypeCode, string? Description);

    /// <summary>บรรทัดของฉบับใหม่ตรงกับบรรทัดที่ติ๊กออกในฉบับเดิมไหม — ตรงทั้ง เอกสาร · ใบ · ประเภทเงินได้ · คำอธิบาย ·
    /// บรรทัดเดิมที่ไม่มีตัวอ้างใบ (ก่อนรอบ 275) เทียบโดยไม่ดูใบ</summary>
    public static bool Matches(IReadOnlyCollection<UntickKey> excluded, UntickKey fresh)
    {
        foreach (var e in excluded)
        {
            if (e.DocumentId != fresh.DocumentId) continue;
            if (!string.Equals((e.IncomeTypeCode ?? "").Trim(), (fresh.IncomeTypeCode ?? "").Trim(), StringComparison.Ordinal)) continue;
            if (!string.Equals(e.Description ?? "", fresh.Description ?? "", StringComparison.Ordinal)) continue;
            if (e.WhtCertId.HasValue && e.WhtCertId != fresh.WhtCertId) continue;
            return true;
        }
        return false;
    }
}

/// <summary>รอบ 275 แพ็ก B (คำตัดสิน 275-B-6 (ก) · T2 L3): ยกเลิกเอกสาร ⇒ 50 ทวิ ที่อ้างเอกสารนั้นทำอะไร — ตัวตัดสินเดียว</summary>
public static class WhtCertVoidCascade
{
    public const string UnlinkRuleCode = "WHT-CERT-UNLINK-ON-VOID-275";

    /// <summary>ใบที่ผูกด้วยมือ (มี <c>LinkedAt</c> — ใบที่ออกไว้ก่อนแล้วผูกกับเอกสาร) ⇒ หลุดการผูก ไม่ยกเลิก · ใบที่ระบบสร้างจากเอกสาร ⇒ ยกเลิกตาม (เดิม)</summary>
    public static bool UnlinkInsteadOfVoid(DateTime? linkedAt) => linkedAt.HasValue;

    public static string UnlinkedNotice(string certNo, string docNo)
        => $"50 ทวิ {certNo} ที่ผูกด้วยมือกับ {docNo} หลุดการผูก (ไม่ถูกยกเลิก) — ขึ้นเป็น \"ยังไม่ผูก\" ที่หน้าหนังสือรับรอง: ผูกกับใบสำคัญจ่ายที่ถูกต้อง หรือยกเลิกใบถ้าไม่มีการจ่ายจริง";

    public static string VoidFailedNotice(string certNo, string reason)
        => $"⚠️ ยกเลิก 50 ทวิ {certNo} ตามเอกสารไม่สำเร็จ ({reason}) — ใบยังนับในแบบ ภ.ง.ด. · ยกเลิกใบที่หน้าหนังสือรับรอง";
}

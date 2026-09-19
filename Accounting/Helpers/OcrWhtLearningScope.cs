namespace Accounting.Helpers;

/// <summary>หลักฐานว่า "ยอดหัก ณ ที่จ่ายบนเอกสารใบนี้มาจากไหน" — ตัวป้อนของ
/// <see cref="OcrWhtLearningScope"/></summary>
public enum WhtLearningEvidence
{
    /// <summary>เอกสารไม่ได้มาจากสแกน (คีย์มือ · แปลงจากใบอื่น · ทางเข้า API) ⇒ ยอดที่อยู่บนใบ
    /// เป็นสิ่งที่**คน**ใส่ไว้เอง — เรียนได้</summary>
    NoScan,

    /// <summary>กระดาษพิมพ์ส่วนหัก ณ ที่จ่ายไว้เอง (<c>OcrScanResult.HasWht</c> หลังรอบ D3-2
    /// แปลว่า "อ่านจากกระดาษ" เท่านั้น) — หลักฐานชั้นดีที่สุด เรียนได้</summary>
    Paper,

    /// <summary>ผู้ใช้<b>ลงมือ</b>แก้/ยืนยันช่อง WHT ในหน้ารีวิวหรือก่อนอนุมัติ
    /// (<c>UserCorrectedFields</c>) = <see cref="Models.Enums.UserChoiceSource.Explicit"/> — เรียนได้</summary>
    UserEdited,

    /// <summary>ยอดมาจาก<b>ข้อเสนอของระบบเอง</b>ที่ไม่มีใครแตะ — <b>ห้ามเรียน</b></summary>
    SystemSuggestedOnly,
}

/// <summary>
/// **"เอกสารใบนี้สอนประวัติหัก ณ ที่จ่ายของผู้ขายได้ไหม"** (pure, ไม่มี I/O)
///
/// ═══ ที่มา (ผลตรวจ 2026-09-18 · D3-2) ═══
/// <para><c>VendorIntelligenceService</c> เรียนจาก <c>doc.WithholdingTaxAmount &gt; 0</c> ตรง ๆ
/// ขณะที่ไปป์ไลน์ OCR เคยเอา<b>ประวัติของผู้ขายรายเดียวกันนั้น</b>ไปเติม WHT ให้ใบใหม่
/// อัตโนมัติ ⇒ ประวัติยืนยันประวัติของตัวเอง (self-confirm loop) ความมั่นใจโตขึ้นเรื่อย ๆ
/// โดยไม่มีหลักฐานใหม่สักชิ้น — สิ่งที่ <c>DECISION_DOCTRINE</c> §3 ห้ามไว้ตรง ๆ</para>
///
/// <para>กติกา: <b>เรียนได้เฉพาะเมื่อคำตอบไม่ได้มาจากปากของเราเอง</b> — กระดาษพูด · คนพิมพ์ ·
/// คนแก้ ⇒ เรียน · ระบบเสนอแล้วไม่มีใครแตะ ⇒ ไม่เรียน (ไม่ใช่ "เรียนแบบถ่วงน้ำหนักน้อย" —
/// สัญญาณที่มาจากตัวเองมีค่าเป็นศูนย์ ไม่ใช่ค่าน้อย)</para>
/// </summary>
public static class OcrWhtLearningScope
{
    /// <summary>ชื่อช่องใน <c>OcrScanResult.UserCorrectedFields</c> ที่แปลว่า "คนลงมือเรื่อง WHT"
    /// — ชุดเดียวกับที่ <see cref="OcrCorrectedFieldList"/> เขียน</summary>
    public static readonly string[] WhtFieldNames = { "HasWht", "WhtRate", "WhtIncomeTypeCode" };

    /// <param name="hasScan">มีแถวสแกนผูกกับเอกสารนี้ไหม</param>
    /// <param name="paperShowsWht">แถวสแกนบอกว่ากระดาษมีส่วนหัก (<c>HasWht</c> / <c>WhtRate &gt; 0</c>)</param>
    /// <param name="userCorrectedFields">CSV ของช่องที่ผู้ใช้แก้ (<c>OcrScanResult.UserCorrectedFields</c>)</param>
    public static (bool Learn, WhtLearningEvidence Evidence) Decide(
        bool hasScan, bool paperShowsWht, string? userCorrectedFields)
    {
        if (!hasScan) return (true, WhtLearningEvidence.NoScan);
        if (paperShowsWht) return (true, WhtLearningEvidence.Paper);
        if (UserTouchedWht(userCorrectedFields)) return (true, WhtLearningEvidence.UserEdited);
        return (false, WhtLearningEvidence.SystemSuggestedOnly);
    }

    /// <summary>ผู้ใช้แก้ช่องที่เกี่ยวกับ WHT อย่างน้อยหนึ่งช่องไหม (รายการคั่นด้วย <c>,</c>)
    /// — <c>private</c> โดยตั้งใจ: ทางเข้าเดียวของทั้งระบบคือ <see cref="Decide"/>
    /// (มีหลายชั้นหลักฐาน ไม่ใช่ช่องนี้ช่องเดียว)</summary>
    private static bool UserTouchedWht(string? userCorrectedFields)
    {
        if (string.IsNullOrWhiteSpace(userCorrectedFields)) return false;
        foreach (var raw in userCorrectedFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var f in WhtFieldNames)
                if (string.Equals(raw, f, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>เหตุผลภาษาไทยสำหรับ log — ห้ามให้ผู้เรียกแต่งคำเอง (สำเนาชุดที่สอง)</summary>
    public static string Explain(WhtLearningEvidence evidence) => evidence switch
    {
        WhtLearningEvidence.NoScan => "เอกสารไม่ได้มาจากสแกน — ยอดหัก ณ ที่จ่ายเป็นสิ่งที่คนใส่เอง",
        WhtLearningEvidence.Paper => "กระดาษพิมพ์ส่วนหัก ณ ที่จ่ายไว้เอง",
        WhtLearningEvidence.UserEdited => "ผู้ใช้ลงมือแก้/ยืนยันช่องหัก ณ ที่จ่ายเอง",
        WhtLearningEvidence.SystemSuggestedOnly =>
            "ยอดหัก ณ ที่จ่ายมาจากข้อเสนอของระบบเองและไม่มีใครแตะ — เรียนกลับ = สอนตัวเอง",
        _ => "ไม่ทราบที่มา",
    };
}

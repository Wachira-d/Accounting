namespace Accounting.Helpers;

/// <summary>คำตัดสินว่าจะให้ทะเบียน DBD "ชนะ" ชื่อที่ระบบต้นทางส่งมาไหม</summary>
public enum DbdTrustVerdict
{
    /// <summary>ชื่อตรงกัน (หลังตัดคำนำหน้า/คำต่อท้ายมาตรฐาน) — ใช้ชื่อทางการได้เต็มที่</summary>
    ExactMatch = 1,
    /// <summary>ชื่อ**คล้าย**พอที่จะเป็นบริษัทเดียวกันที่สะกดเพี้ยน — ทะเบียนชนะ + เรียนรู้ได้</summary>
    SameCompanyMisspelled = 2,
    /// <summary>ต้นทางไม่ได้ส่งชื่อมา — ใช้ชื่อจากทะเบียนได้เลย ไม่มีอะไรให้ขัดแย้ง</summary>
    NoIncomingName = 3,
    /// <summary><b>คนละบริษัท</b> — ผู้ต้องสงสัยคือ "เลขผู้เสียภาษี" ไม่ใช่ชื่อ
    /// ⇒ ห้ามทับ ห้ามสอน ห้ามสร้างข้อมูลของบริษัทที่ไม่เกี่ยวข้อง</summary>
    KeyLooksWrong = 9,
}

/// <summary>
/// **ทะเบียนราชการเชื่อถือได้เฉพาะเมื่อ "กุญแจที่ใช้ค้น" ถูก** — ตัวตัดสินตัวเดียวของระบบ
///
/// ═══ ที่มา (บั๊กจริง — บันทึกไว้ใน CLAUDE.md) ═══
/// การค้น DBD ใช้ <b>เลขผู้เสียภาษี</b> เป็นคีย์ ซึ่งเป็นช่องที่ผิดได้บ่อยที่สุด — ทั้งจาก
/// OCR (บาร์โค้ด EAN-13 ที่ผ่าน mod-11 · เลขผู้ซื้อถูกหยิบมาเป็นผู้ขาย · หลักเดียวเพี้ยน)
/// และจาก<b>ระบบภายนอกที่พิมพ์เลขผิด/ส่งฟิลด์เหลื่อม</b> ⇒ เลขผิดจะได้ข้อมูล
/// <b>บริษัทอื่น</b>ที่ถูกต้อง 100% ตามทะเบียน แล้วระบบเอาไปทับชื่อที่ถูกอยู่แล้ว ·
/// สอนตัวเรียนรู้ผิดถาวร · สร้าง Contact ของบริษัทที่ไม่เกี่ยวข้องเลย
///
/// ═══ ตัวแยก ═══
/// <b>ระดับความต่างของชื่อ</b> — การสะกดเพี้ยนของ "ชื่อเดียวกัน" ได้สตริงที่ <i>คล้าย</i>
/// เสมอ (สมมติฐานทั้งหมดของ <c>FuzzyMatcher</c>) ส่วนคนละบริษัทได้เกือบศูนย์ ·
/// วัดกับตัวอย่างจริง: อ่านเพี้ยน <b>0.772–0.941</b> vs คนละบริษัท <b>0.000–0.087</b>
/// ⇒ เกณฑ์ <b>0.45</b> นั่งกลางช่องว่างกว้าง ๆ
///
/// <para>⚠️ เกณฑ์นี้ตั้งจากการ<b>วัดช่องว่างระหว่างสองกลุ่มจริง</b> ไม่ใช่หยิบเลขสวย ๆ มาใช้ —
/// เทสต์ต้องล็อก<b>ช่องว่าง</b> ไม่ใช่ล็อกแค่ตัวเลขเกณฑ์ (บทเรียน "threshold ที่ตั้งจาก
/// ตัวอย่างชนิดเดียวจะพังกับชนิดอื่น")</para>
///
/// <para>เดิมตรรกะนี้เขียน inline อยู่ใน <c>OcrService.EnrichFromDbdAsync</c> ที่เดียว ·
/// พอเส้น integration ต้องใช้ด้วยจึงยุบมาไว้ที่นี่ — <b>ห้ามคัดลอกไปเขียนใหม่</b>
/// (defect class "สูตร/ตารางที่คัดลอกไปเขียนใหม่ = drift แน่นอน แค่รอเวลา")</para>
/// </summary>
public static class DbdIdentityGuard
{
    /// <summary>คะแนนต่ำกว่านี้ = คนละบริษัท ⇒ กุญแจ (เลขผู้เสียภาษี) น่าจะผิด</summary>
    public const double SameCompanyFloor = 0.45;

    /// <summary>คำนำหน้า/คำต่อท้ายที่ไม่ได้ช่วยแยกตัวตน — ตัดก่อนเทียบ</summary>
    private static readonly string[] Noise =
    {
        "บริษัท", "จำกัด", "จํากัด", "(มหาชน)", "มหาชน", "หจก.", "หจก",
        "ห้างหุ้นส่วนจำกัด", "ห้างหุ้นส่วนสามัญ", "ห้างหุ้นส่วน",
        "(สำนักงานใหญ่)", "สำนักงานใหญ่", "company limited", "co., ltd.", "co.,ltd.",
        "co ltd", "limited", "ltd.", "ltd", "public",
    };

    /// <summary>ตัดคำมาตรฐาน/ช่องว่าง/เครื่องหมายออก เหลือแก่นของชื่อ</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var s = name.Trim().ToLowerInvariant();
        foreach (var n in Noise) s = s.Replace(n.ToLowerInvariant(), " ");
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>ตัดสินว่าทะเบียนชนะได้ไหม
    ///
    /// <para><paramref name="similarity"/> รับเข้ามาเป็นพารามิเตอร์ (ไม่คำนวณเอง) เพื่อให้
    /// helper นี้เป็น<b>ฟังก์ชันบริสุทธิ์</b> ทดสอบได้โดยไม่ต้องลาก FuzzyMatcher —
    /// ผู้เรียกส่ง <c>FuzzyMatcher.Similarity(registryName, incomingName)</c> เข้ามา</para></summary>
    public static DbdTrustVerdict Judge(string? registryName, string? incomingName, double similarity)
    {
        if (string.IsNullOrWhiteSpace(incomingName)) return DbdTrustVerdict.NoIncomingName;
        if (string.IsNullOrWhiteSpace(registryName)) return DbdTrustVerdict.KeyLooksWrong;

        var a = Normalize(incomingName);
        var b = Normalize(registryName);
        if (a.Length > 0 && b.Length > 0
            && (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
                || b.Contains(a, StringComparison.OrdinalIgnoreCase)
                || a.Contains(b, StringComparison.OrdinalIgnoreCase)))
            return DbdTrustVerdict.ExactMatch;

        return similarity >= SameCompanyFloor
            ? DbdTrustVerdict.SameCompanyMisspelled
            : DbdTrustVerdict.KeyLooksWrong;
    }

    /// <summary>ทะเบียนชนะได้ไหม (ใช้ชื่อทางการทับของเดิม)</summary>
    public static bool RegistryWins(DbdTrustVerdict v) => v != DbdTrustVerdict.KeyLooksWrong;

    /// <summary>ข้อความอธิบายเมื่อกุญแจน่าสงสัย — ต้องบอกว่า<b>ให้ไปตรวจอะไร</b>
    /// ไม่ใช่แค่บอกว่าไม่ผ่าน</summary>
    public static string KeyMismatchMessage(string taxId, string registryName, string incomingName)
        => $"เลขผู้เสียภาษี {taxId} ขึ้นทะเบียนเป็น \"{registryName}\" "
         + $"แต่ระบบต้นทางส่งชื่อ \"{incomingName}\" มา (ต่างกันสิ้นเชิง) — "
         + "น่าจะเป็น**เลขผู้เสียภาษีผิด** จึงไม่แก้ชื่อให้อัตโนมัติ กรุณาตรวจเลขที่ต้นทาง";
}

namespace Accounting.Helpers;

/// <summary>คำตัดสินว่าจะให้ทะเบียน DBD "ชนะ" ชื่อที่ระบบต้นทางส่งมาไหม</summary>
public enum DbdTrustVerdict
{
    /// <summary>ชื่อตรงกัน (หลังตัดคำนำหน้า/คำต่อท้ายมาตรฐาน) — ใช้ชื่อทางการได้เต็มที่</summary>
    ExactMatch = 1,
    /// <summary>ชื่อ**คล้าย**พอที่จะเป็นบริษัทเดียวกันที่สะกดเพี้ยน — ทะเบียนชนะ + เรียนรู้ได้</summary>
    SameCompanyMisspelled = 2,
    /// <summary>ต้นทางไม่ได้ส่งชื่อมา <b>และกุญแจพิสูจน์แล้ว</b> — ใช้ชื่อจากทะเบียน
    /// ได้เต็มที่ ไม่มีอะไรให้ขัดแย้ง</summary>
    NoIncomingName = 3,
    /// <summary><b>กุญแจพิสูจน์แล้ว แต่ชื่อไม่คล้ายเลย</b> — ชื่อที่ต้นทางส่งมาเป็น
    /// <b>แบรนด์/โลโก้/ชื่อสาขา</b> ไม่ใช่ชื่อนิติบุคคล (เช่น "DECATHLON" vs
    /// "บริษัท ดีแคทลอน (ประเทศไทย) จำกัด" — คนละตัวอักษร คะแนนคล้าย 0.000)
    /// ⇒ ทะเบียนชนะ + เก็บของเดิมเป็น negative example ได้ เพราะ<b>กุญแจ</b>
    /// ผ่านการพิสูจน์มาแล้ว (ดู <c>OcrVendorKeyEvidence</c>) ไม่ใช่เดาจากความคล้าย</summary>
    KeyVerifiedNameDiffers = 4,

    /// <summary><b>ไม่มีชื่อให้เทียบ และกุญแจก็ยังพิสูจน์ไม่ได้</b> — ทะเบียนยังชนะ
    /// (ต้องมีค่าเติมตาม กฎเหล็ก #3 — ช่องว่างไม่ใช่คำตอบที่ปลอดภัยกว่า) แต่
    /// <b>ห้ามประทับความมั่นใจเต็ม</b>: เลข 13 หลักที่ผ่าน checksum อาจเป็นบาร์โค้ด
    /// หรือเลขผู้ซื้อ ⇒ ทะเบียนจะคืนชื่อ<b>บริษัทอื่น</b>ที่ถูกต้อง 100% ตามทะเบียน
    /// แล้วไหลลงใบกำกับโดยไม่มีอะไรฟ้อง (§86/4 บังคับชื่อผู้ประกอบการที่ถูกต้อง)
    /// ⇒ ผู้เรียกต้องตั้ง confidence ต่ำกว่าเกณฑ์ไฮไลต์เหลือง 0.85 (กฎเหล็ก #3 ข้อ 3)
    ///
    /// <para>แยกจาก <see cref="NoIncomingName"/> เพราะ "ไม่รู้" กับ "รู้แล้วว่าใช่"
    /// ห้ามเป็นค่าเดียวกัน — เงื่อนไขที่เป็นเท็จเพราะ<b>ไม่มีข้อมูล</b> ห้ามตกเป็น "ผ่าน"</para></summary>
    NoIncomingNameUnprovenKey = 5,

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

    /// <summary>ชื่อ (หลัง <see cref="Normalize"/>) ต้องยาวอย่างน้อยเท่านี้จึงจะใช้
    /// เส้นทาง "ชื่อหนึ่งเป็นส่วนหนึ่งของอีกชื่อ" ตัดสินว่าเป็นบริษัทเดียวกันได้
    ///
    /// <para>ที่มา: เส้นทาง <c>Contains</c> ไม่มีขั้นต่ำมาก่อน ⇒ ชื่อย่อ/แบรนด์สั้น
    /// เป็น<b>ส่วนหนึ่ง</b>ของชื่อทะเบียนบริษัทอื่นได้ง่ายมาก — "ปตท" อยู่ใน
    /// "ปตท น้ำมันและการค้าปลีก" (คนละนิติบุคคล) · "cp" อยู่ใน "cp all" ·
    /// "ais" อยู่ใน "aisin" ⇒ ได้ <see cref="DbdTrustVerdict.ExactMatch"/>
    /// แล้ว<b>ข้ามด่าน</b> <see cref="SameCompanyFloor"/> ไปเลย ทั้งที่เป็นเคส
    /// ที่ด่านนั้นถูกสร้างมากันพอดี</para></summary>
    public const int MinSubstringLength = 4;

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
    /// <param name="keyProven">กุญแจ (เลขผู้เสียภาษี) ผ่านการพิสูจน์<b>ด้วยหลักฐานอื่น</b>
    /// นอกเหนือจากความคล้ายของชื่อแล้ว — เส้น OCR ส่งผลจาก
    /// <c>OcrVendorKeyEvidence.Judge</c> (ป้ายกำกับบนกระดาษ + ไม่ใช่เลขผู้ซื้อ/เลขเรา
    /// + ไม่ได้อยู่ในบล็อกผู้ซื้อ) เข้ามา · เส้นที่ไม่มีกระดาษให้ดู (integration)
    /// ปล่อยเป็น <c>false</c> แล้วพฤติกรรมเดิมทุกอย่างคงเดิม</param>
    public static DbdTrustVerdict Judge(string? registryName, string? incomingName, double similarity,
        bool keyProven = false)
    {
        // ⚠️ "อ่านชื่อไม่ได้" **ไม่ใช่** หลักฐานว่าเลขถูก — ต้องถามกุญแจต่อ
        // (เดิมคืน NoIncomingName ทันที ⇒ RegistryWins = true ⇒ call site ประทับ
        //  ความมั่นใจ 1.0 ⇒ ชื่อบริษัทอื่นลงใบกำกับเงียบสนิท · ทีม T1 รอบ 177)
        if (string.IsNullOrWhiteSpace(incomingName))
            return keyProven ? DbdTrustVerdict.NoIncomingName : DbdTrustVerdict.NoIncomingNameUnprovenKey;
        if (string.IsNullOrWhiteSpace(registryName)) return DbdTrustVerdict.KeyLooksWrong;

        var a = Normalize(incomingName);
        var b = Normalize(registryName);
        if (a.Length > 0 && b.Length > 0
            && (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
                || (Math.Min(a.Length, b.Length) >= MinSubstringLength
                    && (b.Contains(a, StringComparison.OrdinalIgnoreCase)
                        || a.Contains(b, StringComparison.OrdinalIgnoreCase)))))
            return DbdTrustVerdict.ExactMatch;

        if (similarity >= SameCompanyFloor) return DbdTrustVerdict.SameCompanyMisspelled;

        // ชื่อไม่คล้ายเลย — คำถามต่อไปคือ "แล้ว**กุญแจ**ล่ะ ถูกไหม"
        // ถ้ากุญแจพิสูจน์มาแล้วด้วยหลักฐานบนกระดาษ ความไม่คล้ายของชื่อ**ไม่ใช่**
        // หลักฐานว่าเลขผิดอีกต่อไป — มันแปลว่ากระดาษพิมพ์ชื่อแบรนด์ ไม่ใช่ชื่อนิติบุคคล
        return keyProven
            ? DbdTrustVerdict.KeyVerifiedNameDiffers
            : DbdTrustVerdict.KeyLooksWrong;
    }

    /// <summary>ทะเบียนชนะได้ไหม (ใช้ชื่อทางการทับของเดิม)</summary>
    public static bool RegistryWins(DbdTrustVerdict v) => v != DbdTrustVerdict.KeyLooksWrong;

    /// <summary>เก็บชื่อเดิมเป็น <b>negative example</b> ได้ไหม
    ///
    /// <para>เฉพาะกรณีที่รู้แน่ว่าเป็น<b>บริษัทเดียวกัน</b> — ชื่อเดิมจึงเป็น
    /// "คำตอบที่ผิดของผู้ขายรายนี้" จริง ๆ. กรณีชื่อตรงกันอยู่แล้วไม่มีอะไรให้สอน
    /// และกรณีกุญแจน่าสงสัยห้ามสอนเด็ดขาด (จะสอนว่าชื่อที่ถูกคือชื่อบริษัทอื่น)</para>
    ///
    /// <para>⚠️ <b>ชื่อย่อที่ถูกต้องไม่ใช่คำตอบผิด</b> — เมื่อชื่อหนึ่งเป็นส่วนหนึ่ง
    /// ของอีกชื่อ ("ซีพี" ⊂ "ซีพี ออลล์" · "PTT" ⊂ "PTT Global Chemical") แปลว่า
    /// ต้นทางเขียน<b>ชื่อย่อ/ชื่อทางการค้า</b> ไม่ใช่สะกดผิด. หลังใส่
    /// <see cref="MinSubstringLength"/> ชื่อย่อสั้น ๆ ไม่ได้ <c>ExactMatch</c> อีก
    /// ต่อไป จึงตกมาที่สาขานี้ ⇒ ถ้าไม่กันไว้ ระบบจะจดชื่อย่อที่ผู้ใช้ใช้อยู่ทุกวัน
    /// เป็น "คำตอบที่ผิด" ถาวร (ฝ่ายค้านรอบ 174 · หลักการข้อ 8 ทิศตรงข้าม)</para></summary>
    public static bool ShouldLearnMismatch(DbdTrustVerdict v, string? registryName = null, string? incomingName = null)
    {
        if (v is not (DbdTrustVerdict.SameCompanyMisspelled or DbdTrustVerdict.KeyVerifiedNameDiffers))
            return false;
        var a = Normalize(incomingName);
        var b = Normalize(registryName);
        // เฉพาะทิศ "ชื่อที่ได้มา **สั้นกว่า** และเป็นส่วนหนึ่งของชื่อทะเบียน" = ชื่อย่อ
        //
        // ⚠️ ทิศกลับ (ชื่อที่ได้มา**ยาวกว่า**และคลุมชื่อทะเบียน) **ต้องสอน** — นั่นคือ
        // ชื่อที่มีขยะพ่วงท้าย เช่น "(มหาซน)" ที่ OCR อ่านเพี้ยนจาก "(มหาชน)" จนตัดคำ
        // มาตรฐานไม่ออก ⇒ เหลือหางติดมา. กติกาแรกที่ผมเขียนเช็คทั้งสองทิศ แล้วเทสต์
        // ทิศตรงข้ามของตัวเองจับได้ว่ามันปิดการเรียนรู้เคสที่ควรเรียน
        if (a.Length > 0 && b.Length > 0 && a.Length < b.Length
            && b.Contains(a, StringComparison.OrdinalIgnoreCase))
            return false;   // ชื่อย่อของรายเดียวกัน — ไม่มี "ความผิด" ให้สอน
        return true;
    }

    /// <summary>ข้อความอธิบายเมื่อกุญแจน่าสงสัย — ต้องบอกว่า<b>ให้ไปตรวจอะไร</b>
    /// ไม่ใช่แค่บอกว่าไม่ผ่าน</summary>
    public static string KeyMismatchMessage(string taxId, string registryName, string incomingName)
        => $"เลขผู้เสียภาษี {taxId} ขึ้นทะเบียนเป็น \"{registryName}\" "
         + $"แต่ระบบต้นทางส่งชื่อ \"{incomingName}\" มา (ต่างกันสิ้นเชิง) — "
         + "น่าจะเป็น**เลขผู้เสียภาษีผิด** จึงไม่แก้ชื่อให้อัตโนมัติ กรุณาตรวจเลขที่ต้นทาง";
}

namespace Accounting.Helpers;

/// <summary>
/// **ประเภทเงินได้ ม.40 ของบรรทัดเอกสาร** — ค่าที่ผู้ใช้/ผู้เรียกส่งมา → รหัสที่เก็บ (pure ไม่มี I/O)
///
/// ═══ ที่มา (รอบ 203 · คำถามเจ้าของ "หักค่าเบี้ยประกันภัย 1% ลงถูกช่องในใบหัก ณ ที่จ่ายไหม") ═══
/// <para><c>DocumentLine.IncomeTypeCode</c> มีอยู่และเป็นตัวตัดสินแถวบน 50 ทวิ + รหัส Col7 ของไฟล์ ภ.ง.ด.
/// แต่ <c>DocumentLineRequest</c> <b>ไม่มีช่องนี้</b> ⇒ (1) ใบสำคัญจ่าย/ค่าใช้จ่ายที่คีย์มือไม่มีทางระบุประเภทเลย —
/// 50 ทวิ ที่ออกอัตโนมัติได้ "8" ค่าบริการเสมอ (ค่าเบี้ยประกันวินาศภัยตกแถว 5 ถูกโดยบังเอิญแต่ไม่ระบุว่าจ่ายอะไร ·
/// ดอกเบี้ย/ค่าสิทธิตกแถวผิด) (2) ใบจากเส้น OCR ที่ระบบเติมประเภทไว้ <b>เปิดแก้แล้วบันทึก ประเภทหายเงียบ ๆ</b>
/// เพราะการแก้สร้างบรรทัดใหม่ทั้งชุด (กฎเหล็ก #4 A "เก็บแล้วต้อง echo กลับ")</para>
///
/// <para>กติกา (ตาม checklist "เพิ่ม field ใหม่"): ไม่ส่ง (<c>null</c>) = <b>คงค่าเดิม</b> ของบรรทัดเดียวกันตอนแก้ไข
/// (ฟอร์ม/API รุ่นเก่าที่ไม่รู้จักช่องนี้ต้องไม่ล้างค่าที่ OCR เติมไว้) · <c>""</c> = ล้างค่า · ค่าอื่นต้องชี้ขาดได้ในตาราง
/// <see cref="ThaiWhtRateTable"/> (รับรหัส/เลขมาตรา แล้วเก็บเป็นรหัสมาตรฐาน) — ชี้ขาดไม่ได้ = ปฏิเสธพร้อมเหตุผล
/// (ห้ามเก็บค่าที่ไม่มีผู้อ่านคนไหนเข้าใจ — ตกแถว 6 เงียบ ๆ)</para>
/// </summary>
public static class DocumentLineIncomeType
{
    /// <param name="Code">รหัสที่จะเก็บ (null = ไม่ระบุ)</param>
    /// <param name="Error">ข้อความปฏิเสธภาษาไทย (null = ใช้ได้)</param>
    public readonly record struct Verdict(string? Code, string? Error);

    /// <summary>ตัดสินค่าที่จะเก็บ</summary>
    /// <param name="requested">ค่าที่ส่งมา — null = ไม่ได้ส่ง · "" = ล้าง</param>
    /// <param name="kept">ค่าเดิมของบรรทัดเดียวกัน (ตอนแก้ไข · ตอนสร้าง = null)</param>
    public static Verdict Decide(string? requested, string? kept)
    {
        if (requested is null) return new Verdict(kept, null);
        if (string.IsNullOrWhiteSpace(requested)) return new Verdict(null, null);
        var t = ThaiWhtRateTable.Resolve(requested);
        return t is null
            ? new Verdict(null, $"ประเภทเงินได้ \"{requested.Trim()}\" ไม่อยู่ในตารางประเภทเงินได้ ม.40 ของระบบ — เลือกจากรายการ")
            : new Verdict(t.Code, null);
    }

    /// <summary>ค่าเดิมของ "บรรทัดเดียวกัน" ตอนแก้ไข — ลำดับเดียวกัน + คำอธิบายเดิม (ตัดช่องว่าง) ·
    /// ไม่ตรง = null (บรรทัดถูกสลับ/แก้คำอธิบาย ⇒ ไม่เดาว่าเป็นบรรทัดเดิม)</summary>
    public static string? KeptFromPrevious(IReadOnlyList<(string? Description, string? Code)> previous, int index, string? description)
    {
        if (index < 0 || index >= previous.Count) return null;
        var (prevDesc, prevCode) = previous[index];
        return string.Equals((prevDesc ?? "").Trim(), (description ?? "").Trim(), StringComparison.Ordinal)
            ? prevCode
            : null;
    }
}

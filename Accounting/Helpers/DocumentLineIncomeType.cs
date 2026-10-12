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

    /// <summary>ประเภทที่ "เอกสารฝั่งผู้จ่าย" (ใบสำคัญจ่าย · ค่าใช้จ่าย · ใบแจ้งหนี้ซื้อ · ใบรับรองแทนใบเสร็จ) <b>เลือกใหม่ไม่ได้</b> —
    /// <c>gov1</c> = เงินได้ที่หน่วยงานรัฐหักเรา (ม.69 ทวิ · ฝั่งทะเบียนภาษีถูกหัก) · <c>1</c> = เงินเดือน 40(1) อัตราก้าวหน้า (ภ.ง.ด.1 ผ่านระบบเงินเดือน)</summary>
    public static readonly IReadOnlyList<string> NotForPayerSide = new[] { "gov1", "1" };

    /// <summary>
    /// ข้อความปฏิเสธเมื่อเอกสารฝั่งผู้จ่าย<b>เลือกใหม่</b>เป็นประเภทใน <see cref="NotForPayerSide"/> (null = ใช้ได้) — ฝ่ายค้าน L3 (2026-10-12)
    /// <para>ปฏิเสธเฉพาะ "การเปลี่ยน": ค่าที่เก็บไว้แล้วบนบรรทัดเดียวกัน (<paramref name="kept"/>) ยังโหลด/บันทึกซ้ำได้ (ข้อมูลเก่าไม่ถูกขังจนแก้ใบไม่ได้) ·
    /// ฝั่งรับเงินไม่ตรวจ (ใบแจ้งหนี้ให้หน่วยงานรัฐ = gov1 ถูกต้อง)</para>
    /// </summary>
    /// <param name="payerSide">เอกสารเป็นฝั่งผู้จ่ายไหม (<see cref="WhtCertIssuePlan.AppliesTo"/>)</param>
    /// <param name="requested">ค่าที่ส่งมา (null = ไม่ส่ง · "" = ล้าง)</param>
    /// <param name="kept">ค่าเดิมของบรรทัดเดียวกัน (สร้าง = null)</param>
    public static string? PayerSideProblem(bool payerSide, string? requested, string? kept)
    {
        if (!payerSide) return null;
        var code = Decide(requested, kept).Code;
        if (code is null || !NotForPayerSide.Contains(code)) return null;
        if (string.Equals(code, Inherit(kept), StringComparison.Ordinal)) return null;
        return code == "gov1"
            ? "ประเภท \"เงินได้ที่ส่วนราชการ/องค์การของรัฐจ่าย (ม.69 ทวิ)\" ใช้กับเงินที่หน่วยงานรัฐหักเรา (บันทึกที่ทะเบียนภาษีถูกหัก ณ ที่จ่าย) — "
              + "เอกสารจ่ายของบริษัทเลือกประเภทนี้ไม่ได้ · เลือกประเภทเงินได้ของสิ่งที่จ่ายจริง (เช่น ค่าบริการ 40(8))"
            : "ประเภท \"เงินเดือน ค่าจ้าง 40(1)\" หักตามอัตราก้าวหน้าและยื่น ภ.ง.ด.1 ผ่านเมนูเงินเดือน — "
              + "เอกสารจ่ายเลือกประเภทนี้ไม่ได้ (50 ทวิ/ภ.ง.ด.3 จะผิดแบบ) · จ่ายเงินเดือนผ่านเมนูเงินเดือน หรือเลือกประเภทที่ตรงกับสิ่งที่จ่าย";
    }

    /// <summary>ค่าที่เอกสารลูก/ใบที่ generate <b>สืบทอด</b> จากบรรทัดต้นทาง (แปลงเอกสาร · คัดลอก · รายการประจำ) — กฎเหล็ก #4 A "เอกสารลูกต้องสืบทอด"
    /// (ฝ่ายค้าน M4 · 2026-10-12: เดิมการแปลงไม่ส่งช่องนี้ ⇒ PO→PV/PI ได้ใบไม่มีประเภท ⇒ 50 ทวิ ตก "8" เสมอ)
    /// · null → null (ห้ามแปลงเป็นค่าตั้งต้น) · ค่าที่ตารางกลางชี้ขาดได้ → รหัสมาตรฐาน · ค่าเก่าที่ชี้ขาดไม่ได้ → null
    /// (ข้อมูลก่อนมีด่าน — ห้ามทำให้การแปลงทั้งใบล้มด้วยค่าที่ผู้ใช้ไม่ได้พิมพ์ในคำขอนี้)</summary>
    public static string? Inherit(string? stored)
        => string.IsNullOrWhiteSpace(stored) ? null : ThaiWhtRateTable.Resolve(stored)?.Code;

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

namespace Accounting.Helpers;

/// <summary>ผลอ่าน KPI คู่ของไปป์ไลน์ OCR</summary>
/// <param name="AiCallRate">สัดส่วนสแกนที่ <b>จ่ายเงินให้ provider จริง</b> (0–1) — ควรลดลง</param>
/// <param name="FirstPassAcceptRate">สัดส่วนสแกนที่กลายเป็นเอกสารโดย<b>ผู้ใช้ไม่ต้องแก้อะไรเลย</b> (0–1) — ควรคงที่หรือสูงขึ้น</param>
/// <param name="Verdict">คำตัดสินภาษาคน</param>
/// <param name="IsRegression">true = ตัวเลขบอกว่ากำลัง "ประหยัดโดยโง่ลง"</param>
public readonly record struct OcrKpiReading(
    decimal AiCallRate, decimal FirstPassAcceptRate, string Verdict, bool IsRegression);

/// <summary>
/// **KPI คู่ของไปป์ไลน์ OCR** (pure, ไม่มี I/O) — สถาปัตยกรรมเป้าหมาย D4
///
/// ═══ ทำไมต้องอ่านเป็น "คู่" ═══
/// กฎเหล็ก #1 ข้อ 6 บอกให้ใช้ <c>&lt;Feature&gt;UsedAi</c> ที่ลดลงเป็นตัวชี้วัดความสำเร็จ
/// — แต่ตัวเลขนั้น<b>ลดลงได้สองสาเหตุที่ตรงข้ามกัน</b>:
/// <list type="bullet">
/// <item>นักเรียนเก่งขึ้นจนไม่ต้องถามครู = <b>สำเร็จ</b></item>
/// <item>โค้ดหยุดใช้คำตอบของโมเดลไปเฉย ๆ (เช่นด่านที่เขียนผิด) = <b>ถอยหลัง</b>
///   ผู้ใช้ได้ heuristic เปล่า ๆ แล้วต้องมานั่งแก้เองทุกใบ</item>
/// </list>
/// ทั้งสองกรณี <c>UsedAi</c> ลดลงเหมือนกันเป๊ะ ⇒ อ่านตัวเดียวแยกไม่ออก
/// (บทเรียนจริง: ด่าน <c>if (UsedAi &amp;&amp; …)</c> ทิ้งคำตอบนักเรียนอยู่หลายเดือน
/// โดยกราฟต้นทุนดู "ดีขึ้น" ตลอด)
///
/// <para>กติกา: ต้องอ่าน <b>คู่กับ first-pass accept rate</b> เสมอ — "สแกนแล้วกลายเป็น
/// เอกสารโดยผู้ใช้ไม่ต้องแก้อะไรเลย" ซึ่งเป็นนิยามของกฎเหล็ก #3 (1-click approve)</para>
/// </summary>
public static class OcrQualityKpi
{
    /// <summary>ต่ำกว่านี้ถือว่าคุณภาพหลุด — ผู้ใช้ต้องแก้มากกว่า 1 ใน 3 ใบ</summary>
    public const decimal AcceptRateFloor = 0.65m;

    /// <summary>จำนวนสแกนขั้นต่ำที่ทำให้ตัวเลขมีความหมาย — น้อยกว่านี้คือ noise
    /// (ห้ามตัดสินว่า "ถอยหลัง" จากกลุ่มตัวอย่าง 3 ใบ)</summary>
    public const int MinSamples = 20;

    /// <param name="totalScans">สแกนทั้งหมดในช่วงเวลา</param>
    /// <param name="aiBilledScans">สแกนที่มีการเรียก provider จริง (เสียเงิน)</param>
    /// <param name="documentsCreated">สแกนที่กลายเป็นเอกสาร</param>
    /// <param name="correctedScans">สแกนที่ผู้ใช้แก้ค่าอย่างน้อยหนึ่งช่อง (ก่อนหรือหลังสร้าง)</param>
    public static OcrKpiReading Read(int totalScans, int aiBilledScans, int documentsCreated, int correctedScans)
    {
        if (totalScans <= 0)
            return new(0m, 0m, "ยังไม่มีสแกนในช่วงนี้", false);

        var aiRate = Ratio(aiBilledScans, totalScans);
        // "ผ่านรอบแรก" = กลายเป็นเอกสาร **และ** ไม่ถูกแก้เลย — วัดจากฐานสแกนทั้งหมด
        // เพราะสแกนที่ผู้ใช้ทิ้งไปเพราะเติมมาผิดจนใช้ไม่ได้ ก็คือความล้มเหลวเหมือนกัน
        var accepted = Math.Max(0, documentsCreated - correctedScans);
        var acceptRate = Ratio(accepted, totalScans);

        if (totalScans < MinSamples)
            return new(aiRate, acceptRate, $"ตัวอย่างยังน้อย ({totalScans} ใบ) — ยังตัดสินไม่ได้", false);

        if (acceptRate < AcceptRateFloor)
            return new(aiRate, acceptRate,
                aiRate < 0.20m
                    // ทั้งเรียก AI น้อย ทั้งผู้ใช้ต้องแก้เยอะ = อาการของ "ประหยัดโดยโง่ลง"
                    ? "⚠️ เรียก AI น้อยแต่ผู้ใช้ต้องแก้เยอะ — ตรวจว่าคำตอบของนักเรียนถูกใช้จริงไหม (ไม่ใช่ประหยัดเพราะโค้ดทิ้งคำตอบ)"
                    : "⚠️ ผู้ใช้ต้องแก้เกิน 1 ใน 3 ใบ — คุณภาพการเติมยังไม่ถึงเป้า 1-click",
                true);

        return new(aiRate, acceptRate,
            aiRate <= 0.30m
                ? "✅ นักเรียนรับงานส่วนใหญ่แล้วโดยคุณภาพยังอยู่ — ตรงเป้ากฎเหล็ก #1"
                : "กำลังสอนอยู่ — ยังพึ่ง AI เป็นหลัก แต่คุณภาพผ่านเกณฑ์",
            false);
    }

    private static decimal Ratio(int part, int whole)
        => whole <= 0 ? 0m : Math.Round((decimal)part / whole, 4, MidpointRounding.AwayFromZero);
}

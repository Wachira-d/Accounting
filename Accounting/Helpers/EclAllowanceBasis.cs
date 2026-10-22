namespace Accounting.Helpers;

/// <summary>
/// **ECL: ฐานยอดค่าเผื่อเปลี่ยนจากการนับคู่กลับรายการ — ต้องดัง ไม่ใช่ลงเงียบ** (pure · ทีม O 2026-10-22 · ฝ่ายค้านข้อ 3 · หลักการ #7)
///
/// <para>เดิม <c>EclAllowanceJob</c> อ่านยอด 18100 จาก Posted อย่างเดียว ⇒ บริษัทที่เคยกลับรายการใบ ECL มียอดค่าเผื่อ "ผิดเท่ายอดใบเดิมที่ถูกกลับ" ·
/// หลังแก้ (<see cref="ReportableJournal"/>) รอบแรกหลัง deploy job จะลงยอดปรับปรุงต่างจากเดิมเท่าผลต่างนั้น ⇒ ต้องบอกในคำอธิบาย JE ที่ผู้ใช้เปิดดู
/// + แจ้งเจ้าของบริษัท (ครั้งเดียว — ดู <see cref="Marker"/>)</para>
/// </summary>
public static class EclAllowanceBasis
{
    /// <summary>ข้อความกำกับในคำอธิบาย JE — ใบ ECL ใดมีคำนี้แล้ว = แจ้งไปแล้ว ไม่แจ้งซ้ำทุกเดือน</summary>
    public const string Marker = "[ฐานค่าเผื่อนับคู่กลับรายการ]";

    /// <summary>ผลต่างต่ำกว่านี้ (บาท) ไม่ถือว่าฐานเปลี่ยน — เกณฑ์เดียวกับเกณฑ์ลง JE ของ job (1 บาท)</summary>
    public const decimal Threshold = 1m;

    /// <summary>
    /// ข้อความอธิบายเมื่อฐานเปลี่ยน — null = ไม่ต่าง (ไม่เคยกลับรายการใบที่แตะ 18100)
    /// </summary>
    /// <param name="currentAllowance">ยอดค่าเผื่อตามกติกาใหม่ (Posted + Reversed)</param>
    /// <param name="reversedOriginalsNet">ยอด Cr−Dr ของ 18100 บนใบเดิมที่ถูกกลับรายการ (สถานะ Reversed) — ส่วนที่ฐานเดิมตกหล่น</param>
    public static string? ChangeNote(decimal currentAllowance, decimal reversedOriginalsNet)
    {
        if (Math.Abs(reversedOriginalsNet) < Threshold) return null;
        var legacy = currentAllowance - reversedOriginalsNet;
        return $"{Marker} ยอดค่าเผื่อเดิมคำนวณ {legacy:N2} (ไม่นับใบที่ถูกกลับรายการ) · ฐานที่ถูกต้อง {currentAllowance:N2} · "
            + $"ยอดปรับปรุงรอบนี้จึงต่างจากวิธีเดิม {(-reversedOriginalsNet):N2} บาท — ตรวจใบที่กลับรายการในผัง 18100 ก่อนปิดงวด";
    }
}

namespace Accounting.Helpers;

/// <summary>
/// **จำนวนวันที่ขยายช่วงทดลองใช้ได้ต่อครั้ง** — ฝ่ายค้านรอบ 193 W-C3
///
/// <para>═══ บั๊กจริง ═══ <c>SubscriptionService.ExtendTrialAsync</c> ใช้ <c>request.AdditionalDays</c> ตรง ๆ เมื่อ &gt; 0 ⇒
/// ผู้เรียกฝั่งลูกค้าขยาย trial ครั้งเดียว 36,500 วันได้ (<c>MaxExtensions</c> จำกัดแค่จำนวนครั้ง) · ประกอบกับ endpoint ไม่มีด่านเจ้าของ
/// ⇒ พนักงานคนไหนก็ได้ใช้ระบบฟรีตลอดไป</para>
///
/// <para>กติกา: ลูกค้า — ไม่ส่ง/0 = ค่าที่แพลตฟอร์มตั้ง (<c>TrialConfig.ExtensionDays</c>) · ส่งมาเกินค่านั้น = ปฏิเสธด้วยข้อความไทย
/// (ไม่ตัดเงียบ — ผู้ใช้ต้องรู้ว่าได้กี่วัน) · ผู้ดูแลแพลตฟอร์ม — กำหนดเองได้ (หน้า admin ใช้ต่อเวลาเป็นกรณีพิเศษ)</para>
/// </summary>
public static class TrialExtensionPolicy
{
    public const string RuleCode = "SUB-TRIAL-EXTEND-CAP";

    public static int ResolveDays(int requestedDays, int configuredDays, bool allowCustomDays)
    {
        if (allowCustomDays)
            return requestedDays > 0 ? requestedDays : configuredDays;
        if (requestedDays <= 0) return configuredDays;
        if (requestedDays > configuredDays)
            throw new BusinessRuleException(
                $"ขยายเวลาทดลองใช้ได้ไม่เกิน {configuredDays} วันต่อครั้ง — ถ้าต้องการมากกว่านี้ กรุณาติดต่อผู้ดูแลระบบ",
                RuleCode);
        return requestedDays;
    }
}

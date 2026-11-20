namespace Accounting.Helpers;

/// <summary>
/// **โต๊ะสลิปภาษีของพนักงาน — ตัวตัดสินเดียว** (pure · รอบ 275 แพ็ก D · ผลตรวจ T1 H1/M2 · คำตัดสินแทนเจ้าของ 4.3):
/// <list type="number">
/// <item><see cref="CandidateBlocker"/> — ผู้สมัครที่ตัวตัดสินคืนแต่ <c>ApplyAsync</c> จะปฏิเสธแน่นอน (ยอดสลิปไม่ตรง · ใบขอชำระปิดแล้ว · งวดนำส่งแล้ว) ⇒ โต๊ะไม่แสดงปุ่ม
/// และ server ตอบ 409 โดยไม่ส่งการ์ดเข้ากลุ่ม (M2)</item>
/// <item><see cref="ManualProblem"/> — ทางบันทึกนำส่งด้วยมือจากสลิปที่ไม่มีผู้สมัคร (ปกส. · ธนาคารอื่น · จ่ายช้า · OCR ล่ม · H1): พนักงานเลือกแบบ/งวด/ยอดเอง
/// แต่ <b>ภาษี + เงินเพิ่ม + เบี้ยปรับ ต้องเท่ายอดบนสลิปทุกสตางค์</b> (คำตัดสิน 4.3 · ไม่ต้องมีผู้อนุมัติคนที่สอง — เท่ากับทางนำส่งด้วยมือเดิม)</item>
/// <item><see cref="LinkProblem"/> — ปิดสลิปด้วยรายการนำส่งที่บันทึกไว้แล้ว (บันทึกมือที่หน้านำส่งของลูกค้าก่อน) แทน "ไม่บันทึก" ที่ส่งการ์ดผิดความจริง</item>
/// </list>
/// ไม่เรียก AI · ทุกด่านเลขล้วนตรงตัว (F2 ข้อ 3)
/// </summary>
public static class TaxSlipDeskRule
{
    public const string ManualRuleCode = "TAX-SLIP-MANUAL";
    public const string LinkRuleCode = "TAX-SLIP-LINKED";
    public const string BlockedRuleCode = "TAX-SLIP-BLOCKED";
    public const string LegalReference = TaxSlipMatch.LegalReference;

    public static bool UsedAi => false;

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>แบบที่นำส่งได้หลายครั้งต่องวด (ภ.พ.36 คำตัดสินข้อ 133 · กท.20ก รอบ 228) — "งวดนี้นำส่งแล้ว" ไม่ใช่เหตุปฏิเสธ · ใบขอชำระของงวดไม่ปิดเองเมื่อมีการนำส่ง</summary>
    public static bool MultiPerPeriod(string type) => type is "VatPp36" || type == WorkersCompensationBase.RemittanceType;

    /// <summary>ผู้สมัครนี้กดแล้วล้มแน่นอนไหม · null = กดได้ (ยังต้องผ่านความยินยอม/ยอดสมุดใต้ล็อก)</summary>
    /// <param name="slipAmount">ยอดบนสลิป (null = อ่านไม่ได้ — ไม่ใช่เหตุบล็อกที่นี่ · ทางมือบังคับให้พนักงานกรอก)</param>
    /// <param name="requestStatus">สถานะใบขอชำระของผู้สมัคร (null = ไม่มีใบ/หาไม่พบ)</param>
    /// <param name="periodRemitted">งวดนี้มีรายการนำส่งที่ยังไม่ยกเลิกแล้ว</param>
    public static string? CandidateBlocker(decimal? slipAmount, TaxSlipMatch.Candidate c, string? requestStatus, bool periodRemitted)
    {
        var total = c.Amount + c.Surcharge + c.Penalty;
        if (slipAmount is decimal paid && R(paid) != R(total))
            return $"ยอดบนสลิป {paid:N2} ไม่ตรงรายการนี้ {total:N2} — ใช้ “บันทึกเอง” แล้วกรอกเงินเพิ่ม/เบี้ยปรับให้ครบยอดสลิป";
        if (c.RequestId != null && requestStatus != TaxPaymentRequestLifecycle.Open)
            return "ใบขอชำระของรายการนี้ไม่ได้เปิดอยู่แล้ว (ชำระ/ยกเลิก/หมดอายุ)";
        if (periodRemitted && !MultiPerPeriod(c.Type))
            return "งวดนี้มีบันทึกนำส่งแล้ว — ใช้ “ผูกกับรายการนำส่งที่มีอยู่” แทน";
        return null;
    }

    /// <summary>ค่าที่พนักงานกรอกในทางมือถูกรูปไหม · null = ผ่าน</summary>
    /// <param name="slipAmount">ยอดบนสลิปที่ระบบอ่านได้ (null = อ่านไม่ได้ ⇒ ต้องมี <paramref name="staffSlipAmount"/>)</param>
    /// <param name="staffSlipAmount">ยอดบนสลิปที่พนักงานอ่านเอง (ใช้เฉพาะเมื่อระบบอ่านไม่ได้ · audit)</param>
    /// <param name="payDate">วันที่จ่ายที่จะลงบัญชี (ปฏิทินไทย)</param>
    /// <param name="todayThai">วันนี้ตามปฏิทินไทย</param>
    /// <param name="slipTaxIdOtherCompany">เลขผู้เสียภาษีบนสลิป (แบบปิดบัง) อ่านได้และไม่ตรงบริษัทที่เลือก</param>
    public static string? ManualProblem(string type, decimal tax, decimal surcharge, decimal penalty, decimal? slipAmount, decimal? staffSlipAmount,
        DateTime? payDate, DateTime todayThai, bool slipTaxIdOtherCompany = false)
    {
        if (!ProviderConsent.TaxTypes.Any(t => t.Type == type)) return "แบบนำส่งนี้ไม่อยู่ในรายการที่โต๊ะสลิปบันทึกได้";
        if (slipTaxIdOtherCompany) return "เลขผู้เสียภาษีบนสลิปไม่ตรงบริษัทที่เลือก — สลิปนี้อาจเป็นของบริษัทอื่น";
        if (tax <= 0m) return "ยอดภาษีต้องมากกว่า 0";
        if (surcharge < 0m || penalty < 0m) return "เงินเพิ่ม/เบี้ยปรับติดลบไม่ได้";
        var paid = slipAmount ?? staffSlipAmount;
        if (paid is not decimal p || p <= 0m) return "ระบบอ่านยอดบนสลิปไม่ได้ — กรอกยอดบนสลิปที่อ่านเอง (ระบบตรวจกับยอดที่บันทึกทุกสตางค์)";
        var total = tax + surcharge + penalty;
        if (R(p) != R(total))
            return $"ภาษี + เงินเพิ่ม + เบี้ยปรับ = {total:N2} ไม่ตรงยอดบนสลิป {p:N2} — ไม่บันทึก (ยอดที่ลงบัญชีต้องเป็นยอดที่สลิปจ่ายทุกสตางค์)";
        if (payDate is not DateTime d) return "ไม่มีวันที่จ่ายบนสลิป — กรอกวันที่จ่ายตามสลิป";
        if (d.Date > todayThai.Date) return "วันที่จ่ายอยู่ในอนาคต — ตรวจวันที่บนสลิป";
        return null;
    }

    /// <summary>ปิดสลิปด้วยรายการนำส่งที่มีอยู่ได้ไหม · null = ได้</summary>
    /// <param name="remittanceTotal">ภาษี + เงินเพิ่ม + เบี้ยปรับ + ค่าปรับ ของรายการนำส่ง</param>
    /// <param name="linkedSlipId">รายการนำส่งผูกสลิปอื่นอยู่แล้ว</param>
    public static string? LinkProblem(decimal? slipAmount, decimal remittanceTotal, bool remittanceDeleted, Guid? linkedSlipId)
    {
        if (remittanceDeleted) return "รายการนำส่งนี้ถูกยกเลิกแล้ว";
        if (linkedSlipId != null) return "รายการนำส่งนี้ผูกกับสลิปอื่นแล้ว";
        if (slipAmount is not decimal p) return "ระบบอ่านยอดบนสลิปไม่ได้ — ผูกได้เมื่อรู้ยอดบนสลิป (ใช้ “บันทึกเอง” แล้วกรอกยอดที่อ่าน)";
        if (R(p) != R(remittanceTotal)) return $"ยอดบนสลิป {p:N2} ไม่ตรงยอดรายการนำส่ง {remittanceTotal:N2}";
        return null;
    }
}

/// <summary>
/// **ไฟล์สลิปเดิมส่งจากช่องทางใหม่ — ตัวตัดสินเดียว** (pure · รอบ 275 แพ็ก D · ผลตรวจ T1 M3): กันซ้ำด้วยแฮชไฟล์ทั้งบริษัทผู้ให้บริการทำให้สลิปที่ส่ง<b>ผิดกลุ่มก่อน</b>
/// ติดอยู่กับกลุ่มแรก (TaxIdNotInGroup ⇒ คิวพนักงาน) และกลุ่มที่ถูกได้แค่ "ได้รับไปแล้ว" · แถวเดิมยังรอพนักงาน + ช่องทางชนิดเดียวกันแต่คนละกลุ่ม/ผู้ส่ง ⇒
/// ประมวลแถวเดิมใหม่ด้วยชุดบริษัทของช่องทางใหม่ (ยังเดินทุกด่าน — เลขภาษีต้องตรงบริษัทที่ช่องทางใหม่ผูก) · แถวที่บันทึกแล้ว/รอลูกค้า/ปิดแล้ว ⇒ ไม่แตะ
/// </summary>
public static class TaxSlipChannelRedirect
{
    public static bool ShouldReprocess(string existingStatus, string existingSource, string existingSourceKey, string newSource, string newSourceKey)
        => existingStatus == "ToStaff" && existingSource == newSource && !string.Equals(existingSourceKey, newSourceKey, StringComparison.Ordinal);
}

/// <summary>
/// **วงจรชีวิตใบขอชำระ (<c>TaxPaymentRequest</c>) — ตัวตั้งเดียว** (pure · รอบ 275 แพ็ก D · ผลตรวจ T1 M7): Open → Paid (สลิป/นำส่งงวดนั้นทางใดก็ได้) ·
/// Cancelled (พนักงานยกเลิก + การ์ดแจ้งกลุ่ม) · Expired (Control Code หมดอายุ — งานเบื้องหลัง) · ยกเลิกรายการนำส่ง ⇒ กลับ Open (หรือ Expired ถ้าหมดอายุแล้ว)
/// </summary>
public static class TaxPaymentRequestLifecycle
{
    public const string Open = "Open";
    public const string Paid = "Paid";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";

    /// <summary>นำส่งงวดนี้แล้ว (เส้นใดก็ได้) ⇒ ใบที่เปิดของงวดนั้นปิดเป็น Paid · แบบที่นำส่งหลายครั้งต่องวดไม่ปิดเอง</summary>
    public static bool ClosesOnRemit(string type) => !TaxSlipDeskRule.MultiPerPeriod(type);

    /// <summary>Control Code หมดอายุแล้วหรือยัง — วันหมดอายุ (ปฏิทินไทย) ผ่านไปแล้ว · null = ไม่มีวันหมดอายุ</summary>
    public static bool IsExpired(DateTime? expiresAt, DateTime nowUtc) => expiresAt is DateTime e && ThaiDate.CalendarDateUtc(nowUtc).Date > e.Date;

    /// <summary>สถานะหลังรายการนำส่งที่ปิดใบนี้ถูกยกเลิก</summary>
    public static string StatusAfterVoid(DateTime? expiresAt, DateTime nowUtc) => IsExpired(expiresAt, nowUtc) ? Expired : Open;

    public static string Label(string status) => status switch
    {
        Open => "รอชำระ",
        Paid => "ชำระแล้ว",
        Cancelled => "ยกเลิก",
        Expired => "หมดอายุ",
        _ => status,
    };
}

/// <summary>
/// **อายุแถวสลิปที่รอพนักงานตัดสิน — ตัวตัดสินเดียว** (pure · รอบ 275 แพ็ก D · ผลตรวจ T1 M4 · คำตัดสิน 275-D-3):
/// รอเกิน <see cref="RemindAfterDays"/> วัน ⇒ กระดิ่งเตือนซ้ำวันละครั้ง · รอเกิน <see cref="ExpireAfterDays"/> วัน ⇒ ปิดเป็น <c>Expired</c> + การ์ดบอกลูกค้าตามจริง ·
/// แถวที่ปิดแล้ว (ไม่บันทึก/หมดอายุ) ลบไฟล์เมื่อครบ <c>ClosedFileRetentionDays</c> (ไม่ลบทันทีตอนปิด — หลักฐานอยู่ครบช่วงที่ลูกค้า/พนักงานอาจโต้แย้ง)
/// </summary>
public static class TaxSlipOpenAge
{
    public const int RemindAfterDays = 2;
    public const int RemindEveryHours = 24;
    public const int ExpireAfterDays = 90;
    public const string ExpiredRuleCode = "TAX-SLIP-OPEN-EXPIRED";

    public enum AgeAction { None, Remind, Expire }

    /// <param name="status">สถานะแถว — ครอบเฉพาะ ToStaff (ตัวอื่นมีนาฬิกาของตัวเอง: รอความยินยอม 30 วัน · รอเลือก 24 ชม. · Received ค้างประมวลใหม่)</param>
    public static AgeAction Decide(string status, DateTime createdAtUtc, DateTime? remindedAtUtc, DateTime nowUtc)
    {
        if (status != "ToStaff") return AgeAction.None;
        if (createdAtUtc.AddDays(ExpireAfterDays) < nowUtc) return AgeAction.Expire;
        if (createdAtUtc.AddDays(RemindAfterDays) > nowUtc) return AgeAction.None;
        return remindedAtUtc is DateTime r && r.AddHours(RemindEveryHours) > nowUtc ? AgeAction.None : AgeAction.Remind;
    }
}

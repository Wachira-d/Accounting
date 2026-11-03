namespace Accounting.Services.Payments.Providers;

/// <summary>
/// รอบ 227 (PLATFORM_OMISE_AUDIT A-10) — ข้อความไทยของ "เหตุที่ charge ไม่สำเร็จ" ของ Omise · pure · ตัวเดียวของ adapter
/// (ทั้ง <c>failure_code</c> บน charge ที่ HTTP 200 แต่สถานะล้มเหลว และ <c>code</c> ของคำตอบ error)
/// <para><b>ที่มา:</b> เดิม <c>ParseCharge</c> เก็บ <c>failure_message</c> ภาษาอังกฤษของผู้ให้บริการตรง ๆ ⇒ ผู้เช่า/ลูกค้าเห็น "insufficient funds in the account"
/// บนหน้าจ่ายเงินภาษาไทย · และ <c>FriendlyError</c> รู้จักแค่ 4 รหัส · รหัสที่ไม่รู้จัก ⇒ คืนข้อความต้นทางพร้อมรหัส (ไม่เดาสาเหตุแทน — บทเรียน CSP/SSO)</para>
/// <para>รายชื่อรหัสมาจากเอกสารสาธารณะของผู้ให้บริการ (charge failure codes) — ยืนยันกับ sandbox ตอนตั้งค่าจริง · รหัสใหม่ที่ไม่อยู่ในตาราง = ข้อความต้นทาง</para>
/// </summary>
public static class OmiseFailureText
{
    private static readonly IReadOnlyDictionary<string, string> Thai = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // ── บัตร ──
        ["insufficient_fund"] = "ยอดเงินในบัญชี/วงเงินบัตรไม่พอ",
        ["insufficient_balance"] = "ยอดเงินในบัญชีไม่พอ",
        ["stolen_or_lost_card"] = "บัตรถูกแจ้งหาย/ถูกขโมย — ธนาคารปฏิเสธรายการ",
        ["failed_processing"] = "ธนาคารประมวลผลรายการไม่สำเร็จ — ลองใหม่อีกครั้งหรือใช้วิธีชำระอื่น",
        ["payment_rejected"] = "ธนาคารผู้ออกบัตรปฏิเสธรายการนี้",
        ["invalid_security_code"] = "รหัสความปลอดภัยหลังบัตร (CVV) ไม่ถูกต้อง",
        ["failed_fraud_check"] = "รายการไม่ผ่านการตรวจสอบความเสี่ยง — ติดต่อธนาคารผู้ออกบัตร",
        ["invalid_account_number"] = "เลขบัญชี/เลขบัตรไม่ถูกต้อง",
        ["invalid_card"] = "ข้อมูลบัตรไม่ถูกต้อง",
        ["confirmed_amount_mismatch"] = "ยอดที่ยืนยันไม่ตรงกับยอดที่เรียกเก็บ",
        ["timeout"] = "ไม่ได้ทำรายการให้เสร็จภายในเวลาที่กำหนด — เริ่มชำระใหม่",
        ["payment_cancelled"] = "ผู้ชำระยกเลิกรายการ",
        ["payment_expired"] = "รายการหมดอายุก่อนชำระ — เริ่มชำระใหม่",
        ["authentication_failed"] = "ยืนยันตัวตน 3-D Secure ไม่สำเร็จ",
        // ── error ระดับ API (HTTP ไม่สำเร็จ) ──
        ["authentication_failure"] = "คีย์ไม่ถูกต้องหรือหมดอายุ — ตรวจคีย์ในหน้าตั้งค่าการรับชำระเงิน",
        ["used_token"] = "โทเคนบัตรถูกใช้ไปแล้ว — กรอกข้อมูลบัตรใหม่",
        ["invalid_charge"] = "คำขอเรียกเก็บเงินไม่ถูกต้อง (ยอด/สกุลเงิน/วิธีชำระ) — ตรวจการตั้งค่าวิธีชำระ",
        ["not_found"] = "ไม่พบรายการที่ผู้ให้บริการ",
        ["service_not_found"] = "บัญชีผู้ขายยังไม่ได้เปิดใช้วิธีชำระนี้กับผู้ให้บริการ",
        ["bad_request"] = "คำขอไม่ถูกต้อง",
        ["rate_limit_exceeded"] = "เรียกผู้ให้บริการถี่เกินไป — รอสักครู่แล้วลองใหม่",
    };

    /// <summary>ข้อความไทย — รู้จักรหัส ⇒ ข้อความไทย · ไม่รู้จัก ⇒ ข้อความต้นทาง (+ รหัส) · ไม่มีทั้งคู่ ⇒ null</summary>
    public static string? Describe(string? code, string? providerMessage)
    {
        if (!string.IsNullOrWhiteSpace(code) && Thai.TryGetValue(code.Trim(), out var th)) return th;
        if (string.IsNullOrWhiteSpace(providerMessage))
            return string.IsNullOrWhiteSpace(code) ? null : $"ผู้ให้บริการปฏิเสธรายการ (รหัส {code.Trim()})";
        return string.IsNullOrWhiteSpace(code)
            ? providerMessage.Trim()
            : $"{providerMessage.Trim()} (รหัส {code.Trim()})";
    }

    /// <summary>รหัสนี้มีข้อความไทยไหม (เทสต์/หน้าแอดมิน)</summary>
    public static bool IsKnown(string? code) => !string.IsNullOrWhiteSpace(code) && Thai.ContainsKey(code.Trim());
}

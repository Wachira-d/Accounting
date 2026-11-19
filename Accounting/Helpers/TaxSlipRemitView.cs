namespace Accounting.Helpers;

/// <summary>
/// รอบ 272 (ดีไซน์ §9.3 · คำตัดสิน 272-1-4 = D-4) — ป้ายของแถวนำส่งที่บันทึกจากสลิปภาษี (server computes · page displays) · pure ·
/// สลิป<b>ไม่ใช่เลขรับการยื่นแบบ</b> ⇒ แถวที่ยังไม่มีเลขรับขึ้น "ชำระแล้ว · ยังไม่ได้บันทึกเลขรับ" (ไม่ยกรายงานเป็นยื่นแล้ว · ไม่ล็อกงวด)
/// </summary>
public static class TaxSlipRemitView
{
    public const string SourceSlip = "Slip";
    public const string NoFilingNumber = "ชำระแล้ว · ยังไม่ได้บันทึกเลขรับ";

    public static class Review
    {
        public const string StatementFound = "StatementFound";
        public const string OtherAccount = "OtherAccount";
        public const string NotFound = "NotFound";
    }

    /// <summary>ป้ายที่มา · null = ไม่ใช่แถวจากสลิป</summary>
    public static string? SourceLabel(string? source, string? controlLast4)
        => source == SourceSlip ? "ยืนยันจากสลิป · NextAcc" + (string.IsNullOrEmpty(controlLast4) ? "" : " · Control ••" + controlLast4) : null;

    /// <summary>บรรทัดรองเรื่องเลขรับ (D-4) — เฉพาะแถวจากสลิปที่ยังไม่มีเลขรับ</summary>
    public static string? FilingNote(string? source, string? filingNumber)
        => source == SourceSlip && string.IsNullOrWhiteSpace(filingNumber) ? NoFilingNumber : null;

    /// <summary>ธงตรวจทานหลังนำส่ง (P5) — null = ไม่ใช่แถวจากสลิป</summary>
    public static string? ReviewLabel(string? source, string? reviewStatus) => source != SourceSlip ? null : reviewStatus switch
    {
        Review.StatementFound => "รายการเดินบัญชียืนยันแล้ว ✓",
        Review.OtherAccount => "⚠ พบยอดนี้ในบัญชีอื่น — ตรวจบัญชีที่จ่าย",
        Review.NotFound => "⚠ ยังไม่พบในรายการเดินบัญชี — ตรวจบัญชีที่จ่าย",
        _ => "รอตรวจกับรายการเดินบัญชี",
    };
}

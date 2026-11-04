namespace Accounting.Helpers;

/// <summary>
/// รอบ 235 (ฝ่ายค้าน S2 · DECISIONS 235-X1) — ตัวตัดสินเดียวของงาน "ซ่อมวันที่ศักราชปนที่ persist แล้ว" (<c>AccountingService.RepairBuddhistDatesAsync</c>) ว่าแถวใด
/// <b>ห้ามแก้ตรง</b>: ปีบัญชี/งวดที่ปิดแล้ว · เอกสารที่ออกเลขแล้ว (§86/4 ห้ามแก้ย้อนหลัง) · รายการในแบบภาษีที่ยื่นแล้ว (§87 ห้ามแก้โดยไม่ขีดฆ่า) ⇒
/// นับเป็น "ข้าม (ต้องแก้ด้วยเอกสารปรับปรุง)" พร้อมเหตุผล · ไม่แก้เลขเอกสารใด ๆ · การแปลงศักราชใช้ <see cref="ThaiDate.TryNormalizeEra"/> ตัวเดียว
/// </summary>
public static class BuddhistDateRepairPolicy
{
    public const string RuleCode = "DATE-ERA-REPAIR";

    public enum Skip
    {
        None = 0,
        /// <summary>วันที่ที่แก้แล้วตกในงวดที่ปิด/ล็อก</summary>
        ClosedPeriod = 1,
        /// <summary>เอกสารออกเลขแล้ว (ไม่ใช่ <c>DRAFT-</c>)</summary>
        NumberIssued = 2,
        /// <summary>บรรทัดของรายงานภาษีที่ยื่น/ส่งแล้ว</summary>
        TaxReportFiled = 3,
    }

    /// <summary>ผลการซ่อม — <see cref="SkippedByReason"/> ใช้ข้อความของ <see cref="Message"/> เป็นคีย์</summary>
    public sealed record Result(int Fixed, int Unresolved, int Skipped, IReadOnlyDictionary<string, int> SkippedByReason);

    /// <summary>ลำดับความสำคัญ: แบบภาษียื่นแล้ว &gt; ออกเลขแล้ว &gt; งวดปิด</summary>
    public static Skip Decide(bool inClosedPeriod, bool numberIssued, bool taxReportFiled)
        => taxReportFiled ? Skip.TaxReportFiled : numberIssued ? Skip.NumberIssued : inClosedPeriod ? Skip.ClosedPeriod : Skip.None;

    /// <summary>เลขเอกสารที่ออกแล้ว = มีค่าและไม่ใช่ placeholder ร่าง <c>DRAFT-</c></summary>
    public static bool IsNumberIssued(string? documentNumber)
        => !string.IsNullOrWhiteSpace(documentNumber) && !documentNumber.StartsWith("DRAFT-", StringComparison.Ordinal);

    /// <summary>วันที่ (หลังแปลง) ตกในช่วงงวดที่ปิด/ล็อกช่วงใดช่วงหนึ่ง (เทียบเฉพาะวันที่)</summary>
    public static bool InClosedPeriod(DateTime corrected, IEnumerable<(DateTime Start, DateTime End)> closedPeriods)
    {
        var d = corrected.Date;
        return closedPeriods.Any(p => d >= p.Start.Date && d <= p.End.Date);
    }

    public static string Message(Skip reason) => reason switch
    {
        Skip.ClosedPeriod => "อยู่ในงวดที่ปิด/ล็อกแล้ว — ต้องแก้ด้วยเอกสารปรับปรุง (หรือเปิดงวดตามขั้นตอน)",
        Skip.NumberIssued => "เอกสารออกเลขแล้ว — ห้ามแก้ย้อนหลัง (§86/4) ต้องยกเลิกแล้วออกใบใหม่/เอกสารปรับปรุง",
        Skip.TaxReportFiled => "อยู่ในรายงานภาษีที่ยื่นแล้ว — ห้ามแก้ (§87) ต้องยื่นเพิ่มเติม/ปรับปรุงงวดถัดไป",
        _ => "",
    };
}

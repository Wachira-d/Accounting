namespace Accounting.Helpers;

/// <summary>
/// ชุดค่าที่ใช้ได้ของ "วิธีเรียกเก็บเงิน" และ "วิธีรับรู้รายได้" ของโครงการ — ตัวตั้งตัวเดียว
/// (F2 ข้อ 4) ที่ทุกทางเข้าต้องเดินผ่าน: ฟอร์ม <c>projects.html</c> → <c>CreateProjectRequest</c>
/// และการนำเข้าไฟล์ (<c>ImportExportService.ImportProjectAsync</c>)
///
/// <para><b>ที่มา (รอบ 193 · A01):</b> <c>CreateProjectRequest</c> เคยประกาศ
/// <c>string BillingMethod, string RevenueRecognitionMethod</c> (non-nullable ⇒ ASP.NET ใส่
/// <c>[Required]</c> โดยปริยาย) ขณะที่ฟอร์มไม่มีทั้งสองช่อง ⇒ "สร้างโครงการ" ไม่สำเร็จ<b>ทุกครั้ง</b>.
/// แก้สองชั้น: ฟอร์มมี dropdown ป้ายไทยให้ผู้ใช้เลือก (เป็นนโยบายบัญชี ไม่ควรเดาแทน) และ
/// API ที่ไม่ส่งมา (partner/สคริปต์) ได้ค่าเริ่มต้นเดียวกับ entity — ค่านอกชุดถูกปฏิเสธเป็นไทย
/// แทนที่จะถูกเก็บเงียบ ๆ</para>
///
/// <para>วิธีรับรู้รายได้ตาม TFRS for NPAEs บทที่ 6 (งานบริการ): ตามขั้นความสำเร็จของงาน
/// และเมื่อประมาณผลงานไม่ได้อย่างน่าเชื่อถือ ให้รับรู้เท่าต้นทุนที่คาดว่าจะได้คืน —
/// <b>ไม่มี</b> "รับรู้เมื่องานเสร็จ" (completed contract) ในชุดนี้โดยตั้งใจ.
/// หมายเหตุ: ณ รอบ 193 ค่านี้<b>ถูกเก็บและแสดงเท่านั้น</b> ยังไม่มีเส้นลงบัญชีใดอ่าน</para>
/// </summary>
public static class ProjectContractMethods
{
    public const string DefaultBilling = "FixedPrice";
    public const string DefaultRevenueRecognition = "PercentageOfCompletion";

    /// <summary>ค่า → ป้ายไทย (ลำดับ = ลำดับใน dropdown)</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Billing = new[]
    {
        ("FixedPrice", "ราคาเหมา"),
        ("TimeAndMaterial", "ตามเวลาและวัสดุที่ใช้จริง"),
        ("Milestone", "ตามงวดงาน (Milestone)"),
    };

    public static readonly IReadOnlyList<(string Value, string Label)> RevenueRecognition = new[]
    {
        ("PercentageOfCompletion", "ตามขั้นความสำเร็จของงาน"),
        ("CostRecovery", "เท่าต้นทุนที่คาดว่าจะได้คืน (ประมาณผลงานไม่ได้)"),
    };

    /// <summary>ว่าง/null → ค่าเริ่มต้น · ค่าในชุด (ไม่สนตัวพิมพ์) → ค่ามาตรฐาน · นอกชุด → throw ไทย</summary>
    public static string NormalizeBilling(string? value)
        => Normalize(value, Billing, DefaultBilling, "วิธีเรียกเก็บเงิน");

    public static string NormalizeRevenueRecognition(string? value)
        => Normalize(value, RevenueRecognition, DefaultRevenueRecognition, "วิธีรับรู้รายได้");

    private static string Normalize(string? value, IReadOnlyList<(string Value, string Label)> set,
        string fallback, string fieldLabel)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var v = value.Trim();
        foreach (var (val, _) in set)
            if (string.Equals(val, v, StringComparison.OrdinalIgnoreCase)) return val;
        throw new BusinessRuleException(
            $"{fieldLabel} \"{v}\" ไม่อยู่ในรายการที่ระบบรองรับ — เลือกได้: " +
            string.Join(" · ", set.Select(s => $"{s.Label} ({s.Value})")));
    }
}

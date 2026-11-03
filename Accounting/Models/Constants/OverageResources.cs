namespace Accounting.Models.Constants;

/// <summary>
/// รอบ 231 (erp-review/2026-11-03/LICENSE_USAGE_OVERAGE_AUDIT.md §B.3 · คำตัดสินข้อ 231-2) — <b>ทรัพยากรที่คิด "ส่วนเกินโควตา" ได้ — closed list ที่เดียว</b>
///
/// <para>คิดส่วนเกินได้เฉพาะทรัพยากรแบบ<b>ใช้แล้วหมด/วัดเป็นปริมาณ</b>ที่มีมิเตอร์จริง (เอกสาร · หน้า OCR · ข้อความช่องกลาง · พื้นที่เป็น GB-เดือน) ·
/// ทรัพยากรแบบ "จำนวนชิ้น" (ที่นั่ง · เว็บ · ที่พัก · หน้า/สินค้าต่อเว็บ) <b>ไม่คิดส่วนเกิน</b> — ซื้อโควตาเสริมตาม <see cref="QuotaResources"/> เหมือนเดิม
/// (ส่วนเกินรายวันของ "ชิ้น" = ค่าที่ระบบประทับเองซึ่งผู้เช่าโต้แย้งได้ · F2 ข้อ 3)</para>
/// <para>รหัสมิเตอร์ = <c>overage.&lt;resource&gt;</c> (<see cref="FeatureCode"/>) · <c>documents.overage</c> เดิม (<c>AddOnCodes.DocumentsOverage</c>) ยังอ่านเป็นเอกสารได้ (<see cref="FromFeatureCode"/>)
/// แต่เขียนใหม่ใช้ <c>overage.documents</c> เท่านั้น</para>
/// </summary>
public static class OverageResources
{
    public const string Documents = "documents";
    public const string OcrPages = QuotaResources.OcrPagesMonth;
    public const string NotifyMessages = "notify.messages";
    public const string StorageGb = QuotaResources.StorageGb;

    public const string MeterPrefix = "overage.";

    /// <param name="Key">รหัสทรัพยากร (ใช้ในนโยบาย/ตารางราคา/snapshot)</param>
    /// <param name="Label">ชื่อไทยบนหน้าจอ/ใบแจ้งหนี้</param>
    /// <param name="Unit">หน่วยที่คิดเงิน (1 หน่วยราคา = 1 หน่วยนี้)</param>
    /// <param name="QuotaResourceKey">คีย์ใน <see cref="QuotaResources"/> ถ้ามี (ลิงก์ซื้อเพิ่ม) · null = ซื้อเพิ่มผ่านเส้นเดิมของทรัพยากรนั้น (แพ็กเอกสาร/แพ็กข้อความ)</param>
    /// <param name="Realtime">คิดต่อเหตุการณ์ ณ เวลาที่เกิด (true) หรือสรุปสิ้นเดือนจาก snapshot รายวัน (พื้นที่)</param>
    public sealed record Resource(string Key, string Label, string Unit, string? QuotaResourceKey, bool Realtime = true);

    public static readonly IReadOnlyList<Resource> All = new[]
    {
        new Resource(Documents, "เอกสาร", "ฉบับ", null),
        new Resource(OcrPages, "หน้าสแกน OCR", "หน้า", QuotaResources.OcrPagesMonth),
        new Resource(NotifyMessages, "ข้อความแจ้งเตือนผ่านช่องกลาง", "ข้อความ", null),
        new Resource(StorageGb, "พื้นที่จัดเก็บ", "GB-เดือน", QuotaResources.StorageGb, Realtime: false),
    };

    public static Resource? Find(string? key)
        => string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(r => r.Key == key.Trim());

    public static bool IsKnown(string? key) => Find(key) != null;

    /// <summary>รหัส <c>UsageEvent.FeatureCode</c> ของส่วนเกินทรัพยากรนี้ (เขียนใหม่ทุกจุดใช้รหัสนี้)</summary>
    public static string FeatureCode(string resourceKey) => MeterPrefix + resourceKey;

    /// <summary>ทรัพยากรจากรหัสมิเตอร์ — รับทั้ง <c>overage.*</c> และ <c>documents.overage</c> เดิม (อ่านย้อนหลังได้ · ไม่มีสำเนาที่สองตอนเขียน) · null = ไม่ใช่มิเตอร์ส่วนเกิน</summary>
    public static Resource? FromFeatureCode(string? featureCode)
    {
        if (string.IsNullOrWhiteSpace(featureCode)) return null;
        if (featureCode == AddOnCodes.DocumentsOverage) return Find(Documents);
        return featureCode.StartsWith(MeterPrefix, StringComparison.Ordinal) ? Find(featureCode[MeterPrefix.Length..]) : null;
    }

    /// <summary>ชื่อบรรทัดบนใบแจ้งหนี้ค่าใช้งาน (§86/4 — ชื่อสินค้า/บริการอ่านออก ไม่ใช่รหัสดิบ) · null = ไม่ใช่รหัสส่วนเกิน</summary>
    public static string? InvoiceName(string? featureCode)
        => FromFeatureCode(featureCode) is { } r ? $"ส่วนเกินโควตา{r.Label}" : null;

    /// <summary>ลิงก์ "ซื้อเพิ่ม/ดูการใช้งาน" ของทรัพยากรส่วนเกิน — เอกสาร/ข้อความไม่มีข้อเสนอใน QuotaResources ⇒ ชี้แท็บใช้ไปเท่าไร</summary>
    public static string BuyUrl(string resourceKey)
        => Find(resourceKey)?.QuotaResourceKey is string q ? QuotaResources.BuyUrl(q) : QuotaResources.UsageUrl;

    /// <summary>RuleCode ของการปฏิเสธเมื่อเปิดส่วนเกินไม่ได้ — หน้าเว็บ/ผู้เรียกอ่านเพื่อบอกทางไปต่อ</summary>
    public const string RuleCap = "OVERAGE-CAP", RuleNoRate = "OVERAGE-NO-RATE", RuleLocked = "OVERAGE-LOCKED", RuleNotEnabled = "OVERAGE-OFF";
}

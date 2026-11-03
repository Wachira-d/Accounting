namespace Accounting.Models.Constants;

/// <summary>
/// รอบ 229 (QUOTA_ADDON_SUBSCRIPTION_AUDIT §B.1/§B.2 · คำตัดสินข้อ 229-1..7) — <b>รายชื่อทรัพยากรที่มีเพดานและขายเพิ่มเป็นจำนวนได้ — ที่เดียว (closed list)</b>
///
/// <para><b>ทำไมต้องปิด:</b> เดิมแอดมินพิมพ์รหัสส่วนเสริมอะไรก็ได้ (<c>MeteringAdminController.UpsertFeature</c>) ⇒ ขายของที่ไม่มีผู้อ่าน
/// (บทเรียน <c>lodging.promo</c>) · ทรัพยากรที่อยู่ในรายการนี้ <b>ต้องมีด่านที่ถูกเรียกจริง</b> (ล็อกด้วย <c>tools/required_call_site_check.py</c>) —
/// ไม่มีด่าน = ไม่อยู่ในรายการ (สาขา · คลัง · POS · โดเมน · แบนด์วิดท์ ยังไม่อยู่ เพราะยังไม่มีด่าน/ตัวนับ)</para>
///
/// <para><b>นอกรายการโดยตั้งใจ:</b> การเรียก AI (กฎเหล็ก #1 — local ต้องแทนได้ 100%) · การจอง/ออเดอร์ (รายได้ของร้าน) · e-Tax (นับในเอกสาร) ·
/// รายการบัญชี/เดือน (คำตัดสินข้อ 229-7: เลิกแสดง ไม่บังคับ)</para>
/// </summary>
public static class QuotaResources
{
    public const string CmsSites = "cms.sites";
    public const string CmsPagesPerSite = "cms.pages-per-site";
    public const string CmsProductsPerSite = "cms.products-per-site";
    public const string Users = "users";
    public const string StorageGb = "storage.gb";
    public const string LodgingProperties = "lodging.properties";
    public const string OcrPagesMonth = "ocr.pages.month";

    /// <summary>Capacity = ของที่มีอยู่พร้อมกัน (ลบแล้วคืนช่อง) · Consumable = ใช้แล้วหมดต่องวด</summary>
    public enum ResourceKind { Capacity = 1, Consumable = 2 }

    /// <summary>ที่มาของเพดาน "ที่แพ็กเกจรวมให้" ก่อนมีตาราง allowance — ช่องเดิมของแพ็กเกจยังเป็นตัวตั้งของทรัพยากรนั้น (ไม่ทำสำเนาที่สอง · กฎ F2 ข้อ 4)</summary>
    public enum LegacySource
    {
        /// <summary>ไม่มีช่องเดิม ⇒ เพดานแพ็กเกจตั้งที่ตาราง <c>PlanQuotaAllowance</c> (ยังไม่ตั้ง = ไม่จำกัด ⇒ deploy แล้วไม่มีใครถูกบล็อก)</summary>
        None = 0,
        /// <summary><c>EffectivePlan.MaxUsers</c> (แพ็กเกจบริษัท หรือ License ชนะ) — แอดมินแก้ที่ /admin/plans.html ตามเดิม</summary>
        PlanMaxUsers = 1,
        /// <summary><c>EffectivePlan.MaxStorageBytes</c> (ปัดเป็น GB ลง) — แอดมินแก้ที่ /admin/plans.html ตามเดิม</summary>
        PlanMaxStorage = 2,
        /// <summary>โควตา OCR ของแพ็กเกจ (<c>OcrQuotaService</c>) — ซื้อเพิ่มเป็นเครดิต OCR (ตัวนับเดิม) ไม่ใช่ตัวนับใหม่</summary>
        PlanOcrPages = 3,
        /// <summary>ที่พักแห่งแรกฟรี (พฤติกรรมเดิม <c>LodgingPropertyQuota</c>) — ค่าตั้งต้น 1</summary>
        LodgingFirstFree = 4,
    }

    public sealed record Resource(string Key, string Label, string Unit, ResourceKind Kind, LegacySource Legacy,
        /// <summary>ต่อเว็บ (เพดานของแต่ละเว็บ — ซื้อเพิ่มไม่ได้ในรุ่นแรก · ตั้งได้ที่แพ็กเกจ/รายบริษัท/รายเว็บ)</summary>
        bool PerSite = false,
        /// <summary>ขายเป็นโควตาเสริมได้ (แอดมินสร้างข้อเสนอได้)</summary>
        bool Sellable = true,
        /// <summary>ข้อความ "เต็มแล้วทำอะไรได้" ฝั่งลบ — ทางไปต่อทางที่หนึ่ง</summary>
        string FreeUpHint = "");

    public static readonly IReadOnlyList<Resource> All = new[]
    {
        new Resource(CmsSites, "เว็บไซต์ (CMS)", "เว็บ", ResourceKind.Capacity, LegacySource.None,
            FreeUpHint: "ลบเว็บที่ไม่ใช้"),
        new Resource(CmsPagesPerSite, "หน้าเว็บต่อเว็บไซต์", "หน้า", ResourceKind.Capacity, LegacySource.None, PerSite: true, Sellable: false,
            FreeUpHint: "ลบหน้าที่ไม่ใช้"),
        new Resource(CmsProductsPerSite, "สินค้าบนเว็บต่อเว็บไซต์", "รายการ", ResourceKind.Capacity, LegacySource.None, PerSite: true, Sellable: false,
            FreeUpHint: "ลบสินค้าที่ไม่ใช้"),
        new Resource(Users, "ผู้ใช้ (ที่นั่ง)", "คน", ResourceKind.Capacity, LegacySource.PlanMaxUsers,
            FreeUpHint: "นำผู้ใช้ที่ไม่ใช้ออก หรือยกเลิกคำเชิญที่ค้าง"),
        new Resource(StorageGb, "พื้นที่จัดเก็บ", "GB", ResourceKind.Capacity, LegacySource.PlanMaxStorage,
            FreeUpHint: "ลบไฟล์ที่ไม่ใช้"),
        new Resource(LodgingProperties, "ที่พัก (แห่ง)", "แห่ง", ResourceKind.Capacity, LegacySource.LodgingFirstFree,
            FreeUpHint: "ลบที่พักที่ไม่ใช้"),
        new Resource(OcrPagesMonth, "หน้าสแกน OCR", "หน้า", ResourceKind.Consumable, LegacySource.PlanOcrPages,
            FreeUpHint: "รอรอบเดือนถัดไป"),
    };

    public static Resource? Find(string? key)
        => string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(r => r.Key == key.Trim());

    public static bool IsKnown(string? key) => Find(key) != null;

    /// <summary>รหัสข้อเสนอในแคตตาล็อก (ใช้เป็น <c>UsageEvent.FeatureCode</c> ของค่ารายเดือน — ไม่ชนรหัสส่วนเสริมเดิม)</summary>
    public static string OfferCode(string resourceKey) => "quota." + resourceKey;

    /// <summary>ชื่อบนใบแจ้งหนี้ค่าใช้งานของค่าเดือนโควตาเสริม (<see cref="OfferCode"/>) — null = ไม่ใช่รหัสโควตาเสริม</summary>
    public static string? InvoiceName(string? featureCode)
        => featureCode != null && featureCode.StartsWith("quota.", StringComparison.Ordinal) && Find(featureCode[6..]) is { } r
            ? $"โควตาเสริม {r.Label} (รายเดือน)" : null;

    /// <summary>RuleCode ของการปฏิเสธเพราะเต็ม — หน้าเว็บอ่านเพื่อเปิดหน้าต่างซื้อ (<c>/pages/billing.html#buy=&lt;resource&gt;</c>)</summary>
    public static string FullRuleCode(string resourceKey) => "QUOTA-FULL:" + resourceKey;

    /// <summary>ลิงก์ทางไปต่อ "ซื้อเพิ่ม" ของทรัพยากร (ตัวเดียวทั้งข้อความปฏิเสธและหน้าเว็บ)</summary>
    public static string BuyUrl(string resourceKey) => "/pages/billing.html#buy=" + resourceKey;

    /// <summary>รอบ 231: แท็บ "ใช้ไปเท่าไร" ของ hub — ลิงก์ในข้อความแจ้งเตือนโควตา/ส่วนเกินทุกทรัพยากร (เดิมชี้ <c>/pages/addons.html</c>)</summary>
    public const string UsageUrl = "/pages/billing.html#tab=usage";

    public const long BytesPerGb = 1024L * 1024L * 1024L;
    /// <summary>เพดานพื้นที่ "ไม่จำกัด" — ไม่ใช้ long.MaxValue (used + เพิ่ม จะล้นเป็นค่าลบแล้วกลายเป็นบล็อก)</summary>
    public const long UnlimitedBytes = long.MaxValue / 4;
}

namespace Accounting.Models.Constants;

/// <summary>
/// **รหัส add-on ทั้งระบบ — ที่เดียว** (LODGING_LICENSING_PLAN.md §3)
///
/// ทำไมเป็น string ไม่ใช่บิตใน <c>FeatureFlags</c>: bitmask ใช้ไปถึงบิต 47 จาก 64
/// แล้ว (`AllEnums.cs` — `CmsBooking = 1L &lt;&lt; 47`) และบิตที่เหลือสงวนไว้ให้
/// "ความสามารถระดับแพ็กเกจบัญชี" เท่านั้น. add-on ที่ขายแยกทุกตัวเดินผ่าน
/// <see cref="Models.Entities.CompanyFeature"/> (string code) — ดู
/// LODGING_LICENSING_PLAN.md §6 "Data model"
///
/// **ห้ามเปลี่ยนค่าคงที่หลังเปิดขาย** — ผูกกับ UsageEvent ย้อนหลังและใบแจ้งหนี้
/// ที่ออกไปแล้ว (เหมือน <c>ApiFeature.FeatureCode</c> ที่คอมเมนต์เตือนไว้)
/// </summary>
public static class AddOnCodes
{
    // ───────── โมดูลที่พัก (Lodging) ─────────
    /// <summary>Guest Portal Pro — QR ต่อห้อง · คำขอแขก · แจ้งเตือนก่อนเช็คอิน/ขอรีวิว
    /// (หน้าการจองด้วย token แบบพื้นฐาน = ฟรี ไม่ผูกกับ add-on นี้)</summary>
    public const string LodgingGuestPortal = "lodging.guest-portal";
    public const string LodgingPromo = "lodging.promo";
    public const string LodgingChannelManager = "lodging.channel-manager";
    public const string LodgingPosFolio = "lodging.pos-folio";
    public const string LodgingAnalytics = "lodging.analytics";
    public const string LodgingMultiProperty = "lodging.multi-property";
    public const string LodgingI18n = "lodging.i18n";
    public const string LodgingEarlyLateFee = "lodging.early-late-fee";
    public const string LodgingLoyalty = "lodging.loyalty";

    // ───────── มิเตอร์ (ไม่ใช่สวิตช์ — ไม่ต้องให้ลูกค้ากดเปิด) ─────────
    /// <summary>เอกสารบัญชีที่เกินโควตาแพ็กเกจ — คิดต่อฉบับ (PerUnit)
    /// **ไม่ใช่ add-on ที่ต้องเปิด**: เกิดอัตโนมัติเมื่อเกินโควตา (ดู §5 "ห้ามบล็อก
    /// เอกสารที่กฎหมายบังคับ — เกินโควตา = เกิดหนี้ ไม่ใช่ปฏิเสธงาน")</summary>
    public const string DocumentsOverage = "documents.overage";

    /// <summary>การเข้าพักที่ปิดสถานะ (เช็คเอาต์/no-show/ยกเลิกที่มีมัดจำ) —
    /// มิเตอร์ของโมดูลที่พัก. นับจาก**สถานะการจอง** ไม่ใช่จากเอกสาร ⇒ ลูกค้าที่
    /// ตั้ง AccountingMode=Off (ไม่ออกเอกสาร) ก็ยังนับเท่ากัน (§13.2)</summary>
    public const string LodgingStay = "lodging.stay";

    /// <summary>อีเมลแจ้งเตือนของที่พักที่เกินโควตาฟรีต่อเดือน</summary>
    public const string LodgingEmailOverage = "lodging.email.overage";

    /// <summary>SMS แจ้งเตือน — ต้นทุนแปรผันตรง คิดต่อข้อความเสมอ</summary>
    public const string LodgingSms = "lodging.notify.sms";

    /// <summary>ซื้อโควตาเอกสารเพิ่มเป็นก้อน (top-up) — ราคาต่อแพ็ก 100 ฉบับ</summary>
    public const string DocumentsTopUp = "documents.topup";

    /// <summary>1 แพ็ก top-up = กี่ฉบับ — **ต้องตรงกับ `UnitLabel` ที่ seed ไว้**
    /// ("แพ็ก 100 ฉบับ") ถ้าจะเปลี่ยนต้องแก้ทั้งสองที่พร้อมกัน มิฉะนั้นลูกค้าจ่าย
    /// ราคาแพ็กหนึ่งแต่ได้โควตาอีกจำนวนหนึ่ง</summary>
    public const int DocumentsPerTopUpPack = 100;

    /// <summary>โควตา top-up ที่ซื้อมีอายุกี่วัน — ยาวกว่าโบนัสจากภารกิจ เพราะ
    /// ลูกค้าจ่ายเงินจริง (ซื้อปลายเดือนแล้วหมดอายุใน 3 วันคือการโกงกลาย ๆ)</summary>
    public const int TopUpValidDays = 60;

    /// <summary>add-on ทั้งหมดที่เป็น "สวิตช์" ให้ลูกค้าเปิด/ปิดเอง (ใช้ในหน้า addons
    /// และตอน seed) — เรียงตามลำดับที่อยากให้เห็นบนหน้าจอ</summary>
    public static readonly IReadOnlyList<string> Switchable = new[]
    {
        LodgingGuestPortal, LodgingPromo, LodgingChannelManager, LodgingPosFolio,
        LodgingAnalytics, LodgingMultiProperty, LodgingI18n, LodgingEarlyLateFee, LodgingLoyalty,
    };

    /// <summary>มิเตอร์ที่ระบบเขียนเอง — ห้ามโผล่เป็นสวิตช์ในหน้าลูกค้า
    /// (ถ้าโผล่ ลูกค้าจะ "ปิด" แล้วคิดว่าไม่ต้องจ่ายค่าเอกสารเกินโควตา)</summary>
    public static readonly IReadOnlyList<string> SystemMeters = new[]
    {
        DocumentsOverage, LodgingStay, LodgingEmailOverage, LodgingSms, DocumentsTopUp,
    };

    public static bool IsSystemMeter(string? code)
        => code != null && SystemMeters.Contains(code);
}

using Accounting.Models.Constants;

namespace Accounting.Helpers;

/// <summary>รอบ 281 (คำตัดสิน 281-2) — แยกการแจ้งเตือนของเส้น "A ส่งบิล → B จ่าย → ใบเสร็จ/ใบกำกับ → ใบสำคัญจ่าย + 50 ทวิ → 50 ทวิ กลับถึง A"
/// ออกจากคีย์เดียว <see cref="NotificationEvents.AutopilotPartnerExchange"/> เป็นคีย์ต่อขั้น ให้บริษัทเลือกช่อง (กระดิ่ง · อีเมล · LINE ส่วนตัว · กลุ่ม LINE) แยกขั้นได้
/// ในหน้าแจ้งเตือนเดิม — <b>โดยไม่ทำค่าตั้งของบริษัทหาย</b> (แบบเดียวกับ <see cref="NotificationEventSplit"/> รอบ 249):
/// <list type="bullet">
/// <item>คัดลอกแถวค่าตั้งของบริษัท + การปิดช่องรายคนของ partner_exchange → คีย์ใหม่ <b>เฉพาะบริษัท (ผู้ใช้) ที่ยังไม่มีแถวของคีย์นั้นเลย</b> ⇒ บริษัทที่ปิดกลุ่ม LINE ไว้
/// ยังปิด · บริษัทที่เปิด LINE ไว้ยังได้ · บริษัทที่ไม่เคยตั้ง ⇒ ค่าตั้งต้นเดียวกับ partner_exchange (<see cref="NotificationAudiencePolicy.EmailDefaultKeys"/>)</item>
/// <item>idempotent: หน้า notifications.html บันทึกทุกคีย์ในแคตตาล็อก ⇒ หลังกดบันทึกครั้งแรก คีย์ใหม่มีแถวของตัวเอง ⇒ บูตถัดไป no-op · ON CONFLICT DO NOTHING กันหลายเครื่อง</item>
/// <item>ต้องรันหลัง <see cref="NotificationEventSplit.MigrationSql"/> (ตาราง/คอลัมน์เดียวกัน)</item>
/// </list></summary>
public static class PartnerFlowNotificationSplit
{
    public const string FromKey = NotificationEvents.AutopilotPartnerExchange;
    public const string Actor = "migration:281-partner-flow";

    /// <summary>คีย์ต่อขั้นของเส้น A↔B (ตัวตั้งเดียว — ค่าตั้งต้น <see cref="NotificationAudiencePolicy.EmailDefaultKeys"/> + การคัดลอกอ่านจากที่นี่)</summary>
    public static readonly IReadOnlyList<string> Keys = new[]
    {
        NotificationEvents.PartnerPurchaseDraftReceived,
        NotificationEvents.PartnerTaxInvoiceReceived,
        NotificationEvents.PartnerReceiptDelivered,
        NotificationEvents.PartnerSellerPaid,
        NotificationEvents.PartnerPaymentAutoRecorded,
        NotificationEvents.PartnerWhtCertDelivered,
        NotificationEvents.PartnerWhtCertReceived,
    };

    public static string SettingsCopySql(string toKey) => $"""
        INSERT INTO "NotificationSettings"
            ("Id", "CompanyId", "EventKey", "RecipientRole", "EnableSystem", "EnableEmail", "EnableLine", "EnableLineGroup", "CreatedAt", "CreatedBy", "IsDeleted")
        SELECT gen_random_uuid(), s."CompanyId", '{toKey}', s."RecipientRole", s."EnableSystem", s."EnableEmail", s."EnableLine", s."EnableLineGroup", now(), '{Actor}', false
          FROM "NotificationSettings" s
         WHERE s."EventKey" = '{FromKey}'
           AND s."IsDeleted" = false
           AND NOT EXISTS (SELECT 1 FROM "NotificationSettings" x WHERE x."CompanyId" = s."CompanyId" AND x."EventKey" = '{toKey}')
        ON CONFLICT DO NOTHING
        """;

    public static string PreferencesCopySql(string toKey) => $"""
        INSERT INTO "NotificationPreferences"
            ("Id", "CompanyId", "UserId", "EventKey", "SuppressSystem", "SuppressEmail", "SuppressLine", "CreatedAt", "CreatedBy", "IsDeleted")
        SELECT gen_random_uuid(), p."CompanyId", p."UserId", '{toKey}', p."SuppressSystem", p."SuppressEmail", p."SuppressLine", now(), '{Actor}', false
          FROM "NotificationPreferences" p
         WHERE p."EventKey" = '{FromKey}'
           AND p."IsDeleted" = false
           AND NOT EXISTS (SELECT 1 FROM "NotificationPreferences" x WHERE x."CompanyId" = p."CompanyId" AND x."UserId" = p."UserId" AND x."EventKey" = '{toKey}')
        ON CONFLICT DO NOTHING
        """;

    /// <summary>ก้อนเดียวที่ <c>DatabaseMigrationHelper</c> รัน (ทุกคีย์ · idempotent)</summary>
    public static string MigrationSql => string.Join("\n", Keys.Select(k => SettingsCopySql(k) + ";\n" + PreferencesCopySql(k) + ";"));
}

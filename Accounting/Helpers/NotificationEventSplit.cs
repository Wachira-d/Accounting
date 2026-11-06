namespace Accounting.Helpers;

/// <summary>รอบ 249 — แยกคีย์ <c>document.payment_made</c> (จ่ายชำระฝั่งซื้อ) ออกจาก <c>document.payment_received</c> โดย<b>ไม่ทำค่าตั้งของบริษัทหาย</b>
/// <list type="bullet">
/// <item>ก่อนรอบ 249 การจ่ายชำระ PI/ค่าใช้จ่ายถูกยิงด้วยคีย์ payment_received ⇒ แถวที่บริษัทตั้งไว้ของคีย์นั้น (เช่น ปิดกลุ่ม LINE · เปิดอีเมลถึงฝ่ายบัญชี)
/// ควบคุมทั้งสองฝั่ง · ถ้าคีย์ใหม่ไม่มีแถว เครื่องแจ้งเตือนจะใช้ <see cref="NotificationAudiencePolicy.DefaultMatrix"/> (กระดิ่งเจ้าของ + กลุ่ม LINE)
/// ⇒ บริษัทที่ "ปิดกลุ่ม LINE ไว้แล้ว" จะกลับมาได้ประกาศฝั่งจ่ายเข้ากลุ่ม = ถดถอย</item>
/// <item>จึงคัดลอกแถว payment_received → payment_made (ค่าตั้งของบริษัท + การปิดช่องรายคน) <b>เฉพาะบริษัท (ผู้ใช้) ที่ยังไม่มีแถวของคีย์ใหม่เลย</b> —
/// หน้า notifications.html บันทึกทุกช่องของทุกเหตุการณ์ในแคตตาล็อก ⇒ ครั้งแรกที่บริษัทกดบันทึกหน้านี้ คีย์ใหม่มีแถวของตัวเองแล้ว ⇒ บูตถัดไป no-op
/// (ไม่ทับค่าที่ผู้ใช้ตั้งฝั่งจ่ายเอง) · ก่อนหน้านั้นฝั่งจ่ายเดินตามค่าตั้งฝั่งรับ = พฤติกรรมเดิมก่อนแยกคีย์ ·
/// บริษัทที่ไม่เคยตั้ง payment_received ⇒ ไม่มีอะไรให้คัดลอก ⇒ ทั้งสองคีย์ใช้ค่าตั้งต้นชุดเดียวกัน · ON CONFLICT DO NOTHING กันบูตพร้อมกันหลายเครื่อง</item>
/// <item>ต้องรัน<b>หลัง</b>การเพิ่มคอลัมน์ <c>EnableLineGroup</c> และการสร้างตาราง <c>NotificationPreferences</c> ใน <c>DatabaseMigrationHelper</c></item>
/// </list></summary>
public static class NotificationEventSplit
{
    public const string FromKey = "document.payment_received";
    public const string ToKey = "document.payment_made";
    public const string Actor = "migration:249-payment-made";

    public const string SettingsCopySql = $"""
        INSERT INTO "NotificationSettings"
            ("Id", "CompanyId", "EventKey", "RecipientRole", "EnableSystem", "EnableEmail", "EnableLine", "EnableLineGroup", "CreatedAt", "CreatedBy", "IsDeleted")
        SELECT gen_random_uuid(), s."CompanyId", '{ToKey}', s."RecipientRole", s."EnableSystem", s."EnableEmail", s."EnableLine", s."EnableLineGroup", now(), '{Actor}', false
          FROM "NotificationSettings" s
         WHERE s."EventKey" = '{FromKey}'
           AND s."IsDeleted" = false
           AND NOT EXISTS (SELECT 1 FROM "NotificationSettings" x WHERE x."CompanyId" = s."CompanyId" AND x."EventKey" = '{ToKey}')
        ON CONFLICT DO NOTHING
        """;

    public const string PreferencesCopySql = $"""
        INSERT INTO "NotificationPreferences"
            ("Id", "CompanyId", "UserId", "EventKey", "SuppressSystem", "SuppressEmail", "SuppressLine", "CreatedAt", "CreatedBy", "IsDeleted")
        SELECT gen_random_uuid(), p."CompanyId", p."UserId", '{ToKey}', p."SuppressSystem", p."SuppressEmail", p."SuppressLine", now(), '{Actor}', false
          FROM "NotificationPreferences" p
         WHERE p."EventKey" = '{FromKey}'
           AND p."IsDeleted" = false
           AND NOT EXISTS (SELECT 1 FROM "NotificationPreferences" x WHERE x."CompanyId" = p."CompanyId" AND x."UserId" = p."UserId" AND x."EventKey" = '{ToKey}')
        ON CONFLICT DO NOTHING
        """;

    /// <summary>ก้อนเดียวที่ <c>DatabaseMigrationHelper</c> รัน (idempotent: บูตซ้ำ = no-op)</summary>
    public const string MigrationSql = SettingsCopySql + ";\n" + PreferencesCopySql + ";";
}

namespace Accounting.Helpers;

/// <summary>รอบ 219 ส่วน B (คำตัดสินข้อ 179 · PLATFORM_GATEWAY_REFUND_POLICY_DESIGN) — schema บล็อกเดียว · รันทุกบูตได้
/// (CREATE/ADD ... IF NOT EXISTS ล้วน · ไม่แก้แถวเดิม):
/// <list type="bullet">
/// <item><c>PolicyAcceptances</c> — หลักฐานติ๊กยอมรับข้อกำหนด + นโยบายคืนเงินก่อนชำระ (<see cref="PurchasePolicyAcceptance"/>)</item>
/// <item><c>Sites.Refund*</c> + <c>PolicyAcceptanceText</c> — นโยบายคืนเงินของร้าน (<see cref="CmsLegalPages"/>)</item>
/// <item><c>PaymentProviderConfigs.LiveKeyVerifiedAt/LastLiveWebhookAt/LastSelfTestPaidAt</c> — ด่านเปิดรับเงินจริงแบบเข้มขึ้น (<see cref="PaymentLiveGate"/>) ·
/// แถวเดิมที่อยู่โหมดใช้งานจริงแล้ว<b>ไม่ถูกแตะ</b> (ด่านมีผลตอนสลับเท่านั้น)</item>
/// <item><c>Subscriptions.CancelAtPeriodEnd/CancelRequestedAt</c> — ยกเลิกแพ็กเกจมีผลสิ้นรอบ (<see cref="SubscriptionCancelPolicy"/>)</item>
/// <item><c>SiteSettings.PlatformPayTestPageMode</c> — หน้าทดลองจ่ายของแพลตฟอร์ม (ค่าเริ่มต้น AdminsOnly)</item>
/// </list></summary>
public static class PurchasePolicyAcceptanceMigration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """CREATE TABLE IF NOT EXISTS "PolicyAcceptances" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "Scope" varchar(20) NOT NULL DEFAULT '', "Channel" varchar(50) NOT NULL DEFAULT '', "SourceId" uuid NULL, "SiteId" uuid NULL, "PolicyVersion" varchar(100) NOT NULL DEFAULT '', "VersionShown" varchar(100) NULL, "Subject" varchar(256) NULL, "UserId" uuid NULL, "AcceptedAt" timestamp without time zone NOT NULL, "IpAddress" varchar(45) NULL, "UserAgent" varchar(500) NULL, "EvidenceHash" varchar(100) NULL, "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'), "UpdatedAt" timestamp without time zone NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
        """ALTER TABLE "PolicyAcceptances" ADD COLUMN IF NOT EXISTS "PseudonymisedAt" timestamp without time zone NULL;""",
        // ฝ่ายค้าน S4: append-only ระดับฐาน — แก้ได้เฉพาะการทำไม่ให้ระบุตัว (Subject/IP/UA/PseudonymisedAt) · ลบได้เฉพาะงานล้างตามอายุ (ไม่มีเส้นอื่น)
        """CREATE INDEX IF NOT EXISTS "IX_PolicyAcceptances_Company_Subject" ON "PolicyAcceptances" ("CompanyId", "Subject");""",
        """CREATE INDEX IF NOT EXISTS "IX_PolicyAcceptances_Company_Channel_Source" ON "PolicyAcceptances" ("CompanyId", "Channel", "SourceId");""",
        """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "RefundPolicyUrl" varchar(500) NULL;""",
        """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "RefundPolicyMarkdown" text NULL;""",
        """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "RefundPolicyUpdatedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "RefundReturnWindowDays" integer NULL;""",
        """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "RefundProcessingDays" integer NULL;""",
        """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "PolicyAcceptanceText" varchar(500) NULL;""",
        """ALTER TABLE "PaymentProviderConfigs" ADD COLUMN IF NOT EXISTS "LiveKeyVerifiedAt" timestamptz NULL;""",
        """ALTER TABLE "PaymentProviderConfigs" ADD COLUMN IF NOT EXISTS "LastLiveWebhookAt" timestamptz NULL;""",
        """ALTER TABLE "PaymentProviderConfigs" ADD COLUMN IF NOT EXISTS "LastSelfTestPaidAt" timestamptz NULL;""",
        // ฝ่ายค้าน S1: เครื่องหมาย "ผ่านด่านแพลตฟอร์ม" — config live เดิมไม่มีค่า ⇒ ช่องทางค่าบริการออนไลน์ปิดจนแอดมินยืนยันผ่านหน้าแอดมิน
        """ALTER TABLE "PaymentProviderConfigs" ADD COLUMN IF NOT EXISTS "PlatformLiveConfirmedAt" timestamptz NULL;""",
        """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "CancelAtPeriodEnd" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "CancelRequestedAt" timestamptz NULL;""",
        """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PlatformPayTestPageMode" varchar(20) NULL;""",
    };
}

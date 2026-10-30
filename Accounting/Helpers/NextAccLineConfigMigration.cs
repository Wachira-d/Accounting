namespace Accounting.Helpers;

/// <summary>รอบ 212 แพ็ก D — คอลัมน์ค่าตั้ง LINE OA กลาง (Messaging API) บนแถว <c>SiteSettings</c> · NULL ทั้งหมด ⇒ แถวเดิมไม่ถูกแตะ
/// (ว่าง = ใช้ config <c>Line:*</c> ตามเดิม — <see cref="NextAccLineConfigRule.Merge"/>) · รันทุกบูตได้ (IF NOT EXISTS)</summary>
public static class NextAccLineConfigMigration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "LineMessagingChannelAccessToken" text NULL;""",
        """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "LineMessagingChannelSecret" text NULL;""",
        """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "LineMessagingBasicId" text NULL;""",
        """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "LineMessagingBotUserId" text NULL;""",
        """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "LineMessagingGroupId" text NULL;""",
    };
}

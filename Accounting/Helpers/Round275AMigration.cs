namespace Accounting.Helpers;

/// <summary>
/// รอบ 275 แพ็ก A (คีย์และความลับ · T4 M-4) — ลายนิ้วมือคีย์เข้ารหัสบน <c>SiteSettings</c> · idempotent (<c>ADD COLUMN IF NOT EXISTS</c>) ·
/// ฐานเทสต์ได้คอลัมน์จากโมเดล (EnsureCreated) · ไม่มี backfill: ค่า null = <see cref="KeyFingerprintVerdict.FirstSeen"/> ⇒ เครื่องแรกที่ตรวจบันทึกคีย์ของตัวเอง
/// </summary>
public static class Round275AMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "EncryptionKeyFingerprint" text NULL;""",
        """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "EncryptionKeyFingerprintPrevious" text NULL;""",
        """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "EncryptionKeyFingerprintAt" timestamp without time zone NULL;""",
    };
}

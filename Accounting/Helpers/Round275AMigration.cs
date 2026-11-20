namespace Accounting.Helpers;

/// <summary>
/// รอบ 275 แพ็ก A (คีย์และความลับ · T4 M-4 · commit 2e7199e0 · ฝ่ายค้าน MED-1/3/4 commit 17305b6a) — ลายนิ้วมือคีย์เข้ารหัสบน <c>SiteSettings</c> · idempotent (<c>ADD COLUMN IF NOT EXISTS</c>) ·
/// ฐานเทสต์ได้คอลัมน์จากโมเดล (EnsureCreated) · ไม่มี backfill: ค่า null = <see cref="KeyFingerprintVerdict.FirstSeen"/> ⇒ เครื่องแรกที่ตรวจบันทึกคีย์ของตัวเอง
/// </summary>
public static class Round275AMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "EncryptionKeyFingerprint" text NULL;""",
        """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "EncryptionKeyFingerprintPrevious" text NULL;""",
        """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "EncryptionKeyFingerprintAt" timestamp without time zone NULL;""",
        // ฝ่ายค้าน MED-1/MED-3: ลายนิ้วมือต่อเครื่อง (ตรงกับ AccountingDbContext — ฐานใหม่ได้จาก EnsureCreated)
        """CREATE TABLE IF NOT EXISTS "EncryptionKeyHeartbeats" ("InstanceId" varchar(200) PRIMARY KEY, "Fingerprint" text NOT NULL DEFAULT '', "PreviousFingerprints" text NULL, "SeenAt" timestamp without time zone NOT NULL);""",
        // ฝ่ายค้าน MED-4: เมล็ดกุญแจ HMAC ถาวร (เข้ารหัสด้วย SecretProtector · ไม่หมุนตาม ENCRYPTION_KEY)
        """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "HmacSeedProtected" text NULL;""",
    };
}

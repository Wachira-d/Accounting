namespace Accounting.Helpers;

/// <summary>
/// รอบ 267 แพ็ก P3 — schema ของ "📤 ส่งให้ลูกค้า + ไทม์ไลน์ + LINE เซ็น" (คำตัดสิน 267-1-61…67 · DOCUMENT_FLOW §2.12n-P3) ·
/// รันตอนบูตผ่าน <c>DatabaseMigrationHelper</c> (idempotent · <c>ADD COLUMN IF NOT EXISTS</c>) · ฐานเทสต์ได้คอลัมน์จากโมเดล (EnsureCreated)
/// </summary>
public static class CustomerShareMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        // แถวคิว LINE ที่ส่งลิงก์หน้าเอกสาร (ปุ่ม "ดู/ยอมรับ" ฯลฯ) — null = แถวก่อนรอบ 267 P3 (การ์ดเดิม: ดู PDF/ชำระ)
        """ALTER TABLE "LineQueues" ADD COLUMN IF NOT EXISTS "IncludePageLink" boolean NULL;""",
        // แหล่งที่มาของคำตอบ (แถวคิว LINE ที่ลูกค้าเปิดลิงก์มา) + เวลาที่การ์ด "ฉบับเซ็น" เข้าคิวกลับไป LINE เดิม (ครั้งเดียว)
        """ALTER TABLE "SignEventOutbox" ADD COLUMN IF NOT EXISTS "SourceLineQueueId" uuid NULL;""",
        """ALTER TABLE "SignEventOutbox" ADD COLUMN IF NOT EXISTS "SignedCopyLineQueuedAt" timestamptz NULL;""",
    };
}

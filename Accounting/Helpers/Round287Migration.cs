namespace Accounting.Helpers;

/// <summary>
/// รอบ 287 — ส่งเอกสารหลังรับเงินทันทีสำหรับผู้จ่ายที่ไว้ใจได้ (คำสั่งเจ้าของ · คำตัดสิน 287-1 · <see cref="TrustedSlipPayerRule.HoldDelivery"/>) · idempotent (<c>IF NOT EXISTS</c>) ·
/// <b>ค่าตั้งต้นเปิด</b> ตามคำสั่งเจ้าของ — บริษัทที่เปิดอนุมัติล่วงหน้าของ 286 อยู่แล้วจะเริ่มส่งเอกสารทันที (ปิดได้ที่แท็บ LINE) ·
/// ไม่มีของเสียที่ persist ไว้ต้องซ่อม: งานส่งที่ถูกกักไว้ก่อน deploy ยังรอ <c>NextAttemptAt</c> เดิม ⇒ รอบถัดไปของงานหลังรับชำระอ่านค่าตั้งใหม่แล้วส่งตามปกติ
/// </summary>
public static class Round287Migration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "SlipTrustedSendImmediately" boolean NOT NULL DEFAULT true;""",
    };
}

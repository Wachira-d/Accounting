namespace Accounting.Helpers;

/// <summary>
/// รอบ 268 (backlog cleanup) — คอลัมน์ใหม่ · idempotent (<c>IF NOT EXISTS</c>) · ฐานเทสต์ได้จากโมเดล (EnsureCreated)
/// <list type="bullet">
/// <item>268-1-3: ช่องทางของเงินมัดจำการจองแต่ละงวด (<c>SiteBookingPayments.ChannelStamped/BankAccountId/MoneyInAccountId</c>) — แถวเดิม = ไม่ได้ประทับ (ไม่เดาย้อนหลัง)</item>
/// </list>
/// </summary>
public static class Round268Migration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "SiteBookingPayments" ADD COLUMN IF NOT EXISTS "ChannelStamped" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "SiteBookingPayments" ADD COLUMN IF NOT EXISTS "BankAccountId" uuid NULL;""",
        """ALTER TABLE "SiteBookingPayments" ADD COLUMN IF NOT EXISTS "MoneyInAccountId" uuid NULL;""",
    };
}

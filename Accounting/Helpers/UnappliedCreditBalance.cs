namespace Accounting.Helpers;

/// <summary>
/// **สูตรยอด "เงินรอจัดสรร" คงเหลือ — ตัวเดียวของฝั่งเขียนและฝั่งตรวจ** (รอบ 258 · บทเรียน hash chain: สองสูตร = ไม่มีวันตรง)
/// <para><c>คงเหลือ = ยอดรับ − Σ การจัดสรรที่ยังมีผล</c> (ทั้งที่จัดสรรพร้อมรับเงินและจัดสรรทีหลัง) · ผู้เขียนทุกตัว
/// (<c>CreateMultiDocPaymentAsync</c> · <c>CustomerCreditService</c> จัดสรร/ยกเลิกการจัดสรร) <b>คิดใหม่จากแถวจริงทุกครั้ง</b> ไม่บวก/ลบสะสม ·
/// backfill ใช้สูตรเดียวกันในรูป SQL (<c>CustomerCreditMigration</c>)</para>
/// <para>ใช้ได้เฉพาะการชำระที่ลงเงินส่วนเกินเข้าผังพัก (<c>Payment.UnappliedAccountId</c> มีค่า) — การชำระใบเดียวไม่มีแถวจัดสรร ห้ามนำสูตรนี้ไปใช้</para>
/// </summary>
public static class UnappliedCreditBalance
{
    /// <summary>ต่ำกว่านี้ถือว่าไม่มีเงินคงเหลือ (ครึ่งสตางค์ — ค่าเดียวกับ <see cref="DocumentSettlementState.Tolerance"/>)</summary>
    public const decimal Tolerance = DocumentSettlementState.Tolerance;

    /// <summary>ยอดคงเหลือ (ปัด 2 ตำแหน่ง AwayFromZero · ไม่ติดลบ)</summary>
    public static decimal Compute(decimal paymentAmount, decimal liveAllocatedSum)
    {
        var raw = Math.Round(paymentAmount - liveAllocatedSum, 2, MidpointRounding.AwayFromZero);
        return raw <= Tolerance ? 0m : raw;
    }

    /// <summary>มีเงินคงเหลือให้จัดสรรไหม</summary>
    public static bool HasBalance(decimal unappliedAmount) => unappliedAmount > Tolerance;
}

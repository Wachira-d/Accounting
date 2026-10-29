using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 209 (ตรวจ CMS ด้วยการจำลองธุรกิจจริง) — ตัวตัดสินแบบ pure · ทุกกลุ่มล็อกทั้ง "เคสที่แก้" และ "เคสเดิมไม่ถูกแตะ"
/// </summary>
public class CmsRound209Tests
{
    // ═════════ C1: เครดิต × ที่นั่ง (ทีม C · blocking) ═════════

    [Fact]
    public void Credit_booking_charges_per_seat()
    {
        Assert.Equal(3m, CourseCreditMath.CreditsForBooking(CreditUnit.Session, 60, 1m, 3));        // จอง 3 ที่ = 3 เครดิต (เดิม 1)
        Assert.Equal(4.5m, CourseCreditMath.CreditsForBooking(CreditUnit.Hour, 90, 1m, 3));         // ชั่วโมง: 1.5 × 3
    }

    [Theory]   // ทิศตรงข้าม: การจองที่นั่งเดียว/ค่าผิด ⇒ เท่าเดิม
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-2)]
    public void Single_seat_booking_unchanged(int seats)
        => Assert.Equal(CourseCreditMath.CreditsForSession(CreditUnit.Session, 60, 1m),
            CourseCreditMath.CreditsForBooking(CreditUnit.Session, 60, 1m, seats));

    // ═════════ C1: จองด้วยบัตรต้องเป็นพนักงาน (ทีม C · blocking) ═════════

    [Fact]
    public void Anonymous_cannot_pay_with_credit_staff_can()
    {
        // รอบ 209 P1: ตัวตรวจชั้น service รวมเป็น ActorPayWithCreditProblem (ผู้กระทำว่าง ⇒ ไม่ได้ · พนักงาน ⇒ ได้)
        Assert.NotNull(CreditBookingLink.ActorPayWithCreditProblem(null, null));
        Assert.NotNull(CreditBookingLink.ActorPayWithCreditProblem(" ", null));
        Assert.Null(CreditBookingLink.ActorPayWithCreditProblem("3f2b0c1e-0000-0000-0000-000000000001", null));
    }
}

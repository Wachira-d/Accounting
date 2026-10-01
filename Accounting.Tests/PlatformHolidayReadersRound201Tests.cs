using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม PL · ฝ่ายค้าน PL-B1 (คำตัดสินข้อ 106) — ผู้อ่านวันหยุดราชการที่เพิ่งต่อสาย + ด่านปิดการกรอกเมื่อยังต่อสายไม่ครบ ·
/// ทุกเคสมีสองทิศ: มีวันหยุด ⇒ เลื่อน · ไม่มี/null ⇒ เท่าเดิมทุกวัน
/// </summary>
public class PlatformHolidayReadersRound201Tests
{
    private static readonly IReadOnlySet<DateTime> Sep15 = new HashSet<DateTime> { new DateTime(2026, 9, 15) };

    [Fact]
    public void WarnByFor_ShiftsPastHoliday_AndEmptySetKeepsOldDate()
    {
        // ภ.พ.30 งวด ส.ค. 2569: e-Filing 23/09/2026 (พุธ) · วันหยุด 23/09 ⇒ 24/09
        var old = TaxFilingDeadline.WarnByFor(TaxType.VAT, 2026, 8);
        Assert.Equal(new DateTime(2026, 9, 23), old);
        Assert.Equal(new DateTime(2026, 9, 24), TaxFilingDeadline.WarnByFor(TaxType.VAT, 2026, 8, new HashSet<DateTime> { new DateTime(2026, 9, 23) }));
        // ทิศตรงข้าม: ชุดว่าง/null = เดิม
        Assert.Equal(old, TaxFilingDeadline.WarnByFor(TaxType.VAT, 2026, 8, new HashSet<DateTime>()));
        Assert.Equal(old, TaxFilingDeadline.WarnByFor(TaxType.VAT, 2026, 8, null));
        // แบบที่ไม่ใช่รายเดือน ⇒ null ทั้งสอง overload
        Assert.Null(TaxFilingDeadline.WarnByFor(TaxType.CorporateIncomeTax, 2026, 8, Sep15));
    }

    [Fact]
    public void DepositForfeit_DeadlineOnHoliday_StillInTimeNextBusinessDay()
    {
        // รับมัดจำ 10/08 · ริบ 20/09 · วันนี้ 16/09 — กำหนดกระดาษงวด ส.ค. = 15/09 (อังคาร)
        var today = new DateTime(2026, 9, 16);
        var noHoliday = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, true,
            new DateTime(2026, 8, 10), new DateTime(2026, 9, 20), depositPeriodLocked: false, today: today);
        Assert.True(noHoliday.LateFlag);                                       // เดิม: เลยกำหนดแล้ว ⇒ งวดปัจจุบัน + ธง
        Assert.Equal(new DateTime(2026, 9, 20), noHoliday.TaxPointDate);
        // 15/09 เป็นวันหยุด ⇒ กำหนดเลื่อนเป็น 16/09 ⇒ วันนี้ยังทัน ⇒ ย้อนเข้างวดเดือนรับเงินได้
        var withHoliday = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, true,
            new DateTime(2026, 8, 10), new DateTime(2026, 9, 20), depositPeriodLocked: false, today: today, holidays: Sep15);
        Assert.False(withHoliday.LateFlag);
        Assert.Equal(new DateTime(2026, 8, 10), withHoliday.TaxPointDate);
        // ทิศตรงข้าม: ปิดงวดแล้ว ⇒ วันหยุดไม่ช่วย (ห้ามย้อนเข้างวดที่ยื่น/ปิดแล้ว)
        var locked = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, true,
            new DateTime(2026, 8, 10), new DateTime(2026, 9, 20), depositPeriodLocked: true, today: today, holidays: Sep15);
        Assert.True(locked.LateFlag);
    }

    [Fact]
    public void HolidayEntry_BlockedWhileReadersPending_OpenWhenAllWired()
    {
        // ปัจจุบัน: ยังมีผู้อ่านค้าง (สปส. · §87) ⇒ ปิดการกรอก พร้อมชื่อผู้อ่านในเหตุผล (ทางไปต่อ: ต่อสายแล้วตัดออกจากรายการ)
        var reason = PlatformHolidayReadiness.EntryBlockedReason();
        Assert.NotNull(reason);
        Assert.Contains("SsoLateFee", reason);
        Assert.Contains("SettlementPosting", reason);
        // ทิศตรงข้าม: ต่อสายครบ (รายการว่าง) ⇒ กรอกได้
        Assert.Null(PlatformHolidayReadiness.EntryBlockedReason(Array.Empty<string>()));
        Assert.Contains("ผู้อ่าน X", PlatformHolidayReadiness.EntryBlockedReason(new[] { "ผู้อ่าน X" }));
    }
}

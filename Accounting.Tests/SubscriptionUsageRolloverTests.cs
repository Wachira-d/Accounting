using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// "โควต้า OCR ดูเหมือนจะไม่ได้ reset รายเดือน ตอนนี้ scan สะสมมา 3 เดือนยอดเพิ่มขึ้นเรื่อย ๆ
/// จนเต็ม 200 แสกนต่อไม่ได้แล้ว" — เจ้าของรายงาน 2026-09-24
///
/// ต้นเหตุ: 4 จุดที่ "ขึ้นเดือนใหม่" ใช้วันรีเซ็ตร่วมกันแต่ล้างตัวนับคนละชุด — จุดตรวจสิทธิ์
/// (ล้างแค่เอกสาร/JE) ชนะเกือบทุกเดือน แล้วเลื่อนวันรีเซ็ต ⇒ ตัวนับ OCR ไม่เคยถูกล้าง
///
/// ครึ่งแรก: ขึ้นเดือนใหม่ต้องล้าง **ครบทุกตัว** · ครึ่งหลัง: กลางเดือนต้อง **ไม่แตะ** ตัวนับเลย
/// (ด่านที่ล้างทุกครั้ง = ให้สแกนไม่จำกัด — คำเตือน F2 ข้อ 8)
/// </summary>
public class SubscriptionUsageRolloverTests
{
    private static Subscription Sub(DateTime resetAt, int n = 200) => new()
    {
        UsageResetDate = resetAt,
        CurrentMonthDocuments = n,
        CurrentMonthJournalEntries = n,
        CurrentMonthOcrPages = n,
        CurrentMonthAzureOcrPages = n,
        CurrentMonthLocalOcrPages = n,
    };

    // ═══════════ ครึ่งที่ 1 — เคสของเจ้าของ ═══════════

    [Fact]
    public void ขึ้นเดือนใหม่แล้วต้องล้างตัวนับทุกตัว_รวม_OCR_และแยกเครื่องยนต์()
    {
        var sub = Sub(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var rolled = SubscriptionUsageRollover.RollIfDue(sub, new DateTime(2026, 9, 24, 3, 0, 0, DateTimeKind.Utc));

        Assert.True(rolled);
        Assert.Equal(0, sub.CurrentMonthOcrPages);        // ← ตัวที่ไม่เคยถูกล้าง (บั๊กจริง)
        Assert.Equal(0, sub.CurrentMonthAzureOcrPages);   // ← ไม่มีเส้นที่รันจริงล้างให้เลย
        Assert.Equal(0, sub.CurrentMonthLocalOcrPages);
        Assert.Equal(0, sub.CurrentMonthDocuments);
        Assert.Equal(0, sub.CurrentMonthJournalEntries);
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), sub.UsageResetDate);
    }

    [Fact]
    public void ใครขึ้นเดือนใหม่ก่อนก็ตาม_อีกเส้นต้องไม่เจอตัวนับค้าง()
    {
        // จำลองลำดับจริง: ตัวตรวจสิทธิ์รันก่อน (เดิมล้างแค่เอกสาร) แล้วเส้น OCR ตามมา
        var sub = Sub(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var now = new DateTime(2026, 9, 2, 1, 0, 0, DateTimeKind.Utc);
        SubscriptionUsageRollover.RollIfDue(sub, now);          // เส้นแรก
        var secondRolled = SubscriptionUsageRollover.RollIfDue(sub, now);   // เส้นที่สอง

        Assert.False(secondRolled);                            // วันรีเซ็ตถูกเลื่อนแล้ว
        Assert.Equal(0, sub.CurrentMonthOcrPages);             // แต่ตัวนับถูกล้างไปแล้วโดยเส้นแรก
    }

    [Fact]
    public void ตัวนับค้างจากเดือนก่อน_เส้นอ่านต้องเห็นเป็นศูนย์()
    {
        // บริษัทพี่น้องใต้ License เดียวที่ยังไม่มีกิจกรรมเดือนนี้ — ห้ามรวมยอดเดือนก่อนเข้ามา
        var now = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(0, SubscriptionUsageRollover.Effective(180, new DateTime(2026, 9, 1), now));
    }

    [Fact]
    public void วันรีเซ็ตถัดไปคือวันที่หนึ่งของเดือนถัดไป_ข้ามปีได้()
    {
        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            SubscriptionUsageRollover.NextResetDate(new DateTime(2026, 12, 31, 23, 59, 0, DateTimeKind.Utc)));
    }

    // ═══════════ ครึ่งที่ 2 — กลางเดือนต้องไม่แตะ ═══════════

    [Fact]
    public void กลางเดือนต้องไม่ล้างตัวนับ_ไม่งั้นโควตาไม่มีความหมาย()
    {
        var sub = Sub(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), n: 57);
        var rolled = SubscriptionUsageRollover.RollIfDue(sub, new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc));

        Assert.False(rolled);
        Assert.Equal(57, sub.CurrentMonthOcrPages);
        Assert.Equal(57, sub.CurrentMonthAzureOcrPages);
        Assert.Equal(57, sub.CurrentMonthDocuments);
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), sub.UsageResetDate);
    }

    [Fact]
    public void ตัวนับของเดือนนี้_เส้นอ่านต้องเห็นค่าจริง()
    {
        var now = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(57, SubscriptionUsageRollover.Effective(57, new DateTime(2026, 10, 1), now));
    }

    [Fact]
    public void วินาทีสุดท้ายของเดือนยังไม่ขึ้นเดือนใหม่()
    {
        var sub = Sub(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), n: 199);
        Assert.False(SubscriptionUsageRollover.RollIfDue(sub, new DateTime(2026, 9, 30, 23, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(199, sub.CurrentMonthOcrPages);
    }
}

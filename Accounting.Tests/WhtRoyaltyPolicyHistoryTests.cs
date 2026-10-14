using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบ 204 (M2 · L6 · boundary) — ค่าตั้งค่าสิทธิ ม.40(3) บุคคลธรรมดา (ภ.ง.ด.2/3) <b>มีประวัติ</b>:
/// ปิดกลับเป็น ภ.ง.ด.3 = ปิดช่วงลงประวัติ (ไม่ทิ้ง) ⇒ ผลของวันที่ผ่านมาคงที่ · การบันทึกที่เปลี่ยนผลของงวดที่ยื่นแล้ว ⇒ รู้ได้ (เตือน/รับทราบ) ·
/// คำเตือน [WHT-FORM] หยุดเมื่อผู้ใช้ตัดสินเอง · วันตัดสินของการลงรายการมาจาก <see cref="WhtRoyaltyPolicy.PolicyDate"/> ตัวเดียว
///
/// ทั้งสองทิศ (CLAUDE.md F2#8): ไม่มีประวัติ/ค่าตั้งต้น ⇒ ผลเหมือนเดิมทุกวัน · มีประวัติ ⇒ เฉพาะช่วงในประวัติที่เป็น ภ.ง.ด.2
/// </summary>
public class WhtRoyaltyPolicyHistoryTests
{
    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 13, 5, 0, 0, DateTimeKind.Utc);   // 13/10/2026 12:00 น. (ไทย)

    private static WhtRoyaltySetting On(DateTime from) => new(WhtRoyaltyIndividualForm.Pnd2, from);

    // ══════════ ไม่มีประวัติ = ผลเดิมทุกวัน ══════════

    [Fact]
    public void ไม่มีประวัติ_ผลเท่าสูตรเดิมทุกวัน()
    {
        var days = Enumerable.Range(0, 120).Select(i => D(2026, 8, 1).AddDays(i)).ToList();
        foreach (var day in days)
        {
            Assert.False(default(WhtRoyaltySetting).AppliesTo(day));                                            // ค่าตั้งต้น
            Assert.False(new WhtRoyaltySetting(WhtRoyaltyIndividualForm.Pnd3, D(2026, 9, 1)).AppliesTo(day));   // ข้อมูลเก่า: เคยเปิดแล้วปิด (ก่อนมีประวัติ)
            Assert.Equal(day >= D(2026, 9, 1), On(D(2026, 9, 1)).AppliesTo(day));                               // ภ.ง.ด.2 ตามวันเริ่ม
            Assert.True(new WhtRoyaltySetting(WhtRoyaltyIndividualForm.Pnd2, null).AppliesTo(day));
        }
    }

    [Fact]
    public void บันทึกเรื่องอื่น_ไม่แตะค่าตั้ง_ไม่ประทับว่าตัดสินแล้ว()
    {
        Assert.Equal(default(WhtRoyaltySetting), WhtRoyaltyPolicy.ApplyUpdate(default, null, null, Now));
        Assert.Equal(default(WhtRoyaltySetting), WhtRoyaltyPolicy.ApplyUpdate(default, WhtRoyaltyIndividualForm.Pnd3, null, Now));   // หน้าเว็บส่งแบบเดิมมาทุกครั้ง
        var on = On(D(2026, 9, 1));
        Assert.Equal(on, WhtRoyaltyPolicy.ApplyUpdate(on, WhtRoyaltyIndividualForm.Pnd2, D(2026, 9, 1), Now));
        Assert.Null(WhtRoyaltyPolicy.FirstChangedDate(on, on));
    }

    // ══════════ M2 — ปิดกลับ = ปิดช่วง ไม่ทิ้ง ══════════

    [Fact]
    public void ปิดกลับเป็น_ภงด3_ช่วงที่ใช้ไปแล้วยังเป็น_ภงด2()
    {
        var on = WhtRoyaltyPolicy.ApplyUpdate(default, WhtRoyaltyIndividualForm.Pnd2, D(2026, 9, 1), Now);
        var off = WhtRoyaltyPolicy.ApplyUpdate(on, WhtRoyaltyIndividualForm.Pnd3, null, Now);   // ไม่ระบุวัน = วันนี้ (ไทย)
        Assert.Equal(WhtRoyaltyIndividualForm.Pnd3, off.Form);
        Assert.Equal("2026-09-01..2026-10-13", off.Pnd2History);
        Assert.False(off.AppliesTo(D(2026, 8, 31)));
        Assert.True(off.AppliesTo(D(2026, 9, 1)));
        Assert.True(off.AppliesTo(D(2026, 10, 12)));
        Assert.False(off.AppliesTo(D(2026, 10, 13)));   // วันที่กลับไปใช้ ภ.ง.ด.3 = ไม่รวม
        Assert.False(off.AppliesTo(D(2026, 12, 1)));
        // เวลาไทย: 12/10 23:30 น. (= 12/10 16:30 UTC) ยังอยู่ในช่วง
        Assert.True(off.AppliesTo(new DateTime(2026, 10, 12, 16, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void ปิดกลับระบุวัน_และวันกลับไม่หลังวันเริ่ม_ไม่มีช่วง()
    {
        var on = On(D(2026, 9, 1));
        var off = WhtRoyaltyPolicy.ApplyUpdate(on, WhtRoyaltyIndividualForm.Pnd3, null, Now, requestedPnd3From: D(2026, 9, 16));
        Assert.Equal("2026-09-01..2026-09-16", off.Pnd2History);
        Assert.True(off.AppliesTo(D(2026, 9, 15)));
        Assert.False(off.AppliesTo(D(2026, 9, 16)));
        // ผู้ใช้บอกว่าไม่เคยใช้จริง (กลับตั้งแต่วันเริ่ม) ⇒ ไม่มีช่วง = ภ.ง.ด.3 ทุกวัน
        var never = WhtRoyaltyPolicy.ApplyUpdate(on, WhtRoyaltyIndividualForm.Pnd3, null, Now, requestedPnd3From: D(2026, 9, 1));
        Assert.Null(never.Pnd2History);
        Assert.False(never.AppliesTo(D(2026, 9, 20)));
    }

    [Fact]
    public void เปิดใหม่หลังปิด_ช่วงเก่ายังอยู่_ช่วงระหว่างกลางเป็น_ภงด3()
    {
        var off = WhtRoyaltyPolicy.ApplyUpdate(On(D(2026, 9, 1)), WhtRoyaltyIndividualForm.Pnd3, null, Now, requestedPnd3From: D(2026, 10, 1));
        var reopened = WhtRoyaltyPolicy.ApplyUpdate(off, WhtRoyaltyIndividualForm.Pnd2, D(2026, 12, 1), Now);
        Assert.True(reopened.AppliesTo(D(2026, 9, 10)));
        Assert.False(reopened.AppliesTo(D(2026, 11, 10)));
        Assert.True(reopened.AppliesTo(D(2026, 12, 10)));
        // ปิดอีกครั้ง ⇒ ประวัติสองช่วง เรียงตามเวลา
        var off2 = WhtRoyaltyPolicy.ApplyUpdate(reopened, WhtRoyaltyIndividualForm.Pnd3, null, Now, requestedPnd3From: D(2027, 1, 1));
        Assert.Equal("2026-09-01..2026-10-01;2026-12-01..2027-01-01", off2.Pnd2History);
    }

    [Fact]
    public void ข้อมูลเก่า_ภงด2_ไม่มีวันเริ่ม_ปิดแล้วยังเป็น_ภงด2_ย้อนหลัง()
    {
        var off = WhtRoyaltyPolicy.ApplyUpdate(new WhtRoyaltySetting(WhtRoyaltyIndividualForm.Pnd2, null), WhtRoyaltyIndividualForm.Pnd3, null, Now);
        Assert.True(off.AppliesTo(D(2020, 1, 1)));
        Assert.False(off.AppliesTo(D(2026, 10, 13)));
    }

    [Fact]
    public void ประวัติ_canonical_รวมช่วงซ้อน_เรียงตามเวลา_ข้อมูลเสียไม่มีผล()
    {
        // ช่วง 1/9–1/10 → เปิดใหม่ย้อนไป 15/9 (ซ้อนช่วงเก่า) → ปิด 20/10 ⇒ ประวัติรวมเป็นช่วงเดียว
        var off = WhtRoyaltyPolicy.ApplyUpdate(On(D(2026, 9, 1)), WhtRoyaltyIndividualForm.Pnd3, null, Now, requestedPnd3From: D(2026, 10, 1));
        var reopened = WhtRoyaltyPolicy.ApplyUpdate(off, WhtRoyaltyIndividualForm.Pnd2, D(2026, 9, 15), Now);
        var merged = WhtRoyaltyPolicy.ApplyUpdate(reopened, WhtRoyaltyIndividualForm.Pnd3, null, Now, requestedPnd3From: D(2026, 10, 20));
        Assert.Equal("2026-09-01..2026-10-20", merged.Pnd2History);
        // ช่วงต่อท้ายที่เก่ากว่า ⇒ เรียงตามเวลา
        var earlier = WhtRoyaltyPolicy.ApplyUpdate(
            WhtRoyaltyPolicy.ApplyUpdate(merged, WhtRoyaltyIndividualForm.Pnd2, D(2026, 5, 1), Now),
            WhtRoyaltyIndividualForm.Pnd3, null, Now, requestedPnd3From: D(2026, 6, 1));
        Assert.Equal("2026-05-01..2026-06-01;2026-09-01..2026-10-20", earlier.Pnd2History);
        // ข้อมูลในฐานที่อ่านไม่ได้ ⇒ ไม่มีผล (ไม่แต่งช่วงขึ้นเอง)
        var garbage = new WhtRoyaltySetting(WhtRoyaltyIndividualForm.Pnd3, null, "garbage;2026-13-01..2026-14-01;2026-09-10..2026-09-01");
        Assert.False(garbage.AppliesTo(D(2026, 9, 5)));
        Assert.Null(WhtRoyaltyPolicy.Summary(garbage));
    }

    // ══════════ M2 — รู้ว่าการบันทึกแตะงวดที่ยื่นแล้ว ══════════

    [Fact]
    public void วันแรกที่ผลเปลี่ยน_ย้อนวันเริ่ม_และปิดกลับ()
    {
        var on = On(D(2026, 9, 1));
        Assert.Equal(D(2026, 8, 1), WhtRoyaltyPolicy.FirstChangedDate(on, WhtRoyaltyPolicy.ApplyUpdate(on, WhtRoyaltyIndividualForm.Pnd2, D(2026, 8, 1), Now)));
        Assert.Equal(D(2026, 9, 1), WhtRoyaltyPolicy.FirstChangedDate(on, WhtRoyaltyPolicy.ApplyUpdate(on, WhtRoyaltyIndividualForm.Pnd2, D(2026, 11, 1), Now)));
        Assert.Equal(D(2026, 10, 13), WhtRoyaltyPolicy.FirstChangedDate(on, WhtRoyaltyPolicy.ApplyUpdate(on, WhtRoyaltyIndividualForm.Pnd3, null, Now)));
        // ยืนยันแบบเดิม (ภ.ง.ด.3) ⇒ ไม่มีวันไหนเปลี่ยน
        Assert.Null(WhtRoyaltyPolicy.FirstChangedDate(default, WhtRoyaltyPolicy.ApplyUpdate(default, WhtRoyaltyIndividualForm.Pnd3, null, Now, confirm: true)));
        // ข้อมูลเก่า ภ.ง.ด.2 ไม่มีวันเริ่ม → ใส่วันเริ่ม ⇒ ผลเปลี่ยนตั้งแต่ต้น
        Assert.NotNull(WhtRoyaltyPolicy.FirstChangedDate(new WhtRoyaltySetting(WhtRoyaltyIndividualForm.Pnd2, null), on));
    }

    [Fact]
    public void งวดยื่นแล้วที่แตะ_เฉพาะเดือนที่ผลเปลี่ยนจริง()
    {
        var filed = new[]
        {
            (TaxType.WithholdingTax3, 2026, 7), (TaxType.WithholdingTax3, 2026, 8),
            (TaxType.WithholdingTax2, 2026, 9), (TaxType.WithholdingTax3, 2026, 9),
        };
        var on = On(D(2026, 9, 1));
        // ย้อนวันเริ่มไป 15/8 ⇒ แตะ ส.ค. เท่านั้น (ก.ย. เป็น ภ.ง.ด.2 ทั้งก่อนและหลัง · ก.ค. ไม่แตะ)
        var backdated = WhtRoyaltyPolicy.ApplyUpdate(on, WhtRoyaltyIndividualForm.Pnd2, D(2026, 8, 15), Now);
        Assert.Equal(new[] { (TaxType.WithholdingTax3, 2026, 8) }, WhtRoyaltyPolicy.TouchedFiledPeriods(on, backdated, filed));
        // ทิศตรงข้าม: ปิดกลับวันนี้ (ต.ค. ยังไม่ยื่น) ⇒ ไม่แตะงวดที่ยื่นแล้ว ⇒ ไม่เตือน
        var off = WhtRoyaltyPolicy.ApplyUpdate(on, WhtRoyaltyIndividualForm.Pnd3, null, Now);
        Assert.Empty(WhtRoyaltyPolicy.TouchedFiledPeriods(on, off, filed));
        // ปิดกลับย้อนไปกลาง ก.ย. ⇒ แตะ ก.ย. (ทั้ง ภ.ง.ด.2 และ 3 ของเดือนนั้น)
        var offBack = WhtRoyaltyPolicy.ApplyUpdate(on, WhtRoyaltyIndividualForm.Pnd3, null, Now, requestedPnd3From: D(2026, 9, 20));
        Assert.Equal(2, WhtRoyaltyPolicy.TouchedFiledPeriods(on, offBack, filed).Count);
        Assert.All(WhtRoyaltyPolicy.TouchedFiledPeriods(on, offBack, filed), p => Assert.Equal(9, p.Month));
        var msg = WhtRoyaltyPolicy.FiledPeriodWarning(WhtRoyaltyPolicy.TouchedFiledPeriods(on, backdated, filed));
        Assert.Contains("08/2569", msg);
        Assert.Contains("รับทราบ", msg);
    }

    // ══════════ L6 — คำเตือน [WHT-FORM] หยุดเมื่อผู้ใช้ตัดสินเอง ══════════

    [Fact]
    public void คำเตือน_เฉพาะค่าตั้งต้นที่ยังไม่ตัดสิน_ยืนยัน_ภงด3_แล้วเงียบ()
    {
        const string person = "1103700123456";
        string? Warn(WhtRoyaltySetting s) => WhtRoyaltyPolicy.ApprovalWarning(s, false, null, person, ContactType.Individual, "นายสมชาย ใจดี", new[] { "3" });
        Assert.NotNull(Warn(default));
        var confirmed3 = WhtRoyaltyPolicy.ApplyUpdate(default, WhtRoyaltyIndividualForm.Pnd3, null, Now, confirm: true);
        Assert.Equal(Now, confirmed3.DecidedAt);
        Assert.Null(Warn(confirmed3));
        // เปลี่ยนแบบ = ตัดสินแล้ว (กลับมา ภ.ง.ด.3 ก็ไม่เตือนอีก)
        var on = WhtRoyaltyPolicy.ApplyUpdate(default, WhtRoyaltyIndividualForm.Pnd2, null, Now);
        Assert.NotNull(on.DecidedAt);
        Assert.Null(Warn(WhtRoyaltyPolicy.ApplyUpdate(on, WhtRoyaltyIndividualForm.Pnd3, null, Now)));
        // บันทึกหน้าบริษัทเรื่องอื่น (แบบเดิม · ไม่ยืนยัน) ⇒ ยังไม่ถือว่าตัดสิน ⇒ ยังแนะนำ
        Assert.NotNull(Warn(WhtRoyaltyPolicy.ApplyUpdate(default, WhtRoyaltyIndividualForm.Pnd3, null, Now)));
    }

    // ══════════ boundary — วันตัดสินตัวเดียว (JE รายงวด = 50 ทวิ ของงวด) ══════════

    [Fact]
    public void วันตัดสิน_งวดจ่ายก่อน_แล้ววันจ่ายของเอกสาร_แล้ววันที่เอกสาร()
    {
        Assert.Equal(D(2026, 10, 5), WhtRoyaltyPolicy.PolicyDate(D(2026, 10, 5), D(2026, 9, 1), D(2026, 8, 1)));
        Assert.Equal(D(2026, 9, 1), WhtRoyaltyPolicy.PolicyDate(null, D(2026, 9, 1), D(2026, 8, 1)));
        Assert.Equal(D(2026, 8, 1), WhtRoyaltyPolicy.PolicyDate(null, null, D(2026, 8, 1)));
        // ใบแจ้งหนี้ 25/9 จ่ายงวด 5/10 หลังเปิด ภ.ง.ด.2 ตั้งแต่ 1/10 ⇒ งวดนั้น ภ.ง.ด.2 (เดิม JE ใช้วันเอกสาร = ภ.ง.ด.3 ขณะที่ 50 ทวิ = ภ.ง.ด.2)
        var on = On(D(2026, 10, 1));
        Assert.True(on.AppliesTo(WhtRoyaltyPolicy.PolicyDate(D(2026, 10, 5), null, D(2026, 9, 25))));
        Assert.False(on.AppliesTo(WhtRoyaltyPolicy.PolicyDate(null, null, D(2026, 9, 25))));
    }

    [Fact]
    public void ค่าตั้งที่ลงบัญชีไว้_ใช้วันงวดจ่ายถ้ามี_ต่างกันระหว่างงวด_บอก()
    {
        var on = On(D(2026, 10, 1));
        Assert.Equal((false, false), WhtRoyaltyPolicy.PostedFlag(on, D(2026, 9, 25), Array.Empty<DateTime>()));
        Assert.Equal((true, false), WhtRoyaltyPolicy.PostedFlag(on, D(2026, 9, 25), new[] { D(2026, 10, 5) }));
        Assert.Equal((true, true), WhtRoyaltyPolicy.PostedFlag(on, D(2026, 9, 25), new[] { D(2026, 10, 5), D(2026, 9, 28) }));
        // ค่าตั้งปัจจุบันเปลี่ยน (ปิดกลับ) ไม่ทำให้ของที่ลงไว้แล้วเปลี่ยน
        var off = WhtRoyaltyPolicy.ApplyUpdate(on, WhtRoyaltyIndividualForm.Pnd3, null, Now);
        Assert.Equal((true, false), WhtRoyaltyPolicy.PostedFlag(off, D(2026, 9, 25), new[] { D(2026, 10, 5) }));
    }

    [Fact]
    public void สรุปให้หน้าเว็บ_เซิร์ฟเวอร์คำนวณ()
    {
        Assert.Null(WhtRoyaltyPolicy.Summary(default));
        var off = WhtRoyaltyPolicy.ApplyUpdate(On(D(2026, 9, 1)), WhtRoyaltyIndividualForm.Pnd3, null, Now, requestedPnd3From: D(2026, 10, 1));
        var text = WhtRoyaltyPolicy.Summary(off)!;
        Assert.Contains("01/09/2569", text);
        Assert.Contains("30/09/2569", text);
        Assert.Contains("(ใช้อยู่)", WhtRoyaltyPolicy.Summary(On(D(2026, 9, 1)))!);
    }

    [Fact]
    public void migration_เพิ่มคอลัมน์ประวัติ_และประทับบริษัทที่เคยเปิด_ภงด2()
    {
        var stmts = DatabaseMigrationHelper.GetAlterStatements();
        Assert.Contains(stmts, s => s.Contains("\"WhtRoyaltyPnd2History\"") && s.Contains("ADD COLUMN IF NOT EXISTS"));
        Assert.Contains(stmts, s => s.Contains("\"WhtRoyaltyFormDecidedAt\"") && s.Contains("ADD COLUMN IF NOT EXISTS"));
        var stamp = Assert.Single(stmts, s => s.StartsWith("UPDATE \"Companies\" SET \"WhtRoyaltyFormDecidedAt\"", StringComparison.Ordinal));
        Assert.Contains("\"WhtRoyaltyFormDecidedAt\" IS NULL", stamp);   // รันซ้ำทุกบูตได้ ไม่ทับเวลาที่ผู้ใช้ตัดสิน
    }

    [Fact]
    public void ปิดภงด2แล้วเปิดใหม่_ไม่ระบุวัน_ไม่พลิกช่วงภงด3ระหว่างกลาง()
    {
        var now0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var on = WhtRoyaltyPolicy.ApplyUpdate(default, WhtRoyaltyIndividualForm.Pnd2, now0, now0);
        var closed = WhtRoyaltyPolicy.ApplyUpdate(on, WhtRoyaltyIndividualForm.Pnd3, null, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Null(closed.Pnd2From);   // ช่วงเดิมอยู่ในประวัติ — ไม่ค้างวันเริ่มให้ส่งกลับ
        Assert.True(WhtRoyaltyPolicy.AppliesTo(closed, new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc)));
        // เปิดใหม่ ต.ค. โดยไม่ระบุวัน ⇒ เริ่มวันที่ 1 ของเดือนนั้น · ช่วง เม.ย.–ก.ย. ยังเป็น ภ.ง.ด.3
        var reopened = WhtRoyaltyPolicy.ApplyUpdate(closed, WhtRoyaltyIndividualForm.Pnd2, null, new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));
        Assert.False(WhtRoyaltyPolicy.AppliesTo(reopened, new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc)));
        Assert.True(WhtRoyaltyPolicy.AppliesTo(reopened, new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc)));
        Assert.True(WhtRoyaltyPolicy.AppliesTo(reopened, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc)));
    }
}

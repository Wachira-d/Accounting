using Accounting.Helpers;
using Accounting.Services;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทิป POS ต้องลง **หนี้สิน** ไม่ใช่รายได้ (DECISION_AUDIT_2026-09-18 · D8-8)
///
/// ═══ ที่มา ═══
/// <c>CreateSalesJournalEntryAsync</c> เดิม: หาบัญชีทิปไม่เจอ → <b>ยัดเข้ารายได้ขาย</b>
/// + <c>LogWarning</c> ⇒ เงินที่บริษัทถือแทนพนักงานกลายเป็นรายได้ของบริษัท
/// (กำไรบวม · เสียภาษีเงินได้จากเงินที่ไม่ใช่ของตัวเอง · หนี้ที่ต้องจ่ายพนักงานหายจากงบ)
/// และ "ล้มเงียบ" เพราะ log ไม่ใช่การล้มดัง — ตอนนี้เปลี่ยนเป็น throw
/// (<c>POS-NO-TIP-ACCOUNT</c>) เหมือนฝั่งจ่ายทิปที่ throw อยู่แล้ว
/// (<c>TipPayoutService</c>) — สองฝั่งของเงินก้อนเดียวต้องตัดสินเหมือนกัน
///
/// เทสต์นี้คือ **ทิศตรงข้ามของการเพิ่ม throw** (กฎเหล็ก #4 F2 ข้อ 8):
/// พิสูจน์ว่าบริษัทที่ใช้ผังบัญชีมาตรฐานของระบบ **ไม่มีทางเจอ throw ตัวนี้**
/// (ถ้ารหัสในผังมาตรฐานถูกลบ/เปลี่ยนเมื่อไร เทสต์นี้จะล้มก่อนที่ร้านจะปิดบิลไม่ได้)
/// </summary>
public class PosTipLiabilityTests
{
    [Fact]
    public void รหัสบัญชีทิปตั้งต้นต้องมีอยู่ในผังบัญชีมาตรฐาน_ไม่งั้นปิดบิลที่มีทิปไม่ได้()
    {
        var common = ChartOfAccountTemplates.GetCommonAccounts();
        Assert.Contains(TipAccountResolver.DefaultCodes,
            code => common.Any(a => a.Code == code));
    }

    [Fact]
    public void บัญชีทิปตั้งต้นต้องเป็นหนี้สิน_ไม่ใช่รายได้()
    {
        var common = ChartOfAccountTemplates.GetCommonAccounts();
        foreach (var code in TipAccountResolver.DefaultCodes)
        {
            var acc = common.FirstOrDefault(a => a.Code == code);
            if (acc == null) continue;      // รหัสสำรองที่ผังมาตรฐานไม่มีก็ได้
            Assert.Equal(Accounting.Models.Enums.AccountType.Liability, acc.Type);
            Assert.True(acc.Level >= 4, $"{code} ต้องเป็นบัญชีย่อยที่ลงรายการได้");
        }
    }
}

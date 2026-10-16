using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// จุดตัดคำไทยของ PDF ฝั่งเซิร์ฟเวอร์ (<see cref="ThaiWordBreak"/>) — QuestPDF ตัดบรรทัดได้แค่ที่ช่องว่าง ⇒ ประโยคไทยยาวในแบบ 50 ทวิ
/// ถูกยกทั้งก้อนขึ้นบรรทัดใหม่ บรรทัดบนว่างครึ่งแถว (หน้าพิมพ์ของเบราว์เซอร์ตัดตามคำ) · 2026-10-16
/// </summary>
public class ThaiWordBreakTests
{
    private static bool IsCombining(char c) => c == 'ั' || (c >= 'ิ' && c <= 'ฺ') || (c >= '็' && c <= '๎');

    [Fact]
    public void ทุกข้อความของแบบ50ทวิ_พจนานุกรมครอบครบ()
    {
        // ข้อความของแบบเปลี่ยน/เพิ่มแล้วไม่เติมคำ ⇒ ล้มที่นี่ (ไม่ปล่อยให้ PDF กลับไปตัดบรรทัดผิดเงียบ ๆ)
        var missing = WhtCertFormText.AllStaticStrings().Where(s => !ThaiWordBreak.Covers(s)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void แทรกแล้วตัดจุดตัดออก_ได้ข้อความเดิมทุกตัวอักษร_และไม่ฉีกสระวรรณยุกต์()
    {
        foreach (var s in WhtCertFormText.AllStaticStrings())
        {
            var soft = ThaiWordBreak.Soft(s);
            Assert.Equal(s, soft.Replace(ThaiWordBreak.SoftBreak.ToString(), ""));
            for (var i = 0; i < soft.Length - 1; i++)
                if (soft[i] == ThaiWordBreak.SoftBreak) Assert.False(IsCombining(soft[i + 1]), s);
        }
    }

    [Fact]
    public void ประโยคยาวไม่มีช่องว่าง_มีจุดตัดระหว่างคำ()
    {
        // ต้นเหตุ: "ตามคำสั่งกรมสรรพากรที่ออกตามมาตรา" ไม่มีช่องว่าง ⇒ QuestPDF ยกทั้งก้อน
        Assert.Equal("ตาม​มาตรา", ThaiWordBreak.Soft("ตามมาตรา"));
        Assert.Contains("กรมสรรพากร​ที่", ThaiWordBreak.Soft(WhtCertFormText.Row5));
    }

    [Fact]
    public void ทิศตรงข้าม_คำที่ไม่รู้จักและข้อความไม่ใช่ไทย_ไม่ถูกแตะ()
    {
        Assert.Equal("ABC 123-45/6", ThaiWordBreak.Soft("ABC 123-45/6"));
        Assert.Equal("ฬฬฬ", ThaiWordBreak.Soft("ฬฬฬ"));                 // ไม่รู้จัก = ไม่มีจุดตัด (พฤติกรรมเดิม)
        Assert.Equal("ฬฬฬ ภาษี", ThaiWordBreak.Soft("ฬฬฬ ภาษี"));       // คำเดียวต่อช่วง = ไม่มีจุดตัด
        Assert.Equal("", ThaiWordBreak.Soft(null));
        Assert.True(ThaiWordBreak.Covers("ABC"));
        Assert.False(ThaiWordBreak.Covers("ฬฬฬ"));
    }
}

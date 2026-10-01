using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม AI · A-AI1 (report-H H-1 · P1) — คลังจับคู่ธนาคารห้ามสอนตัวเอง
///
/// ของจริงที่พัง: ปุ่ม "✨ AI จับคู่จากประวัติ" ติ๊กคู่จากคลัง → กดยืนยัน → <c>TimesConfirmed</c> ของแพตเทิร์นที่เสนอเองเพิ่ม →
/// คะแนน <c>0.2·min(1, TimesConfirmed/10)</c> และ Wilson ของนักเรียนโตเองจนทะลุ short-circuit 0.85. สองครึ่ง: กดผ่านรัว ๆ
/// ต้องไม่ดันความมั่นใจ · และเมื่อทุกคำยืนยันเป็นแบบตั้งใจ สูตรต้องเท่าสูตรเดิม · แถวเก่าเริ่ม Explicit = 0 (คำตัดสินข้อ 99)
/// </summary>
public class BankPatternEvidenceTests
{
    private static readonly DateTime Last = new(2026, 9, 30);

    [Theory]
    [InlineData(null, UserChoiceSource.Implicit)]
    [InlineData("", UserChoiceSource.Implicit)]
    [InlineData("Explicit", UserChoiceSource.Explicit)]
    [InlineData("explicit", UserChoiceSource.Explicit)]
    [InlineData("BulkApprove", UserChoiceSource.BulkApprove)]
    [InlineData("Implicit", UserChoiceSource.Implicit)]
    [InlineData("1", UserChoiceSource.Implicit)]        // ตัวเลขไม่รับ — enum ออก/เข้าเป็นชื่อเท่านั้น
    [InlineData("99", UserChoiceSource.Implicit)]
    [InlineData("อะไรก็ได้", UserChoiceSource.Implicit)]
    public void แหล่งของคำยืนยัน_ไม่ส่งหรืออ่านไม่ออก_ต้องเป็น_Implicit(string? raw, UserChoiceSource expected)
        => Assert.Equal(expected, BankPatternEvidence.ParseSource(raw));

    [Fact]
    public void เฉพาะ_Explicit_ที่นับเป็นหลักฐาน()
    {
        Assert.Equal(1, BankPatternEvidence.ExplicitIncrement(UserChoiceSource.Explicit));
        Assert.Equal(0, BankPatternEvidence.ExplicitIncrement(UserChoiceSource.Implicit));
        Assert.Equal(0, BankPatternEvidence.ExplicitIncrement(UserChoiceSource.BulkApprove));
    }

    [Fact]
    public void กดรับข้อเสนอของคลังรัว_ๆ_ต้องไม่ดันความเกี่ยวข้องและความมั่นใจของนักเรียน()
    {
        // จำลองการยืนยันคู่ที่คลังติ๊กให้เอง 50 ครั้ง (Implicit) บนแพตเทิร์นที่ผู้ใช้เคยเลือกเอง 1 ครั้ง
        int times = 1, explicitCount = 1;
        var relevanceBefore = BankPatternEvidence.Relevance(0.8, true, explicitCount, 3);
        var studentBefore = BankPatternEvidence.StudentConfidence(explicitCount, explicitCount, times, times);
        for (var i = 0; i < 50; i++)
        {
            times += 1;
            explicitCount += BankPatternEvidence.ExplicitIncrement(UserChoiceSource.Implicit);
        }
        Assert.Equal(51, times);
        Assert.Equal(1, explicitCount);
        Assert.Equal(relevanceBefore, BankPatternEvidence.Relevance(0.8, true, explicitCount, 3));
        Assert.Equal(studentBefore, BankPatternEvidence.StudentConfidence(explicitCount, explicitCount, times, times));
        Assert.True(studentBefore < 0.85m, "คำยืนยันแบบตั้งใจครั้งเดียวต้องไม่ทะลุ short-circuit");
    }

    [Fact]
    public void แพตเทิร์นที่ไม่มีคำยืนยันแบบตั้งใจเลย_ตอบได้แต่ต้องต่ำกว่าเกณฑ์_apply_และ_short_circuit()
    {
        var c = BankPatternEvidence.StudentConfidence(0, 0, 500, 500);
        Assert.True(c > 0m, "cold-start ต้องไม่ว่างเปล่า (กฎเหล็ก #1 ข้อ 3)");
        Assert.True(c <= BankPatternEvidence.ImplicitOnlyConfidenceCap);
        Assert.True(c < 0.70m);
    }

    [Fact]
    public void ผู้ใช้เลือกคู่เองมากขึ้น_หลักฐานต้องโตจริง()
    {
        Assert.True(BankPatternEvidence.Relevance(0.5, true, 10, 0) > BankPatternEvidence.Relevance(0.5, true, 1, 0));
        Assert.True(BankPatternEvidence.StudentConfidence(30, 30, 30, 30) > 0.85m,
            "ผู้ใช้เลือกเอง 30 ครั้งตรงกันหมด = ผ่าน short-circuit ได้ (นักเรียนโตจริง)");
    }

    [Fact]
    public void คำยืนยันแบบตั้งใจทั้งหมด_ความมั่นใจต้องเท่าสูตรเดิม()
    {
        // ครึ่ง "ใบถูกไม่ถูกแตะ": ผู้ใช้เลือกเองทุกครั้ง (Explicit = TimesConfirmed) ⇒ นักเรียนตอบเท่าสูตรเดิม
        // สูตรเดิม = Wilson(TimesConfirmed, max(TimesConfirmed, totalForKey))
        foreach (var (n, total) in new[] { (1, 1), (3, 5), (12, 12), (40, 50) })
            Assert.Equal(BankPatternEvidence.Wilson(n, Math.Max(n, total)),
                BankPatternEvidence.StudentConfidence(n, total, n, total));
        // ความเกี่ยวข้องเดิม: (0.5·jaccard + bucket + 0.2·min(1, n/10)) × recency
        Assert.Equal((0.5 * 0.6 + 0.3 + 0.2 * 0.4) * 1.0, BankPatternEvidence.Relevance(0.6, true, 4, 0), 10);
    }

    [Fact]
    public void แถวเก่าก่อนรอบ_201_เริ่ม_Explicit_0_ต้องตอบได้แต่ไม่ถึงเกณฑ์ติ๊ก_คำตัดสินข้อ_99()
    {
        // migration ไม่ backfill ⇒ แพตเทิร์นเก่า 40 ครั้งที่แยกไม่ออกว่าใครเลือก = หลักฐานอ่อน (ทิศที่มองเห็น: เสนอน้อยลง ไม่ผิดเงียบ)
        var c = BankPatternEvidence.StudentConfidence(0, 0, 40, 40);
        Assert.True(c > 0m && c <= BankPatternEvidence.ImplicitOnlyConfidenceCap);
        Assert.True(BankPatternEvidence.Relevance(0.6, true, 0, 0) < BankPatternEvidence.Relevance(0.6, true, 40, 0));
    }

    [Fact]
    public void เหตุผลบนจอต้องแยก_เลือกเอง_กับ_ยืนยันผ่าน()
    {
        Assert.Contains("เลือกคู่แบบนี้เอง 2 ครั้ง", BankPatternEvidence.Reason(2, 9, Last));
        Assert.Contains("30/09/2026", BankPatternEvidence.Reason(2, 9, Last));
        Assert.Contains("ยังไม่มีใครเลือกคู่แบบนี้เอง", BankPatternEvidence.Reason(0, 9, Last));
    }
}

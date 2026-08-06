using Accounting.Services.Implementations.Matching;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เคสจริงจากชื่อผู้โอนบน statement ธนาคารไทย — ทุกเคสเคยทำให้ระบบเดิม
/// (Levenshtein ตรง ๆ) ได้คะแนน 0 เพราะคนละ code point
/// </summary>
public class CounterpartyNameMatcherTests
{
    // ── ข้ามภาษา ไทย ↔ อังกฤษ ────────────────────────────────────
    [Theory]
    [InlineData("WACHIRA DILOKSAMPHAN", "วชิระ ดิลกสัมพันธ์")]
    [InlineData("วชิระ ดิลกสัมพันธ์", "WACHIRA DILOKSAMPHAN")]   // สลับด้าน
    [InlineData("VACHIRA DILOKSAMPAN", "วชิระ ดิลกสัมพันธ์")]     // ถอดเสียงคนละสำนัก
    [InlineData("MR. WACHIRA DILOKSAMPHAN", "วชิระ ดิลกสัมพันธ์")] // มีคำนำหน้า
    [InlineData("DILOKSAMPHAN WACHIRA", "วชิระ ดิลกสัมพันธ์")]     // สลับชื่อ-สกุล
    [InlineData("SOMCHAI SIRIPORN", "สมชาย ศิริพร")]
    public void CrossScript_FullName_IsConfident(string statement, string known)
    {
        var m = CounterpartyNameMatcher.Match(statement, known);
        Assert.True(m.Score >= CounterpartyNameMatcher.ConfidentThreshold,
            $"คาดว่า ≥{CounterpartyNameMatcher.ConfidentThreshold} แต่ได้ {m.Score} ({m.Reason})");
        Assert.True(m.CrossScript);
    }

    // ── ตัดคำ / ปิดบัง — ต้อง "เจอ" แต่ไม่ควรมั่นใจเต็มร้อย ────────
    [Theory]
    [InlineData("WACHI", "วชิระ")]                       // แบงก์ตัดความยาว
    [InlineData("WACHIRA D***", "วชิระ ดิลกสัมพันธ์")]     // ปิดบังนามสกุล
    [InlineData("นาย วชิระ ดิลกสั", "วชิระ ดิลกสัมพันธ์")]  // ตัดท้ายฝั่งไทย
    [InlineData("W. DILOKSAMPHAN", "วชิระ ดิลกสัมพันธ์")]  // ย่ออักษรแรก
    public void Truncated_IsFoundButNotCertain(string statement, string known)
    {
        var m = CounterpartyNameMatcher.Match(statement, known);
        Assert.True(m.Score >= CounterpartyNameMatcher.MinimumUsefulThreshold,
            $"ควรเจอ แต่ได้ {m.Score}");
        Assert.True(m.Truncated);
    }

    // ── นิติบุคคล: รูปแบบบริษัทต่างกัน + ขยะช่องทางปน ──────────────
    [Fact]
    public void CompanyName_AcrossForms_AndChannelNoise()
    {
        var statement = "รับเงินจากการขายด้วย Thai QR Payment | จาก KB000001813229 "
                      + "Take Time Nature Resort | EDC/K SHOP/MYQR";
        var m = CounterpartyNameMatcher.Match(statement, "บริษัท เทค ไทม์ เนเจอร์ รีสอร์ท จำกัด");
        Assert.True(m.Score >= CounterpartyNameMatcher.MinimumUsefulThreshold,
            $"ควรเจอชื่อบริษัทท่ามกลางขยะช่องทาง แต่ได้ {m.Score} ({m.Reason})");

        // ชื่อเดียวกันคนละรูปนิติบุคคล = ต้องเท่ากับตรงเป๊ะ
        var same = CounterpartyNameMatcher.Match(
            "TAKE TIME NATURE RESORT CO., LTD.", "Take Time Nature Resort");
        Assert.True(same.Score >= CounterpartyNameMatcher.ConfidentThreshold);
    }

    /// <summary>ขยะช่องทางต้องไม่ปั่นคะแนนให้คู่ที่ไม่เกี่ยวกัน — ทุกบรรทัด
    /// มีคำว่า "Thai QR Payment" เหมือนกันหมด ถ้าไม่ตัดทิ้งจะตรงกันทุกคู่</summary>
    [Fact]
    public void ChannelNoise_DoesNotCreateFalseMatch()
    {
        var a = "รับเงินจากการขายด้วย Thai QR Payment | สมชาย ใจดี | EDC/K SHOP/MYQR";
        var m = CounterpartyNameMatcher.Match(a, "บริษัท เทค ไทม์ เนเจอร์ รีสอร์ท จำกัด");
        Assert.True(m.Score < CounterpartyNameMatcher.MinimumUsefulThreshold,
            $"คนละคนต้องไม่ตรง แต่ได้ {m.Score} ({m.Reason})");
    }

    // ── คนละคนจริง ๆ ต้องไม่ตรง ────────────────────────────────
    [Theory]
    [InlineData("SOMCHAI JAIDEE", "วชิระ ดิลกสัมพันธ์")]
    [InlineData("สมหญิง รักไทย", "วชิระ ดิลกสัมพันธ์")]
    [InlineData("PRASERT KITTI", "ศิริพร ธนาคาร")]
    public void DifferentPeople_DoNotMatch(string statement, string known)
    {
        var m = CounterpartyNameMatcher.Match(statement, known);
        Assert.True(m.Score < CounterpartyNameMatcher.MinimumUsefulThreshold,
            $"ต้องไม่ตรง แต่ได้ {m.Score} ({m.Reason})");
    }

    // ── โครงพยัญชนะระดับหน่วย ─────────────────────────────────
    [Theory]
    [InlineData("วชิระ", "WACHIRA")]
    [InlineData("วชิระ", "Vachira")]
    [InlineData("วชิระ", "Watchira")]
    [InlineData("ดิลกสัมพันธ์", "DILOKSAMPHAN")]
    [InlineData("สมชาย", "Somchai")]
    [InlineData("ศิริพร", "Siriporn")]
    [InlineData("ธนาคาร", "Thanakan")]
    [InlineData("ชัยวัฒน์", "Chaiwat")]
    [InlineData("อนันต์", "Anan")]
    [InlineData("เชียงใหม่", "Chiangmai")]
    [InlineData("ภูเก็ต", "Phuket")]
    [InlineData("กิตติ", "Gitti")]        // ก ถอดเป็น g สำนักเก่า
    [InlineData("ประเสริฐ", "Prasert")]
    [InlineData("รีสอร์ท", "RESORT")]
    public void Skeleton_MatchesAcrossScripts(string thai, string latin)
        => Assert.Equal(ThaiPhonetics.Skeleton(thai), ThaiPhonetics.Skeleton(latin));

    /// <summary>ชื่อคนละคนต้องได้โครงคนละอัน — กันกฎยุบเสียงหลวมเกินจน
    /// ทุกชื่อกลายเป็นคีย์เดียวกัน (ซึ่งจะทำให้ทุกอย่าง "ตรง")</summary>
    [Theory]
    [InlineData("วชิระ", "สมชาย")]
    [InlineData("ศิริพร", "ประเสริฐ")]
    [InlineData("Wachira", "Somchai")]
    public void Skeleton_DistinguishesDifferentNames(string a, string b)
        => Assert.NotEqual(ThaiPhonetics.Skeleton(a), ThaiPhonetics.Skeleton(b));

    [Fact]
    public void EmptyOrNull_IsNoMatch()
    {
        Assert.Equal(0.0, CounterpartyNameMatcher.Match(null, "วชิระ").Score);
        Assert.Equal(0.0, CounterpartyNameMatcher.Match("วชิระ", "").Score);
        Assert.Equal(0.0, CounterpartyNameMatcher.Match("   ", "วชิระ").Score);
        // บรรทัดที่มีแต่เลขบัญชี — ไม่มี token ชื่อเหลือเลย
        Assert.Equal(0.0, CounterpartyNameMatcher.Match("KB000001813229 064-1-70621-3", "วชิระ").Score);
    }

    [Fact]
    public void Best_PicksHighestScoringContact()
    {
        var contacts = new[] { "สมชาย ใจดี", "วชิระ ดิลกสัมพันธ์", "ศิริพร ธนาคาร" };
        var hit = CounterpartyNameMatcher.Best(
            "โอนเงินจาก WACHIRA DILOKSAMPHAN", contacts, c => c);
        Assert.NotNull(hit);
        Assert.Equal("วชิระ ดิลกสัมพันธ์", hit!.Value.Item);
    }
}

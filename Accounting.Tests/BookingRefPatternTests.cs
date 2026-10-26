using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม W10 2026-10-26 (R-56) — รูปแบบเลขอ้างอิงการจองตั้งค่าได้ต่อคีย์ integration (<see cref="BookingRefPattern"/>) ·
/// สองทิศ: ค่าเริ่มต้น = 2 รูปเดิมทุกตัวอักษร (ผล <c>BookingDepositDrawdownTests</c> ไม่เปลี่ยน) · รูปที่ตั้งเพิ่มทำให้ด่าน "เห็น" มัดจำที่ลงด้วยเลขรูปอื่น ·
/// regex ผิด (คอมไพล์ไม่ผ่าน · ไม่มีกลุ่ม key · จับข้อความว่าง · ยาวเกิน · เกินจำนวน) ⇒ ปฏิเสธพร้อมข้อความไทย · ค่าที่เก็บผิดรูป ⇒ Merge ข้าม ไม่ล้ม
/// </summary>
public class BookingRefPatternTests
{
    private static readonly Guid Dep = Guid.NewGuid();
    private const string Custom = @"BK-(?<key>\d{4,})";

    // ── ทิศตรงข้าม: ค่าเริ่มต้นเท่าเดิม ──
    [Fact]
    public void Default_IsExactlyTheTwoLegacyShapes_AndExtractsLikeBefore()
    {
        Assert.Equal(2, BookingRefPattern.DefaultSources.Count);
        Assert.Equal(new[] { "149433" }, BookingDepositDrawdown.ExtractKeys("RES-149433-CHK").ToArray());
        Assert.Equal(new[] { "149433" }, BookingDepositDrawdown.ExtractKeys("รับรู้รายได้ Checkout - การจอง #149433 (คุณปราณี)").ToArray());
        Assert.Equal(new[] { "101-2609-0001" }, BookingDepositDrawdown.ExtractKeys("RES-101-2609-0001").ToArray());
        Assert.Empty(BookingDepositDrawdown.ExtractKeys("BK-77001", "INV-2026-0001"));
        // overload ที่รับ patterns ด้วย Default ให้ผลเท่า overload เดิมทุกตัว
        Assert.Equal(BookingDepositDrawdown.ExtractKeys("RES-149433-CHK", "การจอง #5", "#1234"),
            BookingDepositDrawdown.ExtractKeys(BookingRefPattern.Default, "RES-149433-CHK", "การจอง #5", "#1234"));
    }

    [Fact]
    public void Merge_NoConfig_IsDefaultOnly_AndDuplicatesOfDefaultAreNotAdded()
    {
        Assert.Equal(2, BookingRefPattern.Merge(Array.Empty<string?>()).Count);
        Assert.Equal(2, BookingRefPattern.Merge(new string?[] { null, "", "[]" }).Count);
        Assert.Equal(2, BookingRefPattern.Merge(new[] { BookingRefPattern.ToJson(BookingRefPattern.DefaultSources) }).Count);
    }

    // ── รูปที่ตั้งเพิ่ม ⇒ ด่านเห็น ──
    [Fact]
    public void CustomPattern_ExtractsKey_DefaultDoesNot()
    {
        var merged = BookingRefPattern.Merge(new[] { BookingRefPattern.ToJson(new[] { Custom }) });
        Assert.Equal(3, merged.Count);
        Assert.Equal(new[] { "77001" }, BookingDepositDrawdown.ExtractKeys(merged, "รับมัดจำ BK-77001").ToArray());
        Assert.Empty(BookingDepositDrawdown.ExtractKeys("รับมัดจำ BK-77001"));
        Assert.True(BookingDepositDrawdown.Mentions(merged, "77001", "BK-77001"));
        Assert.False(BookingDepositDrawdown.Mentions(merged, "7700", "BK-77001"));       // ตรงตัว ไม่ใช่ substring
        // รูปเดิมยังทำงานในชุดรวม + สองคีย์คนละระบบรวมกันได้
        Assert.Equal(new[] { "149433", "77001" }, BookingDepositDrawdown.ExtractKeys(merged, "RES-149433 / BK-77001").ToArray());
        var two = BookingRefPattern.Merge(new[] { BookingRefPattern.ToJson(new[] { Custom }), BookingRefPattern.ToJson(new[] { @"PMS#(?<key>\d{5})" }) });
        Assert.Equal(4, two.Count);
        Assert.Equal(new[] { "12345" }, BookingDepositDrawdown.ExtractKeys(two, "PMS#12345").ToArray());
    }

    [Fact]
    public void AssignDrawdown_SeesDrawdownOnlyWithConfiguredPattern()
    {
        var merged = BookingRefPattern.Merge(new[] { BookingRefPattern.ToJson(new[] { Custom }) });
        var lines = new[] { new IncomingJournalLine(Dep, 1_000m, 0m, null), new IncomingJournalLine(Guid.NewGuid(), 0m, 1_000m, null) };
        var seen = BookingDepositDrawdown.AssignDrawdown(merged, new string?[] { "BK-77001", "Checkout" }, lines, id => id == Dep);
        Assert.Equal(1_000m, seen["77001"][Dep]);
        // ทิศตรงข้าม: ไม่ตั้งค่า ⇒ ไม่เห็น ⇒ ด่านไม่ตัดสิน (G3 — ปล่อยผ่านเหมือนเดิม)
        Assert.Empty(BookingDepositDrawdown.AssignDrawdown(new string?[] { "BK-77001", "Checkout" }, lines, id => id == Dep));
    }

    // ── validate ──
    [Theory]
    [InlineData(@"BK-(?<key>\d{4,})")]
    [InlineData(@"(?<![A-Z])PMS#(?<key>\d{5})(?!\d)")]
    [InlineData(@"จองเลข\s*(?<key>\d{3,}(?:-\d+)*)")]
    [InlineData(@"RES-(?<key>\d+)")]
    public void Validate_AcceptsWellFormed(string source) => Assert.Null(BookingRefPattern.Validate(source));

    /// <summary>ฝ่ายค้านข้อ 2: pattern กว้าง ⇒ เลขใบกำกับ/ปี/ยอดเงิน/เลขภาษี/เลขเอกสารกลายเป็น "เลขการจอง" ⇒ ปฏิเสธพร้อมบอกข้อความที่จับได้ + ตัวอย่างที่ใช้ได้</summary>
    [Theory]
    [InlineData(@"(?<key>\d+)")]
    [InlineData(@"(?<key>\d{3,})")]
    [InlineData(@"(?<key>\d{4}-\d{2}-\d{2})")]
    [InlineData(@"(?<key>\d{13})")]
    [InlineData(@"-(?<key>\d{4})")]
    public void Validate_RejectsBroadPatterns_ThatCatchOrdinaryAccountingText(string source)
    {
        var err = BookingRefPattern.Validate(source);
        Assert.NotNull(err);
        Assert.Contains("กว้างเกิน", err);
        Assert.Contains("BK-(?<key>", err);
        // และค่าเริ่มต้นเองต้องผ่านด่านนี้ (ไม่งั้นด่านปิดตัวเอง)
        Assert.All(BookingRefPattern.DefaultSources, d => Assert.Null(BookingRefPattern.Validate(d)));
    }

    /// <summary>ฝ่ายค้านข้อ 1: regex ระเบิด (nested alternation) บนข้อความยาว ⇒ ไม่โยนออกจาก ExtractKeys · ข้ามรูปนั้น · รูปอื่นยังทำงาน · แจ้ง sink ไม่เกิน 1 ครั้งต่อรูป</summary>
    [Fact]
    public void ExtractKeys_CatastrophicPattern_DoesNotThrow_SkipsPattern_ReportsOnce()
    {
        var calls = new List<string>();
        var prev = BookingRefPattern.TimeoutSink;
        BookingRefPattern.TimeoutSink = calls.Add;
        try
        {
            var evil = BookingRefPattern.Compile(@"(?<key>(a|aa)+)-X");
            var patterns = new List<System.Text.RegularExpressions.Regex>(BookingRefPattern.Default) { evil };
            var text = "RES-149433 " + new string('a', 48) + "-Y";
            var keys = BookingDepositDrawdown.ExtractKeys(patterns, text);
            Assert.Equal(new[] { "149433" }, keys.ToArray());          // รูปเริ่มต้นยังทำงาน · evil ไม่ match/timeout ⇒ ไม่มีคีย์เพิ่ม
            BookingDepositDrawdown.ExtractKeys(patterns, text);          // ครั้งที่สอง — ถ้า timeout ก็แจ้งซ้ำไม่ได้
            Assert.True(calls.Count <= 1, string.Join(" | ", calls));
            Assert.All(calls, c => Assert.Contains("ข้ามรูปนี้", c));
        }
        finally { BookingRefPattern.TimeoutSink = prev; }
    }

    /// <summary>ฝ่ายค้านข้อ 3: Merge คืน Regex ตัวเดียวกัน (cache ต่อ source) ไม่ compile ซ้ำทุกคำขอ</summary>
    [Fact]
    public void Merge_CachesCompiledRegexPerSource()
    {
        var json = BookingRefPattern.ToJson(new[] { @"CACHE-(?<key>\d{4,})" });
        var a = BookingRefPattern.Merge(new[] { json })[2];
        var b = BookingRefPattern.Merge(new[] { json })[2];
        Assert.Same(a, b);
    }

    [Theory]
    [InlineData("", "ว่าง")]
    [InlineData(@"BK-(?<key>\d{4,}", "ไม่ใช่ regex ที่ถูกต้อง")]            // วงเล็บไม่ปิด
    [InlineData(@"BK-(\d{4,})", "ต้องมีกลุ่มชื่อ (?<key>…)")]              // ไม่มีกลุ่ม key
    [InlineData(@"(?<key>\d*)", "จับข้อความว่างได้")]                      // จับทุกอย่าง
    [InlineData(@"(?<key>[A-Z]*)INV", "กลุ่ม key ว่าง")]                  // จับได้แต่ key ว่าง
    public void Validate_RejectsWithThaiMessage(string source, string expectedFragment)
    {
        var err = BookingRefPattern.Validate(source);
        Assert.NotNull(err);
        Assert.Contains(expectedFragment, err);
    }

    [Fact]
    public void Validate_RejectsTooLong_AndValidateAll_RejectsTooMany_ReportsFirstBad()
    {
        Assert.Contains("ยาวเกิน", BookingRefPattern.Validate("BK-(?<key>" + new string('x', BookingRefPattern.MaxLength) + ")"));
        var many = Enumerable.Range(0, BookingRefPattern.MaxPatterns + 1).Select(i => $@"K{i}-(?<key>\d{{4,}})").ToList();
        Assert.Contains($"เกิน {BookingRefPattern.MaxPatterns} รูป", BookingRefPattern.ValidateAll(many));
        var firstBad = BookingRefPattern.ValidateAll(new[] { Custom, @"BK-(\d+)", @"(" });
        Assert.NotNull(firstBad);
        Assert.Contains("ต้องมีกลุ่มชื่อ", firstBad);
        // ชุดว่าง/ null = ผ่าน (กลับไปใช้ค่าเริ่มต้น)
        Assert.Null(BookingRefPattern.ValidateAll(null));
        Assert.Null(BookingRefPattern.ValidateAll(Array.Empty<string>()));
        Assert.Null(BookingRefPattern.ValidateAll(new[] { " ", "" }));
    }

    // ── เก็บ/อ่าน ──
    [Fact]
    public void Json_RoundTrip_TrimsDedupes_EmptyIsNull()
    {
        Assert.Null(BookingRefPattern.ToJson(null));
        Assert.Null(BookingRefPattern.ToJson(new[] { "", "  " }));
        var json = BookingRefPattern.ToJson(new[] { " " + Custom + " ", Custom, @"PMS#(?<key>\d{5})" })!;
        Assert.Equal(new[] { Custom, @"PMS#(?<key>\d{5})" }, BookingRefPattern.ParseJson(json).ToArray());
        Assert.Empty(BookingRefPattern.ParseJson(null));
        Assert.Throws<System.Text.Json.JsonException>(() => BookingRefPattern.ParseJson("not json"));
    }

    [Fact]
    public void Merge_SkipsBrokenStoredValues_InsteadOfFailingTheGate()
    {
        // ค่าที่ถูกแก้ในฐานด้วยมือ: JSON พัง · regex พัง · ไม่มีกลุ่ม key — ด่านยังทำงานด้วยค่าเริ่มต้น + รูปที่ดี
        var merged = BookingRefPattern.Merge(new[] { "{broken", @"[""BK-(?<key>\\d{4,}"", ""X-(\\d+)"", ""BK-(?<key>\\d{4,})""]" });
        Assert.Equal(3, merged.Count);
        Assert.Equal(new[] { "77001" }, BookingRefPattern.ExtractKeys(merged, "BK-77001").ToArray());
    }
}

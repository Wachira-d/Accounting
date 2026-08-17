using Xunit;

namespace Accounting.Tests;

/// <summary>
/// แก้ผังบัญชีของ JE ที่เอกสารลงไปแล้ว (AdjustDocumentJournalEntryAsync)
///
/// ที่มา (ผู้ใช้): "ยังไม่เจอปุ่มให้แก้ผังบัญชีที่ลง JE — ควรมีปุ่มตรวจสอบ JE
/// และแก้ไขรายการทั้งวันที่ ผังบัญชี เพิ่ม ลด ตรงนั้นเลย"
///
/// วิธีที่เลือก: ผู้ใช้ส่ง "สถานะปลายทาง" ของใบสำคัญมา ระบบคำนวณผลต่างต่อบัญชี
/// แล้วลง **ใบปรับปรุงใหม่** — ใบเดิมไม่ถูกแก้ (audit trail ครบ) และ GL ถูกต้อง
///
/// สิ่งที่ทำให้เปิดให้แก้อิสระได้อย่างปลอดภัย = 2 ค่าคงที่:
///   • ยอดรวมห้ามเปลี่ยน (ยอดผิด = แก้ที่เอกสาร ไม่ใช่แอบแก้ผ่าน GL)
///   • บัญชีคุมห้ามขยับ (ภ.พ.30 / ภ.ง.ด. / อายุหนี้ / มัดจำ อ่านยอดพวกนี้อยู่)
/// </summary>
public class AdjustDocumentJournalTests
{
    public sealed record Line(string Code, decimal Debit, decimal Credit);

    private static readonly HashSet<string> Control = new()
    {
        "11310", "11610", "11640", "21210",
        "21510", "21520", "21610", "21711", "21712", "21713",
        "21911", "21912", "21913", "21916", "21917", "21918",
    };

    private static Dictionary<string, decimal> Net(IEnumerable<Line> lines)
    {
        var m = new Dictionary<string, decimal>();
        foreach (var l in lines) m[l.Code] = m.GetValueOrDefault(l.Code) + l.Debit - l.Credit;
        return m;
    }

    /// <summary>mirror ของ guard ใน service — คืนข้อความปัญหา หรือ null ถ้าผ่าน</summary>
    private static string? Validate(IReadOnlyList<Line> original, IReadOnlyList<Line> wanted)
    {
        if (wanted.Count < 2) return "lines";
        if (wanted.Any(l => l.Debit > 0 && l.Credit > 0)) return "bothSides";
        if (wanted.Any(l => l.Debit < 0 || l.Credit < 0)) return "negative";

        var wDr = wanted.Sum(l => l.Debit);
        var wCr = wanted.Sum(l => l.Credit);
        if (wDr != wCr) return "unbalanced";
        if (wDr != original.Sum(l => l.Debit)) return "totalChanged";

        var o = Net(original);
        var w = Net(wanted);
        foreach (var code in o.Keys.Union(w.Keys).Where(Control.Contains))
            if (o.GetValueOrDefault(code) != w.GetValueOrDefault(code)) return "controlMoved";

        return null;
    }

    /// <summary>ผลต่างที่จะกลายเป็นใบปรับปรุง (บวก = Dr, ลบ = Cr)</summary>
    private static List<(string Code, decimal Amount)> Deltas(
        IReadOnlyList<Line> original, IReadOnlyList<Line> wanted)
    {
        var o = Net(original);
        var w = Net(wanted);
        return o.Keys.Union(w.Keys)
            .Select(c => (Code: c, Amount: w.GetValueOrDefault(c) - o.GetValueOrDefault(c)))
            .Where(d => d.Amount != 0m)
            .OrderBy(d => d.Code)
            .ToList();
    }

    // ใบสำคัญจ่ายตัวอย่าง: ค่าโฆษณา 1,000 + ภาษีซื้อ 70 / เจ้าหนี้ 1,070
    private static readonly Line[] Pv =
    {
        new("53120", 1000m, 0m),   // ค่าโฆษณา (ลงผิด)
        new("11610", 70m, 0m),     // ภาษีซื้อ — บัญชีคุม
        new("21210", 0m, 1070m),   // เจ้าหนี้การค้า — บัญชีคุม
    };

    [Fact]
    public void Moving_an_expense_to_the_right_account_produces_one_pair()
    {
        var wanted = new[]
        {
            new Line("53310", 1000m, 0m),   // ค่าที่ปรึกษา (ผังที่ถูก)
            new Line("11610", 70m, 0m),
            new Line("21210", 0m, 1070m),
        };
        Assert.Null(Validate(Pv, wanted));

        var d = Deltas(Pv, wanted);
        Assert.Equal(2, d.Count);
        Assert.Equal(("53120", -1000m), d[0]);   // Cr ผังเดิม (ล้างออก)
        Assert.Equal(("53310", 1000m), d[1]);    // Dr ผังใหม่
        Assert.Equal(0m, d.Sum(x => x.Amount));  // ใบปรับปรุงสมดุลเสมอ
    }

    [Fact]
    public void Splitting_one_expense_into_two_accounts_is_allowed()
    {
        // "เพิ่ม/ลดบรรทัด" ที่ผู้ใช้ขอ — แยกค่าโฆษณาเป็นค่าโฆษณา 600 + ค่าที่ปรึกษา 400
        var wanted = new[]
        {
            new Line("53120", 600m, 0m),
            new Line("53310", 400m, 0m),
            new Line("11610", 70m, 0m),
            new Line("21210", 0m, 1070m),
        };
        Assert.Null(Validate(Pv, wanted));

        var d = Deltas(Pv, wanted);
        Assert.Equal(2, d.Count);
        Assert.Equal(("53120", -400m), d[0]);
        Assert.Equal(("53310", 400m), d[1]);
    }

    [Fact]
    public void Merging_two_lines_back_into_one_is_allowed()
    {
        var original = new[]
        {
            new Line("53120", 600m, 0m), new Line("53310", 400m, 0m),
            new Line("11610", 70m, 0m), new Line("21210", 0m, 1070m),
        };
        var wanted = new[]
        {
            new Line("53310", 1000m, 0m),
            new Line("11610", 70m, 0m), new Line("21210", 0m, 1070m),
        };
        Assert.Null(Validate(original, wanted));
        var d = Deltas(original, wanted);
        Assert.Equal(("53120", -600m), d[0]);
        Assert.Equal(("53310", 600m), d[1]);
    }

    [Fact]
    public void Changing_the_total_is_rejected()
    {
        // ยอดผิดต้องแก้ที่เอกสาร — ไม่งั้นเอกสารกับบัญชีหลุดจากกันเงียบ ๆ
        var wanted = new[]
        {
            new Line("53120", 2000m, 0m),
            new Line("11610", 70m, 0m),
            new Line("21210", 0m, 2070m),
        };
        Assert.Equal("totalChanged", Validate(Pv, wanted));
    }

    [Fact]
    public void Unbalanced_input_is_rejected()
    {
        var wanted = new[] { new Line("53310", 1000m, 0m), new Line("21210", 0m, 900m) };
        Assert.Equal("unbalanced", Validate(Pv, wanted));
    }

    [Theory]
    [InlineData("11610")]   // ภาษีซื้อ → ภ.พ.30
    [InlineData("21210")]   // เจ้าหนี้ → ยอดค้างจ่าย/อายุหนี้
    public void Moving_a_control_account_is_rejected(string code)
    {
        // ย้ายยอดออกจากบัญชีคุมไปบัญชีอื่น — ยอดรวมเท่าเดิม แต่รายงานพัง
        var wanted = Pv.Select(l => l.Code == code
            ? l with { Code = "53999", Debit = l.Debit, Credit = l.Credit }
            : l).ToArray();
        Assert.Equal("controlMoved", Validate(Pv, wanted));
    }

    [Fact]
    public void Adding_a_control_account_that_was_not_there_is_rejected()
    {
        // แอบเพิ่มภาษีขายเข้ามาเอง = สร้างยอด ภ.พ.30 ที่ไม่มีเอกสารรองรับ
        // ยอดรวมยังเท่าเดิม (1,070) และสมดุล — จึงผ่านสองด่านแรก แต่ต้องตกที่
        // ด่านบัญชีคุม ไม่งั้นจะสร้างยอดภาษีขายที่ไม่มีเอกสารรองรับได้
        var wanted = new[]
        {
            new Line("53120", 1000m, 0m),
            new Line("11610", 70m, 0m),
            new Line("21911", 0m, 70m),
            new Line("21210", 0m, 1000m),
        };
        Assert.Equal(1070m, wanted.Sum(l => l.Debit));
        Assert.Equal(1070m, wanted.Sum(l => l.Credit));
        Assert.Equal("controlMoved", Validate(Pv, wanted));
    }

    [Fact]
    public void A_line_cannot_carry_both_debit_and_credit()
    {
        var wanted = new[] { new Line("53310", 1000m, 1000m), new Line("21210", 0m, 1070m) };
        Assert.Equal("bothSides", Validate(Pv, wanted));
    }

    [Fact]
    public void No_change_produces_no_adjusting_entry()
    {
        Assert.Null(Validate(Pv, Pv));
        Assert.Empty(Deltas(Pv, Pv));
    }

    [Fact]
    public void The_original_entry_is_never_modified()
    {
        // ใบปรับปรุงเป็นใบใหม่ ยอดสุทธิของทั้งคู่รวมกัน = สถานะปลายทางที่ต้องการ
        var wanted = new[]
        {
            new Line("53310", 1000m, 0m), new Line("11610", 70m, 0m), new Line("21210", 0m, 1070m),
        };
        var after = Net(Pv);
        foreach (var (code, amount) in Deltas(Pv, wanted))
            after[code] = after.GetValueOrDefault(code) + amount;

        Assert.Equal(0m, after["53120"]);        // ผังเดิมถูกล้างเป็นศูนย์
        Assert.Equal(1000m, after["53310"]);     // ผังใหม่รับยอดมาเต็ม
        Assert.Equal(70m, after["11610"]);       // บัญชีคุมไม่ขยับ
        Assert.Equal(-1070m, after["21210"]);
        Assert.Equal(0m, after.Values.Sum());    // งบยังสมดุล
    }

    /// <summary>สิทธิ์ปรับปรุงต่อใบสำคัญ — mirror ของ GetDocumentJournalEntriesAsync</summary>
    [Theory]
    [InlineData("Posted", false, false, true)]
    [InlineData("Draft", false, false, false)]     // ยังไม่ผ่านรายการ — แก้ที่ใบตรง ๆ
    [InlineData("Posted", true, false, false)]     // ตัวกลับ — ไปแก้ที่ใบต้นฉบับ
    [InlineData("Posted", false, true, false)]     // ถูกกลับรายการไปแล้ว
    public void Adjustability_depends_on_entry_state(
        string status, bool isReversal, bool alreadyReversed, bool expected)
    {
        var can = status == "Posted" && !isReversal && !alreadyReversed;
        Assert.Equal(expected, can);
    }
}

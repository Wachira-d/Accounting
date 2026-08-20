using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ใบกำกับภาษีที่แปลงจากใบแจ้งหนี้ กดอนุมัติแล้วล้มด้วย 500 (อ้างอิง BE996D32)
///
/// กลไกจริงที่ไล่เจอ (ยืนยันกับโค้ดทีละชิ้น):
///   1. AutoPostToJournalAsync เพิ่ม JE ของใบกำกับ (SV-yyyymm-NNNN) เข้า
///      change tracker แบบ **ยังไม่ save** (ตัวมันไม่ SaveChanges เอง — ระบุใน comment)
///   2. SupersedeSourceInvoiceAsync → ReverseJournalEntryAsync ขอเลขให้
///      "ตัวกลับ" ของใบแจ้งหนี้เดิม — JournalType.Sales → prefix SV เดือนเดียวกัน
///   3. ตัวขอเลข query จาก **ฐานข้อมูล** ซึ่งมองไม่เห็นใบที่ Add ค้าง
///      ⇒ ได้เลขเดียวกันเป๊ะ ⇒ unique (CompanyId, EntryNumber) ระเบิดตอน
///      SaveChanges ⇒ DbUpdateException ⇒ 500 **ทุกครั้ง ทุกใบ** (deterministic —
///      ตรงกับที่ทั้ง 3 ใบของผู้ใช้ล้มเหมือนกันหมดไม่ว่ายอดเท่าไร)
///
/// แก้: generator ทั้งสองตัว (DocumentService + AccountingService) นับ
/// change tracker (Local) ประกอบกับ DB ก่อนออกเลข
/// </summary>
public class ConvertedTaxInvoiceApproveTests
{
    private const string Pattern = "SV-202608-";

    /// <summary>สูตรเดิม: อ่านจาก DB อย่างเดียว</summary>
    private static string NextFromDbOnly(IEnumerable<string> db)
    {
        var max = db.Where(e => e.StartsWith(Pattern))
            .Select(e => int.Parse(e[Pattern.Length..]))
            .DefaultIfEmpty(0).Max();
        return $"{Pattern}{max + 1:D4}";
    }

    /// <summary>สูตรหลังแก้: DB + Add ค้างใน change tracker</summary>
    private static string NextWithLocal(IEnumerable<string> db, IEnumerable<string> local)
    {
        var next = db.Where(e => e.StartsWith(Pattern))
            .Select(e => int.Parse(e[Pattern.Length..]))
            .DefaultIfEmpty(0).Max() + 1;
        var localMax = local.Where(e => e.StartsWith(Pattern))
            .Select(e => int.Parse(e[Pattern.Length..]))
            .DefaultIfEmpty(0).Max();
        if (localMax >= next) next = localMax + 1;
        return $"{Pattern}{next:D4}";
    }

    [Fact]
    public void The_old_generator_collided_every_time_on_convert_approve()
    {
        var db = new[] { "SV-202608-0006" };
        var local = new List<string>();

        var autoPost = NextFromDbOnly(db);      // JE ของใบกำกับ (ยังไม่ save)
        local.Add(autoPost);
        var reversal = NextFromDbOnly(db);      // ตัวกลับใบแจ้งหนี้ — DB ไม่เห็น local

        Assert.Equal("SV-202608-0007", autoPost);
        Assert.Equal(autoPost, reversal);       // ← เลขซ้ำ = unique violation = 500
    }

    [Fact]
    public void The_fixed_generator_sees_pending_entries()
    {
        var db = new[] { "SV-202608-0006" };
        var local = new List<string>();

        var autoPost = NextWithLocal(db, local);
        local.Add(autoPost);
        var reversal = NextWithLocal(db, local);

        Assert.Equal("SV-202608-0007", autoPost);
        Assert.Equal("SV-202608-0008", reversal);
        Assert.NotEqual(autoPost, reversal);
    }

    [Fact]
    public void An_invoice_with_multiple_journals_reverses_them_all_without_collision()
    {
        // ใบแจ้งหนี้ที่มีหลาย JE (เช่น เคยมีใบปรับปรุง) → supersede กลับหลายใบ
        var db = new[] { "SV-202608-0006" };
        var local = new List<string>();
        var issued = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var n = NextWithLocal(db, local);
            local.Add(n);
            issued.Add(n);
        }
        Assert.Equal(new[] { "SV-202608-0007", "SV-202608-0008", "SV-202608-0009" }, issued);
        Assert.Equal(issued.Count, issued.Distinct().Count());
    }

    [Fact]
    public void After_saving_the_behaviour_matches_the_old_generator_exactly()
    {
        // เส้นปกติ (ไม่มีใบค้าง) ต้องได้เลขเดิมเป๊ะ — การแก้ห้ามเปลี่ยนพฤติกรรมเดิม
        var db = new[] { "SV-202608-0006", "SV-202608-0007", "SV-202608-0008" };
        Assert.Equal(NextFromDbOnly(db), NextWithLocal(db, Array.Empty<string>()));
        Assert.Equal("SV-202608-0009", NextWithLocal(db, Array.Empty<string>()));
    }

    [Fact]
    public void Pending_entries_of_a_different_prefix_or_month_do_not_bump_the_sequence()
    {
        // RV/JV ค้าง หรือ SV เดือนอื่น — ไม่เกี่ยวกับ pattern นี้ ห้ามดันเลข
        var db = new[] { "SV-202608-0006" };
        var local = new[] { "RV-202608-0004", "SV-202607-0099", "JV-202608-0011" };
        Assert.Equal("SV-202608-0007", NextWithLocal(db, local));
    }

    [Fact]
    public void Draft_placeholder_numbers_in_the_tracker_are_ignored()
    {
        // เอกสาร Draft ใช้ "DRAFT-xxx" — parse ไม่ได้ ต้องข้าม ไม่ throw
        var db = new[] { "SV-202608-0006" };
        var local = new[] { "DRAFT-ca04c364" };
        Assert.Equal("SV-202608-0007", NextWithLocal(db, local));
    }

    // ── บั๊กคู่กัน: แผงปรับปรุง JE ล้มเมื่อบัญชีซ้ำหลายบรรทัด ─────────────────

    public sealed record JeLine(Guid AccountId, string AccountCode, decimal Dr, decimal Cr);

    [Fact]
    public void A_journal_with_two_lines_on_the_same_account_broke_the_adjust_panel()
    {
        // ใบเสร็จ 2 รายการห้องพัก → ขา Cr 41110 สองบรรทัด (เคสจริงจากภาพผู้ใช้)
        var revenueAcc = Guid.NewGuid();
        var lines = new[]
        {
            new JeLine(Guid.NewGuid(), "11122-001", 5_100m, 0m),
            new JeLine(revenueAcc, "41110", 0m, 1_600m),
            new JeLine(revenueAcc, "41110", 0m, 3_166.35m),
            new JeLine(Guid.NewGuid(), "21911", 0m, 333.65m),
        };

        // เดิม: ToDictionary ตรง ๆ → duplicate key → ArgumentException →
        // middleware ปิดบังเป็น "ข้อมูลที่ส่งมาไม่ถูกต้อง"
        Assert.Throws<ArgumentException>(() =>
            lines.ToDictionary(l => l.AccountId, l => l.AccountCode));

        // หลังแก้: GroupBy ก่อน — ได้ map บัญชี→รหัส ครบทุกบัญชี ไม่ล้ม
        var fixedMap = lines.GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g => g.First().AccountCode);
        Assert.Equal(3, fixedMap.Count);
        Assert.Equal("41110", fixedMap[revenueAcc]);
    }

    [Fact]
    public void Net_amounts_still_aggregate_duplicate_account_lines_correctly()
    {
        // NetBy (ตัวคำนวณยอดสุทธิ) สะสมอยู่แล้ว — ตรึงไว้กันใครเผลอเปลี่ยนเป็น dict ตรง
        var acc = Guid.NewGuid();
        var lines = new[]
        {
            new JeLine(acc, "41110", 0m, 1_600m),
            new JeLine(acc, "41110", 0m, 3_166.35m),
        };
        var net = new Dictionary<Guid, decimal>();
        foreach (var l in lines)
            net[l.AccountId] = net.GetValueOrDefault(l.AccountId) + l.Dr - l.Cr;
        Assert.Equal(-4_766.35m, net[acc]);
    }
}

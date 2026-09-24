using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ยกเลิกบิล POS เมื่อ JE ขายถูกกลับรายการด้วยมือไปแล้ว (รอบ 193 · ฝ่ายค้าน M2) — <see cref="PosVoidSaleJournal"/>
///
/// <para>ที่มา: JE ขาย POS ไม่ผูกเอกสาร ⇒ ผู้ทำบัญชีกลับเองจากหน้า JE ได้ · <c>VoidOrderAsync</c> เดิมกลับซ้ำเสมอ ⇒
/// <c>ReverseJournalEntryAsync</c> โยน "กลับได้เฉพาะ Posted" ⇒ ยกเลิกบิลล้มทุกครั้ง ไม่มีทางไปต่อ</para>
///
/// <para>สองครึ่ง: (1) สายที่กลับครบแล้วต้อง "ข้าม" (ห้ามกลับสองครั้ง) · สายที่กลับไม่ครบต้องบล็อกพร้อมข้อความไทย
/// (2) บิลปกติที่ JE ขายยังไม่มีใครแตะ ต้องได้ "กลับใบเดิม" เหมือนเดิมทุกประการ</para>
/// </summary>
public class PosVoidSaleJournalTests
{
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly Guid Revenue = Guid.NewGuid();
    private static readonly Guid OutputVat = Guid.NewGuid();
    private static readonly Guid Cogs = Guid.NewGuid();
    private static readonly Guid Inventory = Guid.NewGuid();

    /// <summary>JE ขายบิล 107 บาท: Dr เงินสด 107 / Cr รายได้ 100 / Cr ภาษีขาย 7 · Dr ต้นทุน 40 / Cr สินค้า 40</summary>
    private static PosJournalLineAmount[] SaleLines() => new[]
    {
        new PosJournalLineAmount(Cash, 107m, 0m),
        new PosJournalLineAmount(Revenue, 0m, 100m),
        new PosJournalLineAmount(OutputVat, 0m, 7m),
        new PosJournalLineAmount(Cogs, 40m, 0m),
        new PosJournalLineAmount(Inventory, 0m, 40m),
    };

    private static PosJournalLineAmount[] Mirror(IEnumerable<PosJournalLineAmount> lines)
        => lines.Select(l => new PosJournalLineAmount(l.AccountId, l.Credit, l.Debit)).ToArray();

    private static PosJournalChainEntry Je(string no, JournalEntryStatus st, PosJournalLineAmount[] lines)
        => new(Guid.NewGuid(), no, st, lines);

    // ═══════════ ครึ่งที่ 2 — บิลปกติต้องได้พฤติกรรมเดิม ═══════════

    [Fact]
    public void JE_ขายที่ยังไม่มีใครแตะ_กลับใบเดิม()
    {
        var sale = Je("SV-001", JournalEntryStatus.Posted, SaleLines());
        var d = PosVoidSaleJournal.Decide(new[] { sale }, "POS-0001");
        Assert.Equal(PosVoidSaleJournalAction.Reverse, d.Action);
        Assert.Equal(sale.Id, d.ReverseEntryId);
    }

    [Fact]
    public void JE_ขายยอด0ที่ยังPosted_ยังต้องกลับเพื่อปิดสาย()
    {
        var sale = Je("SV-000", JournalEntryStatus.Posted, Array.Empty<PosJournalLineAmount>());
        Assert.Equal(PosVoidSaleJournalAction.Reverse, PosVoidSaleJournal.Decide(new[] { sale }, "POS-0000").Action);
    }

    // ═══════════ ครึ่งที่ 1 — เคสที่เคยล้มทุกครั้ง ═══════════

    [Fact]
    public void JE_ขายถูกกลับด้วยมือครบแล้ว_ข้ามไม่กลับซ้ำ()
    {
        var sale = Je("SV-002", JournalEntryStatus.Reversed, SaleLines());
        var rev = Je("SV-003", JournalEntryStatus.Posted, Mirror(SaleLines()));
        var d = PosVoidSaleJournal.Decide(new[] { sale, rev }, "POS-0002");
        Assert.Equal(PosVoidSaleJournalAction.SkipAlreadyReversed, d.Action);
        Assert.Null(d.ReverseEntryId);
        Assert.Contains("SV-003", d.Message);           // บอกว่าถูกกลับด้วยใบไหน (ลง audit)
    }

    [Fact]
    public void กลับแล้วกลับคืน_ยอดขายยังอยู่ใน_GL_กลับใบสุดท้ายใบเดียว()
    {
        // SV-004 (ขาย) → SV-005 (กลับ) → SV-006 (กลับตัวกลับ = ขายกลับมามีผล) ⇒ กลับ SV-006 ครั้งเดียวก็สุทธิศูนย์
        var sale = Je("SV-004", JournalEntryStatus.Reversed, SaleLines());
        var rev = Je("SV-005", JournalEntryStatus.Reversed, Mirror(SaleLines()));
        var reRev = Je("SV-006", JournalEntryStatus.Posted, SaleLines());
        var d = PosVoidSaleJournal.Decide(new[] { sale, rev, reRev }, "POS-0004");
        Assert.Equal(PosVoidSaleJournalAction.Reverse, d.Action);
        Assert.Equal(reRev.Id, d.ReverseEntryId);
        Assert.NotEqual(sale.Id, d.ReverseEntryId);      // ห้ามกลับใบเดิมซ้ำ
    }

    [Fact]
    public void กลับไปบางส่วน_บล็อกพร้อมข้อความไทยที่บอกทางไปต่อ()
    {
        // ตัวกลับที่ยอดไม่ครบ (เช่นกลับเฉพาะรายได้/ภาษี ไม่กลับต้นทุน)
        var sale = Je("SV-007", JournalEntryStatus.Reversed, SaleLines());
        var partial = Je("SV-008", JournalEntryStatus.Posted, new[]
        {
            new PosJournalLineAmount(Cash, 0m, 107m),
            new PosJournalLineAmount(Revenue, 100m, 0m),
            new PosJournalLineAmount(OutputVat, 7m, 0m),
        });
        var d = PosVoidSaleJournal.Decide(new[] { sale, partial }, "POS-0007");
        Assert.Equal(PosVoidSaleJournalAction.Block, d.Action);
        Assert.Contains("บางส่วน", d.Message);
        Assert.Contains("ใบสำคัญปรับปรุง", d.Message);   // ทางไปต่อ
        Assert.Contains("2 บัญชี", d.Message);           // ต้นทุน + สินค้า ยังค้าง
    }

    [Fact]
    public void JE_ขายยังเป็นร่าง_บล็อก()
    {
        var d = PosVoidSaleJournal.Decide(new[] { Je("SV-009", JournalEntryStatus.Draft, SaleLines()) }, "POS-0009");
        Assert.Equal(PosVoidSaleJournalAction.Block, d.Action);
        Assert.Contains("ร่าง", d.Message);
    }

    [Fact]
    public void JE_ขายถูกVoid_ไม่มียอดใน_GL_ข้าม()
    {
        var d = PosVoidSaleJournal.Decide(new[] { Je("SV-010", JournalEntryStatus.Voided, SaleLines()) }, "POS-0010");
        Assert.Equal(PosVoidSaleJournalAction.SkipAlreadyReversed, d.Action);
    }

    [Fact]
    public void หา_JE_ขายไม่เจอ_บล็อกไม่ใช่ข้ามเงียบ()
    {
        var d = PosVoidSaleJournal.Decide(Array.Empty<PosJournalChainEntry>(), "POS-0011");
        Assert.Equal(PosVoidSaleJournalAction.Block, d.Action);
        Assert.Contains("POS-0011", d.Message);
    }

    // ═══════════ หลังฝ่ายค้าน (P3) — คืนเงินใช้ตัวตัดสินเดียวกัน ═══════════

    [Fact]
    public void คืนเงิน_JE_ขายยังอยู่ใน_GL_คืนได้()
    {
        var sale = Je("SV-020", JournalEntryStatus.Posted, SaleLines());
        Assert.Null(PosVoidSaleJournal.RefundBlockMessage(PosVoidSaleJournal.Decide(new[] { sale }, "POS-0020"), "POS-0020"));
        // กลับแล้วกลับคืน = ยอดขายกลับมามีผล ⇒ คืนเงินได้
        var chain = new[] { Je("SV-021", JournalEntryStatus.Reversed, SaleLines()),
            Je("SV-022", JournalEntryStatus.Reversed, Mirror(SaleLines())), Je("SV-023", JournalEntryStatus.Posted, SaleLines()) };
        Assert.Null(PosVoidSaleJournal.RefundBlockMessage(PosVoidSaleJournal.Decide(chain, "POS-0021"), "POS-0021"));
    }

    [Fact]
    public void คืนเงิน_JE_ขายถูกกลับด้วยมือแล้ว_บล็อก_ชี้ให้ใช้ยกเลิกบิล()
    {
        // เดิม: คืนเงินลง Dr รายได้/ภาษีขายซ้ำกับที่ถูกกลับไปแล้ว ⇒ รายได้ของบิลติดลบ
        var chain = new[] { Je("SV-024", JournalEntryStatus.Reversed, SaleLines()),
            Je("SV-025", JournalEntryStatus.Posted, Mirror(SaleLines())) };
        var msg = PosVoidSaleJournal.RefundBlockMessage(PosVoidSaleJournal.Decide(chain, "POS-0024"), "POS-0024");
        Assert.NotNull(msg);
        Assert.Contains("ติดลบ", msg);
        Assert.Contains("ยกเลิกบิล", msg);
    }
}

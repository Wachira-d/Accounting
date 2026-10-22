using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม O 2026-10-22 — "JE ไหนนับเข้ารายงาน" (<see cref="ReportableJournal"/>) ·
/// reproduce บั๊กที่ทีม M พบ: รายได้ 1,000 ถูกยกเลิกด้วยใบกลับรายการ ⇒ ตัวกรองเดิม (Posted อย่างเดียว) ได้ −1,000 · ตัวกลางได้ 0 ·
/// ทิศตรงข้าม: ชุดที่ไม่เคยกลับรายการได้ยอดเดิมทุกสตางค์ · ร่าง/ยกเลิกก่อนลงบัญชีไม่นับ · ใบปิดบัญชีไม่นับในผลการดำเนินงานแต่นับในยอดคงเหลือ
/// </summary>
public class ReportableJournalTests
{
    private static readonly ChartOfAccount Revenue = new() { AccountCode = "41000", AccountName = "รายได้จากการขาย", AccountType = AccountType.Revenue };
    private static readonly ChartOfAccount Cash = new() { AccountCode = "11111", AccountName = "เงินสด", AccountType = AccountType.Asset };

    /// <summary>ใบสำคัญ Dr เงินสด / Cr รายได้ (amount &lt; 0 = กลับด้าน)</summary>
    private static JournalEntry Je(decimal amount, JournalEntryStatus status, bool closing = false)
    {
        var je = new JournalEntry { EntryNumber = "JV", Status = status, IsClosingEntry = closing, EntryDate = new DateTime(2026, 5, 1) };
        var a = Math.Abs(amount);
        var revLine = new JournalEntryLine { JournalEntry = je, Account = Revenue, AccountId = Revenue.Id, CreditAmount = amount > 0 ? a : 0, DebitAmount = amount < 0 ? a : 0 };
        var cashLine = new JournalEntryLine { JournalEntry = je, Account = Cash, AccountId = Cash.Id, DebitAmount = amount > 0 ? a : 0, CreditAmount = amount < 0 ? a : 0 };
        je.Lines.Add(revLine);
        je.Lines.Add(cashLine);
        return je;
    }

    private static decimal RevenueOf(IQueryable<JournalEntryLine> q)
        => q.Where(l => l.Account.AccountType == AccountType.Revenue).Sum(l => l.CreditAmount - l.DebitAmount);

    private static IQueryable<JournalEntryLine> Lines(params JournalEntry[] entries)
        => entries.SelectMany(e => e.Lines).ToList().AsQueryable();

    [Fact]
    public void Reproduce_รายได้ที่ถูกกลับรายการ_ตัวกรองเดิมได้ลบพัน_ตัวกลางได้ศูนย์()
    {
        var original = Je(1_000m, JournalEntryStatus.Reversed);   // ใบเดิม — ReverseJournalEntryAsync เปลี่ยนเป็น Reversed
        var reversal = Je(-1_000m, JournalEntryStatus.Posted);    // ใบกลับรายการ — Posted
        var lines = Lines(original, reversal);

        // บั๊ก (ตัวกรองเดิมของ GenerateCitReport / BuildCitFreshnessAsync / ภ.ง.ด.51 / FP&A)
        Assert.Equal(-1_000m, RevenueOf(lines.Where(l => l.JournalEntry.Status == JournalEntryStatus.Posted)));
        // กติกากลาง
        Assert.Equal(0m, RevenueOf(lines.WherePeriodActivity()));
        Assert.Equal(0m, RevenueOf(lines.WhereInLedger()));
    }

    [Fact]
    public void ทิศตรงข้าม_ชุดที่ไม่เคยกลับรายการ_ได้ยอดเท่าตัวกรองเดิมทุกสตางค์()
    {
        var lines = Lines(Je(1_234.56m, JournalEntryStatus.Posted), Je(0.01m, JournalEntryStatus.Posted),
            Je(-99.99m, JournalEntryStatus.Posted), Je(70_000.07m, JournalEntryStatus.Posted));
        var old = RevenueOf(lines.Where(l => l.JournalEntry.Status == JournalEntryStatus.Posted && !l.JournalEntry.IsClosingEntry));
        Assert.Equal(71_134.65m, old);
        Assert.Equal(old, RevenueOf(lines.WherePeriodActivity()));
        Assert.Equal(old, RevenueOf(lines.WhereInLedger()));
    }

    [Fact]
    public void ร่างและยกเลิกก่อนลงบัญชี_ไม่นับ_ใบปิดบัญชีไม่นับในผลการดำเนินงานแต่นับในยอดคงเหลือ()
    {
        var lines = Lines(
            Je(500m, JournalEntryStatus.Posted),
            Je(9_999m, JournalEntryStatus.Draft),
            Je(8_888m, JournalEntryStatus.Voided),
            Je(-500m, JournalEntryStatus.Posted, closing: true));   // ใบปิดบัญชี: Dr รายได้ ล้างเข้ากำไรสะสม
        Assert.Equal(500m, RevenueOf(lines.WherePeriodActivity()));
        Assert.Equal(0m, RevenueOf(lines.WhereInLedger()));
    }

    [Fact]
    public void ใบกลับรายการของใบปิดบัญชี_เปิดปีใหม่_ไม่กระทบผลการดำเนินงาน()
    {
        // YearEndReopen: ใบปิดเดิม → Reversed · ใบกลับ (IsClosingEntry=true) → Posted ⇒ ผลการดำเนินงานคงเดิม
        var lines = Lines(
            Je(700m, JournalEntryStatus.Posted),
            Je(-700m, JournalEntryStatus.Reversed, closing: true),
            Je(700m, JournalEntryStatus.Posted, closing: true));
        Assert.Equal(700m, RevenueOf(lines.WherePeriodActivity()));
        Assert.Equal(700m, RevenueOf(lines.WhereInLedger()));
    }

    [Fact]
    public void หัวใบและตัวตัดสินในหน่วยความจำ_ใช้ชุดสถานะเดียวกัน()
    {
        var entries = new[]
        {
            Je(1m, JournalEntryStatus.Posted), Je(1m, JournalEntryStatus.Reversed), Je(1m, JournalEntryStatus.Draft),
            Je(1m, JournalEntryStatus.Voided), Je(1m, JournalEntryStatus.Posted, closing: true),
        }.AsQueryable();
        Assert.Equal(3, entries.WhereInLedger().Count());
        Assert.Equal(2, entries.WherePeriodActivity().Count());
        foreach (var s in Enum.GetValues<JournalEntryStatus>())
            Assert.Equal(ReportableJournal.LedgerStatuses.Contains(s), ReportableJournal.InLedger(s));
        Assert.Equal(new[] { JournalEntryStatus.Posted, JournalEntryStatus.Reversed }, ReportableJournal.LedgerStatuses);
    }

    [Fact]
    public void Reproduce_ตัดแค่ใบเดิม_ใบกลับรายการติดลบ_เคลม_VAT_ค่าธรรมเนียมและมัดจำ()
    {
        // GatewaySettlementService.LoadFeeVatAgingAsync (claimed) + DocumentService มัดจำ ×3: เดิม Posted && ReversedByEntryId == null —
        // ReverseJournalEntryAsync คัดลอก Tags/SourceDocumentId ไปใบกลับรายการ ⇒ ใบเดิมถูกตัด ใบกลับยังถูกนับ
        var claim = Je(350m, JournalEntryStatus.Reversed);
        var reversal = Je(-350m, JournalEntryStatus.Posted);
        claim.ReversedByEntryId = reversal.Id;
        reversal.OriginalEntryId = claim.Id;
        var lines = Lines(claim, reversal);
        Assert.Equal(-350m, RevenueOf(lines.Where(l => l.JournalEntry.Status == JournalEntryStatus.Posted && l.JournalEntry.ReversedByEntryId == null)));
        Assert.Equal(0m, RevenueOf(lines.Where(l => ReportableJournal.LedgerStatuses.Contains(l.JournalEntry.Status))));

        // ทิศตรงข้าม: ใบเคลมที่ไม่เคยถูกกลับ ยอดเท่าเดิม
        var kept = Lines(Je(350m, JournalEntryStatus.Posted), Je(12.34m, JournalEntryStatus.Posted));
        Assert.Equal(
            RevenueOf(kept.Where(l => l.JournalEntry.Status == JournalEntryStatus.Posted && l.JournalEntry.ReversedByEntryId == null)),
            RevenueOf(kept.Where(l => ReportableJournal.LedgerStatuses.Contains(l.JournalEntry.Status))));
    }

    [Fact]
    public void ECL_ฐานค่าเผื่อเปลี่ยน_ต้องมีข้อความ_ไม่เปลี่ยนต้องเงียบ()
    {
        // เคยตั้งค่าเผื่อ 1,000 แล้วกลับรายการ: ฐานเดิม (Posted) = −1,000 · ฐานใหม่ = 0 · ใบเดิม (Reversed) Cr 1,000
        var note = EclAllowanceBasis.ChangeNote(currentAllowance: 0m, reversedOriginalsNet: 1_000m);
        Assert.NotNull(note);
        Assert.StartsWith(EclAllowanceBasis.Marker, note);
        Assert.Contains("-1,000.00", note);   // ฐานเดิม
        Assert.Contains("0.00", note);
        // ทิศตรงข้าม: ไม่เคยกลับรายการ / ต่ำกว่า 1 บาท ⇒ null (ไม่รบกวน)
        Assert.Null(EclAllowanceBasis.ChangeNote(5_000m, 0m));
        Assert.Null(EclAllowanceBasis.ChangeNote(5_000m, 0.99m));
    }

    [Fact]
    public void คำแนะนำหลังทบทวน_ร่างสร้างใหม่ได้_ยื่นแล้วให้พิจารณายื่นเพิ่มเติม()
    {
        Assert.Equal(TaxReportRecompute.ActionNone, TaxReportRecompute.SuggestedAction(CitReportFreshness.Level.UpToDate));
        Assert.Equal(TaxReportRecompute.ActionNone, TaxReportRecompute.SuggestedAction(CitReportFreshness.Level.SourceTouched));
        Assert.Equal(TaxReportRecompute.ActionRegenerate, TaxReportRecompute.SuggestedAction(CitReportFreshness.Level.Stale));
        Assert.Equal(TaxReportRecompute.ActionConsiderAmendedFiling, TaxReportRecompute.SuggestedAction(CitReportFreshness.Level.StaleAfterFiling));

        // ฐานเดิมหักซ้ำ 1,000 (ยื่นแล้ว) ⇒ ตัวตัดสินความสดเห็นผลต่าง +1,000 และห้ามสร้างใหม่
        var v = CitReportFreshness.Evaluate(DateTime.UtcNow, storedRevenue: 4_000m, freshRevenue: 5_000m,
            storedExpense: 0m, freshExpense: 0m, changedDocuments: 0, changedJournalEntries: 0, alreadyFiled: true);
        Assert.Equal(1_000m, v.RevenueDelta);
        Assert.False(v.CanRegenerate);
        Assert.Equal(TaxReportRecompute.ActionConsiderAmendedFiling, TaxReportRecompute.SuggestedAction(v.Status));
    }

    [Fact]
    public void ร่องรอยคำนวณใหม่_เก็บยอดที่เป็นผลคำนวณ_และอ้างกติกา()
    {
        var r = new TaxReport { TaxType = TaxType.CorporateIncomeTax, Year = 2025, TotalIncome = 4_000m, NetVat = 1_500m, CitAmount = 0m };
        var json = TaxReportRecompute.AuditPayload(r, "before");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(TaxReportRecompute.AuditAction, root.GetProperty("action").GetString());
        Assert.Equal("before", root.GetProperty("stage").GetString());
        Assert.Equal(4_000m, root.GetProperty("totalIncome").GetDecimal());
        Assert.Equal(1_500m, root.GetProperty("netVat").GetDecimal());
        Assert.Equal(ReportableJournal.RuleCode, root.GetProperty("ruleCode").GetString());
    }

    [Fact]
    public void กราฟแนวโน้ม_ไตรมาสและปีตามรอบบัญชี_ต่อกันพอดี()
    {
        // รอบบัญชีเริ่ม เม.ย. · วันที่ 22 ต.ค. 2026 อยู่ในไตรมาส 3 ของรอบ 2026/27
        var q = PnlComparison.TrailingPeriods(new DateTime(2026, 10, 22), 4, PnlCompareGranularity.Quarter, 4);
        Assert.Equal(4, q.Count);
        Assert.Equal(new DateTime(2026, 1, 1), q[0].Start);
        Assert.Equal(new DateTime(2026, 12, 31), q[^1].EndInclusive);
        Assert.Equal(new DateTime(2026, 10, 1), q[^1].Start);
        Assert.Equal("ไตรมาส 3/2569/70", q[^1].Label);
        Assert.Equal("ไตรมาส 4/2568/69", q[0].Label);
        for (var i = 1; i < q.Count; i++) Assert.Equal(q[i - 1].EndInclusive.AddDays(1), q[i].Start);
        Assert.All(q, p => Assert.False(p.IsPartial));

        var y = PnlComparison.TrailingPeriods(new DateTime(2026, 2, 10), 2, PnlCompareGranularity.Year, 4);
        Assert.Equal(new DateTime(2024, 4, 1), y[0].Start);
        Assert.Equal(new DateTime(2026, 3, 31), y[^1].EndInclusive);

        // ทิศตรงข้าม: บริษัทรอบปฏิทิน ⇒ เหมือนไตรมาสปฏิทินเดิม
        var cal = PnlComparison.TrailingPeriods(new DateTime(2026, 10, 22), 1, PnlCompareGranularity.Quarter, 1);
        Assert.Equal(new DateTime(2026, 10, 1), cal[0].Start);
        Assert.Equal(new DateTime(2026, 12, 31), cal[0].EndInclusive);
        Assert.Equal("ไตรมาส 4/2569", cal[0].Label);
    }
}

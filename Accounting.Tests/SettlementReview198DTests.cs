using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Settlement.Adapters;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// review198-D (ฝ่ายค้านของหน้าจอ + controller settlement) — ทีม D2: ทุกเรื่องมีสองครึ่ง (เคสที่พังกลับมาถูก · เคสที่ถูกอยู่แล้วไม่ถูกแตะ) ·
/// จุดเรียกใน service/controller ล็อกด้วย tools/required_call_site_check.py · JS ของหน้าล็อกด้วย tools/settlement_import_form_sim.js
/// </summary>
public class SettlementReview198DTests
{
    private static readonly Guid Co = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Clearing = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BankGl = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid BankAcc = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Batch1 = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly DateTime Day = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

    // ═════════════ D-01 · ผู้สมัครที่คนเลือกได้ = ด่านเดียวกับการจับคู่อัตโนมัติ ═════════════

    private static SettlementIntentFact Intent(decimal amount, decimal refunded = 0m, bool usable = true, bool received = true,
        Guid? owner = null, decimal refundCounted = 0m, bool unknown = false)
        => new(Guid.NewGuid(), "ORD-1", amount, refunded, usable, received, false, owner, refundCounted, unknown);

    [Fact]
    public void D01_รายการรับชำระ_ยอดขายตรง_เลือกได้_และตรงกับที่ตัวจับคู่อัตโนมัติจับเอง()
    {
        var c = SettlementSaleMatch.IntentCandidate(Intent(1000m), Batch1);
        Assert.Null(SettlementSaleMatch.AssignRefusal(c, SettlementLineType.Sale, 1000m, 1000m));
        // ทิศเดียวกับตัวจับคู่อัตโนมัติ: ผู้สมัครตัวเดียวยอดตรง = Matched (ไม่ใช่คนละเกณฑ์)
        Assert.Equal(SettlementMatchStatus.Matched,
            SettlementSaleMatch.Decide(SettlementLineType.Sale, "ORD-1", 1000m, new[] { c }).Status);
    }

    [Fact]
    public void D01_รายการรับชำระ_ยอดขายไม่ตรง_เลือกไม่ได้พร้อมเหตุผล_เกณฑ์เดียวกับAmountMismatch()
    {
        var c = SettlementSaleMatch.IntentCandidate(Intent(1000m), Batch1);
        var why = SettlementSaleMatch.AssignRefusal(c, SettlementLineType.Sale, 990m, 990m);
        Assert.NotNull(why);
        Assert.Contains("ไม่เท่ายอดที่รับชำระ", why);
        Assert.Equal(SettlementMatchStatus.AmountMismatch,
            SettlementSaleMatch.Decide(SettlementLineType.Sale, "ORD-1", 990m, new[] { c }).Status);
        // ต่างไม่เกินเศษปัด = เลือกได้ (ขอบของเกณฑ์เดียวกัน)
        Assert.Null(SettlementSaleMatch.AssignRefusal(c, SettlementLineType.Sale, 999.99m, 999.99m));
    }

    [Fact]
    public void D01_รายการรับชำระที่อยู่รอบอื่นแล้ว_ผลคืนเงินไม่แน่ชัด_หรือช่องทางใช้ไม่ได้_เลือกไม่ได้ด้วยเหตุผลของตัวตัดสินเดิม()
    {
        var owned = SettlementSaleMatch.IntentCandidate(Intent(1000m, owner: Guid.NewGuid()), Batch1);
        Assert.Contains("อยู่ในรอบโอนอื่นแล้ว", SettlementSaleMatch.AssignRefusal(owned, SettlementLineType.Sale, 1000m, 1000m));
        var unknown = SettlementSaleMatch.IntentCandidate(Intent(1000m, unknown: true), Batch1);
        Assert.Contains("ผลยังไม่แน่ชัด", SettlementSaleMatch.AssignRefusal(unknown, SettlementLineType.Sale, 1000m, 1000m));
        Assert.Contains("ผลยังไม่แน่ชัด", SettlementSaleMatch.AssignRefusal(unknown, SettlementLineType.Refund, 0m, -100m));
        var foreign = SettlementSaleMatch.IntentCandidate(Intent(1000m, usable: false), Batch1);
        Assert.Contains("ไม่ได้ผูกกับ gateway", SettlementSaleMatch.AssignRefusal(foreign, SettlementLineType.Sale, 1000m, 1000m));
    }

    [Fact]
    public void D01_คืนเงินผ่านรายการรับชำระ_เลือกได้เฉพาะยอดคืนไม่เกินที่เหลือ_เกณฑ์เดียวกับApplyIntentRefundCapacity()
    {
        var c = SettlementSaleMatch.IntentCandidate(Intent(1000m, refunded: 300m, refundCounted: 200m), Batch1);   // เหลือ 100
        Assert.Null(SettlementSaleMatch.AssignRefusal(c, SettlementLineType.Refund, 0m, -100m));
        var over = SettlementSaleMatch.AssignRefusal(c, SettlementLineType.Refund, 0m, -150m);
        Assert.Contains("เกินยอดคืนเงินผ่านระบบที่ยังไม่ถูกนับ", over);
        // ตัวจัดสรรอัตโนมัติให้ผลเดียวกัน
        var matched = new SettlementMatchDecision(SettlementMatchStatus.Matched, null, c.Id, null, new[] { c }, null);
        Assert.Equal(SettlementMatchStatus.Matched, SettlementSaleMatch.ApplyIntentRefundCapacity(matched, -100m, 100m).Decision.Status);
        Assert.Equal(SettlementMatchStatus.Unmatched, SettlementSaleMatch.ApplyIntentRefundCapacity(matched, -150m, 100m).Decision.Status);
        // ไม่เคยคืนผ่านระบบ ⇒ เลือกเป็นต้นทางคืนเงินไม่ได้
        var never = SettlementSaleMatch.IntentCandidate(Intent(1000m), Batch1);
        Assert.Contains("ยังไม่เคยคืนเงินผ่านระบบ", SettlementSaleMatch.AssignRefusal(never, SettlementLineType.Refund, 0m, -100m));
    }

    [Fact]
    public void D01_เอกสาร_รับชำระได้เลือกได้แม้ยอดไม่ตรง_รับชำระไม่ได้เลือกไม่ได้_การจองเลือกไม่ได้()
    {
        var open = new SettlementMatchCandidate(SettlementMatchCandidateKind.Document, Guid.NewGuid(), "TaxInvoice TIV-1", 900m, true, true);
        Assert.Null(SettlementSaleMatch.AssignRefusal(open, SettlementLineType.Sale, 1000m, 1000m));   // พฤติกรรมเดิมของการเลือกเอกสาร
        Assert.Null(SettlementSaleMatch.AssignRefusal(open, SettlementLineType.Refund, 0m, -100m));
        var receipt = new SettlementMatchCandidate(SettlementMatchCandidateKind.Document, Guid.NewGuid(), "Receipt RC-1", 0m, false, true);
        Assert.Contains("รับชำระจากผังพักไม่ได้", SettlementSaleMatch.AssignRefusal(receipt, SettlementLineType.Sale, 1000m, 1000m));
        var draft = new SettlementMatchCandidate(SettlementMatchCandidateKind.Document, Guid.NewGuid(), "Invoice DRAFT", 0m, false, false);
        Assert.Contains("ใบเดิมของใบลดหนี้ไม่ได้", SettlementSaleMatch.AssignRefusal(draft, SettlementLineType.Refund, 0m, -100m));
        var room = new SettlementMatchCandidate(SettlementMatchCandidateKind.Reservation, Guid.NewGuid(), "การจอง R-1", null, false, false);
        Assert.NotNull(SettlementSaleMatch.AssignRefusal(room, SettlementLineType.Sale, 1000m, 1000m));
        // บรรทัดที่ไม่ต้องจับคู่ ⇒ ไม่มีผู้สมัครที่เลือกได้เลย
        Assert.NotNull(SettlementSaleMatch.AssignRefusal(open, SettlementLineType.Commission, 0m, -50m));
    }

    [Fact]
    public void D01_ยอดของออเดอร์_นับเฉพาะองค์ประกอบยอดขาย_ตัดช่องว่าง_ไม่นับค่าคอม()
    {
        var g = SettlementSaleMatch.OrderGroupAmounts(new (SettlementLineType, string?, decimal)[]
        {
            (SettlementLineType.Sale, "ORD-1", 1000m),
            (SettlementLineType.SellerVoucher, " ORD-1 ", -50m),
            (SettlementLineType.Commission, "ORD-1", -30m),
            (SettlementLineType.Sale, "ORD-2", 200m),
            (SettlementLineType.Sale, null, 999m),
        });
        Assert.Equal(950m, g["ORD-1"]);
        Assert.Equal(200m, g["ORD-2"]);
        Assert.Equal(2, g.Count);
    }

    // ═════════════ D-02 · พรีวิวแสดงผังที่จะลงจริง ═════════════

    private static readonly Dictionary<string, Guid> Codes = new[] { "11350", "11910", "21917", "53140", "54950", "57140" }
        .ToDictionary(c => c, _ => Guid.NewGuid());

    private static SettlementChartIndex Chart(params SettlementChartAccount[] extra)
        => new(Codes.Select(kv => new SettlementChartAccount(kv.Value, kv.Key, true, "ผัง " + kv.Key))
            .Append(new SettlementChartAccount(Clearing, "11341", true, "ลูกหนี้แพลตฟอร์ม"))
            .Append(new SettlementChartAccount(BankGl, "11120", true, "ธนาคารกสิกร"))
            .Concat(extra));

    private static SettlementChannel Channel(string? feeMap = null)
        => new()
        {
            CompanyId = Co, Kind = SettlementChannelKind.Marketplace, DisplayName = "Shopee", ClearingAccountId = Clearing,
            FeeVatMode = SettlementFeeVatMode.ThaiVat7, FeeWhtMode = SettlementFeeWhtMode.None, FeeAccountMapJson = feeMap,
        };

    private static SettlementLine L(SettlementLineType t, decimal amount, Guid? overrideAccount = null, string? reason = null)
        => new()
        {
            CompanyId = Co, LineType = t, Amount = amount, TxnDate = Day, OverrideAccountId = overrideAccount, AdjustmentReason = reason,
            MatchStatus = SettlementLineTypeRules.For(t).RequiresSaleMatch ? SettlementMatchStatus.AutoSummary : SettlementMatchStatus.NotRequired,
        };

    private static SettlementBatch Batch(decimal net)
        => new() { CompanyId = Co, PayoutRef = "PO-1", PayoutDate = Day, NetPayout = net, BankAccountId = BankAcc };

    [Fact]
    public void D02_ผังในพรีวิว_ผังของบัญชีธนาคาร_ผังที่ช่องทางแมปทับ_ผังที่ผู้ใช้เลือกให้บรรทัดปรับปรุง_ไม่ใช่ผังมาตรฐาน()
    {
        var mapped = Guid.NewGuid();
        var adjust = Guid.NewGuid();
        var plan = SettlementBatchMath.Plan(Batch(930m),
            new[] { L(SettlementLineType.Sale, 1000m), L(SettlementLineType.Commission, -50m),
                    L(SettlementLineType.Adjustment, -20m, adjust, "ค่าปรับส่งช้า") },
            Channel("{\"commission\":\"" + mapped + "\"}"), true);
        var chart = Chart(new SettlementChartAccount(mapped, "53999", true, "ค่าคอม Shopee"),
            new SettlementChartAccount(adjust, "54990", true, "ค่าปรับแพลตฟอร์ม"));
        var r = SettlementAccountResolver.Resolve(plan, chart, BankGl);
        var d = Assert.IsType<SettlementPlanAccounts>(r.Described);

        Assert.Equal(plan.PayoutJournal.Count, d.PayoutJournal.Count);                         // ขนานกับแผนทุกขา
        var bankIdx = plan.PayoutJournal.ToList().FindIndex(l => l.AccountRole == SettlementAccountRoles.Bank);
        Assert.Equal("11120", d.PayoutJournal[bankIdx].Code);
        Assert.Equal("ธนาคารกสิกร", d.PayoutJournal[bankIdx].Name);
        var adjIdx = plan.PayoutJournal.ToList().FindIndex(l => l.AccountRole == SettlementAccountRoles.Adjustment);
        Assert.Equal("54990", d.PayoutJournal[adjIdx].Code);                                   // ผังที่ผู้ใช้เลือก ไม่ใช่ "ไม่มีผัง"
        Assert.Equal("53999", Assert.Single(Assert.Single(d.FeeLines)).Code);                 // FeeAccountMap ไม่ใช่ 53140
        Assert.Equal("11341", d.Clearing!.Code);
        // ผังที่พรีวิวแสดง = ผังที่ JE ลงจริง (ตัวหาผังเดียวกัน)
        Assert.Equal(r.Journal.Select(j => j.AccountId), d.PayoutJournal.Where(x => x.AccountId != null).Select(x => x.AccountId!.Value));
    }

    [Fact]
    public void D02_ผังหาไม่ได้_พรีวิวบอกปัญหาด้วยข้อความเดียวกับแผน_ไม่แสดงผังมาตรฐานแทน()
    {
        var plan = SettlementBatchMath.Plan(Batch(950m),
            new[] { L(SettlementLineType.Sale, 1000m), L(SettlementLineType.Commission, -50m) }, Channel(), true);
        var r = SettlementAccountResolver.Resolve(plan, Chart(), null);                          // บัญชีธนาคารไม่ผูกผัง
        var bank = r.Described!.PayoutJournal[plan.PayoutJournal.ToList().FindIndex(l => l.AccountRole == SettlementAccountRoles.Bank)];
        Assert.Null(bank.Code);
        Assert.Contains("บัญชีธนาคาร", bank.Problem);
        Assert.Contains(r.Errors, e => e == bank.Problem);
        // ทิศตรงข้าม: ผังค่าคอมมาตรฐานมีจริง ⇒ แสดง 53140 ตามจริง
        Assert.Equal("53140", Assert.Single(Assert.Single(r.Described.FeeLines)).Code);
    }

    // ═════════════ D-03 · บัญชีธนาคารไม่ถูกเลือกให้เงียบ ๆ ═════════════

    [Fact]
    public void D03_มีเงินโอนแต่ไม่เลือกบัญชี_นำเข้าไม่ได้พร้อมทางไปต่อ_รอบไม่มีเงินโอนไม่ต้องเลือก()
    {
        Assert.Contains("ตั้งค่า → บัญชีธนาคาร", SettlementBankAccountRule.MissingForImport(948.48m, null));
        Assert.Null(SettlementBankAccountRule.MissingForImport(948.48m, BankAcc));
        Assert.Null(SettlementBankAccountRule.MissingForImport(0m, null));
    }

    [Fact]
    public void D03_ค่าเริ่มต้นเฉพาะเมื่อมีบัญชีเดียว_สองบัญชีขึ้นไปหรือไม่มีเลย_ไม่เลือกให้()
    {
        Assert.Equal(BankAcc, SettlementBankAccountRule.DefaultChoice(new[] { BankAcc }));
        Assert.Null(SettlementBankAccountRule.DefaultChoice(new[] { BankAcc, Guid.NewGuid() }));
        Assert.Null(SettlementBankAccountRule.DefaultChoice(Array.Empty<Guid>()));
    }

    // ═════════════ D-04 · D-06 · ปุ่มตามสถานะ + chargeback ที่ปิดแล้ว + สิทธิ์ ═════════════

    private static readonly SettlementActionPermissions All = new(true, true, true);

    [Fact]
    public void D04_chargebackที่ปิดแล้ว_ไม่มีปุ่มแพ้ชนะ_แสดงผลที่ปิดไว้_ตัวที่ยังเปิดยังมีปุ่ม()
    {
        var closedLine = Guid.NewGuid();
        var openLine = Guid.NewGuid();
        var closed = new[] { new SettlementClosedChargeback(closedLine, Guid.NewGuid(), "JV-0009", "แพ้ chargeback — Shopee รอบ PO-1") };
        var a = SettlementBatchActions.For(SettlementBatchStatus.Posted,
            new[] { (closedLine, SettlementLineType.Chargeback), (openLine, SettlementLineType.Chargeback) }, 0,
            Array.Empty<SettlementUnpostRefusal>(), closed, All);
        Assert.Equal(new[] { openLine }, a.ResolvableChargebackLineIds);
        Assert.Equal(closedLine, Assert.Single(a.ClosedChargebacks!).LineId);
        // ทิศตรงข้าม: ยังไม่มี JE ปิด ⇒ ทั้งสองบรรทัดมีปุ่ม
        var none = SettlementBatchActions.For(SettlementBatchStatus.Posted,
            new[] { (closedLine, SettlementLineType.Chargeback), (openLine, SettlementLineType.Chargeback) }, 0,
            Array.Empty<SettlementUnpostRefusal>(), Array.Empty<SettlementClosedChargeback>(), All);
        Assert.Equal(2, none.ResolvableChargebackLineIds.Count);
        Assert.Empty(none.ClosedChargebacks!);
    }

    [Fact]
    public void D06_ผู้มีสิทธิ์ดูอย่างเดียว_ไม่เห็นปุ่มเขียน_แต่ดูตัวอย่างได้_และมีข้อความบอกสิทธิ์ที่ขาด()
    {
        var viewOnly = new SettlementActionPermissions(false, false, false);
        var a = SettlementBatchActions.For(SettlementBatchStatus.Matched, Array.Empty<(Guid, SettlementLineType)>(), 0, null, null, viewOnly);
        Assert.False(a.CanEditLines || a.CanVoid || a.CanPost || a.CanUnpost || a.CanBankMatch);
        Assert.True(a.CanPreview);
        Assert.Contains(PermissionKeys.LabelOf(SettlementPermissionScope.Import), a.PermissionNote);
        Assert.Contains(PermissionKeys.LabelOf(SettlementPermissionScope.Post), a.PermissionNote);
        // ทิศตรงข้าม: มีสิทธิ์ครบ ⇒ ปุ่มครบตามสถานะ ไม่มีข้อความ
        var full = SettlementBatchActions.For(SettlementBatchStatus.Matched, Array.Empty<(Guid, SettlementLineType)>(), 0, null, null, All);
        Assert.True(full.CanEditLines && full.CanVoid && full.CanPost);
        Assert.Null(full.PermissionNote);
        // ไม่ได้ตรวจสิทธิ์ (null) ⇒ ตัดสินจากสถานะอย่างเดียว (พฤติกรรมเดิม)
        Assert.True(SettlementBatchActions.For(SettlementBatchStatus.Matched, Array.Empty<(Guid, SettlementLineType)>()).CanEditLines);
    }

    [Fact]
    public void D06_ลงบัญชีได้แต่ไม่มีสิทธิ์สมุดรายวัน_ไม่เห็นปุ่มchargeback_บอกเหตุผล_ปุ่มลงบัญชีอื่นยังอยู่()
    {
        var cb = Guid.NewGuid();
        var noJournal = new SettlementActionPermissions(true, true, false);
        var a = SettlementBatchActions.For(SettlementBatchStatus.Posted, new[] { (cb, SettlementLineType.Chargeback) }, 0,
            Array.Empty<SettlementUnpostRefusal>(), Array.Empty<SettlementClosedChargeback>(), noJournal);
        Assert.Empty(a.ResolvableChargebackLineIds);
        Assert.Contains(PermissionKeys.LabelOf(PermissionKeys.JournalManage), a.PermissionNote);
        Assert.True(a.CanUnpost && a.CanBankMatch);
    }

    // ═════════════ D-05 · ตัวกรองสถานะที่ไม่มีทางมีข้อมูล ═════════════

    [Fact]
    public void D05_ตัวกรองรายการไม่มียกเลิกแล้ว_สถานะอื่นครบ_ป้ายสถานะยังครบทุกค่า()
    {
        var r = SettlementReferenceCatalog.Build();
        Assert.DoesNotContain(r.BatchFilterStatuses, o => o.Value == nameof(SettlementBatchStatus.Voided));
        Assert.Equal(Enum.GetValues<SettlementBatchStatus>().Length - 1, r.BatchFilterStatuses.Count);
        Assert.False(SettlementReferenceCatalog.IsListable(SettlementBatchStatus.Voided));
        Assert.True(SettlementReferenceCatalog.IsListable(SettlementBatchStatus.Posted));
        Assert.Contains(r.BatchStatuses, o => o.Value == nameof(SettlementBatchStatus.Voided));
    }

    // ═════════════ D-P2 · จำการจับคู่คอลัมน์ = ค่าตั้งของช่องทาง ═════════════

    [Fact]
    public void DP2_จำการจับคู่คอลัมน์_ต้องมีสิทธิ์ช่องทางและไม่ใช่คีย์API_ไม่ผ่านไม่จำแต่บอก()
    {
        Assert.Equal((true, (string?)null), SettlementPermissionScope.ColumnMapMemory(true, false, true));
        var noPerm = SettlementPermissionScope.ColumnMapMemory(true, false, false);
        Assert.False(noPerm.Remember);
        Assert.Contains(PermissionKeys.LabelOf(SettlementPermissionScope.Channels), noPerm.Notice);
        var apiKey = SettlementPermissionScope.ColumnMapMemory(true, true, true);
        Assert.False(apiKey.Remember);
        Assert.Contains("คีย์ API", apiKey.Notice);
        // ไม่ได้ขอให้จำ ⇒ ไม่มีอะไรต้องบอก
        Assert.Equal((false, (string?)null), SettlementPermissionScope.ColumnMapMemory(false, true, false));
    }

    // ═════════════ D-10 · สมการลงตัว: เซิร์ฟเวอร์ตัดสิน ═════════════

    [Fact]
    public void D10_ธงลงตัวของแผน_ตรงกับปัญหาUnbalancedเสมอ()
    {
        var even = SettlementBatchMath.Plan(Batch(950m),
            new[] { L(SettlementLineType.Sale, 1000m), L(SettlementLineType.Commission, -50m) }, Channel(), true);
        Assert.True(even.Balanced);
        Assert.DoesNotContain(even.Issues, i => i.Code == SettlementPlanIssueCode.Unbalanced);
        var off = SettlementBatchMath.Plan(Batch(940m),
            new[] { L(SettlementLineType.Sale, 1000m), L(SettlementLineType.Commission, -50m) }, Channel(), true);
        Assert.False(off.Balanced);
        Assert.Contains(off.Issues, i => i.Code == SettlementPlanIssueCode.Unbalanced);
        Assert.True(SettlementBatchMath.IsBalanced(0.01m));
        Assert.False(SettlementBatchMath.IsBalanced(-0.02m));
    }

    // ═════════════ D-P4 · เพดานแถว/คอลัมน์ของไฟล์ ═════════════

    private static IEnumerable<IReadOnlyList<string>> Rows(int n, int cols = 3)
    {
        for (var i = 0; i < n; i++) yield return Enumerable.Repeat("x", cols).ToList();
    }

    [Fact]
    public void DP4_แถวเกินเพดาน_ล้มดังภาษาไทย_แถวเท่าเพดานผ่าน()
    {
        Assert.Equal(5, SettlementFileReader.Capped(Rows(5), 5, 10).Count());
        var ex = Assert.Throws<SettlementFormatException>(() => SettlementFileReader.Capped(Rows(6), 5, 10).ToList());
        Assert.Equal("too-many-rows", ex.Code);
        Assert.Contains("แถว", ex.Message);
    }

    [Fact]
    public void DP4_คอลัมน์เกินเพดาน_ล้มดัง_หยุดอ่านทันทีไม่อ่านต่อ()
    {
        var read = 0;
        IEnumerable<IReadOnlyList<string>> Counting()
        {
            foreach (var r in Rows(1000, 3)) { read++; yield return read == 2 ? Enumerable.Repeat("x", 20).ToList() : r; }
        }
        var ex = Assert.Throws<SettlementFormatException>(() => SettlementFileReader.Capped(Counting(), 1000, 10).ToList());
        Assert.Equal("too-many-columns", ex.Code);
        Assert.Equal(2, read);
        // ไฟล์จริงจากทางเข้าหลักผ่านเพดานเดียวกัน
        var csv = new SettlementFileInput("a.csv", System.Text.Encoding.UTF8.GetBytes("Type,Amount\nSale,100\n"));
        Assert.Equal(2, SettlementFileReader.ReadRows(csv).Count());
    }
}

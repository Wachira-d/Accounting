using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม V2 — ของกำพร้าในรอบโอน settlement (DECISIONS ข้อ 10 · review198-S4 S4-1 "ความเสี่ยงที่เหลือ" · review198-S3 S3-11)
/// ทุกข้อมีสองทิศ (F2 ข้อ 8): เคสที่พังกลับมาถูก + เคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ · จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py
/// (เรพไม่มีเทสต์ที่มี DbContext จริง — ด่านสิทธิ์ของ endpoint ล็อกด้วย checker + เทสต์สัญญาของ attribute ด้านล่าง)
/// </summary>
public class SettlementRound200V2Tests
{
    private static readonly Guid Co = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Clearing = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DeadBatch = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid DocA = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Fee = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid PayA = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Cn = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateTime Day = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly HashSet<(TaxType, int, int)> NothingFiled = new();

    private static string ChildBlock() => DocumentVoidPreconditions.Reason(new DocumentVoidChildFact(Fee, DocumentType.CreditNote, "CN-0009", true, Cn));

    /// <summary>ใบค่าธรรมเนียมกำพร้าของรอบโอน PO-OLD (ยกเลิกแล้ว) ที่มีใบลดหนี้ CN-0009 อ้างเลขที่อยู่</summary>
    private static SettlementUnpostDocument FeeDoc(string? voidBlock = null, bool etaxAccepted = false)
        => new(Fee, "PV-0009", DocumentType.PaymentVoucher, SettlementPostingKeys.FeeComponent(SettlementFeeVatTreatment.InputVatPending),
            Day, 7m, false, etaxAccepted, false, false, null, voidBlock);

    private static SettlementOrphanChild Child(bool etax = false, bool locked = false, bool sent = false, string? wht = null)
        => new(Fee, Cn, DocumentType.CreditNote, "CN-0009", etax, locked, sent, wht);

    private static SettlementOrphanArtifact Orphan(Guid id, bool isPayment, string number, SettlementOrphanAck? ack = null)
        => new(id, DeadBatch, "PO-OLD", isPayment, number, ack);

    private static readonly SettlementOrphanAck Ack = new(Guid.Parse("88888888-8888-8888-8888-888888888888"), "สมหญิง ผู้ทำบัญชี", Day,
        "ตรวจแล้ว รอบ PO-NEW ไม่มีค่าธรรมเนียมซ้ำกับรอบที่ยกเลิก");

    private static SettlementOrphanTriageResult TriageFee(SettlementUnpostDocument doc, IReadOnlyList<SettlementOrphanChild>? children,
        SettlementOrphanAck? ack = null)
    {
        var refusals = SettlementUnpostGate.Evaluate(new[] { doc }, Array.Empty<SettlementUnpostCertificate>(), NothingFiled);
        return SettlementOrphanTriage.Split(new[] { Orphan(doc.Id, false, doc.Number, ack) }, refusals, new[] { doc }, children);
    }

    /// <summary>รอบโอนใหม่ของช่องทางเดียวกัน (ฉากเดียวกับ SettlementReview198S5Tests) — ลงบัญชีได้เมื่อไม่มีของกำพร้า</summary>
    private static (SettlementPostingPlan Plan, SettlementPostingFacts Facts) NewBatch()
    {
        var batch = new SettlementBatch { CompanyId = Co, PayoutRef = "PO-NEW", PayoutDate = Day, NetPayout = 1963m, BankAccountId = Guid.NewGuid() };
        var channel = new SettlementChannel
        {
            CompanyId = Co, Kind = SettlementChannelKind.Marketplace, DisplayName = "Shopee", ClearingAccountId = Clearing,
            CounterpartyContactId = Guid.NewGuid(),
        };
        var lines = new[]
        {
            new SettlementLine { CompanyId = Co, Seq = 1, LineType = SettlementLineType.Sale, Amount = 1000m, MatchedDocumentId = DocA, TxnDate = Day,
                ExternalOrderId = "SP-0", MatchStatus = SettlementMatchStatus.Matched },
            new SettlementLine { CompanyId = Co, Seq = 2, LineType = SettlementLineType.Sale, Amount = 1070m, TxnDate = Day, ExternalOrderId = "SP-1",
                MatchStatus = SettlementMatchStatus.AutoSummary },
            new SettlementLine { CompanyId = Co, Seq = 3, LineType = SettlementLineType.Commission, Amount = -107m, TxnDate = Day,
                MatchStatus = SettlementMatchStatus.NotRequired },
        };
        var plan = SettlementBatchMath.Plan(batch, lines, channel, true);
        var facts = new SettlementPostingFacts(
            SettlementBatchStatus.Matched, Day, Day, null, new Dictionary<DateTime, string>(),
            new HashSet<(int, int)>(), new HashSet<(int, int)>(), Array.Empty<string>(),
            true, true, Clearing,
            plan.Receipts.Select(r => new SettlementReceiptTarget(r.DocumentId, true, "INV-A", DocumentType.Invoice, DocumentStatus.Approved, 1000m, false))
                .ToList(),
            Array.Empty<SettlementClearingSource>(), Array.Empty<SettlementDuplicateSale>(), true, true, 0);
        return (plan, facts);
    }

    private static SettlementPostingPlan Gate(SettlementOrphanTriageResult t)
    {
        var (plan, facts) = NewBatch();
        return SettlementPostingGate.Evaluate(plan, facts with
        {
            OrphanArtifacts = t.Voidable, UnvoidableOrphans = t.Unvoidable, OrphanNeedsAction = t.NeedsUserAction, AcknowledgedOrphans = t.Acknowledged,
        });
    }

    // ═════════════ (ก) ใบที่อ้างซึ่งตัวเองยกเลิกไม่ได้ ⇒ กองยกเลิกไม่ได้จริง (ไม่ใช่ "ต้องทำขั้นก่อน") ═════════════

    [Fact]
    public void V2_ใบกำพร้ามีใบลดหนี้อ้างที่eTaxตอบรับแล้ว_กองยกเลิกไม่ได้จริง_ไม่ใช่ต้องทำขั้นก่อน()
    {
        // เดิม (S5): ไม่ดูสถานะใบลูก ⇒ ตกกอง NeedsUserAction ทางไปต่อ "ยกเลิกใบที่อ้างก่อน" ซึ่งทำไม่ได้ ⇒ ช่องทางบล็อกถาวร
        var t = TriageFee(FeeDoc(ChildBlock()), new[] { Child(etax: true) });
        Assert.Empty(t.NeedsUserAction);
        Assert.Empty(t.Voidable);
        var hard = Assert.Single(t.Unvoidable);
        Assert.Contains("PV-0009", hard);
        Assert.Contains("ใบลดหนี้ CN-0009", hard);
        Assert.Contains("Accepted", hard);
        var item = Assert.Single(t.Items!);
        Assert.Equal(SettlementOrphanPile.Unvoidable, item.Pile);
        Assert.True(item.CanAcknowledge);
        Assert.Equal("ยกเลิกไม่ได้จริง", item.PileLabel);
    }

    [Theory]
    [InlineData(false, true, false, null, "ล็อกการยื่น")]
    [InlineData(false, false, true, null, "ส่งให้ลูกค้าแล้ว")]
    [InlineData(false, false, false, "50 ทวิ WHT-1 อยู่ใน ภ.ง.ด.53 ที่ยื่นแล้ว", "WHT-1")]
    public void V2_ใบที่อ้างอยู่ในรายงานล็อก_ส่งลูกค้าแล้ว_50ทวิยื่นแล้ว_ก็ยกเลิกไม่ได้จริง(bool etax, bool locked, bool sent, string? wht, string expect)
    {
        var t = TriageFee(FeeDoc(ChildBlock()), new[] { Child(etax, locked, sent, wht) });
        Assert.Empty(t.NeedsUserAction);
        Assert.Contains(expect, Assert.Single(t.Unvoidable));
    }

    [Fact]
    public void V2_ทิศตรงข้าม_ใบที่อ้างยังยกเลิกได้_คงกองต้องทำขั้นก่อน_ทางไปต่อยกเลิกใบที่อ้างก่อน()
    {
        var t = TriageFee(FeeDoc(ChildBlock()), new[] { Child() });
        Assert.Empty(t.Unvoidable);
        var block = Assert.Single(t.NeedsUserAction);
        Assert.Contains("ยกเลิกเอกสารที่อ้างใบนี้ก่อน", block.NextStep);
        Assert.Equal(Fee, block.ArtifactId);
        Assert.False(Assert.Single(t.Items!).CanAcknowledge);
        Assert.Null(SettlementOrphanTriage.ChildUnvoidableReason(Child()));
        // ไม่ส่งข้อเท็จจริงของใบลูก (ผู้เรียกเก่า) ⇒ พฤติกรรมเดิมของ S5 ทุกตัวอักษร
        Assert.Equal(t.NeedsUserAction[0].Why, Assert.Single(TriageFee(FeeDoc(ChildBlock()), null).NeedsUserAction).Why);
    }

    [Fact]
    public void V2_ใบกำพร้าไม่มีเหตุใดเลย_ใบลูกยกเลิกไม่ได้ของใบอื่นไม่ลาม_ยังกองยกเลิกได้ทันที()
    {
        var other = new SettlementOrphanChild(Guid.NewGuid(), Cn, DocumentType.CreditNote, "CN-X", true, false, false);
        var t = TriageFee(FeeDoc(), new[] { other });
        Assert.Single(t.Voidable);
        Assert.Empty(t.Unvoidable);
        Assert.Empty(t.NeedsUserAction);
    }

    // ═════════════ (ข) รับรู้ของกำพร้า: ยังไม่รับรู้ = บล็อก · รับรู้แล้ว = ไม่บล็อก + แสดงผู้/เวลา/เหตุผล ═════════════

    [Fact]
    public void V2_ยังไม่รับรู้_บล็อกพร้อมทางไปต่อรับรู้_รับรู้แล้ว_ลงบัญชีได้และแสดงผู้รับรู้()
    {
        var blocked = Gate(TriageFee(FeeDoc(ChildBlock()), new[] { Child(etax: true) }));
        Assert.False(blocked.CanPost);
        var bi = Assert.Single(blocked.Issues, i => i.Code == SettlementPlanIssueCode.OrphanPostingArtifacts);
        Assert.True(bi.Blocking);
        Assert.Contains("รับรู้ของกำพร้า", bi.NextStep);

        var t = TriageFee(FeeDoc(ChildBlock()), new[] { Child(etax: true) }, Ack);
        Assert.Empty(t.Unvoidable);
        var shown = Assert.Single(t.Acknowledged!);
        Assert.Contains("สมหญิง ผู้ทำบัญชี", shown);
        Assert.Contains("ไม่มีค่าธรรมเนียมซ้ำ", shown);
        var item = Assert.Single(t.Items!);
        Assert.Equal(Ack, item.Ack);
        Assert.False(item.CanAcknowledge);
        var open = Gate(t);
        Assert.True(open.CanPost);
        Assert.False(Assert.Single(open.Issues, i => i.Code == SettlementPlanIssueCode.OrphanPostingArtifacts).Blocking);
    }

    [Fact]
    public void V2_การรับรู้ค้างบนชิ้นที่ตอนนี้ยกเลิกได้แล้ว_ไม่มีผล_ยังบล็อกและบอกว่าการรับรู้เดิมไม่มีผล()
    {
        // ใบลูกเคย e-Tax ตอบรับ (รับรู้ไว้) แต่ตอนนี้ใบลูกยกเลิกได้ ⇒ กองต้องทำขั้นก่อน · การรับรู้ห้ามพาผ่าน
        var needs = TriageFee(FeeDoc(ChildBlock()), new[] { Child() }, Ack);
        Assert.Empty(needs.Acknowledged!);
        Assert.Contains("การรับรู้เดิมไม่มีผล", Assert.Single(needs.NeedsUserAction).Why);
        Assert.False(Gate(needs).CanPost);
        // ยกเลิกได้ทันที + เคยรับรู้ ⇒ ยังบล็อก
        var free = TriageFee(FeeDoc(), null, Ack);
        Assert.Contains("การรับรู้เดิมไม่มีผล", Assert.Single(free.Voidable));
        Assert.False(Gate(free).CanPost);
    }

    [Fact]
    public void V2_การรับชำระกำพร้าที่ใบเสร็จeTaxตอบรับแล้ว_รับรู้ได้_รับรู้แล้วไม่บล็อก()
    {
        var pay = new SettlementUnpostPayment(PayA, "RV-0001", DocA, "INV-0001", null, 1070m, 1070m, ReceiptEtaxAccepted: true, ReceiptNumber: "RE-0001");
        var refusals = SettlementUnpostGate.Evaluate(Array.Empty<SettlementUnpostDocument>(), Array.Empty<SettlementUnpostCertificate>(), NothingFiled,
            new[] { pay });
        var unacked = SettlementOrphanTriage.Split(new[] { Orphan(PayA, true, "RV-0001") }, refusals, Array.Empty<SettlementUnpostDocument>());
        Assert.Null(SettlementOrphanTriage.AckRefusal(unacked, PayA, true));
        Assert.NotNull(SettlementOrphanTriage.AckRefusal(unacked, PayA, false));      // id เดียวกันแต่คนละชนิด ⇒ ไม่ใช่ชิ้นนี้
        var acked = SettlementOrphanTriage.Split(new[] { Orphan(PayA, true, "RV-0001", Ack) }, refusals, Array.Empty<SettlementUnpostDocument>());
        Assert.True(Gate(acked).CanPost);
        Assert.False(Gate(unacked).CanPost);
    }

    // ═════════════ ด่านของปุ่มรับรู้ (pure) — รับรู้ได้เฉพาะกองยกเลิกไม่ได้จริง · เหตุผลบังคับ ═════════════

    [Fact]
    public void V2_ปุ่มรับรู้_กองยกเลิกไม่ได้รับรู้ได้_กองต้องทำขั้นก่อนและยกเลิกได้ปฏิเสธพร้อมทางไปต่อ_ไม่ใช่ของกำพร้าปฏิเสธ()
    {
        Assert.Null(SettlementOrphanTriage.AckRefusal(TriageFee(FeeDoc(ChildBlock()), new[] { Child(etax: true) }), Fee, false));
        Assert.Null(SettlementOrphanTriage.AckRefusal(TriageFee(FeeDoc(etaxAccepted: true), null), Fee, false));
        Assert.Null(SettlementOrphanTriage.AckRefusal(TriageFee(FeeDoc(etaxAccepted: true), null, Ack), Fee, false));   // รับรู้แล้ว ⇒ ตอบซ้ำได้

        var needs = SettlementOrphanTriage.AckRefusal(TriageFee(FeeDoc(ChildBlock()), new[] { Child() }), Fee, false);
        Assert.NotNull(needs);
        Assert.Contains("ยกเลิกเอกสารที่อ้างใบนี้ก่อน", needs);
        var free = SettlementOrphanTriage.AckRefusal(TriageFee(FeeDoc(), null), Fee, false);
        Assert.NotNull(free);
        Assert.Contains("ยกเลิก", free);
        Assert.Contains("ไม่ใช่ของกำพร้า", SettlementOrphanTriage.AckRefusal(TriageFee(FeeDoc(), null), Guid.NewGuid(), false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void V2_เหตุผลรับรู้ว่าง_ปฏิเสธ(string? reason)
        => Assert.Contains("ระบุเหตุผล", SettlementOrphanTriage.AckReasonProblem(reason));

    [Fact]
    public void V2_เหตุผลรับรู้ยาวเกิน_ปฏิเสธ_เหตุผลปกติผ่าน()
    {
        Assert.NotNull(SettlementOrphanTriage.AckReasonProblem(new string('ก', SettlementOrphanTriage.AckReasonMaxLength + 1)));
        Assert.Null(SettlementOrphanTriage.AckReasonProblem("ตรวจแล้วไม่ซ้ำ"));
        Assert.Null(SettlementOrphanTriage.AckReasonProblem(new string('ก', SettlementOrphanTriage.AckReasonMaxLength)));
    }

    // ═════════════ ผู้ไม่มีสิทธิ์ถูกปฏิเสธ — endpoint มีด่านสิทธิ์ Settlement.Post + ห้ามคีย์ API (service ตรวจซ้ำ · ล็อกด้วย required_call_site_check) ═════════════

    [Fact]
    public void V2_endpointรับรู้ของกำพร้า_ต้องมีสิทธิ์ลงบัญชีรอบโอนและห้ามคีย์API()
    {
        var m = typeof(Accounting.Controllers.SettlementController).GetMethod("AcknowledgeOrphan")!;
        // คีย์สิทธิ์อ่านจากอาร์กิวเมนต์ของ attribute (ตัวกรองเก็บไว้ใน field ส่วนตัว) — ต้องเป็น Settlement.Post ตัวเดียว ไม่ใช่ View
        var perm = Assert.Single(m.GetCustomAttributesData(), a => a.AttributeType == typeof(Accounting.Filters.RequirePermissionAttribute));
        Assert.Equal(SettlementPermissionScope.Post, perm.ConstructorArguments[0].Value as string);
        Assert.NotEqual(SettlementPermissionScope.View, perm.ConstructorArguments[0].Value as string);
        Assert.Single(m.GetCustomAttributes(typeof(Accounting.Filters.RejectApiKeyAttribute), false));
    }

    // ═════════════ S3-11: SoD นับผู้เติมไฟล์เข้ารอบเดิม ═════════════

    [Fact]
    public void S311_SoD_ผู้เติมไฟล์เข้ารอบเดิมกดลงบัญชีเอง_บล็อก_คนอื่นหรือปิดแยกหน้าที่_ไม่บล็อก()
    {
        var importer = Guid.NewGuid();
        var appender = Guid.NewGuid();
        var poster = Guid.NewGuid();
        var lineCreators = new string?[] { importer.ToString(), appender.ToString(), null, "" };
        Assert.True(SettlementPostingGate.SodSelfApproval(true, importer.ToString(), lineCreators, appender));   // เดิมหลุด (เทียบแค่ผู้สร้างรอบ)
        Assert.True(SettlementPostingGate.SodSelfApproval(true, importer.ToString(), lineCreators, importer));
        Assert.False(SettlementPostingGate.SodSelfApproval(true, importer.ToString(), lineCreators, poster));    // บรรทัดว่างไม่นับเป็นใคร
        Assert.False(SettlementPostingGate.SodSelfApproval(false, importer.ToString(), lineCreators, appender));
        Assert.True(SettlementPostingGate.SodSelfApproval(true, null, Array.Empty<string?>(), poster));           // ผู้สร้างรอบไม่รู้ ⇒ บล็อก (เดิม)
    }

    // ═════════════ schema: คอลัมน์ธงรับรู้อยู่ในบล็อก migration ของ settlement และตรงกับ model ของ EF ═════════════

    [Fact]
    public void V2_Migration_คอลัมน์รับรู้ของกำพร้า_ทั้งเอกสารและการรับชำระ_ตรงกับmodelของEF()
    {
        var stmts = DatabaseMigrationHelper.SettlementSchemaStatements();
        var all = DatabaseMigrationHelper.GetAlterStatements();
        using var db = new AccountingDbContext(new DbContextOptionsBuilder<AccountingDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=none;Password=none").Options);
        foreach (var t in new[] { typeof(Document), typeof(Payment) })
        {
            var et = db.Model.FindEntityType(t)!;
            foreach (var name in new[] { "SettlementOrphanAckAt", "SettlementOrphanAckBy", "SettlementOrphanAckReason" })
            {
                var col = et.FindProperty(name)!.GetColumnName();
                var stmt = Assert.Single(stmts, s => s.Contains($"ALTER TABLE \"{et.GetTableName()}\" ADD COLUMN IF NOT EXISTS \"{col}\"",
                    StringComparison.Ordinal));
                Assert.Contains(stmt, all);
                Assert.Contains("NULL", stmt);
                Assert.DoesNotContain("NOT NULL", stmt);                                       // แถวเดิม = ยังไม่รับรู้
            }
        }
    }
}

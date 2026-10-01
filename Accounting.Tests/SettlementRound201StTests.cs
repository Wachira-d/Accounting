using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Models.DTOs.Tax;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม ST (Settlement) — BACKLOG §1.2 A-ST1..A-ST9 + คำตัดสินข้อ 82 (C-9) · ทุกเรื่องสองทิศ (F2 ข้อ 8): เคสที่พังกลับมาถูก + เคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ ·
/// จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py (บล็อก "รอบ 201 ทีม ST")
/// </summary>
public class SettlementRound201StTests
{
    private static readonly Guid BatchA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid BatchB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid DocX = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid DocY = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    private static AccountingDbContext OfflineDb() => new(new DbContextOptionsBuilder<AccountingDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=none;Password=none;Timeout=1").Options);

    // ═════════════ A-ST1: เจ้าของการรับชำระ = คอลัมน์ ไม่ใช่ป้ายใน Notes ═════════════

    [Fact]
    public void ST1_ประทับเฉพาะการรับชำระของใบนั้นที่ยังไม่มีเจ้าของ_ป้ายในNotesไม่มีผล()
    {
        var mine = new Payment { DocumentId = DocX, Notes = "โอนเงินปกติ" };
        var otherDoc = new Payment { DocumentId = DocY, Notes = SettlementPostingKeys.PaymentMarker(BatchA) + " ปลอมป้าย" };
        var owned = new Payment { DocumentId = DocX, SettlementBatchId = BatchB };
        var n = SettlementPaymentOwner.StampAdded(new[] { mine, otherDoc, owned }, BatchA, DocX);
        Assert.Equal(1, n);
        Assert.Equal(BatchA, mine.SettlementBatchId);
        Assert.Null(otherDoc.SettlementBatchId);         // ป้ายใน Notes ไม่ทำให้เป็นของรอบโอน
        Assert.Equal(BatchB, owned.SettlementBatchId);   // ห้ามย้ายเจ้าของ
    }

    [Fact]
    public void ST1_ตัวฟังSaveChanges_ประทับในSaveChangesเดียวกับINSERT_และถอดเมื่อออกจากขอบเขต()
    {
        using var db = OfflineDb();
        var inScope = new Payment { CompanyId = Guid.NewGuid(), DocumentId = DocX, PaymentNumber = "PAY-1" };
        db.Payments.Add(inScope);
        using (SettlementPaymentOwner.StampOnSave(db, BatchA, DocX))
            Assert.ThrowsAny<Exception>(() => db.SaveChanges());   // ไม่มีฐานข้อมูล — ตัวฟังทำงานก่อนเปิดการเชื่อมต่อ
        Assert.Equal(BatchA, inScope.SettlementBatchId);

        // ทิศตรงข้าม: นอกขอบเขต (เส้นรับชำระทั่วไป) ไม่มีใครประทับ แม้ใบเดียวกัน
        using var db2 = OfflineDb();
        var outside = new Payment { CompanyId = Guid.NewGuid(), DocumentId = DocX, PaymentNumber = "PAY-2",
            Notes = SettlementPostingKeys.PaymentMarker(BatchA) + SettlementPostingKeys.PaymentNoteLead + "ปลอม" };
        db2.Payments.Add(outside);
        using (SettlementPaymentOwner.StampOnSave(db2, BatchA, DocX)) { }
        Assert.ThrowsAny<Exception>(() => db2.SaveChanges());
        Assert.Null(outside.SettlementBatchId);
    }

    [Fact]
    public void ST1_เส้นรับชำระทั่วไปไม่มีช่องตั้งเจ้าของ()
    {
        // CreatePaymentRequest ถูก bind จาก body ของ controller/API — มีช่องนี้ = ผู้ใช้ตั้งเจ้าของเองได้ (ช่องโหว่เดิมอีกรูป)
        Assert.Null(typeof(CreatePaymentRequest).GetProperty(nameof(Payment.SettlementBatchId)));
    }

    [Fact]
    public void ST1_ข้อความที่ระบบเขียน_ตรงรูปที่backfillพิสูจน์()
    {
        var plan = new SettlementReceiptPlan(DocX, 100m, new List<Guid> { Guid.NewGuid() });
        var req = SettlementDocumentBuilder.ReceiptPayment(plan, BatchA, Guid.NewGuid(), new DateTime(2026, 9, 30), "PO-77", "Shopee",
            SettlementReceiptWhtKind.None);
        Assert.StartsWith(SettlementPostingKeys.PaymentMarker(BatchA) + SettlementPostingKeys.PaymentNoteLead, req.Notes);
        Assert.Equal("PO-77", req.Reference);
        Assert.Equal(PaymentMethod.EWallet, req.PaymentMethod);
        Assert.NotNull(req.OverridePaymentAccountId);
    }

    [Fact]
    public void ST1_backfill_ต้องพิสูจน์ครบทุกข้อ_และรันครั้งเดียวตอนสร้างคอลัมน์()
    {
        var sql = SettlementPostingKeys.PaymentOwnerBackfillSql();
        Assert.Contains("\"SettlementBatchId\" IS NULL", sql);
        Assert.Contains("b.\"CompanyId\" = p.\"CompanyId\"", sql);                                       // tenant เดียวกัน
        Assert.Contains("LIKE ('[SETTLEMENT:' || replace(b.\"Id\"::text, '-', '') || '] รับเงินผ่าน %')", sql); // ป้ายต้น Notes + ข้อความระบบ
        Assert.Contains("p.\"Reference\" = b.\"PayoutRef\"", sql);
        Assert.Contains("p.\"PaymentMethod\" = 7", sql);
        Assert.Contains("p.\"OverridePaymentAccountId\" IS NOT NULL", sql);
        Assert.DoesNotContain("IsDeleted", sql);                                                          // รอบที่ยกเลิก/ลบแล้วนับด้วย (ตัวหาของกำพร้า)

        var mig = DatabaseMigrationHelper.PaymentSettlementOwnerMigrationSql();
        var exists = mig.IndexOf("IF EXISTS", StringComparison.Ordinal);
        var ret = mig.IndexOf("RETURN;", StringComparison.Ordinal);
        var add = mig.IndexOf("ADD COLUMN \"SettlementBatchId\"", StringComparison.Ordinal);
        var upd = mig.IndexOf("UPDATE \"Payments\"", StringComparison.Ordinal);
        Assert.True(exists >= 0 && exists < ret && ret < add && add < upd, "ต้องตรวจว่าคอลัมน์มีแล้วก่อน ⇒ backfill เกิดเฉพาะบูตที่สร้างคอลัมน์");
        Assert.Contains("pg_advisory_xact_lock(" + DatabaseMigrationHelper.PaymentSettlementOwnerLockKey + ")", mig);
        Assert.DoesNotContain("__", mig);
        Assert.Contains(mig, DatabaseMigrationHelper.GetAlterStatements());
        Assert.Contains(DatabaseMigrationHelper.SettlementSchemaStatements(), s => s.Contains("\"IX_Payments_Company_SettlementBatch\"", StringComparison.Ordinal));
        // ทิศตรงข้าม: ADD COLUMN IF NOT EXISTS ห้ามมีอีกชุด (จะสร้างคอลัมน์ก่อน DO block ⇒ backfill ไม่เคยรัน)
        Assert.DoesNotContain(DatabaseMigrationHelper.GetAlterStatements(),
            s => s.Contains("ADD COLUMN IF NOT EXISTS \"SettlementBatchId\"", StringComparison.Ordinal) && s.Contains("\"Payments\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ST1_คอลัมน์ตรงกับmodelของEF()
    {
        using var db = OfflineDb();
        var et = db.Model.FindEntityType(typeof(Payment))!;
        Assert.Equal("SettlementBatchId", et.FindProperty(nameof(Payment.SettlementBatchId))!.GetColumnName());
        Assert.True(et.FindProperty(nameof(Payment.SettlementBatchId))!.IsNullable);
    }

    // ═════════════ A-ST3: ยอดไม่ลงตัวของรอบที่ประกอบจาก intent โดยไม่กรอก "ถึงวันที่" — บอกเหตุที่น่าจะเป็น ═════════════

    private static readonly DateTime Day = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Co = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Clearing = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void ST3_ประกอบจากintentไม่กรอกถึงวันที่_ยอดไม่ลงตัว_ข้อความชี้ให้กรอกถึงวันที่()
    {
        var batch = new SettlementBatch { CompanyId = Co, PayoutRef = "PO-T0", PayoutDate = Day, NetPayout = 900m, BankAccountId = Guid.NewGuid(),
            SourceKind = SettlementSourceKind.PaymentIntents, PeriodTo = null };
        var channel = new SettlementChannel { CompanyId = Co, Kind = SettlementChannelKind.Gateway, DisplayName = "Omise", ClearingAccountId = Clearing };
        var lines = new[] { new SettlementLine { CompanyId = Co, Seq = 1, LineType = SettlementLineType.Sale, Amount = 1000m, TxnDate = Day,
            PaymentIntentId = Guid.NewGuid(), MatchStatus = SettlementMatchStatus.Matched } };
        var issue = Assert.Single(SettlementBatchMath.Plan(batch, lines, channel, true).Issues, i => i.Code == SettlementPlanIssueCode.Unbalanced);
        Assert.Contains("ถึงวันที่", issue.NextStep);
        Assert.Contains("T+0", issue.NextStep);
        Assert.Contains("ปรับปรุงอื่น", issue.NextStep);       // ทางเดิมยังอยู่ท้ายข้อความ
    }

    [Theory]
    [InlineData(SettlementSourceKind.PaymentIntents, true)]   // กรอกถึงวันที่แล้ว ⇒ ข้อความเดิม
    [InlineData(SettlementSourceKind.CsvImport, false)]       // ไฟล์ ⇒ ข้อความเดิม
    [InlineData(SettlementSourceKind.CsvImport, true)]
    public void ST3_ทิศตรงข้าม_กรอกถึงวันที่แล้วหรือรอบจากไฟล์_ข้อความเดิมทุกตัวอักษร(SettlementSourceKind kind, bool hasPeriodTo)
    {
        var general = SettlementBatchMath.UnbalancedNextStep(SettlementSourceKind.CsvImport, null);
        Assert.Equal(general, SettlementBatchMath.UnbalancedNextStep(kind, hasPeriodTo ? Day : null));
        Assert.DoesNotContain("ถึงวันที่", general);
    }

    // ═════════════ A-ST5 / A-ST6: การรับรู้ผูกกับเหตุ · ไล่ใบที่อ้างทุกชั้น ═════════════

    private static readonly Guid Fee = Guid.Parse("77777777-0000-0000-0000-000000000007");
    private static readonly Guid Cn = Guid.Parse("77777777-0000-0000-0000-000000000008");
    private static readonly Guid Dn = Guid.Parse("77777777-0000-0000-0000-000000000009");
    private static readonly Guid DeadBatch = Guid.Parse("77777777-0000-0000-0000-00000000000a");
    private static readonly HashSet<(TaxType, int, int)> NothingFiled = new();

    private static SettlementUnpostDocument FeeDoc(bool etax = false, bool locked = false, string? voidBlock = null)
        => new(Fee, "PV-0009", DocumentType.PaymentVoucher, SettlementPostingKeys.FeeComponent(SettlementFeeVatTreatment.InputVatPending),
            Day, 7m, false, etax, locked, false, null, voidBlock);

    private static SettlementOrphanTriageResult Triage(SettlementUnpostDocument doc, SettlementOrphanAck? ack = null,
        IReadOnlyList<SettlementOrphanChild>? children = null)
        => SettlementOrphanTriage.Split(new[] { new SettlementOrphanArtifact(doc.Id, DeadBatch, "PO-OLD", false, doc.Number, ack) },
            SettlementUnpostGate.Evaluate(new[] { doc }, Array.Empty<SettlementUnpostCertificate>(), NothingFiled), new[] { doc }, children);

    private static SettlementOrphanAck AckWith(string? hash) => new(Guid.Parse("88888888-8888-8888-8888-888888888888"), "สมหญิง", Day,
        "ตรวจแล้วรอบใหม่ไม่ซ้ำ", hash);

    [Fact]
    public void ST5_รับรู้ด้วยเหตุเดิม_มีผล_เหตุเปลี่ยน_ไม่มีผลและบอกว่าเหตุเปลี่ยน()
    {
        var hashEtax = Assert.Single(Triage(FeeDoc(etax: true)).Items!).ReasonHash;
        Assert.NotNull(hashEtax);
        Assert.StartsWith("v1:", hashEtax);
        var ok = Triage(FeeDoc(etax: true), AckWith(hashEtax));
        Assert.Single(ok.Acknowledged!);
        Assert.Empty(ok.Unvoidable);
        // เหตุเปลี่ยน: ไม่ใช่ e-Tax แล้ว แต่เอกสารเข้ารายงานที่ล็อก ⇒ ยังยกเลิกไม่ได้ แต่ไม่ใช่เหตุที่ผู้รับรู้เห็น
        var changed = Triage(FeeDoc(locked: true), AckWith(hashEtax));
        Assert.Empty(changed.Acknowledged!);
        var item = Assert.Single(changed.Items!);
        Assert.True(item.CanAcknowledge);
        Assert.Contains("เหตุที่ยกเลิกไม่ได้เปลี่ยนไป", item.Why);
        Assert.NotEqual(hashEtax, item.ReasonHash);
    }

    [Fact]
    public void ST5_ทิศตรงข้าม_ไม่มีลายนิ้วมือ_ไม่ครอบ_และลายนิ้วมือไม่ขึ้นกับลำดับ()
    {
        var legacy = Triage(FeeDoc(etax: true), AckWith(null));
        Assert.Single(legacy.Unvoidable);
        Assert.Contains("ก่อนระบบเก็บเหตุ", Assert.Single(legacy.Items!).Why);
        Assert.Equal(SettlementOrphanTriage.ReasonHash(new[] { "a", "b" }), SettlementOrphanTriage.ReasonHash(new[] { "b", "a", "a" }));
        Assert.NotEqual(SettlementOrphanTriage.ReasonHash(new[] { "a" }), SettlementOrphanTriage.ReasonHash(new[] { "a", "b" }));
    }

    private static string ChildBlock() => DocumentVoidPreconditions.Reason(new DocumentVoidChildFact(Fee, DocumentType.CreditNote, "CN-0009", true, Cn));

    [Fact]
    public void ST6_หลานยกเลิกไม่ได้_ใบกำพร้ายกเลิกไม่ได้จริง_บอกสายเอกสาร()
    {
        // ใบลดหนี้ CN-0009 อ้างใบกำพร้า (ตัวเองยกเลิกได้) · ใบเพิ่มหนี้ DN-0001 อ้าง CN-0009 และ e-Tax ตอบรับแล้ว
        var children = new[]
        {
            new SettlementOrphanChild(Fee, Cn, DocumentType.CreditNote, "CN-0009", false, false, false),
            new SettlementOrphanChild(Cn, Dn, DocumentType.DebitNote, "DN-0001", true, false, false),
        };
        var t = Triage(FeeDoc(voidBlock: ChildBlock()), null, children);
        Assert.Empty(t.NeedsUserAction);
        var hard = Assert.Single(t.Unvoidable);
        Assert.Contains("DN-0001", hard);
        Assert.Contains("CN-0009", hard);
        Assert.True(Assert.Single(t.Items!).CanAcknowledge);
    }

    [Fact]
    public void ST6_ทิศตรงข้าม_ทั้งสายยกเลิกได้_คงกองต้องทำขั้นก่อน_และวนไม่ค้าง()
    {
        var children = new[]
        {
            new SettlementOrphanChild(Fee, Cn, DocumentType.CreditNote, "CN-0009", false, false, false),
            new SettlementOrphanChild(Cn, Dn, DocumentType.DebitNote, "DN-0001", false, false, false),
            new SettlementOrphanChild(Dn, Cn, DocumentType.CreditNote, "CN-0009", false, false, false),   // วน
        };
        var t = Triage(FeeDoc(voidBlock: ChildBlock()), null, children);
        Assert.Empty(t.Unvoidable);
        Assert.Single(t.NeedsUserAction);
        // ลูกชั้นแรกที่ยกเลิกไม่ได้ ⇒ ข้อความเดิมทุกตัวอักษร (ไม่มีสาย)
        var directChild = new SettlementOrphanChild(Fee, Cn, DocumentType.CreditNote, "CN-0009", true, false, false);
        var direct = SettlementOrphanTriage.DescendantHardReasons(new[] { directChild }, new[] { Fee });
        Assert.Equal(SettlementOrphanTriage.ChildUnvoidableReason(directChild), Assert.Single(direct[Fee]).Why);
    }

    [Fact]
    public void ST6_ลึกเกินเพดาน_ไม่ไล่ต่อ()
    {
        var ids = Enumerable.Range(0, SettlementOrphanTriage.MaxChildDepth + 2).Select(_ => Guid.NewGuid()).ToList();
        var edges = new List<SettlementOrphanChild>();
        var parent = Fee;
        for (var i = 0; i < ids.Count; i++)
        {
            var last = i == ids.Count - 1;
            edges.Add(new SettlementOrphanChild(parent, ids[i], DocumentType.CreditNote, "CN-" + i, last, false, false));
            parent = ids[i];
        }
        Assert.Empty(SettlementOrphanTriage.DescendantHardReasons(edges, new[] { Fee })[Fee]);
    }

    // ═════════════ A-ST4: รายงานของกำพร้าระดับช่องทาง ═════════════

    private static SettlementOrphanItem Item(Guid id, bool isPayment, SettlementOrphanPile pile, SettlementOrphanAck? ack)
        => new(id, isPayment, "X-" + id.ToString("N")[..4], "PO-OLD", pile, "", "", ack, false);

    [Fact]
    public void ST4_ยอดค้างผังพักของที่รับรู้แล้ว_ใบสรุปบวก_ค่าธรรมเนียมลบ_การรับชำระบวก()
    {
        var sum = Guid.NewGuid(); var fee = Guid.NewGuid(); var pay = Guid.NewGuid(); var open = Guid.NewGuid();
        var ack = AckWith("v1:x");
        var items = new[]
        {
            Item(sum, false, SettlementOrphanPile.Unvoidable, ack), Item(fee, false, SettlementOrphanPile.Unvoidable, ack),
            Item(pay, true, SettlementOrphanPile.Unvoidable, ack), Item(open, false, SettlementOrphanPile.Voidable, null),
        };
        var docs = new Dictionary<Guid, (decimal Total, string? Component)>
        {
            [sum] = (1070m, "sum-20260920"), [fee] = (107m, "fee-InputVatPending"), [open] = (50m, "fee-InputVatPending"),
        };
        var r = SettlementOrphanReport.Build(Guid.Empty, "Shopee", items, docs, new Dictionary<Guid, decimal> { [pay] = 1000m }, null);
        Assert.Equal(1070m - 107m + 1000m, r.AcknowledgedClearingTotal);   // รายการที่ยังบล็อก (ยกเลิกได้) ไม่นับ
        Assert.Equal(1, r.BlockingCount);
        Assert.Equal(-107m, r.Rows.Single(x => x.Item.Id == fee).ClearingEffect);
    }

    [Fact]
    public void ST4_ทิศตรงข้าม_ผู้มีแค่สิทธิ์ดู_ซ่อนยอด_และชิ้นไม่รู้ชนิดไม่เดาทิศ()
    {
        var d = Guid.NewGuid();
        var items = new[] { Item(d, false, SettlementOrphanPile.Unvoidable, AckWith("v1:x")) };
        var hidden = SettlementOrphanReport.Build(Guid.Empty, "Shopee", items,
            new Dictionary<Guid, (decimal Total, string? Component)> { [d] = (500m, "sum-20260920") }, new Dictionary<Guid, decimal>(), "ซ่อน");
        Assert.Null(hidden.AcknowledgedClearingTotal);
        Assert.Null(Assert.Single(hidden.Rows).Amount);
        Assert.Equal("ซ่อน", hidden.AmountsHiddenReason);
        var unknown = SettlementOrphanReport.Build(Guid.Empty, "Shopee", items,
            new Dictionary<Guid, (decimal Total, string? Component)> { [d] = (500m, "อื่น") }, new Dictionary<Guid, decimal>(), null);
        Assert.Null(Assert.Single(unknown.Rows).ClearingEffect);
        Assert.Equal(0m, unknown.AcknowledgedClearingTotal);
    }

    // ═════════════ A-ST7: ผู้ตัดสินการจับคู่/จัดประเภท = ผู้ทำใน SoD ═════════════

    [Fact]
    public void ST7_ผู้ตัดสินบรรทัดกดลงบัญชีเอง_บล็อกเมื่อเปิดแยกหน้าที่()
    {
        var poster = Guid.NewGuid();
        var makers = SettlementLineMakers.Of(new (string?, string?)[] { (Guid.NewGuid().ToString(), poster.ToString()), (null, null) });
        Assert.True(SettlementPostingGate.SodSelfApproval(true, Guid.NewGuid().ToString(), makers, poster));
    }

    [Fact]
    public void ST7_ทิศตรงข้าม_ผู้ตัดสินเป็นคนอื่นหรือระบบตัดสิน_ไม่บล็อก_ปิดแยกหน้าที่ไม่บล็อก()
    {
        var poster = Guid.NewGuid();
        var makers = SettlementLineMakers.Of(new (string?, string?)[] { (Guid.NewGuid().ToString(), Guid.NewGuid().ToString()), ("", null) });
        Assert.False(SettlementPostingGate.SodSelfApproval(true, Guid.NewGuid().ToString(), makers, poster));
        Assert.False(SettlementPostingGate.SodSelfApproval(false, Guid.NewGuid().ToString(),
            SettlementLineMakers.Of(new (string?, string?)[] { (null, poster.ToString()) }), poster));
        Assert.DoesNotContain("", makers);
    }

    // ═════════════ A-ST8: ลายนิ้วมือชิ้นตอนออกเอกสาร ═════════════

    private sealed class Scenario
    {
        public readonly SettlementBatch Batch = new() { CompanyId = Co, PayoutRef = "PO-001", PayoutDate = Day, NetPayout = 1963m, BankAccountId = Guid.NewGuid() };
        public readonly SettlementChannel Channel = new()
        {
            CompanyId = Co, Kind = SettlementChannelKind.Marketplace, DisplayName = "Shopee", ClearingAccountId = Clearing,
            CounterpartyContactId = Guid.NewGuid(),
        };
        public readonly SettlementLine Receipt = new() { Id = Guid.NewGuid(), CompanyId = Co, Seq = 1, LineType = SettlementLineType.Sale, Amount = 1000m,
            MatchedDocumentId = DocX, TxnDate = Day, ExternalOrderId = "SP-0", MatchStatus = SettlementMatchStatus.Matched };
        public readonly SettlementLine Summary = new() { Id = Guid.NewGuid(), CompanyId = Co, Seq = 2, LineType = SettlementLineType.Sale, Amount = 1070m,
            TxnDate = Day, ExternalOrderId = "SP-1", MatchStatus = SettlementMatchStatus.AutoSummary };
        public readonly SettlementLine Fee = new() { Id = Guid.NewGuid(), CompanyId = Co, Seq = 3, LineType = SettlementLineType.Commission, Amount = -107m,
            TxnDate = Day, MatchStatus = SettlementMatchStatus.NotRequired };
        public IReadOnlyList<SettlementLine> Lines => new[] { Receipt, Summary, Fee };
        public SettlementPostingPlan Plan() => SettlementBatchMath.Plan(Batch, Lines, Channel, true);
        public string SumComponent => SettlementPostingKeys.SummaryComponent(Assert.Single(Plan().SummarySales).Date);
    }

    [Fact]
    public void ST8_แผนก่อนแก้คลาดจากเอกสารแล้ว_การแก้ที่ทำให้กลับมาตรงเนื้อหาที่ออก_แก้ได้()
    {
        var s = new Scenario();
        var component = s.SumComponent;
        var issued = SettlementPlanFingerprint.PieceHash(s.Plan(), component);   // ตอนออกใบสรุป (ใบสรุปมีบรรทัด SP-1)
        s.Receipt.MatchStatus = SettlementMatchStatus.AutoSummary;              // ภายหลังบรรทัด SP-0 ถูกย้ายเข้าใบสรุป (เส้นเก่า) ⇒ คลาด
        s.Receipt.MatchedDocumentId = null;
        var drifted = s.Plan();
        s.Receipt.MatchStatus = SettlementMatchStatus.Matched;                  // ผู้ใช้แก้กลับให้ตรงที่ออก
        s.Receipt.MatchedDocumentId = DocX;
        var withFp = new SettlementFrozenParts(new[] { component }, Array.Empty<Guid>(),
            new Dictionary<string, string> { [component] = issued });
        Assert.Null(SettlementPartialEdit.Refusal(drifted, s.Plan(), withFp));
        // ทิศตรงข้าม: ไม่มีลายนิ้วมือ (ใบก่อนรอบ 201) ⇒ พฤติกรรมเดิม (ชิ้นเปลี่ยน = ปฏิเสธ)
        Assert.NotNull(SettlementPartialEdit.Refusal(drifted, s.Plan(), withFp with { IssuedFingerprints = null }));
    }

    [Fact]
    public void ST8_ทิศตรงข้าม_มีลายนิ้วมือแต่การแก้ทำให้ต่างจากที่ออก_ยังปฏิเสธ()
    {
        var s = new Scenario();
        var component = s.SumComponent;
        var before = s.Plan();
        var frozen = new SettlementFrozenParts(new[] { component }, Array.Empty<Guid>(),
            new Dictionary<string, string> { [component] = SettlementPlanFingerprint.PieceHash(before, component) });
        s.Receipt.MatchStatus = SettlementMatchStatus.AutoSummary;   // ย้ายบรรทัดเข้าใบสรุปที่ออกแล้ว
        s.Receipt.MatchedDocumentId = null;
        Assert.Contains("ใบขายสรุปรายวัน", SettlementPartialEdit.Refusal(before, s.Plan(), frozen));
    }

    [Fact]
    public void ST8_เอกสารที่ลงไว้มีเนื้อหาไม่ตรงแผน_เตือนไม่บล็อก_ไม่มีลายนิ้วมือไม่เตือน()
    {
        var s = new Scenario();
        var component = s.SumComponent;
        var issued = SettlementPlanFingerprint.PieceHash(s.Plan(), component);
        Assert.Null(SettlementPlanFingerprint.IssuedDrift(s.Plan(), new[] { (component, "INV-S1", (string?)issued) }));
        s.Receipt.MatchStatus = SettlementMatchStatus.AutoSummary;   // ย้ายบรรทัดเข้าใบสรุป ⇒ เนื้อหาเปลี่ยน
        s.Receipt.MatchedDocumentId = null;
        var drift = SettlementPlanFingerprint.IssuedDrift(s.Plan(), new[] { (component, "INV-S1", (string?)issued) });
        Assert.NotNull(drift);
        Assert.False(drift!.Blocking);
        Assert.Equal(SettlementPlanIssueCode.IssuedPieceDrift, drift.Code);
        Assert.Null(SettlementPlanFingerprint.IssuedDrift(s.Plan(), new[] { (component, "INV-S1", (string?)null) }));
    }

    // ═════════════ A-ST9: คีย์ "วันที่ตามตัวอักษร" เทียบเฉพาะบรรทัดที่นำเข้าด้วยตัวอ่านรุ่นก่อน ═════════════

    private static readonly SettlementTxnKeyInput[] RefundRow = { new("R-1", "refund", null, -50m, new DateTime(2026, 9, 21), "PO-9") };
    private static readonly IReadOnlyList<DateTime?>[] LiteralDates = { new DateTime?[] { new DateTime(2026, 9, 20) } };

    [Fact]
    public void ST9_คีย์วันที่ตามตัวอักษร_ไม่นับบรรทัดที่นำเข้าด้วยตัวอ่านรุ่นปัจจุบัน()
    {
        var lit = Assert.Single(SettlementTxnKey.LegacyKeySets(RefundRow, "PO-9", LiteralDates)).LiteralCurrent;
        Assert.NotEmpty(lit);
        var only = lit.ToHashSet(StringComparer.Ordinal);
        Assert.False(SettlementTxnKey.CountsAsExisting(lit[0], SettlementTxnKey.StoredKeyVersion, only));   // อีกรายการที่นำเข้าหลังมีคอลัมน์
        Assert.True(SettlementTxnKey.CountsAsExisting(lit[0], null, only));                                 // บรรทัดก่อนมีคอลัมน์ = ทิศเดิม
    }

    [Fact]
    public void ST9_ทิศตรงข้าม_คีย์รุ่นอื่นนับเสมอ_และLegacyKeysเดิมครบเท่าเดิม()
    {
        var set = Assert.Single(SettlementTxnKey.LegacyKeySets(RefundRow, "PO-9", LiteralDates));
        var only = set.LiteralCurrent.ToHashSet(StringComparer.Ordinal);
        Assert.All(set.Any, k => Assert.True(SettlementTxnKey.CountsAsExisting(k, SettlementTxnKey.StoredKeyVersion, only)));
        Assert.Equal(set.Any.Concat(set.LiteralCurrent).OrderBy(x => x, StringComparer.Ordinal),
            Assert.Single(SettlementTxnKey.LegacyKeys(RefundRow, "PO-9", LiteralDates)).OrderBy(x => x, StringComparer.Ordinal));
    }

    // ═════════════ C-9 (คำตัดสินข้อ 82): ใบสรุปกำพร้าที่รับรู้แล้ว = ใบแรกของวัน + ด่านเนื้อหาซ้ำทำงานต่อ ═════════════

    [Fact]
    public void C9_ใบสรุปกำพร้าที่รับรู้แล้วมีผล_นับเป็นใบแรก_ยังไม่รับรู้หรือการรับรู้ไม่มีผล_ไม่นับ()
    {
        var d = Guid.NewGuid();
        Assert.True(SettlementSummarySupplement.AckedOrphanCountsAsFirst(d, new[] { Item(d, false, SettlementOrphanPile.Unvoidable, AckWith("v1:x")) }));
        Assert.False(SettlementSummarySupplement.AckedOrphanCountsAsFirst(d, new[] { Item(d, false, SettlementOrphanPile.Unvoidable, null) }));
        Assert.False(SettlementSummarySupplement.AckedOrphanCountsAsFirst(d, new[] { Item(d, false, SettlementOrphanPile.Voidable, AckWith("v1:x")) }));
        Assert.False(SettlementSummarySupplement.AckedOrphanCountsAsFirst(d, new[] { Item(d, true, SettlementOrphanPile.Unvoidable, AckWith("v1:x")) }));
        Assert.False(SettlementSummarySupplement.AckedOrphanCountsAsFirst(d, null));
    }

    [Fact]
    public void C9_รายการเดียวกับรอบเจ้าของใบกำพร้า_เลขรายการหรือออเดอร์ตรง_บล็อกเป็นรายได้ซ้ำ()
    {
        var owner = Guid.NewGuid();
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid(); var rowKey = Guid.NewGuid();
        var dups = SettlementSummarySupplement.OrphanFirstDuplicates(
            new (Guid, string?, string?)[] { (a, "v2:T-1:abc", null), (b, null, "SP-9"), (c, "v2:T-2:def", "SP-NEW"), (rowKey, "v2:row:zzz", null) },
            new (Guid, string?, string?)[] { (owner, "v2:T-1:abc", "SP-1"), (owner, null, "SP-9"), (owner, "v2:row:zzz", null) },
            new Dictionary<Guid, (string PayoutRef, string Number)> { [owner] = ("PO-OLD", "INV-S1") });
        var dup = Assert.Single(dups);
        Assert.Equal(new[] { a, b }.OrderBy(x => x), dup.LineIds.OrderBy(x => x));   // แถวไม่มี id ⇒ ตัดสินด้วยเนื้อหาที่ SplitDuplicates
        Assert.Contains("PO-OLD", dup.Evidence);
        Assert.Contains("INV-S1", dup.Evidence);
        Assert.False(dup.DistinctConfirmable);
    }

    [Fact]
    public void C9_ทิศตรงข้าม_ใบกำพร้าเป็นใบแรก_เนื้อหาไม่ตรง_เป็นใบสรุปเพิ่มเติมอ้างเลขใบกำพร้า()
    {
        var line = Guid.NewGuid();
        var sup = SettlementSummarySupplement.Judge(Day, new[] { line },
            new[] { new SettlementSameDaySummary("INV-S1", "PO-OLD", true, Day) });
        Assert.NotNull(sup);
        Assert.Equal("INV-S1", sup!.FirstNumber);
        var (dups, kept) = SettlementSummarySupplement.SplitDuplicates(new[] { sup }, Array.Empty<SettlementContentHit<Guid>>(), new HashSet<Guid>());
        Assert.Empty(dups);
        Assert.Single(kept);
        // เนื้อหาตรงรอบเจ้าของใบกำพร้าทุกบรรทัด ⇒ ไฟล์ซ้ำ (บล็อก)
        var (dups2, kept2) = SettlementSummarySupplement.SplitDuplicates(new[] { sup },
            new[] { new SettlementContentHit<Guid>(line, new[] { "PO-OLD" }) }, new HashSet<Guid>());
        Assert.Single(dups2);
        Assert.Empty(kept2);
        Assert.Empty(SettlementSummarySupplement.OrphanFirstDuplicates(
            new (Guid, string?, string?)[] { (line, "v2:T-3:x", "SP-3") }, new (Guid, string?, string?)[] { (Guid.NewGuid(), "v2:T-4:y", "SP-4") },
            new Dictionary<Guid, (string PayoutRef, string Number)>()));
    }

    // ═════════════ ฝ่ายค้านรอบ 201 (ST-X1..X7) ═════════════

    [Fact]
    public void X1_ยกเลิกล้มกลางทาง_ข้อความมีธงของชิ้นที่ยกเลิกไปแล้ว_ตัวเดียวกับเส้นสำเร็จ()
    {
        var flags = new[] { "ใบเสร็จ RV-1 ส่ง e-Tax ระหว่างทาง", "ภาษีขาย 9/2026 ประกาศยื่นแล้ว" };
        var msg = SettlementUnpostNotice.Partial("PO-9", "ล้มที่ใบที่ 3", 2, 1, flags);
        Assert.Contains("ยกเลิกไปแล้ว 2 เอกสาร · 1 การรับชำระ", msg);
        Assert.Contains("มี 2 รายการที่ต้องตามต่อ", msg);
        Assert.Contains("RV-1", msg);
        Assert.EndsWith(SettlementUnpostNotice.FlagsTail(flags), msg);
    }

    [Fact]
    public void X1_ทิศตรงข้าม_ไม่มีธง_ไม่มีท้ายข้อความตามต่อ()
    {
        Assert.Equal("", SettlementUnpostNotice.FlagsTail(Array.Empty<string>()));
        var msg = SettlementUnpostNotice.Partial("PO-9", "x", 0, 0, Array.Empty<string>());
        Assert.DoesNotContain("ต้องตามต่อ", msg);
        Assert.Contains("กดยกเลิกอีกครั้ง", msg);
    }

    [Fact]
    public void X2_ประทับเจ้าของตอนแถวเริ่มถูกติดตาม_ก่อนSaveChanges_auditจับค่าจริงได้()
    {
        using var db = OfflineDb();
        var mine = new Payment { CompanyId = Guid.NewGuid(), DocumentId = DocX, PaymentNumber = "PAY-1" };
        var other = new Payment { CompanyId = Guid.NewGuid(), DocumentId = DocY, PaymentNumber = "PAY-2" };
        using (SettlementPaymentOwner.StampOnSave(db, BatchA, DocX))
        {
            db.Payments.Add(mine);
            db.Payments.Add(other);
            Assert.Equal(BatchA, mine.SettlementBatchId);   // ยังไม่ SaveChanges — ค่าอยู่แล้วตอน CaptureAuditEntries
            Assert.Null(other.SettlementBatchId);          // ใบอื่นไม่ถูกแตะ
        }
    }

    [Fact]
    public void X2_ทิศตรงข้าม_นอกขอบเขต_เพิ่มหลังถอดตัวฟัง_ไม่ประทับ_และไม่ย้ายเจ้าของเดิม()
    {
        using var db = OfflineDb();
        var owned = new Payment { CompanyId = Guid.NewGuid(), DocumentId = DocX, PaymentNumber = "PAY-3", SettlementBatchId = BatchB };
        using (SettlementPaymentOwner.StampOnSave(db, BatchA, DocX))
            db.Payments.Add(owned);
        Assert.Equal(BatchB, owned.SettlementBatchId);
        var later = new Payment { CompanyId = Guid.NewGuid(), DocumentId = DocX, PaymentNumber = "PAY-4" };
        db.Payments.Add(later);
        Assert.Null(later.SettlementBatchId);
    }

    [Fact]
    public void X3_ข้อความแยกหน้าที่บอกบทบาทที่ชนจริง()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid().ToString();
        Assert.Equal(new[] { "ผู้สร้างรอบโอน" }, SettlementLineMakers.RolesOf(me.ToString(), Array.Empty<(string?, string?)>(), me));
        Assert.Equal(new[] { "ผู้เติมไฟล์เข้ารอบ" },
            SettlementLineMakers.RolesOf(other, new (string?, string?)[] { (me.ToString(), null) }, me));
        Assert.Equal(new[] { "ผู้ตัดสินการจับคู่/จัดประเภทบรรทัด" },
            SettlementLineMakers.RolesOf(other, new (string?, string?)[] { (other, me.ToString().ToUpperInvariant()) }, me));
        Assert.Contains("ไม่รู้ผู้สร้างรอบโอน (ระบบบล็อกไว้ก่อน)", SettlementLineMakers.RolesOf(null, Array.Empty<(string?, string?)>(), me));

        var plan = SodPlan();
        var issue = Assert.Single(SettlementPostingGate.Evaluate(plan, SodFacts(plan) with
            { SodSelfApprovalBlocked = true, SodRoles = new[] { "ผู้ตัดสินการจับคู่/จัดประเภทบรรทัด" } }).Issues,
            i => i.Code == SettlementPlanIssueCode.SodSelfApproval);
        Assert.Contains("เป็นผู้ตัดสินการจับคู่/จัดประเภทบรรทัดของรอบโอนนี้เอง", issue.Message);
        Assert.DoesNotContain("ผู้นำเข้า", issue.Message);
    }

    [Fact]
    public void X3_ทิศตรงข้าม_ไม่ชนบทบาทใด_รายการว่าง_ไม่ส่งบทบาท_ข้อความเดิม_ปิดด่าน_ไม่มีปัญหา()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid().ToString();
        Assert.Empty(SettlementLineMakers.RolesOf(other, new (string?, string?)[] { (other, other), (null, null) }, me));
        var plan = SodPlan();
        var issue = Assert.Single(SettlementPostingGate.Evaluate(plan, SodFacts(plan) with { SodSelfApprovalBlocked = true }).Issues,
            i => i.Code == SettlementPlanIssueCode.SodSelfApproval);
        Assert.Contains("ผู้นำเข้ารอบโอนนี้เอง", issue.Message);
        Assert.DoesNotContain(SettlementPostingGate.Evaluate(plan, SodFacts(plan) with { SodRoles = new[] { "ผู้สร้างรอบโอน" } }).Issues,
            i => i.Code == SettlementPlanIssueCode.SodSelfApproval);   // บทบาทเป็นข้อความเท่านั้น — ไม่บล็อกเอง
    }

    private static SettlementPostingPlan SodPlan()
    {
        var batch = new SettlementBatch { CompanyId = Co, PayoutRef = "PO-SOD", PayoutDate = Day, NetPayout = 963m, BankAccountId = Guid.NewGuid() };
        var channel = new SettlementChannel
        {
            CompanyId = Co, Kind = SettlementChannelKind.Marketplace, DisplayName = "Shopee", ClearingAccountId = Clearing,
            CounterpartyContactId = Guid.NewGuid(),
        };
        var lines = new[]
        {
            new SettlementLine { CompanyId = Co, Seq = 1, LineType = SettlementLineType.Sale, Amount = 1070m, TxnDate = Day,
                ExternalOrderId = "SP-1", MatchStatus = SettlementMatchStatus.AutoSummary },
            new SettlementLine { CompanyId = Co, Seq = 2, LineType = SettlementLineType.Commission, Amount = -107m, TxnDate = Day,
                MatchStatus = SettlementMatchStatus.NotRequired },
        };
        return SettlementBatchMath.Plan(batch, lines, channel, true);
    }

    private static SettlementPostingFacts SodFacts(SettlementPostingPlan plan) => new(
        SettlementBatchStatus.Matched, Day, Day, null, new Dictionary<DateTime, string>(),
        new HashSet<(int, int)>(), new HashSet<(int, int)>(), Array.Empty<string>(),
        true, true, Clearing,
        plan.Receipts.Select(r => new SettlementReceiptTarget(r.DocumentId, true, "TIV-0001", DocumentType.TaxInvoice,
            DocumentStatus.Approved, r.Amount, false)).ToList(),
        Array.Empty<SettlementClearingSource>(), Array.Empty<SettlementDuplicateSale>(), true, true, 0);

    private static readonly SettlementOrphanAck XAck = new(Guid.Parse("88888888-8888-8888-8888-888888888888"), "สมหญิง", Day, "ยื่นภาษีแล้ว", "v1:abc");

    [Fact]
    public void X4_สถานะการรับรู้ของเอกสาร_มีผล_ป้ายรับรู้แล้ว_ไม่มีทางไปต่อ()
    {
        var eff = new SettlementOrphanItem(DocX, false, "PV-1", "PO-OLD", SettlementOrphanPile.Unvoidable, "ยกเลิกไม่ได้", "ไม่ต้องทำอะไร", XAck, false);
        var st = SettlementOrphanReport.AckStatusOf(DocX, new[] { eff });
        Assert.True(st.Effective);
        Assert.Equal("✅ รับรู้แล้ว", st.Label);
        Assert.Null(st.NextStep);
    }

    [Fact]
    public void X4_ทิศตรงข้าม_การรับรู้ไม่ครอบเหตุ_ไม่อยู่ในรายการ_เป็นการรับชำระ_ไม่มีผลพร้อมทางไปต่อ()
    {
        // ตัวแยกคืนกองยกเลิกไม่ได้จริงที่การรับรู้ไม่ครอบเป็น "ยังไม่รับรู้" (Ack = null) + เหตุ
        var stale = new SettlementOrphanItem(DocX, false, "PV-1", "PO-OLD", SettlementOrphanPile.Unvoidable,
            "ยกเลิกไม่ได้ (เหตุเปลี่ยนหลังรับรู้)", "ตรวจแล้วกดรับรู้", null, true);
        var st = SettlementOrphanReport.AckStatusOf(DocX, new[] { stale });
        Assert.False(st.Effective);
        Assert.Contains("ต้องตรวจแล้วรับรู้ใหม่", st.Label);
        Assert.Contains("ทางไปต่อ: ตรวจแล้วกดรับรู้", st.NextStep);
        var voidable = stale with { Pile = SettlementOrphanPile.Voidable, Ack = XAck };
        Assert.Contains("ต้องยกเลิกแทน", SettlementOrphanReport.AckStatusOf(DocX, new[] { voidable }).Label);
        Assert.False(SettlementOrphanReport.AckStatusOf(DocX, Array.Empty<SettlementOrphanItem>()).Effective);
        Assert.False(SettlementOrphanReport.AckStatusOf(DocX, null).Effective);
        var payment = new SettlementOrphanItem(DocX, true, "RV-1", "PO-OLD", SettlementOrphanPile.Unvoidable, "", "", XAck, false);
        Assert.False(SettlementOrphanReport.AckStatusOf(DocX, new[] { payment }).Effective);   // id ซ้ำกับการรับชำระ — ไม่ใช่เอกสาร
    }

    [Fact]
    public void X5_ตัวนับแถวที่ไม่ถูกbackfill_อ่านอย่างเดียว_เฉพาะแถวที่ยังไม่มีเจ้าของและมีป้าย()
    {
        var sql = SettlementPostingKeys.PaymentOwnerUnbackfilledCountSql();
        Assert.StartsWith("SELECT COUNT(*)", sql);
        Assert.Contains("\"SettlementBatchId\" IS NULL", sql);
        Assert.Contains("LIKE '" + SettlementPostingKeys.PaymentMarkerHead + "%'", sql);
        Assert.DoesNotContain("UPDATE", sql);
    }

    [Fact]
    public void X6_บรรทัดขายที่เพิ่งอ้างintentของรอบอื่น_ล้มดัง()
    {
        Assert.True(SettlementSaleMatch.SaleReferenceTakenByOtherBatch(false, true, BatchB, BatchA));
    }

    [Fact]
    public void X6_ทิศตรงข้าม_บรรทัดคืนเงิน_อ้างค้างจากก่อนหน้า_เจ้าของคือรอบนี้หรือไม่มีเจ้าของ_ไม่ล้ม()
    {
        Assert.False(SettlementSaleMatch.SaleReferenceTakenByOtherBatch(true, true, BatchB, BatchA));    // คืนเงินภายหลัง = ปกติ
        Assert.False(SettlementSaleMatch.SaleReferenceTakenByOtherBatch(false, false, BatchB, BatchA));  // อ้างค้าง — ไม่ทำให้ทุกการแก้ตัน
        Assert.False(SettlementSaleMatch.SaleReferenceTakenByOtherBatch(false, true, BatchA, BatchA));
        Assert.False(SettlementSaleMatch.SaleReferenceTakenByOtherBatch(false, true, null, BatchA));
    }

    [Fact]
    public void X7_รายงานกำพร้า_ข้อความซ่อนยอดเฉพาะของรายงาน_เกณฑ์เดียวกับหน้ารอบโอน()
    {
        var reason = SettlementPermissionScope.OrphanAmountsHiddenReason(false, false);
        Assert.NotNull(reason);
        Assert.Contains("กำพร้า", reason);
        Assert.Contains("ผังพัก", reason);
        Assert.NotEqual(SettlementPermissionScope.CandidatesHiddenReason(false, false), reason);
        Assert.DoesNotContain("ผู้สมัคร", reason);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void X7_ทิศตรงข้าม_มีสิทธิ์นำเข้าหรือลงบัญชี_เห็นยอด(bool canImport, bool canPost)
    {
        Assert.Null(SettlementPermissionScope.OrphanAmountsHiddenReason(canImport, canPost));
        Assert.Null(SettlementPermissionScope.CandidatesHiddenReason(canImport, canPost));
    }
}

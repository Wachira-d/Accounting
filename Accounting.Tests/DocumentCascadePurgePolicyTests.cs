using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 242 · คำตัดสินข้อ 242-A — ตัวตัดสินเดียวของการลบเอกสารถาวร (พร้อม/ไม่พร้อมเอกสารที่เกี่ยวข้อง) · ทั้งสองทิศ:
/// สายร่างลบครบ (ลูกก่อนราก) · ลูกหลานที่ยังมีผล/ใบลดหนี้อ้างเลขที่ ⇒ ปฏิเสธทั้งคำขอพร้อมรายชื่อและทางไปต่อ · e-Tax ถึงกรมสรรพากร/รายงานที่ยื่น ⇒
/// ปฏิเสธแม้ force · ช่วงเก็บรักษา ⇒ force + เหตุผล · "ลบเฉพาะใบนี้" ไม่ตัดสินลูกหลาน (ตัดการอ้างอิงตามเดิม)
/// </summary>
public class DocumentCascadePurgePolicyTests
{
    private static readonly DateTime Today = new(2026, 11, 6);
    private static readonly Guid Root = Guid.NewGuid();

    private static PurgeNodeFacts RootNode(DocumentStatus status = DocumentStatus.Approved, DateTime? until = null,
        bool etax = false, bool filed = false)
        => new(Root, null, 0, status == DocumentStatus.Draft ? "DRAFT-1" : "QT-2026-0001", DocumentType.Quotation, status, until, etax, filed);

    private static PurgeNodeFacts Child(string no, DocumentType type, DocumentStatus status, int depth = 1, Guid? parent = null,
        DateTime? until = null, bool etax = false, bool filed = false, bool byText = false)
        => new(Guid.NewGuid(), parent ?? Root, depth, no, type, status, until, etax, filed, byText);

    [Fact]
    public void สายร่างทั้งหมด_ลบพร้อมเอกสารที่เกี่ยวข้องได้_ลูกหลานก่อนรากท้ายสุด()
    {
        var inv = Child("DRAFT-2", DocumentType.Invoice, DocumentStatus.Draft);
        var rec = Child("DRAFT-3", DocumentType.Receipt, DocumentStatus.WaitingApproval, depth: 2, parent: inv.Id);
        var v = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Draft), inv, rec }, cascade: true, force: false, reason: null, Today);
        Assert.True(v.Allowed);
        Assert.False(v.NeedsReason);
        Assert.Equal(new[] { rec.Id, inv.Id, Root }, v.DeleteOrder);
    }

    [Fact]
    public void เจ้าของ_ใบเสนอราคาที่ออกใบแจ้งหนี้แล้ว_ลบพร้อมเอกสารที่เกี่ยวข้อง_ปฏิเสธพร้อมรายชื่อและทางไปต่อ()
    {
        var inv = Child("INV-2026-0042", DocumentType.Invoice, DocumentStatus.Approved, until: Today.AddYears(5));
        var v = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Approved, Today.AddYears(5)), inv },
            cascade: true, force: true, reason: "ข้อมูลทดสอบ", Today);
        Assert.False(v.Allowed);
        Assert.Equal(DocumentCascadePurgePolicy.RuleDescendantEffective, v.RuleCode);
        Assert.Contains("INV-2026-0042", v.Message);
        Assert.Contains("ยกเลิก (Void)", v.Message);
        Assert.Contains("ลบเฉพาะใบนี้", v.Message);
        Assert.Contains("ระบบยังไม่ได้ลบอะไร", v.Message);
        Assert.Empty(v.DeleteOrder);
    }

    [Fact]
    public void ใบลูกที่ยกเลิกแล้ว_ลบตามได้_แต่ช่วงเก็บรักษาต้องforceและเหตุผล()
    {
        var voided = Child("TIV-2026-0007", DocumentType.TaxInvoice, DocumentStatus.Voided, until: Today.AddYears(5));
        var nodes = new[] { RootNode(DocumentStatus.Draft), voided };
        var noForce = DocumentCascadePurgePolicy.Decide(nodes, true, false, null, Today);
        Assert.False(noForce.Allowed);
        Assert.Equal(DocumentCascadePurgePolicy.RuleRetention, noForce.RuleCode);
        Assert.Contains("TIV-2026-0007", noForce.Message);
        var noReason = DocumentCascadePurgePolicy.Decide(nodes, true, true, "   ", Today);
        Assert.False(noReason.Allowed);
        Assert.Equal(DocumentCascadePurgePolicy.RuleReasonRequired, noReason.RuleCode);
        var ok = DocumentCascadePurgePolicy.Decide(nodes, true, true, "สร้างซ้ำจาก integration", Today);
        Assert.True(ok.Allowed);
        Assert.True(ok.NeedsReason);
        Assert.Equal(new[] { voided.Id, Root }, ok.DeleteOrder);
    }

    [Fact]
    public void ห้ามเด็ดขาด_eTaxถึงกรมสรรพากรหรืออยู่ในรายงานที่ยื่น_แม้forceพร้อมเหตุผล()
    {
        var etax = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Voided, Today.AddYears(5), etax: true) },
            false, true, "ทดสอบ", Today);
        Assert.False(etax.Allowed);
        Assert.Equal(DocumentCascadePurgePolicy.RuleEtaxReached, etax.RuleCode);
        Assert.DoesNotContain("ลบเฉพาะใบนี้", etax.Message);   // รากถูกกัน — ทางลบเฉพาะใบก็ไม่ช่วย ห้ามแนะนำ

        var filedChild = Child("DRAFT-9", DocumentType.Invoice, DocumentStatus.Voided, filed: true);
        var filed = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Draft), filedChild }, true, true, "ทดสอบ", Today);
        Assert.False(filed.Allowed);
        Assert.Equal(DocumentCascadePurgePolicy.RuleFilingLocked, filed.RuleCode);
    }

    [Fact]
    public void ใบลดหนี้ที่อ้างแค่เลขที่และยังมีผล_กันการลบพร้อมเอกสารที่เกี่ยวข้อง()
    {
        var cn = Child("CN-2026-0003", DocumentType.CreditNote, DocumentStatus.Approved, byText: true);
        var v = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Draft), cn }, true, false, null, Today);
        Assert.False(v.Allowed);
        Assert.Contains("CN-2026-0003", v.Message);
    }

    [Fact]
    public void ทิศตรงข้าม_ลบเฉพาะใบนี้_ไม่ตัดสินลูกหลาน_คงไว้ตัดการอ้างอิง()
    {
        // ลูกที่ยังมีผล + e-Tax ถึงกรมสรรพากร: ลบเฉพาะรากไม่แตะลูก ⇒ ไม่ใช่เหตุกัน (พฤติกรรมเดิมของการลบถาวร)
        var tiv = Child("TIV-2026-0010", DocumentType.TaxInvoice, DocumentStatus.Approved, until: Today.AddYears(5), etax: true);
        var v = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Draft), tiv }, cascade: false, force: false, reason: null, Today);
        Assert.True(v.Allowed);
        Assert.Equal(new[] { Root }, v.DeleteOrder);
        Assert.Equal(PurgeNodeAction.Unlink, v.Nodes.Single(n => n.Node.Id == tiv.Id).Action);
        Assert.Contains("คงเอกสารที่เกี่ยวข้อง 1 ใบ", v.Message);
    }

    // ── ฝ่ายค้านรอบ 242 ──

    [Fact]
    public void B1_ใบเสร็จหลักฐานของใบที่ลบ_ผ่านด่านเดียวกัน_eTaxถึงกรมสรรพากรกันแม้ลบเฉพาะใบนี้()
    {
        var rec = Child("RE-2026-0001", DocumentType.Receipt, DocumentStatus.Voided, until: Today.AddYears(5), etax: true) with { IsSettlementReceipt = true };
        var v = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Voided, Today.AddYears(5)), rec }, cascade: false, force: true, reason: "ทดสอบ", Today);
        Assert.False(v.Allowed);
        Assert.Equal(DocumentCascadePurgePolicy.RuleEtaxReached, v.RuleCode);
        Assert.Contains("RE-2026-0001", v.Message);
    }

    [Fact]
    public void B1_ใบเสร็จหลักฐานที่ยังมีผล_กัน_บอกให้ยกเลิกการชำระ_ทิศตรงข้ามใบเสร็จที่ยกเลิกแล้วลบพร้อมใบแม่()
    {
        var live = Child("RE-2026-0002", DocumentType.Receipt, DocumentStatus.Approved) with { IsSettlementReceipt = true };
        var blocked = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Voided, Today.AddYears(5)), live }, false, true, "ทดสอบ", Today);
        Assert.False(blocked.Allowed);
        Assert.Contains("ยกเลิกการชำระเงิน", blocked.Message);

        var voided = Child("RE-2026-0003", DocumentType.Receipt, DocumentStatus.Voided, until: Today.AddYears(5)) with { IsSettlementReceipt = true };
        var ok = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Voided, Today.AddYears(5)), voided }, false, true, "ทดสอบ", Today);
        Assert.True(ok.Allowed);
        Assert.Equal(new[] { voided.Id, Root }, ok.DeleteOrder);
    }

    [Fact]
    public void S2_JEอยู่ในงวดที่ปิดแล้ว_กันทุกใบที่จะถูกลบ_รวมราก()
    {
        var v = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Voided, Today.AddYears(5)) with { PeriodClosed = true } },
            false, true, "ทดสอบ", Today);
        Assert.False(v.Allowed);
        Assert.Equal(DocumentCascadePurgePolicy.RulePeriodClosed, v.RuleCode);
        Assert.Contains("งวด", v.Message);
    }

    [Fact]
    public void S3_ใบแทนของใบที่ยังมีผล_หรือมีใบนอกแผนชี้มาเป็นใบแทน_กัน_ทิศตรงข้ามใบเดิมอยู่ในแผนเดียวกันลบได้()
    {
        var original = Guid.NewGuid();
        var replaces = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Draft) with { ReplacesLiveDocumentId = original } },
            false, false, null, Today);
        Assert.False(replaces.Allowed);
        Assert.Equal(DocumentCascadePurgePolicy.RuleReplacement, replaces.RuleCode);

        var pointed = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Draft) with { ReplacedByPointers = new[] { original } } },
            false, false, null, Today);
        Assert.False(pointed.Allowed);
        Assert.Equal(DocumentCascadePurgePolicy.RuleReplacement, pointed.RuleCode);

        var child = Child("DRAFT-7", DocumentType.TaxInvoice, DocumentStatus.Draft);
        var inPlan = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Draft) with { ReplacedByPointers = new[] { child.Id } }, child },
            true, false, null, Today);
        Assert.True(inPlan.Allowed);
    }

    [Fact]
    public void S4_ปันส่วนชำระกับใบนอกแผน_กัน_ทิศตรงข้ามอีกฝั่งอยู่ในแผนลบได้()
    {
        var other = Guid.NewGuid();
        var v = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Draft) with { AllocationCounterparts = new[] { other } } },
            false, false, null, Today);
        Assert.False(v.Allowed);
        Assert.Equal(DocumentCascadePurgePolicy.RuleAllocation, v.RuleCode);

        var child = Child("DRAFT-8", DocumentType.Invoice, DocumentStatus.Draft);
        var ok = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Draft) with { AllocationCounterparts = new[] { child.Id } }, child },
            true, false, null, Today);
        Assert.True(ok.Allowed);
    }

    [Fact]
    public void N1_ใบสั่งซื้อที่อนุมัติแล้วแต่ไม่มีผลทางบัญชี_ลบตามได้ด้วยforceและเหตุผล_ทิศตรงข้ามมีJEหรือสต็อกกัน()
    {
        var po = Child("PO-2026-0009", DocumentType.PurchaseOrder, DocumentStatus.Approved, until: Today.AddYears(5));
        var nodes = new[] { RootNode(DocumentStatus.Approved, Today.AddYears(5)), po };
        var noForce = DocumentCascadePurgePolicy.Decide(nodes, true, false, null, Today);
        Assert.False(noForce.Allowed);
        Assert.True(noForce.NeedsReason);
        var ok = DocumentCascadePurgePolicy.Decide(nodes, true, true, "ลูกค้ายกเลิกงาน", Today);
        Assert.True(ok.Allowed);
        Assert.Equal(new[] { po.Id, Root }, ok.DeleteOrder);

        foreach (var withEffect in new[] { po with { HasJournal = true }, po with { HasStockMovement = true }, po with { HasLivePayment = true } })
        {
            var v = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Approved, Today.AddYears(5)), withEffect }, true, true, "x", Today);
            Assert.False(v.Allowed);
            Assert.Equal(DocumentCascadePurgePolicy.RuleDescendantEffective, v.RuleCode);
        }
        Assert.True(DocumentCascadePurgePolicy.HasAccountingEffect(DocumentType.TaxInvoice, false, false, false));   // ชนิดที่มีเลข §86/4 มีผลเสมอ
        Assert.False(DocumentCascadePurgePolicy.HasAccountingEffect(DocumentType.Quotation, false, false, false));
    }

    [Fact]
    public void N3_บิลPOSหรือเครดิตภาษีหักที่ใช้แล้ว_กัน()
    {
        var pos = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Draft) with { PosLinked = true } }, false, false, null, Today);
        Assert.Equal(DocumentCascadePurgePolicy.RulePosLinked, pos.RuleCode);
        Assert.Contains("POS", pos.Message);
        var wht = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Draft) with { WhtCreditUsed = true } }, false, false, null, Today);
        Assert.Equal(DocumentCascadePurgePolicy.RuleWhtCreditUsed, wht.RuleCode);
    }

    [Fact]
    public void S1_ลายนิ้วมือแผน_คงที่เมื่อแผนเดิม_เปลี่ยนเมื่อสถานะใบเปลี่ยน()
    {
        var child = Child("DRAFT-5", DocumentType.Invoice, DocumentStatus.Draft);
        var a = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Draft), child }, true, false, null, Today);
        var b = DocumentCascadePurgePolicy.Decide(new[] { child, RootNode(DocumentStatus.Draft) }, true, false, null, Today);
        Assert.Equal(a.PlanToken, b.PlanToken);
        var approved = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Draft), child with { Status = DocumentStatus.Approved } },
            true, false, null, Today);
        Assert.NotEqual(a.PlanToken, approved.PlanToken);
    }

    [Fact]
    public void ทิศตรงข้าม_รากร่างไม่อยู่ในช่วงเก็บรักษา_ไม่ต้องforce()
    {
        var v = DocumentCascadePurgePolicy.Decide(new[] { RootNode(DocumentStatus.Draft) }, false, false, null, Today);
        Assert.True(v.Allowed);
        Assert.False(v.NeedsReason);
    }
}

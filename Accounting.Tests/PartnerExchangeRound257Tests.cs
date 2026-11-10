using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 257 ชุด 3 (erp-review/2026-11-10/team-D.md · D-H4 · D-H5 · D-M2 · D-M4 · LOW) — ตัวตัดสิน pure ของคู่ค้า NextAcc · ทุกด่านมีทิศตรงข้าม
/// </summary>
public class PartnerExchangeRound257Tests
{
    // ═══════════ D-H4: หลักฐานที่ข้ามการแปลงใบแจ้งหนี้ ⇒ ใบกำกับ ═══════════

    [Fact]
    public void R257_H4_ใบแจ้งหนี้ยืนยันไม่ได้_ปุ่มซ่อน_ทิศตรงข้ามใบกำกับยืนยันได้()
    {
        var fromInvoice = PartnerInputVatEvidence.Of(sourceIsTaxInvoice: false, signed: false, signedFileDelivered: false);
        Assert.Equal(PartnerInputVatEvidence.AwaitingTaxInvoice, fromInvoice);
        Assert.True(PartnerInputVatEvidence.BlocksClaim(fromInvoice));
        Assert.Equal(PartnerInputVatEvidence.AwaitingTaxInvoiceMessage, PartnerInputVatEvidence.ConfirmProblem(fromInvoice));
        Assert.False(PartnerInputVatEvidence.CanConfirmStampedEtax(fromInvoice));
        Assert.Contains("ใบแจ้งหนี้", PartnerInputVatEvidence.MissingLabel(fromInvoice));
        // ทิศตรงข้าม: ต้นทางเป็นใบกำกับ ⇒ ยืนยันได้ตามเดิม
        Assert.Null(PartnerInputVatEvidence.ConfirmProblem(PartnerInputVatEvidence.Of(true, false, false)));
        Assert.Null(PartnerInputVatEvidence.ConfirmProblem(PartnerInputVatEvidence.Of(true, true, true)));
        Assert.True(PartnerInputVatEvidence.CanConfirmStampedEtax(PartnerInputVatEvidence.Of(true, true, true)));
        Assert.False(PartnerInputVatEvidence.CanConfirmStampedEtax(PartnerInputVatEvidence.PaperRequired));
    }

    [Fact]
    public void R257_H4_ยืนยันบนใบแจ้งหนี้แล้ว_ใบกำกับมาถึง_ตั้งหลักฐานใหม่ด้วยOf()
    {
        foreach (var confirmed in new[] { PartnerInputVatEvidence.PaperConfirmed, PartnerInputVatEvidence.EtaxVerified })
        {
            var offered = PartnerInputVatEvidence.Of(true, false, false);
            var after = PartnerInputVatEvidence.OnNewSource(confirmed, offered, newSourceIsTaxInvoice: true, priorSourceWasTaxInvoice: false);
            Assert.Equal(PartnerInputVatEvidence.PaperRequired, after);          // ต้องยืนยันต้นฉบับใบกำกับใหม่
            Assert.True(PartnerInputVatEvidence.BlocksClaim(after));
            Assert.Null(PartnerInputVatEvidence.ConfirmProblem(after));          // ทางตันเดิมหาย: กดยืนยันได้ (รวมใบที่อนุมัติแล้ว)
        }
        Assert.Equal(PartnerInputVatEvidence.AwaitingTimestamp,
            PartnerInputVatEvidence.OnNewSource(PartnerInputVatEvidence.AwaitingTaxInvoice, PartnerInputVatEvidence.Of(true, true, true), true, false));
    }

    [Fact]
    public void R257_H4_ทิศตรงข้าม_ยืนยันใบกำกับไว้แล้ว_ใบที่ออกแทนมาถึง_คงค่า_ต้นทางยกเลิกคงเดิม()
    {
        Assert.Equal(PartnerInputVatEvidence.PaperConfirmed,
            PartnerInputVatEvidence.OnNewSource(PartnerInputVatEvidence.PaperConfirmed, PartnerInputVatEvidence.PaperRequired, true, priorSourceWasTaxInvoice: true));
        Assert.Equal(PartnerInputVatEvidence.SourceVoided,
            PartnerInputVatEvidence.OnNewSource(PartnerInputVatEvidence.SourceVoided, PartnerInputVatEvidence.AwaitingTimestamp, true, false));
        // ใบใหม่ที่ไม่ใช่ใบกำกับไม่ลดระดับค่าที่ดีกว่า
        Assert.Equal(PartnerInputVatEvidence.PaperRequired,
            PartnerInputVatEvidence.OnNewSource(PartnerInputVatEvidence.PaperRequired, PartnerInputVatEvidence.AwaitingTaxInvoice, false, true));
        Assert.Equal(PartnerInputVatEvidence.PaperRequired,
            PartnerInputVatEvidence.Merge(PartnerInputVatEvidence.AwaitingTaxInvoice, PartnerInputVatEvidence.PaperRequired));
        // แก้ยอดใบที่ยังรอใบกำกับ ⇒ ยังรอใบกำกับ (ไม่ใช่ "ต้องได้ต้นฉบับ")
        Assert.Equal(PartnerInputVatEvidence.AwaitingTaxInvoice, PartnerInputVatEvidence.AfterEdit(PartnerInputVatEvidence.AwaitingTaxInvoice, amountsChanged: true));
    }

    [Fact]
    public void R257_H4_ข้อความทางตัน_ยืนยันแล้ว_บอกทางไปต่อ()
    {
        Assert.Equal(PartnerInputVatEvidence.AlreadyConfirmedMessage, PartnerInputVatEvidence.ConfirmProblem(PartnerInputVatEvidence.PaperConfirmed));
        Assert.Contains("เติมเลขที่", PartnerInputVatEvidence.AlreadyConfirmedMessage);
        Assert.NotNull(PartnerInputVatEvidence.ConfirmProblem(null));
    }

    [Fact]
    public void R257_ใบที่ทำหน้าที่ใบกำกับ_ใบเสร็จต้องตรึงและมีVAT_ทิศตรงข้ามไม่รู้ไม่นับ()
    {
        Assert.True(PartnerExchangeRules.IsTaxInvoiceCarrier(DocumentType.TaxInvoice, null, 70m));
        Assert.True(PartnerExchangeRules.IsTaxInvoiceCarrier(DocumentType.TaxInvoice, true, 70m));
        Assert.False(PartnerExchangeRules.IsTaxInvoiceCarrier(DocumentType.TaxInvoice, false, 0m));
        Assert.True(PartnerExchangeRules.IsTaxInvoiceCarrier(DocumentType.Receipt, true, 70m));
        Assert.False(PartnerExchangeRules.IsTaxInvoiceCarrier(DocumentType.Receipt, null, 70m));   // ไม่รู้ = ไม่เดา
        Assert.False(PartnerExchangeRules.IsTaxInvoiceCarrier(DocumentType.Receipt, true, 0m));
        Assert.False(PartnerExchangeRules.IsTaxInvoiceCarrier(DocumentType.Invoice, true, 70m));
    }

    // ═══════════ D-H5: ระงับการเชื่อม ═══════════

    [Fact]
    public void R257_H5_ระงับได้เฉพาะคู่ที่เชื่อม_ทิศตรงข้ามสถานะอื่นปฏิเสธพร้อมทางไปต่อ()
    {
        Assert.Null(PartnerExchangeRules.SuspendProblem(TradingPartnershipStatus.Accepted));
        Assert.Contains("เชิญ", PartnerExchangeRules.SuspendProblem(TradingPartnershipStatus.Suspended));
        Assert.Contains("ปฏิเสธ", PartnerExchangeRules.SuspendProblem(TradingPartnershipStatus.Pending));
        Assert.NotNull(PartnerExchangeRules.SuspendProblem(TradingPartnershipStatus.Rejected));
        // ทางกลับ = เชิญใหม่ แล้วยอมรับจาก Pending (ตัวตัดสินเดิม)
        Assert.NotNull(PartnerExchangeRules.AcceptProblem(TradingPartnershipStatus.Suspended));
        Assert.Equal(PartnerExchangeRules.LinkState.Suspended, PartnerExchangeRules.StateOf(TradingPartnershipStatus.Suspended, true));
    }

    // ═══════════ D-M2: คัดลอกใบขายเป็นใบซื้อร่าง ═══════════

    [Fact]
    public void R257_M2_ส่วนลดท้ายบิล_และภาษีหัก_คัดลอกได้เมื่อยอดรวมเท่าหัวใบ()
    {
        // ใบ 2 บรรทัด ส่วนลดท้ายบิล 100 เฉลี่ยลงบรรทัดแล้ว (Σ บรรทัด 900 = ฐานหลังหักท้ายบิล) · VAT 63 · หัก 3% = 27 ⇒ ยอดรวม 936
        var lines = new[] { (Amount: 450m, Wht: 13.5m), (Amount: 450m, Wht: 13.5m) };
        Assert.Null(PartnerExchangeRules.CopyProblem(900m, 63m, 27m, 936m, 0m, 0m, 0m, 0m, lines));
        // ผลต่างปัดเศษ: Σ บรรทัด 4,912.16 · หัวใบ 4,912.15 (−0.01)
        Assert.Null(PartnerExchangeRules.CopyProblem(4_912.15m, 343.85m, 0m, 5_256.00m, -0.01m, 0m, 0m, 0m, new[] { (Amount: 4_912.16m, Wht: 0m) }));
    }

    [Fact]
    public void R257_M2_ทิศตรงข้าม_ยอดไม่รวมเท่าหัวใบ_มัดจำ_ออกภาษีแทน_หักหลังVAT_ไม่สร้าง()
    {
        var lines = new[] { (Amount: 1_000m, Wht: 0m) };
        Assert.Contains("ไม่เท่ายอดก่อนภาษี", PartnerExchangeRules.CopyProblem(900m, 63m, 0m, 963m, 0m, 0m, 0m, 0m, lines));   // ส่วนลดท้ายบิลไม่ได้เฉลี่ยลงบรรทัด
        Assert.Contains("ยอดรวม", PartnerExchangeRules.CopyProblem(1_000m, 70m, 30m, 1_070m, 0m, 0m, 0m, 0m, new[] { (Amount: 1_000m, Wht: 30m) }));
        Assert.Contains("ภาษีหัก", PartnerExchangeRules.CopyProblem(1_000m, 70m, 30m, 1_040m, 0m, 0m, 0m, 0m, lines));
        Assert.Contains("มัดจำ", PartnerExchangeRules.CopyProblem(1_000m, 70m, 0m, 1_070m, 0m, 500m, 0m, 0m, lines));
        Assert.Contains("ออกภาษี", PartnerExchangeRules.CopyProblem(1_000m, 70m, 0m, 1_070m, 0m, 0m, 30m, 0m, lines));
        Assert.Contains("หลังคิด VAT", PartnerExchangeRules.CopyProblem(1_000m, 70m, 0m, 1_070m, 0m, 0m, 0m, 98m, lines));
        Assert.NotNull(PartnerExchangeRules.CopyProblem(0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, Array.Empty<(decimal Amount, decimal Wht)>()));
    }

    // ═══════════ D-M4: ส่งออกนักบัญชี ═══════════

    [Fact]
    public void R257_M4_คอลัมน์อ้างอิง_บทบาทใบกำกับ_สาขาตามใบ()
    {
        var rows = PortalAccountantExport.Build(new[]
        {
            new PortalAccountantExport.Row(new DateTime(2026, 11, 1), "INV-0001", DocumentType.Invoice, 1_000m, 70m, 1_070m, 0m, false, SellerBranch: "00003"),
            new PortalAccountantExport.Row(new DateTime(2026, 11, 2), "RC-0001", DocumentType.Receipt, 1_000m, 70m, 1_070m, 0m, false,
                SellerBranch: "00003", Reference: PortalAccountantExport.ReferenceText("INV-0001", null), IsTaxInvoiceByLaw: true),
            new PortalAccountantExport.Row(new DateTime(2026, 11, 3), "TIV-0001", DocumentType.TaxInvoice, 1_000m, 70m, 1_070m, 0m, false),
        }, "บริษัท ผู้ขาย จำกัด", "0105556000007", "00000");
        Assert.Equal(PortalAccountantExport.Columns, rows[0].Keys.ToList());
        Assert.Equal(PortalAccountantExport.RoleNotTaxInvoice, rows[0]["ใบกำกับภาษี (ใช้ลงภาษีซื้อ)"]);   // ใบแจ้งหนี้ ห้ามลงภาษีซื้อ
        Assert.Equal(PortalAccountantExport.RoleTaxInvoice, rows[1]["ใบกำกับภาษี (ใช้ลงภาษีซื้อ)"]);
        Assert.Equal("ต้นทาง INV-0001", rows[1]["อ้างอิงเอกสาร"]);
        Assert.Equal("สาขาที่ 00003", rows[0]["สาขาผู้ขาย"]);    // สาขาที่ออกใบ ไม่ใช่สาขาของบริษัท
        Assert.Equal("สำนักงานใหญ่", rows[2]["สาขาผู้ขาย"]);     // ทิศตรงข้าม: ไม่ระบุ ⇒ ค่าตั้งต้น
        Assert.Equal("", rows[2]["อ้างอิงเอกสาร"]);
    }

    [Fact]
    public void R257_M4_บทบาทใบกำกับ_ไม่รู้ไม่เดา()
    {
        Assert.Equal(PortalAccountantExport.RoleUnknown, PortalAccountantExport.TaxInvoiceRole(DocumentType.Receipt, null, 70m));
        Assert.Equal(PortalAccountantExport.RoleNotTaxInvoice, PortalAccountantExport.TaxInvoiceRole(DocumentType.Receipt, false, 70m));
        Assert.Equal(PortalAccountantExport.RoleNotTaxInvoice, PortalAccountantExport.TaxInvoiceRole(DocumentType.TaxInvoice, null, 0m));
        Assert.Equal(PortalAccountantExport.RoleAdjustment, PortalAccountantExport.TaxInvoiceRole(DocumentType.CreditNote, null, 7m));
        Assert.Equal("", PortalAccountantExport.ReferenceText("DRAFT-abc", null));
        Assert.Equal("ต้นทาง INV-1 · PO 55", PortalAccountantExport.ReferenceText("INV-1", "PO 55"));
    }

    // ═══════════ LOW: e-Tax ตรวจแล้ว — กลุ่มส่งอีเมลพร้อมสำเนา ETDA ═══════════

    [Fact]
    public void R257_LOW_กลุ่มส่งอีเมลETDA_เลือกได้แค่ยกเลิกที่กรมฯ_ทิศตรงข้ามกลุ่มอื่นเลือกได้ทั้งสอง()
    {
        var emailed = EtaxReissueReview.GroupEmailedWithRdTimestamp;
        Assert.Equal(new[] { EtaxReissueReview.OutcomeCancelledAtRd }, EtaxReissueReview.AllowedOutcomes(emailed));
        Assert.NotNull(EtaxReissueReview.OutcomeForGroupProblem(emailed, EtaxReissueReview.OutcomeRejectedOrNotReceived));
        Assert.Null(EtaxReissueReview.OutcomeForGroupProblem(emailed, EtaxReissueReview.OutcomeCancelledAtRd));
        const string submitted = nameof(EtaxReissueReviewReport.SubmittedEtaxVoidedWithoutEvidence);
        Assert.Null(EtaxReissueReview.OutcomeForGroupProblem(submitted, EtaxReissueReview.OutcomeRejectedOrNotReceived));
        Assert.Null(EtaxReissueReview.OutcomeForGroupProblem(submitted, EtaxReissueReview.OutcomeCancelledAtRd));
        // ยกเลิกที่กรมฯ ยังต้องมีเลขอ้างอิง (ตัวตรวจเดิม)
        Assert.NotNull(EtaxReissueReview.ValidateAcknowledgement(EtaxReissueReview.OutcomeCancelledAtRd, null, "ยกเลิกแล้ว 1 พ.ย."));
    }

    // ═══════════ D-M1: คำตอบการเชิญบอกเงื่อนไข ═══════════

    [Fact]
    public void R257_M1_คำตอบเชิญบอกว่าปลายทางต้องเปิดค้นเจอก่อน_ไม่รั่วผล()
    {
        Assert.Contains("ให้คู่ค้าค้นเจอ", PartnerCompanyMatch.InviteAckMessage);
        Assert.Contains("ถ้าผู้รับใช้ NextAcc", PartnerCompanyMatch.InviteAckMessage);
        Assert.DoesNotContain("ไม่พบ", PartnerCompanyMatch.InviteAckMessage);
    }
}

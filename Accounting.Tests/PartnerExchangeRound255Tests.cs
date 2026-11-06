using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 255 WP-12B ข้อ 6–8 (AUDIT_249 §WP-12 · AUTOPILOT §6/§8 · คำตัดสินเจ้าของ Y5/N3) — ตัวตัดสิน pure ของคู่ค้า NextAcc · หลักฐานภาษีซื้อ · ขอบเขต API ·
/// ลิงก์วิเศษพอร์ทัล · ส่งออกนักบัญชี · ทุกด่านมีทิศตรงข้าม
/// </summary>
public class PartnerExchangeRound255Tests
{
    // ═══════════ ข้อ 6: เพดานคู่ (Y5) · สถานะการเชื่อม (ไม่รั่ว) · scope documents:read ═══════════

    [Fact]
    public void R255_เพดานคู่ค้า_ไม่ตั้ง_ไม่จำกัด_ตั้งแล้วถึงเพดาน_ปฏิเสธ()
    {
        Assert.Null(PartnerExchangeRules.CapProblem(1_000, null, "บริษัท"));   // Y5: ทุกแพ็กเกจไม่มีเพดาน
        Assert.Null(PartnerExchangeRules.CapProblem(1_000, -1, "บริษัท"));
        Assert.Null(PartnerExchangeRules.CapProblem(2, 3, "บริษัท"));
        Assert.NotNull(PartnerExchangeRules.CapProblem(3, 3, "บริษัท"));
        Assert.NotNull(PartnerExchangeRules.CapProblem(0, 0, "บริษัท"));
    }

    [Fact]
    public void R255_สถานะการเชื่อม_คำเชิญที่เราส่งเอง_ตอบเหมือนไม่มีแถว()
    {
        Assert.Equal(PartnerExchangeRules.LinkState.NotLinked, PartnerExchangeRules.StateOf(null, false));
        Assert.Equal(PartnerExchangeRules.LinkState.NotLinked, PartnerExchangeRules.StateOf(TradingPartnershipStatus.Pending, invitedByUs: true));
        Assert.Equal(PartnerExchangeRules.LinkState.NotLinked, PartnerExchangeRules.StateOf(TradingPartnershipStatus.Rejected, invitedByUs: true));
        Assert.Equal(PartnerExchangeRules.LinkState.NotLinked, PartnerExchangeRules.StateOf(TradingPartnershipStatus.Rejected, invitedByUs: false));
        // ทิศตรงข้าม: สถานะที่เรารู้อยู่แล้วบอกได้
        Assert.Equal(PartnerExchangeRules.LinkState.PendingOurs, PartnerExchangeRules.StateOf(TradingPartnershipStatus.Pending, invitedByUs: false));
        Assert.Equal(PartnerExchangeRules.LinkState.Linked, PartnerExchangeRules.StateOf(TradingPartnershipStatus.Accepted, true));
        Assert.Equal(PartnerExchangeRules.LinkState.Suspended, PartnerExchangeRules.StateOf(TradingPartnershipStatus.Suspended, true));
        // บอก "ใช้ NextAcc" เฉพาะปลายทางเปิดเอง และยังไม่เชื่อม
        Assert.False(PartnerExchangeRules.ShowDiscoverable(PartnerExchangeRules.LinkState.NotLinked, targetDiscoverable: false));
        Assert.True(PartnerExchangeRules.ShowDiscoverable(PartnerExchangeRules.LinkState.NotLinked, targetDiscoverable: true));
        Assert.False(PartnerExchangeRules.ShowDiscoverable(PartnerExchangeRules.LinkState.Linked, targetDiscoverable: true));
    }

    [Fact]
    public void R255_scope_documentsRead_แยก_write_ครอบread_ทิศตรงข้ามread_ไม่ครอบwrite()
    {
        Assert.True(ApiScopePolicy.Satisfies("documents:read", ApiScopePolicy.DocumentsRead));
        Assert.True(ApiScopePolicy.Satisfies("ocr:write documents:write", ApiScopePolicy.DocumentsRead));   // คีย์เดิม (สัญญา v1)
        Assert.True(ApiScopePolicy.Satisfies("documents:*", ApiScopePolicy.DocumentsRead));
        Assert.True(ApiScopePolicy.Satisfies("*", "documents:write"));
        Assert.False(ApiScopePolicy.Satisfies("documents:read", "documents:write"));
        Assert.False(ApiScopePolicy.Satisfies("contacts:write", ApiScopePolicy.DocumentsRead));
        Assert.False(ApiScopePolicy.Satisfies("", ApiScopePolicy.DocumentsRead));
        Assert.False(ApiScopePolicy.Satisfies("bank:write", "bank:write2"));
    }

    // ═══════════ ข้อ 7: ใบซื้อร่างจากคู่ค้า (N3) ═══════════

    [Fact]
    public void R255_ใบซื้อร่าง_ค่าตั้งต้นปิด_เพดานต่อคู่_ทิศตรงข้ามในเพดานสร้างได้()
    {
        Assert.NotNull(PartnerExchangeRules.DraftProblem(false, DocumentType.TaxInvoice, DocumentStatus.Approved, 1_070m, null, "THB"));
        Assert.Null(PartnerExchangeRules.DraftProblem(true, DocumentType.TaxInvoice, DocumentStatus.Approved, 1_070m, null, "THB"));
        Assert.Null(PartnerExchangeRules.DraftProblem(true, DocumentType.Invoice, DocumentStatus.Sent, 1_070m, 1_070m, "THB"));
        Assert.Contains("เกินเพดาน", PartnerExchangeRules.DraftProblem(true, DocumentType.TaxInvoice, DocumentStatus.Approved, 1_070.01m, 1_070m, "THB"));
        Assert.NotNull(PartnerExchangeRules.DraftProblem(true, DocumentType.Quotation, DocumentStatus.Approved, 1_070m, null, "THB"));
        Assert.NotNull(PartnerExchangeRules.DraftProblem(true, DocumentType.TaxInvoice, DocumentStatus.Voided, 1_070m, null, "THB"));
        Assert.NotNull(PartnerExchangeRules.DraftProblem(true, DocumentType.TaxInvoice, DocumentStatus.Approved, 1_070m, null, "USD"));
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.Equal((decimal?)500m, PartnerExchangeRules.ReceiverLimit(a, a, 500m, 900m));
        Assert.Equal((decimal?)900m, PartnerExchangeRules.ReceiverLimit(b, a, 500m, 900m));
    }

    [Fact]
    public void R255_หลักฐานภาษีซื้อ_N3_ต้องมีต้นฉบับถึงผู้รับ_ทิศตรงข้ามใบทั่วไปไม่ถูกกระทบ()
    {
        Assert.Equal(PartnerInputVatEvidence.EtaxVerified, PartnerInputVatEvidence.Of(true, signed: true, stamped: true, signedFileDelivered: true));
        Assert.Equal(PartnerInputVatEvidence.AwaitingTimestamp, PartnerInputVatEvidence.Of(true, true, false, true));
        // ไฟล์ที่ลงลายมือชื่อไม่ถึงผู้รับ = ผู้รับไม่มีต้นฉบับ ⇒ ต้องใช้กระดาษ (ไม่แต่ง EtaxVerified)
        Assert.Equal(PartnerInputVatEvidence.PaperRequired, PartnerInputVatEvidence.Of(true, true, true, signedFileDelivered: false));
        Assert.Equal(PartnerInputVatEvidence.PaperRequired, PartnerInputVatEvidence.Of(true, false, false, false));
        Assert.Equal(PartnerInputVatEvidence.PaperRequired, PartnerInputVatEvidence.Of(sourceIsTaxInvoice: false, true, true, true));
        Assert.True(PartnerInputVatEvidence.BlocksClaim(PartnerInputVatEvidence.PaperRequired));
        Assert.True(PartnerInputVatEvidence.BlocksClaim(PartnerInputVatEvidence.AwaitingTimestamp));
        Assert.False(PartnerInputVatEvidence.BlocksClaim(PartnerInputVatEvidence.EtaxVerified));
        Assert.False(PartnerInputVatEvidence.BlocksClaim(PartnerInputVatEvidence.PaperConfirmed));
        Assert.False(PartnerInputVatEvidence.BlocksClaim(null));   // ใบซื้อทั่วไป
        Assert.Null(PartnerInputVatEvidence.ConfirmProblem(PartnerInputVatEvidence.PaperRequired));
        Assert.NotNull(PartnerInputVatEvidence.ConfirmProblem(PartnerInputVatEvidence.PaperConfirmed));
        Assert.NotNull(PartnerInputVatEvidence.ConfirmProblem(null));
    }

    private static (Document Doc, Contact Supplier) CompletePurchase(string? evidence)
    {
        var supplier = new Contact { Name = "บริษัท ผู้ขาย จำกัด", TaxId = "0105556000009", Address = "1 ถนนสีลม กรุงเทพฯ", BranchCode = "00000" };
        var doc = new Document
        {
            DocumentType = DocumentType.PurchaseInvoice, SupplierInvoiceNumber = "TIV-001", SupplierTaxInvoiceDate = new DateTime(2026, 11, 1),
            SupplierBranchCode = "00000", HasTaxInvoiceReference = true, PartnerInputVatEvidence = evidence,
        };
        return (doc, supplier);
    }

    [Fact]
    public void R255_ตัวตรวจ86_4_ใบจากคู่ค้าที่ยังไม่มีต้นฉบับ_ไม่ครบ_ทิศตรงข้ามยืนยันแล้ว_ครบ()
    {
        var (blocked, s1) = CompletePurchase(PartnerInputVatEvidence.PaperRequired);
        var r1 = TaxInvoiceCompletenessChecker.Evaluate(blocked, s1);
        Assert.False(r1.IsClaimable);
        Assert.Contains(r1.MissingFields, f => f.Contains("ต้นฉบับใบกำกับ"));
        var (stampWait, s2) = CompletePurchase(PartnerInputVatEvidence.AwaitingTimestamp);
        Assert.False(TaxInvoiceCompletenessChecker.Evaluate(stampWait, s2).IsClaimable);
        // ทิศตรงข้าม: ยืนยันแล้ว/e-Tax ครบ/ใบซื้อทั่วไป (null) ⇒ ครบเหมือนเดิม
        foreach (var ok in new[] { PartnerInputVatEvidence.PaperConfirmed, PartnerInputVatEvidence.EtaxVerified, null })
        {
            var (d, s) = CompletePurchase(ok);
            Assert.True(TaxInvoiceCompletenessChecker.Evaluate(d, s).IsClaimable);
        }
    }

    // ═══════════ ข้อ 8: 50 ทวิ ผ่านคู่ค้า · ลิงก์วิเศษ · ส่งออกนักบัญชี ═══════════

    [Fact]
    public void R255_50ทวิผ่านคู่ค้า_เฉพาะใบออกแล้วหลังเปิด_ยังไม่ส่ง_ทิศตรงข้าม()
    {
        var since = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(PartnerExchangeRules.WhtCertDeliverable(1, since.AddDays(1), since, null, 0));
        Assert.True(PartnerExchangeRules.WhtCertDeliverable(3, since, since, null, 0));   // พิมพ์แล้วนับเป็นออกแล้ว
        Assert.False(PartnerExchangeRules.WhtCertDeliverable(0, since.AddDays(1), since, null, 0));   // ร่าง
        Assert.False(PartnerExchangeRules.WhtCertDeliverable(2, since.AddDays(1), since, null, 0));   // ยกเลิก
        Assert.False(PartnerExchangeRules.WhtCertDeliverable(1, since.AddDays(-1), since, null, 0)); // ก่อนเปิด (ไม่ส่งย้อนหลัง)
        Assert.False(PartnerExchangeRules.WhtCertDeliverable(1, since.AddDays(1), null, null, 0));   // ไม่ได้เปิด
        Assert.False(PartnerExchangeRules.WhtCertDeliverable(1, since.AddDays(1), since, since.AddDays(2), 0));   // ส่งแล้ว
        Assert.False(PartnerExchangeRules.WhtCertDeliverable(1, since.AddDays(1), since, null, PartnerExchangeRules.MaxWhtDeliveryAttempts));
        Assert.Equal(3m, PartnerExchangeRules.CertRate(new[] { 3m, 3m }, 10_000m, 300m));
        Assert.Equal(2.5m, PartnerExchangeRules.CertRate(new[] { 3m, 2m }, 10_000m, 250m));
    }

    [Fact]
    public void R255_ลิงก์วิเศษ_โทเคนสุ่ม_ใช้ครั้งเดียว_หมดอายุ_ข้อความเดียวทุกเหตุ()
    {
        var t = PortalMagicLinkPolicy.NewToken();
        Assert.True(PortalMagicLinkPolicy.LooksValid(t));
        Assert.NotEqual(t, PortalMagicLinkPolicy.NewToken());
        Assert.Equal(64, PortalMagicLinkPolicy.Hash(t).Length);
        Assert.False(PortalMagicLinkPolicy.LooksValid("short"));
        Assert.False(PortalMagicLinkPolicy.LooksValid(t[..42] + "!"));
        var now = new DateTime(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);
        Assert.Null(PortalMagicLinkPolicy.RedeemProblem(now.AddMinutes(1), null, true, now));
        Assert.Equal(PortalMagicLinkPolicy.InvalidMessage, PortalMagicLinkPolicy.RedeemProblem(now, null, true, now));          // หมดอายุ
        Assert.Equal(PortalMagicLinkPolicy.InvalidMessage, PortalMagicLinkPolicy.RedeemProblem(now.AddMinutes(1), now, true, now)); // ใช้แล้ว
        Assert.Equal(PortalMagicLinkPolicy.InvalidMessage, PortalMagicLinkPolicy.RedeemProblem(now.AddMinutes(1), null, false, now)); // ถูกปิดสิทธิ์
        Assert.Null(PortalMagicLinkPolicy.Link(null, Guid.NewGuid(), t));   // ไม่รู้ URL ของระบบ ⇒ ไม่แต่งโดเมน
        Assert.StartsWith("https://acc.example.co.th/portal.html?company=", PortalMagicLinkPolicy.Link("https://acc.example.co.th/", Guid.NewGuid(), t));
        Assert.Equal("ap@shop.co.th", PortalMagicLinkPolicy.NormalizeEmail("  AP@Shop.co.th "));
        Assert.Null(PortalMagicLinkPolicy.NormalizeEmail("not-an-email"));
    }

    [Fact]
    public void R255_ส่งออกนักบัญชี_คอลัมน์ทั่วไป_สาขา_ปัดเศษ_ยกเลิกยังอยู่()
    {
        var rows = PortalAccountantExport.Build(new[]
        {
            new PortalAccountantExport.Row(new DateTime(2026, 11, 2), "TIV-0001", DocumentType.TaxInvoice, 1_000.005m, 70m, 1_070.005m, 30m, false),
            new PortalAccountantExport.Row(new DateTime(2026, 11, 3), "CN-0001", DocumentType.CreditNote, 100m, 7m, 107m, 0m, true),
        }, "บริษัท ผู้ขาย จำกัด", "0105556000007", "00000");
        Assert.Equal(2, rows.Count);
        Assert.Equal(PortalAccountantExport.Columns, rows[0].Keys.ToList());
        Assert.Equal("2026-11-02", rows[0]["วันที่"]);
        Assert.Equal("สำนักงานใหญ่", rows[0]["สาขาผู้ขาย"]);
        Assert.Equal(1_000.01m, rows[0]["มูลค่าก่อนภาษี"]);   // AwayFromZero
        Assert.Equal("ยกเลิก", rows[1]["สถานะ"]);
        Assert.Equal("สาขาที่ 00012", PortalAccountantExport.Build(new[] { new PortalAccountantExport.Row(DateTime.UtcNow, "X", DocumentType.Invoice, 1m, 0m, 1m, 0m, false) },
            "S", "0105556000007", "00012")[0]["สาขาผู้ขาย"]);
    }
}

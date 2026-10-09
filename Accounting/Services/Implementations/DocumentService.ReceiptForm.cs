using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 203 — <b>รูปแบบกระดาษหลักฐานรับเงิน</b> ที่ด่านอนุมัติ (คำถามเจ้าของ 2026-10-09: ใบเสร็จค่าห้องพักที่พิมพ์ VAT แต่หัวเป็น
/// "ใบเสร็จรับเงิน") · แยกไฟล์ (partial) ตาม F4 ข้อ 7 — ตัวตัดสินทั้งหมดอยู่ที่ <see cref="ReceiptFormRule"/> (pure + เทสต์) ·
/// ไฟล์นี้แค่<b>รวบรวมข้อเท็จจริง</b>จากฐานข้อมูลด้วยตัวตัดสินที่มีอยู่แล้ว (สิทธิ์ §86/6 · นโยบายมัดจำ · ความครบ §86/4 · ใบต้นทาง)
/// </summary>
public partial class DocumentService
{
    /// <summary>
    /// ตัดสินรูปแบบกระดาษของใบที่กำลังอนุมัติ — <c>null</c> = ใบชนิดที่ตัวตัดสินไม่ครอบ (<see cref="ReceiptFormRule.AppliesTo"/>) ·
    /// ผู้เรียก 2 จุด: <c>CollectApprovalWarningsAsync</c> (คำเตือน) และจุดตรึง <c>doc.ReceiptForm</c> ก่อนออกเลข (ค่าที่ใช้) — ตัวเดียวกัน ไม่มีสำเนา
    ///
    /// <para>ข้อเท็จจริงทุกตัวมาจากเจ้าของกติกาเดิม ห้ามประกอบเอง: สิทธิ์อย่างย่อ = <see cref="AbbreviatedTaxInvoiceRule.CanIssue"/>
    /// ช่องทางเอกสาร (ตัวเดียวกับหัวกระดาษ) · มัดจำเป็นจุดความรับผิดไหม = <see cref="DepositPolicyResolver.OfDocument"/> (อ่านนโยบายที่
    /// ตรึงบนใบ — ตัวตัดสินนี้<b>ไม่</b>ตัดสินเรื่องมัดจำเอง ตามคำตัดสินเจ้าของ #34) · ผู้ซื้อต้องการเต็มรูป/ข้อมูลครบ =
    /// <c>TaxInvoiceCompletenessChecker</c> (ตัวเดียวกับด่าน §86/4 และหัวกระดาษ) · ค่าตั้งบริษัท = <see cref="ReceiptFormPolicy.Parse"/></para>
    ///
    /// <para>ผู้ติดต่อ: ใช้ที่โหลดมากับใบ · ไม่มี navigation ⇒ อ่านแยกแบบ IgnoreQueryFilters + กรอง CompanyId เอง (ผู้ติดต่อที่ถูกลบภายหลัง
    /// ยังเป็นผู้ซื้อของใบนี้ — แบบเดียวกับ <c>IssuedDocumentHooks</c>)</para>
    /// </summary>
    private async Task<ReceiptFormDecision?> DecideReceiptFormAsync(Guid companyId, Document doc)
    {
        // ใบกำกับภาษีเข้าตัวตัดสินเฉพาะที่เป็นหลักฐานรับเงินด้วย (ฝ่ายค้านรอบ 203 ข้อ 2): ขายเงินสด · รับเงินครบ ณ วันออก · ใบเช็คเอาต์ที่พัก
        // (รับเงินหน้าเคาน์เตอร์ — คำตัดสินข้อ 140 ข้อ 2 ให้แขกบุคคลได้ใบย่อ) · ServedAsReceipt ยังไม่ถูกคำนวณ ณ จุดอนุมัติ (resolver ตั้งตอน render) จึงเป็น false ที่นี่
        var channel = ReceiptFormRule.ChannelOf(doc.OriginModule);
        var taxInvoicePaysNow = doc.IssuedAsCashReceipt || doc.ServedAsReceipt || doc.PaidOnIssue
            || doc.PaymentType == PaymentType.Cash || channel == ReceiptFormChannel.Lodging;
        if (!ReceiptFormRule.AppliesTo(doc.DocumentType, doc.VatAmount, taxInvoicePaysNow)) return null;

        var issuer = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.IsVatRegistered, c.IsRetailApproved, c.PhoR06ApprovedDate })
            .FirstOrDefaultAsync();
        if (issuer == null) return null;   // ไม่พบบริษัท — ด่านอื่นของการอนุมัติล้มดังอยู่แล้ว
        var requirePhoR06 = await _db.SiteSettings.AsNoTracking()
            .Select(x => (bool?)x.RequirePhoR06ForAbbreviatedTaxInvoice)
            .FirstOrDefaultAsync() ?? true;
        var policyJson = await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => s.ReceiptFormPolicyJson)
            .FirstOrDefaultAsync();
        var buyer = doc.Contact;
        if (buyer == null && doc.ContactId != Guid.Empty)
            buyer = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == doc.ContactId && c.CompanyId == companyId);
        var settlesTaxInvoice = false;
        if (doc.RelatedDocumentId is Guid srcId)
            settlesTaxInvoice = await _db.Documents.AsNoTracking()
                .AnyAsync(s => s.Id == srcId && s.CompanyId == companyId && s.DocumentType == DocumentType.TaxInvoice);

        var mayAbbreviated = AbbreviatedTaxInvoiceRule.CanIssue(
            issuer.IsVatRegistered, issuer.IsRetailApproved, issuer.PhoR06ApprovedDate,
            doc.DocumentDate, requirePhoR06, AbbreviatedInvoiceChannel.Document);
        var facts = new ReceiptFormFacts(
            CompanyVatRegistered: issuer.IsVatRegistered,
            CompanyMayIssueAbbreviated: mayAbbreviated,
            HasVatLines: doc.VatAmount > 0.005m,
            DepositTreatment: DepositPolicyResolver.OfDocument(doc.IsDeposit, doc.VatAmount, doc.DepositOutputVatDeferred),
            BuyerNeedsFullForm: Tax.TaxInvoiceCompletenessChecker.IsJuristicBuyer(buyer),
            BuyerFullInfoComplete: buyer != null && !buyer.IsWalkInCustomer && !doc.BuyerDeclinedTaxInvoice
                && Tax.TaxInvoiceCompletenessChecker.MissingBuyerFields(buyer).Count == 0,
            SettlesExistingTaxInvoice: settlesTaxInvoice,
            IsRefund: false,
            Channel: channel,
            // ฝ่ายค้าน 1b: บุคคลธรรมดาที่แจ้งเลขภาษี 13 หลัก (ไม่ขึ้นต้น 0 ⇒ IsJuristicBuyer=false) ก็ต้องการเต็มรูป
            BuyerHasTaxId: buyer != null && Tax.TaxInvoiceCompletenessChecker.IsValidThaiTaxId(buyer.TaxId));
        var policy = ReceiptFormPolicy.Parse(policyJson);
        return ReceiptFormRule.Decide(facts, policy.PreferenceFor(ReceiptFormRule.CaseOf(facts)), doc.ReceiptForm);
    }
}

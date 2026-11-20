using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>รอบ 281 (คำตัดสิน 281-3) — ผู้โหลดข้อเท็จจริงของ "เส้นทางของใบนี้" (หน้า partners.html §partner-journey) ให้ทั้งสองฝั่ง
/// <para>อ่านอย่างเดียว · ตัวตัดสินสถานะ = <see cref="PartnerJourney.Build"/> ตัวเดียว · ทุก query ผูก CompanyId ของฝั่งที่อ่าน
/// (ผู้ขาย = <c>link.SourceCompanyId</c> · ผู้ซื้อ = <c>link.TargetCompanyId</c>) และ link ต้องมีบริษัทผู้เรียกเป็นฝั่งใดฝั่งหนึ่ง ·
/// ฝั่งขายเห็นเฉพาะผู้ที่เห็นเอกสารขาย · ฝั่งซื้อเฉพาะผู้ที่เห็นเอกสารซื้อ (ผู้เรียกส่งธงจาก <c>DocumentPermissionHelper.VisibleDirectionsAsync</c>)</para></summary>
public sealed class PartnerJourneyService
{
    private readonly AccountingDbContext _db;
    public PartnerJourneyService(AccountingDbContext db) => _db = db;

    public const int MaxRows = 50;

    /// <summary>หนึ่งใบในมุมมอง — <paramref name="Direction"/> = <c>Seller</c> (เราเป็นผู้ขาย) / <c>Buyer</c></summary>
    public sealed record JourneyView(Guid LinkId, string Direction, string PartnerName, Guid? OwnDocumentId, string? OwnDocumentNumber,
        decimal Total, string? CurrentStep, IReadOnlyList<PartnerJourney.Row> Steps);

    /// <summary>ใบล่าสุดของเส้นใบแจ้งหนี้ระหว่างคู่ค้า (ทั้งสองทิศตามสิทธิ์) · <paramref name="documentId"/> ≠ null ⇒ เฉพาะใบนั้น (ใบขายของเรา หรือใบซื้อของเรา)</summary>
    public async Task<List<JourneyView>> ListAsync(Guid companyId, bool seeSale, bool seePurchase, Guid? documentId = null, int limit = 20, CancellationToken ct = default)
    {
        if (!seeSale && !seePurchase) return new();
        limit = Math.Clamp(limit, 1, MaxRows);
        var q = _db.CrossTenantDocumentLinks.AsNoTracking()
            .Where(l => l.LinkType == CrossTenantLinkType.InvoiceFlow
                && ((seeSale && l.SourceCompanyId == companyId) || (seePurchase && l.TargetCompanyId == companyId)));
        if (documentId is Guid did)
            q = q.Where(l => (l.SourceCompanyId == companyId && l.SourceDocumentId == did) || (l.TargetCompanyId == companyId && l.TargetDocumentId == did));
        var links = await q.OrderByDescending(l => l.CreatedAt).Take(limit).ToListAsync(ct);
        var result = new List<JourneyView>();
        foreach (var l in links)
            if (await BuildAsync(companyId, l, ct) is { } v) result.Add(v);
        return result;
    }

    private async Task<JourneyView?> BuildAsync(Guid companyId, CrossTenantDocumentLink l, CancellationToken ct)
    {
        var viewerIsSeller = l.SourceCompanyId == companyId;
        var sellerId = l.SourceCompanyId;
        var buyerId = l.TargetCompanyId;

        var src = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.Id == l.SourceDocumentId && d.CompanyId == sellerId)
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.IsTaxInvoiceByLaw, d.VatAmount, d.ReceiptForm, d.TotalAmount, d.WithholdingTaxAmount, d.BalanceDue, d.Status, d.IsDeleted })
            .FirstOrDefaultAsync(ct);
        if (src == null) return null;

        var partnerName = await _db.Companies.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == (viewerIsSeller ? buyerId : sellerId)).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "-";

        // ── ฝั่งผู้ขาย: การรับเงิน + ใบเสร็จ/ใบกำกับ ณ วันรับเงิน + ใบนั้นเข้าระบบผู้ซื้อแล้วไหม ──
        var pays = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == sellerId && p.DocumentId == src.Id)
            .Select(p => new { p.Amount, p.WithholdingTaxAmount, p.FeeAmount, p.PaymentDate, p.CreatedAt })
            .ToListAsync(ct);
        var received = pays.Sum(p => p.Amount + p.WithholdingTaxAmount + p.FeeAmount);
        var receipt = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == sellerId && d.RelatedDocumentId == src.Id && d.IsSettlementReceipt
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new { d.Id, d.DocumentNumber, d.CreatedAt })
            .FirstOrDefaultAsync(ct);
        DateTime? receiptDelivered = receipt == null ? null : await _db.CrossTenantDocumentLinks.AsNoTracking()
            .Where(x => x.SourceCompanyId == sellerId && x.SourceDocumentId == receipt.Id && x.TargetCompanyId == buyerId
                && x.LinkType == CrossTenantLinkType.ReceiptFlow && x.TargetDocumentId != null)
            .Select(x => (DateTime?)(x.UpdatedAt ?? x.CreatedAt)).FirstOrDefaultAsync(ct);

        // ── ฝั่งผู้ซื้อ: ใบซื้อ · การจ่าย · การแจ้งที่ค้างพร้อมเหตุ · 50 ทวิ ──
        var purchase = l.TargetDocumentId is Guid pid
            ? await _db.Documents.AsNoTracking()
                .Where(d => d.Id == pid && d.CompanyId == buyerId)
                .Select(d => new { d.Id, d.DocumentNumber, d.CreatedAt, d.WithholdingTaxAmount, d.PartnerInputVatEvidence, d.Status })
                .FirstOrDefaultAsync(ct)
            : null;
        var buyerPay = purchase == null ? null : await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == buyerId && p.DocumentId == purchase.Id)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new { p.PaymentNumber, p.CreatedAt })
            .FirstOrDefaultAsync(ct);
        var blocked = purchase == null || buyerPay != null ? null : await _db.PartnerPaymentNotices.AsNoTracking()
            .Where(n => n.CompanyId == buyerId && n.TargetDocumentId == purchase.Id && n.Status == PartnerPaymentNoticeRule.StatusOpen && n.AutoBlockedReason != null)
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => n.AutoBlockedReason)
            .FirstOrDefaultAsync(ct);
        var cert = purchase == null ? null : await _db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == buyerId && w.DocumentId == purchase.Id
                && (w.Status == WithholdingTaxCertStatus.Issued || w.Status == WithholdingTaxCertStatus.Printed))
            .OrderByDescending(w => w.IssuedDate)
            .Select(w => new { w.CertificateNumber, w.IssuedDate, w.CreatedAt, w.TotalTaxAmount, w.PartnerDeliveredAt, w.PartnerDeliveryError, w.PartnerIntakeId })
            .FirstOrDefaultAsync(ct);
        var sendWht = await _db.WorkflowAutomationConfigs.AsNoTracking()
            .AnyAsync(c => c.CompanyId == buyerId && c.SendWhtCertsToPartners && c.SendWhtCertsToPartnersSince != null, ct);
        var intake = cert?.PartnerIntakeId is Guid iid
            ? await _db.WhtCertIntakes.AsNoTracking()
                .Where(i => i.Id == iid && i.CompanyId == sellerId)
                .Select(i => new { i.Status, i.ConfirmedAt })
                .FirstOrDefaultAsync(ct)
            : null;

        var facts = new PartnerJourney.Facts(
            ViewerIsSeller: viewerIsSeller,
            InvoiceSentAt: l.CreatedAt,
            SellerDocumentNumber: src.DocumentNumber,
            SourceIsTaxInvoice: PartnerExchangeRules.IsTaxInvoiceCarrier(src.DocumentType, src.IsTaxInvoiceByLaw, src.VatAmount, src.ReceiptForm),
            SourceVoided: src.IsDeleted || src.Status == DocumentStatus.Voided,
            PurchaseCreatedAt: purchase?.CreatedAt,
            PurchaseNumber: purchase?.DocumentNumber,
            PurchaseProblem: purchase == null ? l.Comment : null,
            // ยอดรวม VAT ก่อนหัก ณ ที่จ่าย (TotalAmount เก็บหลังหัก — ฐาน + VAT − WHT) ⇒ เทียบกับ "เงิน + ภาษีหัก" ที่ผู้ขายรับ
            Total: src.TotalAmount + src.WithholdingTaxAmount,
            SellerReceived: received,
            SellerSettled: pays.Count > 0 && src.BalanceDue <= 0.005m,
            LastReceivedAt: pays.Count == 0 ? null : pays.Max(p => p.CreatedAt),
            ReceiptIssuedAt: receipt?.CreatedAt,
            ReceiptNumber: receipt?.DocumentNumber,
            ReceiptDeliveredAt: receiptDelivered,
            InputVatEvidenceLabel: purchase?.PartnerInputVatEvidence == null ? null : PartnerInputVatEvidence.Label(purchase.PartnerInputVatEvidence),
            BuyerRecordedAt: buyerPay?.CreatedAt,
            BuyerPaymentNumber: buyerPay?.PaymentNumber,
            BuyerBlockedReason: blocked,
            PurchaseWht: purchase?.WithholdingTaxAmount ?? 0m,
            WhtCertIssuedAt: cert == null ? null : cert.IssuedDate ?? cert.CreatedAt,
            WhtCertNumber: cert?.CertificateNumber,
            WhtCertAmount: cert?.TotalTaxAmount ?? 0m,
            WhtDeliveredAt: cert?.PartnerDeliveredAt,
            WhtDeliveryError: cert?.PartnerDeliveryError,
            SendWhtEnabled: sendWht,
            WhtCreditedAt: intake?.Status == WhtCertIntakeRecordStatus.Confirmed ? intake.ConfirmedAt ?? cert?.PartnerDeliveredAt : null,
            WhtAwaitingConfirm: intake?.Status == WhtCertIntakeRecordStatus.AwaitingConfirm);
        var steps = PartnerJourney.Build(facts);
        return new JourneyView(l.Id, viewerIsSeller ? "Seller" : "Buyer", partnerName,
            viewerIsSeller ? src.Id : purchase?.Id,
            viewerIsSeller ? src.DocumentNumber : purchase?.DocumentNumber,
            src.TotalAmount + src.WithholdingTaxAmount, PartnerJourney.CurrentStep(steps), steps);
    }
}

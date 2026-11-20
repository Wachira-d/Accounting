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
        if (links.Count == 0) return new();
        return await BuildAllAsync(companyId, links, ct);
    }

    /// <summary>ฝ่ายค้าน 281 LOW-4: โหลดเป็นชุด (จำนวน query คงที่ต่อหน้า ไม่ใช่ต่อใบ) · ทุกแถวที่อ่านถูกจับคู่กลับด้วย (Id, CompanyId) ของฝั่งที่ link ระบุ
    /// ⇒ แถวของบริษัทอื่นที่บังเอิญอยู่ในชุดไม่ถูกใช้ (tenant isolation ต่อใบ ไม่ใช่ต่อชุด)</summary>
    private async Task<List<JourneyView>> BuildAllAsync(Guid companyId, List<CrossTenantDocumentLink> links, CancellationToken ct)
    {
        var sellerIds = links.Select(l => l.SourceCompanyId).Distinct().ToList();
        var buyerIds = links.Select(l => l.TargetCompanyId).Distinct().ToList();
        var srcIds = links.Select(l => l.SourceDocumentId).Distinct().ToList();
        var purchaseIds = links.Where(l => l.TargetDocumentId != null).Select(l => l.TargetDocumentId!.Value).Distinct().ToList();
        var companyIds = sellerIds.Concat(buyerIds).Distinct().ToList();

        var srcs = (await _db.Documents.AsNoTracking().IgnoreQueryFilters()
                .Where(d => srcIds.Contains(d.Id) && sellerIds.Contains(d.CompanyId))
                .Select(d => new { d.Id, d.CompanyId, d.DocumentNumber, d.DocumentType, d.IsTaxInvoiceByLaw, d.VatAmount, d.ReceiptForm, d.TotalAmount, d.WithholdingTaxAmount, d.BalanceDue, d.Status, d.IsDeleted })
                .ToListAsync(ct)).ToDictionary(d => (d.Id, d.CompanyId));
        var names = await _db.Companies.AsNoTracking().IgnoreQueryFilters()
            .Where(c => companyIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        // ── ฝั่งผู้ขาย: การรับเงิน + ใบเสร็จ/ใบกำกับ ณ วันรับเงิน + ใบนั้นเข้าระบบผู้ซื้อแล้วไหม ──
        var pays = (await _db.Payments.AsNoTracking()
                .Where(p => srcIds.Contains(p.DocumentId) && sellerIds.Contains(p.CompanyId))
                .Select(p => new { p.CompanyId, p.DocumentId, p.Amount, p.WithholdingTaxAmount, p.FeeAmount, p.CreatedAt })
                .ToListAsync(ct)).ToLookup(p => (p.DocumentId, p.CompanyId));
        var receipts = (await _db.Documents.AsNoTracking()
                .Where(d => d.RelatedDocumentId != null && srcIds.Contains(d.RelatedDocumentId.Value) && sellerIds.Contains(d.CompanyId) && d.IsSettlementReceipt
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
                .Select(d => new { d.Id, d.CompanyId, Related = d.RelatedDocumentId!.Value, d.DocumentNumber, d.CreatedAt })
                .ToListAsync(ct)).ToLookup(d => (d.Related, d.CompanyId));
        var receiptIds = receipts.SelectMany(g => g).Select(r => r.Id).ToList();
        var receiptLinks = receiptIds.Count == 0 ? new List<(Guid Src, Guid SrcCo, Guid TgtCo, DateTime At)>()
            : (await _db.CrossTenantDocumentLinks.AsNoTracking()
                .Where(x => receiptIds.Contains(x.SourceDocumentId) && sellerIds.Contains(x.SourceCompanyId) && x.LinkType == CrossTenantLinkType.ReceiptFlow && x.TargetDocumentId != null)
                .Select(x => new { x.SourceDocumentId, x.SourceCompanyId, x.TargetCompanyId, At = x.UpdatedAt ?? x.CreatedAt })
                .ToListAsync(ct)).Select(x => (Src: x.SourceDocumentId, SrcCo: x.SourceCompanyId, TgtCo: x.TargetCompanyId, x.At)).ToList();

        // ── ฝั่งผู้ซื้อ: ใบซื้อ · การจ่าย · การแจ้งที่ค้างพร้อมเหตุ · 50 ทวิ · ค่าตั้งส่ง 50 ทวิ ──
        var purchases = (await _db.Documents.AsNoTracking()
                .Where(d => purchaseIds.Contains(d.Id) && buyerIds.Contains(d.CompanyId))
                .Select(d => new { d.Id, d.CompanyId, d.DocumentNumber, d.CreatedAt, d.WithholdingTaxAmount, d.PartnerInputVatEvidence })
                .ToListAsync(ct)).ToDictionary(d => (d.Id, d.CompanyId));
        var buyerPays = (await _db.Payments.AsNoTracking()
                .Where(p => purchaseIds.Contains(p.DocumentId) && buyerIds.Contains(p.CompanyId))
                .Select(p => new { p.CompanyId, p.DocumentId, p.PaymentNumber, p.CreatedAt })
                .ToListAsync(ct)).ToLookup(p => (p.DocumentId, p.CompanyId));
        var notices = (await _db.PartnerPaymentNotices.AsNoTracking()
                .Where(n => purchaseIds.Contains(n.TargetDocumentId) && buyerIds.Contains(n.CompanyId) && n.Status == PartnerPaymentNoticeRule.StatusOpen && n.AutoBlockedReason != null)
                .Select(n => new { n.CompanyId, n.TargetDocumentId, n.AutoBlockedReason, n.CreatedAt })
                .ToListAsync(ct)).ToLookup(n => (n.TargetDocumentId, n.CompanyId));
        var certs = (await _db.WithholdingTaxCerts.AsNoTracking()
                .Where(w => w.DocumentId != null && purchaseIds.Contains(w.DocumentId.Value) && buyerIds.Contains(w.CompanyId)
                    && (w.Status == WithholdingTaxCertStatus.Issued || w.Status == WithholdingTaxCertStatus.Printed))
                .Select(w => new { w.CompanyId, DocumentId = w.DocumentId!.Value, w.CertificateNumber, w.IssuedDate, w.CreatedAt, w.TotalTaxAmount, w.PartnerDeliveredAt, w.PartnerDeliveryError, w.PartnerIntakeId })
                .ToListAsync(ct)).ToLookup(w => (w.DocumentId, w.CompanyId));
        var sendWht = (await _db.WorkflowAutomationConfigs.AsNoTracking()
                .Where(c => buyerIds.Contains(c.CompanyId) && c.SendWhtCertsToPartners && c.SendWhtCertsToPartnersSince != null)
                .Select(c => c.CompanyId).ToListAsync(ct)).ToHashSet();
        var intakeIds = certs.SelectMany(g => g).Where(c => c.PartnerIntakeId != null).Select(c => c.PartnerIntakeId!.Value).ToList();
        var intakes = intakeIds.Count == 0 ? new Dictionary<(Guid, Guid), (WhtCertIntakeRecordStatus Status, DateTime? ConfirmedAt)>()
            : (await _db.WhtCertIntakes.AsNoTracking()
                .Where(i => intakeIds.Contains(i.Id) && sellerIds.Contains(i.CompanyId))
                .Select(i => new { i.Id, i.CompanyId, i.Status, i.ConfirmedAt }).ToListAsync(ct))
                .ToDictionary(i => (i.Id, i.CompanyId), i => (i.Status, i.ConfirmedAt));

        var result = new List<JourneyView>();
        foreach (var l in links)
        {
            var viewerIsSeller = l.SourceCompanyId == companyId;
            var sellerId = l.SourceCompanyId;
            var buyerId = l.TargetCompanyId;
            if (!srcs.TryGetValue((l.SourceDocumentId, sellerId), out var src)) continue;
            var payRows = pays[(src.Id, sellerId)].ToList();
            var received = payRows.Sum(p => p.Amount + p.WithholdingTaxAmount + p.FeeAmount);
            var receipt = receipts[(src.Id, sellerId)].OrderByDescending(r => r.CreatedAt).FirstOrDefault();
            DateTime? receiptDelivered = receipt == null ? null
                : receiptLinks.Where(x => x.Src == receipt.Id && x.SrcCo == sellerId && x.TgtCo == buyerId).Select(x => (DateTime?)x.At).FirstOrDefault();
            var purchase = l.TargetDocumentId is Guid pid && purchases.TryGetValue((pid, buyerId), out var pu) ? pu : null;
            var buyerPay = purchase == null ? null : buyerPays[(purchase.Id, buyerId)].OrderBy(p => p.CreatedAt).FirstOrDefault();
            var blocked = purchase == null || buyerPay != null ? null
                : notices[(purchase.Id, buyerId)].OrderByDescending(n => n.CreatedAt).Select(n => n.AutoBlockedReason).FirstOrDefault();
            var cert = purchase == null ? null : certs[(purchase.Id, buyerId)].OrderByDescending(w => w.IssuedDate).FirstOrDefault();
            (WhtCertIntakeRecordStatus Status, DateTime? ConfirmedAt)? intake =
                cert?.PartnerIntakeId is Guid iid && intakes.TryGetValue((iid, sellerId), out var ik) ? ik : null;

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
                SellerSettled: payRows.Count > 0 && src.BalanceDue <= 0.005m,
                LastReceivedAt: payRows.Count == 0 ? null : payRows.Max(p => p.CreatedAt),
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
                SendWhtEnabled: sendWht.Contains(buyerId),
                WhtCreditedAt: intake is { Status: WhtCertIntakeRecordStatus.Confirmed } ok ? ok.ConfirmedAt ?? cert?.PartnerDeliveredAt : null,
                WhtAwaitingConfirm: intake is { Status: WhtCertIntakeRecordStatus.AwaitingConfirm });
            var steps = PartnerJourney.Build(facts);
            result.Add(new JourneyView(l.Id, viewerIsSeller ? "Seller" : "Buyer",
                names.TryGetValue(viewerIsSeller ? buyerId : sellerId, out var nm) ? nm : "-",
                viewerIsSeller ? src.Id : purchase?.Id,
                viewerIsSeller ? src.DocumentNumber : purchase?.DocumentNumber,
                src.TotalAmount + src.WithholdingTaxAmount, PartnerJourney.CurrentStep(steps), steps));
        }
        return result;
    }
}

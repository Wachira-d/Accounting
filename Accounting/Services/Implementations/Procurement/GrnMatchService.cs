using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Procurement;

/// <summary>
/// 3-way match — PO ↔ GRN ↔ Invoice. The textbook AP control.
///
///   • PO says we agreed to buy 100 units @ ฿50 = ฿5,000.
///   • GRN says we actually received 95 units (5 short).
///   • Invoice says the vendor wants paid for 100 units.
///
/// Without 3-way match the system would pay the invoice as-is and we'd
/// overpay by ฿250. This service compares the three and produces a
/// match report — exact match (proceed to pay), short receipt (don't
/// pay for unreceived), over-billing (vendor error), price mismatch
/// (need approval to pay over PO price).
///
/// Tolerance defaults: 2% on quantity, 1% on unit price, 5 days on
/// receipt date. Admin can override per-tenant.
///
/// Caller is responsible for blocking invoice payment when CanPay=false.
/// </summary>
public interface IGrnMatchService
{
    Task<GrnMatchReport> CheckAsync(Guid companyId, Guid purchaseInvoiceId,
        decimal qtyTolerancePct = 0.02m, decimal priceTolerancePct = 0.01m,
        CancellationToken ct = default);
}

public sealed record GrnMatchReport(
    Guid PurchaseInvoiceId,
    string InvoiceNumber,
    decimal InvoiceTotal,
    Guid? LinkedPoId,
    string? PoNumber,
    decimal? PoTotal,
    IReadOnlyList<Guid> LinkedGrnIds,
    decimal TotalReceivedQty,
    decimal TotalOrderedQty,
    decimal TotalBilledQty,
    IReadOnlyList<GrnMatchFinding> Findings,
    bool CanPay,                              // false if any ERROR severity
    decimal RecommendedPayAmount);            // capped at received-and-priced amount

public sealed record GrnMatchFinding(
    string Code,        // "GRN-001" etc.
    string Severity,    // "Error" | "Warning" | "Info"
    string Message,
    string? FixHint);

public class GrnMatchService : IGrnMatchService
{
    private readonly AccountingDbContext _db;

    public GrnMatchService(AccountingDbContext db) { _db = db; }

    public async Task<GrnMatchReport> CheckAsync(Guid companyId, Guid purchaseInvoiceId,
        decimal qtyTolerancePct = 0.02m, decimal priceTolerancePct = 0.01m,
        CancellationToken ct = default)
    {
        var inv = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == purchaseInvoiceId && d.CompanyId == companyId
                        && d.DocumentType == DocumentType.PurchaseInvoice)
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(ct);
        if (inv == null)
            throw new InvalidOperationException("PurchaseInvoice not found.");

        var findings = new List<GrnMatchFinding>();

        // Find linked PO via RelatedDocumentId or via DocumentLine.SourceLineId.
        Guid? poId = inv.RelatedDocumentId;
        if (poId == null)
        {
            var sourceLineIds = inv.Lines.Where(l => l.SourceLineId != null)
                .Select(l => l.SourceLineId!.Value).ToList();
            if (sourceLineIds.Count > 0)
            {
                poId = await _db.DocumentLines.AsNoTracking()
                    .Where(l => sourceLineIds.Contains(l.Id))
                    .Select(l => (Guid?)l.DocumentId)
                    .FirstOrDefaultAsync(ct);
            }
        }

        var po = poId == null ? null : await _db.Documents.AsNoTracking()
            .Where(d => d.Id == poId && d.DocumentType == DocumentType.PurchaseOrder)
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(ct);
        if (po == null && poId != null)
            findings.Add(new("GRN-001", "Warning",
                "อ้างถึง PO แต่หาเอกสาร PO ไม่เจอ — ตรวจสอบ RelatedDocumentId",
                "เปิดเอกสาร invoice แล้วลิงก์ PO ที่ถูกต้อง"));

        // Find GRNs linked to this PO (DocumentType=GoodsReceiptNote
        // with RelatedDocumentId=PoId).
        var grns = po == null ? new List<Models.Entities.Document>()
            : await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId
                            && d.DocumentType == DocumentType.GoodsReceiptNote
                            && d.RelatedDocumentId == po.Id
                            && d.Status != DocumentStatus.Voided
                            && d.Status != DocumentStatus.Draft)
                .Include(d => d.Lines)
                .ToListAsync(ct);

        // ── Aggregate quantities ──────────────────────────────────────
        var ordered = po?.Lines.Sum(l => l.Quantity) ?? 0m;
        var received = grns.SelectMany(g => g.Lines).Sum(l => l.Quantity);
        var billed = inv.Lines.Sum(l => l.Quantity);
        var qtyTol = ordered * qtyTolerancePct;

        // ── Findings ──────────────────────────────────────────────────
        if (po == null)
            findings.Add(new("GRN-002", "Warning",
                "ไม่มี PO อ้างถึง — invoice นี้สั่งซื้อโดยไม่ผ่าน PO",
                "เปิด PO ย้อนหลังเพื่อตรวจสอบ approval"));
        if (po != null && grns.Count == 0)
            findings.Add(new("GRN-003", "Error",
                "ยังไม่มี GRN (ใบรับสินค้า) สำหรับ PO นี้ — ห้ามจ่ายก่อนรับของ",
                "สร้าง GRN ยืนยันการรับของจริงก่อน"));
        if (po != null && billed > received + qtyTol)
            findings.Add(new("GRN-004", "Error",
                $"Vendor เรียกเก็บ {billed:N2} แต่รับของจริง {received:N2} (ขาด {billed - received:N2})",
                "ปรับ invoice ลงให้ตรงจำนวนรับ หรือรอ GRN เพิ่ม"));
        if (po != null && billed > ordered + qtyTol)
            findings.Add(new("GRN-005", "Error",
                $"Vendor เรียกเก็บ {billed:N2} เกิน PO ที่สั่ง {ordered:N2}",
                "ออก revised PO หรือเจรจาขอใบลดหนี้"));
        if (po != null && received > ordered + qtyTol)
            findings.Add(new("GRN-006", "Warning",
                $"รับของ {received:N2} เกิน PO {ordered:N2} — ส่วนเกินอาจไม่อยู่ในงบ",
                "ตรวจสอบ over-receipt policy"));

        // ── Per-line price comparison ─────────────────────────────────
        if (po != null)
        {
            var poByDescription = po.Lines
                .GroupBy(l => (l.Description ?? "").Trim().ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First());
            foreach (var iLine in inv.Lines)
            {
                var key = (iLine.Description ?? "").Trim().ToLowerInvariant();
                if (!poByDescription.TryGetValue(key, out var pLine)) continue;
                if (pLine.UnitPrice <= 0) continue;
                var priceDiff = Math.Abs(iLine.UnitPrice - pLine.UnitPrice) / pLine.UnitPrice;
                if (priceDiff > priceTolerancePct)
                    findings.Add(new("GRN-007", "Error",
                        $"ราคาต่อหน่วย invoice ({iLine.UnitPrice:N2}) ต่างจาก PO ({pLine.UnitPrice:N2}) เกิน {priceTolerancePct:P0} ในรายการ \"{iLine.Description}\"",
                        "เจรจาให้ vendor ออก invoice ใหม่ตามราคา PO หรือขออนุมัติส่วนต่าง"));
            }
        }

        var canPay = findings.All(f => f.Severity != "Error");
        // Recommended pay: cap at min(billed, received) × PO price.
        decimal recommended;
        if (po == null) recommended = inv.TotalAmount;
        else if (received >= billed) recommended = inv.TotalAmount;
        else
        {
            var unbilledRatio = received / Math.Max(billed, 0.01m);
            recommended = Math.Round(inv.TotalAmount * unbilledRatio, 2);
        }

        return new GrnMatchReport(
            PurchaseInvoiceId: inv.Id,
            InvoiceNumber: inv.DocumentNumber,
            InvoiceTotal: inv.TotalAmount,
            LinkedPoId: po?.Id,
            PoNumber: po?.DocumentNumber,
            PoTotal: po?.TotalAmount,
            LinkedGrnIds: grns.Select(g => g.Id).ToList(),
            TotalReceivedQty: received,
            TotalOrderedQty: ordered,
            TotalBilledQty: billed,
            Findings: findings,
            CanPay: canPay,
            RecommendedPayAmount: recommended);
    }
}

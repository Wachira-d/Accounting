using Accounting.Helpers;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 267 แพ็ก P5 — <b>ใบวางบิลจ่ายทั้งใบ</b> (คำตัดสินเจ้าของ 267-1-9 v2 · 267-1-72 · DOCUMENT_FLOW §2.12n-P5) · ตัวโหลดข้อเท็จจริงของใบในบิลตัวเดียว
/// (หน้าเอกสาร · ออกลิงก์ · ตัวจัดการเงินเข้า เรียกเมธอดนี้) — ตัดสินที่ <see cref="BillingNoteCombinedPay"/> · ไม่มีเส้นเงินใหม่ (ดูคำอธิบายของ helper)
/// </summary>
public partial class DocumentPayLinkService
{
    /// <summary>จำนวนใบในใบวางบิลสูงสุดที่จ่ายรวมได้ (เกิน = ชำระรายใบ — ไม่ล็อกแถวเป็นร้อยในการชำระเดียว)</summary>
    public const int BillingNoteMaxChildren = 50;

    public async Task<BillingNoteCombinedPay.Plan?> BillingNotePlanAsync(Guid companyId, Guid billingNoteId, CancellationToken ct = default)
    {
        var bn = await LoadDocAsync(companyId, billingNoteId, null, ct);
        if (bn == null || bn.Type != DocumentType.BillingNote) return null;
        var lineIds = await _db.DocumentLines.AsNoTracking()
            .Where(l => l.DocumentId == bn.Id && l.Document.CompanyId == companyId && l.SourceDocumentId != null)
            .OrderBy(l => l.LineOrder).Select(l => l.SourceDocumentId!.Value).ToListAsync(ct);
        var ids = lineIds.Distinct().ToList();
        if (ids.Count == 0)
            return new BillingNoteCombinedPay.Plan(0m, Array.Empty<(Guid, decimal)>(),
                "ใบวางบิลนี้ยังไม่ได้รวมใบแจ้งหนี้ — กรุณาติดต่อผู้ขาย");
        if (ids.Count > BillingNoteMaxChildren)
            return new BillingNoteCombinedPay.Plan(0m, Array.Empty<(Guid, decimal)>(),
                $"ใบวางบิลนี้มีมากกว่า {BillingNoteMaxChildren} ใบ" + BillingNoteCombinedPay.PayEachHint);
        var children = new List<BillingNoteCombinedPay.Child>();
        foreach (var id in ids)
        {
            var d = await LoadDocAsync(companyId, id, null, ct);
            if (d == null)
                return new BillingNoteCombinedPay.Plan(0m, Array.Empty<(Guid, decimal)>(), "ใบในใบวางบิลบางใบไม่พบแล้ว" + BillingNoteCombinedPay.PayEachHint);
            var refusal = Refusal(d);
            // ภาษีหัก ณ ที่จ่ายที่ผู้ซื้อต้องหัก — ตัวคิดเดียวกับลิงก์ชำระของใบนั้น (ใบที่จ่ายรายใบไม่ได้/ไม่ค้าง ไม่ต้องคิด)
            var wht = refusal == null && d.BalanceDue > 0.005m ? (await AmountsAsync(companyId, d, ct)).Amounts.Wht : 0m;
            children.Add(new BillingNoteCombinedPay.Child(d.Id, d.DocumentNumber, d.Type, d.Status, d.ContactId, d.Currency, d.BalanceDue,
                wht, refusal, d.SupersedingTaxInvoice != null));
        }
        return BillingNoteCombinedPay.Decide(bn.ContactId, children);
    }

    /// <summary>เหตุที่ "จ่ายทั้งใบวางบิล" ไม่ได้ ณ ตอนนี้ (null = จ่ายได้) — ใบค่าบริการผู้เช่า · ชุดใบไม่ผ่าน · ชำระครบทุกใบแล้ว</summary>
    private async Task<string?> BillingNoteRefusalAsync(Guid companyId, DocFacts d, CancellationToken ct)
    {
        if (d.PlatformTenantDocument) return DocumentPayLinkPolicy.PlatformTenantRefusal;
        if (d.Status == DocumentStatus.Voided) return "ใบวางบิลนี้ถูกยกเลิกแล้ว";
        var plan = await BillingNotePlanAsync(companyId, d.Id, ct);
        if (plan == null) return "ไม่พบใบวางบิล";
        return plan.Refusal ?? (plan.Total > 0.005m ? null : "ใบในใบวางบิลนี้ชำระครบแล้ว");
    }

    /// <summary>ด่านชั้นจ่ายของใบ — ใบวางบิล = ชุดใบ (<see cref="BillingNoteRefusalAsync"/>) · ชนิดอื่น = ด่านเดิม <see cref="Refusal"/> ทุกตัวอักษร</summary>
    private async Task<string?> PayRefusalAsync(Guid companyId, DocFacts d, CancellationToken ct)
        => d.Type == DocumentType.BillingNote ? await BillingNoteRefusalAsync(companyId, d, ct) : Refusal(d);
}

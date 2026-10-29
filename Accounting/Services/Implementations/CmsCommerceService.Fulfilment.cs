using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 209 P3 — หลังบ้านของออเดอร์หน้าเว็บ:
/// <list type="bullet">
///   <item>A5 คิวผลิต (<see cref="GetProductionQueueAsync"/>) — ออเดอร์ที่ยังไม่ส่งมอบ เรียงตามวันพร้อม (ไม่มีวันพร้อม = วันที่สั่ง) + ของค้างเลยกำหนด ·
///     วันที่ใช้เรียง = <c>EarliestReadyDate</c> (ฐานรอบนี้ยังไม่มี <c>RequestedReadyDate</c> — เมื่อแพ็ก P2 รวม วันที่ลูกค้าเลือกต้องมาก่อนในคีย์เรียงเดียวกันนี้)</item>
///   <item>E7 บันทึกรับเงินหน้าร้าน/ปลายทาง (<see cref="RecordCounterPaymentAsync"/>) — สร้างรายการรับเงิน (รอยืนยัน) แล้วเดิน <see cref="ConfirmPaymentAsync"/> เดิม
///     (ด่านรับเกินยอด · เอกสาร/JE/ใบมัดจำ/ตัดสต็อกตามโหมดของออเดอร์) — ห้ามประทับ "รับแล้ว"/ยอดรับเอง</item>
///   <item>E8 คืนเงินหลังขาย ⇒ ใบลดหนี้ร่าง (<see cref="PlanRefundCreditAsync"/> · <see cref="ProposeRefundCreditNoteAsync"/>) — ตัวตัดสิน <see cref="SiteOrderRefundPlan"/> · ไม่อนุมัติเอง</item>
/// </list>
/// </summary>
public partial class CmsCommerceService
{
    public const string CounterPaymentRuleCode = "CMS-ORDER-COUNTER-PAYMENT";
    public const string CounterPaymentReferencePrefix = "COUNTER-";
    public const string ProductionQueueRuleCode = "CMS-ORDER-QUEUE";

    /// <summary>ค่าคำนวณของแถวรายการออเดอร์ (วิธีรับ · ยอดค้าง) — ตัวคิดเดียวกับหน้ารายละเอียด (<see cref="MtoPaymentTerms.FromSnapshot"/>)</summary>
    private static void FillListDerived(List<OrderListResponse> items)
    {
        foreach (var o in items)
        {
            o.FulfillmentMethod ??= FulfillmentMethod.Delivery;
            o.FulfillmentLabel = FulfillmentMethod.Label(o.FulfillmentMethod);
            var due = MtoPaymentTerms.FromSnapshot(o.PaymentTermMode, o.TotalAmount, o.DepositRequiredAmount, o.PaidAmount);
            o.PaymentTermMode = due.Mode;
            o.BalanceDue = SiteOrderStatusTransition.IsClosed(o.Status) ? 0m : due.Balance;
        }
    }

    public async Task<PagedResponse<OrderListResponse>> GetProductionQueueAsync(Guid companyId, Guid siteId, DateOnly? from, DateOnly? to, int page, int pageSize)
    {
        page = CmsSiteScope.ClampPage(page);
        pageSize = CmsSiteScope.ClampPageSize(pageSize);
        var today = DateOnly.FromDateTime(BangkokNow());
        var f = from ?? today;
        var t = to ?? f.AddDays(7);
        if (t < f) throw new BusinessRuleException("ช่วงวันที่ของคิวผลิตไม่ถูกต้อง — วันสิ้นสุดต้องไม่ก่อนวันเริ่ม", ProductionQueueRuleCode);
        var fromUtc = DateTime.SpecifyKind(f.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var toExclusiveUtc = DateTime.SpecifyKind(t.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var todayUtc = DateTime.SpecifyKind(today.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var statuses = SiteOrderStatusTransition.ProductionQueueStatuses.ToList();

        // วันพร้อมอยู่ในช่วง หรือเลยกำหนดแล้วยังไม่ส่งมอบ (ครัวต้องเห็นของค้างเสมอ) · ไม่มีวันพร้อม (สินค้าไม่ตามสั่ง) = วันที่สั่ง
        var query = _db.SiteOrders.AsNoTracking()
            .Where(o => o.SiteId == siteId && o.CompanyId == companyId && statuses.Contains(o.Status))
            .Where(o => (o.EarliestReadyDate ?? o.CreatedAt) < toExclusiveUtc
                && ((o.EarliestReadyDate ?? o.CreatedAt) >= fromUtc || (o.EarliestReadyDate ?? o.CreatedAt) < todayUtc));

        var total = await query.CountAsync();
        var rows = await query
            .OrderBy(o => o.EarliestReadyDate ?? o.CreatedAt).ThenBy(o => o.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(o => new
            {
                Row = new OrderListResponse
                {
                    Id = o.Id, SiteId = o.SiteId, OrderNumber = o.OrderNumber, Status = o.Status,
                    CustomerName = o.Customer != null ? o.Customer.FullName : o.ShippingName,
                    TotalAmount = o.TotalAmount, Currency = o.Currency,
                    ItemCount = o.Lines.Count, PaidAt = o.PaidAt, CreatedAt = o.CreatedAt,
                    EarliestReadyDate = o.EarliestReadyDate.HasValue ? o.EarliestReadyDate.Value.ToString("yyyy-MM-dd") : null,
                    FulfillmentMethod = o.FulfillmentMethod, PaidAmount = o.PaidAmount, PaymentTermMode = o.PaymentTermMode,
                    DepositRequiredAmount = o.DepositRequiredAmount, TrackingNumber = o.TrackingNumber,
                },
                Ready = o.EarliestReadyDate,
                Lines = o.Lines.OrderBy(l => l.LineOrder).Select(l => new { l.ProductName, l.Quantity, l.ModifierSelectionsJson }).ToList(),
            })
            .ToListAsync();

        var items = new List<OrderListResponse>();
        foreach (var r in rows)
        {
            r.Row.LineSummary = string.Join(" · ", r.Lines.Select(l =>
            {
                var opt = SummaryOf(l.ModifierSelectionsJson);
                return $"{l.ProductName} ×{l.Quantity:0.##}" + (string.IsNullOrWhiteSpace(opt) ? "" : $" ({opt})");
            }));
            r.Row.IsOverdue = r.Ready is DateTime ready && ready < todayUtc;
            items.Add(r.Row);
        }
        FillListDerived(items);
        foreach (var o in items)
        {
            var due = MtoPaymentTerms.FromSnapshot(o.PaymentTermMode, o.TotalAmount, o.DepositRequiredAmount, o.PaidAmount);
            o.NextActions = SiteOrderStatusTransition.NextActions(o.Status, o.FulfillmentMethod, due).ToList();
        }
        return new PagedResponse<OrderListResponse>(items, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize));
    }

    // ───────────── E7 บันทึกรับเงินหน้าร้าน/ปลายทาง ─────────────

    /// <summary>คีย์ล็อกเดียวกับเส้นสถานะ/ยืนยันรับเงิน ⇒ ไม่สวนกับยกเลิก/ยืนยันสลิป (ConfirmPaymentAsync ซ้อนในล็อกเดียวกันได้)</summary>
    public Task<OrderResponse> RecordCounterPaymentAsync(Guid companyId, Guid siteId, Guid orderId, RecordCounterPaymentRequest request, string userId)
    {
        return AdvisorySessionLock.RunAsync(_db, AdvisoryLockKey.For(companyId, AdvisoryLockKey.CmsOrderStatus, orderId.ToString("N")),
            () => RecordCounterPaymentLockedAsync(companyId, siteId, orderId, request, userId),
            "มีการเปลี่ยนสถานะออเดอร์นี้กำลังทำงานอยู่ — รอสักครู่แล้วลองใหม่", SiteOrderStatusTransition.RuleCode);
    }

    private async Task<OrderResponse> RecordCounterPaymentLockedAsync(Guid companyId, Guid siteId, Guid orderId, RecordCounterPaymentRequest request, string userId)
    {
        var order = await _db.SiteOrders.Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.SiteId == siteId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Order not found.");
        var due = MtoPaymentTerms.FromSnapshot(order.PaymentTermMode, order.TotalAmount, order.DepositRequiredAmount, order.PaidAmount);
        if (!SiteOrderStatusTransition.AcceptsCounterPayment(order.Status, due.Balance))
            throw new BusinessRuleException(due.Balance <= 0.005m
                ? $"ออเดอร์ {order.OrderNumber} ไม่มียอดค้างชำระแล้ว — ไม่ต้องบันทึกรับเงินเพิ่ม"
                : $"ออเดอร์ {order.OrderNumber} ปิดแล้ว/อยู่ระหว่างคืนเงิน — บันทึกรับเงินไม่ได้ · ถ้าเงินเข้าจริงให้คืนลูกค้าหรือเปิดออเดอร์ใหม่",
                CounterPaymentRuleCode, 409);
        var amount = Math.Round(request.Amount ?? due.Balance, 2, MidpointRounding.AwayFromZero);
        if (amount <= 0m)
            throw new BusinessRuleException("ยอดที่รับต้องมากกว่า 0", CounterPaymentRuleCode);
        if (amount > due.Balance + 0.005m)
            throw new BusinessRuleException($"ยอดที่รับ {amount:N2} บาท เกินยอดค้าง {due.Balance:N2} บาท — ถ้าลูกค้าจ่ายเกิน บันทึกเท่ายอดค้างแล้วทอน/คืนส่วนเกิน", CounterPaymentRuleCode);
        var method = request.PaymentMethod ?? PaymentMethod.Cash;
        var reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        if (reference is { Length: > 80 }) reference = reference[..80];

        // รายการรับเงิน "รอยืนยัน" — ConfirmPaymentAsync เป็นผู้ประทับรับแล้ว + PaidAmount + ด่านรับเกินยอด (เส้นเดียวกับสลิป/webhook)
        var pay = new SiteOrderPayment
        {
            CompanyId = companyId, OrderId = order.Id, Amount = amount, Currency = order.Currency,
            PaymentMethod = method, Status = SitePaymentStatus.Pending,
            Reference = CounterPaymentReferencePrefix + BangkokNow().ToString("yyyyMMddHHmm") + (reference == null ? "" : "-" + reference),
            CreatedBy = userId,
        };
        _db.SiteOrderPayments.Add(pay);
        order.InternalNotes = DepositPolicyResolver.AppendNoteOnce(order.InternalNotes,
            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm}Z] พนักงานบันทึกรับเงินหน้าร้าน/ปลายทาง {amount:N2} บาท ({method})");
        await _db.SaveChangesAsync();

        if (!await ConfirmPaymentAsync(companyId, siteId, orderId, pay.Id, userId, request.MoneyInAccountId))
            throw new KeyNotFoundException("Order not found.");
        return await GetOrderAsync(companyId, siteId, orderId) ?? throw new InvalidOperationException("Failed.");
    }

    // ───────────── E8 คืนเงิน ⇒ ใบลดหนี้ร่าง ─────────────

    /// <summary>แผนใบลดหนี้ของการคืนเงิน — Doc null + SkipWhy = ไม่ร่าง (บอกเหตุบนออเดอร์)</summary>
    private sealed record RefundDraft(Document? Doc, RefundCreditPlan? Plan, bool Partial, string? Reason, string? SkipWhy);

    private static List<RefundSourceLine> RefundSourceLinesOf(Document doc)
        => (doc.Lines ?? new List<DocumentLine>()).Where(l => !l.IsDeleted).OrderBy(l => l.LineOrder)
            .Select(l => new RefundSourceLine(l.Description, l.Quantity, l.Unit, l.UnitPrice, l.DiscountPercent, l.DiscountAmount,
                l.VatRate, l.AccountId, l.ProductCode))
            .ToList();

    /// <summary>บรรทัดค่าจัดส่งที่ใบขายของออเดอร์มี — ตัวแบ่งเดียวกับตอนออกใบ (<see cref="ShippingVatAllocation.Split"/> · สถานะ VAT ของบริษัท)</summary>
    private async Task<IReadOnlyList<(string Description, decimal Fee)>> ShippingPartsOfOrderAsync(Guid companyId, SiteOrder order)
    {
        if (order.ShippingAmount <= 0m) return Array.Empty<(string, decimal)>();
        var vatRegistered = await CompanyVatStatus.IsRegisteredAsync(_db, companyId);
        var parts = ShippingVatAllocation.Split(order.ShippingVatAllocation, vatRegistered, order.ShippingAmount,
            order.Lines.Select(x => (Math.Round(x.Quantity * x.UnitPrice, 2, MidpointRounding.AwayFromZero), x.VatRate)).ToList());
        return SiteOrderRefundPlan.ShippingPartsOf(parts);
    }

    /// <summary>ใบลดหนี้ที่ออก/ร่างไว้แล้วอ้างใบนี้ (ไม่นับที่ยกเลิก/ปฏิเสธ) — ตัวเดียวของเส้นคืนเงินและเส้นยกเลิก (Σ ใบลดหนี้ ≤ ใบเดิม §86/10)</summary>
    private async Task<decimal> AlreadyCreditedAsync(Guid companyId, Guid docId)
    {
        return await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.RelatedDocumentId == docId && d.DocumentType == DocumentType.CreditNote
                && !d.IsDeleted && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
            .SumAsync(d => (decimal?)d.TotalAmount) ?? 0m;
    }

    /// <summary>ใบลดหนี้ของการยกเลิกหลังชำระ (R-07) — เต็มใบ ยกเว้นค่าจัดส่งเมื่อจัดส่งแล้ว (ข้อ 168 ฉ) ·
    /// รอบ 209 ฝ่ายค้าน (เงิน) ข้อ 1–2: พาฐานมัดจำที่ใบหักไว้ + หักใบลดหนี้ที่ออกไปแล้ว (เดิม alreadyCredited = 0 ⇒ คืนบางส่วนแล้วยกเลิก ได้ใบลดหนี้เต็มซ้ำ)</summary>
    private async Task<RefundCreditPlan> CancelCreditPlanAsync(Guid companyId, SiteOrder order, Document doc)
    {
        var shippingParts = await ShippingPartsOfOrderAsync(companyId, order);
        var alreadyCredited = await AlreadyCreditedAsync(companyId, doc.Id);
        return SiteOrderRefundPlan.Plan(RefundSourceLinesOf(doc), doc.TotalAmount, doc.BillDiscountAmount,
            shippingParts, SiteOrderRefundPlan.ExcludesShipping(order.ShippedAt, order.DeliveredAt),
            alreadyCredited, paid: doc.TotalAmount, requestedAmount: null, fullRefund: true,
            depositBaseDeducted: doc.DepositBaseDeducted);
    }

    private static List<Models.DTOs.Document.DocumentLineRequest> ToCreditDocumentLines(RefundCreditPlan plan, string prefix)
        => plan.Lines.Select(l => new Models.DTOs.Document.DocumentLineRequest(
                Description: prefix + l.Description, Quantity: l.Quantity, Unit: l.Unit, UnitPrice: l.UnitPrice,
                DiscountPercent: l.DiscountPercent, VatRate: l.VatRate, WithholdingTaxRate: 0m,
                AccountId: l.AccountId, ProductCode: l.ProductCode, DiscountAmount: l.DiscountAmount))
            .ToList();

    /// <summary>วางแผนก่อนเปลี่ยนสถานะ — ยอดเกิน/ไม่ระบุยอดคืนบางส่วน ⇒ โยน (สถานะไม่เปลี่ยน) · ไม่มีเอกสารที่ลดหนี้ได้ ⇒ แผน "ไม่ร่าง" พร้อมเหตุ</summary>
    private async Task<RefundDraft> PlanRefundCreditAsync(Guid companyId, SiteOrder order, UpdateOrderStatusRequest request)
    {
        var partial = request.Status == SiteOrderStatus.PartiallyRefunded;
        var reason = string.IsNullOrWhiteSpace(request.RefundReason) ? request.CancellationReason : request.RefundReason;
        if (partial && (request.RefundAmount is not decimal want || want <= 0m))
            throw new BusinessRuleException("คืนเงินบางส่วน — กรุณาระบุยอดที่คืน (มากกว่า 0)", SiteOrderRefundPlan.RuleCode);
        if (request.RefundAmount is decimal req && req > order.PaidAmount + 0.005m)
            throw new BusinessRuleException($"ยอดคืน {req:N2} บาท เกินเงินที่รับแล้ว {order.PaidAmount:N2} บาท", SiteOrderRefundPlan.RuleCode);
        if (order.ErpDocumentId is not Guid erpDocId || _docService == null)
            return new RefundDraft(null, null, partial, reason, "ออเดอร์ยังไม่มีใบขายในระบบบัญชี (หรือมีแต่ใบรับเงินมัดจำ) — คืนเงินลูกค้าด้วยมือ · มัดจำคืน/ริบที่หน้าใบมัดจำ");

        var doc = await _db.Documents.AsNoTracking().Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == erpDocId && d.CompanyId == companyId);
        var hasPayments = doc != null && await _db.Payments.AsNoTracking()
            .AnyAsync(p => p.CompanyId == companyId && p.DocumentId == erpDocId && !p.IsDeleted);
        var action = SiteOrderCancelPlan.Decide(doc != null, doc?.Status, hasPayments);
        if (action != SiteOrderCancelErpAction.ProposeCreditNote || doc == null)
            return new RefundDraft(null, null, partial, reason, action switch
            {
                SiteOrderCancelErpAction.NothingToDo => "ใบขายของออเดอร์ถูกยกเลิก/ไม่มีผลแล้ว — ไม่มีภาษีขายให้ลด",
                _ => $"ใบขาย {doc?.DocumentNumber} ยังไม่อนุมัติหรือยังไม่มีการรับชำระผูก — แก้/ยกเลิกใบเดิมที่หน้าเอกสาร แทนใบลดหนี้",
            });

        var alreadyCredited = await AlreadyCreditedAsync(companyId, doc.Id);
        var plan = SiteOrderRefundPlan.Plan(RefundSourceLinesOf(doc), doc.TotalAmount, doc.BillDiscountAmount,
            await ShippingPartsOfOrderAsync(companyId, order), SiteOrderRefundPlan.ExcludesShipping(order.ShippedAt, order.DeliveredAt),
            alreadyCredited, order.PaidAmount, request.RefundAmount, fullRefund: !partial,
            depositBaseDeducted: doc.DepositBaseDeducted);
        if (plan.Problem != null)
            throw new BusinessRuleException(plan.Problem, SiteOrderRefundPlan.RuleCode);
        return new RefundDraft(doc, plan, partial, reason, null);
    }

    /// <summary>ร่างใบลดหนี้ของการคืนเงิน (<b>ไม่อนุมัติ</b> — R1) · ล้ม ⇒ ล้าง context (กันครึ่งทางของเส้นเอกสาร) · บันทึกสถานะ + [ERP-REFUND-CN-FAILED] · โยน</summary>
    private async Task<SiteOrder> ProposeRefundCreditNoteAsync(Guid companyId, Guid siteId, SiteOrder order, RefundDraft draft, string userId)
    {
        var now = DateTime.UtcNow;
        if (draft.Doc == null || draft.Plan == null)
        {
            order.InternalNotes = DepositPolicyResolver.AppendNoteOnce(order.InternalNotes, SiteOrderRefundPlan.SkippedNote(now, draft.SkipWhy ?? "-"));
            return order;
        }
        var doc = draft.Doc;
        var plan = draft.Plan;
        try
        {
            var create = new Models.DTOs.Document.CreateDocumentRequest(
                DocumentType: DocumentType.CreditNote, DocumentDate: now, DueDate: null,
                ContactId: doc.ContactId, Reference: doc.DocumentNumber,
                Notes: SiteOrderRefundPlan.DraftNotes(order.OrderNumber, doc.DocumentNumber, doc.DocumentDate, plan.Amount, draft.Partial,
                    plan.ShippingExcluded > 0m, draft.Reason),
                Lines: ToCreditDocumentLines(plan, $"คืนเงิน ({order.OrderNumber}): "),
                RelatedDocumentId: doc.Id,
                CnDnPurchaseSideOverride: false,
                BillDiscountAmount: plan.BillDiscount > 0m ? plan.BillDiscount : null,
                CreditNoteReason: SiteOrderRefundPlan.DraftReason,
                PricesIncludeVat: doc.PricesIncludeVat,
                BranchId: doc.BranchId,
                Currency: doc.Currency, ExchangeRate: doc.ExchangeRate);
            var cn = await _docService!.CreateDocumentAsync(companyId, create, userId, originModule: SiteOrderDepositInvoicing.OriginModule);
            order.InternalNotes = DepositPolicyResolver.AppendNoteOnce(order.InternalNotes,
                SiteOrderRefundPlan.ProposedNote(now, cn.DocumentNumber, doc.DocumentNumber, cn.TotalAmount, plan.ShippingExcluded > 0m)
                + SiteOrderRefundPlan.DepositPortionNote(doc.DepositBaseDeducted, doc.DepositAppliedRef));
            _logger.LogInformation("คืนเงินออเดอร์ {Order} — ร่างใบลดหนี้ {Cn} อ้างใบ {Doc} ยอด {Amount}", order.OrderNumber, cn.DocumentNumber, doc.DocumentNumber, cn.TotalAmount);
            return order;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "คืนเงินออเดอร์ {Order} — ร่างใบลดหนี้อ้างใบ {Doc} ไม่สำเร็จ", order.OrderNumber, doc.DocumentNumber);
            _db.ChangeTracker.Clear();
            var reloaded = await _db.SiteOrders.Include(o => o.Lines).Include(o => o.Payments)
                .FirstAsync(o => o.Id == order.Id && o.SiteId == siteId && o.CompanyId == companyId);
            var why = ex is BusinessRuleException or InvalidOperationException ? ex.Message : "เกิดข้อผิดพลาดภายใน (ดูบันทึกระบบ)";
            reloaded.Status = order.Status;
            reloaded.InternalNotes = DepositPolicyResolver.AppendNoteOnce(order.InternalNotes, SiteOrderRefundPlan.FailedNote(now, doc.DocumentNumber, why));
            reloaded.UpdatedBy = userId;
            await _db.SaveChangesAsync();
            throw new BusinessRuleException(
                $"ตั้งสถานะคืนเงินแล้ว แต่ร่างใบลดหนี้อ้างใบ {doc.DocumentNumber} ไม่สำเร็จ: {why} — ออกใบลดหนี้เองที่หน้าเอกสาร",
                SiteOrderRefundPlan.RuleCode);
        }
    }
}

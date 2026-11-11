using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 258-3 "เส้นบิลอัตโนมัติจนจบ" (F-H2 · F-M2) — ขั้นหลังออกใบเสร็จ settlement (ใบเสร็จ/ใบกำกับภาษี ณ วันรับเงิน §78/1) ที่เกิดเป็น Paid ตรง ไม่ผ่าน
/// <c>ApproveDocumentAsync</c> · ไฟล์นี้แยกออกจาก DocumentService.cs ตาม F4.7 (ผู้เรียกในไฟล์หลักเป็นบรรทัดเรียกบรรทัดเดียว)
/// </summary>
public partial class DocumentService
{
    /// <summary>
    /// เรียกหลัง commit + <c>_issuedHooks.RunAsync</c> ทุกเส้นที่ออกใบเสร็จ settlement (รับชำระ · ออกย้อนหลัง/งานหลังรับชำระ · ออกใหม่แทน) —
    /// (1) ล้างป้าย <see cref="SettlementReceiptFailedNote"/> บนใบต้นทาง (ออกได้แล้ว) · (2) ใบที่อนุมัติแล้ว ⇒ ส่งเข้าระบบคู่ค้าด้วยทางเข้าเดียว
    /// <c>CrossTenantWorkflowService.RouteSentDocumentAsync</c> (เหมือนเอกสารที่อนุมัติแล้ว · ผู้รับ: ใบเสร็จที่ถือ VAT ⇒ แนบหลักฐานใบกำกับเข้าใบซื้อเดิม ·
    /// ใบรับเปล่า ⇒ แถวกล่องเข้าเท่านั้น ไม่สร้างใบซื้อใหม่) · ล้ม ⇒ ป้าย [PARTNER-ROUTE-FAILED] บนใบเสร็จ (ตัวเส้นเอง) · ไม่โยน (เงิน/ใบเสร็จ commit แล้ว)
    /// </summary>
    private async Task AfterSettlementReceiptIssuedAsync(Guid companyId, Document receipt)
    {
        if (receipt.RelatedDocumentId is Guid sourceId)
            await StampSettlementReceiptFailureAsync(companyId, sourceId, null, null);
        if (receipt.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected) return;   // ร่าง ⇒ ส่งตอนอนุมัติ (ApproveDocumentAsync)
        if (_crossTenantWorkflow != null) await _crossTenantWorkflow.RouteSentDocumentAsync(receipt);
    }

    /// <summary>
    /// F-M2: ออกใบเสร็จตอนรับชำระล้ม — ล้มดัง 3 ที่ (ป้ายบนใบต้นทาง → แถว Autopilot · แจ้งเตือน <c>PostPaymentAutomationBlocked</c> · LogError) ·
    /// เรียกหลัง commit ของการชำระ (เงินบันทึกแล้ว — ไม่โยน)
    /// </summary>
    private async Task ReportSettlementReceiptFailureAsync(Guid companyId, Document source, Payment payment, string reason)
    {
        _logger.LogError("{Marker} ออกใบเสร็จรับเงิน/ใบกำกับ ณ วันรับเงินไม่สำเร็จ (payment {PaymentNumber} · เอกสาร {Doc}): {Reason}",
            SettlementReceiptFailedNote.Marker, payment.PaymentNumber, source.DocumentNumber, reason);
        await StampSettlementReceiptFailureAsync(companyId, source.Id, payment.PaymentNumber, reason);
        if (_notify == null) return;
        try
        {
            await _notify.DispatchAsync(companyId, NotificationEvents.PostPaymentAutomationBlocked, new NotificationContext
            {
                Title = $"⚠️ รับชำระแล้วแต่ออกใบเสร็จไม่สำเร็จ — {source.DocumentNumber}",
                Message = SettlementReceiptFailedNote.Build(payment.PaymentNumber, reason),
                ActionUrl = $"/pages/documents.html?id={source.Id}",
                EntityType = nameof(Document), EntityId = source.Id,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งเตือนออกใบเสร็จไม่สำเร็จของเอกสาร {Doc} ไม่สำเร็จ", source.DocumentNumber);
        }
    }

    /// <summary>ประทับ/ล้างป้ายบนใบต้นทาง (ExecuteUpdate แถวเดียว · อ่านค่าล่าสุดก่อน) · <paramref name="reason"/> null = ล้าง</summary>
    private async Task StampSettlementReceiptFailureAsync(Guid companyId, Guid sourceId, string? paymentNumber, string? reason)
    {
        try
        {
            var current = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == sourceId && d.CompanyId == companyId)
                .Select(d => d.InternalNotes).FirstOrDefaultAsync();
            var updated = reason == null ? SettlementReceiptFailedNote.Clear(current) : SettlementReceiptFailedNote.Append(current, paymentNumber, reason);
            if (updated == current) return;
            await _db.Documents.Where(d => d.Id == sourceId && d.CompanyId == companyId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.InternalNotes, updated));
            var tracked = _db.Documents.Local.FirstOrDefault(d => d.Id == sourceId);
            if (tracked != null)
            {
                tracked.InternalNotes = updated;
                _db.Entry(tracked).Property(d => d.InternalNotes).IsModified = false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ประทับป้ายออกใบเสร็จไม่สำเร็จบนเอกสาร {Doc} ไม่สำเร็จ", sourceId);
        }
    }
}

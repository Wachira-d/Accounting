using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments.Handlers;

/// <summary>
/// เงินเข้าแล้วสำหรับ **บิล POS ที่ลูกค้าสแกน QR จ่ายที่หน้าร้าน**
/// (<c>PaymentSourceKind.PosOrder</c>)
///
/// <para>สองขั้นตามลำดับของ POS เดิม: <c>AddPaymentAsync</c> (บันทึกยอดรับ) แล้ว
/// <c>CompleteOrderAsync</c> (ออกเลขใบกำกับอย่างย่อ · ลง JE · ตัดสต๊อก/วัตถุดิบตามสูตร ·
/// สะสมแต้ม) — <b>ห้ามข้ามไปเรียก Complete ตรง ๆ</b> เพราะมันตรวจว่ายอดชำระครบก่อน</para>
///
/// <para><b>ขา "เงินเข้า" ของ JE</b>: ผูก <c>PosPayment.PaymentIntentId</c> ไว้ ⇒
/// <c>CreateSalesJournalEntryAsync</c> จะเห็นเองแล้วลง<b>บัญชีพัก 11340</b>แทนบัญชี
/// ธนาคาร/ลิ้นชักของสาขา (เงินอยู่กับผู้ให้บริการ จะเข้าธนาคาร T+n) — ถ้าไม่ผูก id นี้
/// เงินจะไปโผล่ในลิ้นชักสาขาทั้งที่ไม่มีเงินสดจริงอยู่ในนั้น</para>
///
/// <para><b>บิลที่จ่ายหลายวิธี</b> (เงินสดบางส่วน + QR บางส่วน) ทำงานได้ตามปกติ:
/// เพิ่มเฉพาะแถวของ intent นี้ แล้ว Complete จะสำเร็จก็ต่อเมื่อผลรวมครบ —
/// ถ้ายังไม่ครบจะข้ามไปเงียบ ๆ ไม่ได้ ต้อง log ให้เห็นว่ารอส่วนที่เหลืออยู่</para>
/// </summary>
public class PosOrderPaymentHandler : IPaymentCompletionHandler
{
    private readonly AccountingDbContext _db;
    private readonly IPosService _pos;
    private readonly ILogger<PosOrderPaymentHandler> _logger;

    public PosOrderPaymentHandler(AccountingDbContext db, IPosService pos,
        ILogger<PosOrderPaymentHandler> logger)
    { _db = db; _pos = pos; _logger = logger; }

    public PaymentSourceKind SourceKind => PaymentSourceKind.PosOrder;

    public async Task HandleSucceededAsync(PaymentIntent intent, CancellationToken ct = default)
    {
        var order = await _db.PosOrders.AsNoTracking()
            .Where(o => o.Id == intent.SourceId && o.CompanyId == intent.CompanyId)
            .Select(o => new { o.Id, o.OrderNumber, o.Status, o.NetAmount })
            .FirstOrDefaultAsync(ct);
        if (order == null)
        {
            _logger.LogError(
                "เงินเข้าแล้วแต่หาบิล POS ไม่พบ — intent {Intent} บิล {Order} บริษัท {Company} "
                + "ยอด {Amount:N2} · ต้องบันทึกรับเงินด้วยมือที่เครื่อง",
                intent.Id, intent.SourceId, intent.CompanyId, intent.Amount);
            return;
        }
        if (order.Status == PosOrderStatus.Voided)
        {
            // เงินเข้าจริงแต่บิลถูกยกเลิกไปแล้ว = ต้องคืนเงิน · ห้ามเงียบเด็ดขาด
            _logger.LogError(
                "เงินเข้าแล้วแต่บิล {No} ถูกยกเลิกไปก่อน — intent {Intent} ยอด {Amount:N2} "
                + "· ต้องคืนเงินให้ลูกค้า",
                order.OrderNumber, intent.Id, intent.Amount);
            return;
        }

        // กันซ้ำ: มีแถวรับเงินของ intent นี้แล้วหรือยัง (webhook + poll ยิงพร้อมกันได้)
        var already = await _db.PosPayments.AsNoTracking()
            .AnyAsync(p => p.OrderId == order.Id && p.PaymentIntentId == intent.Id && !p.IsDeleted, ct);
        if (!already)
        {
            await _pos.AddPaymentAsync(intent.CompanyId, new Models.DTOs.Pos.CreatePaymentRequest(
                OrderId: order.Id,
                PaymentMethod: PaymentMethod.PromptPay,
                Amount: intent.Amount,
                ReceivedAmount: intent.Amount,
                ReferenceNo: intent.ProviderRef,
                CardLastFour: null
            ), intent.ConfirmedBy ?? "payment-gateway");

            // ผูกแถวที่เพิ่งสร้างกับ intent — DTO ของ POS ไม่มีช่องนี้ (และไม่ควรมี:
            // ผู้เรียก API ภายนอกต้องตั้งเองไม่ได้) จึงเขียนตรงหลังสร้าง
            var justAdded = await _db.PosPayments
                .Where(p => p.OrderId == order.Id && p.PaymentIntentId == null && !p.IsDeleted
                    && p.ReferenceNo == intent.ProviderRef)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (justAdded != null)
            {
                justAdded.PaymentIntentId = intent.Id;
                await _db.SaveChangesAsync(ct);
            }
            else
            {
                // ผูกไม่ได้ = JE จะลงบัญชีธนาคาร/ลิ้นชักแทนบัญชีพัก ⇒ ยอดเงินสดสาขา
                // สูงเกินจริง · ต้องดังพอให้ตามแก้ได้ ไม่ใช่ปล่อยผ่าน
                _logger.LogError(
                    "บันทึกรับเงินบิล {No} แล้วแต่ผูกกับรายการชำระออนไลน์ไม่ได้ — intent {Intent} "
                    + "· JE จะลงบัญชีเงินสด/ธนาคารแทนบัญชีพัก ต้องปรับปรุงด้วยมือ",
                    order.OrderNumber, intent.Id);
            }
        }

        if (order.Status == PosOrderStatus.Completed) return;   // ปิดบิลไปแล้ว = ซ้ำ

        var paid = await _db.PosPayments.AsNoTracking()
            .Where(p => p.OrderId == order.Id && !p.IsDeleted).SumAsync(p => p.Amount, ct);
        if (paid + 0.005m < order.NetAmount)
        {
            // จ่ายหลายวิธี — ยังไม่ครบ ปิดบิลไม่ได้ (ไม่ใช่ error) แต่ต้องเห็นว่ารออะไร
            _logger.LogInformation(
                "บิล {No} รับเงินออนไลน์ {Paid:N2}/{Total:N2} แล้ว — รอส่วนที่เหลือก่อนปิดบิล",
                order.OrderNumber, paid, order.NetAmount);
            return;
        }

        await _pos.CompleteOrderAsync(intent.CompanyId, order.Id,
            intent.ConfirmedBy ?? "payment-gateway");
    }
}

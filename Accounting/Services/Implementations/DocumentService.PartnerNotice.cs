using Accounting.Helpers;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 264 P3 (team-264-a2b-design §5.5 · C-03 · C-04 · คำตัดสิน 264-3-6/264-3-7) — ขั้นหลัง commit ของการรับชำระ/ยกเลิกการรับชำระ<b>ฝั่งผู้ขาย</b>
/// ที่แจ้งบริษัทผู้ซื้อที่เป็นคู่ค้า NextAcc · ไฟล์นี้แยกจาก DocumentService.cs ตาม F4.7 (ไฟล์หลักมีแค่บรรทัดเรียก) ·
/// สร้าง/ถอน "การแจ้ง" เท่านั้น — <b>ไม่บันทึกบัญชีในสมุดของผู้ซื้อ</b> (ท่อของผู้ซื้อ <see cref="PartnerPayablePipeline"/> ตัดสินเองภายใต้ความยินยอมของผู้ซื้อ) · ไม่โยน
/// </summary>
public partial class DocumentService
{
    /// <summary>
    /// หลัง commit ของ <see cref="CreatePaymentAsync"/> (ทุกทางเข้า 1:1 — มือ · gateway · สลิป · รายการเดินบัญชี · นำเข้า) และ <see cref="AfterPaymentRecordedAsync"/> (integration) ·
    /// ใบที่ไม่ได้ส่งเข้าระบบคู่ค้าจบด้วย query เดียว · เดิมการแจ้งเกิดเฉพาะเส้น <c>DocumentPaymentHandler</c> (C-04)
    /// </summary>
    private Task NotifyPartnerOfSellerPaymentAsync(Guid companyId, Guid paymentId, PaymentEntrySource source)
        => PartnerPaymentNoticeService.NotifyForSellerPaymentAsync(_db, _notify, _logger, companyId, paymentId, source, CancellationToken.None);

    /// <summary>การรับชำระที่ตัวกลับรายการร่วม (<c>ReversePaymentInternalAsync</c> · <c>ReverseMultiDocPaymentInternalAsync</c>) แตะในคำขอนี้ — รอถอนการแจ้งหลัง commit ·
    /// ฝ่ายค้าน 264-P3 M2: เดิมเรียกเฉพาะ <see cref="VoidPaymentAsync"/> ⇒ ยกเลิกทั้งใบ (cascade) ไม่ถอน/ไม่ป้าย</summary>
    private readonly List<Guid> _reversedPaymentsForPartnerNotice = new();

    private void QueuePartnerNoticeWithdrawal(Guid paymentId)
    {
        if (!_reversedPaymentsForPartnerNotice.Contains(paymentId)) _reversedPaymentsForPartnerNotice.Add(paymentId);
    }

    /// <summary>
    /// หลัง commit ของ <see cref="VoidPaymentAsync"/> / <see cref="VoidDocumentAsync"/> — การแจ้งที่ผู้ซื้อยังไม่บันทึก ⇒ ถอน · ที่บันทึกแล้ว ⇒ ป้ายบนใบซื้อ + แจ้ง
    /// (ไม่ยกเลิกการจ่ายของผู้ซื้อแทน · E6) · ตัวถอน<b>ตรวจในฐานว่าการรับชำระถูกกลับจริง</b> (ธุรกรรมที่ rollback/วนใหม่ไม่ถอนผิด) · ไม่มีการแจ้งที่อ้าง = ไม่ทำอะไร · ไม่โยน
    /// </summary>
    private async Task FlushPartnerNoticeWithdrawalsAsync(Guid companyId)
    {
        var ids = _reversedPaymentsForPartnerNotice.ToList();
        _reversedPaymentsForPartnerNotice.Clear();
        foreach (var paymentId in ids)
            await PartnerPaymentNoticeService.WithdrawForSellerPaymentAsync(_db, _notify, _logger, companyId, paymentId, DateTime.UtcNow, CancellationToken.None);
    }
}

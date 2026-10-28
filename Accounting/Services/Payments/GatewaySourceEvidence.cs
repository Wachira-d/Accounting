using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

/// <summary>
/// ตัวโหลด "บันทึกรับเงินฝั่งต้นทาง" ของรายการชำระ — **ตัวเดียว**ที่ปุ่ม "ส่งต่อใหม่" (<c>PaymentIntentService</c>) และ handler ทุกตัวใช้ตัดสินว่า
/// "ยอดนี้ถูกบันทึกแล้ว (โดยคน/ทางอื่น)" (รอบ 205 ทีม W1 · ฝ่ายค้าน W1 ข้อ 2) · กติกาตัดสินอยู่ที่ <see cref="PaymentRedispatchPolicy.SourceRecorded"/> (pure)
///
/// <para>ที่พัก: ใบมัดจำ/ใบเสร็จของการจอง (BookingNumber = เลขจอง · ไม่ร่าง/ยกเลิก) + ยอดบนตัวการจอง (<c>DepositPaid</c>/<c>PaymentReference</c> — ที่พักโหมดไม่ออกเอกสาร) ·
/// ออเดอร์: แถว <c>SiteOrderPayment</c> ที่สำเร็จ · เอกสาร: แถว <c>Payment</c> ของใบนั้น (ยกเว้นแถวที่ผูก intent นี้เอง) · ชนิดอื่น = ไม่มีหลักฐาน (ว่าง)</para>
/// </summary>
public static class GatewaySourceEvidence
{
    /// <summary>เลขอ้างอิงที่ handler ของระบบประทับเอง — บันทึกที่ถือเลขนี้ไม่ใช่หลักฐานการบันทึกมือ</summary>
    public static IReadOnlyList<string> GatewayReferences(PaymentIntent intent) => new[]
    {
        GatewayPaymentRecord.Reference(intent.Id),
        GatewayPaymentRecord.ReservationReference(intent.Id, intent.ProviderRef),
    };

    public static async Task<IReadOnlyList<SourceRecordedPayment>> RecordedPaymentsAsync(AccountingDbContext db, PaymentIntent intent, CancellationToken ct)
    {
        var companyId = intent.CompanyId;
        switch (intent.SourceKind)
        {
            case PaymentSourceKind.LodgingReservation:
            {
                var r = await db.LodgingReservations.AsNoTracking()
                    .Where(x => x.Id == intent.SourceId && x.CompanyId == companyId)
                    .Select(x => new { x.ReservationNumber, x.DepositPaid, x.PaymentReference, x.UpdatedAt, x.CreatedAt })
                    .FirstOrDefaultAsync(ct);
                if (r == null) return Array.Empty<SourceRecordedPayment>();
                var docs = await db.Documents.AsNoTracking()
                    .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.BookingNumber == r.ReservationNumber && d.IsDeposit
                        && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                        && (d.DocumentType == DocumentType.Receipt || d.DocumentType == DocumentType.TaxInvoice || d.DocumentType == DocumentType.Invoice))
                    .Select(d => new SourceRecordedPayment(d.TotalAmount, d.CreatedAt, d.Reference))
                    .ToListAsync(ct);
                if (r.DepositPaid > 0m)
                    docs.Add(new SourceRecordedPayment(r.DepositPaid, r.UpdatedAt ?? r.CreatedAt, r.PaymentReference));
                return docs;
            }
            case PaymentSourceKind.SiteOrder:
                return await db.SiteOrderPayments.AsNoTracking()
                    .Where(p => p.CompanyId == companyId && p.OrderId == intent.SourceId && !p.IsDeleted && p.Status == SitePaymentStatus.Completed)
                    .Select(p => new SourceRecordedPayment(p.Amount, p.PaidAt ?? p.CreatedAt, p.Reference))
                    .ToListAsync(ct);
            case PaymentSourceKind.Document:
                return await db.Payments.AsNoTracking()
                    .Where(p => p.CompanyId == companyId && p.DocumentId == intent.SourceId && !p.IsDeleted && p.PaymentIntentId != intent.Id)
                    .Select(p => new SourceRecordedPayment(p.Amount, p.PaymentDate, p.Reference))
                    .ToListAsync(ct);
            default:
                return Array.Empty<SourceRecordedPayment>();
        }
    }

    /// <summary>handler ใช้: ยอดของ intent นี้ถูกบันทึกที่ต้นทางแล้ว (โดยคน/ทางอื่น) หลังเงินเข้า ⇒ ห้ามรับซ้ำ</summary>
    public static async Task<bool> AlreadyRecordedBySomeoneElseAsync(AccountingDbContext db, PaymentIntent intent, CancellationToken ct)
    {
        var recorded = await RecordedPaymentsAsync(db, intent, ct);
        return PaymentRedispatchPolicy.SourceRecorded(recorded, intent.Amount, GatewayReferences(intent), intent.ConfirmedAt ?? intent.CreatedAt);
    }
}

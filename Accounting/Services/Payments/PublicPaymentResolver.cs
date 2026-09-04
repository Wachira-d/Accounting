using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

/// <summary>เป้าหมายการจ่ายเงินที่ resolve จาก "ของที่ผู้เรียกถืออยู่แล้ว" —
/// ยอดและตัวตนมาจาก **แถวในฐานข้อมูล** ไม่ใช่จาก payload ของผู้เรียก</summary>
public sealed record PublicPayTarget(
    PaymentSourceKind SourceKind,
    Guid SourceId,
    Guid SiteId,
    decimal AmountDue,
    string Description,
    string? CustomerEmail,
    string? CustomerPhone,
    string Reference)
{
    /// <summary>จ่ายเพิ่มได้ตอนนี้ไหม — <c>false</c> เมื่อจ่ายครบ/ยกเลิก/เช็คเอาต์แล้ว
    ///
    /// <para>⚠️ แยก "ตัวตน" ออกจาก "ยอดที่ค้าง" โดยตั้งใจ: resolver ต้องคืนเป้าหมาย
    /// <b>เสมอ</b>เมื่อ token ถูกต้อง แม้ยอดค้างจะเป็น 0 — ไม่งั้นตอนจ่ายสำเร็จแล้ว
    /// (เคสที่สำคัญที่สุด) จะยืนยันไม่ได้ว่า intent นี้เป็นของผู้ถือ token คนนี้
    /// แล้วต้องไปตรวจแบบหลวม ๆ ซึ่งกลายเป็นช่องให้ใครถือ intentId ก็อ่านของคนอื่นได้</para></summary>
    public bool CanPayNow => AmountDue > 0.005m;
}

public interface IPublicPaymentResolver
{
    /// <summary>การจองที่พัก — พิสูจน์สิทธิ์ด้วย <c>PublicToken</c> ที่ออกให้แขกตอนจอง ·
    /// คืน <c>null</c> เฉพาะเมื่อ<b>ไม่พบแถว</b> (token ผิด) ไม่ใช่เมื่อไม่มียอดค้าง</summary>
    Task<PublicPayTarget?> ResolveLodgingAsync(Guid companyId, Guid siteId, string token, CancellationToken ct = default);

    /// <summary>คำสั่งซื้อหน้าเว็บ — พิสูจน์สิทธิ์ด้วย <c>orderId</c> (GUID ที่คืนตอนสั่งซื้อ)
    /// ซึ่งเป็น trust model เดียวกับ endpoint สาธารณะเดิมของ <c>CmsCommerceController</c></summary>
    Task<PublicPayTarget?> ResolveOrderAsync(Guid companyId, Guid siteId, Guid orderId, CancellationToken ct = default);
}

/// <summary>
/// **ตัวแปล "ของที่แขกถือ" → "เป้าหมายการจ่ายเงิน" — ที่เดียวของระบบ**
///
/// ═══ ที่มา (ผลตรวจ LDG-P0-01) ═══
/// <para><c>PaymentGatewayController</c> เป็น <c>[Authorize]</c> และ<b>ไม่มีทางเข้า
/// แบบไม่ล็อกอินเลยสักเส้น</b> ที่สร้าง <c>PaymentIntent</c> ได้ ⇒ ลูกค้าปลายทาง
/// (แขกที่พัก · ผู้ซื้อหน้าร้าน) <b>จ่ายออนไลน์ไม่ได้ทั้งระบบ</b> ⇒
/// <c>LodgingReservationPaymentHandler</c> และ <c>SiteOrderPaymentHandler</c> ที่
/// เขียนไว้ครบและถูกต้อง <b>ไม่มีวันถูกเรียก</b> — defect class
/// "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" ในรูปที่แพงที่สุด (ฟีเจอร์ที่เก็บเงินไม่ได้)</para>
///
/// <para><b>สองกติกาที่ห้ามผ่อน</b>:
/// <list type="number">
///   <item><b>ห้ามรับ <c>SourceId</c> ดิบจากผู้เรียก</b> — ไม่งั้นใครก็สร้าง intent
///     ให้การจองของคนอื่นได้ (IDOR) · ต้อง resolve จาก token/orderId ที่เขาถืออยู่</item>
///   <item><b>ยอดมาจากเซิร์ฟเวอร์เท่านั้น</b> — ถ้าเชื่อ <c>Amount</c> ที่ส่งมา
///     ใครก็จ่าย 1 บาทแล้วได้ห้อง (ญาติของ "ค่า default ที่แต่งขึ้น")</item>
/// </list></para>
/// </summary>
public class PublicPaymentResolver : IPublicPaymentResolver
{
    private readonly AccountingDbContext _db;
    public PublicPaymentResolver(AccountingDbContext db) => _db = db;

    public async Task<PublicPayTarget?> ResolveLodgingAsync(
        Guid companyId, Guid siteId, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var r = await _db.LodgingReservations.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.SiteId == siteId
                     && x.PublicToken == token && !x.IsDeleted)
            .Select(x => new
            {
                x.Id, x.ReservationNumber, x.Status, x.GuestEmail, x.GuestPhone,
                x.DepositRequired, x.DepositPaid, x.TotalAmount, x.FolioTotal, x.PaidAmount,
            })
            .FirstOrDefaultAsync(ct);
        if (r == null) return null;

        var due = LodgingAmounts.OnlinePayableAmount(
            r.Status, r.DepositRequired, r.DepositPaid, r.TotalAmount, r.FolioTotal, r.PaidAmount);

        var what = r.Status == LodgingReservationStatus.Pending && r.DepositRequired > 0.005m
            ? "มัดจำการจอง" : "ค่าที่พัก";
        return new PublicPayTarget(
            PaymentSourceKind.LodgingReservation, r.Id, siteId, due ?? 0m,
            $"{what} {r.ReservationNumber}", r.GuestEmail, r.GuestPhone, r.ReservationNumber);
    }

    public async Task<PublicPayTarget?> ResolveOrderAsync(
        Guid companyId, Guid siteId, Guid orderId, CancellationToken ct = default)
    {
        var o = await _db.SiteOrders.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.SiteId == siteId
                     && x.Id == orderId && !x.IsDeleted)
            .Select(x => new
            {
                x.Id, x.OrderNumber, x.Status, x.TotalAmount, x.PaidAmount,
                x.ShippingEmail, x.ShippingPhone,
            })
            .FirstOrDefaultAsync(ct);
        if (o == null) return null;

        // ยกเลิก/คืนเงินแล้ว = ไม่รับเงินเพิ่ม (ห้ามเปิดช่องให้จ่ายบิลที่ตายแล้ว)
        // — คืนเป้าหมายที่ยอด 0 ไม่ใช่ null เพราะยังต้องยืนยันตัวตนเพื่ออ่านผลการจ่ายได้
        var due = o.Status is SiteOrderStatus.Cancelled or SiteOrderStatus.Refunded
            ? 0m : Math.Max(0m, o.TotalAmount - o.PaidAmount);

        return new PublicPayTarget(
            PaymentSourceKind.SiteOrder, o.Id, siteId, due,
            $"คำสั่งซื้อ {o.OrderNumber}", o.ShippingEmail, o.ShippingPhone, o.OrderNumber);
    }
}

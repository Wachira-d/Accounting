using Accounting.Helpers;

namespace Accounting.Services.Interfaces;

/// <summary>ช่องส่งข้อความถึงลูกค้าของเครื่องจอง (รอบ 205 W3 · R-12/R-13) — อีเมลก่อน แล้ว LINE (บัญชีลูกค้าที่ล็อกอินด้วย LINE) ·
/// <b>บอกผลจริง</b>: <c>Sent</c> = ช่องทางใดช่องทางหนึ่งรับไปแล้ว · ไม่มีช่องทาง/ส่งล้มทุกช่อง ⇒ <c>Error</c> (ผู้เรียกประทับ "ส่งแล้ว" ได้เฉพาะเมื่อ Sent)</summary>
public interface IBookingNotifier
{
    Task<BookingSendResult> SendAsync(Guid companyId, BookingContact contact, string subject, string text, CancellationToken ct = default);
}

using Accounting.Helpers;

namespace Accounting.Services.Interfaces;

/// <summary>ช่องส่งข้อความถึงลูกค้า (และร้าน) — <b>ทางเดียว</b>ของทุกข้อความปลายทาง (รอบ 205 W3 · รอบ 211 LINE ที่ผูกเอง · รอบ 212 แพ็ก A ค่าตั้ง/โควตา/กันซ้ำ) ·
/// <b>บอกผลจริง</b>: <c>Sent</c> = ช่องทางใดช่องทางหนึ่งรับไปแล้ว (หรือส่งถึงไปแล้วก่อนหน้า — <c>Duplicate</c>) · ระงับโดยตั้งใจ ⇒ <c>IsSuppressed</c> + เหตุ ·
/// ไม่มีช่องทาง/ส่งล้มทุกช่อง ⇒ <c>Error</c> (ผู้เรียกประทับ "ส่งแล้ว" ได้เฉพาะเมื่อ Sent) ·
/// ผู้เรียกต้องส่ง <see cref="CustomerNotifyRequest.EventKey"/> จาก <see cref="CustomerNotifyEvents"/> (ด่าน <c>tools/required_call_site_check.py</c>)</summary>
public interface IBookingNotifier
{
    Task<BookingSendResult> SendAsync(CustomerNotifyRequest request, CancellationToken ct = default);
}

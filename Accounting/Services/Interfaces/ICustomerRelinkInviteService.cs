using Accounting.Models.DTOs.Line;

namespace Accounting.Services.Interfaces;

/// <summary>ผลหนึ่งรอบของงานเชิญผูกใหม่ — <c>Events</c> = การสลับที่แตะในรอบนี้ · <c>Completed</c> = การสลับที่ประมวลครบในรอบนี้</summary>
public sealed record RelinkRunResult(int Events, int Sent, int Failed, int Completed);

/// <summary>
/// รอบ 212 แพ็ก B (คำตัดสินข้อ 171 ข้อ 4 · เจ้าของ: "ส่ง") — ร้านเปลี่ยน LINE OA ⇒ บันทึกการสลับ แล้วงานเบื้องหลังเชิญลูกค้าที่ผูกไว้เดิมให้ผูกใหม่ ·
/// ตัวตัดสิน: <see cref="Accounting.Helpers.CustomerRelinkInvitePlan"/> · ส่งผ่าน <see cref="IBookingNotifier.SendAsync"/> เท่านั้น (ค่าตั้ง/โควตา/กันซ้ำของแพ็ก A) ·
/// ทุก query กรอง <c>CompanyId</c> ยกเว้นการตรวจ bot ของ OA กลาง (OA เดียวทุกบริษัท — แตกเป็นแถวต่อบริษัท)
/// </summary>
public interface ICustomerRelinkInviteService
{
    /// <summary>บันทึกการสลับ (ถ้าเป็นการสลับจริงตาม <c>CustomerRelinkInvitePlan.IsSwitch</c>) · กันซ้ำด้วยคีย์ (คำขอซ้อนได้แถวเดียว) · คืน id ของแถวใหม่ หรือ null</summary>
    Task<Guid?> RecordSwitchAsync(Guid companyId, string? fromScope, string? fromDestination, string toScope, string? toDestination,
        string source, string? actor, CancellationToken ct = default);

    /// <summary>webhook ที่ลายเซ็นผ่านแล้วเห็น bot (<c>destination</c>) — ลิงก์ที่ยังเปิดซึ่งผูกกับ bot อื่นของ OA ชนิดเดียวกัน ⇒ บันทึกการสลับ ·
    /// <paramref name="companyId"/> = บริษัทของ webhook ต่อบริษัท (null = OA กลาง) · คืนจำนวนแถวที่บันทึก · ไม่โยน (log)</summary>
    Task<int> NoteWebhookDestinationAsync(string scope, Guid? companyId, string? destination, CancellationToken ct = default);

    /// <summary>ถาม LINE ว่า token นี้เป็น bot ไหน (<c>GET /v2/bot/info</c> → <c>userId</c>) — ใช้ตอนบันทึก token ใหม่เพื่อรู้ทันทีว่าเปลี่ยน OA หรือแค่ออก token ใหม่ ·
    /// ไม่สำเร็จ ⇒ null (webhook ครั้งถัดไปตัดสินแทน)</summary>
    Task<string?> FetchBotUserIdAsync(string accessToken, CancellationToken ct = default);

    /// <summary>หนึ่งรอบของ <c>CustomerNotifyOutboxJob</c>: ทยอยส่งคำเชิญของการสลับที่ค้าง (ต่อบริษัท ≤ <c>BatchPerCompanyPerRun</c>) แล้วประทับความคืบหน้าบนแถว</summary>
    Task<RelinkRunResult> ProcessPendingAsync(DateTime nowUtc, CancellationToken ct = default, Guid? onlyCompanyId = null);

    /// <summary>ความคืบหน้าของการสลับล่าสุด (หน้าตั้งค่า LINE) · ไม่มี ⇒ null</summary>
    Task<CustomerRelinkProgress?> GetProgressAsync(Guid companyId, CancellationToken ct = default);
}

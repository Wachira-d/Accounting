using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;

namespace Accounting.Services.Interfaces;

/// <summary>การจอง/ออเดอร์ที่หาได้จากโทเคนสาธารณะ + สมาชิกเจ้าของ (ถ้ามี)</summary>
public sealed record CustomerLineRecordRef(Guid Id, Guid? CustomerId);

/// <summary>
/// รอบ 211 (คำตัดสินข้อ 170) — ลูกค้าปลายทางผูก LINE เองเพื่อรับแจ้งเตือน (สมาชิกเว็บ / แขกของการจอง-ออเดอร์) ·
/// ตัวตัดสิน: <see cref="CustomerLineLinkRule"/> (รหัส/ลิงก์) + <see cref="CustomerNotifyChannel"/> (OA ไหน · ลิงก์ใช้ได้ไหม · ลำดับช่อง) ·
/// ทุก query กรอง <c>CompanyId</c> ยกเว้นการไถ่รหัสบน OA กลางของ NextAcc (OA เดียวทุกบริษัท — ค้นด้วย hash ของรหัสสุ่ม 40 บิต · ด่านความถี่ต่อ LINE user)
/// </summary>
public interface ICustomerLineLinkService
{
    /// <summary>OA ที่ใช้แจ้งลูกค้าของบริษัทตอนนี้ (กติกาเดียวกับการ push)</summary>
    Task<CustomerLineChannel> ResolveChannelAsync(Guid companyId, CancellationToken ct = default);

    /// <summary>ข้อมูลหน้าตั้งค่า: OA ที่ใช้ · ผูกได้ไหม · URL webhook ต่อบริษัท · จำนวนลิงก์ใช้งาน/ค้าง OA เก่า</summary>
    Task<CustomerLineChannelInfo> GetChannelInfoAsync(Guid companyId, CancellationToken ct = default);

    /// <summary>หาเป้าหมายจากโทเคนสาธารณะของการจอง/ออเดอร์ (กรองบริษัท+เว็บ) · ไม่พบ ⇒ null</summary>
    Task<CustomerLineRecordRef?> ResolveRecordByTokenAsync(Guid companyId, Guid siteId, string kind, string token, CancellationToken ct = default);

    Task<CustomerLineStatusResponse> GetStatusAsync(Guid companyId, Guid siteId, string kind, Guid targetId, Guid? memberId, CancellationToken ct = default);

    /// <summary>ออกรหัสผูก (ยกเลิกรหัสค้างเดิมของเป้าหมายเดียวกัน) · ร้านยังผูกไม่ได้ ⇒ <see cref="BusinessRuleException"/></summary>
    Task<CustomerLineStartResponse> StartAsync(Guid companyId, Guid siteId, string kind, Guid targetId, CancellationToken ct = default);

    /// <summary>ยกเลิกการเชื่อมต่อ (ถอนความยินยอมของแถวนั้นด้วย) · คืนจำนวนลิงก์ที่ปิด</summary>
    Task<int> UnlinkAsync(Guid companyId, Guid siteId, string kind, Guid targetId, string reason, CancellationToken ct = default);

    /// <summary>webhook: ข้อความ "ผูก LN-…" ⇒ ข้อความตอบกลับ · ไม่ใช่ข้อความผูกของลูกค้า ⇒ null (ให้เส้นเดิมจัดการต่อ)</summary>
    /// <param name="scope"><see cref="CustomerNotifyChannel.ScopeNextAcc"/> หรือ <see cref="CustomerNotifyChannel.ScopeCompany"/></param>
    /// <param name="companyId">บริษัทเจ้าของ OA (เฉพาะ scope company — มาจาก URL webhook ที่ลายเซ็นผ่านแล้ว)</param>
    Task<string?> TryRedeemAsync(string scope, Guid? companyId, string? destination, string lineUserId, string text, CancellationToken ct = default);

    /// <summary>webhook <c>unfollow</c> ⇒ ประทับ BlockedAt · <c>follow</c> ⇒ ล้าง (คืนจำนวนลิงก์ที่กลับมาใช้)</summary>
    Task<int> SetBlockedAsync(string scope, Guid? companyId, string lineUserId, bool blocked, CancellationToken ct = default);

    /// <summary>webhook ต่อบริษัทที่ลายเซ็นผ่าน ⇒ จำ bot userId ของ OA ปัจจุบัน</summary>
    Task LearnDestinationAsync(Guid companyId, string? destination, CancellationToken ct = default);

    /// <summary>ตอบกลับด้วย reply token (ไม่กินโควตา push) ผ่าน token ของ OA ตาม scope · ไม่สำเร็จ ⇒ false (log)</summary>
    Task<bool> ReplyAsync(string scope, Guid? companyId, string replyToken, string text, CancellationToken ct = default);

    /// <summary>LINE userId ที่ลูกค้าผูกเองและยังใช้ได้กับ OA ปัจจุบัน — ระเบียน (การจอง/ออเดอร์) &gt; สมาชิก &gt; สมาชิกที่ผูกผู้ติดต่อ ERP · ไม่มี ⇒ null</summary>
    Task<string?> ResolveLineUserIdAsync(Guid companyId, CustomerLineTarget target, CancellationToken ct = default);
}

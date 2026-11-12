using Accounting.Models.DTOs.Notification;

namespace Accounting.Services.Interfaces;

/// <summary>รอบ 254 WP-11 L2 (คำตอบเจ้าของ Q6 ทางลัด) — ผูก/ยกเลิก LINE ของผู้ติดต่อผ่าน <b>LINE OA ของบริษัท</b> พร้อมความยินยอมของผู้รับ ·
/// ผู้เขียนปลายทาง LINE ของผู้ติดต่อ (<c>ContactNotifyEndpoint</c> Kind=LineUser · scope company) ทางเดียวของรอบนี้</summary>
public interface IContactLineBindingService
{
    /// <summary>ผู้ดูแลบริษัทสร้างลิงก์/QR (ผู้เรียกตรวจสิทธิ์ Contact.Edit แล้ว) · OA ของบริษัทไม่พร้อม ⇒ <c>BusinessRuleException</c> พร้อมทางไปต่อ</summary>
    Task<ContactLineInviteResponse> CreateInviteAsync(Guid companyId, Guid contactId, Guid? actorUserId, string? actorEmail, CancellationToken ct = default);

    /// <summary>หน้าเชิญ (public · สิทธิ์ = token) — ลิงก์ใช้ไม่ได้ ⇒ <c>Problem</c> (ข้อความเดียว ไม่บอกเหตุละเอียด)</summary>
    Task<ContactLineJoinView> GetJoinViewAsync(Guid companyId, string? token, string? ip, CancellationToken ct = default);

    /// <summary>ฝ่ายค้าน M4: ยกเลิกลิงก์ผูก LINE ที่ยังเปิดของผู้ติดต่อ (ผู้เรียกตรวจ Contact.Edit แล้ว) · audit chain · คืนจำนวนที่ยกเลิก</summary>
    Task<int> RevokeInvitesAsync(Guid companyId, Guid contactId, Guid? actorUserId, string? actorEmail, CancellationToken ct = default);

    /// <summary>ผู้รับกดยินยอม (public) ⇒ บันทึก <c>PdpaConsentRecord</c> + ออกรหัส <c>LN-…</c> อายุ 30 นาที · จำกัดความถี่ต่อ IP (DB)</summary>
    Task<ContactLineJoinConsentResponse> ConsentAsync(Guid companyId, string? token, string? ip, string? userAgent, CancellationToken ct = default);

    /// <summary>webhook ของ OA บริษัท: <c>ผูก LN-…</c> ที่เป็นรหัสของคำเชิญผู้ติดต่อ ⇒ สร้างปลายทาง + คำตอบ · ไม่ใช่รหัสของผู้ติดต่อ ⇒ null (ผู้เรียกส่งต่อให้เส้นลูกค้าเว็บ)</summary>
    /// <param name="oaScope">รอบ 262: OA ของ webhook ที่รับข้อความ (ค่าตั้งต้น company · webhook กลางส่ง nextacc ผ่าน <see cref="TryRedeemCentralAsync"/>)</param>
    Task<string?> TryRedeemAsync(Guid companyId, string? destination, string lineUserId, string text, CancellationToken ct = default,
        string oaScope = Accounting.Helpers.NotifyEndpointKind.ScopeCompany);

    /// <summary>รอบ 262 (คำตัดสิน 262-1-2): webhook ของ OA กลาง — รหัสผูกผู้ติดต่อของ<b>บริษัทผู้ให้บริการเท่านั้น</b> · ไม่ใช่รหัสของผู้ติดต่อ/ไม่ได้ตั้งบริษัทผู้ให้บริการ ⇒ null
    /// (ผู้เรียกส่งต่อให้บอท ERP/เส้นลูกค้าเว็บตามเดิม)</summary>
    Task<string?> TryRedeemCentralAsync(string? destination, string lineUserId, string text, CancellationToken ct = default);

    /// <summary>รอบ 262: webhook OA กลาง — "หยุด" ปลดปลายทางของบริษัทผู้ให้บริการที่ผูกกับบอทกลาง · ไม่มี ⇒ null</summary>
    Task<string?> TryStopCentralAsync(string? destination, string lineUserId, string text, CancellationToken ct = default);

    /// <summary>รอบ 262: webhook OA กลาง — follow/unfollow ของปลายทางบริษัทผู้ให้บริการ</summary>
    Task<int> SetBlockedCentralAsync(string? destination, string lineUserId, bool blocked, CancellationToken ct = default);

    /// <summary>รอบ 262 (คำตัดสิน 262-1-3): กล่อง "รับใบแจ้งหนี้ทาง LINE" บนศูนย์บิลของผู้เช่า (server computes)</summary>
    Task<Accounting.Helpers.ProviderCentralOa.BillingLineView> BillingLineViewAsync(Guid buyerCompanyId, bool canManage, CancellationToken ct = default);

    /// <summary>รอบ 262 (คำตัดสิน 262-1-3): ผู้เช่าขอลิงก์ผูก LINE ของผู้ติดต่อตัวเองในบริษัทผู้ให้บริการ (ผู้เรียกตรวจ Billing.Manage แล้ว) · ไม่พร้อม ⇒ BusinessRuleException</summary>
    Task<ContactLineInviteResponse> CreateBillingInviteAsync(Guid buyerCompanyId, Guid? actorUserId, string? actorEmail, CancellationToken ct = default);

    /// <summary>รอบ 262 ฝ่ายค้าน M1 (คำตัดสิน 262-1-10): ผู้เช่ายกเลิกปลายทาง LINE รับใบค่าบริการของตัวเอง (ผู้เรียกตรวจ Billing.Manage แล้ว) · ตัวยกเลิกเดียว + ถอนความยินยอม + audit ·
    /// ปลายทางที่ไม่ใช่ของผู้เช่ารายนี้ ⇒ 404</summary>
    Task<Accounting.Helpers.ProviderCentralOa.BillingLineView> UnlinkBillingEndpointAsync(Guid buyerCompanyId, Guid endpointId, Guid? actorUserId, string? actorEmail,
        CancellationToken ct = default);

    /// <summary>webhook ของ OA บริษัท: ผู้รับพิมพ์ "หยุด" ⇒ ปลดปลายทาง 1:1 ของ userId นั้นในบริษัทนี้ + ถอนความยินยอม · ไม่มีปลายทาง ⇒ null (ไม่ตอบ — แชตของร้าน)</summary>
    Task<string?> TryStopAsync(Guid companyId, string? destination, string lineUserId, string text, CancellationToken ct = default);

    /// <summary>webhook ของ OA บริษัท: unfollow ⇒ ประทับบล็อก · follow ⇒ ล้าง (ปลายทาง 1:1 ของบริษัทนี้)</summary>
    Task<int> SetBlockedAsync(Guid companyId, string? destination, string lineUserId, bool blocked, CancellationToken ct = default);
}

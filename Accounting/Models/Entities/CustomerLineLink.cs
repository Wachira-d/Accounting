namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 211 (คำตัดสินข้อ 170) — LINE ของลูกค้าปลายทางที่ลูกค้า<b>ผูกเอง</b> (opt-in) กับ สมาชิกเว็บ / การจอง / คำสั่งซื้อ ·
/// <c>LineUserId</c> ใช้ได้กับ OA ที่ผูกเท่านั้น (<c>OaScope</c> + <c>OaDestination</c>) · ใช้งานอยู่ = <c>UnlinkedAt == null &amp;&amp; BlockedAt == null</c> ·
/// ตัวตัดสินอยู่ที่ <c>Helpers/CustomerLineLinkRule</c> + <c>Helpers/CustomerNotifyChannel</c> · บริการ <c>CustomerLineLinkService</c>
/// </summary>
public class CustomerLineLink : TenantEntity
{
    public Guid SiteId { get; set; }
    /// <summary>member / booking / order (<c>CustomerLineLinkRule.Kind*</c>)</summary>
    public string TargetKind { get; set; } = "";
    /// <summary>SiteCustomer.Id / SiteBooking.Id / SiteOrder.Id</summary>
    public Guid TargetId { get; set; }
    /// <summary>LINE userId ("U…") ตามที่ OA นั้นเห็น — ห้าม log เต็ม (ใช้ <c>CustomerLineLinkRule.Mask</c>)</summary>
    public string LineUserId { get; set; } = "";
    /// <summary>nextacc / company (<c>CustomerNotifyChannel.Scope*</c>)</summary>
    public string OaScope { get; set; } = "";
    /// <summary>bot userId ของ OA ที่รับการผูก (ฟิลด์ <c>destination</c> ใน webhook) · null = ไม่รู้</summary>
    public string? OaDestination { get; set; }
    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
    /// <summary>ลูกค้า/ระบบยกเลิกการเชื่อมต่อ (ถาวร — ผูกใหม่ = แถวใหม่)</summary>
    public DateTime? UnlinkedAt { get; set; }
    /// <summary>user / relinked / erase (DSR)</summary>
    public string? UnlinkReason { get; set; }
    /// <summary>ลูกค้าบล็อก OA (event <c>unfollow</c>) · กลับมาเพิ่มเพื่อน (<c>follow</c>) ⇒ ล้าง</summary>
    public DateTime? BlockedAt { get; set; }
    /// <summary>แถว <c>PdpaConsentRecord</c> ของการยินยอมรับข้อความ (null = ไม่มีอีเมล/เบอร์ให้ผูกหลักฐาน)</summary>
    public Guid? ConsentRecordId { get; set; }
}

/// <summary>รหัสผูก LINE ใช้ครั้งเดียว — เก็บเฉพาะ hash (<c>CustomerLineLinkRule.Hash</c>) · อายุ 30 นาที · ผูกกับ OA ที่ออกรหัส (<c>OaScope</c>)</summary>
public class CustomerLineLinkCode : TenantEntity
{
    public Guid SiteId { get; set; }
    public string TargetKind { get; set; } = "";
    public Guid TargetId { get; set; }
    public string CodeHash { get; set; } = "";
    public string OaScope { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public Guid? LinkId { get; set; }
}

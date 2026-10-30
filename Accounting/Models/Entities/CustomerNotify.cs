namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 212 แพ็ก A (คำตัดสินข้อ 171) — ค่าตั้งช่องทางแจ้งลูกค้าต่อประเภทข้อความ · <c>SiteId</c> null = ระดับบริษัท ·
/// <c>EventKey</c> = คีย์ใน <c>Helpers/CustomerNotifyEvents</c> หรือ <c>*</c> (ทั้งหมด) · ช่องละ <c>null</c> = สืบทอด ·
/// <b>ไม่มีแถว = ส่งเหมือนเดิม</b> (ตัวตัดสิน <c>Helpers/CustomerNotifyPolicy.Resolve</c>)
/// </summary>
public class CustomerNotifyRule : TenantEntity
{
    public Guid? SiteId { get; set; }
    public string EventKey { get; set; } = "";
    public bool? LineEnabled { get; set; }
    public bool? EmailEnabled { get; set; }
}

/// <summary>
/// รอบ 212 แพ็ก A — บันทึกการส่งต่อข้อความ (กันส่งซ้ำด้วย <c>MessageKey</c> unique ต่อบริษัท + claim lease หลาย instance) ·
/// <b>ไม่เก็บอีเมล/LINE userId</b> (PDPA — เก็บแค่ชนิด/รหัสรายการ) · อายุ 13 เดือนแล้วลบ (<c>PdpaRetentionPurgeJob</c>)
/// </summary>
public class CustomerNotifyLog : TenantEntity
{
    public string MessageKey { get; set; } = "";
    public string EventKey { get; set; } = "";
    public string? TargetKind { get; set; }
    public Guid? TargetId { get; set; }
    /// <summary>line / email</summary>
    public string? Channel { get; set; }
    /// <summary>nextacc-oa / company-oa / system-smtp / tenant-smtp</summary>
    public string? Source { get; set; }
    public bool Metered { get; set; }
    /// <summary>Pending / Claimed / Sent / Failed / Suppressed (<c>NotifyLogStatus</c>)</summary>
    public string Status { get; set; } = "Pending";
    public DateTime? ClaimedUntil { get; set; }
    public int Attempts { get; set; }
    public DateTime? SentAt { get; set; }
    public string? Note { get; set; }
    /// <summary>รอบ 213: เหตุที่ระงับ (<c>Helpers/NotifySuppressReason</c>: quota-full / event-off / tenant-only) — แยกแถวที่ส่งซ้ำได้ (โควตา) ออกจากแถวที่ร้านปิดเอง ·
    /// ส่งถึงแล้ว ⇒ ล้างเป็น null</summary>
    public string? SuppressReason { get; set; }
    /// <summary>รอบ 213: เวลาที่ร้านกด "ส่งซ้ำ" ล่าสุด — ระหว่างส่งเป็น lease กันกดซ้ำ (<c>CustomerNotifyResend.ResendLease</c>) · ส่งถึง ⇒ คงไว้เป็นร่องรอย · ไม่ถึง ⇒ ปล่อย (null)</summary>
    public DateTime? ResendRequestedAt { get; set; }
    /// <summary>รอบ 213: จำนวนครั้งที่ร้านสั่งส่งซ้ำ (ร่องรอย)</summary>
    public int ResendCount { get; set; }
}

/// <summary>
/// รอบ 212 แพ็ก A — ตัวนับข้อความช่องกลางต่อบริษัทต่องวด (เดือนตามเวลาไทย "yyyy-MM") · PK (CompanyId, Period) ·
/// เพิ่ม/คืนด้วย <c>UPDATE … RETURNING</c> (atomic หลาย instance) · งวดใหม่ = แถวใหม่ (ไม่มีงาน reset)
/// </summary>
public class NotifyUsageCounter
{
    public Guid CompanyId { get; set; }
    public string Period { get; set; } = "";
    /// <summary>ข้อความที่นับในเพดานแพ็กเกจ (รวมข้อความจำเป็นที่นับเกินเพดาน)</summary>
    public int Used { get; set; }
    /// <summary>ข้อความที่ใช้โบนัส (แพ็กที่ซื้อ)</summary>
    public int BonusUsed { get; set; }
    public DateTime? Warned80At { get; set; }
    public DateTime? Warned100At { get; set; }
}

/// <summary>สถานะของ <see cref="CustomerNotifyLog"/> (เก็บเป็นข้อความ)</summary>
public static class NotifyLogStatus
{
    public const string Pending = "Pending";
    public const string Claimed = "Claimed";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    public const string Suppressed = "Suppressed";
}

/// <summary>
/// รอบ 217 (คำตัดสินข้อ 175) — คิวแจ้งเตือน "สร้างแล้ว" ของการจอง/ออเดอร์หน้าเว็บ: เขียนพร้อมแถวการจอง (ธุรกรรมเดียว) แล้วให้
/// <c>DeferredNotifyDispatchJob</c> ส่งตามหลัง ⇒ คำขอจองตอบกลับทันทีไม่ต้องรอ SMTP/LINE (ผู้ใช้กดแล้ว "นิ่ง" จนกดซ้ำ) ·
/// unique (<see cref="Kind"/>, <see cref="EntityId"/>) ⇒ หนึ่งเหตุการณ์ต่อรายการ · lease <see cref="ClaimedUntil"/> กันสองเครื่องส่งคู่ ·
/// ผลส่งถึง/ไม่ถึงอยู่ที่ CustomerNotifyLogs + หมายเหตุภายในของรายการ (เส้นเดิม) · แถวนี้บอกเฉพาะ "เดินแล้วหรือยัง" (กติกา <c>Helpers/DeferredNotifyPolicy</c>)
/// </summary>
public class DeferredNotification : TenantEntity
{
    public Guid SiteId { get; set; }
    /// <summary><c>DeferredNotifyPolicy.BookingCreated</c> / <c>OrderCreated</c></summary>
    public string Kind { get; set; } = "";
    public Guid EntityId { get; set; }
    public DateTime DueAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClaimedUntil { get; set; }
    public int Attempts { get; set; }
    public DateTime? DoneAt { get; set; }
    public string? LastError { get; set; }
}

namespace Accounting.Models.Entities;

/// <summary>ชุดสรุปประจำวันทาง LINE ถึงพนักงาน (รอบ 212 C · คำตัดสินข้อ 5 · ตารางตาม <c>NOTIFY_PREFS_QUOTA_DAILY_SUMMARY_DESIGN.md</c> §2.5) —
/// ต่อบริษัท (+ เว็บเดียวถ้าเลือก) · ตัวตัดสินทุกค่าอยู่ใน <c>Helpers/StaffDailySummaryPlan</c> · migration <c>Helpers/StaffDailySummaryMigration</c></summary>
public class StaffDailySummaryConfig : TenantEntity
{
    /// <summary>null = ทุกเว็บที่ผู้รับเห็น · มีค่า = เว็บนี้เว็บเดียว (ผู้รับยังเห็นเฉพาะเมื่อมีสิทธิ์บนเว็บนี้)</summary>
    public Guid? SiteId { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>เวลาส่ง (Asia/Bangkok) ปัดเป็นช่วง 5 นาที</summary>
    public TimeOnly SendAtLocal { get; set; } = new(7, 30);

    /// <summary>csv ของคีย์หัวข้อจาก <c>StaffDailySummaryPlan.Sections</c></summary>
    public string Sections { get; set; } = "";

    /// <summary>csv ของ UserId ที่เลือกรายคน</summary>
    public string? RecipientUserIds { get; set; }

    /// <summary>csv ของ <c>UserRole</c> (บทบาทบริษัท) — ทุกสมาชิกที่มีบทบาทนี้</summary>
    public string? RecipientRoles { get; set; }

    /// <summary>watermark ต่อบริษัท/ชุด — วันที่ (เวลาไทย) ที่ทุกผู้รับจบแล้ว · วันนี้ = ไม่ส่งซ้ำ</summary>
    public DateOnly? LastSentLocalDate { get; set; }

    /// <summary>สรุปผลรอบล่าสุด (ส่งถึง/ยังไม่ผูก/ล้ม + เหตุ) — หน้าตั้งค่าแสดง (ล้มไม่เงียบ)</summary>
    public string? LastRunNote { get; set; }

    public DateTime? LastRunAt { get; set; }
}

/// <summary>หนึ่งแถวต่อ (ชุด · วัน · ผู้รับ) — unique ⇒ ไม่ส่งซ้ำต่อวันต่อคนแม้สองเครื่อง/สองรอบ · สถานะจาก <c>StaffDailySummaryPlan.Status*</c></summary>
public class StaffDailySummaryDelivery : TenantEntity
{
    public Guid ConfigId { get; set; }
    public DateOnly LocalDate { get; set; }
    public Guid UserId { get; set; }
    public string Status { get; set; } = "";
    public int Attempts { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? SentAt { get; set; }
    /// <summary>จำนวนก้อนข้อความในคำขอ push (≤ 5)</summary>
    public int Messages { get; set; }
    /// <summary>หน่วยโควตาข้อความช่องกลางที่ใช้ (1 ต่อผู้รับต่อวันเมื่อส่งถึง)</summary>
    public int Units { get; set; }
    public string? Error { get; set; }
}

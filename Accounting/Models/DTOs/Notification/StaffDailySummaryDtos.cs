namespace Accounting.Models.DTOs.Notification;

/// <summary>รอบ 212 C — สรุปประจำวันทาง LINE ถึงพนักงาน · ป้าย/รายการหัวข้อ/บทบาทมาจากเซิร์ฟเวอร์ (<c>Helpers/StaffDailySummaryPlan</c>) — หน้าเว็บไม่มีสำเนา</summary>
public class StaffDailySummarySectionDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Hint { get; set; } = "";
}

public class StaffDailySummaryOptionDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
}

public class StaffDailySummaryMemberDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string RoleLabel { get; set; } = "";
    /// <summary>ผูก LINE กับ OA กลางแล้ว ("ผูก 123456") — ไม่ผูก = ข้ามตอนส่ง + จอแสดง "ยังไม่ผูก LINE"</summary>
    public bool LineBound { get; set; }
}

public class StaffDailySummaryConfigDto
{
    public Guid Id { get; set; }
    public Guid? SiteId { get; set; }
    public string? SiteName { get; set; }
    public bool Enabled { get; set; }
    /// <summary>"HH:mm" เวลาไทย</summary>
    public string SendAt { get; set; } = "";
    public List<string> Sections { get; set; } = new();
    public List<Guid> RecipientUserIds { get; set; } = new();
    public List<string> RecipientRoles { get; set; } = new();
    /// <summary>ผู้รับที่ resolve แล้ว ณ ตอนนี้ (รายคน ∪ ตามบทบาท) พร้อมสถานะผูก LINE</summary>
    public List<StaffDailySummaryMemberDto> Recipients { get; set; } = new();
    public DateOnly? LastSentLocalDate { get; set; }
    public string? LastRunNote { get; set; }
    public DateTime? LastRunAt { get; set; }
}

public class StaffDailySummaryHistoryDto
{
    public Guid ConfigId { get; set; }
    public DateOnly Date { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string StatusLabel { get; set; } = "";
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public int Messages { get; set; }
    public DateTime? SentAt { get; set; }
}

public class StaffDailySummaryOverviewDto
{
    public List<StaffDailySummarySectionDto> Sections { get; set; } = new();
    public List<StaffDailySummaryOptionDto> Roles { get; set; } = new();
    public List<StaffDailySummaryOptionDto> Sites { get; set; } = new();
    public List<StaffDailySummaryMemberDto> Members { get; set; } = new();
    public List<StaffDailySummaryConfigDto> Configs { get; set; } = new();
    public List<StaffDailySummaryHistoryDto> History { get; set; } = new();
    /// <summary>วิธีผูก LINE (ข้อความจากเซิร์ฟเวอร์) + หน้าผูก</summary>
    public string BindHowTo { get; set; } = "";
    public string BindPage { get; set; } = "";
    public string DefaultSendAt { get; set; } = "";
}

public class UpsertStaffDailySummaryRequest
{
    public Guid? SiteId { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>"HH:mm" เวลาไทย (ปัดลงเป็นช่วง 5 นาที)</summary>
    public string? SendAt { get; set; }
    public List<string>? Sections { get; set; }
    public List<Guid>? RecipientUserIds { get; set; }
    public List<string>? RecipientRoles { get; set; }
}

public class StaffDailySummaryTestResult
{
    public bool Sent { get; set; }
    public string Message { get; set; } = "";
    public int Messages { get; set; }
}

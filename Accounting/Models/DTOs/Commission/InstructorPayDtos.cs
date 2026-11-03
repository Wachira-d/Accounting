using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Commission;

/// <summary>รอบ 209 P5 (R-209-C6 · คำตัดสินข้อ 168 ฏ) — ค่าตอบแทนของผู้ให้บริการหนึ่งคน (echo กลับครบทุกช่องที่บันทึก)</summary>
public record ResourcePayResponse(Guid ResourceId, string Name, bool IsActive, Guid? StaffUserId, Guid? EmployeeId, string? EmployeeName,
    InstructorPayMode Mode, decimal Rate, decimal MinimumPerClass);

/// <summary>บันทึกค่าตอบแทน — ทุกช่องส่งครบ (แทนค่าเดิมทั้งชุด) · EmployeeId null = ถอดการผูกพนักงาน</summary>
public record SaveResourcePayRequest(Guid? EmployeeId, InstructorPayMode Mode, decimal Rate, decimal MinimumPerClass);

/// <summary>ตัวเลือกพนักงาน (ชื่อเท่านั้น)</summary>
public record PayEmployeeOption(Guid Id, string Name);

/// <summary>รอบ 230: ตัวเลือกกติกาค่าตอบแทน (ค่า + ป้ายไทยจากเซิร์ฟเวอร์ — หน้าไม่มีสำเนาตาราง · <c>InstructorPayRule.ModeOptions</c>)</summary>
public record PayModeOption(string Value, string Label);

public record ResourcePaySettingsResponse(List<ResourcePayResponse> Resources, List<PayEmployeeOption> Employees, IReadOnlyList<PayModeOption> Modes);

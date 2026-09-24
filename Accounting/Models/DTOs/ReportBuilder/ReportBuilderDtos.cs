namespace Accounting.Models.DTOs.ReportBuilder;

/// <summary>รอบ 193 · A09: <c>ReportType</c>/<c>Category</c> เป็น <c>string?</c> โดยตั้งใจ — เดิม non-nullable
/// ⇒ [Required] โดยปริยาย ขณะที่ฟอร์มไม่มีช่อง ReportType และถือว่า "หมวด" ไม่บังคับ (ส่ง null) ⇒ สร้างรายงาน
/// ไม่ได้เลย. ตอนนี้ ReportType derive จาก ChartType (มี chart = "Chart" · ไม่มี = "Table") และหมวดว่าง = "Custom"
/// (ตัวตั้ง: <c>ReportBuilderService.ResolveReportType/ResolveCategory</c>)</summary>
public record CreateCustomReportRequest(string Name, string? Description, string? ReportType, string? Category, string DataSourceType, string? FilterJson, string? ColumnsJson, string? SortingJson, string? GroupingJson, string? AggregationJson, string? ChartType, string? ChartConfigJson, bool ShowTotals, bool IsPublic, bool IsScheduled, string? ScheduleFrequency, string? SendToEmails, string? ExportFormat);
/// <summary>null = ไม่แก้ · <c>""</c> ใน Description/ChartType = ล้างค่า · Category <c>""</c> = กลับเป็น "Custom" ·
/// Category เพิ่มรอบ 193 — ฟอร์มแก้ไขเปิดช่อง "หมวด" และส่งมาตลอดแต่สัญญาไม่รับ (silent no-op)</summary>
public record UpdateCustomReportRequest(string? Name, string? Description, string? FilterJson, string? ColumnsJson, string? SortingJson, string? GroupingJson, string? AggregationJson, string? ChartType, bool? ShowTotals, bool? IsPublic, bool? IsScheduled, string? ScheduleFrequency, string? Category = null);
// ShowTotals: echo กลับ (รอบ 193) — เดิมรับตอนสร้าง/แก้ แต่ไม่คืน ⇒ ฟอร์มแก้ไขติ๊ก "แสดงผลรวม" ทุกครั้ง
public record CustomReportResponse(Guid Id, string Name, string? Description, string ReportType, string Category, string DataSourceType, string? FilterJson, string? ColumnsJson, string? GroupingJson, string? ChartType, bool IsPublic, bool IsScheduled, DateTime CreatedAt, bool ShowTotals = false);
public record CustomReportListResponse(Guid Id, string Name, string ReportType, string Category, bool IsPublic, DateTime CreatedAt);
public record ReportExecutionResponse(string ReportName, string ReportType, List<Dictionary<string, object>> Data, Dictionary<string, object>? Totals, int TotalRows, DateTime ExecutedAt);
public record ReportDataSourceResponse(string Type, string DisplayName, string Description);
public record ReportColumnDefinition(string Name, string DisplayName, string DataType, bool IsSortable, bool IsFilterable, bool IsGroupable);

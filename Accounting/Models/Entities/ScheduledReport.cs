namespace Accounting.Models.Entities;

/// <summary>
/// #15 — Scheduled report email. User บอก "ส่ง P&L รายสัปดาห์ทุกจันทร์
/// 8:00 ให้เจ้าของ". Background service ตรวจตาม Cron + ใช้ existing
/// TaxFilingExport / ReportingService → email ตาม recipient.
///
/// Frequency:
///   Daily   — รายวัน 08:00 ตาม TZ ของบริษัท (Asia/Bangkok)
///   Weekly  — ทุกวัน X (DayOfWeek 1=Mon)
///   Monthly — วันที่ X ของเดือน (1-28)
///   Quarterly — สิ้นไตรมาส
/// </summary>
public class ScheduledReport : TenantEntity
{
    public string Name { get; set; } = "";          // "P&L รายสัปดาห์"
    public string ReportCode { get; set; } = "";    // "PNL" | "BALANCE_SHEET" | "AR_AGING" | "CASH_FORECAST" | "TRIAL_BALANCE"
    public string Frequency { get; set; } = "Weekly"; // Daily/Weekly/Monthly/Quarterly
    public int? DayOfWeek { get; set; }              // 0=Sun, 1=Mon, ...
    public int? DayOfMonth { get; set; }             // 1-28
    public int HourBangkok { get; set; } = 8;        // ส่งเวลานี้ (24h)
    public string RecipientsJson { get; set; } = "[]";  // JSON array of emails
    public bool IsActive { get; set; } = true;
    public DateTime? LastSentAt { get; set; }
    public string? LastResult { get; set; }          // "OK" | "ERROR: ..."

    /// <summary>Format ที่ส่ง: "PDF" | "EXCEL" | "CSV" | "INLINE" (inline body only).</summary>
    public string Format { get; set; } = "PDF";
}

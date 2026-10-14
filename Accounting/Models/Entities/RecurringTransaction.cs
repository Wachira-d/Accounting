using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// รายการที่เกิดซ้ำ (Recurring Invoice, Expense, Journal)
/// เทียบเท่า FlowAccount: Recurring invoices / PEAK: Recurring
/// </summary>
public class RecurringTransaction : TenantEntity
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public RecurringFrequency Frequency { get; set; }
    public RecurringStatus Status { get; set; } = RecurringStatus.Active;

    // Schedule
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime NextRunDate { get; set; }
    public DateTime? LastRunDate { get; set; }
    public int TotalRuns { get; set; }
    public int? MaxRuns { get; set; }
    public int? PreferredDay { get; set; } // day-of-month anchor (1-31) to prevent date drift

    // Template: which document/journal to create
    public string TemplateType { get; set; } = null!; // "Document" or "Journal"
    public DocumentType? DocumentType { get; set; }
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }

    // Template data (JSON)
    public string TemplateData { get; set; } = null!; // JSON of CreateDocumentRequest or CreateJournalEntryRequest

    // Notification
    public bool NotifyBeforeRun { get; set; } = true;
    public int NotifyDaysBefore { get; set; } = 1;
    public bool AutoApprove { get; set; } = false;

    /// <summary>ส่งอีเมลเอกสาร (PDF แนบ) ให้ผู้ติดต่อทันทีที่รอบสร้างเอกสาร —
    /// ไม่ต้องไปสร้างกฎในหน้า "ตารางส่งอีเมล" เอง. ใช้อีเมลของบริษัทเอง
    /// (CompanySettings.Email*) ผ่าน EmailSenderFactory เหมือนช่องทางอื่น.
    ///
    /// ⚠️ บังคับคู่กับ AutoApprove — เอกสาร Draft ยังไม่มีเลขที่จริงตาม §86/4
    /// (ใช้ DRAFT-{guid}) ส่งออกไปหาลูกค้าไม่ได้. service จึงตั้ง
    /// AutoApprove = true ให้อัตโนมัติเมื่อเปิดตัวนี้ และ echo กลับใน
    /// response ให้ UI เห็น (ห้าม silent no-op).</summary>
    public bool AutoSendEmail { get; set; } = false;

    // ── ผลรอบล่าสุด (ทีมตรวจรายการประจำ 2026-10-14 · F2-7 ล้มดัง 3 ที่) ──
    // เดิม cron ล้ม = LogError + ErrorLog ทุก 15 นาที ผู้ใช้เห็นแค่ "ครั้งถัดไป" ค้างวันเดิมโดยไม่รู้สาเหตุ ·
    // ตอนนี้: ข้อความ (ไทย · บอกช่อง + ทางแก้ — Helpers/RecurringRunFailure) เก็บบนแถว แสดงในหน้ารายการประจำ ·
    // รอบถัดไปที่สำเร็จล้างเอง · ระบบยังลองใหม่ทุกรอบ (ไม่ล็อกตาย — คำตัดสินข้อ 150)

    /// <summary>เหตุผลที่รอบล่าสุดสร้างเอกสารไม่สำเร็จ (null = รอบล่าสุดสำเร็จ/ยังไม่เคยล้ม)</summary>
    public string? LastRunError { get; set; }
    /// <summary>เวลาที่ล้มล่าสุด (UTC)</summary>
    public DateTime? LastRunErrorAt { get; set; }
    /// <summary>สร้างเอกสารสำเร็จแต่มีเรื่องต้องทำต่อ (อนุมัติอัตโนมัติไม่สำเร็จ · ส่งอีเมลไม่ได้เพราะผู้ติดต่อไม่มีอีเมล ·
    /// post สมุดรายวันไม่สำเร็จ · ผู้ถูกหักถูกตัดทิ้ง) — null = ไม่มี</summary>
    public string? LastRunNotice { get; set; }
    /// <summary>เอกสารที่รอบล่าสุดสร้าง (ลิงก์ "เปิดเอกสารล่าสุด" ในหน้ารายการประจำ) — null = ยังไม่เคยสร้าง/เป็นสมุดรายวัน</summary>
    public Guid? LastDocumentId { get; set; }

    /// <summary>Late-fee accrual policy — เปิดเมื่อสร้าง invoice แล้วเกิน due
    /// date จะ accrue ค่าปรับ. Off (default) = recurring เก่าไม่กระทบ.</summary>
    public bool LateFeeEnabled { get; set; } = false;

    /// <summary>อัตราค่าปรับต่อวัน (% ต่อวัน). default 0.05% (= 18.25%/ปี
    /// = อัตราเพดานตามประมวลรัษฎากร §103 + §103 ทวิ ไม่เกิน 15%/ปี เท่านั้น
    /// — แต่ภาคเอกชนใช้ %/วัน ตามสัญญา). 0 = no fee แม้ flag เปิด.</summary>
    public decimal LateFeeRatePerDay { get; set; } = 0.05m;

    /// <summary>Grace period วันก่อนเริ่มคิดค่าปรับ — default 7 วัน หลัง due
    /// date. คำนวณ daysOverdue = (today - dueDate - GraceDays). negative =
    /// ยังไม่ถึงเวลาคิด.</summary>
    public int LateFeeGraceDays { get; set; } = 7;

    /// <summary>เพดานค่าปรับ (% ของ totalAmount). default 20% = stop accrue
    /// เมื่อ accumulated late fee ถึง 20% ของยอดต้นทุน. null = no cap.</summary>
    public decimal? LateFeeMaxPercent { get; set; } = 20m;
}

namespace Accounting.Models.Entities;

/// <summary>
/// กฎการส่งอีเมลอัตโนมัติ — ตั้งครั้งเดียวต่อบริษัทต่อ trigger,
/// ระบบจะ enqueue งานส่งตามกฎทุกครั้งที่เกิด event.
///
/// Trigger ที่รองรับ:
///   DocumentApproved        = อนุมัติเอกสาร (ขายฝั่ง: Invoice/TaxInvoice/QT)
///   DocumentDueSoon         = ครบกำหนดใน N วัน (ถ้ายังไม่ชำระ)
///   DocumentOverdue         = เกินกำหนด N วัน (ยังไม่ชำระ)
///   PayrollPaid             = จ่ายเงินเดือนเสร็จ → ส่งสลิป
///   WhtCertIssued           = ออกใบ 50 ทวิแล้ว → ส่งให้ผู้รับ
///   RecurringInvoiceCreated = recurring สร้าง doc ใหม่ — ส่งเฉพาะใบที่
///                             อนุมัติแล้วเท่านั้น (Draft ยังเป็นเลข
///                             DRAFT-{guid} ตาม §86/4 ห้ามส่งออกหาลูกค้า);
///                             ถ้าไม่มีกฎนี้เลย ระบบจะดูธง AutoSendEmail
///                             บนตัว RecurringTransaction แทน (ส่งทันที)
///   MonthlyStatement        = สรุปรายการลูกค้าเดือนที่แล้ว
///                             (DayOfMonth + SendAtHour กำหนดเวลาส่ง)
/// OffsetDays:
///   - Approved/Issued/RecurringCreated: 0 = ส่งทันที, +N = หลัง event N วัน
///   - DueSoon: -N = ก่อนครบกำหนด N วัน
///   - Overdue: +N = หลังเกินกำหนด N วัน (เตือนซ้ำทุก N วันได้)
///   - MonthlyStatement: ใช้ DayOfMonth (1-31) แทน
/// SendAtHour (0-23 UTC+7): เวลาในวันที่จะส่ง (default 09:00).
/// DayOfMonth (1-31, 0 = ไม่ใช้): เฉพาะ MonthlyStatement — ส่งวันที่กี่
/// ของเดือน (default 5 = ส่งวันที่ 5 ของเดือนถัดไป สำหรับยอดเดือนก่อน).
/// </summary>
public class EmailScheduleRule : TenantEntity
{
    public string Trigger { get; set; } = "";
    public bool IsActive { get; set; } = true;

    /// <summary>"Invoice", "TaxInvoice", "Receipt", "Quotation", "BillingNote",
    /// "PurchaseInvoice", "Expense", "PaymentVoucher", "CreditNote", "DebitNote",
    /// "CertificateInLieu", "PayrollPayslip", "WhtCert50tawi" ฯลฯ.
    /// ใช้กับ Document* triggers; null = ทุกประเภท.</summary>
    public string? DocumentType { get; set; }

    /// <summary>วันชดเชย — บวก = หลัง event, ลบ = ก่อน event (สำหรับ DueSoon).</summary>
    public int OffsetDays { get; set; }

    /// <summary>เวลาที่จะส่ง (ชั่วโมง UTC+7). Default 9 = 09:00 น. กรุงเทพ.</summary>
    public int SendAtHour { get; set; } = 9;

    /// <summary>ส่งเตือนซ้ำทุก N วัน หลังครั้งแรก (เฉพาะ Overdue/DueSoon);
    /// 0 = ส่งครั้งเดียว.</summary>
    public int RepeatEveryDays { get; set; }

    /// <summary>หัวเรื่องเทมเพลต — รองรับ placeholder
    /// {DocNumber} {DueDate} {ContactName} {Amount} {CompanyName}.
    /// null = ใช้ default ของ DocumentEmailService.</summary>
    public string? SubjectTemplate { get; set; }

    /// <summary>เนื้อหา HTML เทมเพลต — placeholder เดียวกัน. null = default.</summary>
    public string? BodyTemplate { get; set; }

    /// <summary>คนรับเพิ่มเติม (BCC) คั่นด้วย comma — สำหรับ HR/Owner archive.</summary>
    public string? BccEmails { get; set; }

    /// <summary>วันที่กี่ของเดือนสำหรับ MonthlyStatement (default 5 =
    /// ส่งวันที่ 5 ของเดือน รวมยอดเดือนก่อน). อื่น ๆ ไม่ใช้.</summary>
    public int DayOfMonth { get; set; } = 5;

    /// <summary>เฉพาะ MonthlyStatement: กรองเฉพาะ contact ที่ใช้ filter นี้
    /// — "ทุกคน" (null/empty), "เฉพาะ contact ที่มีรายการในเดือน" (default),
    /// หรือ tag/group ID เฉพาะ.</summary>
    public string? AudienceFilter { get; set; }

    /// <summary>ผู้ใช้ที่สร้างกฎ (audit).</summary>
    public string? CreatedByName { get; set; }
}

/// <summary>
/// คิวการส่งอีเมล — รายการที่จะส่งแต่ละครั้ง. BackgroundService ดึงไปส่ง.
/// idempotency: (CompanyId, EntityType, EntityId, RuleId, ScheduledFor) unique.
/// </summary>
public class EmailQueue : TenantEntity
{
    public Guid? RuleId { get; set; }
    public EmailScheduleRule? Rule { get; set; }

    /// <summary>ชนิด entity ที่อ้างถึง: "Document", "PayrollDetail",
    /// "WithholdingTaxCert", "Employee" (รายปี).</summary>
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }

    /// <summary>email ผู้รับหลัก (resolve จาก contact/employee ตอน enqueue).</summary>
    public string ToEmail { get; set; } = "";
    public string? CcEmail { get; set; }
    public string? BccEmail { get; set; }

    /// <summary>subject + body — render template ตอน enqueue เพื่อเก็บ snapshot
    /// (template เปลี่ยนภายหลังไม่กระทบงานที่ queue ไว้).</summary>
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";

    /// <summary>แนบ PDF (ลูกค้าจะเห็น) — สำหรับ Document/WhtCert/Payslip.</summary>
    public bool AttachPdf { get; set; } = true;
    public bool AttachXml { get; set; }

    /// <summary>เวลา UTC ที่ "ถึงเวลา" ส่ง. background service ส่งเฉพาะที่
    /// ScheduledFor &lt;= now AND Status = Pending.</summary>
    public DateTime ScheduledFor { get; set; }

    /// <summary>Pending | Sending | Sent | Failed | Cancelled.</summary>
    public string Status { get; set; } = "Pending";

    public int RetryCount { get; set; }
    public DateTime? SentAt { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>idempotency key ใช้กับ unique index เพื่อกัน enqueue ซ้ำ.</summary>
    public string? IdempotencyKey { get; set; }
}

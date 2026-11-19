namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 272 แพ็ก P2 (คำตัดสินเจ้าของ 272-0-2) — ความยินยอมของ<b>บริษัทลูกค้า</b> (<see cref="TenantEntity.CompanyId"/>) ให้ระบบของสำนักงานบัญชี
/// (<see cref="ProviderCompanyId"/> = บริษัทผู้ให้บริการ NextAcc) บันทึกการนำส่งภาษีจากสลิป · คู่แฝดของ <see cref="PartnerAutomationConsent"/> ·
/// <b>append-only</b>: เปลี่ยนแบบ/บัญชี = ถอนแถวเดิม + แถวใหม่ · แก้ได้แค่ประทับถอนครั้งเดียว (trigger <c>Helpers/Round272Migration</c>) ·
/// ตัวตัดสินเดียว <c>Helpers/ProviderConsent</c> · ผู้เขียนเดียว <c>ProviderConsentService</c>
/// </summary>
public class ProviderAutomationConsent : TenantEntity
{
    public Guid ProviderCompanyId { get; set; }
    /// <summary><c>ProviderConsent.Scope</c> (ชื่อ)</summary>
    public string Scope { get; set; } = "";
    /// <summary>ชื่อแบบนำส่งที่อนุญาต (CSV เรียง · อยู่ใน hash)</summary>
    public string TaxTypes { get; set; } = "";
    /// <summary>เพดานต่อสลิป (ไม่บังคับ · 272-1-7)</summary>
    public decimal? MaxAmount { get; set; }
    /// <summary>บัญชีที่ใช้จ่ายภาษีเป็นหลัก (ไม่บังคับ · ชั้น 2 ของ <c>TaxSlipBankAccount</c>)</summary>
    public Guid? DefaultTaxPayBankAccountId { get; set; }
    /// <summary>รวมสลิปที่พักรอไว้ก่อนวันให้</summary>
    public bool IncludesHeldSlips { get; set; }
    public int TextVersion { get; set; }
    public string EvidenceHash { get; set; } = "";
    public int HashVersion { get; set; } = 1;
    /// <summary><c>web</c> = ผู้มีอำนาจของลูกค้ายืนยันบนเว็บ · <c>staff-poa</c> = หนังสือมอบอำนาจที่พนักงานแนบ (ไม่เปิดการบันทึกเอง · 272-1-6)</summary>
    public string EvidenceSource { get; set; } = "web";
    /// <summary>ไฟล์หนังสือมอบอำนาจ (เฉพาะ staff-poa)</summary>
    public Guid? EvidenceAttachmentId { get; set; }
    /// <summary>ฝ่ายค้าน 272 M4: หนังสือมอบอำนาจ — พนักงานอีกคนของผู้ให้บริการ (ไม่ใช่ผู้แนบ) อนุมัติ · ประทับครั้งเดียว (trigger) · null = ยังใช้ไม่ได้</summary>
    public Guid? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public Guid GrantedByUserId { get; set; }
    public DateTime GrantedAt { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public Guid? WithdrawnByUserId { get; set; }
    public DateTime? WithdrawnAt { get; set; }
    public string? WithdrawReason { get; set; }
}

/// <summary>รอบ 272 P2 (ทาง B · ดีไซน์ §5.4) — คำขอความยินยอมที่ระบบ/พนักงานเปิดให้บริษัทลูกค้า (<see cref="TenantEntity.CompanyId"/>) · Open ได้ 1 ใบต่อคู่ ·
/// หมดอายุ 30 วัน (สลิปที่พักหมดอายุพร้อมกัน) · ไม่มีแถวความยินยอมเกิดจากฝั่งผู้ขอ</summary>
public class ProviderConsentRequest : TenantEntity
{
    public Guid ProviderCompanyId { get; set; }
    /// <summary>Open · Granted · Expired · Cancelled</summary>
    public string Status { get; set; } = "Open";
    /// <summary>GroupBind (T1) · FirstPaymentRequest (T2) · FirstSlip (T3) · Staff (ส่งซ้ำ)</summary>
    public string RequestedVia { get; set; } = "";
    public Guid? RequestedByUserId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastCardAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? ConsentId { get; set; }
}

/// <summary>
/// รอบ 272 แพ็ก P3 (คำตัดสิน 272-1-2 = D-2) — "ใบขอชำระ": ตอน NextAcc ส่งเอกสารชำระภาษีให้ลูกค้า ระบบเก็บตัวคาดหวัง (แบบ · งวด · ยอดบนใบแจ้งชำระ · Control Code)
/// ไว้ใน<b>บริษัทลูกค้า</b> ⇒ สลิปจับคู่ด้วย Control Code ตรงตัว · Control Code เก็บเป็นแฮช HMAC + 4 ตัวท้ายเท่านั้น (<c>TaxSlipRefHash</c>)
/// </summary>
public class TaxPaymentRequest : TenantEntity
{
    public Guid ProviderCompanyId { get; set; }
    /// <summary>ผู้ติดต่อ (ในบริษัทผู้ให้บริการ) ที่ส่งใบให้</summary>
    public Guid? ProviderContactId { get; set; }
    public string RemittanceType { get; set; } = "";
    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }
    public decimal Amount { get; set; }
    public decimal Surcharge { get; set; }
    public decimal Penalty { get; set; }
    public string ControlCodeHash { get; set; } = "";
    public string ControlCodeLast4 { get; set; } = "";
    public DateTime? ControlCodeExpiresAt { get; set; }
    public DateTime? DueDate { get; set; }
    public bool IsAmendment { get; set; }
    /// <summary>ยอดในสมุดของลูกค้า ≠ ยอดบนใบแจ้งชำระตอนส่ง (ผู้ส่งติ๊ก "ส่งไปก่อน") ⇒ สลิปไม่บันทึกเอง</summary>
    public bool BooksMismatch { get; set; }
    public decimal? BooksAmountAtSend { get; set; }
    /// <summary>line-group · email · none (ส่งไม่ได้ — บอกเหตุใน <see cref="SendNote"/>)</summary>
    public string SentVia { get; set; } = "";
    public string? SendNote { get; set; }
    public DateTime SentAt { get; set; }
    public Guid? SentByUserId { get; set; }
    /// <summary>Open · Paid · Cancelled · Expired</summary>
    public string Status { get; set; } = "Open";
    public Guid? StatutoryRemittanceId { get; set; }
    public Guid? TaxSlipEvidenceId { get; set; }
}

/// <summary>
/// รอบ 272 แพ็ก P4 — สลิปชำระภาษีที่ลูกค้าส่งในกลุ่ม LINE/อีเมลของสำนักงานบัญชี · แถวอยู่ใน<b>บริษัทผู้ให้บริการ</b> (<see cref="TenantEntity.CompanyId"/>) ผูก
/// <see cref="ClientCompanyId"/> · <b>ไม่ใช้ <c>LineChatSlips</c></b> (นั่นคือสลิปที่ลูกค้าจ่ายเรา) แต่ลอกกลไกกันซ้ำ/ล้มแล้วเข้าคิวพนักงาน ·
/// ผลอ่านเก็บเฉพาะที่จำเป็น: ยอด · วันที่ · เลขภาษีแบบปิดบาง · แฮช + 4 ตัวท้ายของ Control Code/เลขที่รายการ · ไม่มีชื่อผู้โอน (PDPA ม.22)
/// </summary>
public class TaxSlipEvidence : TenantEntity
{
    public Guid? ClientCompanyId { get; set; }
    /// <summary>line · email</summary>
    public string Source { get; set; } = "";
    public string ExternalMessageId { get; set; } = "";
    /// <summary>แฮชของช่องทาง (LINE: <c>NotifyEndpointKind.LineIdHash</c> ของกลุ่ม · อีเมล: แฮชที่อยู่ผู้ส่ง)</summary>
    public string SourceKey { get; set; } = "";
    public Guid? ContactId { get; set; }
    public string FileSha256 { get; set; } = "";
    public string StoragePath { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string? Agency { get; set; }
    public string? BankCode { get; set; }
    public string? BankTxnHash { get; set; }
    public string? BankTxnLast4 { get; set; }
    public string? ControlCodeHash { get; set; }
    public string? ControlCodeLast4 { get; set; }
    public string? TaxIdMasked { get; set; }
    public decimal? Amount { get; set; }
    public decimal? Fee { get; set; }
    public DateTime? SlipDate { get; set; }
    /// <summary>Received · Remitted · AwaitingConsent · AwaitingChoice · ToStaff · Rejected · Expired</summary>
    public string Status { get; set; } = "Received";
    public string? RuleCode { get; set; }
    public string? Reason { get; set; }
    /// <summary>ผู้สมัครที่ตัวตัดสินคืน (JSON · ปุ่มเลือก/คิวพนักงาน)</summary>
    public string? CandidatesJson { get; set; }
    public DateTime? ChoiceExpiresAt { get; set; }
    /// <summary>บัญชีที่ preselect ให้พนักงาน (CSV ของ BankAccountId ในบริษัทลูกค้า)</summary>
    public string? PreselectAccounts { get; set; }
    public Guid? TaxPaymentRequestId { get; set; }
    public Guid? StatutoryRemittanceId { get; set; }
    public Guid? ClientAttachmentId { get; set; }
    public bool ShowAmounts { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public DateTime? ResolvedAt { get; set; }
    /// <summary>ข้อความการ์ดล่าสุดที่ส่งกลับ + ผลการส่ง (ห้ามเงียบ — การ์ดส่งไม่ได้ ⇒ เห็นบนแถว)</summary>
    public string? LastCardText { get; set; }
    public string? LastCardOutcome { get; set; }
    public DateTime? LastCardAt { get; set; }
    /// <summary>ฝ่ายค้าน 272 H3: อีเมลที่ผู้ส่งยืนยันโดเมนของ From ไม่ได้ (DKIM/SPF ไม่ align · ไม่มี DMARC) ⇒ ไม่บันทึกเองทุกกรณี (คิวพนักงานเท่านั้น)</summary>
    public bool SenderUnverified { get; set; }
    /// <summary>ฝ่ายค้าน 272 M6: ลบไฟล์สลิปเมื่อพ้นระยะเก็บแล้ว (ไฟล์ของแถวที่บันทึกแล้วย้ายเป็นหลักฐานของลูกค้า · แถวที่ยังไม่ตัดสินไม่ถูกลบ)</summary>
    public DateTime? FilePurgedAt { get; set; }
}

namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 264 P4 (team-264-a2b-design §6.1/§6.2 · คำตัดสิน 264-5-11/264-5-12) — <b>ผู้ซื้อ (B) เป็นผู้เริ่มจ่าย</b> ใบซื้อที่มาจากคู่ค้า NextAcc ·
/// <see cref="TenantEntity.CompanyId"/> = <b>บริษัทผู้ซื้อ</b> (ผู้กระทำ) · หนึ่งแถวต่อการกด (ปุ่ม "จ่ายจากระบบของเรา" หรือ "แจ้งโอนให้ผู้ขาย")
/// <list type="bullet">
/// <item><b>ไม่ใช่สถานะ "ชำระแล้ว"</b> (F2 ข้อ 3): แถวนี้คือ "ประกาศของคนก่อน/ตอนจ่าย" (บัญชีที่เลือก · วิธี · ผู้เลือก) — การรับเงินฝั่งผู้ขายเกิดเมื่อผู้ให้บริการ/ผู้ขายยืนยันจริง
/// (การแจ้ง <c>PartnerPaymentNotice</c> ที่ตามมา) · ผู้อ่าน: <c>PartnerPayablePipeline.SourceDecisionAsync</c> (ชั้น PayerDeclared) · ตัวปิดการแจ้งด้วย PV ที่บันทึกไว้แล้ว</item>
/// <item>กุญแจข้ามบริษัท = <see cref="SellerIntentId"/> (รายการชำระของผู้ขาย — ออนไลน์ หรือสลิปรอยืนยัน) · unique ต่อบริษัท</item>
/// </list>
/// </summary>
public class PartnerPaymentPlan : TenantEntity
{
    public Guid TradingPartnershipId { get; set; }
    /// <summary>ลิงก์ InvoiceFlow ที่ใบปลายทางคือใบซื้อนี้</summary>
    public Guid LinkId { get; set; }
    /// <summary>ใบซื้อของผู้ซื้อ</summary>
    public Guid PurchaseDocumentId { get; set; }
    /// <summary>บริษัทผู้ขาย + เอกสารของผู้ขาย (ใบแจ้งหนี้)</summary>
    public Guid SourceCompanyId { get; set; }
    public Guid SourceDocumentId { get; set; }
    /// <summary><c>Gateway</c> (A2 — จ่ายผ่านช่องทางออนไลน์ของผู้ขาย) · <c>Transfer</c> (A7 — แจ้งโอน + สลิป) — ค่าใน <c>Helpers/PartnerPaymentPlanRule</c></summary>
    public string Kind { get; set; } = "";
    /// <summary>บัญชีของผู้ซื้อที่เงินออก (ผู้เลือก/สลิปตรงบัญชีเดียว — ผ่านชุดบัญชีที่ใช้จ่ายได้ของบริษัท)</summary>
    public Guid BankAccountId { get; set; }
    /// <summary>วิธีชำระ (ชื่อ <c>PaymentMethodKind</c> สำหรับ Gateway · ชื่อ <c>PaymentMethod</c> สำหรับ Transfer)</summary>
    public string Method { get; set; } = "";
    /// <summary>เงินที่ออกจากผู้ซื้อ (ยอดของรายการชำระของผู้ขาย / ยอดที่ผู้แจ้งยืนยัน)</summary>
    public decimal Amount { get; set; }
    public Guid ChosenByUserId { get; set; }
    /// <summary>รายการชำระในบริษัทผู้ขาย (ออนไลน์ หรือ ManualSlip รอยืนยัน) · null = ยังไม่ได้เปิด (Transfer ที่ผู้ขายไม่รับสำเนาสลิป)</summary>
    public Guid? SellerIntentId { get; set; }
    /// <summary>Transfer: การจ่าย (PV) ของผู้ซื้อที่คนแนบสลิปบันทึกเอง · Gateway = null (บันทึกเมื่อการแจ้งมาถึง)</summary>
    public Guid? BuyerPaymentId { get; set; }
    /// <summary>Transfer: วันที่โอนตามที่ผู้แจ้งยืนยัน (UTC)</summary>
    public DateTime? TransferredAt { get; set; }
    /// <summary>ผลส่งสำเนาสลิปเข้าคิวของผู้ขาย (ข้อความไทย — "ส่งแล้ว" หรือเหตุที่ไม่ได้ส่ง) · ห้ามว่างเงียบ</summary>
    public string? SlipCopyNote { get; set; }
    /// <summary>ที่มาของบัญชี (ชื่อ <c>PartnerPaySourceAccount.Origin</c>)</summary>
    public string? AccountOrigin { get; set; }

    // ═══ ฝ่ายค้าน 264-P4 H1/M2 (คำตัดสิน 264-5-15) — แจ้งโอน: กุญแจกันซ้ำ + outbox สำเนาสลิป ═══

    /// <summary>Transfer: <c>PartnerPaymentPlanRule.TransferIdempotencyKey</c> (เอกสารผู้ขาย · แฮชสลิป · ยอด) · unique ต่อบริษัทในแถวที่ไม่ถูกลบ · Gateway = null</summary>
    public string? IdempotencyKey { get; set; }
    /// <summary>Transfer: SHA-256 ของไฟล์สลิปต้นฉบับ (ตัวเดียวกับ <c>SlipEvidenceRule.Sha256Hex</c>)</summary>
    public string? SlipSha256 { get; set; }
    /// <summary>Transfer: ที่เก็บไฟล์สลิปของเรา (ที่เก็บกลาง · ไม่เสิร์ฟตรง) — outbox อ่านไฟล์นี้ตอนส่งสำเนาให้ผู้ขาย</summary>
    public string? SlipStoragePath { get; set; }
    public string? SlipContentType { get; set; }
    /// <summary>Transfer: เลขอ้างอิงการโอน (ผู้แจ้งกรอก/อ่านจากสลิป)</summary>
    public string? SlipBankRef { get; set; }
    /// <summary>Transfer: สถานะ outbox สำเนาสลิป (<c>Pending/Sent/Skipped/Failed</c> — ค่าใน <c>PartnerPaymentPlanRule</c>)</summary>
    public string? SlipCopyState { get; set; }
    public int SlipCopyAttempts { get; set; }
    /// <summary>ฝ่ายค้าน M7: แจ้งผู้ขาย (กระดิ่ง) ว่าต้องให้/ยืนยันความยินยอมรับสลิปเมื่อไร — กันแจ้งซ้ำต่อคู่ค้า</summary>
    public DateTime? SellerNotifiedAt { get; set; }
}

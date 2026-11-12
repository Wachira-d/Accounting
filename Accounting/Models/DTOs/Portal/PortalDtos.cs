namespace Accounting.Models.DTOs.Portal;

public record CreatePortalAccessRequest(Guid ContactId, string Email, string? Password, string? DisplayName, bool CanViewInvoices, bool CanViewStatements, bool CanDownloadPdf, bool CanMakePayment);
public record UpdatePortalAccessRequest(bool? CanViewInvoices, bool? CanViewStatements, bool? CanDownloadPdf, bool? CanMakePayment, bool? IsActive);
/// <param name="UnappliedCredit">รอบ 260-3: เงินรอจัดสรรคงเหลือของผู้ติดต่อ (ตัวอ่านเดียว <c>CustomerCreditQuery</c>) — หน้าพนักงานแสดงอย่างเดียว</param>
/// <param name="UnappliedCreditShownToCustomer">ลูกค้าเห็นยอดนี้ในพอร์ทัลไหม (<c>PortalAccessPermissions.CreditShownToCustomer</c> — ด่านเดียวกับเส้นใบแจ้งยอด)</param>
public record PortalAccessResponse(Guid Id, Guid ContactId, string ContactName, string Email, string? DisplayName, bool IsActive, DateTime? LastLoginAt, bool CanViewInvoices, bool CanViewStatements, bool CanDownloadPdf, bool CanMakePayment,
    decimal UnappliedCredit = 0m, bool UnappliedCreditShownToCustomer = false);

public record PortalLoginRequest(string Email, string Password, Guid CompanyId);
public record PortalLoginResponse(string AccessToken, string RefreshToken, Guid ContactId, string ContactName, Guid CompanyId, string CompanyName);
public record PortalRefreshRequest(string RefreshToken);

/// <param name="SlipPendingAmount">รอบ 250 ฝ่ายค้าน L4: ยอดสลิปที่ลูกค้าแนบแล้วรอผู้ขายตรวจ (เซิร์ฟเวอร์รวมจากรายการรอยืนยัน) · 0 = ไม่มี — ไม่ลดยอดค้างจนผู้ขายยืนยัน</param>
/// <summary><c>CanPay</c> — รอบ 250 WP-03: พอร์ทัลแสดงปุ่มชำระตามค่านี้ (เซิร์ฟเวอร์ตัดสินด้วย <c>DocumentPayLinkPolicy.IssueRefusal</c>)</summary>
public record PortalDocumentResponse(Guid Id, string DocumentNumber, string DocumentType, DateTime DocumentDate, DateTime? DueDate, decimal TotalAmount, decimal PaidAmount, decimal BalanceDue, string Status, decimal SlipPendingAmount = 0m, bool CanPay = false,
    /// <summary>รอบ 257 ฝ่ายค้าน L8: ธงดาวน์โหลด PDF ของสิทธิ์ในโทเคน (เซิร์ฟเวอร์ตัดสินด้วยตาราง PortalAccessPermissions.Route("pdf")) — หน้าซ่อนปุ่ม PDF เมื่อ false</summary>
    bool CanDownloadPdf = false)
{
    /// <summary>รอบ 259-3 (E 4.2): ป้าย/เกินกำหนดจากตัวตั้งเดียว <c>PortalDocumentStatus</c> (เวลาไทยของเซิร์ฟเวอร์) — หน้าแสดงอย่างเดียว</summary>
    public bool IsOverdue => global::Accounting.Helpers.PortalDocumentStatus.IsOverdue(Status, BalanceDue, DueDate, DateTime.UtcNow);
    public string StatusLabel => global::Accounting.Helpers.PortalDocumentStatus.Label(Status, BalanceDue, DueDate, DateTime.UtcNow);
}
/// <param name="UnappliedCredit">รอบ 260-3 (ต่อ 259-1-9): เงินรอจัดสรรของ<b>ผู้ติดต่อในโทเคน</b> ณ ปัจจุบัน (รับแล้วยังไม่ผูกกับใบ) — บรรทัดแยก
/// <b>ไม่หัก</b>จากยอดคงเหลือ (เหมือนใบแจ้งยอดฝั่งพนักงาน) · อยู่ใต้ธง "ดูใบแจ้งยอด" ของเส้นนี้ · 0 = ไม่มี</param>
public record PortalStatementResponse(DateTime FromDate, DateTime ToDate, decimal OpeningBalance, decimal TotalCharged, decimal TotalPaid, decimal ClosingBalance, List<PortalStatementLine> Lines,
    decimal UnappliedCredit = 0m);
public record PortalStatementLine(DateTime Date, string DocumentNumber, string Description, decimal Amount, decimal Balance);
public record PortalPaymentResponse(Guid Id, DateTime PaymentDate, decimal Amount, string PaymentMethod, string? Reference, string DocumentNumber)
{
    /// <summary>รอบ 252 WP-13 J: ชื่อช่องทางภาษาไทยจากตารางเดียว (<c>PaymentMethodNames</c>) — พอร์ทัลเคยแสดง enum ดิบ "BankTransfer"</summary>
    public string PaymentMethodLabel => Enum.TryParse<global::Accounting.Models.Enums.PaymentMethod>(PaymentMethod, out var m)
        ? global::Accounting.Helpers.PaymentMethodNames.Thai(m) : PaymentMethod;
}

/// <summary>รอบ 259-3 (E-M1): <paramref name="Url"/> = หน้าชำระของเอกสาร (null = ออกไม่ได้) · <paramref name="Refusal"/> = เหตุ ·
/// <paramref name="SlipFallback"/> = ใบนี้ชำระได้ แต่บริษัทยังไม่มีช่องทางออนไลน์ ⇒ ใช้กล่องโอน/สลิปของพอร์ทัล (ยอดจากเซิร์ฟเวอร์)</summary>
public record PortalPayLinkResponse(string? Url, string? Refusal, bool SlipFallback);

/// <summary>ผลการแนบสลิปพอร์ทัล — รอบ 250 WP-02b (M-2): ไม่ใช่การรับชำระ · <paramref name="PaymentIntentId"/> = รายการรอยืนยันของผู้ขาย
/// (เดิม <c>PaymentId</c> ของแถว Payment ที่สร้างก่อนตรวจ) · <paramref name="AwaitingReview"/> = รอพนักงานตรวจสลิปเสมอ</summary>
public record PortalSlipUploadResponse(Guid PaymentIntentId, Guid DocumentId, string SlipUrl, decimal Amount, DateTime UploadedAt, bool AwaitingReview = true);

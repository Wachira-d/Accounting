using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs.Portal;
using Microsoft.AspNetCore.Http;

namespace Accounting.Services.Interfaces;

public interface IPortalService
{
    // Portal access management (by company)
    Task<PortalAccessResponse> CreateAccessAsync(Guid companyId, CreatePortalAccessRequest request);
    Task<List<PortalAccessResponse>> GetAccessesAsync(Guid companyId);
    Task<PortalAccessResponse> UpdateAccessAsync(Guid companyId, Guid accessId, UpdatePortalAccessRequest request);
    Task DeactivateAccessAsync(Guid companyId, Guid accessId);

    // Portal auth (by customer/supplier)
    Task<PortalLoginResponse> LoginAsync(PortalLoginRequest request);
    Task<PortalLoginResponse> RefreshTokenAsync(string refreshToken);

    Guid? ExtractContactIdFromToken(string token);

    // Portal data (by customer/supplier)
    Task<List<PortalDocumentResponse>> GetMyDocumentsAsync(Guid companyId, Guid contactId, string? documentType = null);
    Task<PortalDocumentResponse> GetDocumentAsync(Guid companyId, Guid contactId, Guid documentId);
    Task<byte[]> DownloadDocumentPdfAsync(Guid companyId, Guid contactId, Guid documentId);
    Task<PortalStatementResponse> GetMyStatementAsync(Guid companyId, Guid contactId, DateTime fromDate, DateTime toDate);
    Task<List<PortalPaymentResponse>> GetMyPaymentsAsync(Guid companyId, Guid contactId);

    // Customer-self-service payments
    /// <summary>Surface the company's first active PromptPay / bank
    /// gateway so the portal can render QR + account info for the
    /// customer to pay an outstanding invoice.</summary>
    Task<StorefrontPaymentOptions> GetCompanyPaymentOptionsAsync(Guid companyId);
    /// <summary>รอบ 250 WP-03 (A G-01): ตัวเลือกชำระของ<b>ใบนี้</b> (เจ้าของใบต้องเป็นผู้ติดต่อของโทเค็น) + QR พร้อมเพย์ที่เซิร์ฟเวอร์สร้าง
    /// ฝังยอดค้างของใบ (<c>PromptPayQrDataUri</c>) — แทนการให้หน้าเว็บส่งเลขพร้อมเพย์+ยอดไปบริการ QR ภายนอก</summary>
    Task<StorefrontPaymentOptions> GetDocumentPaymentOptionsAsync(Guid companyId, Guid contactId, Guid documentId, CancellationToken ct = default);
    /// <summary>ลูกค้าแนบสลิปของใบแจ้งหนี้ — สร้าง <c>PaymentIntent(ManualSlip)</c> รอตรวจ (ไม่ใช่รับชำระ) · รอบ 252 WP-12A: กันสลิปซ้ำด้วยแฮชไฟล์ +
    /// เลขอ้างอิงโอน (<paramref name="bankRef"/> ที่ลูกค้ากรอก · ไม่บังคับ) · แจ้งทีม E-06</summary>
    /// <summary>รอบ 255 WP-12B ข้อ 8: ขอลิงก์วิเศษทางอีเมล (ผู้เรียกตอบข้อความเดียวเสมอ) · true = ส่งแล้ว</summary>
    Task<bool> RequestMagicLinkAsync(Guid companyId, string? email, CancellationToken ct = default);
    /// <summary>รอบ 255 WP-12B ข้อ 8: แลกลิงก์วิเศษเป็นเซสชันพอร์ทัล (ใช้ครั้งเดียว)</summary>
    Task<PortalLoginResponse> RedeemMagicLinkAsync(Guid companyId, string? token, CancellationToken ct = default);
    /// <summary>รอบ 255 WP-12B ข้อ 8: Excel รูปแบบทั่วไปให้นักบัญชีของลูกค้า</summary>
    Task<byte[]> ExportAccountantLedgerAsync(Guid companyId, Guid contactId, DateTime fromDate, DateTime toDate, CancellationToken ct = default);
    Task<PortalSlipUploadResponse> UploadDocumentSlipAsync(
        Guid companyId, Guid contactId, Guid documentId, IFormFile file, decimal? amount, string? bankRef = null);
}

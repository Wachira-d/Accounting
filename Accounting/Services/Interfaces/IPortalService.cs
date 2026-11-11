using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs.Portal;
using Microsoft.AspNetCore.Http;

namespace Accounting.Services.Interfaces;

public interface IPortalService
{
    // Portal access management (by company)
    // รอบ 257 D-H1: ผู้ทำ (actorUserId) บังคับ — ทุกการให้/แก้/ปิดสิทธิ์ลง audit chain
    Task<PortalAccessResponse> CreateAccessAsync(Guid companyId, CreatePortalAccessRequest request, Guid actorUserId);
    Task<List<PortalAccessResponse>> GetAccessesAsync(Guid companyId);
    Task<PortalAccessResponse> UpdateAccessAsync(Guid companyId, Guid accessId, UpdatePortalAccessRequest request, Guid actorUserId);
    Task DeactivateAccessAsync(Guid companyId, Guid accessId, Guid actorUserId);
    /// <summary>รอบ 257 D-H3: ธงสิทธิ์ของสิทธิ์พอร์ทัลในโทเคน (บริษัทนี้ · ยังเปิด · ไม่ถูกลบ) · ไม่พบ = null (ผู้เรียกปฏิเสธ)</summary>
    Task<Accounting.Helpers.PortalAccessFlags?> GetAccessFlagsAsync(Guid companyId, Guid portalAccessId, CancellationToken ct = default);

    // Portal auth (by customer/supplier)
    Task<PortalLoginResponse> LoginAsync(PortalLoginRequest request);
    Task<PortalLoginResponse> RefreshTokenAsync(string refreshToken);

    Guid? ExtractContactIdFromToken(string token);
    /// <summary>รอบ 255 WP-12B ฝ่ายค้าน H1/M9: ตัวตรวจของ scheme "Portal" (access เท่านั้น · สิทธิ์ยังเปิด · บริษัทตรงเส้นทาง) · ไม่ผ่าน = null</summary>
    Task<PortalTokenPrincipal?> AuthenticateAccessAsync(string token, Guid? routeCompanyId, CancellationToken ct = default);

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
    /// <summary>รอบ 259-3 (E-M1 · คำตัดสิน 259-3-2): ปุ่ม "ชำระ" ในพอร์ทัล = หน้าชำระของเอกสาร (pay-document · ช่องทางออนไลน์ + โอน/สลิป + ไม่หัก ณ ที่จ่าย
    /// ตัวเดียวกับลิงก์ในอีเมล) — ออก/ใช้ลิงก์เดิมของใบผ่าน <c>IDocumentPayLinkService.EnsureLinkAsync</c> · บริษัทไม่มีช่องทางออนไลน์ ⇒ <c>Url</c> null +
    /// เหตุ + <c>SlipFallback</c> (พอร์ทัลแสดงกล่องโอน/สลิปด้วยยอดจากเซิร์ฟเวอร์) · ใบที่ลูกค้าไม่เห็น = <see cref="KeyNotFoundException"/></summary>
    Task<PortalPayLinkResponse> GetDocumentPayLinkAsync(Guid companyId, Guid contactId, Guid documentId, CancellationToken ct = default);
}

/// <summary>รอบ 255 WP-12B: ตัวตนจากโทเคนพอร์ทัลที่ผ่านการตรวจ</summary>
public sealed record PortalTokenPrincipal(Guid PortalAccessId, Guid ContactId, Guid CompanyId);

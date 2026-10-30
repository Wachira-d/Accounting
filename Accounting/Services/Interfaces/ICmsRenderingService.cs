using Accounting.Models.DTOs.Cms;

namespace Accounting.Services.Interfaces;

public interface ICmsRenderingService
{
    Task<string> GenerateThemeCssAsync(Guid companyId, Guid siteId);
    Task<RenderedPageResponse?> RenderPageAsync(Guid companyId, Guid siteId, string slug, string? languageCode = null);
    Task<StorefrontDataResponse> GetStorefrontDataAsync(Guid companyId, Guid siteId, string? languageCode = null);
    /// <summary>รอบ 214: หน้านโยบาย/ข้อกำหนดของร้าน (สาธารณะ · เว็บที่เผยแพร่แล้ว) — null = ไม่พบ</summary>
    Task<StorefrontLegalPageResponse?> GetLegalPageAsync(Guid companyId, Guid siteId, string kind);
    /// <summary>รอบ 214: ตัวอย่างจากข้อความที่กำลังแก้ (หลังบ้าน) — null = ไม่พบเว็บ/ชนิด</summary>
    Task<(string Html, string Markdown, List<string> Warnings)?> PreviewLegalPageAsync(Guid companyId, Guid siteId, string kind, string? markdown);
    Task<string> RenderBlockHtmlAsync(Guid companyId, Guid siteId, Guid blockId, string? languageCode = null);
    /// <summary>R-44/R-47 — แท็ก &lt;head&gt; (title/description/og/canonical/JSON-LD) ที่เซิร์ฟเวอร์เรนเดอร์ให้ crawler · "" = ไม่พบเว็บ</summary>
    Task<string> BuildSeoHeadAsync(Guid companyId, Guid siteId, string? slug, string origin, string requestPath, string? languageCode = null);
}

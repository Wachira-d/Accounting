using Accounting.Helpers;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 209 P3 (R-209-D5/E2 · คำตัดสินข้อ 168 ซ) — แจ้งร้านเมื่อมี lead ใหม่ และเมื่อลูกค้ากดยอมรับใบเสนอราคาออนไลน์ (+ lead ที่ผูกใบนั้น ⇒ ได้งาน) ·
/// ส่งผ่าน <see cref="Interfaces.ICmsShopNotifier"/> (ช่องเดิม) · ส่งไม่ถึง ⇒ ประทับ <see cref="CmsNotificationText.FailureTag"/> บนหมายเหตุภายในของ lead (ไม่เงียบ)
/// </summary>
public partial class CmsLeadService
{
    private async Task NotifyShopNewLeadAsync(CmsLead lead)
    {
        if (_shopNotifier == null) return;
        try
        {
            var site = await _db.Sites.AsNoTracking().Where(s => s.Id == lead.SiteId && s.CompanyId == lead.CompanyId)
                .Select(s => s.Name).FirstOrDefaultAsync() ?? "";
            var msg = CmsNotificationText.ShopNewLead(site, lead.LeadNumber, lead.LeadType, lead.CustomerName, lead.CustomerEmail, lead.CustomerPhone, lead.Message);
            var err = await _shopNotifier.NotifyShopAsync(lead.CompanyId, lead.SiteId, msg, orderEvent: false);
            if (err == null) return;
            lead.InternalNotes = DepositPolicyResolver.AppendNoteOnce(lead.InternalNotes, CmsNotificationText.FailureNote("ร้าน", msg.Subject, err, DateTime.UtcNow));
            await _db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // lead บันทึกแล้ว — ฟอร์มของลูกค้าต้องสำเร็จ · ล้มดังที่บันทึกระบบระดับ Error
            _logger?.LogError(ex, "แจ้งร้านเรื่อง lead ใหม่ {LeadNumber} ล้ม (company {CompanyId})", lead.LeadNumber, lead.CompanyId);
        }
    }

    /// <summary>
    /// ลูกค้ายอมรับใบเสนอราคาทางลิงก์ (<c>PublicQuotationController.Accept</c>) — lead ที่ผูกใบนี้ (<c>ErpDocumentId</c>) และยังไม่ปิด ⇒ ได้งาน (Won) ·
    /// แจ้งร้าน (เว็บของ lead · ไม่มี lead = อีเมลบริษัท) · ไม่โยน — การยอมรับบันทึกแล้ว
    /// </summary>
    public async Task OnQuotationAcceptedAsync(Guid companyId, Guid documentId, string documentNumber, string acceptedBy, decimal total)
    {
        try
        {
            var lead = await _db.Set<CmsLead>()
                .Where(l => l.CompanyId == companyId && l.ErpDocumentId == documentId && !l.IsDeleted)
                .OrderByDescending(l => l.CreatedAt)
                .FirstOrDefaultAsync();
            var now = DateTime.UtcNow;
            string? wonLead = null;
            if (lead != null && lead.Status is not (LeadStatus.Won or LeadStatus.Lost))
            {
                lead.Status = LeadStatus.Won;
                lead.WonAt ??= now;
                lead.UpdatedAt = now;
                lead.UpdatedBy = "quotation-accept";
                lead.InternalNotes = DepositPolicyResolver.AppendNoteOnce(lead.InternalNotes,
                    $"[{now:yyyy-MM-dd HH:mm}Z] ลูกค้า ({acceptedBy.Trim()}) ยอมรับใบเสนอราคา {documentNumber} ออนไลน์ ⇒ ได้งาน");
                await _db.SaveChangesAsync();
                wonLead = lead.LeadNumber;
            }
            if (_shopNotifier == null) return;
            var msg = CmsNotificationText.ShopQuotationAccepted(documentNumber, acceptedBy, total, wonLead);
            var err = await _shopNotifier.NotifyShopAsync(companyId, lead?.SiteId, msg, orderEvent: false);
            if (err != null && lead != null)
            {
                lead.InternalNotes = DepositPolicyResolver.AppendNoteOnce(lead.InternalNotes, CmsNotificationText.FailureNote("ร้าน", msg.Subject, err, now));
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "หลังลูกค้ายอมรับใบเสนอราคา {DocNo}: ปรับ lead/แจ้งร้านล้ม (company {CompanyId})", documentNumber, companyId);
        }
    }
}

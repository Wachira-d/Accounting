using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>รอบ 259-3 (ทีม E 3ก-1 · คำตัดสิน 259-3-7) — เช็กลิสต์เริ่มต้นใช้งาน: ข้อเท็จจริงจากฐาน (นับจริง ไม่ใช่หน้าแรกของรายการ) →
/// ตัวตัดสินเดียว <see cref="GettingStartedChecklist"/> · อ่านอย่างเดียว · ทุก query ผูก CompanyId</summary>
[ApiController]
[Route("api/companies/{companyId:guid}/getting-started")]
[Authorize]
public class GettingStartedController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentPayLinkService? _payLinks;

    public GettingStartedController(AccountingDbContext db, IDocumentPayLinkService? payLinks = null)
    {
        _db = db;
        _payLinks = payLinks;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<GettingStartedChecklist.Result>>> Get(Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<GettingStartedChecklist.Result>(true, GettingStartedChecklist.Build(await FactsAsync(_db, _payLinks, companyId, ct))));

    /// <summary>รวบรวมข้อเท็จจริง (ตัวเดียว — เทสต์เรียกตรงได้)</summary>
    public static async Task<GettingStartedChecklist.Facts> FactsAsync(AccountingDbContext db, IDocumentPayLinkService? payLinks, Guid companyId, CancellationToken ct = default)
    {
        var co = await db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => new { c.Name, c.TaxId, c.Address, c.BuildingNumber, c.Province, c.IsVatRegistered, c.VatRate })
            .FirstOrDefaultAsync(ct);
        var s = await db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId, ct);
        var info = co != null && !string.IsNullOrWhiteSpace(co.Name) && !string.IsNullOrWhiteSpace(co.TaxId)
            && (!string.IsNullOrWhiteSpace(co.Address) || (!string.IsNullOrWhiteSpace(co.BuildingNumber) && !string.IsNullOrWhiteSpace(co.Province)));
        var pay = payLinks == null ? null : await payLinks.CompanyFactsAsync(companyId, ct);
        var onlineReady = pay != null && Accounting.Helpers.DocumentPayLinkPolicy.UnavailableReason(pay) == null;
        var bankAccounts = await db.BankAccounts.AsNoTracking().CountAsync(b => b.CompanyId == companyId && !b.IsDeleted, ct);
        return new GettingStartedChecklist.Facts(
            CompanyInfoComplete: info,
            FiscalPeriods: await db.FiscalPeriods.AsNoTracking().CountAsync(p => p.CompanyId == companyId, ct),
            Accounts: await db.ChartOfAccounts.AsNoTracking().CountAsync(a => a.CompanyId == companyId && !a.IsDeleted, ct),
            VatRegistered: co?.IsVatRegistered ?? false,
            VatRate: co?.VatRate ?? 0m,
            BankAccounts: bankAccounts,
            Contacts: await db.Contacts.AsNoTracking().CountAsync(c => c.CompanyId == companyId && !c.IsDeleted, ct),
            Products: await db.Products.AsNoTracking().CountAsync(p => p.CompanyId == companyId && !p.IsDeleted, ct),
            Documents: await db.Documents.AsNoTracking().CountAsync(d => d.CompanyId == companyId && !d.IsDeleted, ct),
            // ตัวตัดสินเดียวกับค่าตั้งส่งเอกสารอัตโนมัติ ("ทดสอบผ่าน" + ผู้ส่งของบริษัทครบ)
            SenderEmailVerified: s != null && s.EmailConfigured && Accounting.Services.Implementations.Email.EmailSenderFactory.HasTenantSender(s),
            OnlinePayReady: onlineReady,
            TransferReady: (pay?.BankTransfer ?? false) || await GettingStartedChecklist.TransferAccounts(db.BankAccounts.AsNoTracking(), companyId).AnyAsync(ct),
            LineConfigured: s?.LineConfigured ?? false);
    }
}

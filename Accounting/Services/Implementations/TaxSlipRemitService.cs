using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 272 แพ็ก P4 — <b>ผู้เขียนตัวเดียว</b>ของการบันทึกนำส่งจากสลิปภาษีลงบัญชีบริษัทลูกค้า (ดีไซน์ §4.1 · คำตัดสินเจ้าของ 272-0-1/272-0-2):
/// <list type="number">
/// <item>ธุรกรรมเดียว · ล็อกแถวสลิป + ใบขอชำระ (FOR UPDATE) + <c>pg_advisory_xact_lock(StatutoryRemit, type:year)</c> ของบริษัทลูกค้า</item>
/// <item>ตรวจความยินยอม<b>ใต้ล็อก</b> (<see cref="ProviderConsentService.CheckForWriteAsync"/>) ก่อน <c>RemitAsync</c> — ถอนระหว่างทาง ⇒ ไม่บันทึก</item>
/// <item>บันทึกผ่าน <see cref="IStatutoryRemittanceService.RemitAsync"/> <b>ตัวเดียว</b> (JE + แถวนำส่ง + ด่าน 50 ทวิ/ยอดค้าง/กันซ้ำงวดของเส้นมือ · ไม่สร้าง JE/แถวเอง)</item>
/// <item>แนบสลิปเป็นหลักฐานในบริษัทลูกค้า · ประทับที่มา (<c>Source=Slip</c> · ผู้ทำแทน · ความยินยอม) · audit chain ทั้งสองบริษัท</item>
/// </list>
/// ด่านใดไม่ผ่าน / RemitAsync ปฏิเสธ ⇒ <see cref="TaxSlipGateException"/> หรือ exception เดิมขึ้นไปให้ผู้เรียกพาแถวเข้าคิวพนักงานพร้อมเหตุจริง (ไม่กลืน · กฎ #4 E)
/// </summary>
public class TaxSlipRemitService
{
    public const string ActorAuto = "provider-slip:auto";

    private readonly AccountingDbContext _db;
    private readonly IStatutoryRemittanceService _remit;
    private readonly ProviderConsentService _consent;
    private readonly IFileAttachmentService _files;
    private readonly Accounting.Services.Storage.IFileStorage _storage;
    private readonly ILogger<TaxSlipRemitService> _logger;
    private readonly INotificationEngine? _notify;

    public TaxSlipRemitService(AccountingDbContext db, IStatutoryRemittanceService remit, ProviderConsentService consent, IFileAttachmentService files,
        Accounting.Services.Storage.IFileStorage storage, ILogger<TaxSlipRemitService> logger, INotificationEngine? notify = null)
    { _db = db; _remit = remit; _consent = consent; _files = files; _storage = storage; _logger = logger; _notify = notify; }

    /// <param name="StaffUserId">null = ระบบบันทึกเอง (ต้องมีความยินยอมบนเว็บที่ใช้ได้) · มีค่า = พนักงานผู้ให้บริการกด 1 คลิก</param>
    /// <param name="StaffIsClientTaxFiler">พนักงานเป็นสมาชิกบริษัทลูกค้าที่มีสิทธิ์ยื่นภาษีเอง (ไม่ต้องพึ่งความยินยอม)</param>
    public sealed record ApplyRequest(Guid ProviderId, Guid SlipId, TaxSlipMatch.Candidate Candidate, Guid? BankAccountId, Guid? BankGlAccountId,
        Guid? StaffUserId, bool StaffIsClientTaxFiler, DateTime PayDate, string? AccountLabel);

    public sealed record Applied(RemitResult Result, Guid? ConsentId, Guid AttachmentId);

    /// <summary>ด่านของเส้นสลิปที่ไม่ผ่าน (ความยินยอม · สถานะแถว · ใบขอชำระ) — ผู้เรียกพาแถวเข้าคิวพนักงานพร้อม <see cref="RuleCode"/></summary>
    public sealed class TaxSlipGateException : Exception
    {
        public string RuleCode { get; }
        public TaxSlipGateException(string message, string ruleCode) : base(message) => RuleCode = ruleCode;
    }

    public async Task<Applied> ApplyAsync(ApplyRequest a, CancellationToken ct = default)
    {
        var c = a.Candidate;
        if (a.BankAccountId == null && a.BankGlAccountId == null)
            throw new TaxSlipGateException("ยังไม่รู้บัญชีที่จ่าย — ไม่บันทึก (ระบบไม่ถอยไปบัญชีตั้งต้น)", TaxSlipBankAccount.RuleCode);
        var total = c.Amount + c.Surcharge + c.Penalty;
        Applied done; string storagePath;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"TaxSlipEvidences\" WHERE \"Id\" = {a.SlipId} AND \"CompanyId\" = {a.ProviderId} FOR UPDATE", ct);
            var slip = await _db.TaxSlipEvidences.FirstOrDefaultAsync(s => s.Id == a.SlipId && s.CompanyId == a.ProviderId, ct)
                ?? throw new TaxSlipGateException("ไม่พบแถวสลิป", TaxSlipMatch.Codes.None);
            if (slip.Status == TaxSlipIntakeService.Statuses.Remitted)
                throw new TaxSlipGateException("สลิปนี้บันทึกไปแล้ว", TaxSlipMatch.Codes.Duplicate);
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(c.CompanyId, AdvisoryLockKey.StatutoryRemit, $"{c.Type}:{c.Year}") }, ct);
            TaxPaymentRequest? req = null;
            if (c.RequestId is Guid rid)
            {
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"TaxPaymentRequests\" WHERE \"Id\" = {rid} AND \"CompanyId\" = {c.CompanyId} FOR UPDATE", ct);
                req = await _db.TaxPaymentRequests.FirstOrDefaultAsync(r => r.Id == rid && r.CompanyId == c.CompanyId, ct);
                if (req == null || req.Status != "Open")
                    throw new TaxSlipGateException("ใบขอชำระไม่ได้เปิดอยู่แล้ว (ถูกยกเลิก/ชำระแล้วระหว่างนี้)", TaxSlipMatch.Codes.RequestClosed);
            }
            // ── ความยินยอมใต้ล็อก (272-0-2) — ถอนแทรกไม่ได้จน commit ──
            var verdict = await _consent.CheckForWriteAsync(c.CompanyId, a.ProviderId, c.Type, total, ct);
            if (a.StaffUserId == null && !verdict.Ok)
                throw new TaxSlipGateException(verdict.Reason, ProviderConsent.RuleCode + "-" + verdict.Code);
            if (a.StaffUserId != null && !verdict.AllowsStaffClick && !a.StaffIsClientTaxFiler)
                throw new TaxSlipGateException("บริษัทลูกค้ายังไม่อนุญาต/ไม่มีหนังสือมอบอำนาจ และผู้กดไม่ได้เป็นผู้มีสิทธิ์ยื่นภาษีของบริษัทลูกค้า — "
                    + verdict.Reason, ProviderConsent.RuleCode + "-" + verdict.Code);
            var consentId = verdict.Ok || verdict.AllowsStaffClick ? verdict.ConsentId : null;

            var actor = a.StaffUserId is Guid su ? "provider-slip:staff:" + su.ToString("N") : ActorAuto;
            var note = "บันทึกจากสลิป" + (slip.ControlCodeLast4 != null ? " · Control Code ••" + slip.ControlCodeLast4 : "")
                + (slip.BankTxnLast4 != null ? " · Txn ••" + slip.BankTxnLast4 : "") + " · โดย NextAcc" + (consentId is Guid cg ? " (ความยินยอม " + cg.ToString("N")[..8] + ")" : "");
            // ── ผู้เขียนตัวเดียว (ห้ามสร้าง JE/แถวนำส่งเอง) ──
            var result = await _remit.RemitAsync(c.CompanyId, new RemitRequest(c.Type, c.Year, c.Month, a.PayDate,
                BankAccountId: a.BankAccountId, BankGlAccountId: a.BankGlAccountId, FilingNumber: null, ReceiptAttachmentId: null,
                IncludeLateFee: true, Note: note, LateSurcharge: c.Surcharge, Penalty: c.Penalty), actor);

            var bytes = await _storage.ReadAllBytesAsync(slip.StoragePath, ct)
                ?? throw new TaxSlipGateException("อ่านไฟล์สลิปจากที่เก็บไม่ได้ — แนบหลักฐานไม่ได้ จึงไม่บันทึก", "TAX-SLIP-FILE");
            var uploader = a.StaffUserId ?? (await _db.ProviderAutomationConsents.AsNoTracking().Where(x => x.Id == consentId).Select(x => (Guid?)x.GrantedByUserId).FirstOrDefaultAsync(ct))
                ?? throw new TaxSlipGateException("ไม่รู้ผู้รับผิดชอบการแนบหลักฐาน", ProviderConsent.RuleCode);
            var ext = SlipEvidenceRule.ExtensionFor(slip.ContentType) ?? ".bin";
            var att = await _files.UploadBytesAsync(c.CompanyId, "StatutoryRemittance", result.Id, "tax-slip-" + slip.Id.ToString("N")[..8] + ext, slip.ContentType, bytes, uploader);

            var rec = await _db.StatutoryRemittances.FirstAsync(r => r.Id == result.Id && r.CompanyId == c.CompanyId, ct);
            rec.ReceiptAttachmentId = att.Id;
            rec.Source = TaxSlipRemitView.SourceSlip;
            rec.SlipEvidenceId = slip.Id;
            rec.ActedByProviderCompanyId = a.ProviderId;
            rec.ProviderConsentId = consentId;
            rec.ControlCodeLast4 = slip.ControlCodeLast4;
            rec.UpdatedAt = DateTime.UtcNow;
            if (req != null) { req.Status = "Paid"; req.StatutoryRemittanceId = rec.Id; req.TaxSlipEvidenceId = slip.Id; req.UpdatedAt = DateTime.UtcNow; }
            // ทุกการเขียนลงบัญชีลูกค้าเข้า audit chain ทั้งสองบริษัท
            var payload = JsonSerializer.Serialize(new
            {
                @event = "TaxSlipRemitted", slipId = slip.Id, remittanceId = rec.Id, journalEntryId = result.JournalEntryId, type = c.Type, year = c.Year, month = c.Month,
                amount = c.Amount, surcharge = c.Surcharge, penalty = c.Penalty, bankAccountId = a.BankAccountId, consentId, staff = a.StaffUserId,
                requestId = c.RequestId, controlLast4 = slip.ControlCodeLast4, ruleCode = slip.RuleCode, legalReference = TaxSlipMatch.LegalReference,
            });
            _db.AddChainedAuditLog(new AuditLog { CompanyId = c.CompanyId, UserId = a.StaffUserId, Action = AuditAction.Create, EntityType = nameof(StatutoryRemittance),
                EntityId = rec.Id.ToString(), NewValues = payload, Timestamp = DateTime.UtcNow });
            _db.AddChainedAuditLog(new AuditLog { CompanyId = a.ProviderId, UserId = a.StaffUserId, Action = AuditAction.Update, EntityType = nameof(TaxSlipEvidence),
                EntityId = slip.Id.ToString(), NewValues = JsonSerializer.Serialize(new { @event = "ProviderActedForClient", clientId = c.CompanyId, remittanceId = rec.Id, consentId, ruleCode = slip.RuleCode }),
                Timestamp = DateTime.UtcNow });
            slip.Status = TaxSlipIntakeService.Statuses.Remitted;
            slip.ClientCompanyId = c.CompanyId;
            slip.StatutoryRemittanceId = rec.Id;
            slip.TaxPaymentRequestId = c.RequestId ?? slip.TaxPaymentRequestId;
            slip.ClientAttachmentId = att.Id;
            slip.ResolvedByUserId = a.StaffUserId;
            slip.ResolvedAt = DateTime.UtcNow;
            slip.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            done = new Applied(result, consentId, att.Id);
            storagePath = slip.StoragePath;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
        // หลัง commit: สำเนาในที่เก็บของสลิปลบหลังแนบเป็นหลักฐานของลูกค้าแล้ว (ดีไซน์ §4.4 · คนละถังกับเอกสารบัญชี) · ล้มไม่ย้อนการบันทึก แต่ log ดัง
        try { await _storage.DeleteAsync(storagePath, CancellationToken.None); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "ลบสำเนาสลิปภาษี {Slip} หลังแนบไม่สำเร็จ", a.SlipId); }
        await NotifyClientAsync(c, done.Result.Id, a.AccountLabel);
        return done;
    }

    private async Task NotifyClientAsync(TaxSlipMatch.Candidate c, Guid remittanceId, string? accountLabel)
    {
        if (_notify == null) return;
        try
        {
            await _notify.DispatchAsync(c.CompanyId, NotificationEvents.TaxSlipRemitted, new NotificationContext
            {
                Title = "🧾 บันทึกการชำระ " + ProviderConsent.LabelOf(c.Type) + " งวด " + ThaiDate.ShortMonthBe(c.Year, c.Month) + " จากสลิปแล้ว",
                Message = $"ยอด {c.Amount + c.Surcharge + c.Penalty:N2} บาท" + (accountLabel != null ? " · จ่ายจาก " + accountLabel : "")
                    + " · บันทึกโดย NextAcc · ยกเลิกได้ที่หน้านำส่งภาษี · " + TaxSlipRemitView.NoFilingNumber,
                ActionUrl = "/pages/tax-remittance.html", EntityType = nameof(StatutoryRemittance), EntityId = remittanceId,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งกระดิ่งบริษัท {Client} หลังบันทึกนำส่งจากสลิปไม่สำเร็จ", c.CompanyId);
        }
    }
}

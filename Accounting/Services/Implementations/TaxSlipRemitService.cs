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
    /// <param name="ManualEntry">รอบ 275 แพ็ก D (H1 · คำตัดสิน 4.3): พนักงานเลือกแบบ/งวด/ยอดเอง (ไม่ใช่ผู้สมัครจากตัวตัดสิน) — ต้องมี <paramref name="StaffUserId"/> · audit แยก</param>
    /// <param name="StaffSlipAmount">ยอดบนสลิปที่พนักงานอ่านเอง — ใช้เฉพาะเมื่อระบบอ่านยอดไม่ได้ (OCR ล่ม) · ประทับลงแถวใต้ล็อก + audit</param>
    public sealed record ApplyRequest(Guid ProviderId, Guid SlipId, TaxSlipMatch.Candidate Candidate, Guid? BankAccountId, Guid? BankGlAccountId,
        Guid? StaffUserId, bool StaffIsClientTaxFiler, DateTime PayDate, string? AccountLabel, bool ManualEntry = false, decimal? StaffSlipAmount = null);

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
            var slip = await LockedSlipAsync(a.ProviderId, a.SlipId, ct);
            // ฝ่ายค้าน 272 H1: ยอดที่ลงบัญชีต้องเป็นยอดที่สลิปจ่ายทุกสตางค์ (ผู้สมัครเก่าจากปุ่ม/คิวพนักงานไม่ถูกเชื่อ) — ยอดค้างในสมุดตรวจอีกชั้นใน RemitAsync (ExpectedAmount) ·
            // รอบ 275 D: ระบบอ่านยอดไม่ได้ ⇒ ใช้ยอดที่พนักงานอ่านเอง (มีผู้กดเท่านั้น) — ไม่รู้ยอดเลย ⇒ ไม่บันทึก (เดิมข้ามด่านนี้เงียบ ๆ)
            var staffRead = slip.Amount == null && a.StaffUserId != null ? a.StaffSlipAmount : null;
            if ((slip.Amount ?? staffRead) is not decimal paid)
                throw new TaxSlipGateException("ไม่รู้ยอดบนสลิป — ไม่บันทึก (พนักงานกรอกยอดบนสลิปที่ “บันทึกเอง”)", TaxSlipMatch.Codes.Amount);
            if (Math.Round(paid, 2, MidpointRounding.AwayFromZero) != Math.Round(total, 2, MidpointRounding.AwayFromZero))
                throw new TaxSlipGateException($"ยอดบนสลิป {paid:N2} ไม่ตรงรายการที่เลือก {total:N2} — ไม่บันทึก", TaxSlipMatch.Codes.Amount);
            if (a.ManualEntry && a.StaffUserId == null)
                throw new TaxSlipGateException("การบันทึกเองต้องมีพนักงานผู้กด", TaxSlipDeskRule.ManualRuleCode);
            if (staffRead is decimal sr) slip.Amount = Math.Round(sr, 2, MidpointRounding.AwayFromZero);
            // ฝ่ายค้าน 272 H3: อีเมลที่ยืนยันโดเมนผู้ส่งไม่ได้ ⇒ ระบบไม่บันทึกเอง (พนักงานกดได้หลังตรวจ)
            if (slip.SenderUnverified && a.StaffUserId == null)
                throw new TaxSlipGateException(InboundEmailRule.UnverifiedReason, "TAX-SLIP-SENDER");
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(c.CompanyId, AdvisoryLockKey.StatutoryRemit, $"{c.Type}:{c.Year}") }, ct);
            TaxPaymentRequest? req = null;
            if (c.RequestId is Guid rid)
            {
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"TaxPaymentRequests\" WHERE \"Id\" = {rid} AND \"CompanyId\" = {c.CompanyId} FOR UPDATE", ct);
                req = await _db.TaxPaymentRequests.FirstOrDefaultAsync(r => r.Id == rid && r.CompanyId == c.CompanyId, ct);
                if (req == null || req.Status != TaxPaymentRequestLifecycle.Open)
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
            var note = (a.ManualEntry ? "บันทึกจากสลิป (พนักงานเลือกแบบ/งวด/ยอดเอง)" : "บันทึกจากสลิป") + (slip.ControlCodeLast4 != null ? " · Control Code ••" + slip.ControlCodeLast4 : "")
                + (slip.BankTxnLast4 != null ? " · Txn ••" + slip.BankTxnLast4 : "") + " · โดย NextAcc" + (consentId is Guid cg ? " (ความยินยอม " + cg.ToString("N")[..8] + ")" : "");
            // ── ผู้เขียนตัวเดียว (ห้ามสร้าง JE/แถวนำส่งเอง) ──
            var result = await _remit.RemitAsync(c.CompanyId, new RemitRequest(c.Type, c.Year, c.Month, a.PayDate,
                BankAccountId: a.BankAccountId, BankGlAccountId: a.BankGlAccountId, FilingNumber: null, ReceiptAttachmentId: null,
                IncludeLateFee: true, Note: note, LateSurcharge: c.Surcharge, Penalty: c.Penalty, ExpectedAmount: c.Amount), actor);

            var bytes = await SlipBytesAsync(slip, ct);
            var ext = SlipEvidenceRule.ExtensionFor(slip.ContentType) ?? ".bin";
            var fileName = "tax-slip-" + slip.Id.ToString("N")[..8] + ext;
            // ฝ่ายค้าน 272 L3: เส้นอัตโนมัติไม่มีผู้แนบที่เป็นคน ⇒ ไม่ประทับผู้ให้ความยินยอมเป็นผู้แนบ (ค่าที่แต่งขึ้น) — ผู้กระทำอยู่ใน audit (provider-slip:auto + ความยินยอม)
            var att = a.StaffUserId is Guid staffUploader
                ? await _files.UploadBytesAsync(c.CompanyId, "StatutoryRemittance", result.Id, fileName, slip.ContentType, bytes, staffUploader)
                : await _files.UploadAnonymousBytesAsync(c.CompanyId, "StatutoryRemittance", result.Id, fileName, slip.ContentType, bytes);

            var rec = await _db.StatutoryRemittances.FirstAsync(r => r.Id == result.Id && r.CompanyId == c.CompanyId, ct);
            rec.ReceiptAttachmentId = att.Id;
            rec.Source = TaxSlipRemitView.SourceSlip;
            rec.SlipEvidenceId = slip.Id;
            rec.ActedByProviderCompanyId = a.ProviderId;
            rec.ProviderConsentId = consentId;
            rec.ControlCodeLast4 = slip.ControlCodeLast4;
            rec.UpdatedAt = DateTime.UtcNow;
            if (req != null) { req.Status = TaxPaymentRequestLifecycle.Paid; req.StatutoryRemittanceId = rec.Id; req.TaxSlipEvidenceId = slip.Id; req.UpdatedAt = DateTime.UtcNow; }
            // ทุกการเขียนลงบัญชีลูกค้าเข้า audit chain ทั้งสองบริษัท
            var payload = JsonSerializer.Serialize(new
            {
                @event = "TaxSlipRemitted", slipId = slip.Id, remittanceId = rec.Id, journalEntryId = result.JournalEntryId, type = c.Type, year = c.Year, month = c.Month,
                amount = c.Amount, surcharge = c.Surcharge, penalty = c.Penalty, bankAccountId = a.BankAccountId, consentId, staff = a.StaffUserId,
                requestId = c.RequestId, controlLast4 = slip.ControlCodeLast4, ruleCode = a.ManualEntry ? TaxSlipDeskRule.ManualRuleCode : slip.RuleCode,
                manualEntry = a.ManualEntry, staffReadSlipAmount = staffRead, legalReference = TaxSlipMatch.LegalReference,
            });
            _db.AddChainedAuditLog(new AuditLog { CompanyId = c.CompanyId, UserId = a.StaffUserId, Action = AuditAction.Create, EntityType = nameof(StatutoryRemittance),
                EntityId = rec.Id.ToString(), NewValues = payload, Timestamp = DateTime.UtcNow });
            _db.AddChainedAuditLog(new AuditLog { CompanyId = a.ProviderId, UserId = a.StaffUserId, Action = AuditAction.Update, EntityType = nameof(TaxSlipEvidence),
                EntityId = slip.Id.ToString(), NewValues = JsonSerializer.Serialize(new { @event = "ProviderActedForClient", clientId = c.CompanyId, remittanceId = rec.Id, consentId, ruleCode = slip.RuleCode }),
                Timestamp = DateTime.UtcNow });
            slip.Status = TaxSlipIntakeService.Statuses.Remitted;
            if (a.ManualEntry) slip.RuleCode = TaxSlipDeskRule.ManualRuleCode;
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

    /// <summary>รอบ 275 แพ็ก D (H2 · L9): ล็อกแถวสลิป (FOR UPDATE) แล้ว<b>อ่านสถานะใหม่จากฐาน</b> — ผู้เรียกทุกทางโหลดแถวนี้ไว้ใน DbContext เดียวกันแล้ว
    /// (EF คืน instance เดิม = ค่าก่อนล็อก) ⇒ คำขอที่รอล็อกต้อง Reload ไม่ใช่เชื่อ tracker · สถานะที่ไม่ใช่ "รอตัดสิน" (บันทึกแล้ว/ไม่บันทึก/หมดอายุ) ⇒ ปฏิเสธ</summary>
    private async Task<TaxSlipEvidence> LockedSlipAsync(Guid providerId, Guid slipId, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"TaxSlipEvidences\" WHERE \"Id\" = {slipId} AND \"CompanyId\" = {providerId} FOR UPDATE", ct);
        var slip = await _db.TaxSlipEvidences.FirstOrDefaultAsync(s => s.Id == slipId && s.CompanyId == providerId, ct)
            ?? throw new TaxSlipGateException("ไม่พบแถวสลิป", TaxSlipMatch.Codes.None);
        await _db.Entry(slip).ReloadAsync(ct);
        if (slip.Status == TaxSlipIntakeService.Statuses.Remitted)
            throw new TaxSlipGateException("สลิปนี้บันทึกไปแล้ว", TaxSlipMatch.Codes.Duplicate);
        if (!TaxSlipIntakeService.Statuses.Open.Contains(slip.Status))
            throw new TaxSlipGateException("สลิปนี้ปิดแล้ว (" + TaxSlipIntakeService.StatusLabel(slip.Status) + ") — ไม่บันทึก", ClosedRuleCode);
        return slip;
    }

    public const string ClosedRuleCode = "TAX-SLIP-CLOSED";
    public const string VoidedRuleCode = "TAX-SLIP-VOIDED";

    /// <summary>ไฟล์สลิป — สำเนาในที่เก็บของสลิป · รอบ 275 D (M1): สลิปที่รายการนำส่งถูกยกเลิก สำเนาถูกลบไปแล้วหลังแนบครั้งแรก ⇒ อ่านจากไฟล์แนบเดิมในบริษัทลูกค้า</summary>
    private async Task<byte[]> SlipBytesAsync(TaxSlipEvidence slip, CancellationToken ct)
    {
        var bytes = slip.FilePurgedAt == null ? await _storage.ReadAllBytesAsync(slip.StoragePath, ct) : null;
        if (bytes == null && slip.ClientAttachmentId is Guid aid && slip.ClientCompanyId is Guid cc)
            bytes = await _files.ReadBytesAsync(cc, aid, ct);
        return bytes ?? throw new TaxSlipGateException("อ่านไฟล์สลิปจากที่เก็บไม่ได้ — แนบหลักฐานไม่ได้ จึงไม่บันทึก", "TAX-SLIP-FILE");
    }

    /// <summary>รอบ 275 แพ็ก D (H1): ปิดสลิปด้วย<b>รายการนำส่งที่บันทึกไว้แล้ว</b> (บันทึกมือที่หน้านำส่งของลูกค้าก่อน) — แนบสลิปเป็นหลักฐานของรายการนั้น ·
    /// ไม่สร้าง JE/รายการนำส่งใหม่ · ด่านเดียวกับการกดบันทึก (ความยินยอม/หนังสือมอบอำนาจ/ผู้ยื่นภาษีของลูกค้า ใต้ล็อก) · ยอดสลิป = ยอดรายการทุกสตางค์ · audit สองบริษัท</summary>
    /// <param name="AllowedClients">บริษัทที่ช่องทางของสลิปผูก (กฎ M — ห้ามผูกกับบริษัทนอกชุด)</param>
    /// <param name="StaffConfirmedTaxId">พนักงานติ๊กว่าตรวจเลขผู้เสียภาษีบนรูปแล้ว — ใช้ได้เฉพาะแถวเก่าที่ตัดสินไม่ได้ (<c>TaxSlipDeskRule.TaxIdCheck.Undecided</c>) · audit</param>
    public sealed record LinkRequest(Guid ProviderId, Guid SlipId, Guid ClientCompanyId, Guid RemittanceId, Guid StaffUserId, bool StaffIsClientTaxFiler,
        IReadOnlyCollection<Guid> AllowedClients, bool StaffConfirmedTaxId = false);

    public sealed record Linked(Guid RemittanceId, string RemittanceType, int PeriodYear, int PeriodMonth, decimal Tax, decimal Surcharge, decimal Penalty, DateTime PayDate);

    public async Task<Linked> LinkAsync(LinkRequest a, CancellationToken ct = default)
    {
        if (!a.AllowedClients.Contains(a.ClientCompanyId))
            throw new TaxSlipGateException("บริษัทนี้ไม่ได้ผูกกับช่องทางที่ส่งสลิป — ผูกไม่ได้", TaxSlipMatch.Codes.TaxId);
        Linked done; string storagePath; bool purged;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var slip = await LockedSlipAsync(a.ProviderId, a.SlipId, ct);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"StatutoryRemittances\" WHERE \"Id\" = {a.RemittanceId} AND \"CompanyId\" = {a.ClientCompanyId} FOR UPDATE", ct);
            var rec = await _db.StatutoryRemittances.FirstOrDefaultAsync(r => r.Id == a.RemittanceId && r.CompanyId == a.ClientCompanyId, ct)
                ?? throw new TaxSlipGateException("ไม่พบรายการนำส่งของบริษัทนี้", TaxSlipDeskRule.LinkRuleCode);
            await _db.Entry(rec).ReloadAsync(ct);
            var recTotal = rec.Amount + rec.LateFee + rec.Penalty + rec.OtherFine;
            var coTaxId = await _db.Companies.AsNoTracking().Where(co => co.Id == a.ClientCompanyId).Select(co => co.TaxId).FirstOrDefaultAsync(ct);
            var taxIdCheck = TaxSlipDeskRule.TaxIdVerdict(slip.TaxIdMasked, slip.TaxIdHash, coTaxId, TaxSlipRefHash.TaxId(coTaxId));
            if (TaxSlipDeskRule.LinkProblem(slip.Amount, recTotal, rec.IsDeleted, rec.SlipEvidenceId, taxIdCheck,
                    slip.Agency, rec.RemittanceType, a.StaffConfirmedTaxId) is { } problem)
                throw new TaxSlipGateException(problem + " — ไม่ผูก", TaxSlipDeskRule.LinkRuleCode);
            var verdict = await _consent.CheckForWriteAsync(a.ClientCompanyId, a.ProviderId, rec.RemittanceType, recTotal, ct);
            if (!verdict.AllowsStaffClick && !a.StaffIsClientTaxFiler)
                throw new TaxSlipGateException("บริษัทลูกค้ายังไม่อนุญาต/ไม่มีหนังสือมอบอำนาจ และผู้กดไม่ได้เป็นผู้มีสิทธิ์ยื่นภาษีของบริษัทลูกค้า — " + verdict.Reason,
                    ProviderConsent.RuleCode + "-" + verdict.Code);
            var consentId = verdict.Ok || verdict.AllowsStaffClick ? verdict.ConsentId : null;
            var bytes = await SlipBytesAsync(slip, ct);
            var ext = SlipEvidenceRule.ExtensionFor(slip.ContentType) ?? ".bin";
            var att = await _files.UploadBytesAsync(a.ClientCompanyId, "StatutoryRemittance", rec.Id, "tax-slip-" + slip.Id.ToString("N")[..8] + ext, slip.ContentType, bytes, a.StaffUserId);
            rec.ReceiptAttachmentId ??= att.Id;
            rec.SlipEvidenceId = slip.Id;
            rec.ActedByProviderCompanyId ??= a.ProviderId;
            rec.ControlCodeLast4 ??= slip.ControlCodeLast4;
            rec.UpdatedAt = DateTime.UtcNow;
            // ใบขอชำระของงวดนั้นที่ยังเปิด ⇒ ชำระแล้ว (ตัวตั้งเดียวกับ RemitAsync)
            await TaxPaymentRequestService.CloseOnRemitAsync(_db, a.ClientCompanyId, rec.RemittanceType, rec.PeriodYear, rec.PeriodMonth, rec.Id, slip.Id, recTotal, slip.ControlCodeHash, ct);
            var payload = JsonSerializer.Serialize(new
            {
                @event = "TaxSlipLinkedToRemittance", slipId = slip.Id, remittanceId = rec.Id, type = rec.RemittanceType, year = rec.PeriodYear, month = rec.PeriodMonth,
                total = recTotal, consentId, staff = a.StaffUserId, taxIdCheck = taxIdCheck.ToString(), staffConfirmedTaxId = taxIdCheck == TaxSlipDeskRule.TaxIdCheck.Undecided, controlLast4 = slip.ControlCodeLast4, ruleCode = TaxSlipDeskRule.LinkRuleCode,
                legalReference = TaxSlipMatch.LegalReference,
            });
            _db.AddChainedAuditLog(new AuditLog { CompanyId = a.ClientCompanyId, UserId = a.StaffUserId, Action = AuditAction.Update, EntityType = nameof(StatutoryRemittance),
                EntityId = rec.Id.ToString(), NewValues = payload, Timestamp = DateTime.UtcNow });
            _db.AddChainedAuditLog(new AuditLog { CompanyId = a.ProviderId, UserId = a.StaffUserId, Action = AuditAction.Update, EntityType = nameof(TaxSlipEvidence),
                EntityId = slip.Id.ToString(), NewValues = payload, Timestamp = DateTime.UtcNow });
            slip.Status = TaxSlipIntakeService.Statuses.Remitted;
            slip.RuleCode = TaxSlipDeskRule.LinkRuleCode;
            slip.Reason = "ผูกกับรายการนำส่งที่บันทึกไว้แล้ว";
            slip.ClientCompanyId = a.ClientCompanyId;
            slip.StatutoryRemittanceId = rec.Id;
            slip.ClientAttachmentId = att.Id;
            slip.ResolvedByUserId = a.StaffUserId;
            slip.ResolvedAt = DateTime.UtcNow;
            slip.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            done = new Linked(rec.Id, rec.RemittanceType, rec.PeriodYear, rec.PeriodMonth, rec.Amount, rec.LateFee, rec.Penalty + rec.OtherFine, rec.PayDate);
            storagePath = slip.StoragePath; purged = slip.FilePurgedAt != null;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
        if (!purged)
        {
            try { await _storage.DeleteAsync(storagePath, CancellationToken.None); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "ลบสำเนาสลิปภาษี {Slip} หลังผูกไม่สำเร็จ", a.SlipId); }
        }
        return done;
    }

    /// <summary>
    /// รอบ 275 แพ็ก D (M1): รายการนำส่งที่ผูกสลิปถูกยกเลิก ⇒ <b>ปล่อยแถวสลิปกลับเข้าคิวพนักงาน</b> (เหตุจริง · ผู้สมัครเดิมยังอยู่ · ไฟล์อ่านจากไฟล์แนบเดิม) และ
    /// <b>ใบขอชำระที่ปิดด้วยรายการนี้กลับเปิด</b> (หรือหมดอายุ) — เดิมสลิปค้าง "บันทึกแล้ว" ⇒ ส่งสลิปเดิมใหม่ได้ "บันทึกไปแล้วเมื่อ…" ทั้งที่ยกเลิกแล้ว ·
    /// ผู้เรียก: <c>StatutoryRemittanceService.VoidRemittanceAsync</c> ในธุรกรรมเดียวกัน (ผู้เรียก SaveChanges) · คืนจำนวนแถวที่ปล่อย
    /// </summary>
    public static async Task<(int Slips, int Requests)> ReleaseOnVoidAsync(AccountingDbContext db, StatutoryRemittance rec, string why, DateTime nowUtc, CancellationToken ct = default)
    {
        var slips = 0;
        if (rec.SlipEvidenceId is Guid sid && rec.ActedByProviderCompanyId is Guid pid)
        {
            var slip = await db.TaxSlipEvidences.FirstOrDefaultAsync(s => s.Id == sid && s.CompanyId == pid && s.StatutoryRemittanceId == rec.Id, ct);
            if (slip != null && slip.Status == TaxSlipIntakeService.Statuses.Remitted)
            {
                var reason = "รายการนำส่งที่บันทึกจากสลิปนี้ถูกยกเลิก (" + why + ") — ตรวจแล้วบันทึกใหม่ ผูกรายการอื่น หรือไม่บันทึก";
                slip.Status = TaxSlipIntakeService.Statuses.ToStaff;
                slip.RuleCode = VoidedRuleCode;
                slip.Reason = reason.Length > 500 ? reason[..500] : reason;
                slip.StatutoryRemittanceId = null;
                slip.ResolvedAt = null;
                slip.ResolvedByUserId = null;
                slip.StaffRemindedAt = null;
                slip.ReleasedAt = nowUtc;   // ฝ่ายค้าน MED-3: อายุคิวนับใหม่จากเวลาที่ปล่อย
                slip.UpdatedAt = nowUtc;
                db.AddChainedAuditLog(new AuditLog { CompanyId = pid, Action = AuditAction.Update, EntityType = nameof(TaxSlipEvidence), EntityId = slip.Id.ToString(),
                    NewValues = JsonSerializer.Serialize(new { @event = "TaxSlipReleasedByVoid", remittanceId = rec.Id, clientId = rec.CompanyId, reason = why, ruleCode = VoidedRuleCode }),
                    Timestamp = nowUtc });
                slips = 1;
            }
        }
        var reqs = await db.TaxPaymentRequests
            .Where(r => r.CompanyId == rec.CompanyId && r.StatutoryRemittanceId == rec.Id && r.Status == TaxPaymentRequestLifecycle.Paid).ToListAsync(ct);
        foreach (var r in reqs)
        {
            r.Status = TaxPaymentRequestLifecycle.StatusAfterVoid(r.ControlCodeExpiresAt, nowUtc);
            r.StatutoryRemittanceId = null;
            r.TaxSlipEvidenceId = null;
            r.ReopenedAt = nowUtc; r.ReopenNoticeAt = null;   // ฝ่ายค้าน LOW-3: งานเบื้องหลังแจ้งกลุ่มตามจริง
            r.UpdatedAt = nowUtc;
        }
        return (slips, reqs.Count);
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

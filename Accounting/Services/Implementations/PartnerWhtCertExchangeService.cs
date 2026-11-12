using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// **50 ทวิ กลับอัตโนมัติผ่านคู่ค้า NextAcc** (รอบ 255 WP-12B ข้อ 8 · AUTOPILOT_BILLING_DESIGN §6.5 · ขอบเขต <c>WhtCertBack</c>)
/// <para>ผู้จ่าย (บริษัท P) เปิด <c>WorkflowAutomationConfig.SendWhtCertsToPartners</c> (ค่าตั้งต้นปิด · ประทับ <c>SendWhtCertsToPartnersSince</c> ตอนเปิด) ⇒
/// งาน <c>AutopilotJob</c> ขั้น PartnerWhtCerts (JobLock) หาใบ 50 ทวิ ที่ P <b>ออกแล้ว</b>หลังเปิด ของผู้ถูกหักที่เป็นบริษัทคู่ค้าที่เชื่อมแล้ว
/// (ผู้ติดต่อผูก <c>NextAccTenant</c> + คู่ Accepted) ⇒ ส่งเข้า<b>ประตูเดียว</b>ของผู้ขาย <see cref="IWhtCertIntakeService.SubmitAsync"/> (ช่อง <c>NextAccLink</c>)
/// — กันซ้ำ (เลขที่ + ผู้จ่าย) · ด่านเชิงกำหนด (เลขภาษีผู้ถูกหักต้องเป็นของผู้ขาย · ยอด · อัตรา · ปีภาษี) · ตรวจของจริงแบบ local-first ทำที่ประตูนั้นทั้งหมด</para>
/// <para>"ส่งแล้ว" (<c>PartnerDeliveredAt</c>) ประทับ<b>เฉพาะเมื่อกล่องรับของผู้ขายตอบผลจริง</b> (มีรหัสรายการ) · ล้ม ⇒ เหตุ + จำนวนครั้งบนใบ (ไม่เงียบ) ·
/// ผู้จ่ายยกเลิกใบที่ส่งไปแล้ว ⇒ แจ้งผู้ขายทันที (ไม่ตีกลับรายการของผู้ขายเอง — ผู้ขายตัดสิน)</para>
/// </summary>
public class PartnerWhtCertExchangeService
{
    private readonly AccountingDbContext _db;
    private readonly IWhtCertIntakeService _intake;
    private readonly INotificationEngine? _notify;
    private readonly ILogger<PartnerWhtCertExchangeService> _logger;

    public PartnerWhtCertExchangeService(AccountingDbContext db, IWhtCertIntakeService intake, ILogger<PartnerWhtCertExchangeService> logger,
        INotificationEngine? notify = null)
    {
        _db = db; _intake = intake; _logger = logger; _notify = notify;
    }

    public const string ExternalSystem = PartnerExchangeRules.NextAccTenantSystem;
    private const int BatchPerCompany = 50;

    /// <summary>หนึ่งรอบของงาน — คืนจำนวนใบที่ส่ง/แจ้งได้ (ผลรอบของงาน)</summary>
    public async Task<int> RunOnceAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var payers = await _db.WorkflowAutomationConfigs.AsNoTracking()
            .Where(c => c.SendWhtCertsToPartners && c.SendWhtCertsToPartnersSince != null)
            .Select(c => new { c.CompanyId, Since = c.SendWhtCertsToPartnersSince })
            .ToListAsync(ct);
        var done = 0;
        foreach (var p in payers)
        {
            done += await DeliverForPayerAsync(p.CompanyId, p.Since!.Value, nowUtc, ct);
            done += await NotifyVoidedAsync(p.CompanyId, ct);
        }
        return done;
    }

    /// <summary>ใบที่ส่งได้ของผู้จ่ายหนึ่งราย (tenant = <paramref name="payerId"/> ทุก query)</summary>
    public async Task<int> DeliverForPayerAsync(Guid payerId, DateTime since, DateTime nowUtc, CancellationToken ct = default)
    {
        var payer = await _db.Companies.AsNoTracking().Where(c => c.Id == payerId)
            .Select(c => new { c.Name, c.TaxId, c.BranchCode }).FirstOrDefaultAsync(ct);
        if (payer == null) return 0;
        var certs = await _db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == payerId && !w.IsDeleted && w.PartnerDeliveredAt == null
                && (w.Status == WithholdingTaxCertStatus.Issued || w.Status == WithholdingTaxCertStatus.Printed)
                && w.IssuedDate != null && w.IssuedDate >= since && w.PartnerDeliveryAttempts < PartnerExchangeRules.MaxWhtDeliveryAttempts)
            .OrderBy(w => w.IssuedDate)
            .Take(BatchPerCompany)
            .Select(w => new
            {
                w.Id, w.CertificateNumber, w.IssuedDate, w.Status, w.TaxFormType, w.TotalIncomeAmount, w.TotalTaxAmount, w.DocumentId,
                w.PartnerDeliveredAt, w.PartnerDeliveryAttempts, w.PayeeContactId,
                Lines = w.Lines.Select(l => new { l.IncomeTypeCode, l.TaxRate, l.PaymentDate }).ToList(),
            })
            .ToListAsync(ct);
        if (certs.Count == 0) return 0;

        // รอบ 264 P5 (C-10): โหลดผู้ติดต่อผู้ถูกหักทุกราย (ไม่กรองเฉพาะที่ผูก NextAccTenant) — ผูกระบบอื่นแต่เลขตรงคู่ค้า ⇒ เหตุบนใบ ไม่ใช่ข้ามเงียบ
        var contactIds = certs.Select(c => c.PayeeContactId).Distinct().ToList();
        var contacts = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == payerId && contactIds.Contains(c.Id))
            .Select(c => new { c.Id, c.ExternalSystem, c.ExternalId, c.TaxId, c.BranchCode, c.Address, c.District, c.Province })
            .ToListAsync(ct);
        var accepted = await _db.TradingPartnerships.AsNoTracking()
            .Where(t => (t.CompanyAId == payerId || t.CompanyBId == payerId) && t.Status == TradingPartnershipStatus.Accepted)
            .Select(t => t.CompanyAId == payerId ? t.CompanyBId : t.CompanyAId)
            .ToListAsync(ct);
        // ผู้รับ (บริษัทคู่ค้า) — เลข/สาขาของจริงจากบริษัทนั้น ใช้ตรวจผู้ถูกหักบนใบก่อนส่ง (B12 · คำตัดสิน 264-5-3)
        var partnerCos = (await _db.Companies.AsNoTracking().IgnoreQueryFilters()
                .Where(c => accepted.Contains(c.Id)).Select(c => new { c.Id, c.TaxId, c.BranchCode }).ToListAsync(ct))
            .ToDictionary(c => c.Id);

        var sent = 0;
        foreach (var cert in certs)
        {
            if (!PartnerExchangeRules.WhtCertDeliverable((int)cert.Status, cert.IssuedDate, since, cert.PartnerDeliveredAt, cert.PartnerDeliveryAttempts)) continue;
            var contact = contacts.FirstOrDefault(c => c.Id == cert.PayeeContactId);
            if (contact == null) continue;
            var linked = contact.ExternalSystem == ExternalSystem && Guid.TryParse(contact.ExternalId, out var linkedId) && accepted.Contains(linkedId)
                ? linkedId : (Guid?)null;
            var partner = linked is Guid lp && partnerCos.TryGetValue(lp, out var pc) ? pc : null;
            var check = PartnerExchangeRules.WhtDeliveryCheckOf(new PartnerExchangeRules.WhtDeliveryFacts(
                contact.ExternalSystem, partner != null,
                partner == null && partnerCos.Values.Any(c => ThaiTaxId.Same(c.TaxId, contact.TaxId)),
                contact.TaxId, contact.BranchCode, partner?.TaxId, partner?.BranchCode,
                PartnerExchangeRules.PayeeAddressPresent(contact.Address, contact.District, contact.Province),
                partner != null && await ReturnWhtCertsAllowedAsync(payerId, partner.Id, ct)));
            // ไม่มีหลักฐานว่าผู้ถูกหักเป็นคู่ค้า ⇒ ไม่ใช่งานของเส้นนี้ (ใบส่งทางอีเมล/กระดาษเดิม) — ไม่ประทับอะไร
            if (check.Action == PartnerExchangeRules.WhtDeliveryAction.NotPartner) continue;
            // C-10 · B12 · ผู้รับไม่ตรง · คู่ไม่ได้เชื่อม ⇒ เหตุบนใบ (ไม่นับครั้ง — แก้แล้วส่งรอบถัดไปเอง) · ไม่ข้ามเงียบ
            if (check.Action == PartnerExchangeRules.WhtDeliveryAction.Skip)
            {
                await StampSkipAsync(payerId, cert.Id, check.Reason!, ct);
                continue;
            }
            var payeeCompanyId = partner!.Id;
            var form = cert.TaxFormType switch
            {
                TaxType.WithholdingTax53 => (WhtPayerFormType?)WhtPayerFormType.Pnd53,
                TaxType.WithholdingTax3 => WhtPayerFormType.Pnd3,
                _ => null,
            };
            if (form == null)
            {
                await StampFailureAsync(payerId, cert.Id, "แบบนี้ไม่ส่งผ่านคู่ค้า NextAcc (ส่งเฉพาะ ภ.ง.ด.3/53) — ส่งหนังสือรับรองให้ผู้ขายทางเดิม", ct);
                continue;
            }

            // เอกสารฝั่งผู้ขายที่ใบนี้ครอบ: ใบซื้อร่างของเรามาจากเอกสารของผู้ขายใบไหน (ผ่านแถวกล่องเข้า) — เป็นชุดแรกของการจับคู่
            Guid? hint = null;
            if (cert.DocumentId is Guid ourDoc)
                hint = await _db.CrossTenantDocumentLinks.AsNoTracking()
                    .Where(l => l.TargetCompanyId == payerId && l.TargetDocumentId == ourDoc && l.SourceCompanyId == payeeCompanyId)
                    .Select(l => (Guid?)l.SourceDocumentId).FirstOrDefaultAsync(ct);

            var submission = new WhtCertSubmission(
                Channel: WhtCertIntakeChannel.NextAccLink,
                EvidenceKind: WhtEvidenceKind.ElectronicCertificate,
                CertificateNumber: cert.CertificateNumber,
                // รอบ 255 ฝ่ายค้าน LOW: วันที่บน 50 ทวิ = วันที่จ่ายเงิน (ท.ป.4/2528 — ออกในวันจ่าย) ไม่ใช่วันที่กดออกใบ ·
                // หลายบรรทัด ⇒ วันจ่ายล่าสุด · ไม่มีบรรทัด/ไม่ได้ตั้ง ⇒ วันที่ออกใบ (ไม่แต่ง)
                CertificateDate: Accounting.Helpers.PartnerExchangeRules.WhtCertificateDate(cert.Lines.Select(l => l.PaymentDate), cert.IssuedDate),
                PayerName: payer.Name,
                PayerTaxId: payer.TaxId,
                PayerBranchCode: payer.BranchCode,
                PayerContactId: null,
                // ผู้ถูกหักตาม "ผู้ติดต่อของผู้จ่าย" — ไม่ตรงเลขภาษีของผู้ขาย ⇒ ด่านของกล่องรับตีกลับเอง (ไม่เดาว่าเป็นเรา)
                PayeeTaxId: contact.TaxId,
                PayeeIsUs: null,
                IncomeTypeCode: cert.Lines.Select(l => l.IncomeTypeCode).FirstOrDefault(),
                IncomeAmount: cert.TotalIncomeAmount,
                WhtRate: PartnerExchangeRules.CertRate(cert.Lines.Select(l => l.TaxRate).ToList(), cert.TotalIncomeAmount, cert.TotalTaxAmount),
                WhtAmount: cert.TotalTaxAmount,
                PayerFormType: form.Value,
                ExtractionConfidence: null,
                FileAttachmentId: null,
                FileSha256: null,
                OcrScanResultId: null,
                DocumentHints: hint is Guid h ? new[] { h } : null,
                RawText: null,
                SubmittedBy: "คู่ค้า NextAcc: " + payer.Name,
                SubmittedByUserId: null,
                Notes: "ส่งอัตโนมัติจากระบบของผู้จ่าย (คู่ค้า NextAcc)");
            try
            {
                var r = await _intake.SubmitAsync(payeeCompanyId, submission, ct);
                // รอบ 257 ชุด 2 (B-L4): กล่องรับตอบ "ตีกลับ" ⇒ ไม่ใช่ "ส่งแล้ว" — เหตุบนใบของผู้จ่าย + หยุดลองซ้ำ (ผลเชิงกำหนด) · เดิมประทับส่งแล้วทั้งที่ผู้ขายไม่ได้ใบ
                if (PartnerExchangeRules.IntakeRejected(r.Status))
                {
                    await StampFailureAsync(payerId, cert.Id, PartnerExchangeRules.IntakeRejectedReason(r.Message), ct, giveUp: true);
                    continue;
                }
                // ภายนอก (กล่องรับของผู้ขาย) ตอบผลจริงแล้วเท่านั้นจึงประทับ "ส่งแล้ว" · compare-and-set (หลายเครื่อง = ครั้งเดียว)
                var n = await _db.WithholdingTaxCerts
                    .Where(w => w.Id == cert.Id && w.CompanyId == payerId && w.PartnerDeliveredAt == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(w => w.PartnerDeliveredAt, nowUtc)
                        .SetProperty(w => w.PartnerIntakeId, (Guid?)r.IntakeId)
                        .SetProperty(w => w.PartnerDeliveryError, (string?)null), ct);
                if (n > 0) sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ด่าน 409 ของกล่องรับ (ใบอยู่ในทะเบียนแล้ว) = ข้อความไทยถึงผู้ใช้ · อื่น ๆ = ข้อความกลาง · ใบยังไม่ "ส่งแล้ว"
                _db.ChangeTracker.Clear();
                var reason = ex is BusinessRuleException ? ex.Message : "ส่งเข้ากล่องรับของผู้ขายไม่สำเร็จ — ระบบลองใหม่รอบถัดไป";
                _logger.LogError(ex, "ส่ง 50 ทวิ {Cert} ของบริษัท {Payer} ถึงคู่ค้า {Payee} ไม่สำเร็จ", cert.Id, payerId, payeeCompanyId);
                await StampFailureAsync(payerId, cert.Id, reason, ct);
            }
        }
        return sent;
    }

    /// <summary>รอบ 264 P5 (C-10 · คำตัดสิน 264-5-2): เหตุที่ยังส่งไม่ได้ (ผูกระบบอื่น · ที่อยู่ว่าง · ผู้รับไม่ตรง · คู่ไม่ได้เชื่อม) — ประทับบนใบโดย<b>ไม่นับครั้ง</b>
    /// (ไม่ใช่การล้มของการส่ง · แก้ต้นเหตุแล้วรอบถัดไปส่งเอง) · เขียนเฉพาะเมื่อเหตุเปลี่ยน (ไม่เขียนซ้ำทุกรอบ)</summary>
    private async Task StampSkipAsync(Guid payerId, Guid certId, string reason, CancellationToken ct)
    {
        await _db.WithholdingTaxCerts
            .Where(w => w.Id == certId && w.CompanyId == payerId && w.PartnerDeliveredAt == null
                && (w.PartnerDeliveryError == null || w.PartnerDeliveryError != reason))
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.PartnerDeliveryError, reason), ct);
    }

    /// <summary>รอบ 264 P5 — <b>จุดเรียกความยินยอมเดียว</b>ของเส้น "ส่ง 50 ทวิ กลับให้คู่ค้า" (ขอบเขต ReturnWhtCerts) · ตอนนี้ = ค่าตั้งทั้งบริษัทของผู้จ่าย
    /// <c>WorkflowAutomationConfig.SendWhtCertsToPartners</c> (+ watermark) — แพ็ก P1 (ความยินยอมต่อคู่ × ขอบเขต) สลับตัวอ่านที่นี่ที่เดียว
    /// (คำตัดสิน 264-5-5 · required_call_site_check R264-P5)</summary>
    private async Task<bool> ReturnWhtCertsAllowedAsync(Guid payerId, Guid payeeCompanyId, CancellationToken ct)
    {
        _ = payeeCompanyId;   // ความยินยอมต่อคู่ (P1) จะใช้คู่นี้ · ตอนนี้ค่าตั้งทั้งบริษัท
        return await _db.WorkflowAutomationConfigs.AsNoTracking()
            .AnyAsync(c => c.CompanyId == payerId && c.SendWhtCertsToPartners && c.SendWhtCertsToPartnersSince != null, ct);
    }

    /// <param name="giveUp">รอบ 257 ชุด 2 (B-L4): ผลเชิงกำหนด (กล่องรับตีกลับ) ⇒ ตั้งจำนวนครั้งเต็มเพดาน (ไม่ส่งซ้ำ) · false = ล้มชั่วคราว นับ +1</param>
    private async Task StampFailureAsync(Guid payerId, Guid certId, string reason, CancellationToken ct, bool giveUp = false)
    {
        var q = _db.WithholdingTaxCerts.Where(w => w.Id == certId && w.CompanyId == payerId && w.PartnerDeliveredAt == null);
        if (giveUp)
            await q.ExecuteUpdateAsync(s => s
                .SetProperty(w => w.PartnerDeliveryError, reason)
                .SetProperty(w => w.PartnerDeliveryAttempts, PartnerExchangeRules.MaxWhtDeliveryAttempts), ct);
        else
            await q.ExecuteUpdateAsync(s => s
                .SetProperty(w => w.PartnerDeliveryError, reason)
                .SetProperty(w => w.PartnerDeliveryAttempts, w => w.PartnerDeliveryAttempts + 1), ct);
    }

    /// <summary>ผู้จ่ายยกเลิกใบที่ส่งเข้ากล่องรับของผู้ขายแล้ว ⇒ แจ้งผู้ขายครั้งเดียว (ประทับก่อนแจ้ง · ไม่แก้รายการของผู้ขายเอง)</summary>
    public async Task<int> NotifyVoidedAsync(Guid payerId, CancellationToken ct = default)
    {
        var voided = await _db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == payerId && w.Status == WithholdingTaxCertStatus.Voided && w.PartnerDeliveredAt != null
                && w.PartnerVoidNotifiedAt == null && w.PartnerIntakeId != null)
            .Select(w => new { w.Id, w.CertificateNumber, w.PartnerIntakeId, w.PayeeContactId })
            .Take(BatchPerCompany)
            .ToListAsync(ct);
        var payerName = await _db.Companies.AsNoTracking().Where(c => c.Id == payerId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var n = 0;
        foreach (var v in voided)
        {
            var payeeCompanyId = await _db.WhtCertIntakes.AsNoTracking().IgnoreQueryFilters()
                .Where(i => i.Id == v.PartnerIntakeId).Select(i => (Guid?)i.CompanyId).FirstOrDefaultAsync(ct);
            var claimed = await _db.WithholdingTaxCerts
                .Where(w => w.Id == v.Id && w.CompanyId == payerId && w.PartnerVoidNotifiedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.PartnerVoidNotifiedAt, DateTime.UtcNow), ct);
            if (claimed == 0 || payeeCompanyId is not Guid payee) continue;
            if (_notify != null)
                await _notify.DispatchAsync(payee, Accounting.Models.Constants.NotificationEvents.AutopilotPartnerExchange, new NotificationContext
                {
                    Title = $"คู่ค้า {payerName} ยกเลิกหนังสือรับรอง 50 ทวิ เลขที่ {v.CertificateNumber}",
                    // รอบ 257 ชุด 2 (B-H2): ชี้ทางที่ทำได้จริง — ยืนยันแล้ว ⇒ "ถอนการยืนยัน" (UnconfirmAsync) · ยังรอยืนยัน ⇒ "ตีกลับ"
                    Message = "ใบนี้เคยส่งเข้ากล่องรับ 50 ทวิ ของท่านผ่าน NextAcc — เปิดหน้า \"ภาษีถูกหัก ณ ที่จ่าย\" ที่ใบนี้ในกล่องรับ: ถ้ายืนยันแล้ว กด \"ถอนการยืนยัน\" "
                        + "(ระบบถอนเครดิตจากใบนี้ออกจากทะเบียน) · ถ้ายังรอยืนยัน กด \"ตีกลับ\" · แล้วขอใบใหม่จากผู้จ่าย ก่อนใช้เป็นเครดิตภาษี",
                    ActionUrl = "/pages/wht-credit.html", EntityType = "WhtCertIntake", EntityId = v.PartnerIntakeId,
                });
            n++;
        }
        return n;
    }
}

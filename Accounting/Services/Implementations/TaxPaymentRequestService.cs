using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 272 แพ็ก P3 (คำตัดสิน 272-1-2 = D-2) — <b>ผู้เขียนเดียว</b>ของใบขอชำระ <see cref="TaxPaymentRequest"/>: พนักงานของบริษัทผู้ให้บริการส่งเอกสารชำระภาษีให้ลูกค้า
/// พร้อม Control Code ⇒ ระบบเก็บตัวคาดหวังในบริษัทลูกค้า (แฮช + 4 ตัวท้าย) แล้วส่งการ์ดเข้ากลุ่ม/อีเมลของผู้ติดต่อ · ยอดในสมุดของลูกค้าไม่ตรง ⇒ บล็อกพร้อมเหตุ
/// (ทางไปต่อ: แก้สมุด หรือติ๊ก "ส่งไปก่อน" ⇒ <c>BooksMismatch</c> = สลิปเข้าคิวพนักงาน ไม่บันทึกเอง) · T2: ใบแรกของบริษัทที่ยังไม่อนุญาต ⇒ ขอความยินยอมเอง ·
/// อ่านยอดค้างของลูกค้าผ่าน <see cref="IStatutoryRemittanceService.GetDashboardAsync"/> หลังยืนยันว่าผู้ติดต่อนี้เป็นบริษัทลูกค้าจริง (ไม่ query ข้ามผู้เช่าเอง)
/// </summary>
public class TaxPaymentRequestService
{
    private readonly AccountingDbContext _db;
    private readonly IStatutoryRemittanceService _remit;
    private readonly ProviderConsentService _consent;
    private readonly TaxSlipChannelService _channel;
    private readonly ILogger<TaxPaymentRequestService> _logger;

    public TaxPaymentRequestService(AccountingDbContext db, IStatutoryRemittanceService remit, ProviderConsentService consent, TaxSlipChannelService channel,
        ILogger<TaxPaymentRequestService> logger)
    { _db = db; _remit = remit; _consent = consent; _channel = channel; _logger = logger; }

    /// <param name="PendingPoaId">ฝ่ายค้าน 272 M4: หนังสือมอบอำนาจที่รอผู้อนุมัติคนที่สอง</param>
    /// <param name="PoaUploadedBy">ผู้แนบ (ผู้อนุมัติต้องไม่ใช่คนนี้)</param>
    public sealed record ClientOption(Guid ContactId, Guid ClientCompanyId, string Name, string ConsentLabel, bool HasLineGroup, Guid? PendingPoaId = null, Guid? PoaUploadedBy = null);
    public sealed record PendingOption(string Type, string Label, int Year, int Month, decimal Amount, DateTime DueDate);

    /// <summary>ผู้ติดต่อ (ของบริษัทผู้ให้บริการ) ที่เป็นบริษัทลูกค้า — ตัวเลือกบนหน้า "ส่งเอกสารชำระภาษี"</summary>
    public async Task<List<ClientOption>> ClientsAsync(Guid providerId, CancellationToken ct = default)
    {
        var rows = await _db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == providerId && c.ExternalSystem == PlatformInvoiceRules.TenantContactSystem && c.ExternalId != null)
            .Select(c => new { c.Id, c.Name, c.ExternalId }).Take(500).ToListAsync(ct);
        var groups = await _db.ContactNotifyEndpoints.AsNoTracking()
            .Where(e => e.CompanyId == providerId && e.Kind == NotifyEndpointKind.LineGroup && e.UnlinkedAt == null && e.OaScope == NotifyEndpointKind.ScopeNextAcc)
            .Select(e => e.ContactId).Distinct().ToListAsync(ct);
        var list = new List<ClientOption>();
        foreach (var r in rows)
        {
            if (!Guid.TryParse(r.ExternalId, out var cid) || cid == providerId) continue;
            var v = await _consent.CheckAsync(cid, providerId, null, null, ct);
            var poa = await _db.ProviderAutomationConsents.AsNoTracking()
                .Where(x => x.CompanyId == cid && x.ProviderCompanyId == providerId && x.EvidenceSource == ProviderConsent.SourcePowerOfAttorney && x.WithdrawnAt == null && x.ApprovedAt == null)
                .Select(x => new { x.Id, x.GrantedByUserId }).FirstOrDefaultAsync(ct);
            list.Add(new ClientOption(r.Id, cid, r.Name, v.Ok ? "อนุญาตแล้ว" : v.Code == ProviderConsent.Codes.PowerOfAttorneyOnly ? "มีหนังสือมอบอำนาจ"
                : v.Code == ProviderConsent.Codes.PowerOfAttorneyPendingApproval ? "หนังสือมอบอำนาจรออนุมัติ" : "ยังไม่อนุญาต", groups.Contains(r.Id), poa?.Id, poa?.GrantedByUserId));
        }
        return list.OrderBy(x => x.Name).ToList();
    }

    private async Task<Guid> ClientOfAsync(Guid providerId, Guid contactId, CancellationToken ct)
    {
        var ext = await _db.Contacts.AsNoTracking()
            .Where(c => c.Id == contactId && c.CompanyId == providerId && c.ExternalSystem == PlatformInvoiceRules.TenantContactSystem)
            .Select(c => c.ExternalId).FirstOrDefaultAsync(ct);
        if (!Guid.TryParse(ext, out var cid) || cid == providerId || !await _db.Companies.AsNoTracking().AnyAsync(c => c.Id == cid, ct))
            throw new BusinessRuleException("ผู้ติดต่อนี้ไม่ใช่บริษัทลูกค้าที่มีบัญชีในระบบ — ส่งใบขอชำระไม่ได้", TaxSlipMatch.Codes.NoTenant, 400);
        return cid;
    }

    /// <summary>ยอดค้างของบริษัทลูกค้า (เฉพาะแบบที่ชำระกับกรมสรรพากรผ่านช่องทางที่ยืนยันแล้ว)</summary>
    public async Task<List<PendingOption>> PendingAsync(Guid providerId, Guid contactId, CancellationToken ct = default)
    {
        var cid = await ClientOfAsync(providerId, contactId, ct);
        var dash = await _remit.GetDashboardAsync(cid, 24, includeOldAnnual: true);
        return dash.Pending.Where(p => GovPaymentSlip.RdTaxTypes.Contains(p.RemittanceType))
            .Select(p => new PendingOption(p.RemittanceType, TaxSlipIntakeService.FormLabel(p.RemittanceType, p.PeriodYear, p.PeriodMonth), p.PeriodYear, p.PeriodMonth, p.Amount, p.EFilingDueDate))
            .ToList();
    }

    public sealed record CreateInput(Guid ContactId, string RemittanceType, int PeriodYear, int PeriodMonth, decimal Amount, decimal Surcharge, decimal Penalty,
        string ControlCode, DateTime? ControlCodeExpiresAt, DateTime? DueDate, bool IsAmendment, bool SendAnyway);

    public sealed record CreateResult(Guid Id, bool BooksMismatch, string SendOutcome, bool ConsentRequested);

    public async Task<CreateResult> CreateAsync(Guid providerId, Guid staffUserId, CreateInput req, CancellationToken ct = default)
    {
        var cid = await ClientOfAsync(providerId, req.ContactId, ct);
        var dash = await _remit.GetDashboardAsync(cid, 24, includeOldAnnual: true);
        var item = dash.Pending.FirstOrDefault(p => p.RemittanceType == req.RemittanceType && p.PeriodYear == req.PeriodYear && p.PeriodMonth == req.PeriodMonth);
        var check = TaxSlipMatch.RequestProblem(req.RemittanceType, req.Amount, req.Surcharge, req.Penalty, req.ControlCode, item?.Amount, req.SendAnyway, req.IsAmendment);
        if (check.Problem != null) throw new BusinessRuleException(check.Problem, check.RuleCode, 400);
        // รอบ 275 แพ็ก D (M7): ใบที่เปิดได้ทีละใบต่อ (บริษัท·แบบ·งวด) — ส่งใบใหม่ทับโดยใบเก่ายังเปิด ⇒ ลูกค้ามี Control Code สองชุดของงวดเดียว
        var openSame = await _db.TaxPaymentRequests.AsNoTracking()
            .Where(r => r.CompanyId == cid && r.ProviderCompanyId == providerId && r.Status == TaxPaymentRequestLifecycle.Open
                && r.RemittanceType == req.RemittanceType && r.PeriodYear == req.PeriodYear && r.PeriodMonth == req.PeriodMonth)
            .Select(r => r.ControlCodeLast4).FirstOrDefaultAsync(ct);
        if (openSame != null)
            throw new BusinessRuleException($"งวดนี้มีใบขอชำระที่ยังเปิดอยู่ (Control ••{openSame}) — ยกเลิกใบเดิมก่อน (ระบบแจ้งลูกค้าในกลุ่มให้ไม่ชำระตามใบเดิม)",
                TaxSlipMatch.Codes.RequestClosed, 409);
        var cc = TaxSlipRefHash.Canon(req.ControlCode);
        var now = DateTime.UtcNow;
        var row = new TaxPaymentRequest
        {
            CompanyId = cid, ProviderCompanyId = providerId, ProviderContactId = req.ContactId, RemittanceType = req.RemittanceType,
            PeriodYear = req.PeriodYear, PeriodMonth = req.PeriodMonth, Amount = Math.Round(req.Amount, 2, MidpointRounding.AwayFromZero),
            Surcharge = Math.Round(req.Surcharge, 2, MidpointRounding.AwayFromZero), Penalty = Math.Round(req.Penalty, 2, MidpointRounding.AwayFromZero),
            ControlCodeHash = TaxSlipRefHash.ControlCode(cc)!, ControlCodeLast4 = TaxSlipRefHash.Last4(cc)!, ControlCodeExpiresAt = req.ControlCodeExpiresAt,
            DueDate = req.DueDate ?? item?.EFilingDueDate, IsAmendment = req.IsAmendment, BooksMismatch = check.BooksMismatch, BooksAmountAtSend = item?.Amount,
            SentVia = "pending", SentAt = now, SentByUserId = staffUserId, Status = "Open", CreatedBy = staffUserId.ToString(),
        };
        _db.TaxPaymentRequests.Add(row);
        foreach (var co in new[] { cid, providerId })
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = co, UserId = staffUserId, Action = AuditAction.Create, EntityType = nameof(TaxPaymentRequest), EntityId = row.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new { @event = "TaxPaymentRequestSent", clientId = cid, providerId, type = row.RemittanceType, row.PeriodYear, row.PeriodMonth,
                    row.Amount, row.Surcharge, row.Penalty, controlLast4 = row.ControlCodeLast4, row.BooksMismatch, row.IsAmendment, ruleCode = check.RuleCode }),
                Timestamp = now,
            });
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();
            throw new BusinessRuleException("Control Code นี้ใช้กับใบขอชำระอื่นของบริษัทนี้แล้ว", TaxSlipMatch.Codes.Duplicate, 409);
        }
        // T2: ยังไม่อนุญาต ⇒ ขอความยินยอมเอง + ต่อท้ายการ์ด
        var verdict = await _consent.CheckAsync(cid, providerId, null, null, ct);
        var requested = false;
        if (!verdict.Ok) requested = (await _consent.EnsureRequestAsync(cid, providerId, ProviderConsentService.ViaFirstPaymentRequest, staffUserId, ct)).IsNew;
        var name = await _db.Companies.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var facts = new TaxSlipReplyCard.Facts(CompanyName: name, FormLabel: TaxSlipIntakeService.FormLabel(row.RemittanceType, row.PeriodYear, row.PeriodMonth),
            Amount: row.Amount + row.Surcharge + row.Penalty, DueDate: row.DueDate, ControlLast4: row.ControlCodeLast4);
        var outcome = await _channel.PostToClientAsync(providerId, cid,
            show => TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.PaymentRequest, facts, show) + (verdict.Ok ? "" : TaxSlipReplyCard.ConsentHint), ct);
        var tracked = await _db.TaxPaymentRequests.FirstAsync(r => r.Id == row.Id && r.CompanyId == cid, ct);
        tracked.SentVia = outcome.Contains("ส่งแล้ว") ? (outcome.Contains("กลุ่ม LINE: ส่งแล้ว") ? "line-group" : "email") : "none";
        tracked.SendNote = outcome.Length > 500 ? outcome[..500] : outcome;
        await _db.SaveChangesAsync(ct);
        return new CreateResult(row.Id, row.BooksMismatch, outcome, requested);
    }

    public sealed record RequestRow(Guid Id, string ClientName, string FormLabel, decimal Total, string ControlLast4, string Status, bool BooksMismatch, DateTime SentAt, string? SendNote);

    public async Task<List<RequestRow>> ListAsync(Guid providerId, CancellationToken ct = default)
    {
        var rows = await _db.TaxPaymentRequests.AsNoTracking().Where(r => r.ProviderCompanyId == providerId).OrderByDescending(r => r.SentAt).Take(200).ToListAsync(ct);
        var ids = rows.Select(r => r.CompanyId).Distinct().ToList();
        var names = await _db.Companies.AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        return rows.Select(r => new RequestRow(r.Id, names.FirstOrDefault(n => n.Id == r.CompanyId)?.Name ?? "-", TaxSlipIntakeService.FormLabel(r.RemittanceType, r.PeriodYear, r.PeriodMonth),
            r.Amount + r.Surcharge + r.Penalty, r.ControlCodeLast4, TaxPaymentRequestLifecycle.Label(r.Status),
            r.BooksMismatch, r.SentAt, r.SendNote)).ToList();
    }

    /// <summary>รอบ 275 แพ็ก D (M7): ยกเลิก ⇒ แจ้งกลุ่ม/อีเมลที่เคยได้ใบแจ้งชำระว่า<b>อย่าชำระตามใบนี้</b> (เดิมไม่แจ้ง ⇒ ลูกค้าจ่ายตามใบที่ยกเลิก) · ผลการส่งเก็บบนใบ</summary>
    public async Task CancelAsync(Guid providerId, Guid staffUserId, Guid requestId, CancellationToken ct = default)
    {
        var row = await _db.TaxPaymentRequests.FirstOrDefaultAsync(r => r.Id == requestId && r.ProviderCompanyId == providerId, ct) ?? throw new KeyNotFoundException("ไม่พบใบขอชำระ");
        if (row.Status != TaxPaymentRequestLifecycle.Open) throw new BusinessRuleException("ยกเลิกได้เฉพาะใบที่ยังรอชำระ", TaxSlipMatch.Codes.RequestClosed, 409);
        row.Status = TaxPaymentRequestLifecycle.Cancelled; row.UpdatedAt = DateTime.UtcNow;
        foreach (var co in new[] { row.CompanyId, providerId })
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = co, UserId = staffUserId, Action = AuditAction.Update, EntityType = nameof(TaxPaymentRequest), EntityId = row.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new { @event = "TaxPaymentRequestCancelled" }), Timestamp = DateTime.UtcNow,
            });
        await _db.SaveChangesAsync(ct);
        await NotifyClosedAsync(row, TaxSlipReplyCard.Kind.PaymentRequestCancelled, ct);
    }

    private async Task NotifyClosedAsync(TaxPaymentRequest row, TaxSlipReplyCard.Kind kind, CancellationToken ct)
    {
        var name = await _db.Companies.AsNoTracking().Where(c => c.Id == row.CompanyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var facts = new TaxSlipReplyCard.Facts(CompanyName: name, FormLabel: TaxSlipIntakeService.FormLabel(row.RemittanceType, row.PeriodYear, row.PeriodMonth),
            ControlLast4: row.ControlCodeLast4);
        var outcome = await _channel.PostToClientAsync(row.ProviderCompanyId, row.CompanyId, show => TaxSlipReplyCard.Text(kind, facts, show), ct);
        var note = ((row.SendNote ?? "") + " · " + TaxPaymentRequestLifecycle.Label(row.Status) + ": " + (string.IsNullOrWhiteSpace(outcome) ? "ไม่มีปลายทางให้แจ้ง" : outcome)).Trim(' ', '·');
        row.SendNote = note.Length > 500 ? note[..500] : note;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>รอบ 275 แพ็ก D (M7): ใบที่ Control Code หมดอายุ ⇒ Expired + แจ้งกลุ่ม (ผู้เรียก: <c>TaxSlipJob</c> · JobLock) · คืนจำนวนใบ</summary>
    public async Task<int> ExpireDueAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        if (await _consent.ProviderIdAsync(ct) is not Guid providerId) return 0;
        var rows = await _db.TaxPaymentRequests.Where(r => r.ProviderCompanyId == providerId && r.Status == TaxPaymentRequestLifecycle.Open && r.ControlCodeExpiresAt != null)
            .OrderBy(r => r.ControlCodeExpiresAt).Take(100).ToListAsync(ct);
        var n = 0;
        foreach (var r in rows.Where(r => TaxPaymentRequestLifecycle.IsExpired(r.ControlCodeExpiresAt, nowUtc)))
        {
            r.Status = TaxPaymentRequestLifecycle.Expired; r.UpdatedAt = nowUtc;
            foreach (var co in new[] { r.CompanyId, providerId })
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = co, Action = AuditAction.Update, EntityType = nameof(TaxPaymentRequest), EntityId = r.Id.ToString(),
                    NewValues = JsonSerializer.Serialize(new { @event = "TaxPaymentRequestExpired", expiresAt = r.ControlCodeExpiresAt }), Timestamp = nowUtc,
                });
            await _db.SaveChangesAsync(ct);
            await NotifyClosedAsync(r, TaxSlipReplyCard.Kind.PaymentRequestExpired, ct);
            n++;
        }
        return n;
    }

    /// <summary>
    /// รอบ 275 แพ็ก D (M7): งวดนี้ถูกนำส่งแล้ว (สลิป · บันทึกมือที่หน้านำส่ง · ผูกสลิปกับรายการเดิม) ⇒ ใบขอชำระที่ยังเปิดของงวดนั้นปิดเป็น <c>Paid</c> ผูกรายการนำส่ง ·
    /// เดิมเส้นมือไม่ปิด ⇒ ใบค้าง "รอชำระ" ตลอดไป · แบบที่นำส่งหลายครั้งต่องวด (ภ.พ.36 · กท.20ก) ไม่ปิดเอง ·
    /// ผู้เรียก: <c>StatutoryRemittanceService.RemitAsync</c> / <c>TaxSlipRemitService.LinkAsync</c> ในธุรกรรมเดียวกัน (ผู้เรียก SaveChanges)
    /// </summary>
    public static async Task<int> CloseOnRemitAsync(AccountingDbContext db, Guid companyId, string type, int year, int month, Guid remittanceId, Guid? slipId,
        CancellationToken ct = default)
    {
        if (!TaxPaymentRequestLifecycle.ClosesOnRemit(type)) return 0;
        var rows = await db.TaxPaymentRequests.Where(r => r.CompanyId == companyId && r.Status == TaxPaymentRequestLifecycle.Open
            && r.RemittanceType == type && r.PeriodYear == year && r.PeriodMonth == month).ToListAsync(ct);
        foreach (var r in rows)
        {
            r.Status = TaxPaymentRequestLifecycle.Paid;
            r.StatutoryRemittanceId = remittanceId;
            r.TaxSlipEvidenceId = slipId ?? r.TaxSlipEvidenceId;
            r.UpdatedAt = DateTime.UtcNow;
        }
        return rows.Count;
    }
}

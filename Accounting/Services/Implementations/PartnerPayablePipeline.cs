using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Scope = Accounting.Helpers.PartnerConsent.Scope;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 264 P3 (team-264-a2b-design §5 · คำตัดสิน 264-3-1…264-3-9) — <b>ท่อฝั่งผู้ซื้อ (B)</b>: การแจ้ง "ผู้ขายคู่ค้ารับเงินแล้ว" ⇒ ระบบของ B บันทึกจ่ายใบซื้อเอง
/// เมื่อ<b>ทุกช่องมีหลักฐาน</b> และ B ยินยอม (<c>AutoRecordPayment</c>) · ไม่ผ่าน ⇒ กลับเป็นปุ่มเดียวเดิม + เหตุบนแถว + แถว Autopilot
/// <list type="bullet">
/// <item><b>ไม่ทำงานในคำขอของผู้ขาย</b> (เขียนสมุด B ใต้ scope ของคำขอ A = L1 รอบ 258) ⇒ ขั้น <c>PartnerPayable</c> ของ <c>AutopilotJob</c> (JobLock ต่อขั้น) ·
/// รายแถวประเมินซ้ำทุก <see cref="ReevaluateEvery"/> (watermark ต่อแถว <c>AutoEvaluatedAt</c> — ไม่วนอ่านทั้งฐานทุกรอบ)</item>
/// <item><b>idempotent ต่อการรับชำระของผู้ขาย</b>: จองแถวแบบมีเงื่อนไข (Open → Recording) · เลขอ้างอิง canonical <c>PARTNER-PAID-P{paymentId}</c> ·
/// พบการจ่ายที่อ้างแล้ว ⇒ ปิดแถวเป็นบันทึกแล้ว (ไม่สร้างใบที่สอง)</item>
/// <item><b>ความยินยอมตรวจเพื่อเขียน</b>: <see cref="PartnerConsentService.CheckForWriteAsync"/> ในธุรกรรมล็อก (FOR SHARE) ที่ถือไว้<b>ตลอดการบันทึกจ่าย</b> —
/// การถอน/ระงับคู่รอจนการบันทึกจบ (ฝ่ายค้าน P1 M1) · <c>CreatePaymentAsync</c> เปิดธุรกรรมของตัวเองเสมอ ⇒ ธุรกรรมล็อกอยู่บน connection แยก (scope ใหม่) ·
/// ผู้บันทึก = GUID ผู้ให้ความยินยอม (264-2-3 · ไม่ใช่สตริงระบบ) · ยอดเทียบเพดาน = ยอดรวมใบซื้อเป็นบาทก่อนหัก (CapAmount — P1 M3)</item>
/// <item>ตรวจสดก่อนเขียนทุกครั้ง: คู่ค้า · ความยินยอม · สิทธิ์ผู้ให้ · การรับชำระของผู้ขายยังไม่ถูกยกเลิก · ใบซื้อ · ยอด/ภาษีหักของ B · แหล่งเงินออก</item>
/// <item><b>ไม่มีจุดเรียก AI ในเส้นเงิน</b> (ตัวตัดสิน <see cref="PartnerPaySourceAccount"/> · <see cref="PartnerAutoRecordPolicy"/> pure) ·
/// จุดเรียก AI เดียวของไฟล์นี้ = ผังบัญชีของใบซื้อร่างจากคู่ค้า (<see cref="SuggestDraftGlAsync"/> · C-08) ผ่านตัวแนะนำผังเดิม (orchestrator · นักเรียนก่อน · คีย์ GlAccountSuggestion เดิม)</item>
/// </list>
/// </summary>
public class PartnerPayablePipeline
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docs;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PartnerPayablePipeline> _logger;
    private readonly Accounting.Services.Ai.IOcrAiAugmenter? _glAi;

    public const int BatchPerCompany = 50;
    public const int DraftBatchPerCompany = 20;
    /// <summary>ประเมินแถวเดิมซ้ำเมื่อพ้นช่วงนี้ (ความยินยอม/ใบซื้อ/หลักฐานอาจเปลี่ยน)</summary>
    public static readonly TimeSpan ReevaluateEvery = TimeSpan.FromMinutes(15);
    /// <summary>ป้ายบนใบซื้อร่างว่าเติมผังบัญชีจากตัวแนะนำแล้ว (ครั้งเดียวต่อใบ — ไม่ถาม AI ซ้ำทุกรอบ)</summary>
    public const string DraftGlMarker = "[PARTNER-DRAFT-GL]";

    public PartnerPayablePipeline(AccountingDbContext db, IDocumentService docs, IServiceScopeFactory scopes, ILogger<PartnerPayablePipeline> logger,
        Accounting.Services.Ai.IOcrAiAugmenter? glAi = null)
    { _db = db; _docs = docs; _scopes = scopes; _logger = logger; _glAi = glAi; }

    /// <summary>หนึ่งรอบของงานเบื้องหลัง (ผู้เรียกเดียว = <c>AutopilotJob</c> ขั้น PartnerPayable) — คืนจำนวนงานที่ทำ (บันทึกจ่าย + ใบร่างที่เติมผัง)</summary>
    public async Task<int> RunOnceAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var name = Scope.AutoRecordPayment.ToString();
        // บริษัทที่มีความยินยอมบันทึกจ่ายที่ยังใช้อยู่ — ไม่มี = ไม่มีงาน (ปุ่มเดียวเดิมทำงานเหมือนเดิม)
        var payers = await _db.PartnerAutomationConsents.AsNoTracking()
            .Where(c => c.WithdrawnAt == null && c.Scope == name).Select(c => c.CompanyId).Distinct().ToListAsync(ct);
        var done = 0;
        foreach (var companyId in payers)
            done += await RunForCompanyAsync(companyId, nowUtc, ct);
        // C-08: ใบซื้อร่างจากคู่ค้าของบริษัทที่เปิดสร้างใบร่าง ⇒ ผังบัญชีจากตัวแนะนำเดิม (ไม่อนุมัติเอง)
        var drafters = await _db.WorkflowAutomationConfigs.AsNoTracking()
            .Where(c => c.CreatePurchaseDraftsFromPartners).Select(c => c.CompanyId).ToListAsync(ct);
        foreach (var companyId in drafters)
            done += await SuggestDraftGlAsync(companyId, nowUtc, ct);
        return done;
    }

    /// <summary>แถวที่ถึงรอบประเมินของบริษัทผู้ซื้อหนึ่งราย (tenant = <paramref name="companyId"/> ทุก query)</summary>
    public async Task<int> RunForCompanyAsync(Guid companyId, DateTime nowUtc, CancellationToken ct = default)
    {
        var due = nowUtc - ReevaluateEvery;
        var stale = nowUtc - PartnerPaymentNoticeRule.RecordingStale;
        var ids = await _db.PartnerPaymentNotices.AsNoTracking()
            .Where(n => n.CompanyId == companyId && !n.IsDeleted && n.SellerVoidedAt == null
                && (n.Status == PartnerPaymentNoticeRule.StatusOpen
                    || (n.Status == PartnerPaymentNoticeRule.StatusRecording && (n.UpdatedAt ?? n.CreatedAt) < stale))
                && (n.AutoEvaluatedAt == null || n.AutoEvaluatedAt < due))
            .OrderBy(n => n.AutoEvaluatedAt ?? DateTime.MinValue).ThenBy(n => n.PaidAt)
            .Select(n => n.Id).Take(BatchPerCompany).ToListAsync(ct);
        var recorded = 0;
        foreach (var id in ids)
            if (await ProcessAsync(companyId, id, nowUtc, ct)) recorded++;
        return recorded;
    }

    /// <summary>ประเมิน/บันทึกการแจ้งหนึ่งแถว — true = บันทึกจ่ายแล้ว (ใหม่หรือพบของเดิม) · ไม่โยน (ล้ม = แถวกลับเป็นรอบันทึก + เหตุ + LogError)</summary>
    public async Task<bool> ProcessAsync(Guid companyId, Guid noticeId, DateTime nowUtc, CancellationToken ct = default)
    {
        var staleBefore = nowUtc - PartnerPaymentNoticeRule.RecordingStale;
        // จองแบบมีเงื่อนไข — ชนกับปุ่มเดียว/การถอนของผู้ขายได้คนเดียว
        var claimed = await _db.PartnerPaymentNotices
            .Where(x => x.Id == noticeId && x.CompanyId == companyId && x.SellerVoidedAt == null
                && (x.Status == PartnerPaymentNoticeRule.StatusOpen
                    || (x.Status == PartnerPaymentNoticeRule.StatusRecording && x.UpdatedAt < staleBefore)))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, PartnerPaymentNoticeRule.StatusRecording).SetProperty(x => x.UpdatedAt, (DateTime?)nowUtc), ct);
        if (claimed != 1) return false;
        var n = await _db.PartnerPaymentNotices.AsNoTracking().FirstAsync(x => x.Id == noticeId && x.CompanyId == companyId, ct);
        try
        {
            // กันซ้ำก่อนทุกอย่าง: การจ่ายที่อ้างการแจ้งนี้มีอยู่แล้ว (รอบก่อนล้มหลังบันทึก) ⇒ ปิดแถว
            var refs = PartnerPaymentNoticeRule.PaymentReferencesToCheck(n.SellerPaymentId, n.PaymentIntentId);
            var existing = await _db.Payments.AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.Reference != null && refs.Contains(p.Reference))
                .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
            if (existing is Guid already)
            {
                await MarkRecordedAsync(companyId, noticeId, already, null, null, null, nowUtc, ct);
                return true;
            }

            var (plan, doc) = await PartnerPaymentNoticeService.PlanForAsync(_db, companyId, n, ct);
            var live = await PartnerPaymentNoticeService.SellerPaymentLiveAsync(_db, n, ct);
            var src = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
                .Where(d => d.Id == n.SourceDocumentId && d.CompanyId == n.SourceCompanyId)
                .Select(d => new { d.Status, d.IsDeleted, d.ReceiptForm, d.VatAmount }).FirstOrDefaultAsync(ct);
            var usable = await UsablePayAccountsAsync(_db, companyId, ct);

            // ── ธุรกรรมล็อกความยินยอม (connection แยก) — ถือไว้จนบันทึกจ่ายเสร็จ ──
            using var lockScope = _scopes.CreateScope();
            var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var consentSvc = lockScope.ServiceProvider.GetRequiredService<PartnerConsentService>();
            await using var lockTx = await lockDb.Database.BeginTransactionAsync(ct);
            var consent = doc == null
                ? null
                : await consentSvc.CheckForWriteAsync(companyId, n.TradingPartnershipId, Scope.AutoRecordPayment, PartnerConsent.CapAmount.Thb(doc.TotalAmount), ct);
            var source = await SourceDecisionAsync(_db, companyId, n, doc, usable, consent, ct);
            var verdict = PartnerAutoRecordPolicy.Decide(new PartnerAutoRecordPolicy.Facts(
                NoticeOpen: true,
                SellerPaymentKnown: live != null,
                SellerPaymentLive: live == true,
                SourceDocumentVoided: src == null || src.IsDeleted || src.Status == DocumentStatus.Voided,
                ConsentOk: consent?.Ok == true,
                ConsentReason: consent?.Reason ?? "ไม่พบใบซื้อปลายทาง",
                Plan: plan,
                Currency: doc?.Currency,
                SourceIsAbbreviated: src?.ReceiptForm == ReceiptForm.ReceiptTaxInvoiceAbbreviated,
                PayerBorneWht: (doc?.WhtPayerBorneAmount ?? 0m) > 0m,
                VatMismatch: doc != null && src != null && !PartnerInputVatEvidence.VatMatches(src.VatAmount, doc.VatAmount),
                Source: source));
            if (!verdict.Auto || consent?.GrantorUserId is not Guid grantor || source.BankAccountId is not Guid bank || plan.Amount is not decimal amount)
            {
                await lockTx.RollbackAsync(ct);
                if (verdict.Kind == PartnerAutoRecordPolicy.Kind.Refuse && verdict.Code == PartnerAutoRecordPolicy.Codes.SellerVoided)
                    await WithdrawStaleAsync(n, nowUtc, ct);
                else
                    await ReleaseAsync(n, verdict.Auto ? PartnerAutoRecordPolicy.Codes.Consent : verdict.Code,
                        verdict.Auto ? "ไม่พบผู้ให้ความยินยอม — ระบบไม่บันทึกเอง" : verdict.Reason, nowUtc, ct);
                return false;
            }

            var reference = PartnerPaymentNoticeRule.PaymentReferenceFor(n.SellerPaymentId, n.PaymentIntentId)!;
            Models.DTOs.Document.PaymentResponse pay;
            try
            {
                pay = await _docs.CreatePaymentAsync(companyId, new Models.DTOs.Document.CreatePaymentRequest(
                    DocumentId: n.TargetDocumentId, PaymentDate: n.PaidAt, Amount: amount, PaymentMethod: PartnerPaymentNoticeRule.DefaultMethod(n.SourceChannel),
                    Reference: reference, BankAccount: null,
                    Notes: $"บันทึกจ่ายอัตโนมัติตามการแจ้งของผู้ขายคู่ค้า ({PartnerPaymentNoticeRule.ChannelLabel(n.SourceChannel)}) — {n.SourceDocumentNumber} · ความยินยอม {consent.ConsentId:N}",
                    OverrideBankAccountId: bank, WithholdingTaxAmount: plan.Wht), grantor.ToString(), PaymentEntrySource.PartnerAuto);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or BusinessRuleException or DbUpdateException)
            {
                // เส้นจ่ายเดิมปฏิเสธ (งวดปิด · ยอดเกิน · วันที่ ฯลฯ — ตัวตรวจตัวเดียวกับปุ่มเดียว) ⇒ กลับเป็นปุ่มเดียว + เหตุของมัน
                await lockTx.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();
                await ReleaseAsync(n, PartnerAutoRecordPolicy.Codes.PostFailed, "บันทึกจ่ายอัตโนมัติไม่สำเร็จ: " + ex.Message + " — กดบันทึกเองได้", nowUtc, ct);
                return false;
            }
            await lockTx.CommitAsync(ct);
            await MarkRecordedAsync(companyId, noticeId, pay.Id, consent, source, plan, nowUtc, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ท่อบันทึกจ่ายจากการแจ้งคู่ค้า {Notice} ของบริษัท {Company} ล้ม — แถวกลับเป็นรอบันทึก (กดบันทึกเองได้)", noticeId, companyId);
            try
            {
                _db.ChangeTracker.Clear();
                await ReleaseAsync(n, PartnerAutoRecordPolicy.Codes.PostFailed, "ระบบบันทึกเองไม่สำเร็จ (ข้อผิดพลาดภายใน) — กดบันทึกเองได้ · ระบบลองใหม่รอบถัดไป", nowUtc, CancellationToken.None);
            }
            catch (Exception rex) when (rex is not OperationCanceledException)
            {
                _logger.LogError(rex, "ปล่อยแถวแจ้ง {Notice} กลับเป็นรอบันทึกไม่สำเร็จ (ค้างกำลังบันทึก — ปุ่มเดียวกดได้หลัง {Min} นาที)", noticeId, PartnerPaymentNoticeRule.RecordingStale.TotalMinutes);
            }
            return false;
        }
    }

    /// <summary>ปิดแถวเป็น "บันทึกแล้ว" + audit (ความยินยอม · ที่มาของบัญชี · ผู้แพ้ · ยอด · ภาษีหัก) · ผู้ขายยกเลิกระหว่างบันทึก ⇒ ป้ายบนใบซื้อ</summary>
    private async Task MarkRecordedAsync(Guid companyId, Guid noticeId, Guid paymentId, PartnerConsent.UseCheck? consent, PartnerPaySourceAccount.Decision? source,
        PartnerPaymentNoticeRule.Plan? plan, DateTime nowUtc, CancellationToken ct)
    {
        var fresh = await _db.PartnerPaymentNotices.FirstAsync(x => x.Id == noticeId && x.CompanyId == companyId, ct);
        fresh.Status = PartnerPaymentNoticeRule.StatusRecorded;
        fresh.RecordedPaymentId = paymentId;
        fresh.ResolvedAt = nowUtc;
        fresh.ResolvedBy = consent?.GrantorUserId?.ToString() ?? "partner-auto";
        fresh.AutoBlockedReason = null;
        fresh.AutoBlockedCode = null;
        fresh.AutoEvaluatedAt = nowUtc;
        fresh.UpdatedAt = nowUtc;
        PartnerPaymentNoticeService.Audit(_db, companyId, consent?.GrantorUserId, consent == null ? "PartnerPaymentNoticeRecorded" : "PartnerPaymentAutoRecorded", fresh,
            extra: consent == null ? (object)new { recoveredExistingPayment = true } : new
            {
                consentId = consent.ConsentId, grantorUserId = consent.GrantorUserId, origin = source?.Origin.ToString(), bankAccountId = source?.BankAccountId,
                confidence = source?.Confidence, losers = source?.Losers.Select(l => new { origin = l.Origin.ToString(), l.BankAccountId }),
                amount = plan?.Amount, wht = plan?.Wht, ruleCode = PartnerAutoRecordPolicy.RuleCode, legalReference = PartnerAutoRecordPolicy.LegalReference,
                consentRuleCode = PartnerConsent.RuleCode, usedAi = false,
            });
        await _db.SaveChangesAsync(ct);
        if (fresh.SellerVoidedAt != null || await PartnerPaymentNoticeService.SellerPaymentLiveAsync(_db, fresh, ct) == false)
            await PartnerPaymentNoticeService.FlagRecordedVoidedAsync(_db, companyId, fresh.TargetDocumentId, "ผู้ขาย", fresh.SourceDocumentNumber, fresh.CashAmount, nowUtc, ct);
    }

    /// <summary>ไม่ผ่าน ⇒ แถวกลับเป็นรอบันทึก + เหตุ · audit <c>PartnerPaymentAutoBlocked</c> เฉพาะเมื่อเหตุเปลี่ยน (ครั้งแรกต่อเหตุ — ไม่สแปม)</summary>
    private async Task ReleaseAsync(PartnerPaymentNotice n, string code, string reason, DateTime nowUtc, CancellationToken ct)
    {
        var r = reason.Length > 500 ? reason[..500] : reason;
        await _db.PartnerPaymentNotices
            .Where(x => x.Id == n.Id && x.CompanyId == n.CompanyId && x.Status == PartnerPaymentNoticeRule.StatusRecording)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, PartnerPaymentNoticeRule.StatusOpen)
                .SetProperty(x => x.AutoBlockedReason, r)
                .SetProperty(x => x.AutoBlockedCode, code)
                .SetProperty(x => x.AutoEvaluatedAt, (DateTime?)nowUtc)
                .SetProperty(x => x.UpdatedAt, (DateTime?)nowUtc), ct);
        if (n.AutoBlockedCode == code) return;
        var fresh = await _db.PartnerPaymentNotices.AsNoTracking().FirstAsync(x => x.Id == n.Id && x.CompanyId == n.CompanyId, ct);
        PartnerPaymentNoticeService.Audit(_db, n.CompanyId, null, "PartnerPaymentAutoBlocked", fresh,
            extra: new { code, reason = r, ruleCode = PartnerAutoRecordPolicy.RuleCode, legalReference = PartnerAutoRecordPolicy.LegalReference, usedAi = false });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>ผู้ขายยกเลิกการรับชำระแล้วแต่ขั้นถอนตอนยกเลิกไม่ได้ทำงาน (ล้ม/แถวเก่า) ⇒ ถอนที่นี่ (ตรวจสด — ตาข่ายชั้นสอง)</summary>
    private async Task WithdrawStaleAsync(PartnerPaymentNotice n, DateTime nowUtc, CancellationToken ct)
    {
        await _db.PartnerPaymentNotices
            .Where(x => x.Id == n.Id && x.CompanyId == n.CompanyId && x.Status == PartnerPaymentNoticeRule.StatusRecording)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, PartnerPaymentNoticeRule.StatusWithdrawn)
                .SetProperty(x => x.SellerVoidedAt, (DateTime?)nowUtc)
                .SetProperty(x => x.ResolvedAt, (DateTime?)nowUtc)
                .SetProperty(x => x.ResolvedBy, "partner-seller-voided")
                .SetProperty(x => x.AutoBlockedReason, PartnerAutoRecordPolicy.SellerVoidedReason)
                .SetProperty(x => x.AutoBlockedCode, PartnerAutoRecordPolicy.Codes.SellerVoided)
                .SetProperty(x => x.AutoEvaluatedAt, (DateTime?)nowUtc)
                .SetProperty(x => x.UpdatedAt, (DateTime?)nowUtc), ct);
        var fresh = await _db.PartnerPaymentNotices.AsNoTracking().FirstAsync(x => x.Id == n.Id && x.CompanyId == n.CompanyId, ct);
        PartnerPaymentNoticeService.Audit(_db, n.CompanyId, null, "PartnerPaymentNoticeWithdrawn", fresh,
            extra: new { detectedBy = "pipeline", ruleCode = PartnerAutoRecordPolicy.RuleCode });
        await _db.SaveChangesAsync(ct);
    }

    // ═══════════ ข้อเท็จจริงของตัวตัดสินแหล่งเงิน (ผู้อ่านเดียว — ปุ่มเดียวและท่อใช้ร่วม) ═══════════

    /// <summary>บัญชีของผู้ซื้อที่ใช้จ่ายออกได้ → ป้าย · ตัวตรวจเดียวกับฝั่งรับ (<see cref="SlipReceivingAccount.UsableProblem"/> คู่สมมาตร) + ผังที่ลงได้
    /// (<see cref="DocumentMoneyChannel.EvaluateAsync"/>) · ของบริษัทผู้เรียกเท่านั้น</summary>
    public static async Task<Dictionary<Guid, string>> UsablePayAccountsAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var rows = await db.BankAccounts.AsNoTracking()
            .Where(b => b.CompanyId == companyId && !b.IsDeleted)
            .OrderBy(b => b.BankName).ThenBy(b => b.AccountNumber).ThenBy(b => b.Id)
            .Select(b => new { b.Id, b.BankName, b.AccountName, b.AccountNumber, b.IsActive, b.Currency, b.AccountType })
            .ToListAsync(ct);
        var map = new Dictionary<Guid, string>();
        foreach (var b in rows)
        {
            var label = $"{b.BankName} · {b.AccountName} · ••{(b.AccountNumber.Length > 4 ? b.AccountNumber[^4..] : b.AccountNumber)}";
            var gl = await DocumentMoneyChannel.EvaluateAsync(db, companyId, b.Id, null, MoneyChannelCheck.Post, "แก้ที่หน้า “บัญชีธนาคาร”", ct);
            // เงินออก: ไม่ต้องมีเลขรับโอน (HasTransferId = true) — ส่วนอื่นเหมือนฝั่งรับ (ใช้งาน · บาท · ไม่ใช่ฝากประจำ · ผูกผังที่ลงได้)
            var problem = SlipReceivingAccount.UsableProblem(
                new SlipReceivingAccount.AccountFacts(b.Id, label, b.IsActive, b.Currency ?? "THB", b.AccountType ?? "", true, gl.Problem), "THB");
            if (problem == null) map[b.Id] = label;
        }
        return map;
    }

    /// <summary>ตัวตัดสินแหล่งเงินของการแจ้งหนึ่งแถว — โหลดข้อเท็จจริงแล้วเรียก <see cref="PartnerPaySourceAccount.Decide"/> (ตัวเดียว · ผู้เรียก: ท่อ + หน้า)</summary>
    public static async Task<PartnerPaySourceAccount.Decision> SourceDecisionAsync(AccountingDbContext db, Guid companyId, PartnerPaymentNotice n, Document? purchase,
        IReadOnlyDictionary<Guid, string> usable, PartnerConsent.UseCheck? consent, CancellationToken ct = default)
    {
        var bankLine = await BankLineEvidenceAsync(db, companyId, n, ct);
        var slip = await SlipEvidenceAsync(db, companyId, n, ct);
        Guid? history = null;
        if (purchase != null)
        {
            var since = DateTime.UtcNow.AddMonths(-12);
            history = await db.Payments.AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.BankAccountId != null && p.PaymentDate >= since
                    && p.Document.CompanyId == companyId && p.Document.ContactId == purchase.ContactId)
                .GroupBy(p => p.BankAccountId)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key).FirstOrDefaultAsync(ct);
        }
        return PartnerPaySourceAccount.Decide(new PartnerPaySourceAccount.Facts(
            Usable: usable,
            BankLine: bankLine,
            PayerDeclared: null,   // แพ็ก P4 ("จ่ายจากระบบของเรา" — PartnerPaymentPlans) ป้อนที่นี่
            SlipMatch: slip,
            ConsentDefault: consent is { Ok: true } ? consent.DefaultPayBankAccountId : null,
            ConsentDefaultProblem: consent?.PayAccountProblem,
            History: history,
            PurchaseDocBank: purchase?.BankAccountId));
    }

    /// <summary>ชั้นที่ 1 — รายการเดินบัญชีฝั่งเงินออกของผู้ซื้อที่ตรงการแจ้งเดียว (ตัวตัดสิน <c>BankPaymentLineMatch</c> ของแพ็ก P4) · <b>ยังไม่มีผู้ป้อน</b> ⇒ ไม่มีหลักฐานชั้นนี้</summary>
    private static Task<Guid?> BankLineEvidenceAsync(AccountingDbContext db, Guid companyId, PartnerPaymentNotice n, CancellationToken ct)
    {
        _ = db; _ = companyId; _ = n; _ = ct;
        return Task.FromResult<Guid?>(null);
    }

    /// <summary>ชั้นที่ 3 — สลิปของผู้ซื้อที่ตรงบัญชีเดียว (ตัวจับคู่ <c>SlipAccountMatch</c> ของแพ็ก P2) · <b>ยังไม่มีผู้ป้อน</b> ⇒ ไม่มีหลักฐานชั้นนี้ (ไม่เดาจากสลิปที่อ่านไม่ได้)</summary>
    private static Task<PartnerPaySourceAccount.Slip?> SlipEvidenceAsync(AccountingDbContext db, Guid companyId, PartnerPaymentNotice n, CancellationToken ct)
    {
        _ = db; _ = companyId; _ = n; _ = ct;
        return Task.FromResult<PartnerPaySourceAccount.Slip?>(null);
    }

    // ═══════════ แถว Autopilot "การแจ้งชำระจากคู่ค้าค้าง" (query ของท่อ — ผู้อ่าน: AutopilotService.PartnerPayBlockedIssuesAsync) ═══════════

    /// <summary>แถวค้างของบริษัทหนึ่ง (อ่านอย่างเดียว · ผูก CompanyId): (ก) รอบันทึกเกิน 1 วัน / มีเหตุจากท่อ (ข) กำลังบันทึกค้าง (ค) บันทึกแล้วแต่ผู้ขายยกเลิกการรับเงิน
    /// และการจ่ายของเรายังมีผล</summary>
    public sealed record BlockedRow(Guid Id, string SourceDocumentNumber, Guid TargetDocumentId, decimal CashAmount, decimal SellerRecordedWht, DateTime PaidAt,
        string? Note, string? AutoBlockedReason, DateTime CreatedAt, bool SellerVoidedAfterRecord);

    public static async Task<List<BlockedRow>> BlockedRowsAsync(AccountingDbContext db, Guid companyId, DateTime nowUtc, CancellationToken ct = default)
    {
        var openCut = nowUtc.AddDays(-1);
        var staleCut = nowUtc - PartnerPaymentNoticeRule.RecordingStale;
        var pending = await db.PartnerPaymentNotices.AsNoTracking()
            .Where(n => n.CompanyId == companyId && !n.IsDeleted && n.SellerVoidedAt == null
                && ((n.Status == PartnerPaymentNoticeRule.StatusOpen && (n.CreatedAt < openCut || n.AutoBlockedReason != null))
                    || (n.Status == PartnerPaymentNoticeRule.StatusRecording && (n.UpdatedAt ?? n.CreatedAt) < staleCut)))
            .OrderBy(n => n.PaidAt).Take(50)
            .Select(n => new BlockedRow(n.Id, n.SourceDocumentNumber, n.TargetDocumentId, n.CashAmount, n.SellerRecordedWht, n.PaidAt, n.Note, n.AutoBlockedReason,
                n.CreatedAt, false))
            .ToListAsync(ct);
        var voided = await db.PartnerPaymentNotices.AsNoTracking()
            .Where(n => n.CompanyId == companyId && !n.IsDeleted && n.Status == PartnerPaymentNoticeRule.StatusRecorded && n.SellerVoidedAt != null
                && db.Payments.Any(p => p.Id == n.RecordedPaymentId && p.CompanyId == companyId && !p.IsDeleted))
            .OrderByDescending(n => n.SellerVoidedAt).Take(50)
            .Select(n => new BlockedRow(n.Id, n.SourceDocumentNumber, n.TargetDocumentId, n.CashAmount, n.SellerRecordedWht, n.PaidAt, n.Note, n.AutoBlockedReason,
                n.SellerVoidedAt ?? n.CreatedAt, true))
            .ToListAsync(ct);
        return pending.Concat(voided).ToList();
    }

    // ═══════════ C-08: ผังบัญชีของใบซื้อร่างจากคู่ค้า (ตัวแนะนำเดิม · ไม่อนุมัติเอง) ═══════════

    /// <summary>
    /// ใบซื้อร่างที่สร้างจากเอกสารของคู่ค้า (<c>PartnerSourceLinkId</c>) ยังไม่มีผังบัญชี ⇒ ถามตัวแนะนำผังเดิม <c>IOcrAiAugmenter.SuggestGlAccountAsync</c>
    /// (orchestrator · นักเรียนก่อน · คีย์เดิม GlAccountSuggestion — ไม่เพิ่ม AiFeatureKey) · เติมเฉพาะคำตอบที่ผ่าน <see cref="GlSuggestionApplyPolicy.MayAutoFill"/>
    /// และ<b>อยู่ในผังที่ใช้งานของบริษัท</b> (anti-hallucination) · เก็บ <c>GlAccountAiFeedbackId</c> ต่อบรรทัด (ตอนผู้ใช้อนุมัติ/แก้ ระบบเรียก RecordUserChoice เดิม) ·
    /// ป้ายซื่อสัตย์บนหมายเหตุภายในของใบ ("🤖 AI" เฉพาะเมื่อเรียกจริง · "⚙️ ระบบ" เมื่อโมเดลในบ้านตอบ) · ถามครั้งเดียวต่อใบ (<see cref="DraftGlMarker"/>) ·
    /// kill-switch: AI ปิด + นักเรียนไม่ถึงเกณฑ์ ⇒ ใบยังเป็นร่างที่ไม่มีผัง (เท่าเดิม — ผู้ใช้เลือกตอนอนุมัติ) ไม่พัง
    /// </summary>
    public async Task<int> SuggestDraftGlAsync(Guid companyId, DateTime nowUtc, CancellationToken ct = default)
    {
        if (_glAi == null) return 0;
        var since = nowUtc.AddDays(-30);
        var drafts = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.PartnerSourceLinkId != null && d.Status == DocumentStatus.Draft
                && d.DocumentType == DocumentType.PurchaseInvoice && d.CreatedAt >= since
                && (d.InternalNotes == null || !d.InternalNotes.Contains(DraftGlMarker))
                && d.Lines.Any(l => !l.IsDeleted && l.AccountId == null))
            .OrderBy(d => d.CreatedAt).Take(DraftBatchPerCompany)
            .Select(d => new { d.Id, d.ContactId })
            .ToListAsync(ct);
        if (drafts.Count == 0) return 0;
        var coa = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.IsActive && !a.IsDeleted)
            .Select(a => new { a.Id, a.AccountCode }).ToListAsync(ct);
        var byCode = coa.GroupBy(a => a.AccountCode).ToDictionary(g => g.Key, g => g.First().Id);
        var done = 0;
        foreach (var d in drafts)
        {
            try
            {
                var vendor = await _db.Contacts.AsNoTracking().Where(c => c.Id == d.ContactId && c.CompanyId == companyId)
                    .Select(c => new { c.Name, c.TaxId }).FirstOrDefaultAsync(ct);
                var lines = await _db.DocumentLines.Where(l => l.DocumentId == d.Id && !l.IsDeleted && l.AccountId == null).ToListAsync(ct);
                int filled = 0, asked = 0;
                bool usedAi = false, fromStudent = false;
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line.Description) || line.Amount <= 0m) continue;
                    asked++;
                    var sugg = await _glAi.SuggestGlAccountAsync(companyId, Guid.Empty, vendor?.Name, vendor?.TaxId, null, line.Description, line.Amount, "THB",
                        null, 0m, ct: ct);
                    usedAi |= sugg.UsedAi;
                    fromStudent |= sugg.FromStudent;
                    var inCoa = sugg.Answer != null && byCode.ContainsKey(sugg.Answer);
                    if (!GlSuggestionApplyPolicy.MayAutoFill(sugg.Answer, sugg.Confidence, sugg.HasModelAnswer, inCoa)) continue;
                    line.AccountId = byCode[sugg.Answer!];
                    line.GlAccountAiFeedbackId = sugg.FeedbackId;
                    filled++;
                }
                var who = AiAnswerSource.Of(usedAi, fromStudent);
                var whoLabel = AiAnswerSource.Label(who);   // ป้ายซื่อสัตย์ (กฎ #1): 🤖 AI เฉพาะเมื่อเรียกจริง
                var note = $"{DraftGlMarker} {nowUtc.AddHours(7):dd/MM/yyyy}: ผังบัญชีจาก {whoLabel} — เติม {filled}/{asked} บรรทัด"
                    + (filled < asked ? " (บรรทัดที่เหลือเลือกเองตอนตรวจ)" : "") + " · ตรวจผังแล้วอนุมัติใบซื้อเอง (ระบบไม่อนุมัติแทน)";
                var doc = await _db.Documents.FirstAsync(x => x.Id == d.Id && x.CompanyId == companyId, ct);
                if (doc.Status != DocumentStatus.Draft) { _db.ChangeTracker.Clear(); continue; }   // ผู้ใช้อนุมัติไปแล้วระหว่างรอ — ไม่แตะ
                doc.InternalNotes = string.IsNullOrWhiteSpace(doc.InternalNotes) ? note : doc.InternalNotes + "\n" + note;
                doc.UpdatedAt = nowUtc;
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId, Action = AuditAction.Update, EntityType = "Document", EntityId = d.Id.ToString(),
                    NewValues = JsonSerializer.Serialize(new
                    {
                        @event = "PartnerDraftGlSuggested", filled, asked, usedAi, fromLocalModel = fromStudent, ruleCode = PartnerExchangeRules.RuleCode,
                    }),
                    Timestamp = nowUtc,
                });
                await _db.SaveChangesAsync(ct);
                _db.ChangeTracker.Clear();
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                _logger.LogError(ex, "เติมผังบัญชีใบซื้อร่างจากคู่ค้า {Doc} ของบริษัท {Company} ไม่สำเร็จ (ใบยังเป็นร่าง — เลือกผังเองตอนตรวจ)", d.Id, companyId);
            }
        }
        return done;
    }
}

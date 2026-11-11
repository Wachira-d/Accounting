using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Recurring;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Storage;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 248 — รายการประจำแบบ Autopilot (AUTOPILOT_BILLING_DESIGN §4.3–§4.5 · §5.1) · <b>ตัวต่อสาย + ตัวรวบรวมข้อเท็จจริง</b> เท่านั้น (หลักการ #1):
/// ความพร้อม = <see cref="AutopilotReadiness.Evaluate"/> · คำเตือนก่อนอนุมัติ = ตัวรวบรวมของ <c>DocumentService</c> บนใบจำลอง ·
/// สิ่งที่หยุด = <see cref="ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource, IReadOnlyList{string}, AutoApprovePolicy?)"/> ·
/// ยอดในลิงก์ = <see cref="DocumentPayLinkPolicy.AmountsFor"/> · ช่องทาง = <see cref="AutopilotSettingsResolver"/>
///
/// <para><b>ความพร้อม/ทดลองรันไม่เขียนอะไรเลย</b> — ไม่มี SaveChanges · ไม่ออกลิงก์ · ไม่ส่ง · ไม่จองโควตา (ล็อกด้วย Db test
/// <c>RecurringAutopilotRound248DbTests</c> + <c>tools/required_call_site_check.py</c>) · ทุก query กรองบริษัท</para>
/// </summary>
public class RecurringAutopilotService : IRecurringAutopilotService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docs;
    private readonly IDocumentPayLinkService? _payLinks;
    private readonly IPermissionService? _permissions;
    /// <summary>รอบ 250: ค่า <c>App:BaseUrl</c> สำหรับตัวตัดสิน "ตั้ง URL ของระบบแล้วไหม" (null = นอก DI ⇒ ดูแค่ค่าที่ผู้ดูแลตั้ง)</summary>
    private readonly IConfiguration? _config;
    /// <summary>รอบ 251 (WP-07 · PAY-QR): ที่เก็บกลาง — ตัวเลือกบัญชี QR ตัวเดียวกับกระดาษต้องรู้ว่ารูป QR อยู่ใต้โฟลเดอร์ของบริษัทไหม (null = ไม่ตรวจข้อ PAY-QR)</summary>
    private readonly IFileStorage? _storage;
    /// <summary>รอบ 251 (WP-07 ข้อ 4): ผู้ส่งอีเมลของ "ส่งทดสอบถึงอีเมลฉัน" (null = ส่งทดสอบไม่ได้ — 503 พร้อมเหตุ)</summary>
    private readonly IEmailSenderFactory? _senders;

    /// <summary>รอบ 250 WP-08: อีเมลที่ "จะส่งถึง" ในการทดลองรัน/แผงความพร้อม = ตัวหาผู้รับเดียวกับผู้ส่งจริง (เรื่อง "เอกสาร")</summary>
    private readonly IContactRecipientResolver _recipients;

    public RecurringAutopilotService(AccountingDbContext db, IDocumentService docs,
        IDocumentPayLinkService? payLinks = null, IPermissionService? permissions = null, IConfiguration? config = null,
        IFileStorage? storage = null, IEmailSenderFactory? senders = null, IContactRecipientResolver? recipients = null)
    {
        _db = db;
        _docs = docs;
        _payLinks = payLinks;
        _permissions = permissions;
        _config = config;
        _storage = storage;
        _senders = senders;
        _recipients = recipients ?? ContactRecipientResolver.EmailOnly(db);
    }

    // ═══════════════ ตัวเลือก ═══════════════

    public async Task<AutopilotOptionsResponse> OptionsAsync(Guid companyId, CancellationToken ct = default)
    {
        var facts = await CompanyPayFactsAsync(companyId, ct);
        var available = facts == null ? Array.Empty<string>() : DocumentPayLinkPolicy.CompanyAvailable(facts);
        return new AutopilotOptionsResponse(
            AutopilotSettingsResolver.Modes.Select(m => new AutopilotOption(m, AutopilotSettingsResolver.Label(m))).ToList(),
            available.Select(c => new AutopilotOption(c, DocumentPayLinkPolicy.Label(c))).ToList(),
            AutoApprovePolicy.Catalogue.Select(c => new AutopilotOption(c.Code, c.Title, c.Why)).ToList(),
            AutoApprovePolicy.NeverList.Select(c => new AutopilotOption(c.Code, c.Title, c.Why)).ToList(),
            facts == null ? "ยังไม่ได้ตั้งค่ารับชำระเงินออนไลน์" : DocumentPayLinkPolicy.UnavailableReason(facts),
            PostPayEmailOptions: AutopilotSettingsResolver.TriStates.Select(s => new AutopilotOption(s,
                AutopilotSettingsResolver.TriStateLabel(s, "ส่งใบเสร็จถึงลูกค้าอัตโนมัติหลังรับชำระ", "ไม่ส่งใบเสร็จอัตโนมัติ"))).ToList(),
            PostPayPartialOptions: AutopilotSettingsResolver.TriStates.Select(s => new AutopilotOption(s,
                AutopilotSettingsResolver.TriStateLabel(s, "ส่งทุกงวดที่รับ (รวมชำระบางส่วน)", "ส่งเฉพาะงวดที่ชำระครบ"))).ToList(),
            // รอบ 257 ชุด 4 (A-H2 · E-M3): ค่าตั้งต้นจากตัวตัดสินเดียว (AutoApprovePolicy.RecurringDefault) — หน้าไม่มีสำเนารหัส
            RecurringDefaultPassCodes: AutoApprovePolicy.RecurringDefault(null).PassCodes,
            RecurringDefaultNote: RecurringApprovePolicyLoader.DefaultNote,
            DefaultChangeNotice: RecurringApprovePolicyLoader.DefaultChangeNotice,
            DefaultChangeNoticeKey: RecurringApprovePolicyLoader.DefaultChangeNoticeKey);
    }

    // ═══════════════ ความพร้อม / ทดลองรัน ═══════════════

    public async Task<AutopilotReadinessResponse> ReadinessAsync(Guid companyId, Guid recurringId, CancellationToken ct = default)
        => (await BuildAsync(companyId, await LoadAsync(companyId, recurringId, ct), ct)).Readiness;

    public async Task<AutopilotReadinessResponse> ReadinessForDraftAsync(Guid companyId, CreateRecurringTransactionRequest draft, Guid? recurringId,
        CancellationToken ct = default)
        => (await BuildAsync(companyId, await FromDraftAsync(companyId, draft, recurringId, ct), ct)).Readiness;

    public async Task<RecurringDryRunResponse> DryRunAsync(Guid companyId, Guid recurringId, CancellationToken ct = default)
        => await BuildAsync(companyId, await LoadAsync(companyId, recurringId, ct), ct);

    public async Task<RecurringDryRunResponse> DryRunDraftAsync(Guid companyId, CreateRecurringTransactionRequest draft, Guid? recurringId,
        CancellationToken ct = default)
        => await BuildAsync(companyId, await FromDraftAsync(companyId, draft, recurringId, ct), ct);

    /// <summary>รอบ 251 (WP-07 ข้อ 1): เพดานแถวที่ตรวจต่อคำขอ (แต่ละแถวเดินตัวรวบรวมข้อเท็จจริงทั้งชุด — หน้าแบ่งหน้าละ 20 อยู่แล้ว)</summary>
    public const int MaxLevelRows = 50;

    public async Task<IReadOnlyList<RecurringAutopilotLevelRow>> LevelsAsync(Guid companyId, IReadOnlyList<Guid> recurringIds,
        Func<DocumentType, Task<bool>>? canRead = null, CancellationToken ct = default)
    {
        var ids = recurringIds.Distinct().Take(MaxLevelRows).ToList();
        if (ids.Count == 0) return Array.Empty<RecurringAutopilotLevelRow>();
        var rows = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.CompanyId == companyId && ids.Contains(r.Id) && r.Status != RecurringStatus.Completed)
            .ToListAsync(ct);
        var list = new List<RecurringAutopilotLevelRow>(rows.Count);
        // ฝ่ายค้านข้อ 6: ตัดชนิดที่ผู้เรียกไม่มีสิทธิ์ก่อนรวบรวม (ไม่เสียแรงกับแถวที่ไม่ส่งกลับ) · ข้อเท็จจริงระดับบริษัทโหลดครั้งเดียว
        var allowed = new Dictionary<DocumentType, bool>();
        SharedCompanyFacts? shared = null;
        foreach (var r in rows)
        {
            var type = r.DocumentType ?? DocumentType.Invoice;
            if (canRead != null)
            {
                if (!allowed.TryGetValue(type, out var ok)) allowed[type] = ok = await canRead(type);
                if (!ok) continue;
            }
            try
            {
                shared ??= await LoadSharedFactsAsync(companyId, ct);
                var rep = (await BuildAsync(companyId, r, ct, shared)).Readiness;
                var top = rep.Items.FirstOrDefault(i => i.Verdict == AutopilotReadiness.Fail) ?? rep.Items.FirstOrDefault(i => i.Verdict == AutopilotReadiness.Warn);
                list.Add(new RecurringAutopilotLevelRow(r.Id, r.DocumentType, rep.Level, rep.LevelLabel, rep.WarnCount, rep.FailCount, top?.Title, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ล้มดังที่ตัวข้อมูลที่ผู้ใช้เห็น (ช่องของแถวนั้นบอกเหตุ) — แถวอื่นยังได้ผล
                list.Add(new RecurringAutopilotLevelRow(r.Id, r.DocumentType, null, null, 0, 0, null, "ตรวจความพร้อมไม่สำเร็จ: " + ex.Message));
            }
        }
        return list;
    }

    // ═══════════════ ส่งทดสอบถึงอีเมลฉัน (รอบ 251 · WP-07 ข้อ 4) ═══════════════

    public async Task<RecurringSendTestResponse> SendTestAsync(Guid companyId, Guid recurringId, Guid userId, CancellationToken ct = default)
    {
        if (_senders == null)
            throw new BusinessRuleException("ระบบส่งอีเมลยังไม่พร้อม — ส่งทดสอบไม่ได้ (ใช้ปุ่ม \"ทดลองรัน\" แทน)", RecurringTestEmail.RuleCode, 503);
        // ผู้รับ = ผู้ใช้ที่กดเท่านั้น (ไม่รับที่อยู่จากคำขอ) · ฝ่ายค้านข้อ 2a: อีเมลต้องยืนยันแล้ว — ไม่งั้นสมัครด้วยอีเมลของคนอื่นแล้วใช้ปุ่มนี้ส่งอีเมลในนามบริษัทถึงเขาได้
        var user = await _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.Email, u.EmailVerified }).FirstOrDefaultAsync(ct);
        var to = user?.Email?.Trim();
        if (!OnBehalfEmailSender.IsPlausibleEmail(to))
            throw new BusinessRuleException("บัญชีผู้ใช้ของคุณยังไม่มีอีเมลที่ใช้ได้ — ส่งทดสอบไม่ได้ (ตั้งอีเมลที่โปรไฟล์ หรือใช้ปุ่ม \"ทดลองรัน\")",
                RecurringTestEmail.RuleCode, 409);
        if (RecurringTestEmail.UnverifiedRefusal(user!.EmailVerified) is { } unverified)
            throw new BusinessRuleException(unverified, RecurringTestEmail.RuleCode, 409);
        var r = await LoadAsync(companyId, recurringId, ct);

        // ผลทดลองรัน + ผู้ส่ง + เนื้อหา ประกอบนอกล็อก (อ่านอย่างเดียว)
        var dry = await BuildAsync(companyId, r, ct);
        var settings = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId, ct);
        var (plan, sender) = await DocumentEmailService.ResolveOnBehalfSenderAsync(_db, _senders, companyId, settings, ct);
        var co = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => new { c.Name, c.TaxId }).FirstOrDefaultAsync(ct);
        var text = RecurringTestEmail.Compose(r.Name, co?.Name, dry);   // อีเมลผู้รับจริงถูกปิดบังในเนื้อ (ฝ่ายค้านข้อ 2)
        var subject = text.Subject;
        var html = text.Html;
        if (plan.OnBehalf)
        {
            // ฝ่ายค้านข้อ 2b · คำตัดสิน 240-9: เส้นแพลตฟอร์มห่อด้วยหัวเปิดเผยตัวเดียวกับอีเมลเอกสาร (ส่งในนามบริษัทผ่าน NextAcc · ช่องแจ้งใช้ผิด)
            var origin = await SystemBaseUrl.ResolveAsync(_db, _config, ct);
            html = SignRequestEmailText.WrapPlatformDocumentEmail(false, co?.Name, co?.TaxId, plan.ReplyTo, RecurringTestEmail.SubjectPrefix.Trim(),
                null, html, origin == null ? null : $"{origin}/contact.html?topic=email-abuse");
            subject = SignRequestEmailText.SafeSubject(subject);
        }

        // ฝ่ายค้านข้อ 2c/2d: เพดานต่อผู้ใช้ต่อบริษัทต่อวัน (ไม่ใช่ต่อรายการ) ใต้ xact lock ของผู้ใช้ — นับ → ส่ง → audit อยู่ในล็อกเดียว (สองคำขอพร้อมกันเกินไม่ได้)
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.RecurringSendTest, userId.ToString()) }, ct);
            var dayStart = SignEmailThrottle.BangkokDayStartUtc(DateTime.UtcNow);
            var sentToday = await _db.AuditLogs.AsNoTracking()
                .CountAsync(a => a.CompanyId == companyId && a.UserId == userId && a.Action == AuditAction.SendEmail
                    && a.EntityType == RecurringTestEmail.AuditEntityType && a.Timestamp >= dayStart, ct);
            if (RecurringTestEmail.LimitRefusal(sentToday) is { } limit)
                throw new BusinessRuleException(limit, RecurringTestEmail.RuleCode, 429);
            if (plan.OnBehalf)
            {
                // ฝ่ายค้านข้อ 2b: เส้นแพลตฟอร์มเคารพเพดานรายวันของบริษัท + เพดานต่อผู้รับ (ตัวนับ/ตัวตัดสินเดียวกับอีเมลเอกสาร) — อีเมลทดสอบเองไม่ถูกนับเป็นการส่งเอกสาร
                var quota = await PlatformEmailQuota.ForCompanyAsync(_db, _config, companyId, DateTime.UtcNow, ct);
                var (_, anyToday) = await PlatformEmailQuota.RecipientCountsAsync(_db, companyId, Guid.Empty, to!, quota.DayStartUtc, ct);
                if (SignEmailThrottle.Refusal(true, quota.SentToday, quota.Cap, 0, anyToday) is { } capRefusal)
                    throw new BusinessRuleException(capRefusal, "EMAIL-PLATFORM-CAP", 429);
            }

            var msg = new EmailMessage
            {
                FromAddress = plan.FromAddress, FromName = plan.FromName, ReplyTo = plan.ReplyTo,
                Subject = subject, HtmlBody = html, PlainTextBody = text.Text,
            };
            msg.To.Add(to!);
            EmailSendResult result;
            try { result = await sender.SendAsync(msg, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { result = EmailSendResult.Fail(ex.Message); }
            // ไม่บันทึก DocumentEmailLog (ไม่ใช่การส่งเอกสาร · ไม่นับเพดาน) — ร่องรอยว่าใครกดอยู่ใน hash chain (หลักการ #10) ทั้งสำเร็จและล้ม · ตัวนับเพดานอ่านแถวนี้
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = userId,
                UserEmail = to,
                Action = AuditAction.SendEmail,
                EntityType = RecurringTestEmail.AuditEntityType,
                EntityId = r.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new
                {
                    action = RecurringTestEmail.AuditActionName, to, senderMode = plan.Mode, success = result.Success,
                    error = result.Success ? null : result.ErrorMessage, ruleCode = RecurringTestEmail.RuleCode,
                }),
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            if (!result.Success)
                throw new BusinessRuleException("ส่งอีเมลทดสอบไม่สำเร็จ: " + (result.ErrorMessage ?? "ผู้ให้บริการอีเมลไม่ตอบรับ"), RecurringTestEmail.RuleCode, 502);
            return new RecurringSendTestResponse(to!, OnBehalfEmailSender.Describe(plan), subject, RecurringTestEmail.NotCountedNote);
        });
    }

    private async Task<RecurringTransaction> LoadAsync(Guid companyId, Guid recurringId, CancellationToken ct)
        => await _db.RecurringTransactions.AsNoTracking()
               .FirstOrDefaultAsync(r => r.Id == recurringId && r.CompanyId == companyId, ct)
           ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");

    /// <summary>ร่างในฟอร์ม ⇒ แถวในหน่วยความจำ (ไม่ถูกติดตาม) · แก้รายการเดิม ⇒ ใช้นโยบายที่ตั้งไว้ของรายการนั้น (นโยบายตั้งผ่าน endpoint แยก)</summary>
    private async Task<RecurringTransaction> FromDraftAsync(Guid companyId, CreateRecurringTransactionRequest d, Guid? recurringId, CancellationToken ct)
    {
        if (AutopilotSettingsResolver.ValidateMode(d.PaymentMode) is { } badMode)
            throw new BusinessRuleException(badMode, AutopilotSettingsResolver.RuleCode);
        // รอบ 251 (WP-07 · POSTPAY): ค่าหลังรับชำระของร่าง — ตัวตรวจเดียวกับตอนบันทึก · null (หน้าโหลดตัวเลือกไม่สำเร็จ — WP-03) = คงค่าที่บันทึกไว้
        if ((AutopilotSettingsResolver.ValidateTriState(d.PostPayEmailReceipt) ?? AutopilotSettingsResolver.ValidateTriState(d.PostPayIncludePartial)) is { } badTri)
            throw new BusinessRuleException(badTri, AutopilotSettingsResolver.RuleCode);
        string? storedPolicy = null;
        // รอบ 257 ชุด 4 (A-M4): ร่างของรายการเดิม ⇒ ลายนิ้วมือของค่าตั้งต้นที่ประทับไว้ (แผงบอกล่วงหน้าว่าแก้แล้วต้องรับรองใหม่) · ร่างใหม่ = null (ประทับตอนสร้าง)
        string? storedDefaultHash = null, storedStampedBy = null, storedCreatedBy = null;
        bool? storedPostPayEmail = null, storedPostPayPartial = null;
        if (recurringId is { } rid)
        {
            var stored = await _db.RecurringTransactions.AsNoTracking()
                .Where(r => r.Id == rid && r.CompanyId == companyId)
                .FirstOrDefaultAsync(ct);
            // ฝ่ายค้าน MED-1/LOW-1: ประทับ/ยกรุ่นแบบเดียวกับที่ UpdateAsync จะทำก่อนแก้ (แถวในหน่วยความจำ AsNoTracking — ไม่บันทึก) ⇒ แผงทำนายผลหลังบันทึกตรงของจริง
            if (stored != null) RecurringApprovePolicyLoader.UpgradeStampsBeforeEdit(stored);
            storedPolicy = stored?.AutoApprovePolicyJson;
            storedDefaultHash = stored?.DefaultPolicyBindingHash;
            storedStampedBy = stored?.DefaultPolicyStampedBy;
            storedCreatedBy = stored?.CreatedBy;
            storedPostPayEmail = stored?.PostPayEmailReceipt;
            storedPostPayPartial = stored?.PostPayIncludePartial;
        }
        return new RecurringTransaction
        {
            Id = recurringId ?? Guid.Empty,
            CompanyId = companyId,
            Name = d.Name,
            Frequency = d.Frequency,
            StartDate = d.StartDate,
            EndDate = d.EndDate,
            NextRunDate = d.StartDate,
            PreferredDay = d.StartDate.Day,
            MaxRuns = d.MaxRuns,
            TemplateType = d.TemplateType,
            DocumentType = d.DocumentType,
            ContactId = d.ContactId,
            TemplateData = d.TemplateData,
            AutoApprove = d.AutoApprove || d.AutoSendEmail,
            AutoSendEmail = d.AutoSendEmail,
            PaymentMode = AutopilotSettingsResolver.Normalize(d.PaymentMode),
            PayLinkChannelsJson = DocumentPayLinkPolicy.SerializeChannels(d.PayLinkChannels),
            AutoApprovePolicyJson = storedPolicy,
            DefaultPolicyBindingHash = storedDefaultHash,
            DefaultPolicyStampedBy = storedStampedBy,
            CreatedBy = storedCreatedBy,   // ผู้รับผิดชอบค่าตั้งต้นของรายการเดิม (ฝ่ายค้าน MED-2 · ร่างใหม่ = null เหมือนเดิม)
            // รอบ 257 ชุด 4 (A L13): ร่างในฟอร์มส่งสร้างล่วงหน้า/ทุก N เดือน มาด้วย — แผงตรวจวันที่เอกสาร/เดือนภาษีจากค่าในฟอร์ม
            LeadDays = d.LeadDays ?? 0,
            IntervalMonths = d.IntervalMonths is > 0 ? d.IntervalMonths : null,
            PostPayEmailReceipt = d.PostPayEmailReceipt == null ? storedPostPayEmail : AutopilotSettingsResolver.ParseTriState(d.PostPayEmailReceipt),
            PostPayIncludePartial = d.PostPayIncludePartial == null ? storedPostPayPartial : AutopilotSettingsResolver.ParseTriState(d.PostPayIncludePartial),
        };
    }

    private async Task<DocumentPayLinkPolicy.CompanyFacts?> CompanyPayFactsAsync(Guid companyId, CancellationToken ct)
        => _payLinks == null ? null : await _payLinks.CompanyFactsAsync(companyId, ct);

    /// <summary>รอบ 251 (WP-07 ฝ่ายค้านข้อ 6): ข้อเท็จจริงระดับบริษัทที่ไม่ขึ้นกับแถว — ตารางหลายแถวโหลดครั้งเดียวต่อคำขอ</summary>
    private sealed record SharedCompanyFacts(DocumentPayLinkPolicy.CompanyFacts? PayFacts, CompanySettings? Settings, bool BaseUrlConfigured,
        IReadOnlyList<ClosedDateRange> ClosedRanges);

    /// <summary>อ่านอย่างเดียว · tenant: ค่าตั้งบริษัท · ช่องทางรับเงินที่เปิด · URL ของระบบ (Y7) · งวดที่ปิด (ตัวรวมช่วงเดียว ClosedPeriodRanges)</summary>
    private async Task<SharedCompanyFacts> LoadSharedFactsAsync(Guid companyId, CancellationToken ct)
    {
        var payFacts = await CompanyPayFactsAsync(companyId, ct);
        var settings = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId).FirstOrDefaultAsync(ct);
        // รอบ 250 (WP-02 §2.4 ข้อ 2 · Y7): URL ของระบบตั้งแล้วจริงไหม (ตัวตัดสินเดียว SystemBaseUrl.IsConfigured — ค่าที่มากับซอร์ส ≠ ตั้งแล้ว)
        var adminBaseUrl = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.AppBaseUrl).FirstOrDefaultAsync(ct);
        var periods = await _db.FiscalPeriods.AsNoTracking()
            .Where(fp => fp.CompanyId == companyId)
            .Select(fp => new { fp.StartDate, fp.EndDate, fp.Status })
            .ToListAsync(ct);
        return new SharedCompanyFacts(payFacts, settings, SystemBaseUrl.IsConfigured(adminBaseUrl, _config?["App:BaseUrl"]),
            ClosedPeriodRanges.Build(periods.Select(p => (p.StartDate, p.EndDate, p.Status))));
    }

    /// <summary>รวบรวมข้อเท็จจริงครั้งเดียว (อ่านอย่างเดียว) → ตัวตัดสิน → ผลทดลองรัน</summary>
    private async Task<RecurringDryRunResponse> BuildAsync(Guid companyId, RecurringTransaction r, CancellationToken ct, SharedCompanyFacts? shared = null)
    {
        var cf = shared ?? await LoadSharedFactsAsync(companyId, ct);
        var notes = new List<string>();
        var isDocument = !string.Equals(r.TemplateType, "journal", StringComparison.OrdinalIgnoreCase);
        var docType = r.DocumentType ?? DocumentType.Invoice;
        var nextDates = new List<DateTime>();
        var cursor = r.NextRunDate;
        for (var i = 0; i < 6 && RecurringSchedule.WithinEnd(cursor, r.EndDate) && (r.MaxRuns == null || r.TotalRuns + i < r.MaxRuns); i++)
        {
            nextDates.Add(cursor);
            cursor = RecurringSchedule.Next(r.Frequency, cursor, r.PreferredDay, r.StartDate, r.IntervalMonths);
        }

        // ── ผู้ติดต่อ ──
        Contact? contact = null;
        if (r.ContactId is { } cid)
            contact = await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid && c.CompanyId == companyId, ct);
        // รอบ 250 WP-08: ผู้รับเรื่อง "เอกสาร" จากตัวหาผู้รับเดียว (ไม่อ่าน Contact.Email ตรง) · ไม่มีผู้ติดต่อ = ไม่มีผู้รับ
        var docDelivery = contact == null
            ? new ContactRecipientRule.EmailDelivery(Array.Empty<string>(), null)
            : ContactRecipientRule.ForEmail(await _recipients.ResolveAsync(companyId, contact.Id, NotifyTopic.Document, ct));

        // ── บรรทัด (ตัวอ่านเดียวกับตอนสร้าง: RecurringTemplate · ตัวแทนค่าเดียวกัน: ApplyRecurringPlaceholders) ──
        string? templateError = null;
        var lines = new List<RecurringDryRunLine>();
        var pricesIncludeVat = false;
        string currency = "THB";
        Guid? templateBankId = null, templatePaymentAccountId = null;   // รอบ 251 (PAY-QR): บัญชีของใบตามเทมเพลต (ลำดับเดียวกับกระดาษ)
        if (isDocument)
        {
            try
            {
                using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(r.TemplateData) ? "{}" : r.TemplateData);
                var root = json.RootElement;
                pricesIncludeVat = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("pricesIncludeVat", out var piv)
                    && piv.ValueKind == JsonValueKind.True;
                if (RecurringTemplate.Text(root, "currency") is { Length: > 0 } cur) currency = cur.Trim().ToUpperInvariant();
                if (Guid.TryParse(RecurringTemplate.Text(root, "bankAccountId"), out var tb)) templateBankId = tb;
                if (Guid.TryParse(RecurringTemplate.Text(root, "paymentAccountId"), out var tp)) templatePaymentAccountId = tp;
                var baseDate = RecurringSchedule.PlaceholderBaseDate(r.NextRunDate, DateTime.UtcNow, true, r.LeadDays);
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
                {
                    var lineNo = 0;
                    foreach (var line in linesEl.EnumerateArray())
                    {
                        lineNo++;
                        var qty = RecurringTemplate.Number(line, "quantity", 1m, lineNo);
                        var price = RecurringTemplate.Number(line, "unitPrice", 0m, lineNo);
                        var vatRate = RecurringTemplate.Number(line, "vatRate", 7m, lineNo);
                        var whtRate = RecurringTemplate.Number(line, "withholdingTaxRate", 0m, lineNo);
                        var discPct = RecurringTemplate.Number(line, "discountPercent", 0m, lineNo);
                        var discAmt = RecurringTemplate.Number(line, "discountAmount", 0m, lineNo);
                        var gross = Math.Round(qty * price * (1m - discPct / 100m) - discAmt, 2, MidpointRounding.AwayFromZero);
                        var amount = pricesIncludeVat && vatRate > 0m
                            ? Math.Round(gross * 100m / (100m + vatRate), 2, MidpointRounding.AwayFromZero)
                            : gross;
                        var vat = pricesIncludeVat && vatRate > 0m ? gross - amount
                            : Math.Round(amount * vatRate / 100m, 2, MidpointRounding.AwayFromZero);
                        var wht = Math.Round(amount * whtRate / 100m, 2, MidpointRounding.AwayFromZero);
                        var desc = RecurringTransactionService.ApplyRecurringPlaceholders(RecurringTemplate.Text(line, "description"), baseDate) ?? "";
                        lines.Add(new RecurringDryRunLine(desc, qty, price, vatRate, amount, vat, whtRate, wht));
                    }
                }
                if (lines.Count == 0)
                    templateError = "เทมเพลตไม่มีรายการสินค้า/บริการ — เพิ่มอย่างน้อย 1 บรรทัดในขั้น \"รายการ & ราคา\"";
            }
            catch (JsonException) { templateError = "อ่านเทมเพลตไม่ได้ — เปิดแก้ไขรายการประจำแล้วบันทึกใหม่"; }
            catch (BusinessRuleException ex) { templateError = ex.Message; }
        }
        var subTotal = lines.Sum(l => l.Amount);
        var vatTotal = lines.Sum(l => l.VatAmount);
        var total = subTotal + vatTotal;
        var whtTotal = lines.Sum(l => l.WithholdingTaxAmount);
        var buyerWithholds = DocumentPayLinkPolicy.BuyerWithholds(contact?.ContactType, contact?.TaxId) != false;
        var expectedWht = buyerWithholds ? whtTotal : 0m;
        var amounts = DocumentPayLinkPolicy.AmountsFor(total - expectedWht, expectedWht, 0m);

        // ── คำเตือนก่อนอนุมัติบนใบจำลอง (ตัวรวบรวมเดียวกับการอนุมัติจริง · ใบไม่อยู่ใน context) ──
        IReadOnlyList<string>? warnings = null;
        if (isDocument && contact != null && lines.Count > 0 && templateError == null)
        {
            var draft = new Document
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                DocumentType = docType,
                DocumentNumber = "DRAFT-DRYRUN",
                DocumentDate = DateTime.UtcNow,   // ตอนรันจริงวันเอกสาร = วันที่รัน (CreateDocumentAsync ใช้ UtcNow) ⇒ ใบจำลองใช้วันนี้ (ไม่ฟ้อง "ล่วงหน้า" ผิด)
                ContactId = contact.Id,
                Contact = contact,
                Status = DocumentStatus.Draft,
                Currency = currency,
                PricesIncludeVat = pricesIncludeVat,
                SubTotal = subTotal,
                VatAmount = vatTotal,
                TotalAmount = total,
                WithholdingTaxAmount = whtTotal,
                BalanceDue = total - whtTotal,
                Lines = lines.Select((l, i) => new DocumentLine
                {
                    LineOrder = i + 1,
                    Description = l.Description,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice,
                    Amount = l.Amount,
                    VatRate = l.VatRate,
                    VatAmount = l.VatAmount,
                    WithholdingTaxRate = l.WithholdingTaxRate,
                    WithholdingTaxAmount = l.WithholdingTaxAmount,
                }).ToList(),
            };
            try { warnings = await _docs.PreviewApprovalWarningsForUnsavedAsync(companyId, draft); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add("ตรวจคำเตือนก่อนอนุมัติของใบจำลองไม่สำเร็จ: " + ex.Message);
            }
        }

        // ── นโยบาย (ตัวโหลดเดียวกับเส้นอนุมัติจริง) ──
        var policy = await RecurringApprovePolicyLoader.LoadAsync(_db, _permissions, companyId, r, ct);
        // รอบ 258 (B-M2): ด่านเดียวกับรอบรันจริง (ผู้รับผิดชอบการอนุมัติยังมีสิทธิ์ไหม) — ร่างใหม่ที่ยังไม่มีผู้สร้าง (ผู้กรอกคือผู้รับผิดชอบ) ไม่ตรวจ
        var approverRefusal = string.IsNullOrEmpty(r.CreatedBy) ? null
            : await RecurringApprovePolicyLoader.AutoApproverRefusalAsync(_db, _permissions, companyId, r, policy, ct);
        var blocking = warnings == null ? Array.Empty<string>()
            : ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.UnattendedWithPolicy, warnings, policy.Policy);
        var released = warnings == null ? Array.Empty<string>() : AutoApprovePolicy.Released(policy.Policy, warnings);

        // ── ช่องทางรับเงิน (ตัวต่อชั้นเดียว) ──
        var payFacts = cf.PayFacts;
        var available = payFacts == null ? Array.Empty<string>() : DocumentPayLinkPolicy.CompanyAvailable(payFacts);
        var settings = cf.Settings;
        var recurringLayer = AutopilotSettingsResolver.FromMode(r.PaymentMode, DocumentPayLinkPolicy.ParseChannels(r.PayLinkChannelsJson), available);
        var contactLayer = contact == null ? null
            : AutopilotSettingsResolver.FromMode(contact.PaymentMode, DocumentPayLinkPolicy.ParseChannels(contact.PayLinkChannelsJson), available);
        var choice = AutopilotSettingsResolver.Resolve(null, recurringLayer, contactLayer, settings?.PayLinkDefaultEnabled ?? false,
            DocumentPayLinkPolicy.ParseChannels(settings?.PayLinkDefaultChannelsJson));
        var allowed = DocumentPayLinkPolicy.Allowed(available, choice.Channels, null);
        var saleSide = DocumentPayLinkPolicy.IsSaleSide(docType, null, null, false);   // เทมเพลต: ใบเพิ่มหนี้ไม่มีต้นทาง/ฝั่ง ⇒ ไม่ใช่ (ไม่แนบลิงก์)
        var effectiveMode = AutopilotSettingsResolver.Normalize(r.PaymentMode) ?? AutopilotSettingsResolver.Normalize(contact?.PaymentMode);
        var hasRecurringRule = await _db.EmailScheduleRules.AsNoTracking()
            .AnyAsync(x => x.CompanyId == companyId && x.IsActive && !x.IsDeleted && x.Trigger == "RecurringInvoiceCreated"
                && (x.DocumentType == null || x.DocumentType == docType.ToString()), ct);
        // รอบ 250 (WP-02 §2.4 ข้อ 3): กฎ "เมื่ออนุมัติเอกสาร" ครอบชนิดนี้ ⇒ ตัวเข้าคิวของรายการประจำข้าม (EmailEnqueueOutcome.SkipForApprovedRule) — แผงบอกให้รู้
        var hasApprovedRule = await _db.EmailScheduleRules.AsNoTracking()
            .AnyAsync(x => x.CompanyId == companyId && x.IsActive && !x.IsDeleted && x.Trigger == "DocumentApproved"
                && (x.DocumentType == null || x.DocumentType == docType.ToString()), ct);
        var baseUrlConfigured = cf.BaseUrlConfigured;   // SystemBaseUrl.IsConfigured ใน LoadSharedFactsAsync
        if (hasRecurringRule)
            notes.Add("มีกฎ \"เมื่อรายการประจำสร้างเอกสาร\" ในหน้าตารางส่งอีเมล — ระบบส่งตามกฎนั้น (ค่าของกฎชนะค่าของรายการนี้เมื่อกฎตั้งไว้)");

        // ── ผู้ซื้อ §86/4 (ตัวตัดสินเดียวกับงานหลังรับชำระ) ──
        var buyerBlock = saleSide && contact != null
            ? Tax.TaxInvoiceCompletenessChecker.BuyerBlockingFields(true, vatTotal, contact, isDeferredVatDeposit: false,
                buyerDeclinedTaxInvoice: TemplateBuyerDeclined(r.TemplateData))
            : null;

        var hasTenantSender = settings != null && Accounting.Services.Implementations.Email.EmailSenderFactory.HasTenantSender(settings);
        var senderVerified = settings != null && settings.EmailConfigured && hasTenantSender;

        // ── รอบ 251 (WP-07 · A B-17/F-06): ช่องทาง e-Tax ของใบรอบหน้า — ขอบเขต e-Tax ตัวเดียวกับ hook ออก e-Tax (ใบจำลอง = อนุมัติแล้ว · ไม่มีใบต้นทาง ·
        // ไม่ใช่มัดจำ) → ตัวตัดสินช่องทางเดียว → ด่านผู้ส่ง/ผู้รับ (อ่านอย่างเดียว · ไม่ออก e-Tax · ไม่ส่ง) ──
        var buyerDeclined = TemplateBuyerDeclined(r.TemplateData);
        var notFullTaxInvoice = EtaxAutoIssueScope.IsTitleDecidedType(docType) && vatTotal > 0.005m
            && TaxService.NotFullTaxInvoiceByDesign(vatTotal, buyerDeclined, contact);
        // ฝ่ายค้านข้อ 5: ตัวโหลดจริงอ่าน IsTaxInvoiceByLaw ที่ตรึงตอนอนุมัติ — ใบที่ยังไม่ถูกสร้างทำนายด้วยตัวเดียว (ชนิดที่รูปแบบใบเสร็จตัดสินตอนอนุมัติ = ไม่แน่นอน)
        var (predictedByLaw, etaxUncertain) = EtaxAutoIssueScope.PredictForUnissued(docType, vatTotal);
        var etaxScope = EtaxAutoIssueScope.Judge(docType, DocumentStatus.Approved, false, false, null, null, predictedByLaw, notFullTaxInvoice);
        var etaxRoute = EtaxDeliveryRouter.Decide(settings?.EtaxEnabled ?? false, settings?.EtaxMode ?? EtaxMode.None, etaxScope, false);
        var etaxActive = isDocument && settings != null && EtaxDeliveryRouter.ByEmailActive(settings.EtaxEnabled, settings.EtaxMode);
        // ด่านผู้ส่งเป็นของบริษัท (ไม่ขึ้นกับใบ) — ใช้ทั้งใบของรอบและใบเสร็จหลังรับชำระ
        var etaxGateRefusal = etaxActive ? EtaxByEmailSenderGate.Refusal(EtaxByEmailSenderGate.From(settings!, hasTenantSender)) : null;
        // ฝ่ายค้านข้อ 9: กฎที่จะส่งใบนี้จริง — มีกฎ "เมื่ออนุมัติเอกสาร" ครอบ ⇒ ส่งตามกฎนั้นเสมอ (อนุมัติมือหรืออัตโนมัติ · รายการประจำข้ามฝั่งตัวเอง WP-02) ·
        // ไม่งั้นกฎ "เมื่อรายการประจำสร้างเอกสาร" · ไม่มีกฎ (ติ๊กส่งของรายการ) = ไม่มีสำเนาลับ
        var sendTrigger = hasApprovedRule ? "DocumentApproved" : "RecurringInvoiceCreated";
        string? etaxSenderRefusal = null, etaxRecipientProblem = null;
        var etaxRecipientFromSchedule = false;
        // ฝ่ายค้าน WP-08 HIGH-1: ทำนายตามผู้ส่งจริง — ใบ e-Tax by Email ส่งถึงอีเมลหลักเท่านั้น (ContactRecipientRule.ForChannel ตัวเดียวกับคิว/งานหลังรับชำระ)
        ContactRecipientRule.ChannelRecipients? etaxChannel = null;
        if (etaxActive && etaxRoute.Channel == EtaxEmailChannel.EtaxByEmail)
        {
            etaxSenderRefusal = etaxGateRefusal;
            if (RecurringEmailReadiness.HasDeliverableEmail(docDelivery.To))
            {
                var scheduleBcc = await _db.EmailScheduleRules.AsNoTracking()
                    .Where(x => x.CompanyId == companyId && x.IsActive && !x.IsDeleted && x.Trigger == sendTrigger
                        && (x.DocumentType == null || x.DocumentType == docType.ToString()) && x.BccEmails != null && x.BccEmails != "")
                    .Select(x => x.BccEmails).FirstOrDefaultAsync(ct);
                etaxChannel = ContactRecipientRule.ForChannel(docDelivery, etaxByEmail: true);
                var contactProblem = etaxChannel.Problem
                    ?? EtaxByEmailFormat.Recipients(etaxChannel.To, null, null, true, true, false, false).Problem;
                etaxRecipientProblem = contactProblem
                    ?? EtaxByEmailFormat.Recipients(etaxChannel.To, null, scheduleBcc, true, true, false, false).Problem;
                etaxRecipientFromSchedule = contactProblem == null && etaxRecipientProblem != null;
            }
        }

        // ── ฝ่ายค้านข้อ 4: ใบเสร็จที่งานหลังรับชำระจะออก (อ้างใบชนิดนี้) — ขอบเขต e-Tax ตัวเดียวกัน (ใบเสร็จรับชำระใบกำกับ = ไม่ใช่ใบกำกับ) ──
        string? postPayReceiptRefusal = null;
        if (etaxActive && saleSide && etaxGateRefusal != null)
        {
            var receiptNotFull = vatTotal > 0.005m && TaxService.NotFullTaxInvoiceByDesign(vatTotal, buyerDeclined, contact);
            var receiptScope = EtaxAutoIssueScope.Judge(DocumentType.Receipt, DocumentStatus.Approved, false, false, null, docType, null, receiptNotFull);
            if (EtaxDeliveryRouter.Decide(settings!.EtaxEnabled, settings.EtaxMode, receiptScope, false).Channel == EtaxEmailChannel.EtaxByEmail)
                postPayReceiptRefusal = etaxGateRefusal;
        }

        // ── รอบ 251 (WP-07 · PAY-QR): QR บัญชีธนาคารบนเอกสาร — ตัวเลือกบัญชีเดียวกับกระดาษ (แหล่งเงินของใบ → บัญชีของใบ → เทมเพลต → บัญชีเดียวที่ตั้ง QR) ──
        var bankQrApplies = false;
        string? bankQrAccount = null, bankQrProblem = null;
        if (isDocument && saleSide && _storage != null)
        {
            var template = await PdfGenerationService.ResolveDocumentTemplateAsync(_db, companyId,
                new Document { CompanyId = companyId, DocumentType = docType, VatAmount = vatTotal, Currency = currency }, null);
            // ฝ่ายค้านข้อ 8: ตัวตัดสินเดียวกับกระดาษ (ShowBankQr — เทมเพลต · ชนิด · สกุล · ใบอนุมัติแล้วที่ยังมียอดค้าง) · ใบเพิ่มหนี้ไม่รู้ฝั่ง = ไม่มี QR
            bankQrApplies = PaymentQrPolicy.ShowBankQr(template.ShowPaymentQr, docType, null, null, false, DocumentStatus.Approved, total, currency);
            if (bankQrApplies)
            {
                var (qrAccount, qrPick) = await PaymentQrService.PickDocumentAccountAsync(_db, _storage.Locator.AttachmentsRoot, companyId,
                    templatePaymentAccountId, templateBankId, template.PaymentQrBankAccountId, ct);
                bankQrAccount = qrAccount == null ? null : PaymentQrService.AccountLabel(qrAccount);
                bankQrProblem = qrPick.Problem;
            }
        }

        // ── รอบ 251 (WP-07 · QUOTA): เพดานอีเมลผ่าน NextAcc ต่อวัน (ตัวนับเดียวกับผู้ส่งจริง — อีเมลเอกสารไม่ตัดโควตาข้อความแจ้งเตือน) ──
        PlatformEmailQuota.Snapshot? platformQuota = null;
        if (isDocument && r.AutoSendEmail && !senderVerified && !(etaxActive && etaxRoute.Channel == EtaxEmailChannel.EtaxByEmail))
            platformQuota = await PlatformEmailQuota.ForCompanyAsync(_db, _config, companyId, DateTime.UtcNow, ct);

        // ── รอบ 251 (WP-07 · PERIOD): งวดบัญชีของวันที่เอกสาร (รอบค้าง ⇒ วันที่รันจริง = วันนี้) · กติกางวดปิดเดียว ClosedPeriodRanges ──
        var closedRanges = cf.ClosedRanges;
        var todayTh = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        // รอบ 257 ชุด 4 (A L13): สร้างล่วงหน้า ⇒ เอกสารลงวันที่วันที่สร้าง (วันของงวด − LeadDays · ตัวเดียว RecurringSchedule.CreateAt) — เดิมตรวจงวดที่วันของงวด
        var nextCreateAt = nextDates.Count > 0 ? RecurringSchedule.CreateAt(nextDates[0], r.LeadDays).Date : (DateTime?)null;
        DateTime? nextDocDate = nextCreateAt is { } nca ? (nca < todayTh.Date ? todayTh.Date : nca) : null;
        bool? nextPeriodClosed = nextDocDate is { } ndd ? ClosedPeriodRanges.Covers(closedRanges, ndd) : null;
        var laterClosed = nextDates.Skip(1).Where(x => ClosedPeriodRanges.Covers(closedRanges, RecurringSchedule.CreateAt(x, r.LeadDays))).ToList();

        // ── รอบ 251 (WP-07 · POSTPAY): ค่าหลังรับชำระที่มีผล — ชั้นเดียวกับ PostPaymentAutomationService.SettingsForAsync (ตาราง › รายการ › ผู้ติดต่อ › บริษัท) ──
        PostPaymentAutomationPolicy.Overrides? postPaySchedule = null;
        if (r.AutoSendEmail || hasRecurringRule || hasApprovedRule)
        {
            var ruleOverrides = await _db.EmailScheduleRules.AsNoTracking()
                .Where(x => x.CompanyId == companyId && x.IsActive && !x.IsDeleted && x.Trigger == sendTrigger
                    && (x.DocumentType == null || x.DocumentType == docType.ToString()))
                .Select(x => new PostPaymentAutomationPolicy.Overrides(x.PostPayEmailReceipt, x.PostPayIncludePartial))
                .Take(2).ToListAsync(ct);
            if (ruleOverrides.Count == 1) postPaySchedule = ruleOverrides[0];   // หลายกฎ = ไม่เดาว่าฉบับไหนส่งก่อน (ใช้ชั้นถัดไป)
        }
        var postPay = PostPaymentAutomationPolicy.EffectiveLayers(
            new PostPaymentAutomationPolicy.Settings(settings?.PostPayEmailReceipt ?? false, settings?.PostPayIncludePartial ?? false),
            postPaySchedule,
            new PostPaymentAutomationPolicy.Overrides(r.PostPayEmailReceipt, r.PostPayIncludePartial),
            contact == null ? null : new PostPaymentAutomationPolicy.Overrides(contact.PostPayEmailReceipt, contact.PostPayIncludePartial));

        // ── รอบ 258-3 (F-M1): ความพร้อมทั้งเส้น — LINE (ตัวหาผู้รับเดียว · ตัวกรองเดียว ContactLineChannel.LineEndpoints) · gateway · คู่ค้า ──
        int? lineDocEps = null, lineRcEps = null;
        string? lineOaProblem = null;
        bool? gatewayLive = null, partnerLinked = null;
        if (isDocument && saleSide)
        {
            if (contact != null)
            {
                lineDocEps = ContactLineChannel.LineEndpoints((await _recipients.ResolveAsync(companyId, contact.Id, NotifyTopic.Document, ct)).Options).Count;
                lineRcEps = ContactLineChannel.LineEndpoints((await _recipients.ResolveAsync(companyId, contact.Id, NotifyTopic.Receipt, ct)).Options).Count;
                var chainOa = await _recipients.LineChannelAsync(companyId, ct);
                lineOaProblem = chainOa.CompanyOaReady ? null : (chainOa.CompanyOaProblem ?? "ยังไม่ได้ตั้ง LINE OA ของบริษัท (ตั้งค่า → แจ้งเตือนทาง LINE)");
                var chainTaxId = PartnerCompanyMatch.NormalizeTaxId(contact.TaxId);
                partnerLinked = chainTaxId != null && await _db.TradingPartnerships.AsNoTracking()
                    .Where(tp => (tp.CompanyAId == companyId || tp.CompanyBId == companyId) && tp.Status == TradingPartnershipStatus.Accepted)
                    .Select(tp => tp.CompanyAId == companyId ? tp.CompanyBId : tp.CompanyAId)
                    .AnyAsync(otherId => _db.Companies.Any(co => co.Id == otherId && !co.IsDeleted && co.TaxId == chainTaxId), ct);
            }
            gatewayLive = await _db.PaymentProviderConfigs.AsNoTracking()
                .AnyAsync(pc => pc.CompanyId == companyId && !pc.IsDeleted && pc.Mode == PaymentProviderMode.Live, ct);
        }

        var facts = new AutopilotReadiness.Facts
        {
            LineDocumentEndpoints = lineDocEps,
            LineReceiptEndpoints = lineRcEps,
            LineOaProblem = lineOaProblem,
            GatewayLive = gatewayLive,
            PartnerLinked = partnerLinked,
            IsDocumentTemplate = isDocument,
            DocumentType = docType,
            AutoApprove = r.AutoApprove || r.AutoSendEmail,
            AutoSendEmail = r.AutoSendEmail,
            ContactId = r.ContactId,
            ContactUsable = contact != null,
            ContactEmail = docDelivery.To,
            BuyerBlockingFields = buyerBlock,
            CompanySenderVerified = senderVerified,
            SaleSide = saleSide,
            Currency = currency,
            PayLinkAttach = choice.Attach,
            PayLinkSource = choice.Source,
            PaymentMode = effectiveMode,
            PayLinkUnavailableReason = payFacts == null ? "ยังไม่ได้ตั้งค่ารับชำระเงินออนไลน์" : DocumentPayLinkPolicy.UnavailableReason(payFacts),
            PayLinkAllowed = allowed,
            ApprovalWarnings = warnings,
            ApprovalBlocking = blocking,
            ApprovalReleased = released,
            Policy = policy.Stored,
            PolicySetterActive = policy.SetterActive,
            PolicyInactiveReason = policy.InactiveReason,
            AutoApproverRefusal = approverRefusal,
            TemplateError = templateError,
            SystemBaseUrlConfigured = baseUrlConfigured,
            ApprovedEmailRuleCovers = hasApprovedRule,
            RecurringEmailRuleExists = hasRecurringRule,
            EtaxChannel = etaxActive ? etaxRoute.Channel : null,
            EtaxRouteReason = etaxActive ? etaxRoute.Reason : null,
            EtaxSenderRefusal = etaxSenderRefusal,
            EtaxRecipientProblem = etaxRecipientProblem,
            EtaxRecipientFromSchedule = etaxRecipientFromSchedule,
            BankQrApplies = bankQrApplies,
            BankQrAccount = bankQrAccount,
            BankQrProblem = bankQrProblem,
            PlatformDailyCap = platformQuota?.Cap,
            PlatformSentToday = platformQuota?.SentToday ?? 0,
            PlatformIdentityVerified = platformQuota?.IdentityVerified ?? true,
            NextDocumentDate = nextDocDate,
            // รอบ 257 ชุด 4 (A L13): ใบกำกับภาษีที่สร้างล่วงหน้าข้ามเดือน ⇒ เดือนภาษีย้าย (ตัวตัดสิน RecurringSchedule.LeadDaysMovesVatMonth)
            LeadDays = r.LeadDays,
            NextPeriodDate = nextDates.Count > 0 ? nextDates[0] : null,
            NextPeriodClosed = isDocument ? nextPeriodClosed : null,
            LaterClosedRunDates = laterClosed,
            PostPayEmailReceipt = isDocument && saleSide ? postPay.EmailReceipt : null,
            PostPayIncludePartial = postPay.IncludePartial,
            EtaxScopeUncertain = etaxUncertain,
            PostPayReceiptEtaxRefusal = isDocument && saleSide && postPay.EmailReceipt ? postPayReceiptRefusal : null,
        };
        var report = AutopilotReadiness.Evaluate(facts);
        var readiness = new AutopilotReadinessResponse(report.Level, report.LevelLabel, report.Items, report.WarnCount, report.FailCount);

        var approvalOutcome = isDocument && nextPeriodClosed == true ? "จะสร้างเอกสารไม่ได้ — วันที่เอกสารอยู่ในงวดบัญชีที่ปิดแล้ว"
            : !(r.AutoApprove || r.AutoSendEmail) ? "ไม่ได้เปิดอนุมัติอัตโนมัติ — ได้ใบร่าง"
            : warnings == null ? "ตรวจไม่ได้ (ต้องมีผู้ติดต่อและรายการ)"
            : blocking.Count > 0 ? $"จะหยุดเป็นใบร่าง — คำเตือน {blocking.Count} ข้อที่ต้องมีคนรับทราบ"
            : released.Count > 0 ? $"จะอนุมัติ — นโยบายปล่อย {released.Count} ข้อ (ลงหมายเหตุ + audit {AutoApprovePolicy.RuleCode})"
            : "จะอนุมัติ (ได้เลขที่จริงตอนอนุมัติ)";
        var etaxByEmail = etaxActive && etaxRoute.Channel == EtaxEmailChannel.EtaxByEmail;
        // ฝ่ายค้านข้อ 3: ส่งจริงเมื่อติ๊กส่ง หรือมีกฎในตารางส่ง (ตัวตัดสินเดียว AutopilotReadiness.WillEmail) · บอกว่าส่งตามอะไร
        var willEmail = AutopilotReadiness.WillEmail(facts);
        var autoApproves = r.AutoApprove || r.AutoSendEmail;
        var via = hasApprovedRule ? " (ตามกฎ \"เมื่ออนุมัติเอกสาร\" ในตารางส่งอีเมล)"
            : !r.AutoSendEmail ? " (ตามกฎ \"เมื่อรายการประจำสร้างเอกสาร\" ในตารางส่งอีเมล)" : "";
        var whenSent = !autoApproves || blocking.Count > 0 ? "เมื่อมีคนอนุมัติใบร่าง " : "";
        var etaxMaybe = etaxUncertain ? "ถ้ารูปแบบที่ออกตอนอนุมัติเป็นใบกำกับภาษี: " : "";
        var emailPlan = !willEmail
                ? (autoApproves && blocking.Count == 0 ? "ไม่ส่งอีเมลอัตโนมัติ" : "ไม่ส่งอีเมลอัตโนมัติ — ใบยังเป็นร่างจนกว่ามีคนอนุมัติ (เลขที่จริงออกตอนอนุมัติ §86/4)")
            // รอบ 250 WP-08: ผู้ติดต่อปิดส่งอัตโนมัติ/ปิดรับเรื่องเอกสาร — ตัวหาผู้รับเดียวกับเส้นส่งจริง
            : AutopilotSettingsResolver.AutoSendBlock(contact?.AutoSendDisabled == true) is { } autoOff ? autoOff
            : docDelivery.OptOutReason is { } optOut && !docDelivery.Deliverable ? optOut
            : !RecurringEmailReadiness.HasDeliverableEmail(docDelivery.To) ? RecurringEmailReadiness.NotSentNoEmail
            : blocking.Count > 0 && !hasApprovedRule ? "ไม่ส่ง — ใบยังเป็นร่าง (เลขที่จริงออกตอนอนุมัติ §86/4)"
            // รอบ 251 (WP-07 · DoD): ใบ e-Tax by Email ไม่มีทางถอยไปอีเมลของ NextAcc — บอกตามจริงว่าจะล้มที่ด่านไหน
            : etaxByEmail && etaxSenderRefusal != null ? whenSent + etaxMaybe + "จะล้มที่ด่านผู้ส่ง e-Tax by Email — อีเมลไม่ถูกส่ง: " + etaxSenderRefusal + via
            : etaxByEmail && etaxChannel?.Problem is { } chProblem ? whenSent + etaxMaybe + chProblem + via   // ไม่เข้าคิว (แถว "ข้าม" พร้อมเหตุ) — ไม่ใช่ "ล้มตอนส่ง"
            : etaxByEmail && etaxRecipientProblem != null ? whenSent + etaxMaybe + "จะล้มตอนส่ง (ผู้รับ e-Tax by Email): " + etaxRecipientProblem + via
            : etaxByEmail ? whenSent + etaxMaybe + "ส่งแบบ e-Tax by Email จากอีเมลบริษัทที่ลงทะเบียนกับกรมสรรพากร ถึง " + (etaxChannel?.To ?? docDelivery.To) + " (สำเนาถึง ETDA เพื่อประทับเวลา)"
                + (etaxChannel?.Note is { } etaxNote ? " · " + etaxNote : "") + via
            : whenSent + (senderVerified ? "ส่งจากอีเมลบริษัท" : "ส่งผ่านอีเมลของ NextAcc ในนามบริษัท") + " ถึง " + docDelivery.To + via;
        var payPlan = !saleSide ? "ไม่มีลิงก์ชำระ (ไม่ใช่เอกสารเรียกเก็บเงิน)"
            : !choice.Attach ? $"ไม่แนบลิงก์ชำระ (ตาม{choice.Source})"
            : DocumentPayLinkPolicy.OnlineOnly(allowed).Count == 0 ? "ตั้งให้แนบ แต่ไม่มีช่องทางออนไลน์ที่บริษัทเปิด — จะไม่แนบ"
            // รอบ 250 (WP-02 §2.4 ข้อ 2): บอกตามจริง — ไม่มี URL ของระบบ ⇒ ไม่มีลิงก์ในอีเมล (ไม่เดาโดเมน)
            : !baseUrlConfigured ? "ตั้งให้แนบ แต่ยังไม่ได้ตั้ง URL ของระบบ — อีเมลจะส่งโดยไม่มีลิงก์ชำระ (แจ้งผู้ดูแลระบบ)"
            : $"แนบลิงก์ชำระ ({string.Join(" · ", allowed.Select(DocumentPayLinkPolicy.Label))}) ยอดในลิงก์ {amounts.Net:N2} บาท"
              + (amounts.Wht > 0m ? $" (หลังหัก ณ ที่จ่าย {amounts.Wht:N2})" : "");
        notes.Add("ยอดในการทดลองรันคำนวณจากเทมเพลต (ประมาณการ) — ยอดจริงคำนวณตอนสร้างเอกสารด้วยตัวคำนวณของเอกสาร");

        return new RecurringDryRunResponse(
            readiness, nextDates,
            DocumentTypeNames.Title(docType, "th"),
            "เลขที่จริงออกตอนอนุมัติเท่านั้น (§86/4) — ก่อนอนุมัติเป็น DRAFT-…",
            lines, subTotal, vatTotal, total, expectedWht, amounts.Net,
            approvalOutcome, warnings ?? Array.Empty<string>(), released, blocking,
            docDelivery.To, emailPlan, payPlan, notes,
            EtaxPlan: isDocument ? etaxRoute.Reason : null);
    }

    private static bool TemplateBuyerDeclined(string? templateData)
    {
        if (string.IsNullOrWhiteSpace(templateData)) return false;
        try
        {
            using var json = JsonDocument.Parse(templateData);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("buyerDeclinedTaxInvoice", out var el) && el.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    // ═══════════════ นโยบายอนุมัติ ═══════════════

    public async Task<AutoApprovePolicyView?> SetRecurringPolicyAsync(Guid companyId, Guid recurringId, SetAutoApprovePolicyRequest request,
        Guid userId, CancellationToken ct = default)
    {
        var r = await _db.RecurringTransactions.FirstOrDefaultAsync(x => x.Id == recurringId && x.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");
        var before = r.AutoApprovePolicyJson;
        // ผูกลายนิ้วมือของค่าปัจจุบัน (ผู้ติดต่อ · ชนิด · ยอด/บรรทัด · ธงอัตโนมัติ) — เปลี่ยนภายหลังโดยผู้ไม่มีสิทธิ์อนุมัติ ⇒ หยุดมีผล (ฝ่ายค้าน HIGH/MED-2) ·
        // Inherit = ใบรับรอง "ใช้นโยบายของผู้ติดต่อ" ที่ผูกลายนิ้วมือเหมือนกัน (นโยบายผู้ติดต่อไม่มีผลกับรายการที่ไม่มีใบรับรอง)
        r.AutoApprovePolicyJson = request.Clear ? null
            : request.Inherit
                ? AutoApprovePolicy.Serialize(AutoApprovePolicy.Create(Array.Empty<string>(), userId.ToString(), DateTime.UtcNow,
                    RecurringApprovePolicyLoader.CurrentBindingHash(r), inheritContact: true))
                : PolicyJsonFrom(request, userId, RecurringApprovePolicyLoader.CurrentBindingHash(r));
        // รอบ 257 ชุด 4 (A-M4 · H2): ผู้มีสิทธิ์อนุมัติบันทึกขั้นอนุมัติ (endpoint นี้ต้องมี Document.Approve) ⇒ ประทับลายนิ้วมือของค่าตั้งต้นใหม่ —
        // "ค่าเริ่มต้น" (Clear) จึงไม่ใช่ no-op แล้ว: = รับรองว่าค่าปัจจุบันของรายการใช้ค่าตั้งต้นรอบ 256 ได้
        r.DefaultPolicyBindingHash = RecurringApprovePolicyLoader.CurrentBindingHash(r);
        r.DefaultPolicyStampedBy = userId.ToString();   // ฝ่ายค้าน MED-2: ผู้มีสิทธิ์ที่เลือก/บันทึก = ผู้รับผิดชอบค่าตั้งต้น (เดิมยังตรวจผู้สร้าง ⇒ "ค่าเริ่มต้น" ไม่มีผลเมื่อผู้สร้างหมดสิทธิ์)
        AuditPolicy(companyId, "RecurringTransaction", r.Id, before, r.AutoApprovePolicyJson, userId);
        await _db.SaveChangesAsync(ct);
        return RecurringApprovePolicyLoader.View(AutoApprovePolicy.Parse(r.AutoApprovePolicyJson, AutoApprovePolicy.ScopeRecurring));
    }

    public async Task<ContactAutopilotSettingsResponse> SetContactPolicyAsync(Guid companyId, Guid contactId, SetAutoApprovePolicyRequest request,
        Guid userId, CancellationToken ct = default)
    {
        var c = await _db.Contacts.FirstOrDefaultAsync(x => x.Id == contactId && x.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        var before = c.AutoApprovePolicyJson;
        c.AutoApprovePolicyJson = request.Clear || request.Inherit ? null : PolicyJsonFrom(request, userId, null);
        AuditPolicy(companyId, "Contact", c.Id, before, c.AutoApprovePolicyJson, userId);
        await _db.SaveChangesAsync(ct);
        return ContactView(c);
    }

    /// <summary>คำขอ ⇒ JSON (ตัวตรวจเดียว <see cref="AutoApprovePolicy.Validate"/> — รหัสห้ามปล่อย/ไม่รู้จัก ⇒ 400 พร้อมเหตุ)</summary>
    private static string PolicyJsonFrom(SetAutoApprovePolicyRequest request, Guid userId, string? bindingHash)
    {
        if (AutoApprovePolicy.Validate(request.PassCodes) is { } why)
            throw new BusinessRuleException(why, AutoApprovePolicy.RuleCode);
        return AutoApprovePolicy.Serialize(AutoApprovePolicy.Create(request.PassCodes, userId.ToString(), DateTime.UtcNow, bindingHash));
    }

    /// <summary>หลักการ #10: ทุกการตั้งนโยบายปล่อยผ่าน = ร่องรอยใน hash chain (ผู้ตั้ง · ก่อน/หลัง · รหัสกฎ)</summary>
    private void AuditPolicy(Guid companyId, string entityType, Guid entityId, string? before, string? after, Guid userId)
    {
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = userId,
            Action = AuditAction.Update,
            EntityType = entityType,
            EntityId = entityId.ToString(),
            OldValues = JsonSerializer.Serialize(new { autoApprovePolicy = before }),
            NewValues = JsonSerializer.Serialize(new
            {
                action = "SetAutoApprovePolicy",
                autoApprovePolicy = after,
                ruleCode = AutoApprovePolicy.RuleCode,
                legalReference = ArOverdueApprovalWarning.LegalReference,
            }),
        });
    }

    // ═══════════════ ช่องทางรับเงินของผู้ติดต่อ ═══════════════

    public async Task<ContactAutopilotSettingsResponse> GetContactSettingsAsync(Guid companyId, Guid contactId, CancellationToken ct = default)
    {
        var c = await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contactId && x.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        return ContactView(c);
    }

    public async Task<ContactAutopilotSettingsResponse> UpdateContactPaymentAsync(Guid companyId, Guid contactId, UpdateContactAutopilotRequest request,
        Guid userId, CancellationToken ct = default)
    {
        var c = await _db.Contacts.FirstOrDefaultAsync(x => x.Id == contactId && x.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        if (AutopilotSettingsResolver.ValidateMode(request.PaymentMode) is { } badMode)
            throw new BusinessRuleException(badMode, AutopilotSettingsResolver.RuleCode);
        if (AutopilotSettingsResolver.ValidateChannels(request.PayLinkChannels) is { } badCh)
            throw new BusinessRuleException(badCh, AutopilotSettingsResolver.RuleCode);
        if ((AutopilotSettingsResolver.ValidateTriState(request.PostPayEmailReceipt) ?? AutopilotSettingsResolver.ValidateTriState(request.PostPayIncludePartial)) is { } badTri)
            throw new BusinessRuleException(badTri, AutopilotSettingsResolver.RuleCode);
        var before = new { c.PaymentMode, c.PayLinkChannelsJson, c.PostPayEmailReceipt, c.PostPayIncludePartial, c.AutoSendDisabled };
        if (request.PaymentMode != null) c.PaymentMode = AutopilotSettingsResolver.Normalize(request.PaymentMode);
        if (request.PayLinkChannels != null) c.PayLinkChannelsJson = DocumentPayLinkPolicy.SerializeChannels(request.PayLinkChannels);
        // รอบ 250 (WP-02 B-13): ชั้นผู้ติดต่อของงานหลังรับชำระ (ผู้อ่าน PostPaymentAutomationService.SettingsForAsync) · null = ไม่แก้
        if (request.PostPayEmailReceipt != null) c.PostPayEmailReceipt = AutopilotSettingsResolver.ParseTriState(request.PostPayEmailReceipt);
        if (request.PostPayIncludePartial != null) c.PostPayIncludePartial = AutopilotSettingsResolver.ParseTriState(request.PostPayIncludePartial);
        // รอบ 250 WP-08 (คำตัดสิน 250-29): ชั้นผู้ติดต่อ "ไม่ส่งอัตโนมัติ" (ผู้อ่าน: EmailScheduleService · PostPaymentAutomationService · SignRequestService ผ่าน AutopilotSettingsResolver)
        if (request.AutoSendDisabled is bool autoOff) c.AutoSendDisabled = autoOff;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = userId,
            Action = AuditAction.Update,
            EntityType = "Contact",
            EntityId = c.Id.ToString(),
            OldValues = JsonSerializer.Serialize(before),
            NewValues = JsonSerializer.Serialize(new { action = "SetContactPaymentMode", c.PaymentMode, c.PayLinkChannelsJson,
                c.PostPayEmailReceipt, c.PostPayIncludePartial, c.AutoSendDisabled, ruleCode = AutopilotSettingsResolver.RuleCode,
                autoSendRuleCode = before.AutoSendDisabled != c.AutoSendDisabled ? AutopilotSettingsResolver.AutoSendRuleCode : null }),
        });
        await _db.SaveChangesAsync(ct);
        return ContactView(c);
    }

    private static ContactAutopilotSettingsResponse ContactView(Contact c)
        => new(c.Id, c.Name, c.PaymentMode, DocumentPayLinkPolicy.ParseChannels(c.PayLinkChannelsJson),
            RecurringApprovePolicyLoader.View(AutoApprovePolicy.Parse(c.AutoApprovePolicyJson, AutoApprovePolicy.ScopeContact)),
            AutopilotSettingsResolver.TriStateName(c.PostPayEmailReceipt), AutopilotSettingsResolver.TriStateName(c.PostPayIncludePartial),
            c.AutoSendDisabled);
}

using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Accounting.Services.Implementations;

/// <summary>
/// Orchestrate cross-tenant B2B document flows:
///   • Auto-route Sent documents to the partner company's inbox.
///   • Approve incoming docs with stamped signature on the source side.
///   • Auto-create downstream documents (Quotation approval → PO → Invoice).
///
/// Trust model:
///   • Both sides must accept a TradingPartnership before any auto-flow.
///   • Every routing event is recorded in CrossTenantDocumentLink with an
///     immutable SnapshotJson at approval time, so post-edits at the
///     source don't change what the approver actually saw.
///
/// Toggle model:
///   • WorkflowAutomationConfig per company controls every auto step.
///   • Defaults are conservative (everything off). Companies opt in to
///     the level of automation matching their internal control standards.
/// </summary>
public partial class CrossTenantWorkflowService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CrossTenantWorkflowService> _logger;
    /// <summary>รอบ 255 WP-12B ข้อ 7: ส่งไฟล์ e-Tax (XML ที่ลงลายมือชื่อ) ของผู้ขายเป็นไฟล์แนบของใบซื้อร่างฝั่งผู้รับ · null (สร้างนอก DI) = ไม่ส่ง ⇒ หลักฐานไม่ถึง EtaxVerified</summary>
    private readonly Accounting.Services.Storage.IFileStorage? _storage;
    /// <summary>รอบ 255 ฝ่ายค้าน H3/M2: แจ้งผู้รับเมื่อหลักฐานของการขายเดิมเข้าใบซื้อเดิม / ผู้ขายยกเลิกต้นทาง · null (สร้างนอก DI) = ไม่แจ้ง</summary>
    private readonly Accounting.Services.Interfaces.INotificationEngine? _notify;

    public CrossTenantWorkflowService(AccountingDbContext db, ILogger<CrossTenantWorkflowService> logger,
        Accounting.Services.Storage.IFileStorage? storage = null, Accounting.Services.Interfaces.INotificationEngine? notify = null)
    {
        _db = db;
        _logger = logger;
        _storage = storage;
        _notify = notify;
    }

    // ═══════════════════════════════════════════════════════════════════
    // PARTNERSHIP MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// เชิญคู่ค้าด้วยเลขผู้เสียภาษี (+ สาขา) — รอบ 250 WP-02b (AUTOPILOT_BILLING_DESIGN §6.1 P-1/P-2 · §6.3)
    /// <para><b>ไม่คืนผลว่าพบบริษัทหรือไม่</b> (เดิมคืน <c>null</c> ⇒ controller ตอบ 404 "ไม่พบบริษัท…" vs 200 ⇒ สมาชิกบริษัทใดก็ไล่เลขภาษีหาว่าใครใช้ NextAcc ได้) ·
    /// ผู้เรียกตอบ <see cref="Accounting.Helpers.PartnerCompanyMatch.InviteAckMessage"/> เสมอ · audit ของผู้เชิญเขียน<b>แถวเดียวกันทุกกรณี</b> (ไม่มีผลการค้นในแถว)</para>
    /// <para>จับคู่ด้วย <see cref="Accounting.Helpers.PartnerCompanyMatch.InviteTargets"/> (เลขภาษี + สาขา · รอบ 264: ทุกบริษัทที่ตรง ไม่ดู "ให้คู่ค้าค้นเจอ" ·
    /// กำกวม ⇒ ส่งถึงทุกบริษัทที่ตรง — ผู้รับต้องกดยอมรับเอง) · แจ้งปลายทางผ่าน outbox (<see cref="DispatchPendingLinkRequestNoticesAsync"/>) ·
    /// เคยปฏิเสธไม่ถึง 7 วัน ⇒ ไม่เปิดคำเชิญซ้ำ (กันสแปม §6.3) — คำตอบยังเหมือนเดิม</para>
    /// เลขภาษีผิดรูป ⇒ <see cref="Accounting.Helpers.BusinessRuleException"/> (ตัดสินจากรูปแบบอย่างเดียว ไม่ดูฐาน ⇒ ไม่รั่ว)
    /// </summary>
    public async Task InvitePartnerAsync(
        Guid invitingCompanyId, string partnerTaxId, string? partnerBranchCode, Guid invitedByUserId, string? message)
    {
        // ฝ่ายค้าน H3: ข้อความเชิญยาวไม่จำกัด = ช่องฟิชชิง — ตัดที่ 500 ตัวอักษร (ข้อความถูกแสดงแบบ text เท่านั้น)
        message = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (message is { Length: > 500 }) message = message[..500];
        var taxId = Accounting.Helpers.PartnerCompanyMatch.NormalizeTaxId(partnerTaxId)
            ?? throw new Accounting.Helpers.BusinessRuleException(
                Accounting.Helpers.PartnerCompanyMatch.InvalidTaxIdMessage, Accounting.Helpers.PartnerCompanyMatch.RuleCode, 400);
        if (!string.IsNullOrWhiteSpace(partnerBranchCode)
            && !Accounting.Helpers.TaxBranchCode.TryNormalize(partnerBranchCode, out _, out var branchError))
            throw new Accounting.Helpers.BusinessRuleException(branchError ?? "รหัสสาขาไม่ถูกต้อง", Accounting.Helpers.PartnerCompanyMatch.RuleCode, 400);

        // ฝ่ายค้าน H3: เวลาตอบเท่ากันทุกทาง — งานที่ต่างกัน (สร้าง/เปิดคำเชิญ) ถูกเติมเวลาจนถึงเพดานคงที่ (กันวัดเวลาแยก "มี/ไม่มี") ·
        // รอบ 264 ฝ่ายค้าน M1: เติมเวลาใน finally — ทุกทาง (รวม exception) ใช้เวลาเท่ากัน
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await InviteCoreAsync(invitingCompanyId, taxId, (partnerTaxId ?? "").Trim(), partnerBranchCode, invitedByUserId, message);
        }
        finally
        {
            var rest = InviteMinimumDuration - sw.Elapsed;
            if (rest > TimeSpan.Zero) await Task.Delay(rest);
        }
    }

    private async Task InviteCoreAsync(Guid invitingCompanyId, string taxId, string rawTax, string? partnerBranchCode, Guid invitedByUserId, string? message)
    {
        var now = DateTime.UtcNow;
        // รอบ 264 (คำตัดสิน 264-1-2): "คำขอที่เราส่ง" — เกิดทุกครั้ง รูปเดียวกันทุกกรณี (ไม่มีผลการค้นในแถว) ⇒ ผู้ขอติดตามสถานะได้โดยไม่รู้ว่าเลขนี้ใช้ NextAcc
        var request = new PartnerLinkRequest
        {
            CompanyId = invitingCompanyId, PartnerTaxId = taxId, PartnerBranchCode = Accounting.Helpers.PartnerCompanyMatch.WantedBranch(partnerBranchCode),
            Message = message, RequestedByUserId = invitedByUserId, ExpiresAt = now.AddDays(Accounting.Helpers.PartnerLinkRequestRules.ExpiryDays),
            CreatedBy = invitedByUserId.ToString(),
        };
        _db.PartnerLinkRequests.Add(request);
        // รอบ 264 (คำตัดสิน 264-1-1 · แทน 257-3-3): ค้นทุกบริษัทที่ตรงเลขภาษี — ไม่ดู PartnerDiscoverable (เดิมกรอง ⇒ ปลายทางที่ไม่เปิด = ไม่พบ ⇒ คำขอหายเงียบ
        // ทั้งที่ผู้ใช้ส่งเลขถูก) · ปลายทางตัดสินเองด้วยการกดยอมรับ · กำกวม ⇒ ส่งถึงทุกบริษัทที่ตรง (InviteTargets · คำตัดสิน 264-1-3)
        var candidates = await _db.Companies.AsNoTracking()
            .Where(c => !c.IsDeleted && c.Id != invitingCompanyId && (c.TaxId == taxId || c.TaxId == rawTax))
            .Select(c => new Accounting.Helpers.PartnerCompanyCandidate(c.Id, c.BranchCode))
            .ToListAsync();
        var targets = Accounting.Helpers.PartnerCompanyMatch.InviteTargets(candidates, partnerBranchCode);

        // ฝ่ายค้าน M1 (คำตัดสิน 264-1-9): อ่าน-แล้วสร้างแถวคู่ใต้ pg_advisory_xact_lock ต่อคู่ — สองคำขอพร้อมกันไม่ชน unique (เดิมตัวที่สองได้ 500 ⇒ แยก "มี/ไม่มี" ได้)
        var ownTx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            var outcomes = new List<string>();   // ใช้ใน log ของเซิร์ฟเวอร์เท่านั้น — ห้ามไหลเข้า response/audit ที่ผู้เชิญอ่านได้
            if (targets.Count == 0) outcomes.Add(Accounting.Helpers.PartnerMatchKind.None.ToString());
            foreach (var partnerId in targets)
            {
                var (a, b) = OrderPair(invitingCompanyId, partnerId);
                await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                    Accounting.Helpers.AdvisoryLockKey.For(a, Accounting.Helpers.AdvisoryLockKey.TradingPartnerInvite, "invite:" + b.ToString("N")));
                var existing = await _db.TradingPartnerships
                    .FirstOrDefaultAsync(p => p.CompanyAId == a && p.CompanyBId == b);
                string outcome;
                if (existing == null)
                {
                    _db.TradingPartnerships.Add(new TradingPartnership
                    {
                        CompanyAId = a,
                        CompanyBId = b,
                        Status = TradingPartnershipStatus.Pending,
                        InvitedByCompanyId = invitingCompanyId,
                        InvitedByUserId = invitedByUserId,
                        InvitationMessage = message,
                        CreatedBy = invitedByUserId.ToString(),
                        InvitedAt = now,
                        InviteNoticeDueAt = now,
                        LinkRequestId = request.Id,
                    });
                    outcome = "Created";
                }
                // ฝ่ายค้าน L8 (คำตัดสิน 264-1-13): อีกฝ่าย "ไม่รับคำขอจากบริษัทนี้อีก" ⇒ ไม่เปิด ไม่แจ้ง (คำตอบเท่าเดิม)
                else if (Accounting.Helpers.PartnerLinkRequestRules.BlockedForRequester(existing.RequestsBlockedByCompanyId, invitingCompanyId))
                    outcome = "Blocked";
                // รอบ 257 ชุด 3 (ฝ่ายค้าน LOW): ระงับแล้วต้องพ้นช่วงพัก 7 วันเหมือนถูกปฏิเสธ (ตัวตัดสินเดียว ReinviteAllowed) — คำตอบยังเหมือนเดิมทุกกรณี ·
                // รอบ 264: คำขอที่รอตอบแต่หมดอายุแล้ว ⇒ เปิดใหม่เป็นคำขอสด (แจ้งใหม่) · ยังไม่หมดอายุ ⇒ ไม่แจ้งซ้ำ (กันสแปม)
                else if (Accounting.Helpers.PartnerExchangeRules.ReinviteAllowed(existing.Status, existing.RejectedAt, existing.SuspendedAt, now, RejectedReinviteCooldownDays)
                    || Accounting.Helpers.PartnerLinkRequestRules.IsExpired(existing.Status, existing.InvitedAt, existing.CreatedAt, now))
                {
                    // เชิญซ้ำหลังถูกปฏิเสธ (พ้นช่วงพัก) / ถูกระงับ / หมดอายุ — เปิดใหม่ให้ปลายทางเห็นคำเชิญสด
                    // (รอบ 255 ฝ่ายค้าน M4: ยอมรับได้จาก Pending เท่านั้น ⇒ ทางกลับของแถวที่ถูกระงับคือเชิญใหม่ทางนี้)
                    existing.Status = TradingPartnershipStatus.Pending;
                    existing.InvitedByCompanyId = invitingCompanyId;
                    existing.InvitedByUserId = invitedByUserId;
                    existing.InvitationMessage = message;
                    existing.RejectedAt = null;
                    existing.RejectionReason = null;
                    existing.SuspendedAt = null;
                    existing.UpdatedAt = now;
                    existing.InvitedAt = now;
                    existing.InviteNoticeDueAt = now;
                    existing.InviteNotifiedAt = null;
                    existing.InviteNoticeResult = null;
                    existing.LinkRequestId = request.Id;
                    existing.RequestsBlockedByCompanyId = null;   // ผู้บล็อกส่งคำขอเอง = ปลดบล็อก
                    existing.RequestsBlockedAt = null;
                    outcome = "Reopened";
                }
                // ฝ่ายค้าน L1: ส่งซ้ำระหว่างรอ (เราเป็นผู้ขอ) ⇒ ต่ออายุคู่ให้ตรงกับแถวคำขอใหม่ · ไม่แจ้งซ้ำ
                else if (existing.Status == TradingPartnershipStatus.Pending && existing.InvitedByCompanyId == invitingCompanyId)
                {
                    existing.InvitedAt = now;
                    existing.UpdatedAt = now;
                    existing.LinkRequestId = request.Id;
                    outcome = "Refreshed";
                }
                // ฝ่ายค้าน L3: เชื่อมกันอยู่แล้ว ⇒ คำขอของเรา = เชื่อมแล้ว (ผู้ขอเห็นคู่นี้ในตารางคู่ค้าอยู่แล้ว ⇒ ไม่รั่ว)
                else if (existing.Status == TradingPartnershipStatus.Accepted)
                {
                    request.AcceptedAt = now;
                    request.AcceptedPartnershipId = existing.Id;
                    outcome = "AlreadyLinked";
                }
                else outcome = "Unchanged:" + existing.Status;
                outcomes.Add(outcome);
                // รอบ 264 (คำตัดสิน 264-1-4 · แทนแถวกระดิ่งตรงของ 257-3-3): การแจ้งปลายทางไปที่ outbox (InviteNoticeDueAt) — งานเบื้องหลังส่งผ่านเครื่องแจ้งเตือน
                // (กระดิ่ง + อีเมล + LINE ตามตารางของปลายทาง) · ในคำขอนี้ไม่มีงานช่องภายนอก ⇒ เวลาตอบไม่ต่างจนวัดได้ (ถูกเติมถึงเพดานคงที่เหมือนเดิม)
            }

            // audit ของผู้เชิญ — เนื้อหาเหมือนกันทุกกรณี (ผู้ดูแลของบริษัทผู้เชิญอ่าน audit ได้ ⇒ ห้ามมีผลการค้น)
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = invitingCompanyId,
                UserId = invitedByUserId,
                Action = AuditAction.Create,
                EntityType = "TradingPartnerInvite",
                EntityId = invitingCompanyId.ToString(),
                NewValues = JsonSerializer.Serialize(new
                {
                    @event = "TradingPartnerInviteRequested",
                    partnerTaxId = taxId,
                    partnerBranchCode = Accounting.Helpers.PartnerCompanyMatch.WantedBranch(partnerBranchCode),
                    ruleCode = Accounting.Helpers.PartnerCompanyMatch.RuleCode,
                }),
                Timestamp = now,
            });
            await _db.SaveChangesAsync();
            if (ownTx != null) await ownTx.CommitAsync();
            _logger.LogInformation("Trading partner invite from {Company}: outcome {Outcome}", invitingCompanyId, string.Join(",", outcomes));
        }
        finally
        {
            if (ownTx != null) await ownTx.DisposeAsync();
        }
    }

    /// <summary>ฝ่ายค้าน H3: เวลาขั้นต่ำของการเชิญทุกกรณี (กันวัดเวลาตอบแยกสมาชิกภาพ) — สูงกว่างานจริงปกติ</summary>
    public static readonly TimeSpan InviteMinimumDuration = TimeSpan.FromMilliseconds(400);

    /// <summary>ฝ่ายค้าน H3: เพดานคำเชิญต่อบริษัทต่อวัน (AUTOPILOT_BILLING_DESIGN §6.3 · กันสแปม/ฟิชชิงผ่านข้อความเชิญ)</summary>
    public const int InvitesPerCompanyPerDay = 20;

    /// <summary>ช่วงพักก่อนเชิญซ้ำหลังปลายทางปฏิเสธ (AUTOPILOT_BILLING_DESIGN §6.3 "เชิญซ้ำได้หลัง 7 วัน")</summary>
    public const int RejectedReinviteCooldownDays = 7;

    public async Task<TradingPartnership> AcceptPartnershipAsync(
        Guid acceptingCompanyId, Guid partnershipId, Guid acceptedByUserId)
    {
        var p = await _db.TradingPartnerships
            .FirstOrDefaultAsync(x => x.Id == partnershipId)
            ?? throw new InvalidOperationException("ไม่พบ trading partnership");
        if (p.CompanyAId != acceptingCompanyId && p.CompanyBId != acceptingCompanyId)
            throw new UnauthorizedAccessException("บริษัทไม่ได้เป็นส่วนหนึ่งของ partnership นี้");
        if (p.InvitedByCompanyId == acceptingCompanyId)
            throw new InvalidOperationException("ผู้เชิญไม่สามารถยอมรับเอง — รอผู้รับเชิญตอบกลับ");
        if (p.Status == TradingPartnershipStatus.Accepted) return p;
        // รอบ 255 ฝ่ายค้าน M4: ยอมรับได้จากสถานะ "รอยอมรับ" เท่านั้น — ถูกปฏิเสธ/ระงับแล้วต้องเชิญใหม่ (เส้นเชิญเปิดคำเชิญสดให้) ·
        // รอบ 264 (คำตัดสิน 264-1-5): คำขอที่หมดอายุ (30 วัน) ยอมรับไม่ได้ — ทางไปต่อ = ส่งคำขอใหม่ (ฝั่งไหนก็ได้)
        if (Accounting.Helpers.PartnerLinkRequestRules.AcceptProblem(p.Status, p.InvitedAt, p.CreatedAt, DateTime.UtcNow) is string acceptProblem)
            throw new Accounting.Helpers.BusinessRuleException(acceptProblem, Accounting.Helpers.PartnerExchangeRules.RuleCode, 409);

        // รอบ 255 ฝ่ายค้าน M5: ตรวจเพดาน + ยอมรับ ใต้ pg_advisory_xact_lock ของทั้งสองบริษัท (เรียงตาม Guid กัน deadlock) —
        // สองคำยอมรับพร้อมกันไม่ผ่านเพดานทั้งคู่
        var ownTx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            foreach (var cid in new[] { p.CompanyAId, p.CompanyBId }.OrderBy(x => x))
                await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                    Accounting.Helpers.AdvisoryLockKey.For(cid, Accounting.Helpers.AdvisoryLockKey.TradingPartnerAccept, "accept"));
            await _db.Entry(p).ReloadAsync();
            if (p.Status == TradingPartnershipStatus.Accepted) { if (ownTx != null) await ownTx.CommitAsync(); return p; }
            if (Accounting.Helpers.PartnerLinkRequestRules.AcceptProblem(p.Status, p.InvitedAt, p.CreatedAt, DateTime.UtcNow) is string raced)
                throw new Accounting.Helpers.BusinessRuleException(raced, Accounting.Helpers.PartnerExchangeRules.RuleCode, 409);
            // รอบ 255 WP-12B ข้อ 6 (Y5): เพดานจำนวนคู่ต่อแพ็กเกจ — ตั้งต้นไม่จำกัด · ตั้งแล้วตรวจทั้งสองบริษัทก่อนยอมรับ
            await EnsureTradingPartnerCapAsync(p);

            var acceptedAt = DateTime.UtcNow;
            p.Status = TradingPartnershipStatus.Accepted;
            p.AcceptedAt = acceptedAt;
            p.AcceptedByUserId = acceptedByUserId;
            p.UpdatedAt = acceptedAt;
            p.InviteNoticeDueAt = null;   // รอบ 264: ตอบแล้ว ⇒ ไม่ต้องแจ้ง "มีคำขอ" อีก (outbox)
            // รอบ 264 (คำตัดสิน 264-1-6 · PDPA ม.19): หลักฐานความยินยอมของผู้ยอมรับ — ใครกด เมื่อไร กับผู้ขอรายใด (ข้อมูลของเราถึงผู้ขอนับจากนี้)
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = acceptingCompanyId, UserId = acceptedByUserId, Action = AuditAction.Update,
                EntityType = "TradingPartnership", EntityId = p.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new
                {
                    @event = "TradingPartnerLinkAccepted", requesterCompanyId = p.InvitedByCompanyId,
                    ruleCode = Accounting.Helpers.PartnerLinkRequestRules.RuleCode, legalReference = Accounting.Helpers.PartnerLinkRequestRules.LegalReference,
                }),
                Timestamp = acceptedAt,
            });
            await _db.SaveChangesAsync();
            // รอบ 264 (คำตัดสิน 264-1-2 · ฝ่ายค้าน L2 คำตัดสิน 264-1-14): "คำขอที่เราส่ง" ของผู้ขอ ⇒ เชื่อมแล้ว — คำขอที่เปิดแถวนี้ +
            // คำขออื่นที่ยังไม่หมดอายุซึ่ง<b>ส่งถึงเราจริง</b>: เลขภาษีตรง และ (สาขาตรงเป๊ะ หรือไม่ระบุสาขาแล้วตัวเลือกผู้รับเลือกเรา — InviteTargets ตัวเดียวกับตอนส่ง)
            var me = await _db.Companies.AsNoTracking().Where(c => c.Id == acceptingCompanyId).Select(c => new { c.TaxId, c.BranchCode }).FirstOrDefaultAsync();
            var myTax = Accounting.Helpers.PartnerCompanyMatch.NormalizeTaxId(me?.TaxId) ?? "";
            var myBranch = Accounting.Helpers.TaxBranchCode.Normalize(me?.BranchCode);
            var noBranchReachesMe = false;
            if (myTax != "")
            {
                var rawMyTax = (me?.TaxId ?? "").Trim();
                var sameTax = await _db.Companies.AsNoTracking()
                    .Where(c => !c.IsDeleted && c.Id != p.InvitedByCompanyId && (c.TaxId == myTax || c.TaxId == rawMyTax))
                    .Select(c => new Accounting.Helpers.PartnerCompanyCandidate(c.Id, c.BranchCode)).ToListAsync();
                noBranchReachesMe = Accounting.Helpers.PartnerCompanyMatch.InviteTargets(sameTax, null).Contains(acceptingCompanyId);
            }
            var linkRequestId = p.LinkRequestId ?? Guid.Empty;
            await _db.PartnerLinkRequests
                .Where(r => r.CompanyId == p.InvitedByCompanyId && r.AcceptedAt == null
                    && (r.Id == linkRequestId
                        || (myTax != "" && r.PartnerTaxId == myTax && r.ExpiresAt >= acceptedAt
                            && (r.PartnerBranchCode == myBranch || (r.PartnerBranchCode == null && noBranchReachesMe)))))
                .ExecuteUpdateAsync(x => x.SetProperty(r => r.AcceptedAt, (DateTime?)acceptedAt).SetProperty(r => r.AcceptedPartnershipId, (Guid?)p.Id));
            if (ownTx != null) await ownTx.CommitAsync();
        }
        finally
        {
            if (ownTx != null) await ownTx.DisposeAsync();
        }
        await NotifyRequesterAcceptedAsync(p, acceptingCompanyId, acceptedByUserId);
        return p;
    }

    /// <summary>รอบ 264 (คำตัดสิน 264-1-3): ผู้ขอได้แจ้งว่าใครตอบรับ (ชื่อ + เลขภาษี + สาขาของผู้ยอมรับ — ยินยอมแล้ว) ⇒ ตรวจได้ว่าเป็นคู่ค้าที่ตั้งใจ
    /// (กันบริษัทที่ตั้งเลขภาษีซ้ำรับคำขอแทน) · หลัง commit · แจ้งล้มไม่ย้อนการยอมรับ (เครื่องแจ้งเตือนไม่โยน)</summary>
    private async Task NotifyRequesterAcceptedAsync(TradingPartnership p, Guid acceptingCompanyId, Guid actorUserId)
    {
        if (_notify == null) return;
        var me = await _db.Companies.AsNoTracking().Where(c => c.Id == acceptingCompanyId)
            .Select(c => new { c.TaxId, c.BranchCode }).FirstOrDefaultAsync();
        // ฝ่ายค้าน L4 (คำตัดสิน 264-1-11): คีย์ของตัวเอง (เดิมยืม autopilot.partner_exchange = "ยกเลิก 50 ทวิ") · M2/L5/L6: ไม่มีชื่อ (ข้อความอิสระ) · เลขปิดบัง · "ไม่ได้ยืนยัน"
        await _notify.DispatchAsync(p.InvitedByCompanyId, Accounting.Models.Constants.NotificationEvents.PartnerLinkAccepted, new Accounting.Services.Interfaces.NotificationContext
        {
            Title = Accounting.Helpers.PartnerLinkRequestRules.AcceptedNoticeTitle,
            Message = Accounting.Helpers.PartnerLinkRequestRules.AcceptedNoticeMessage(me?.TaxId, me?.BranchCode),
            ActionUrl = "/pages/partners.html?company=" + p.InvitedByCompanyId,
            EntityType = "TradingPartnership", EntityId = p.Id, ActorUserId = actorUserId,
        });
    }

    /// <summary>ปฏิเสธคำขอเชื่อม — รอบ 264 ฝ่ายค้าน M4 (คำตัดสิน 264-1-12): เฉพาะคำขอที่รอเรายอมรับ (Pending และเราไม่ใช่ผู้ขอ) · อื่น ๆ ⇒ 409 + ทางไปต่อ "ระงับการเชื่อม"
    /// (เดิมตัดคู่ที่เชื่อมแล้วได้เงียบ ๆ โดยไม่มีหลักฐาน) · audit <c>TradingPartnerLinkRejected</c> (เหตุผลเก็บฝั่งผู้ปฏิเสธ · ผู้ขอไม่ได้สัญญาณ — 264-1-2) ·
    /// <paramref name="blockRequester"/> = "ไม่รับคำขอจากบริษัทนี้อีก" (คำตัดสิน 264-1-13)</summary>
    public async Task<TradingPartnership> RejectPartnershipAsync(
        Guid rejectingCompanyId, Guid partnershipId, string? reason, Guid? rejectedByUserId = null, bool blockRequester = false)
    {
        var p = await _db.TradingPartnerships
            .FirstOrDefaultAsync(x => x.Id == partnershipId)
            ?? throw new InvalidOperationException("ไม่พบ trading partnership");
        if (p.CompanyAId != rejectingCompanyId && p.CompanyBId != rejectingCompanyId)
            throw new UnauthorizedAccessException("บริษัทไม่ได้เป็นส่วนหนึ่งของ partnership นี้");
        if (Accounting.Helpers.PartnerLinkRequestRules.RejectProblem(p.Status, p.InvitedByCompanyId == rejectingCompanyId) is string problem)
            throw new Accounting.Helpers.BusinessRuleException(problem, Accounting.Helpers.PartnerLinkRequestRules.RuleCode, 409);

        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason is { Length: > 500 }) reason = reason[..500];
        var now = DateTime.UtcNow;
        p.Status = TradingPartnershipStatus.Rejected;
        p.RejectedAt = now;
        p.RejectionReason = reason;
        p.UpdatedAt = now;
        p.InviteNoticeDueAt = null;   // รอบ 264: ตอบแล้ว ⇒ ไม่แจ้ง "มีคำขอ" (outbox) · ผู้ขอไม่ได้รับสัญญาณการปฏิเสธ (คำตัดสิน 264-1-2)
        if (blockRequester)
        {
            p.RequestsBlockedByCompanyId = rejectingCompanyId;
            p.RequestsBlockedAt = now;
        }
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = rejectingCompanyId, UserId = rejectedByUserId, Action = AuditAction.Update,
            EntityType = "TradingPartnership", EntityId = p.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                @event = "TradingPartnerLinkRejected", requesterCompanyId = p.InvitedByCompanyId, reason, blockRequester,
                ruleCode = Accounting.Helpers.PartnerLinkRequestRules.RuleCode, legalReference = Accounting.Helpers.PartnerLinkRequestRules.LegalReference,
            }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync();
        return p;
    }

    // ═══════════════════════════════════════════════════════════════════
    // DOCUMENT ROUTING (hook — called when a Document becomes "Sent")
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Called by the document service when a Document moves into
    /// Sent status. If the recipient (Contact) maps to a Company we have
    /// an Accepted trading partnership with, a CrossTenantDocumentLink in
    /// PendingApproval state is created — and the receiving company's
    /// inbox now shows the doc.
    /// </summary>
    public async Task<CrossTenantDocumentLink?> OnDocumentSentAsync(Document doc)
    {
        if (doc == null) return null;

        // Only route doc types that have a cross-tenant approval workflow:
        // Quotations are the canonical case. We also route PO/Invoice for
        // visibility but the linkType encodes which one.
        var linkType = doc.DocumentType switch
        {
            DocumentType.Quotation => CrossTenantLinkType.QuotationFlow,
            DocumentType.PurchaseOrder => CrossTenantLinkType.PoFlow,
            DocumentType.Invoice or DocumentType.TaxInvoice => CrossTenantLinkType.InvoiceFlow,
            DocumentType.Receipt or DocumentType.ReceiptVoucher => CrossTenantLinkType.ReceiptFlow,
            _ => (CrossTenantLinkType?)null
        };
        if (linkType == null) return null;

        // Lookup recipient Contact → corresponding Company (เลขภาษี + สาขา · รอบ 250 WP-02b P-2)
        var contact = await _db.Contacts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == doc.ContactId && c.CompanyId == doc.CompanyId && !c.IsDeleted);
        if (contact == null) return null;
        var taxId = Accounting.Helpers.PartnerCompanyMatch.NormalizeTaxId(contact.TaxId);
        if (taxId == null) return null;   // ไม่ใช่ 13 หลัก ("-" ของบริษัทที่ยังไม่กรอกเลข) — เดิมเทียบตรงตัวแล้วชนบริษัทอื่นที่มี "-" เหมือนกัน

        // ผู้สมัคร = บริษัทที่มีคู่ค้า Accepted กับเราอยู่แล้ว + เลขภาษีตรง ⇒ ตัดสินสาขาด้วยตัวจับคู่เดียว (เดิม FirstOrDefault ไม่ดูสาขา = สุ่ม)
        var acceptedPartnerIds = await _db.TradingPartnerships.AsNoTracking()
            .Where(p => (p.CompanyAId == doc.CompanyId || p.CompanyBId == doc.CompanyId)
                && p.Status == TradingPartnershipStatus.Accepted)
            .Select(p => p.CompanyAId == doc.CompanyId ? p.CompanyBId : p.CompanyAId)
            .ToListAsync();
        if (acceptedPartnerIds.Count == 0) return null;
        var rawTax = contact.TaxId!.Trim();
        var candidates = await _db.Companies.AsNoTracking()
            .Where(c => acceptedPartnerIds.Contains(c.Id) && !c.IsDeleted && c.Id != doc.CompanyId
                && (c.TaxId == taxId || c.TaxId == rawTax))
            .Select(c => new Accounting.Helpers.PartnerCompanyCandidate(c.Id, c.BranchCode))
            .ToListAsync();
        var pick = Accounting.Helpers.PartnerCompanyMatch.Pick(candidates, contact.BranchCode);
        if (pick.Kind == Accounting.Helpers.PartnerMatchKind.Ambiguous)
            // ห้ามเดาว่าส่งเข้าสมุดของบริษัทไหน — ล้มดัง (RouteSentDocumentAsync ประทับป้ายบนเอกสาร + ทางไปต่อในข้อความ)
            throw new Accounting.Helpers.BusinessRuleException(
                Accounting.Helpers.PartnerCompanyMatch.AmbiguousRouteMessage(taxId), Accounting.Helpers.PartnerCompanyMatch.RuleCode, 409);
        if (pick.Kind == Accounting.Helpers.PartnerMatchKind.None && candidates.Count > 0)
            // ฝ่ายค้าน M5: เลขตรงคู่ Accepted แต่สาขาไม่ตรง — เดิมส่งได้ (ไม่ดูสาขา) ⇒ ไม่ส่งเงียบไม่ได้: ล้มดังพร้อมทางไปต่อ
            throw new Accounting.Helpers.BusinessRuleException(
                Accounting.Helpers.PartnerCompanyMatch.BranchMismatchRouteMessage(taxId, contact.BranchCode), Accounting.Helpers.PartnerCompanyMatch.RuleCode, 409);
        if (pick.CompanyId is not Guid partnerCompanyId) return null;

        var (a, b) = OrderPair(doc.CompanyId, partnerCompanyId);
        var partnership = await _db.TradingPartnerships
            .FirstOrDefaultAsync(p => p.CompanyAId == a && p.CompanyBId == b
                && p.Status == TradingPartnershipStatus.Accepted);
        if (partnership == null) return null;

        // Idempotent: if a link already exists for this doc, don't dup.
        var existingLink = await _db.CrossTenantDocumentLinks
            .FirstOrDefaultAsync(l => l.SourceDocumentId == doc.Id && l.LinkType == linkType);
        if (existingLink != null) return existingLink;

        var link = new CrossTenantDocumentLink
        {
            TradingPartnershipId = partnership.Id,
            SourceCompanyId = doc.CompanyId,
            SourceDocumentId = doc.Id,
            TargetCompanyId = partnerCompanyId,
            LinkType = linkType.Value,
            Status = CrossTenantLinkStatus.PendingApproval,
            CreatedBy = "system-routing",
        };
        _db.CrossTenantDocumentLinks.Add(link);
        await _db.SaveChangesAsync();

        // Check target's config for auto-approve
        await TryAutoApproveAsync(link);
        // รอบ 255 WP-12B ข้อ 7 (N3): ใบแจ้งหนี้/ใบกำกับ ⇒ ใบซื้อร่างของผู้รับ (ค่าตั้งต้นปิด · ไม่อนุมัติ/ไม่ลงภาษีซื้อเอง · ล้มไม่ล้มการส่งของผู้ขาย)
        await TryCreatePurchaseDraftAsync(link);

        return link;
    }

    /// <summary>ผลการส่งเอกสารเข้าระบบคู่ค้า — <paramref name="Routed"/> = มีแถวในกล่องของคู่ค้าแล้ว ·
    /// <paramref name="FailureReason"/> ≠ null = ล้ม (ป้ายถูกประทับบนเอกสารแล้ว)</summary>
    public sealed record PartnerRouteOutcome(bool Routed, string? FailureReason);

    /// <summary>
    /// **ทางเข้าเดียวของ "ส่งเอกสารที่อนุมัติแล้วเข้ากล่องคู่ค้า"** — รอบ 250 WP-02b (P-6 · ล้มดัง 3 ที่ F2 ข้อ 7)
    /// <para>เดิม <c>DocumentService</c> ห่อ <see cref="OnDocumentSentAsync"/> ด้วย <c>try/LogWarning</c> ⇒ ล้มเงียบ · ตอนนี้ล้ม ⇒
    /// (1) ป้าย <see cref="Accounting.Helpers.PartnerRoutingFailedNote"/> บนเอกสาร (หน้าเอกสารแสดงแถบเตือน + ปุ่มส่งใหม่) · (2) <c>LogError</c> ·
    /// (3) คืนเหตุให้ผู้เรียก · สำเร็จ ⇒ ล้างป้ายของรอบก่อน · ไม่โยน (การอนุมัติ commit ไปแล้ว — ส่งใหม่ได้ด้วย <see cref="RetryRoutingAsync"/>)</para>
    /// </summary>
    public async Task<PartnerRouteOutcome> RouteSentDocumentAsync(Document doc, CancellationToken ct = default)
    {
        CrossTenantDocumentLink? link;
        try
        {
            link = await OnDocumentSentAsync(doc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // แถวที่ SaveChanges ล้มยังค้างใน ChangeTracker ⇒ SaveChanges ถัดไปของผู้เรียกจะลองเขียนซ้ำแล้วล้มอีก — ทิ้งก่อน (ฝ่ายค้าน L3: ครบทุกชนิด)
            DetachPendingCrossTenantWrites();
            var reason = ex is Accounting.Helpers.BusinessRuleException
                ? ex.Message
                : "ระบบขัดข้องระหว่างส่ง — กดส่งใหม่ (ถ้ายังล้ม แจ้งผู้ดูแลพร้อมเลขเอกสาร)";
            _logger.LogError(ex, "{Marker} ส่งเอกสาร {DocNumber} ({DocId}) ของบริษัท {Company} เข้าระบบคู่ค้าไม่สำเร็จ: {Reason}",
                Accounting.Helpers.PartnerRoutingFailedNote.Marker, doc.DocumentNumber, doc.Id, doc.CompanyId, reason);
            await StampRoutingNoteAsync(doc, reason, ct);
            return new PartnerRouteOutcome(false, reason);
        }
        if (Accounting.Helpers.PartnerRoutingFailedNote.Has(doc.InternalNotes))
            await StampRoutingNoteAsync(doc, null, ct);   // ส่งสำเร็จ/ไม่มีคู่ค้าแล้ว ⇒ ป้ายของรอบก่อนไม่จริงอีกต่อไป
        return new PartnerRouteOutcome(link != null, null);
    }

    /// <summary>ส่งเอกสารเข้าระบบคู่ค้าอีกครั้ง (ปุ่ม "ส่งให้คู่ค้าอีกครั้ง") — เฉพาะเอกสารที่อนุมัติแล้ว · tenant = <paramref name="companyId"/> ·
    /// ส่งซ้ำปลอดภัย (<see cref="OnDocumentSentAsync"/> idempotent ต่อเอกสาร+ชนิด)</summary>
    public async Task<PartnerRouteOutcome> RetryRoutingAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        if (doc.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Voided or DocumentStatus.Rejected)
            throw new Accounting.Helpers.BusinessRuleException(
                "ส่งให้คู่ค้าได้เฉพาะเอกสารที่อนุมัติแล้ว", Accounting.Helpers.PartnerRoutingFailedNote.RuleCode, 409);
        return await RouteSentDocumentAsync(doc, ct);
    }

    /// <summary>ประทับ/ล้างป้ายบน <c>InternalNotes</c> แถวเดียว (ExecuteUpdate — ไม่ปนกับ SaveChanges ของผู้เรียก) ·
    /// <paramref name="reason"/> = null ⇒ ล้างป้าย · อ่านค่าล่าสุดจากฐานก่อนต่อท้าย (รูปแบบเดียวกับ IssuedDocumentHooks.StampNoteAsync)</summary>
    private async Task StampRoutingNoteAsync(Document doc, string? reason, CancellationToken ct)
    {
        try
        {
            var current = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == doc.Id && d.CompanyId == doc.CompanyId)
                .Select(d => d.InternalNotes)
                .FirstOrDefaultAsync(ct);
            var updated = reason == null
                ? Accounting.Helpers.PartnerRoutingFailedNote.Clear(current)
                : Accounting.Helpers.PartnerRoutingFailedNote.Append(current, reason);
            if (updated == current) return;
            await _db.Documents
                .Where(d => d.Id == doc.Id && d.CompanyId == doc.CompanyId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.InternalNotes, updated), ct);

            // ให้ entity ในหน่วยความจำตรงกับฐาน โดยไม่ทำให้ SaveChanges ถัดไปของผู้เรียกเขียนซ้ำ
            doc.InternalNotes = updated;
            var entry = _db.Entry(doc);
            if (entry.State is EntityState.Unchanged or EntityState.Modified)
            {
                var prop = entry.Property(d => d.InternalNotes);
                prop.OriginalValue = updated;
                prop.IsModified = false;
            }
        }
        catch (Exception saveEx) when (saveEx is not OperationCanceledException)
        {
            _logger.LogError(saveEx, "บันทึกป้าย {Marker} ลงเอกสาร {DocNumber} ไม่สำเร็จ: {Reason}",
                Accounting.Helpers.PartnerRoutingFailedNote.Marker, doc.DocumentNumber, reason ?? "(ล้างป้าย)");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // INCOMING APPROVAL
    // ═══════════════════════════════════════════════════════════════════

    public async Task<CrossTenantDocumentLink> ApproveIncomingAsync(
        Guid approvingCompanyId, Guid linkId, Guid actingUserId,
        Guid? signatureUserId = null, bool? overrideAutoCreatePo = null, string? comment = null)
    {
        var link = await _db.CrossTenantDocumentLinks
            .Include(l => l.SourceDocument).ThenInclude(d => d.Lines)
            .FirstOrDefaultAsync(l => l.Id == linkId)
            ?? throw new InvalidOperationException("ไม่พบ document link");
        if (link.TargetCompanyId != approvingCompanyId)
            throw new UnauthorizedAccessException("บริษัทไม่ใช่ผู้รับ");
        if (link.Status != CrossTenantLinkStatus.PendingApproval)
            throw new InvalidOperationException($"link อยู่ในสถานะ {link.Status} — ไม่สามารถอนุมัติได้");

        var config = await GetOrCreateConfigAsync(approvingCompanyId);

        // รอบ 2026-10-14 ทีม F4 (ID#10): ผู้ลงนามต้องเป็นสมาชิกที่ยังใช้งานของบริษัทผู้อนุมัติ — เดิมรับ SignatureUserId อะไรก็ได้ (รวมผู้ใช้
        // ของบริษัทอื่น) และผู้อนุมัติเริ่มต้นที่ลาออกแล้วยังลงนามต่อ · ตัวตัดสินเดียว Helpers/CrossTenantSignerResolver + Helpers/CompanyMembership
        var signerMembers = await Accounting.Helpers.CompanyMembership.ActiveMemberIdsAsync(_db, approvingCompanyId,
            new[] { signatureUserId ?? Guid.Empty, config.DefaultApproverUserId ?? Guid.Empty, actingUserId });
        var resolution = Accounting.Helpers.CrossTenantSignerResolver.Resolve(
            signatureUserId, config.DefaultApproverUserId, actingUserId, signerMembers);
        if (resolution.Problem != null)
            throw new Accounting.Helpers.BusinessRuleException(resolution.Problem, Accounting.Helpers.CrossTenantSignerResolver.RuleCode, 400);
        if (resolution.Notice != null)
            _logger.LogWarning("Cross-tenant approve {Link}: ผู้อนุมัติเริ่มต้นไม่ได้เป็นสมาชิกแล้ว — ลงนามในชื่อผู้กดแทน", link.Id);
        var signerUserId = resolution.SignerUserId;
        // ลายเซ็นเริ่มต้นที่ตั้งไว้ใช้ได้เฉพาะเมื่อผู้ลงนามคือผู้อนุมัติเริ่มต้นและลายเซ็นเป็นของคนนั้น (เดิมค่าตั้งนี้ไม่มีผู้อ่าน) ·
        // อื่น ๆ = ลายเซ็นที่ใช้งานอยู่ของผู้ลงนามเอง
        UserSignature? signature = null;
        if (config.DefaultSignatureId is { } defaultSigId && signerUserId == config.DefaultApproverUserId)
            signature = await _db.UserSignatures
                .FirstOrDefaultAsync(s => s.Id == defaultSigId && !s.IsDeleted && s.IsActive && s.UserId == signerUserId);
        signature ??= await _db.UserSignatures
            .Where(s => !s.IsDeleted && s.IsActive && s.UserId == signerUserId)
            .OrderByDescending(s => s.IsDefault)
            .FirstOrDefaultAsync();
        var signer = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == signerUserId && !u.IsDeleted);

        // ─── Create DocumentApproval on the SOURCE side (A's doc) ───
        var approval = new DocumentApproval
        {
            CompanyId = link.SourceCompanyId,
            DocumentId = link.SourceDocumentId,
            ApprovalType = "External",
            ApproverRole = "Customer",
            StepOrder = 1,
            Status = ApprovalStatus.Approved,
            ApproverUserId = signerUserId,
            ApproverName = signer?.FullName,
            ApproverEmail = signer?.Email,
            SignatureId = signature?.Id,
            SignatureData = signature?.SignatureData,
            SignatureFormat = signature?.SignatureFormat,
            ApprovedAt = DateTime.UtcNow,
            Comments = comment,
            PostApprovalAction = link.LinkType == CrossTenantLinkType.QuotationFlow ? "CreatePO" : null,
            // รอบ 193 R4-1: ลายเซ็นคู่ค้า (บทบาท Customer) ต้องผูกกับเนื้อหาที่เซ็น — ตัวเดียวกับเส้นลายเซ็นลูกค้า
            // (ไม่มี hash ⇒ ใบที่ยังเป็นร่างไม่นับลายเซ็นนี้ · ใบที่อนุมัติแล้วไม่กระทบ)
            SignedContentHash = Accounting.Helpers.DocumentSignedContent.Hash(link.SourceDocument, link.SourceDocument.Lines),
        };
        _db.DocumentApprovals.Add(approval);

        // ฝ่ายค้าน 267-P5 MED-6: การอนุมัติของคู่ค้า = ทางลงนามของลูกค้าอีกทาง ⇒ แถวหลักฐานผ่านผู้เขียนเดียว ฝั่งเอกสารของผู้ขาย (บริษัทต้นทาง) ·
        // ช่องทาง Partner · ระดับ PartnerMember (ผู้ใช้ที่ล็อกอินของบริษัทผู้ซื้อ — ไม่ได้ติ๊กรับรองบนหน้าเอกสาร) · ใบเสนอราคา ⇒ ตั้งภาพฉายคำตอบ (คำเตือนอากรแสตมป์ 267-1-14)
        var buyerOrg = await _db.Companies.AsNoTracking().Where(x => x.Id == approvingCompanyId).Select(x => x.Name).FirstOrDefaultAsync();
        var partnerKind = link.SourceDocument.DocumentType == DocumentType.DeliveryNote ? Accounting.Helpers.CustomerSignRule.Kind.DeliverySigned
            : link.SourceDocument.DocumentType == DocumentType.PurchaseOrder ? Accounting.Helpers.CustomerSignRule.Kind.PoAcknowledged
            : Accounting.Helpers.CustomerSignRule.Kind.Accepted;
        var partnerEvidence = Accounting.Services.Implementations.CustomerDocumentResponseService.ExternalEvidence(link.SourceDocument, partnerKind,
            Accounting.Helpers.CustomerSignRule.ChannelPartner, Accounting.Helpers.CustomerSignRule.IdentityPartnerMember, buyerOrg,
            approval.SignedContentHash!, signer?.FullName, null, signer?.Email, comment, Accounting.Helpers.QuotationAcceptance.Sha256Hex(signature?.SignatureData),
            null, actingUserId.ToString(), DateTime.UtcNow);
        await Accounting.Services.Implementations.CustomerDocumentResponseService.AppendEvidenceAsync(_db, partnerEvidence);
        if (link.SourceDocument.DocumentType == DocumentType.Quotation && link.SourceDocument.CustomerResponse == null)
        {
            link.SourceDocument.CustomerResponse = nameof(Accounting.Helpers.CustomerSignRule.Kind.Accepted);
            link.SourceDocument.CustomerResponseAt = DateTime.UtcNow;
        }

        // Also stamp a rendered DocumentSignature row at the source side
        // (matches how the existing approval flow works for in-tenant
        // approvals, so PDF rendering / display logic is identical).
        if (signature != null && config.AutoStampSignature)
        {
            _db.DocumentSignatures.Add(new DocumentSignature
            {
                CompanyId = link.SourceCompanyId,
                DocumentId = link.SourceDocumentId,
                DocumentApprovalId = approval.Id,
                SignerRole = "ผู้อนุมัติ (ภายนอก)",
                SignerName = signer?.FullName ?? "External Approver",
                SignerTitle = "Buyer",
                SignatureData = signature.SignatureData,
                SignatureFormat = signature.SignatureFormat,
                SignedAt = DateTime.UtcNow,
            });
        }

        // ─── Snapshot the source doc so the approval is immutable ───
        link.SnapshotJson = JsonSerializer.Serialize(new
        {
            link.SourceDocument.DocumentNumber,
            link.SourceDocument.DocumentType,
            link.SourceDocument.DocumentDate,
            link.SourceDocument.SubTotal,
            link.SourceDocument.VatAmount,
            link.SourceDocument.TotalAmount,
            Lines = link.SourceDocument.Lines.Select(l => new
            {
                l.Description, l.Quantity, l.UnitPrice, l.Amount, l.VatAmount
            }),
        });

        link.Status = CrossTenantLinkStatus.Approved;
        link.ApproverUserId = actingUserId;
        link.ApprovedAt = DateTime.UtcNow;
        link.DocumentApprovalId = approval.Id;
        link.Comment = comment;
        link.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // ─── Downstream automation ───
        await TryChainDownstreamAsync(link, config, overrideAutoCreatePo, actingUserId);

        return link;
    }

    public async Task<CrossTenantDocumentLink> RejectIncomingAsync(
        Guid rejectingCompanyId, Guid linkId, string reason)
    {
        var link = await _db.CrossTenantDocumentLinks
            .FirstOrDefaultAsync(l => l.Id == linkId)
            ?? throw new InvalidOperationException("ไม่พบ document link");
        if (link.TargetCompanyId != rejectingCompanyId)
            throw new UnauthorizedAccessException("บริษัทไม่ใช่ผู้รับ");
        if (link.Status != CrossTenantLinkStatus.PendingApproval)
            throw new InvalidOperationException($"link อยู่ในสถานะ {link.Status} — ไม่สามารถปฏิเสธได้");

        link.Status = CrossTenantLinkStatus.Rejected;
        link.RejectionReason = reason;
        link.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return link;
    }

    // ═══════════════════════════════════════════════════════════════════
    // AUTO-APPROVAL + DOWNSTREAM CHAIN
    // ═══════════════════════════════════════════════════════════════════

    internal async Task TryAutoApproveAsync(CrossTenantDocumentLink link)
    {
        if (link.LinkType != CrossTenantLinkType.QuotationFlow) return;
        var cfg = await _db.WorkflowAutomationConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyId == link.TargetCompanyId);
        if (cfg == null || !cfg.AutoApproveIncomingQuotations) return;
        if (cfg.DefaultApproverUserId == null) return;  // need a signer

        // ID#10: ผู้อนุมัติเริ่มต้นที่ออกจากบริษัทแล้วห้าม "อนุมัติอัตโนมัติ + ลงนาม" ในนามบริษัทต่อ — ไม่อนุมัติ แต่ไม่เงียบ:
        // หมายเหตุบนเอกสารเข้า (กล่องเอกสารเข้าเห็น) + หน้าตั้งค่าแสดงคำเตือน (GetDefaultApproverNoticeAsync) · ทางไปต่อ = ตั้งผู้อนุมัติเริ่มต้นใหม่
        if (!await Accounting.Helpers.CompanyMembership.IsActiveMemberAsync(_db, link.TargetCompanyId, cfg.DefaultApproverUserId.Value))
        {
            link.Comment = "ไม่ได้อนุมัติอัตโนมัติ: " + Accounting.Helpers.CrossTenantSignerResolver.DepartedDefaultApproverNotice;
            link.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            _logger.LogWarning("Auto-approve skipped for link {Id}: default approver is no longer an active member of {Company}",
                link.Id, link.TargetCompanyId);
            return;
        }

        // Amount gating: respect min/max bounds + partnership credit limit
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == link.SourceDocumentId);
        if (doc == null) return;
        var partnership = await _db.TradingPartnerships.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == link.TradingPartnershipId);
        if (cfg.AutoApproveMinAmount.HasValue && doc.TotalAmount < cfg.AutoApproveMinAmount.Value) return;
        if (cfg.AutoApproveMaxAmount.HasValue && doc.TotalAmount > cfg.AutoApproveMaxAmount.Value) return;
        if (partnership?.AutoApproveAmountLimit.HasValue == true
            && doc.TotalAmount > partnership.AutoApproveAmountLimit.Value) return;

        try
        {
            await ApproveIncomingAsync(
                approvingCompanyId: link.TargetCompanyId,
                linkId: link.Id,
                actingUserId: cfg.DefaultApproverUserId.Value,
                comment: "อนุมัติอัตโนมัติตามการตั้งค่า workflow ข้ามบริษัท");
        }
        catch (Exception ex)
        {
            // ฝ่ายค้าน L3: ล้มกลางทาง ⇒ แถวครึ่งทางของฝั่งผู้รับ (ลายเซ็น/PO/ผู้ติดต่อที่เพิ่งสร้าง) ค้างใน ChangeTracker — SaveChanges ถัดไปของผู้เรียก
            // (เส้นอนุมัติของผู้ขาย) จะเขียนลงสมุดของบริษัทอื่นครึ่ง ๆ ⇒ ทิ้งก่อน · เอกสารเข้ายังรออนุมัติด้วยมือในกล่อง (ไม่เงียบ: LogError)
            DetachPendingCrossTenantWrites();
            _logger.LogError(ex, "Auto-approve failed for link {Id} — เอกสารเข้ายังรออนุมัติด้วยมือ", link.Id);
        }
    }

    /// <summary>ฝ่ายค้าน L3: ทิ้งการเขียนที่ยังไม่บันทึกของเส้นข้ามบริษัท (แถวใหม่ของ PO/บรรทัด/ผู้ติดต่อ/ลายเซ็น/การอนุมัติ + link ที่แก้ค้าง)</summary>
    private void DetachPendingCrossTenantWrites()
    {
        foreach (var e in _db.ChangeTracker.Entries().ToList())
        {
            var crossTenantRow = e.Entity is CrossTenantDocumentLink or DocumentApproval or DocumentSignature;
            var newRow = e.State == EntityState.Added && e.Entity is Document or DocumentLine or Contact;
            if ((crossTenantRow && e.State is EntityState.Added or EntityState.Modified) || newRow)
                e.State = EntityState.Detached;
        }
    }

    private async Task TryChainDownstreamAsync(CrossTenantDocumentLink link, WorkflowAutomationConfig targetCfg,
        bool? overrideAutoCreatePo, Guid actingUserId)
    {
        // รอบ 257 ชุด 3 (D-H5): คู่ที่ถูกระงับแล้ว ⇒ ไม่สร้างเอกสารปลายทางในระบบของอีกฝ่าย (อนุมัติเอกสารที่ค้างในกล่องได้ แต่ไม่ต่อสาย) · เหตุบนแถว (ไม่เงียบ)
        var partnershipStatus = await _db.TradingPartnerships.AsNoTracking()
            .Where(p => p.Id == link.TradingPartnershipId).Select(p => (TradingPartnershipStatus?)p.Status).FirstOrDefaultAsync();
        if (partnershipStatus != TradingPartnershipStatus.Accepted)
        {
            if (link.LinkType is CrossTenantLinkType.QuotationFlow or CrossTenantLinkType.PoFlow)
            {
                link.Comment = "อนุมัติแล้ว แต่ไม่ได้สร้างเอกสารปลายทาง — การเชื่อมกับคู่ค้ารายนี้ถูกระงับ";
                link.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
            return;
        }
        if (link.LinkType == CrossTenantLinkType.QuotationFlow)
        {
            var shouldCreatePo = overrideAutoCreatePo ?? targetCfg.AutoCreatePoOnQuotationApproval;
            if (!shouldCreatePo) return;
            await CreateDownstreamPoAsync(link, actingUserId);
        }
        else if (link.LinkType == CrossTenantLinkType.PoFlow)
        {
            // Now check the SOURCE side's config (the original quotation
            // issuer received the PO — should they auto-create Invoice?)
            var sourceCfg = await _db.WorkflowAutomationConfigs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.CompanyId == link.TargetCompanyId);  // note: target of PO = quotation issuer
            if (sourceCfg?.AutoCreateInvoiceFromIncomingPo == true)
                await CreateDownstreamInvoiceAsync(link, actingUserId);
        }
    }

    /// <summary>ฝ่ายค้าน M5 (รอบ 257 ชุด 3): ข้อความบนแถวกล่องเข้าเมื่อสร้าง PO ปลายทางเป็นร่าง</summary>
    public const string DownstreamPoDraftComment = "PO เป็นร่าง — อนุมัติและส่งเพื่อให้ผู้ขายออกใบต่อ";

    private async Task CreateDownstreamPoAsync(CrossTenantDocumentLink approvedQuoteLink, Guid actingUserId)
    {
        // Build a PO at the BUYER side (= target of the quotation link)
        // pointing back to the supplier (= source of the quotation).
        var quote = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == approvedQuoteLink.SourceDocumentId);
        if (quote == null) return;
        // ฝ่ายค้าน LOW (รอบ 257 ชุด 3): ตัวตรวจ/ตัวคัดลอกเดียวกับใบซื้อร่าง — คัดลอกครบไม่ได้ ⇒ ไม่สร้าง + เหตุบนแถว (ไม่สร้าง PO ที่บรรทัดไม่ตรงใบเสนอราคา)
        if (PartnerCopyProblem(quote) is string copyProblem)
        {
            approvedQuoteLink.Comment = copyProblem;
            approvedQuoteLink.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return;
        }

        // The buyer needs a Contact for the supplier. Find by TaxId of the
        // supplier company. If no contact exists yet, create one.
        var supplier = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == approvedQuoteLink.SourceCompanyId);
        if (supplier == null) return;

        // รอบ 193 ทีม C3: คีย์เลขภาษี + สาขา (Helpers/ContactTaxBranchKey) — สาขาของบริษัทคู่ค้า = Company.BranchCode
        // เดิม `c.TaxId == supplier.TaxId` หยิบแถวไหนก็ได้ของเลขนั้น และเมื่อบริษัทคู่ค้าไม่มีเลขภาษี EF แปลเป็น
        // `TaxId IS NULL` ⇒ ผูกผู้ติดต่อรายแรกที่ไม่มีเลข (คนละรายสิ้นเชิง)
        var supplierContact = await FindPartnerContactAsync(
            approvedQuoteLink.TargetCompanyId, supplier, asSupplier: true);
        if (supplierContact == null)
        {
            supplierContact = NewPartnerContact(approvedQuoteLink.TargetCompanyId, supplier, asSupplier: true);
            _db.Contacts.Add(supplierContact);
            await _db.SaveChangesAsync();
        }

        var po = new Document
        {
            CompanyId = approvedQuoteLink.TargetCompanyId,
            // รอบ 257 ชุด 3 (LOW · คำตัดสิน 257-3-6): ใบที่ระบบสร้างในฝั่งผู้รับเป็น<b>ร่างเสมอ</b> (N3 · ข้อกำหนดฉบับใหม่) — เดิมประทับ Approved + เลขสุ่ม
            // "PO-yyyyMMdd-XXXXXX" ที่ไม่อยู่ในชุดเลขเอกสาร · ร่าง ⇒ เลข DRAFT- ออกเลขจริงตอนผู้ซื้ออนุมัติ (เส้นอนุมัติเดิม)
            DocumentNumber = $"DRAFT-{Guid.NewGuid()}",
            DocumentType = DocumentType.PurchaseOrder,
            Status = DocumentStatus.Draft,
            DocumentDate = DateTime.UtcNow.Date,
            ContactId = supplierContact.Id,
            Reference = $"Quotation {quote.DocumentNumber}",
            Notes = $"Auto-created from approved Quotation (cross-tenant link {approvedQuoteLink.Id})",
            CreatedBy = "cross-tenant-chain",
        };
        CopyPartnerMoney(quote, po);
        _db.Documents.Add(po);
        await _db.SaveChangesAsync();

        approvedQuoteLink.TargetDocumentId = po.Id;
        approvedQuoteLink.Status = CrossTenantLinkStatus.AutoChained;
        // ฝ่ายค้าน M5: บอกบนแถวว่า PO เป็นร่าง (ต่อท้ายความเห็นของผู้อนุมัติ — ไม่ทับ) + แจ้งทั้งสองฝ่าย
        approvedQuoteLink.Comment = string.IsNullOrWhiteSpace(approvedQuoteLink.Comment)
            ? DownstreamPoDraftComment : approvedQuoteLink.Comment + " · " + DownstreamPoDraftComment;
        approvedQuoteLink.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        if (_notify != null)
        {
            await _notify.DispatchAsync(po.CompanyId, Accounting.Models.Constants.NotificationEvents.AutopilotPartnerExchange, new Accounting.Services.Interfaces.NotificationContext
            {
                Title = $"สร้างใบสั่งซื้อร่างจากใบเสนอราคา {quote.DocumentNumber}",
                Message = DownstreamPoDraftComment, ActionUrl = "/pages/documents.html", EntityType = "Document", EntityId = po.Id,
            });
            await _notify.DispatchAsync(approvedQuoteLink.SourceCompanyId, Accounting.Models.Constants.NotificationEvents.AutopilotPartnerExchange, new Accounting.Services.Interfaces.NotificationContext
            {
                Title = $"คู่ค้าอนุมัติใบเสนอราคา {quote.DocumentNumber} แล้ว",
                Message = "ใบสั่งซื้อในระบบของคู่ค้ายังเป็นร่าง — เมื่อคู่ค้าอนุมัติและส่ง ใบสั่งซื้อจะเข้ากล่องเอกสารเข้าของท่าน", ActionUrl = "/pages/partners.html",
                EntityType = "Document", EntityId = quote.Id,
            });
        }

        // รอบ 257 ชุด 3 (คำตัดสิน 257-3-6): ไม่ส่งใบสั่งซื้อ<b>ร่าง</b>ย้อนเข้าระบบผู้ขาย และไม่ให้ผู้ขายสร้างใบแจ้งหนี้จากใบที่ผู้ซื้อยังไม่อนุมัติ —
        // เมื่อผู้ซื้ออนุมัติแล้วส่ง เส้นส่งเดิม (OnDocumentSentAsync · PurchaseOrder ⇒ PoFlow) สร้างแถวกล่องเข้าของผู้ขาย ⇒ ผู้ขายอนุมัติ ⇒
        // TryChainDownstreamAsync (PoFlow) สร้างใบแจ้งหนี้ร่างตามค่าตั้งของผู้ขายเหมือนเดิม
        _ = actingUserId;
    }

    /// <summary>ผู้ติดต่อที่แทน "บริษัทคู่ค้าในระบบเดียวกัน" ในผังผู้ติดต่อของ <paramref name="ownerCompanyId"/> —
    /// ลำดับกุญแจ: (1) แถวที่ผูกกับบริษัทนั้นแล้ว (ExternalSystem "NextAccTenant" + Id บริษัท) → (2) เลขภาษี + สาขา
    /// (Helpers/ContactTaxBranchKey ตัวเดียวกับทุกทางเข้า) → (3) ชื่อบนชุด SoftScope · เลขนี้มีแล้วแต่คนละสาขา ⇒ คืน null ให้ผู้เรียกสร้างแถว
    /// ของสาขานั้น · แถวที่จับได้และยังไม่มีเลขรับเลขของคู่ค้า (AdoptTaxId) · "-" = ไม่มีเลข (รอบ 193 ทีม C3 + ฝ่ายค้านสองรอบ)</summary>
    private async Task<Contact?> FindPartnerContactAsync(Guid ownerCompanyId, Company partner, bool asSupplier)
    {
        var partnerKey = partner.Id.ToString();
        var branch = string.IsNullOrWhiteSpace(partner.BranchCode) ? Accounting.Helpers.TaxBranchCode.HeadOffice : partner.BranchCode;
        // ฝ่ายค้าน C-8: "-" (บริษัทที่สมัครใหม่ยังไม่กรอกเลข · AuthService) = ไม่มีเลข — เดิมถูกเทียบตรงตัว
        // ⇒ บริษัทคู่ค้ารายที่สองที่ยังไม่มีเลขได้ผู้ติดต่อ "-" ของรายแรก
        var taxId = Accounting.Helpers.ContactTaxBranchKey.HasTaxId(partner.TaxId) ? partner.TaxId : null;

        // (1) กุญแจแรกเสมอ = แถวที่ผูกกับบริษัทคู่ค้านี้แล้ว (ฝ่ายค้านรอบสอง R2-C9 — เดิมไม่มีกุญแจนี้ ⇒ คู่ค้าที่กรอกเลขทีหลัง
        //     ได้ผู้ติดต่อแถวที่สอง) · ข้ามเมื่อแถวนั้นถือเลข/สาขาอื่นแล้ว (คู่ค้าเปลี่ยนสาขา ⇒ ผู้ติดต่อของสาขาใหม่ §86/4)
        var found = await _db.Contacts.FirstOrDefaultAsync(c => c.CompanyId == ownerCompanyId && !c.IsDeleted
            && c.ExternalSystem == "NextAccTenant" && c.ExternalId == partnerKey);
        if (found != null && taxId != null && Accounting.Helpers.ContactTaxBranchKey.HasTaxId(found.TaxId)
            && !Accounting.Helpers.ContactTaxBranchKey.Pick(
                new[] { new Accounting.Helpers.ContactKeyCandidate(found.Id, found.TaxId, found.BranchCode) }, taxId, branch).Found)
            found = null;
        var matchedBy = Accounting.Helpers.ContactMatchKind.ExternalKey;

        // (2) เลขภาษี + สาขา
        var key = default(Accounting.Helpers.ContactKeyMatch);
        if (found == null && taxId != null)
        {
            key = await Accounting.Helpers.ContactTaxBranchKey.FindAsync(_db.Contacts, ownerCompanyId, taxId, branch);
            if (key.ContactId is Guid id)
                found = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == ownerCompanyId && !c.IsDeleted);
            matchedBy = Accounting.Helpers.ContactMatchKind.TaxKey;
        }

        // (3) ชื่อ บนชุด SoftScope (เลขใหม่ ⇒ เฉพาะแถวที่ยังไม่มีเลข · เลขมีแล้วคนละสาขา ⇒ ห้าม) · ไม่เอาแถวที่ผูกกับบริษัทอื่น
        var softScope = Accounting.Helpers.ContactTaxBranchKey.SoftScope(_db.Contacts, ownerCompanyId, taxId, key);
        if (found == null && softScope != null && !string.IsNullOrWhiteSpace(partner.Name))
        {
            matchedBy = Accounting.Helpers.ContactMatchKind.ExactName;
            found = await softScope
                .Where(c => !c.IsDeleted && c.Name == partner.Name
                    && (asSupplier ? c.IsSupplier : c.IsCustomer)
                    && !(c.ExternalSystem == "NextAccTenant" && c.ExternalId != partnerKey))
                .OrderBy(c => c.CreatedAt)
                .FirstOrDefaultAsync();
        }

        // แถวที่ยังไม่มีเลขรับเลข + สาขาของคู่ค้า (ตัวช่วยเดียวของทุกทางเข้า — R2-C5/R2-C9) · แถวลูกค้าทั่วไปไม่รับเลข ⇒ สร้างใหม่ (B8)
        var adopt = Accounting.Helpers.ContactTaxBranchKey.AdoptTaxId(found, taxId, branch, matchedBy);
        if (adopt == Accounting.Helpers.ContactAdoptOutcome.Reject) found = null;

        if (found != null)
        {
            // ผูกกุญแจถ้ายังไม่ผูกกับระบบใด
            var dirty = adopt == Accounting.Helpers.ContactAdoptOutcome.Adopted;
            if (string.IsNullOrWhiteSpace(found.ExternalSystem) && string.IsNullOrWhiteSpace(found.ExternalId))
            {
                found.ExternalSystem = "NextAccTenant";
                found.ExternalId = partnerKey;
                dirty = true;
            }
            if (dirty) await _db.SaveChangesAsync();
        }
        return found;
    }

    /// <summary>แถวผู้ติดต่อใหม่ของบริษัทคู่ค้า — ชนิด + รหัสสาขาจากตัวตัดสินตัวเดียว (Helpers/ContactTypeResolver)
    /// เพื่อให้ใบกำกับที่ออกให้คู่ค้ารายนี้มีสาขาตาม §86/4 (เดิมไม่ตั้งสาขาเลย)</summary>
    private static Contact NewPartnerContact(Guid ownerCompanyId, Company partner, bool asSupplier)
    {
        var identity = Accounting.Helpers.ContactTypeResolver.ResolveWithBranch(
            declared: null, taxId: partner.TaxId,
            branchCode: string.IsNullOrWhiteSpace(partner.BranchCode) ? Accounting.Helpers.TaxBranchCode.HeadOffice : partner.BranchCode,
            name: partner.Name);
        return new Contact
        {
            CompanyId = ownerCompanyId,
            Name = partner.Name,
            TaxId = Accounting.Helpers.ContactTaxBranchKey.HasTaxId(partner.TaxId) ? partner.TaxId : null,   // "-" ไม่ใช่เลข (C-8)
            BranchCode = identity.BranchCode,
            ContactType = identity.Type,
            Address = partner.Address,
            IsSupplier = asSupplier,
            IsCustomer = !asSupplier,
            // ผูกกลับไปยังบริษัทคู่ค้า — กุญแจแรกของ FindPartnerContactAsync (R2-C9 · ชื่อระบบเดียวกับใบค่าบริการแพลตฟอร์ม)
            ExternalSystem = "NextAccTenant",
            ExternalId = partner.Id.ToString(),
            CreatedBy = "cross-tenant-routing",
        };
    }

    private async Task CreateDownstreamInvoiceAsync(CrossTenantDocumentLink poLink, Guid actingUserId)
    {
        var po = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == poLink.SourceDocumentId);
        if (po == null) return;
        // ฝ่ายค้าน LOW (รอบ 257 ชุด 3): ตัวตรวจ/ตัวคัดลอกเดียวกับใบซื้อร่าง
        if (PartnerCopyProblem(po) is string copyProblem)
        {
            poLink.Comment = copyProblem;
            poLink.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return;
        }

        // The supplier (target of the PO) creates an Invoice back to the buyer
        var buyer = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == poLink.SourceCompanyId);
        if (buyer == null) return;

        // รอบ 193 ทีม C3: คีย์เลขภาษี + สาขา — ใบกำกับที่ออกให้ผู้ซื้อต้องเป็นสาขาของผู้ซื้อจริง (§86/4)
        var buyerContact = await FindPartnerContactAsync(
            poLink.TargetCompanyId, buyer, asSupplier: false);
        if (buyerContact == null)
        {
            buyerContact = NewPartnerContact(poLink.TargetCompanyId, buyer, asSupplier: false);
            _db.Contacts.Add(buyerContact);
            await _db.SaveChangesAsync();
        }

        var inv = new Document
        {
            CompanyId = poLink.TargetCompanyId,
            // ⚠️ ต้องเป็น DRAFT- placeholder — เดิม hardcode "INV-{วันที่}-{GUID}"
            // ให้เอกสารชนิด **TaxInvoice** ⇒ ใบกำกับถือเลขสุ่มที่ไม่เรียงลำดับ
            // ไม่อยู่เล่ม TIV และเพราะเลขไม่ใช่ DRAFT- ตอนอนุมัติก็ไม่ออกเลขใหม่
            // ให้ (ApproveDocumentAsync ออกเลขเฉพาะใบที่ยังเป็น DRAFT-) = ติดถาวร
            // ผิด §86/4 (เลขใบกำกับต้องเรียงต่อเนื่องไม่ขาดช่วง). ใช้ DRAFT- แล้ว
            // เลขจริงจะออกจาก DocumentNumberGenerator ตอน AR อนุมัติ เหมือนใบอื่น
            DocumentNumber = $"DRAFT-{Guid.NewGuid()}",
            DocumentType = DocumentType.TaxInvoice,
            Status = DocumentStatus.Draft,   // Invoice is Draft so AR team reviews before sending
            DocumentDate = DateTime.UtcNow.Date,
            ContactId = buyerContact.Id,
            Reference = $"PO {po.DocumentNumber}",
            Notes = $"Auto-created from incoming PO (cross-tenant link {poLink.Id})",
            CreatedBy = "cross-tenant-chain",
        };
        CopyPartnerMoney(po, inv);
        _db.Documents.Add(inv);
        await _db.SaveChangesAsync();

        poLink.TargetDocumentId = inv.Id;
        poLink.Status = CrossTenantLinkStatus.AutoChained;

        // Forward routing link — Invoice to buyer
        _db.CrossTenantDocumentLinks.Add(new CrossTenantDocumentLink
        {
            TradingPartnershipId = poLink.TradingPartnershipId,
            SourceCompanyId = inv.CompanyId,
            SourceDocumentId = inv.Id,
            TargetCompanyId = poLink.SourceCompanyId,
            LinkType = CrossTenantLinkType.InvoiceFlow,
            Status = CrossTenantLinkStatus.PendingApproval,
            CreatedBy = "cross-tenant-chain",
        });
        await _db.SaveChangesAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    // CONFIG
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>ฝ่ายค้าน L2: อ่านค่าตั้งโดยไม่เขียนแถว (GET ห้ามสร้างแถว) — ไม่มี = ค่าตั้งต้นที่ยังไม่บันทึก · tenant = <paramref name="companyId"/></summary>
    public async Task<WorkflowAutomationConfig> GetConfigReadOnlyAsync(Guid companyId)
        => await _db.WorkflowAutomationConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == companyId)
           ?? new WorkflowAutomationConfig { CompanyId = companyId };

    public async Task<WorkflowAutomationConfig> GetOrCreateConfigAsync(Guid companyId)
    {
        var cfg = await _db.WorkflowAutomationConfigs
            .FirstOrDefaultAsync(c => c.CompanyId == companyId);
        if (cfg != null) return cfg;
        cfg = new WorkflowAutomationConfig { CompanyId = companyId, CreatedBy = "system" };
        _db.WorkflowAutomationConfigs.Add(cfg);
        await _db.SaveChangesAsync();
        return cfg;
    }

    /// <summary>ID#10 — คำเตือนบนหน้าตั้งค่า/คำตอบการอนุมัติ: ผู้อนุมัติเริ่มต้นไม่ได้เป็นสมาชิกบริษัทแล้ว · null = ปกติ/ไม่ได้ตั้ง</summary>
    public async Task<string?> GetDefaultApproverNoticeAsync(WorkflowAutomationConfig cfg)
        => cfg.DefaultApproverUserId is { } approver && approver != Guid.Empty
           && !await Accounting.Helpers.CompanyMembership.IsActiveMemberAsync(_db, cfg.CompanyId, approver)
            ? Accounting.Helpers.CrossTenantSignerResolver.DepartedDefaultApproverNotice
            : null;

    /// <summary>ID#10 — ตั้งผู้อนุมัติเริ่มต้น/ลายเซ็นเริ่มต้น (ยังไม่บันทึก — ผู้เรียก SaveChanges) · ส่ง <see cref="Guid.Empty"/> = ล้างค่า ·
    /// ผู้อนุมัติต้องเป็นสมาชิกที่ใช้งานอยู่ของบริษัทนี้ · ลายเซ็นต้องเป็นของผู้อนุมัติเริ่มต้นคนนั้น · เปลี่ยนผู้อนุมัติแล้วลายเซ็นเดิมที่ไม่ใช่ของคนใหม่ถูกล้าง</summary>
    public async Task ApplyDefaultSignerAsync(WorkflowAutomationConfig cfg, Guid? defaultApproverUserId, Guid? defaultSignatureId)
    {
        var effectiveApprover = defaultApproverUserId.HasValue
            ? (defaultApproverUserId.Value == Guid.Empty ? (Guid?)null : defaultApproverUserId.Value)
            : cfg.DefaultApproverUserId;
        var approverIsMember = effectiveApprover is { } ea
            && await Accounting.Helpers.CompanyMembership.IsActiveMemberAsync(_db, cfg.CompanyId, ea);
        var sig = defaultSignatureId is { } sid && sid != Guid.Empty
            ? await _db.UserSignatures.AsNoTracking()
                .Where(s => s.Id == sid && !s.IsDeleted && s.IsActive)
                .Select(s => new { s.UserId }).FirstOrDefaultAsync()
            : null;
        var problem = Accounting.Helpers.CrossTenantSignerResolver.ConfigProblem(
            defaultApproverUserId.HasValue ? effectiveApprover : null, approverIsMember,
            defaultSignatureId, sig != null, sig?.UserId, effectiveApprover);
        if (problem != null)
            throw new Accounting.Helpers.BusinessRuleException(problem, Accounting.Helpers.CrossTenantSignerResolver.RuleCode, 400);

        if (defaultApproverUserId.HasValue) cfg.DefaultApproverUserId = effectiveApprover;
        if (defaultSignatureId.HasValue)
            cfg.DefaultSignatureId = defaultSignatureId.Value == Guid.Empty ? null : defaultSignatureId.Value;
        else if (defaultApproverUserId.HasValue && cfg.DefaultSignatureId is { } keptSig)
        {
            // เปลี่ยนผู้อนุมัติโดยไม่ส่งลายเซ็นมา ⇒ ลายเซ็นเดิมที่ไม่ใช่ของคนใหม่ต้องไม่ติดไปประทับในชื่อคนใหม่
            var owner = await _db.UserSignatures.AsNoTracking().Where(s => s.Id == keptSig)
                .Select(s => (Guid?)s.UserId).FirstOrDefaultAsync();
            if (effectiveApprover is not { } newApprover
                || !Accounting.Helpers.CrossTenantSignerResolver.SignatureBelongsTo(owner, newApprover))
                cfg.DefaultSignatureId = null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static (Guid A, Guid B) OrderPair(Guid x, Guid y)
        => x.CompareTo(y) < 0 ? (x, y) : (y, x);
}

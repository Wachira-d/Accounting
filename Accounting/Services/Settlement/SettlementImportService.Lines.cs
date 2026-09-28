using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.DTOs.Settlement;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Settlement;

/// <summary>บรรทัดของรอบโอน: จับคู่ใบขาย (อ่านอย่างเดียว) · ผู้ใช้จัดประเภท/จับคู่เอง · ยกเลิกรอบโอน · มุมมอง</summary>
public sealed partial class SettlementImportService
{
    /// <summary>ชนิดเอกสารขายที่ใช้เป็นผู้สมัครจับคู่ (มีเลขออเดอร์ใน <c>Document.Reference</c>) — ใบเสร็จอยู่ในชุดเพื่อ "เห็น" รายได้ที่บันทึกแล้ว
    /// (รับชำระจากผังพักไม่ได้ ⇒ ตัวตัดสินให้คนเลือก ไม่ตกใบขายสรุปซ้ำ)</summary>
    private static readonly DocumentType[] SaleDocumentTypes = { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt };

    private static readonly DocumentType[] RefundTargetTypes = { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt };

    // ═══════════════════ จับคู่ ═══════════════════

    /// <summary>จับคู่บรรทัดใน <paramref name="toMatch"/> ด้วยเลขออเดอร์ตรงตัว (tenant) — <paramref name="apply"/> = false ⇒ แค่คำนวณผู้สมัคร/เหตุผลให้หน้าจอ ·
    /// ยอดของออเดอร์นับจากทุกบรรทัดของรอบโอน (<paramref name="allLines"/>) · ตัวตัดสิน = <see cref="SettlementSaleMatch.Decide"/> ตัวเดียว</summary>
    private async Task<Dictionary<Guid, SettlementMatchDecision>> MatchLinesAsync(Guid companyId, SettlementChannel channel,
        IReadOnlyList<SettlementLine> allLines, IReadOnlyCollection<SettlementLine> toMatch, bool apply, CancellationToken ct)
    {
        var decisions = new Dictionary<Guid, SettlementMatchDecision>();
        var orderIds = toMatch
            .Where(l => SettlementLineTypeRules.For(l.LineType).RequiresSaleMatch && !string.IsNullOrWhiteSpace(l.ExternalOrderId))
            .Select(l => l.ExternalOrderId!.Trim()).Distinct(StringComparer.Ordinal).ToList();

        var byOrder = new Dictionary<string, List<SettlementMatchCandidate>>(StringComparer.Ordinal);
        void Add(string? key, SettlementMatchCandidate c)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!byOrder.TryGetValue(key.Trim(), out var l)) byOrder[key.Trim()] = l = new List<SettlementMatchCandidate>();
            l.Add(c);
        }

        foreach (var chunk in orderIds.Chunk(500))
        {
            var ids = chunk.ToList();
            var docs = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && d.Reference != null && ids.Contains(d.Reference)
                    && SaleDocumentTypes.Contains(d.DocumentType) && d.Status != DocumentStatus.Voided)
                .Select(d => new { d.Id, d.Reference, d.DocumentType, d.Status, d.DocumentNumber, d.BalanceDue })
                .ToListAsync(ct);
            foreach (var d in docs)
            {
                var issued = DocumentStatusRules.IsEffective(d.Status);
                Add(d.Reference, new SettlementMatchCandidate(SettlementMatchCandidateKind.Document, d.Id,
                    $"{d.DocumentType} {d.DocumentNumber}" + (issued ? $" (ค้าง {d.BalanceDue:N2})" : " (ยังไม่ออกเอกสาร)"),
                    d.BalanceDue,
                    CanReceive: issued && ArApScope.IsReceivable(d.DocumentType) && d.BalanceDue > 0m,
                    IsRefundTarget: issued && RefundTargetTypes.Contains(d.DocumentType)));
            }

            var reservations = await _db.LodgingReservations.AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.SourceReference != null && ids.Contains(r.SourceReference))
                .Select(r => new { r.Id, r.SourceReference, r.ReservationNumber })
                .ToListAsync(ct);
            foreach (var r in reservations)
                Add(r.SourceReference, new SettlementMatchCandidate(SettlementMatchCandidateKind.Reservation, r.Id,
                    $"การจอง {r.ReservationNumber}", null, false, false));
        }

        // intent ของ gateway ในระบบ — เฉพาะช่องทาง gateway ที่ผังพักตรงกับขาเงินเข้าของ intent (R-A1) · ไม่งั้น "อยู่ในผังพักแล้ว" เป็นเท็จ
        if (orderIds.Count > 0 && channel.Kind == SettlementChannelKind.Gateway && channel.PaymentProviderConfigId is Guid cfgId)
        {
            var providerCode = await _db.PaymentProviderConfigs.AsNoTracking()
                .Where(c => c.Id == cfgId && c.CompanyId == companyId).Select(c => c.ProviderCode).FirstOrDefaultAsync(ct);
            if (providerCode != null && await GatewayClearingMatchesAsync(companyId, channel, providerCode, ct))
            {
                var batchId = allLines.Count > 0 ? allLines[0].BatchId : Guid.Empty;
                foreach (var chunk in orderIds.Chunk(500))
                {
                    var ids = chunk.ToList();
                    var intents = await _db.PaymentIntents.AsNoTracking()
                        .Where(i => i.CompanyId == companyId && i.ProviderCode == providerCode && i.ProviderRef != null
                            && ids.Contains(i.ProviderRef))
                        .Select(i => new { i.Id, i.ProviderRef, i.Status, i.ConfirmedAt, i.Amount, i.RefundedAmount,
                            i.SettlementJournalEntryId, i.SettlementBatchId })
                        .ToListAsync(ct);
                    foreach (var i in intents)
                    {
                        // ถูกบันทึกรอบโอนด้วยเส้นเดิม หรืออยู่ในรอบโอนอื่นแล้ว ⇒ ผังพักถูกล้างไปแล้ว — "อยู่ในผังพัก" ไม่จริงอีกต่อไป
                        var free = i.SettlementJournalEntryId == null && (i.SettlementBatchId == null || i.SettlementBatchId == batchId);
                        var received = i.ConfirmedAt != null && (i.Status == PaymentIntentStatus.Succeeded
                            || i.Status == PaymentIntentStatus.PartiallyRefunded || i.Status == PaymentIntentStatus.Refunded);
                        Add(i.ProviderRef, new SettlementMatchCandidate(SettlementMatchCandidateKind.PaymentIntent, i.Id,
                            $"รายการรับชำระ {i.ProviderRef} ({i.Amount:N2})", i.Amount - i.RefundedAmount,
                            CanReceive: free && received, IsRefundTarget: free && i.RefundedAmount > 0m));
                    }
                }
            }
        }

        var orderGroup = allLines
            .Where(l => SettlementLineTypeRules.For(l.LineType).Posting == SettlementPostingKind.SaleComponent
                && !string.IsNullOrWhiteSpace(l.ExternalOrderId))
            .GroupBy(l => l.ExternalOrderId!.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount), StringComparer.Ordinal);

        foreach (var l in toMatch)
        {
            var key = l.ExternalOrderId?.Trim();
            var cands = key != null && byOrder.TryGetValue(key, out var c) ? c : new List<SettlementMatchCandidate>();
            var fromAdapter = IsIntentSourced(l) ? l.PaymentIntentId : null;
            var d = SettlementSaleMatch.Decide(l.LineType, key, key != null ? orderGroup.GetValueOrDefault(key) : l.Amount, cands, fromAdapter);
            decisions[l.Id] = d;
            if (!apply) continue;
            l.MatchStatus = d.Status;
            l.MatchedDocumentId = d.DocumentId;
            l.ReservationId = d.ReservationId;
            l.PaymentIntentId = fromAdapter ?? d.PaymentIntentId;
        }
        return decisions;
    }

    /// <summary>บรรทัดที่ adapter ประกอบจาก PaymentIntent (คีย์ขึ้นต้น "pi:") — PaymentIntentId เป็นที่มา ไม่ใช่ผลการจับคู่</summary>
    private static bool IsIntentSourced(SettlementLine l)
        => l.PaymentIntentId != null && l.ExternalTxnId != null && l.ExternalTxnId.StartsWith("pi:", StringComparison.Ordinal);

    /// <summary>ประทับ <c>PaymentIntent.SettlementBatchId</c> ให้ตรงกับบรรทัดของรอบโอน — intent ที่บรรทัดอ้างถึงและยังไม่อยู่รอบใด ⇒ ประทับรอบนี้ ·
    /// intent ที่ประทับรอบนี้แต่ไม่มีบรรทัดอ้างแล้ว ⇒ ปลด (กันเส้นเดิม/รอบอื่นหยิบซ้ำ · กันค้างหลังผู้ใช้เปลี่ยนการจับคู่) ·
    /// intent ที่อยู่รอบอื่นแล้ว (บรรทัด "คืนเงินภายหลัง" ของรอบถัดไป) ⇒ คงรอบเดิม — รอบแรกเป็นเจ้าของยอดขาย</summary>
    private async Task SyncIntentStampsAsync(Guid companyId, Guid batchId, IReadOnlyList<SettlementLine> lines, CancellationToken ct)
    {
        var referenced = lines.Where(l => !l.IsDeleted && l.PaymentIntentId != null).Select(l => l.PaymentIntentId!.Value)
            .Distinct().ToList();
        var stamped = await _db.PaymentIntents
            .Where(i => i.CompanyId == companyId && (i.SettlementBatchId == batchId || referenced.Contains(i.Id)))
            .ToListAsync(ct);
        foreach (var i in stamped)
        {
            var want = referenced.Contains(i.Id) ? batchId : (Guid?)null;
            if (i.SettlementBatchId == want) continue;
            if (want != null && i.SettlementBatchId != null) continue;          // เจ้าของคือรอบแรก (คืนเงินภายหลัง)
            i.SettlementBatchId = want;
            i.UpdatedAt = DateTime.UtcNow;
        }
    }

    // ═══════════════════ มุมมอง ═══════════════════

    public async Task<SettlementBatchView> GetBatchAsync(Guid companyId, Guid batchId, CancellationToken ct = default)
    {
        var batch = await _db.SettlementBatches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == batchId && b.CompanyId == companyId, ct)
            ?? throw new BusinessRuleException("ไม่พบรอบโอนนี้ในบริษัท", "SETTLEMENT-BATCH", 404);
        var channel = await LoadChannelAsync(companyId, batch.ChannelId, tracked: false, ct);
        var lines = await _db.SettlementLines.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.BatchId == batch.Id).OrderBy(l => l.Seq).ToListAsync(ct);
        return await BuildBatchViewAsync(companyId, channel, batch, lines, ct);
    }

    public async Task<IReadOnlyList<SettlementBatchView>> ListBatchesAsync(Guid companyId, Guid? channelId,
        SettlementBatchStatus? status, int skip = 0, int take = 50, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        var q = _db.SettlementBatches.AsNoTracking().Where(b => b.CompanyId == companyId);
        if (channelId is Guid cid) q = q.Where(b => b.ChannelId == cid);
        if (status is SettlementBatchStatus st) q = q.Where(b => b.Status == st);
        var batches = await q.OrderByDescending(b => b.PayoutDate).ThenByDescending(b => b.CreatedAt)
            .Skip(Math.Max(0, skip)).Take(take)
            .Select(b => new { Batch = b, ChannelName = b.Channel.DisplayName })
            .ToListAsync(ct);
        var ids = batches.Select(b => b.Batch.Id).ToList();
        var stats = await _db.SettlementLines.AsNoTracking()
            .Where(l => l.CompanyId == companyId && ids.Contains(l.BatchId))
            .GroupBy(l => l.BatchId)
            .Select(g => new
            {
                BatchId = g.Key,
                Total = g.Sum(x => x.Amount),
                Count = g.Count(),
                Unclassified = g.Count(x => x.LineType == SettlementLineType.Unclassified),
                Needs = g.Count(x => x.LineType != SettlementLineType.Unclassified
                    && (x.MatchStatus == SettlementMatchStatus.Unmatched || x.MatchStatus == SettlementMatchStatus.AmountMismatch)),
                Auto = g.Count(x => x.MatchStatus == SettlementMatchStatus.AutoSummary),
                Ai = g.Count(x => x.ClassifyUsedAi),
            })
            .ToListAsync(ct);
        var byId = stats.ToDictionary(s => s.BatchId);
        return batches.Select(x =>
        {
            var s = byId.GetValueOrDefault(x.Batch.Id);
            return ToBatchView(x.Batch, x.ChannelName, s?.Total ?? 0m, s?.Count ?? 0, s?.Unclassified ?? 0, s?.Needs ?? 0,
                s?.Auto ?? 0, (s?.Ai ?? 0) > 0, Array.Empty<SettlementLineView>());
        }).ToList();
    }

    private async Task<SettlementBatchView> BuildBatchViewAsync(Guid companyId, SettlementChannel channel, SettlementBatch batch,
        IReadOnlyList<SettlementLine> lines, CancellationToken ct)
    {
        // ผู้สมัคร/เหตุผลของบรรทัดที่รอคนตัดสิน — คำนวณสด (อ่านอย่างเดียว) ไม่เก็บลงฐาน
        var pending = lines.Where(l => SettlementSaleMatch.NeedsDecision(l.MatchStatus)
            && SettlementLineTypeRules.For(l.LineType).RequiresSaleMatch).ToList();
        var decisions = pending.Count == 0
            ? new Dictionary<Guid, SettlementMatchDecision>()
            : await MatchLinesAsync(companyId, channel, lines, pending, apply: false, ct);
        var views = lines.OrderBy(l => l.Seq).Select(l => ToLineView(l, decisions.GetValueOrDefault(l.Id))).ToList();
        return ToBatchView(batch, channel.DisplayName, lines.Sum(l => l.Amount), lines.Count,
            lines.Count(l => l.LineType == SettlementLineType.Unclassified),
            lines.Count(l => l.LineType != SettlementLineType.Unclassified && SettlementSaleMatch.NeedsDecision(l.MatchStatus)),
            lines.Count(l => l.MatchStatus == SettlementMatchStatus.AutoSummary),
            lines.Any(l => l.ClassifyUsedAi), views);
    }

    private static SettlementBatchView ToBatchView(SettlementBatch b, string channelName, decimal total, int count, int unclassified,
        int needs, int auto, bool anyAi, IReadOnlyList<SettlementLineView> lines)
        => new(b.Id, b.ChannelId, channelName, b.PayoutRef, b.PayoutDate, b.PeriodFrom, b.PeriodTo, b.Currency, b.NetPayout,
            b.OpeningWalletBalance, b.ClosingWalletBalance, b.Status, b.SourceKind, b.SourceFileAttachmentId, b.BankAccountId,
            total, count, unclassified, needs, auto, anyAi, b.Note, b.CreatedAt, lines);

    private static SettlementLineView ToLineView(SettlementLine l, SettlementMatchDecision? d)
        => new(l.Id, l.Seq, l.LineType, SettlementLineTypeRules.For(l.LineType).LabelTh, l.RawTypeLabel, l.Description, l.TxnDate,
            l.ExternalOrderId, l.ExternalTxnId, l.Amount, l.VatAmount, l.WhtAmount, l.ClassifiedBy, l.ClassifyUsedAi,
            l.ClassifyAiFeedbackId, l.MatchStatus, l.MatchedDocumentId, l.PaymentIntentId, l.ReservationId, d?.Note,
            d == null
                ? Array.Empty<SettlementMatchCandidateView>()
                : d.Candidates.Select(c => new SettlementMatchCandidateView(c.Kind.ToString(), c.Id, c.Label, c.OpenAmount,
                    c.CanReceive, c.IsRefundTarget)).ToList(),
            l.OverrideAccountId, l.AdjustmentReason);

    // ═══════════════════ ผู้ใช้จัดประเภท (ปิดลูปการเรียนรู้) ═══════════════════

    public async Task<SettlementLineView> ReclassifyLineAsync(Guid companyId, Guid userId, Guid lineId,
        SettlementReclassifyRequest request, CancellationToken ct = default)
    {
        var type = request.LineType;
        var rule = SettlementLineTypeRules.For(type);
        if (!Enum.IsDefined(type) || !rule.Postable)
            throw new BusinessRuleException("เลือกประเภทรายการที่ลงบัญชีได้ (\"รอจัดประเภท\" ไม่ใช่คำตอบ)", "SETTLEMENT-TYPE");

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var line = await _db.SettlementLines.FirstOrDefaultAsync(l => l.Id == lineId && l.CompanyId == companyId, ct)
                       ?? throw new BusinessRuleException("ไม่พบบรรทัดนี้ในบริษัท", "SETTLEMENT-LINE", 404);
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.SettlementImport, line.ChannelId.ToString("N")) }, ct);
            var batch = await LoadEditableBatchAsync(companyId, line.BatchId, ct);

            // ด่านเครื่องหมาย — ตัวเดียวกับตัวคิดแผน (SettlementLineTypeRules.SignAllowed ผ่าน Fits)
            if (!SettlementLineClassification.Fits(type, line.Amount))
                throw new BusinessRuleException(
                    $"บรรทัดที่ {line.Seq} ยอด {line.Amount:N2} ใช้ประเภท \"{rule.LabelTh}\" ไม่ได้ (เครื่องหมายยอดขัดกับประเภท) — "
                    + "ตรวจว่าเลือกประเภทถูก หรือการจับคู่คอลัมน์กลับเครื่องหมายผิด", "SETTLEMENT-SIGN");
            Guid? overrideAccount = null;
            string? reason = null;
            if (request.OverrideAccountId is Guid acc)
            {
                if (!await _db.ChartOfAccounts.AsNoTracking().AnyAsync(a => a.Id == acc && a.CompanyId == companyId && a.IsActive, ct))
                    throw new BusinessRuleException("ไม่พบผังบัญชีที่เลือก (หรือถูกปิดใช้)", "SETTLEMENT-ACCOUNT", 404);
                overrideAccount = acc;
            }
            if (rule.RequiresReason)
            {
                reason = request.AdjustmentReason?.Trim();
                if (string.IsNullOrEmpty(reason))
                    throw new BusinessRuleException("รายการปรับปรุงต้องระบุเหตุผล", "SETTLEMENT-ADJUSTMENT-REASON");
                if (reason.Length > 500) reason = reason[..500];
                if (overrideAccount == null)
                    throw new BusinessRuleException("รายการปรับปรุงต้องเลือกผังบัญชีที่ลง", "SETTLEMENT-ADJUSTMENT-ACCOUNT");
            }

            // "รับคำตอบของโมเดล" มีความหมายเฉพาะบรรทัดที่ครู/นักเรียนเคยตอบจริง (มีแถว feedback) — คลังต่อช่องทาง/seed ไม่ใช่คำตอบ AI
            var acceptedAi = line.ClassifyAiFeedbackId != null
                && line.ClassifiedBy is SettlementClassifiedBy.Ai or SettlementClassifiedBy.Learned
                && line.LineType == type;
            Apply(line, type, SettlementClassifiedBy.User, overrideAccount, reason);
            var changed = new List<SettlementLine> { line };

            // ป้ายเดียวกัน+เครื่องหมายเดียวกันในรอบนี้ที่ผู้ใช้ยังไม่ได้เลือกเอง ⇒ ใช้คำตอบเดียวกัน (Learned — ไม่นับเป็นเสียงของผู้ใช้ซ้ำ)
            var batchLines = await _db.SettlementLines.Where(l => l.CompanyId == companyId && l.BatchId == batch.Id).ToListAsync(ct);
            var norm = SettlementLineClassification.NormalizeLabel(line.RawTypeLabel);
            if (request.ApplyToSameLabel && norm.Length > 0 && !rule.RequiresReason)
                foreach (var other in batchLines.Where(o => o.Id != line.Id && o.ClassifiedBy != SettlementClassifiedBy.User
                             && (o.Amount > 0m) == (line.Amount > 0m)
                             && SettlementLineClassification.NormalizeLabel(o.RawTypeLabel) == norm
                             && SettlementLineClassification.Fits(type, o.Amount)).ToList())
                {
                    Apply(other, type, SettlementClassifiedBy.Learned, other.OverrideAccountId, null);
                    changed.Add(other);
                }

            var channel = await LoadChannelAsync(companyId, line.ChannelId, tracked: false, ct);
            await RematchChangedAsync(companyId, channel, batchLines, changed, ct);
            await SyncIntentStampsAsync(companyId, batch.Id, batchLines, ct);
            batch.Status = SettlementSaleMatch.DeriveImportStatus(batchLines.Select(l => (l.LineType, l.MatchStatus)));
            batch.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            // ── ปิดลูป (กฎเหล็ก #1): คำตอบของผู้ใช้ = label แบบตั้งใจ · อยู่ธุรกรรมเดียวกับการกระทำ (DOCTRINE §3) ──
            var feedbackId = line.ClassifyAiFeedbackId;
            if (feedbackId == null)
            {
                // บรรทัดที่ชั้น local จัดให้ (ไม่เคยถามครู) — สร้างแถว feedback ที่ไม่มีคำตอบ AI เพื่อให้นักเรียนเรียนป้ายนี้ได้
                var payload = SettlementLineClassification.BuildPromptPayload(norm, channel.Kind, line.Amount);
                feedbackId = await _recorder.RecordCallAsync(new AiFeedbackRecord(
                    companyId, AiFeatureKey.SettlementLineClassify, AiMemoryKey.Of(payload), payload, null,
                    AiPrimaryAnswer: null, AiConfidence: null, LocalModelAnswer: null, LocalModelConfidence: null,
                    LocalModelVersion: null, SourceEntityType: nameof(SettlementLine), SourceEntityId: line.Id,
                    Status: AiCallStatus.Skipped, ProviderUsed: AiProviderType.None, ModelVersion: null, LatencyMs: 0,
                    InputTokens: 0, OutputTokens: 0, CostUsd: 0m, CacheHitOfFeedbackId: null,
                    ErrorMessage: "ผู้ใช้จัดประเภทบรรทัด settlement เอง (ไม่ได้ถาม AI)"), ct);
                line.ClassifyAiFeedbackId = feedbackId;
                await _db.SaveChangesAsync(ct);
            }
            await _recorder.RecordUserChoiceAsync(feedbackId.Value, type.ToString(), acceptedAi, ct, UserChoiceSource.Explicit);
            await tx.CommitAsync(ct);

            var decisions = SettlementSaleMatch.NeedsDecision(line.MatchStatus)
                ? await MatchLinesAsync(companyId, channel, batchLines, new[] { line }, apply: false, ct)
                : new Dictionary<Guid, SettlementMatchDecision>();
            return ToLineView(line, decisions.GetValueOrDefault(line.Id));
        });
    }

    private static void Apply(SettlementLine l, SettlementLineType type, SettlementClassifiedBy by, Guid? overrideAccount, string? reason)
    {
        l.LineType = type;
        l.ClassifiedBy = by;
        l.OverrideAccountId = overrideAccount;
        l.AdjustmentReason = reason;
        l.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>หลังเปลี่ยนประเภท: ประเภทที่ไม่ต้องจับคู่ ⇒ NotRequired (ถอดการจับคู่ที่ไม่ใช่ที่มาจาก adapter) · ต้องจับคู่ ⇒ จับคู่ใหม่</summary>
    private async Task RematchChangedAsync(Guid companyId, SettlementChannel channel, IReadOnlyList<SettlementLine> all,
        IReadOnlyList<SettlementLine> changed, CancellationToken ct)
    {
        var needMatch = new List<SettlementLine>();
        foreach (var l in changed)
        {
            if (SettlementLineTypeRules.For(l.LineType).RequiresSaleMatch) { needMatch.Add(l); continue; }
            l.MatchStatus = SettlementMatchStatus.NotRequired;
            l.MatchedDocumentId = null;
            l.ReservationId = null;
            if (!IsIntentSourced(l)) l.PaymentIntentId = null;
        }
        if (needMatch.Count > 0) await MatchLinesAsync(companyId, channel, all, needMatch, apply: true, ct);
    }

    // ═══════════════════ ผู้ใช้ตัดสินการจับคู่ ═══════════════════

    public async Task<SettlementLineView> AssignLineMatchAsync(Guid companyId, Guid lineId, SettlementAssignMatchRequest request,
        CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var line = await _db.SettlementLines.FirstOrDefaultAsync(l => l.Id == lineId && l.CompanyId == companyId, ct)
                       ?? throw new BusinessRuleException("ไม่พบบรรทัดนี้ในบริษัท", "SETTLEMENT-LINE", 404);
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.SettlementImport, line.ChannelId.ToString("N")) }, ct);
            var batch = await LoadEditableBatchAsync(companyId, line.BatchId, ct);
            var rule = SettlementLineTypeRules.For(line.LineType);
            if (!rule.RequiresSaleMatch)
                throw new BusinessRuleException($"บรรทัดประเภท \"{rule.LabelTh}\" ไม่ต้องจับคู่ใบขาย", "SETTLEMENT-MATCH-NOT-REQUIRED");
            if (IsIntentSourced(line))
                throw new BusinessRuleException("บรรทัดนี้มาจากรายการรับชำระในระบบ (อยู่ในผังพักแล้ว) — ไม่ต้องจับคู่ใบขายซ้ำ",
                    "SETTLEMENT-MATCH-INTENT");
            var isRefund = rule.Posting == SettlementPostingKind.Refund;
            var batchLines = await _db.SettlementLines.Where(l => l.CompanyId == companyId && l.BatchId == batch.Id).ToListAsync(ct);
            // บรรทัดขายของออเดอร์เดียวกันต้องไปใบเดียวกัน (ตัวคิดแผนรวมรับชำระต่อใบ) · คืนเงินตัดสินทีละบรรทัด
            var group = isRefund || string.IsNullOrWhiteSpace(line.ExternalOrderId)
                ? new List<SettlementLine> { line }
                : batchLines.Where(l => l.ExternalOrderId?.Trim() == line.ExternalOrderId!.Trim()
                    && SettlementLineTypeRules.For(l.LineType).Posting == SettlementPostingKind.SaleComponent
                    && !IsIntentSourced(l)).ToList();

            if (request.UseDailySummary)
            {
                if (isRefund)
                    throw new BusinessRuleException("คืนเงินต้องอ้างใบขายเดิม (§86/10) — เลือกใบขายของออเดอร์นี้", "SETTLEMENT-REFUND-NEEDS-DOC");
                foreach (var l in group) SetMatch(l, SettlementMatchStatus.AutoSummary, null);
            }
            else
            {
                if (request.DocumentId is not Guid docId)
                    throw new BusinessRuleException("เลือกเอกสารขายที่จะจับคู่ หรือยืนยันให้เข้าใบขายสรุปรายวัน", "SETTLEMENT-MATCH-DOC");
                var doc = await _db.Documents.AsNoTracking()
                    .Where(d => d.Id == docId && d.CompanyId == companyId && SaleDocumentTypes.Contains(d.DocumentType))
                    .Select(d => new { d.DocumentType, d.Status, d.BalanceDue, d.DocumentNumber })
                    .FirstOrDefaultAsync(ct)
                    ?? throw new BusinessRuleException("ไม่พบเอกสารขายนี้ในบริษัท", "SETTLEMENT-MATCH-DOC", 404);
                var issued = DocumentStatusRules.IsEffective(doc.Status);
                if (isRefund && !(issued && RefundTargetTypes.Contains(doc.DocumentType)))
                    throw new BusinessRuleException($"เอกสาร {doc.DocumentNumber} ใช้เป็นใบเดิมของใบลดหนี้ไม่ได้ (ต้องออกแล้วและไม่ถูกยกเลิก)",
                        "SETTLEMENT-MATCH-DOC");
                if (!isRefund && !(issued && ArApScope.IsReceivable(doc.DocumentType) && doc.BalanceDue > 0m))
                    throw new BusinessRuleException(
                        $"เอกสาร {doc.DocumentNumber} รับชำระจากผังพักไม่ได้ (ต้องเป็นใบแจ้งหนี้/ใบกำกับที่ออกแล้วและยังมียอดค้าง) — "
                        + "ถ้ารายได้ของออเดอร์นี้บันทึกด้วยใบเสร็จแล้ว ให้บันทึกบรรทัดนี้เป็นรายการปรับปรุงแทน (กันรายได้ซ้ำ)",
                        "SETTLEMENT-MATCH-DOC");
                foreach (var l in group) SetMatch(l, SettlementMatchStatus.Matched, docId);
            }
            await SyncIntentStampsAsync(companyId, batch.Id, batchLines, ct);
            batch.Status = SettlementSaleMatch.DeriveImportStatus(batchLines.Select(l => (l.LineType, l.MatchStatus)));
            batch.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToLineView(line, null);
        });
    }

    private static void SetMatch(SettlementLine l, SettlementMatchStatus status, Guid? documentId)
    {
        l.MatchStatus = status;
        l.MatchedDocumentId = documentId;
        l.ReservationId = null;
        l.PaymentIntentId = null;
        l.UpdatedAt = DateTime.UtcNow;
    }

    public async Task<SettlementBatchView> RematchBatchAsync(Guid companyId, Guid batchId, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var batch = await LoadEditableBatchAsync(companyId, batchId, ct);
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.SettlementImport, batch.ChannelId.ToString("N")) }, ct);
            var channel = await LoadChannelAsync(companyId, batch.ChannelId, tracked: false, ct);
            var lines = await _db.SettlementLines.Where(l => l.CompanyId == companyId && l.BatchId == batch.Id).ToListAsync(ct);
            // บรรทัดที่ผู้ใช้/ระบบจับคู่ได้แล้ว (Matched/AmountMismatch) ไม่แตะ — จับใหม่เฉพาะที่ยังไม่มีเอกสาร (Unmatched/AutoSummary)
            var toMatch = lines.Where(l => SettlementLineTypeRules.For(l.LineType).RequiresSaleMatch && !IsIntentSourced(l)
                && l.MatchStatus is SettlementMatchStatus.Unmatched or SettlementMatchStatus.AutoSummary).ToList();
            await MatchLinesAsync(companyId, channel, lines, toMatch, apply: true, ct);
            await SyncIntentStampsAsync(companyId, batch.Id, lines, ct);
            batch.Status = SettlementSaleMatch.DeriveImportStatus(lines.Select(l => (l.LineType, l.MatchStatus)));
            batch.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return await BuildBatchViewAsync(companyId, channel, batch, lines, ct);
        });
    }

    // ═══════════════════ ยกเลิกรอบโอน ═══════════════════

    public async Task VoidBatchAsync(Guid companyId, Guid userId, Guid batchId, string reason, CancellationToken ct = default)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why))
            throw new BusinessRuleException("ระบุเหตุผลที่ยกเลิกรอบโอน (เก็บในประวัติการแก้ไข)", "SETTLEMENT-VOID-REASON");
        if (why.Length > 500) why = why[..500];

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var batch = await LoadEditableBatchAsync(companyId, batchId, ct);
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.SettlementImport, batch.ChannelId.ToString("N")) }, ct);
            var lines = await _db.SettlementLines.Where(l => l.CompanyId == companyId && l.BatchId == batch.Id).ToListAsync(ct);
            // รอบถัดไปมีบรรทัด "คืนเงินภายหลัง" ของ intent ในรอบนี้ ⇒ ยกเลิกรอบนี้แล้ว intent จะกลับมาเป็นรายการใหม่ (ยอดคืนนับซ้ำ)
            var ownedIntents = await _db.PaymentIntents.AsNoTracking()
                .Where(i => i.CompanyId == companyId && i.SettlementBatchId == batch.Id).Select(i => i.Id).ToListAsync(ct);
            if (ownedIntents.Count > 0 && await _db.SettlementLines.AsNoTracking().AnyAsync(l => l.CompanyId == companyId
                    && l.BatchId != batch.Id && l.PaymentIntentId != null && ownedIntents.Contains(l.PaymentIntentId.Value), ct))
                throw new BusinessRuleException(
                    $"รอบโอนถัดไปมีรายการคืนเงินของรายการรับชำระในรอบ \"{batch.PayoutRef}\" — ยกเลิกรอบถัดไปก่อน แล้วค่อยยกเลิกรอบนี้",
                    "SETTLEMENT-VOID-ORDER");
            var now = DateTime.UtcNow;
            // ยกเลิก = soft-delete ทั้งรอบและบรรทัด ⇒ unique (PayoutRef · ExternalTxnId) กรอง IsDeleted = false ⇒ นำเข้าไฟล์เดิมใหม่ได้ (R-A9)
            foreach (var l in lines)
            {
                l.IsDeleted = true;
                l.UpdatedAt = now;
                l.UpdatedBy = userId.ToString();
            }
            await SyncIntentStampsAsync(companyId, batch.Id, Array.Empty<SettlementLine>(), ct);
            batch.Status = SettlementBatchStatus.Voided;
            batch.IsDeleted = true;
            batch.Note = Fit(string.IsNullOrWhiteSpace(batch.Note) ? "ยกเลิก: " + why : batch.Note + " · ยกเลิก: " + why, 2000);
            batch.UpdatedAt = now;
            batch.UpdatedBy = userId.ToString();
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = userId,
                EntityType = nameof(SettlementBatch),
                EntityId = batch.Id.ToString(),
                Action = AuditAction.Delete,
                OldValues = JsonSerializer.Serialize(new { payoutRef = batch.PayoutRef, lines = lines.Count, netPayout = batch.NetPayout }),
                NewValues = JsonSerializer.Serialize(new { action = "settlement-void", reason = why }),
                Timestamp = now,
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    private async Task<SettlementBatch> LoadEditableBatchAsync(Guid companyId, Guid batchId, CancellationToken ct)
    {
        var batch = await _db.SettlementBatches.FirstOrDefaultAsync(b => b.Id == batchId && b.CompanyId == companyId, ct)
                    ?? throw new BusinessRuleException("ไม่พบรอบโอนนี้ในบริษัท", "SETTLEMENT-BATCH", 404);
        if (!SettlementSaleMatch.IsEditable(batch.Status))
            throw new BusinessRuleException(
                $"รอบโอน \"{batch.PayoutRef}\" ลงบัญชีแล้ว (สถานะ {batch.Status}) — แก้ไข/ยกเลิกไม่ได้ · กลับรายการลงบัญชีของรอบนี้ก่อน",
                "SETTLEMENT-BATCH-LOCKED");
        return batch;
    }
}

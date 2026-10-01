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
    /// ยอดของออเดอร์นับจากทุกบรรทัดของรอบโอน (<paramref name="allLines"/>) · ตัวตัดสิน = <see cref="SettlementSaleMatch.Decide"/> ตัวเดียว ·
    /// บรรทัดที่คนตัดสินการจับคู่เอง (<c>MatchDecidedByUser</c>) ไม่ถูกเขียนทับ (R-B1)</summary>
    private async Task<Dictionary<Guid, SettlementMatchDecision>> MatchLinesAsync(Guid companyId, SettlementChannel channel,
        IReadOnlyList<SettlementLine> allLines, IReadOnlyCollection<SettlementLine> toMatch, bool apply, CancellationToken ct)
    {
        var decisions = new Dictionary<Guid, SettlementMatchDecision>();
        var orderIds = toMatch
            .Where(l => SettlementLineTypeRules.For(l.LineType).RequiresSaleMatch && !string.IsNullOrWhiteSpace(l.ExternalOrderId))
            .Select(l => l.ExternalOrderId!.Trim()).Distinct(StringComparer.Ordinal).ToList();

        // R-B4: เลขอ้างอิงในเอกสารที่ผู้ใช้พิมพ์เอง เทียบแบบตัดช่องว่าง ไม่สนตัวพิมพ์ — เลขเดียวกันต่างตัวพิมพ์ = ผู้สมัครหลายราย ⇒ คนเลือก (ไม่เดา)
        var byOrder = new Dictionary<string, List<SettlementMatchCandidate>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? key, SettlementMatchCandidate c)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!byOrder.TryGetValue(key.Trim(), out var l)) byOrder[key.Trim()] = l = new List<SettlementMatchCandidate>();
            l.Add(c);
        }

        foreach (var chunk in orderIds.Chunk(500))
        {
            var ids = chunk.ToList();
            var upper = ids.Select(x => x.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();
            var docs = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && d.Reference != null && upper.Contains(d.Reference.Trim().ToUpper())
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
                .Where(r => r.CompanyId == companyId && r.SourceReference != null && upper.Contains(r.SourceReference.Trim().ToUpper()))
                .Select(r => new { r.Id, r.SourceReference, r.ReservationNumber })
                .ToListAsync(ct);
            foreach (var r in reservations)
                Add(r.SourceReference, new SettlementMatchCandidate(SettlementMatchCandidateKind.Reservation, r.Id,
                    $"การจอง {r.ReservationNumber}", null, false, false));
        }

        // intent ของ gateway ในระบบ — R-B4: ค้นด้วยเลขอ้างอิงทั้งบริษัทเสมอ (ไม่ขึ้นกับชนิดช่องทาง/การผูก config) · ใช้รับชำระ/คืนเงินได้เฉพาะช่องทาง
        // gateway ที่ผังพักตรงกับขาเงินเข้าของ intent (R-A1) · พบแต่ใช้ไม่ได้ ⇒ ผู้สมัครที่ใช้ไม่ได้ + เหตุผล ⇒ Unmatched (ห้ามตกใบขายสรุปซ้ำ)
        if (orderIds.Count > 0)
        {
            string? usableProvider = null;
            if (channel.Kind == SettlementChannelKind.Gateway && channel.PaymentProviderConfigId is Guid cfgId)
            {
                var providerCode = await _db.PaymentProviderConfigs.AsNoTracking()
                    .Where(c => c.Id == cfgId && c.CompanyId == companyId).Select(c => c.ProviderCode).FirstOrDefaultAsync(ct);
                if (providerCode != null && await GatewayClearingMatchesAsync(companyId, channel, providerCode, ct))
                    usableProvider = providerCode;
            }
            var batchId = allLines.Count > 0 ? allLines[0].BatchId : Guid.Empty;
            var deciding = toMatch.Select(l => l.Id).ToHashSet();
            foreach (var chunk in orderIds.Chunk(500))
            {
                var ids = chunk.ToList();
                var intents = await _db.PaymentIntents.AsNoTracking()
                    .Where(i => i.CompanyId == companyId && i.ProviderRef != null && ids.Contains(i.ProviderRef))
                    .Select(i => new { i.Id, i.ProviderRef, i.ProviderCode, i.Status, i.ConfirmedAt, i.Amount, i.RefundedAmount,
                        i.SettlementJournalEntryId, i.SettlementBatchId, i.RefundOutcomeUnknownSince })
                    .ToListAsync(ct);
                var intentIds = intents.Select(i => i.Id).ToList();
                // R-B2: ยอดคืนที่ถูกนับไปแล้วในบรรทัดคืนเงินของทุกรอบโอนที่ยังไม่ถูกยกเลิก (ไม่ว่ารอบไหนเป็นเจ้าของยอดขาย · ไม่รวมบรรทัดที่กำลังตัดสิน)
                var refundCounted = intentIds.Count == 0
                    ? new Dictionary<Guid, decimal>()
                    : (await _db.SettlementLines.AsNoTracking()
                            .Where(l => l.CompanyId == companyId && l.PaymentIntentId != null && intentIds.Contains(l.PaymentIntentId.Value)
                                && l.LineType == SettlementLineType.Refund)
                            .Select(l => new { l.Id, Intent = l.PaymentIntentId!.Value, l.Amount })
                            .ToListAsync(ct))
                        .Where(x => !deciding.Contains(x.Id))
                        .GroupBy(x => x.Intent).ToDictionary(g => g.Key, g => -g.Sum(x => x.Amount));
                foreach (var i in intents)
                {
                    var received = i.ConfirmedAt != null && (i.Status == PaymentIntentStatus.Succeeded
                        || i.Status == PaymentIntentStatus.PartiallyRefunded || i.Status == PaymentIntentStatus.Refunded);
                    Add(i.ProviderRef, SettlementSaleMatch.IntentCandidate(new SettlementIntentFact(i.Id, i.ProviderRef, i.Amount,
                        i.RefundedAmount, usableProvider != null && i.ProviderCode == usableProvider, received,
                        i.SettlementJournalEntryId != null, i.SettlementBatchId, refundCounted.GetValueOrDefault(i.Id),
                        i.RefundOutcomeUnknownSince != null), batchId));
                }
            }
        }

        // ยอดของออเดอร์ — ตัวตั้งตัวเดียวกับหน้าจอ (ผู้สมัครที่เลือกได้) และการตัดสินของคน (D-01)
        var orderGroup = OrderGroupsOf(allLines);

        var refundLeft = new Dictionary<Guid, decimal>();
        foreach (var l in toMatch.OrderBy(x => x.Seq))
        {
            if (apply && l.MatchDecidedByUser) continue;    // R-B1: คำตัดสินของคน — ตัวจับคู่อัตโนมัติห้ามทับ
            var key = l.ExternalOrderId?.Trim();
            var cands = key != null && byOrder.TryGetValue(key, out var c) ? c : new List<SettlementMatchCandidate>();
            var fromAdapter = IsIntentSourced(l) ? l.PaymentIntentId : null;
            var d = SettlementSaleMatch.Decide(l.LineType, key, key != null ? orderGroup.GetValueOrDefault(key) : l.Amount, cands, fromAdapter);
            // R-B2: ยอดคืนผ่านระบบของ intent หนึ่งจัดสรรให้บรรทัดคืนเงินทีละบรรทัด — เกินยอดที่เหลือ ⇒ ให้คนเลือกใบขายเดิม
            if (fromAdapter == null && d.PaymentIntentId is Guid pid
                && SettlementLineTypeRules.For(l.LineType).Posting == SettlementPostingKind.Refund)
            {
                var left = refundLeft.TryGetValue(pid, out var v) ? v : cands.FirstOrDefault(x => x.Id == pid)?.RefundRemaining ?? 0m;
                (d, left) = SettlementSaleMatch.ApplyIntentRefundCapacity(d, l.Amount, left);
                refundLeft[pid] = left;
            }
            decisions[l.Id] = d;
            if (!apply) continue;
            l.MatchStatus = d.Status;
            l.MatchedDocumentId = d.DocumentId;
            l.ReservationId = d.ReservationId;
            l.PaymentIntentId = fromAdapter ?? d.PaymentIntentId;
        }
        return decisions;
    }

    private static Dictionary<string, decimal> OrderGroupsOf(IEnumerable<SettlementLine> lines)
        => SettlementSaleMatch.OrderGroupAmounts(lines.Select(l => (l.LineType, l.ExternalOrderId, l.Amount)));

    /// <summary>ยอดของออเดอร์ของบรรทัดนี้ — ตรงกับอาร์กิวเมนต์ที่ <c>MatchLinesAsync</c> ส่งให้ <see cref="SettlementSaleMatch.Decide"/></summary>
    private static decimal GroupAmountOf(SettlementLine l, IReadOnlyDictionary<string, decimal> groups)
    {
        var key = l.ExternalOrderId?.Trim();
        return key != null ? groups.GetValueOrDefault(key) : l.Amount;
    }

    /// <summary>บรรทัดที่ adapter ประกอบจาก PaymentIntent (คีย์ขึ้นต้น "pi:") — PaymentIntentId เป็นที่มา ไม่ใช่ผลการจับคู่</summary>
    private static bool IsIntentSourced(SettlementLine l)
        => l.PaymentIntentId != null && l.ExternalTxnId != null && l.ExternalTxnId.StartsWith("pi:", StringComparison.Ordinal);

    /// <summary>ประทับ <c>PaymentIntent.SettlementBatchId</c> ให้ตรงกับบรรทัดของรอบโอน — intent ที่บรรทัดอ้างถึงและยังไม่อยู่รอบใด ⇒ ประทับรอบนี้ ·
    /// intent ที่ประทับรอบนี้แต่ไม่มีบรรทัดอ้างแล้ว ⇒ ปลด (กันเส้นเดิม/รอบอื่นหยิบซ้ำ · กันค้างหลังผู้ใช้เปลี่ยนการจับคู่) ·
    /// intent ที่อยู่รอบอื่นแล้ว (บรรทัด "คืนเงินภายหลัง" ของรอบถัดไป) ⇒ คงรอบเดิม — รอบแรกเป็นเจ้าของยอดขาย
    /// <para>รอบ 201 ทีม ST (A-ST2 · X-6/X-7): ถือ<b>ล็อก gateway ตัวเดียวกับเส้นประกอบ/เส้นบันทึกรอบโอนเดิม</b> (<see cref="LockGatewaysAsync"/>) ก่อนโหลด intent ·
    /// ตรวจซ้ำใต้ล็อก: intent ที่จะประทับใหม่แต่เส้นเดิม (<c>GatewaySettlementService</c>) บันทึกรอบโอนไปแล้วระหว่างนี้ ⇒ ล้มดังให้กดใหม่ (เดิมพึ่งตาข่าย
    /// <c>IntentSettledElsewhere</c> ตอนลงบัญชี — รอบค้าง/ไม่ลงตัวที่มองเห็นแต่ต้องตามแก้) · เส้นไฟล์/จับคู่มือ/จับคู่ใหม่/ยกเลิกรอบ เข้าทางนี้ทุกเส้น</para></summary>
    private async Task SyncIntentStampsAsync(Guid companyId, Guid batchId, IReadOnlyList<SettlementLine> lines, CancellationToken ct)
    {
        var referenced = lines.Where(l => !l.IsDeleted && l.PaymentIntentId != null).Select(l => l.PaymentIntentId!.Value)
            .Distinct().ToList();
        await LockGatewaysAsync(companyId, batchId, referenced, ct);
        var stamped = await _db.PaymentIntents
            .Where(i => i.CompanyId == companyId && (i.SettlementBatchId == batchId || referenced.Contains(i.Id)))
            .ToListAsync(ct);
        var takenByLegacy = 0;
        foreach (var i in stamped)
        {
            var want = referenced.Contains(i.Id) ? batchId : (Guid?)null;
            if (i.SettlementBatchId == want) continue;
            if (want != null && i.SettlementBatchId != null) continue;          // เจ้าของคือรอบแรก (คืนเงินภายหลัง)
            if (want != null && i.SettlementJournalEntryId != null) { takenByLegacy++; continue; }
            i.SettlementBatchId = want;
            i.UpdatedAt = DateTime.UtcNow;
        }
        if (takenByLegacy > 0)
            throw new BusinessRuleException(
                $"รายการรับชำระออนไลน์ {takenByLegacy} รายการถูกบันทึกรอบโอนด้วยหน้า \u201Cรอบโอน gateway\u201D ไปแล้วระหว่างที่ระบบตัดสินบรรทัดนี้ — "
                + "ระบบยังไม่ได้บันทึกอะไร · กดอีกครั้ง (บรรทัดนั้นจะถูกจับคู่ใหม่ตามข้อมูลล่าสุด)", "SETTLEMENT-INTENT-TAKEN", 409);
    }

    /// <summary>
    /// **ล็อก gateway (<see cref="AdvisoryLockKey.GatewaySettlement"/> ต่อผู้ให้บริการ) ของ intent ที่รอบโอนนี้ถือ/กำลังจะถือ** — ตัวเดียวกับที่เส้นประกอบจาก intent
    /// (<c>PersistAsync</c>) และเส้นบันทึกรอบโอนเดิม (<c>GatewaySettlementService</c>) ถือ ⇒ สองเส้นห้ามหยิบ intent ชุดเดียวกันพร้อมกัน (A-ST2) ·
    /// ต้องอยู่ในธุรกรรมที่เปิดแล้ว (xact lock ถือถึง commit) และหลังล็อกช่องทาง (ลำดับ: ช่องทาง → gateway) · หลายผู้ให้บริการ ⇒ เรียงชื่อก่อนล็อก (ลำดับคงที่ กัน deadlock) ·
    /// คีย์คงที่ข้ามเครื่องผ่าน <see cref="AdvisoryLockKey.For(Guid, string, string)"/> (<c>advisory_lock_key_check</c>)
    /// </summary>
    private async Task LockGatewaysAsync(Guid companyId, Guid batchId, IReadOnlyCollection<Guid> intentIds, CancellationToken ct)
    {
        var ids = intentIds.ToList();
        var providers = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && (i.SettlementBatchId == batchId || ids.Contains(i.Id)))
            .Select(i => i.ProviderCode).Distinct().ToListAsync(ct);
        foreach (var pc in providers.Where(p => !string.IsNullOrWhiteSpace(p)).OrderBy(p => p, StringComparer.Ordinal))
            await LockGatewayAsync(companyId, pc, ct);
    }

    /// <summary>ล็อก gateway ของผู้ให้บริการหนึ่งราย (ตัวเดียวของทุกเส้นใน <c>SettlementImportService</c> — รวม <c>PersistAsync</c>) · รอได้ (re-entrant ในธุรกรรมเดียว)</summary>
    private async Task LockGatewayAsync(Guid companyId, string providerCode, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.GatewaySettlement, providerCode) }, ct);
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
        var view = await BuildBatchViewAsync(companyId, channel, batch, lines, ct);
        // ปุ่มของหน้าจอ (SettlementBatchActions) ต้องรู้ "ลงค้างครึ่งทาง" จากป้ายชุดเดียวกับด่าน LoadEditableBatchAsync (C-1)
        return view with { PostingArtifacts = (await PostingArtifactsAsync(companyId, batch.Id, ct)).Count };
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
        var groups = OrderGroupsOf(lines);
        var views = lines.OrderBy(l => l.Seq).Select(l => ToLineView(l, decisions.GetValueOrDefault(l.Id), groups)).ToList();
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

    /// <summary>มุมมองบรรทัด — ผู้สมัครแต่ละรายติดธง "เลือกได้/เหตุผล" จาก <see cref="SettlementSaleMatch.AssignRefusal"/> ตัวเดียวกับด่านของ
    /// <see cref="AssignLineMatchAsync"/> (D-01: หน้าเว็บเคยเปิดให้เลือกรายการรับชำระแล้ว server ตีกลับ 404 เสมอ)</summary>
    private static SettlementLineView ToLineView(SettlementLine l, SettlementMatchDecision? d, IReadOnlyDictionary<string, decimal> groups)
    {
        var groupAmount = GroupAmountOf(l, groups);
        return new(l.Id, l.Seq, l.LineType, SettlementLineTypeRules.For(l.LineType).LabelTh, l.RawTypeLabel, l.Description, l.TxnDate,
            l.ExternalOrderId, l.ExternalTxnId, l.Amount, l.VatAmount, l.WhtAmount, l.ClassifiedBy, l.ClassifyUsedAi,
            l.ClassifyAiFeedbackId, l.MatchStatus, l.MatchedDocumentId, l.PaymentIntentId, l.ReservationId, d?.Note,
            d == null
                ? Array.Empty<SettlementMatchCandidateView>()
                : d.Candidates.Select(c =>
                {
                    var refusal = SettlementSaleMatch.AssignRefusal(c, l.LineType, groupAmount, l.Amount);
                    return new SettlementMatchCandidateView(c.Kind.ToString(), c.Id, c.Label, c.OpenAmount,
                        c.CanReceive, c.IsRefundTarget, refusal == null, refusal);
                }).ToList(),
            l.OverrideAccountId, l.AdjustmentReason, l.MatchDecidedByUser,
            SettlementLineTypeRules.For(l.LineType).RequiresSaleMatch && !IsIntentSourced(l));
    }

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
            // R-B3: ล็อกช่องทางก่อน แล้วค่อยโหลด/ตรวจ (อ่านก่อนล็อก = ตัดสินจากข้อมูลที่อาจเปลี่ยนระหว่างรอล็อก)
            await LockChannelAsync(companyId, await LineChannelAsync(companyId, lineId, ct), ct);
            var line = await _db.SettlementLines.FirstOrDefaultAsync(l => l.Id == lineId && l.CompanyId == companyId, ct)
                       ?? throw new BusinessRuleException("ไม่พบบรรทัดนี้ในบริษัท", "SETTLEMENT-LINE", 404);
            // โหลดบรรทัดทั้งรอบก่อนแก้ (แผนก่อนแก้ของรอบค้างครึ่งทาง · S3-1) — ตัวแปร line อยู่ในชุดนี้ (instance เดียวกันจาก change tracker)
            var batchLines = await _db.SettlementLines.Where(l => l.CompanyId == companyId && l.BatchId == line.BatchId).ToListAsync(ct);
            var (batch, halfPosted) = await LoadRedecidableBatchAsync(companyId, line.BatchId, batchLines, ct);

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
            // ประเภทก่อนเปลี่ยน — ตัดสินว่าคำตัดสินการจับคู่ของคนยังใช้ได้ไหม (R-B1)
            var oldTypes = new Dictionary<Guid, SettlementLineType> { [line.Id] = line.LineType };
            Apply(line, type, SettlementClassifiedBy.User, overrideAccount, reason);
            MarkDecided(line, userId);
            var changed = new List<SettlementLine> { line };

            // ป้ายเดียวกัน+เครื่องหมายเดียวกันในรอบนี้ที่ผู้ใช้ยังไม่ได้เลือกเอง ⇒ ใช้คำตอบเดียวกัน (Learned — ไม่นับเป็นเสียงของผู้ใช้ซ้ำ)
            var norm = SettlementLineClassification.NormalizeLabel(line.RawTypeLabel);
            // R-B1: เฉพาะบรรทัดที่ประเภท "เปลี่ยนจริง" — บรรทัดที่ประเภทเท่าเดิมไม่ต้องแตะ (เดิมถูกส่งไปจับคู่ใหม่ ⇒ การจับคู่ของคนถูกทับ)
            if (request.ApplyToSameLabel && norm.Length > 0 && !rule.RequiresReason)
                foreach (var other in batchLines.Where(o => o.Id != line.Id && o.ClassifiedBy != SettlementClassifiedBy.User
                             && o.LineType != type
                             && (o.Amount > 0m) == (line.Amount > 0m)
                             && SettlementLineClassification.NormalizeLabel(o.RawTypeLabel) == norm
                             && SettlementLineClassification.Fits(type, o.Amount)).ToList())
                {
                    oldTypes[other.Id] = other.LineType;
                    Apply(other, type, SettlementClassifiedBy.Learned, other.OverrideAccountId, null);
                    MarkDecided(other, userId);     // A-ST7: คนสั่ง "ใช้กับป้ายเดียวกัน" = คนตัดสินบรรทัดเหล่านี้ด้วย
                    changed.Add(other);
                }

            var channel = await LoadChannelAsync(companyId, line.ChannelId, tracked: false, ct);
            await RematchChangedAsync(companyId, channel, batchLines, changed, oldTypes, ct);
            // S3-1: รอบค้างครึ่งทาง — การจัดประเภทใหม่ (รวม "ใช้กับป้ายเดียวกัน" และการจับคู่ใหม่ที่ตามมา) ต้องไม่เปลี่ยนชิ้นที่ออกแล้ว
            halfPosted?.Check(batchLines);
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
            return ToLineView(line, decisions.GetValueOrDefault(line.Id), OrderGroupsOf(batchLines));
        });
    }

    /// <summary>ประทับผู้ตัดสินบนบรรทัด (รอบ 201 ทีม ST · A-ST7) — ตัวเดียวของการจับคู่มือและการจัดประเภทโดยคน</summary>
    private static void MarkDecided(SettlementLine l, Guid userId)
    {
        l.DecidedBy = userId.ToString();
        l.DecidedAt = DateTime.UtcNow;
    }

    private static void Apply(SettlementLine l, SettlementLineType type, SettlementClassifiedBy by, Guid? overrideAccount, string? reason)
    {
        l.LineType = type;
        l.ClassifiedBy = by;
        l.OverrideAccountId = overrideAccount;
        l.AdjustmentReason = reason;
        l.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>หลังเปลี่ยนประเภท: ประเภทที่ไม่ต้องจับคู่ ⇒ NotRequired (ถอดการจับคู่ที่ไม่ใช่ที่มาจาก adapter) · ต้องจับคู่ ⇒ จับคู่ใหม่ —
    /// <b>ยกเว้น</b>บรรทัดที่คนตัดสินการจับคู่เองและยังอยู่กลุ่มการจับคู่เดิม (<see cref="SettlementSaleMatch.KeepUserMatch"/> · R-B1)</summary>
    private async Task RematchChangedAsync(Guid companyId, SettlementChannel channel, IReadOnlyList<SettlementLine> all,
        IReadOnlyList<SettlementLine> changed, IReadOnlyDictionary<Guid, SettlementLineType> oldTypes, CancellationToken ct)
    {
        var needMatch = new List<SettlementLine>();
        foreach (var l in changed)
        {
            var before = oldTypes.TryGetValue(l.Id, out var t) ? t : l.LineType;
            if (SettlementSaleMatch.KeepUserMatch(l.MatchDecidedByUser, before, l.LineType)) continue;
            l.MatchDecidedByUser = false;
            if (SettlementLineTypeRules.For(l.LineType).RequiresSaleMatch) { needMatch.Add(l); continue; }
            l.MatchStatus = SettlementMatchStatus.NotRequired;
            l.MatchedDocumentId = null;
            l.ReservationId = null;
            if (!IsIntentSourced(l)) l.PaymentIntentId = null;
        }
        if (needMatch.Count > 0) await MatchLinesAsync(companyId, channel, all, needMatch, apply: true, ct);
    }

    // ═══════════════════ ผู้ใช้ตัดสินการจับคู่ ═══════════════════

    public async Task<SettlementLineView> AssignLineMatchAsync(Guid companyId, Guid userId, Guid lineId, SettlementAssignMatchRequest request,
        CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            // R-B3: ล็อกช่องทางก่อน แล้วค่อยโหลด/ตรวจ
            await LockChannelAsync(companyId, await LineChannelAsync(companyId, lineId, ct), ct);
            var line = await _db.SettlementLines.FirstOrDefaultAsync(l => l.Id == lineId && l.CompanyId == companyId, ct)
                       ?? throw new BusinessRuleException("ไม่พบบรรทัดนี้ในบริษัท", "SETTLEMENT-LINE", 404);
            var batchLines = await _db.SettlementLines.Where(l => l.CompanyId == companyId && l.BatchId == line.BatchId).ToListAsync(ct);
            // S3-1: รอบที่ลงค้างครึ่งทางตัดสินการจับคู่ได้ เฉพาะเมื่อไม่เปลี่ยนชิ้นที่ออกแล้ว (halfPosted = แผนก่อนแก้ + ชิ้นที่ออกแล้ว · ตรวจก่อนบันทึก)
            var (batch, halfPosted) = await LoadRedecidableBatchAsync(companyId, line.BatchId, batchLines, ct);
            var rule = SettlementLineTypeRules.For(line.LineType);
            if (!rule.RequiresSaleMatch)
                throw new BusinessRuleException($"บรรทัดประเภท \"{rule.LabelTh}\" ไม่ต้องจับคู่ใบขาย", "SETTLEMENT-MATCH-NOT-REQUIRED");
            if (IsIntentSourced(line))
                throw new BusinessRuleException("บรรทัดนี้มาจากรายการรับชำระในระบบ (อยู่ในผังพักแล้ว) — ไม่ต้องจับคู่ใบขายซ้ำ",
                    "SETTLEMENT-MATCH-INTENT");
            var isRefund = rule.Posting == SettlementPostingKind.Refund;
            // บรรทัดขายของออเดอร์เดียวกันต้องไปใบเดียวกัน (ตัวคิดแผนรวมรับชำระต่อใบ) · คืนเงินตัดสินทีละบรรทัด
            var group = isRefund || string.IsNullOrWhiteSpace(line.ExternalOrderId)
                ? new List<SettlementLine> { line }
                : batchLines.Where(l => l.ExternalOrderId?.Trim() == line.ExternalOrderId!.Trim()
                    && SettlementLineTypeRules.For(l.LineType).Posting == SettlementPostingKind.SaleComponent
                    && !IsIntentSourced(l)).ToList();

            if (request.DocumentId != null && request.PaymentIntentId != null)
                throw new BusinessRuleException("เลือกได้อย่างเดียว — เอกสารขาย หรือรายการรับชำระออนไลน์ในระบบ", "SETTLEMENT-MATCH-ONE");
            if (request.UseDailySummary)
            {
                if (isRefund)
                    throw new BusinessRuleException("คืนเงินต้องอ้างใบขายเดิม (§86/10) — เลือกใบขายของออเดอร์นี้", "SETTLEMENT-REFUND-NEEDS-DOC");
                foreach (var l in group) SetMatch(l, SettlementMatchStatus.AutoSummary, null, userId);
            }
            else if (request.PaymentIntentId is Guid intentId)
            {
                // D-01: รายการรับชำระ = ผู้สมัครของเลขออเดอร์นี้ที่ตัวจับคู่อัตโนมัติคำนวณสดใต้ล็อก (ข้อเท็จจริงชุดเดียวกัน: ช่องทางใช้ได้ ·
                // อยู่รอบอื่นแล้ว · ผลคืนเงินไม่แน่ชัด · ยอดคืนที่ถูกนับแล้ว) แล้วตัดสินด้วย AssignRefusal ตัวเดียวกับที่หน้าจอใช้ติดธง "เลือกได้"
                var channel = await LoadChannelAsync(companyId, line.ChannelId, tracked: false, ct);
                var probe = await MatchLinesAsync(companyId, channel, batchLines, new[] { line }, apply: false, ct);
                var candidate = (probe.TryGetValue(line.Id, out var pd) ? pd.Candidates : Array.Empty<SettlementMatchCandidate>())
                    .FirstOrDefault(c => c.Kind == SettlementMatchCandidateKind.PaymentIntent && c.Id == intentId)
                    ?? throw new BusinessRuleException(
                        "รายการรับชำระที่เลือกไม่ใช่ผู้สมัครของเลขออเดอร์นี้ (เลขอ้างอิงไม่ตรง หรือไม่ใช่ของบริษัทนี้) — เปิดรอบโอนใหม่แล้วเลือกจากรายการที่ระบบเสนอ",
                        "SETTLEMENT-MATCH-INTENT-NOT-CANDIDATE", 404);
                var refusal = SettlementSaleMatch.AssignRefusal(candidate, line.LineType, GroupAmountOf(line, OrderGroupsOf(batchLines)), line.Amount);
                if (refusal != null)
                    throw new BusinessRuleException(refusal, "SETTLEMENT-MATCH-INTENT-REFUSED", 409);
                foreach (var l in group) SetMatch(l, SettlementMatchStatus.Matched, null, userId, intentId);
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
                foreach (var l in group) SetMatch(l, SettlementMatchStatus.Matched, docId, userId);
            }
            // S3-1: การแก้ของรอบค้างครึ่งทางต้องไม่เปลี่ยนชิ้นที่ออกแล้ว (ใบค่าธรรมเนียม · ใบสรุป · การรับชำระที่บันทึกแล้ว) — ไม่ผ่าน = 409 ทั้งธุรกรรม
            halfPosted?.Check(batchLines);
            await SyncIntentStampsAsync(companyId, batch.Id, batchLines, ct);
            batch.Status = SettlementSaleMatch.DeriveImportStatus(batchLines.Select(l => (l.LineType, l.MatchStatus)));
            batch.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToLineView(line, null, OrderGroupsOf(batchLines));
        });
    }

    /// <summary>คนตัดสินการจับคู่ — ติดธง <c>MatchDecidedByUser</c> (ตัวจับคู่อัตโนมัติห้ามทับ · R-B1) + ผู้ตัดสิน (A-ST7 · นับเป็นผู้ทำใน SoD ของการลงบัญชี)</summary>
    /// <param name="paymentIntentId">รายการรับชำระที่คนเลือก (D-01) — null = ไม่ผูก (ถอดของเดิม)</param>
    private static void SetMatch(SettlementLine l, SettlementMatchStatus status, Guid? documentId, Guid decidedBy, Guid? paymentIntentId = null)
    {
        l.MatchDecidedByUser = true;
        MarkDecided(l, decidedBy);
        l.MatchStatus = status;
        l.MatchedDocumentId = documentId;
        l.ReservationId = null;
        l.PaymentIntentId = paymentIntentId;
        l.UpdatedAt = DateTime.UtcNow;
    }

    public async Task<SettlementBatchView> RematchBatchAsync(Guid companyId, Guid batchId, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            // R-B3: ล็อกช่องทางก่อน แล้วค่อยโหลด/ตรวจว่ายังแก้ได้ (เดิมตรวจก่อนล็อก ⇒ รอล็อกหลังการลงบัญชีแล้วทำต่อบนข้อมูลเก่า)
            await LockChannelAsync(companyId, await BatchChannelAsync(companyId, batchId, ct), ct);
            var batch = await LoadEditableBatchAsync(companyId, batchId, ct);
            var channel = await LoadChannelAsync(companyId, batch.ChannelId, tracked: false, ct);
            var lines = await _db.SettlementLines.Where(l => l.CompanyId == companyId && l.BatchId == batch.Id).ToListAsync(ct);
            // บรรทัดที่ผู้ใช้/ระบบจับคู่ได้แล้ว (Matched/AmountMismatch) ไม่แตะ — จับใหม่เฉพาะที่ยังไม่มีเอกสาร (Unmatched/AutoSummary) ·
            // R-B1: ใบขายสรุปที่คน "ยืนยัน" เอง (MatchDecidedByUser) ไม่ใช่ "ยังไม่มีเอกสาร" — ห้ามจับใหม่
            var toMatch = lines.Where(l => SettlementLineTypeRules.For(l.LineType).RequiresSaleMatch && !IsIntentSourced(l)
                && !l.MatchDecidedByUser
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
            // R-B3 + C-1: ล็อกช่องทาง (ตัวเดียวกับการลงบัญชี) ก่อน แล้วค่อยโหลด/ตรวจ — ยกเลิกแทรกการลงบัญชีไม่ได้ · รอบที่ลงค้างครึ่งทางยกเลิกไม่ได้
            await LockChannelAsync(companyId, await BatchChannelAsync(companyId, batchId, ct), ct);
            var batch = await LoadEditableBatchAsync(companyId, batchId, ct);
            var lines = await _db.SettlementLines.Where(l => l.CompanyId == companyId && l.BatchId == batch.Id).ToListAsync(ct);
            // A-ST2: ล็อก gateway ก่อนอ่าน "รอบถัดไปมีบรรทัดคืนเงินของ intent ในรอบนี้ไหม" (เส้นประกอบของอีกช่องทางที่ผูก config เดียวกันเพิ่มบรรทัดได้พร้อมกัน)
            await LockGatewaysAsync(companyId, batch.Id, Array.Empty<Guid>(), ct);
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

    // ═══════════════════ บัญชีธนาคารที่รับเงิน (D-03) ═══════════════════

    public async Task<SettlementBatchView> SetBankAccountAsync(Guid companyId, Guid userId, Guid batchId, Guid? bankAccountId,
        CancellationToken ct = default)
    {
        if (bankAccountId is not Guid bankId)
            throw new BusinessRuleException("เลือกบัญชีธนาคารที่เงินรอบนี้เข้าจริง (ตามสเตทเมนต์ธนาคาร)", "SETTLEMENT-BANK-REQUIRED");
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            // ล็อกตัวเดียวกับผู้ลงบัญชีก่อนโหลด/ตรวจ (R-B3 · C-1) — เปลี่ยนบัญชีแทรกระหว่างการลงบัญชีไม่ได้ · รอบที่ลงแล้ว/ค้างครึ่งทางแก้ไม่ได้
            await LockChannelAsync(companyId, await BatchChannelAsync(companyId, batchId, ct), ct);
            var batch = await LoadEditableBatchAsync(companyId, batchId, ct);
            var bank = await _db.BankAccounts.AsNoTracking()
                .Where(b => b.Id == bankId && b.CompanyId == companyId && b.IsActive)
                .Select(b => new { b.Id, b.BankName, b.AccountNumber })
                .FirstOrDefaultAsync(ct)
                ?? throw new BusinessRuleException("ไม่พบบัญชีธนาคารที่เลือกในบริษัทนี้ (หรือถูกปิดใช้) — เพิ่ม/เปิดใช้ที่ ตั้งค่า → บัญชีธนาคาร",
                    "SETTLEMENT-BANK", 404);
            var before = batch.BankAccountId;
            if (before != bank.Id)
            {
                batch.BankAccountId = bank.Id;
                batch.UpdatedAt = DateTime.UtcNow;
                batch.UpdatedBy = userId.ToString();
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId,
                    UserId = userId,
                    EntityType = nameof(SettlementBatch),
                    EntityId = batch.Id.ToString(),
                    Action = AuditAction.Update,
                    OldValues = JsonSerializer.Serialize(new { bankAccountId = before }),
                    NewValues = JsonSerializer.Serialize(new { action = "settlement-bank-account", bankAccountId = bank.Id,
                        bank = bank.BankName + " " + bank.AccountNumber }),
                    Timestamp = DateTime.UtcNow,
                });
                await _db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            var channel = await LoadChannelAsync(companyId, batch.ChannelId, tracked: false, ct);
            var lines = await _db.SettlementLines.AsNoTracking()
                .Where(l => l.CompanyId == companyId && l.BatchId == batch.Id).OrderBy(l => l.Seq).ToListAsync(ct);
            return await BuildBatchViewAsync(companyId, channel, batch, lines, ct);
        });
    }

    /// <summary>โหลดรอบโอนที่ยังแก้ได้ — <b>ต้องเรียกหลัง <see cref="LockChannelAsync"/></b> (R-B3) · ลงบัญชีแล้ว/ยกเลิกแล้ว/ลงค้างครึ่งทาง (C-1) ⇒ ล้มดังพร้อมทางไปต่อ</summary>
    private async Task<SettlementBatch> LoadEditableBatchAsync(Guid companyId, Guid batchId, CancellationToken ct)
    {
        var batch = await _db.SettlementBatches.FirstOrDefaultAsync(b => b.Id == batchId && b.CompanyId == companyId, ct)
                    ?? throw new BusinessRuleException("ไม่พบรอบโอนนี้ในบริษัท", "SETTLEMENT-BATCH", 404);
        if (!SettlementSaleMatch.IsEditable(batch.Status))
            throw new BusinessRuleException(
                $"รอบโอน \"{batch.PayoutRef}\" ลงบัญชีแล้ว (สถานะ {batch.Status}) — แก้ไข/ยกเลิกไม่ได้ · กลับรายการลงบัญชีของรอบนี้ก่อน",
                "SETTLEMENT-BATCH-LOCKED");
        var artifacts = await PostingArtifactsAsync(companyId, batch.Id, ct);
        if (!SettlementSaleMatch.IsEditable(batch.Status, artifacts.Count))
            throw new BusinessRuleException(
                $"รอบโอน \"{batch.PayoutRef}\" ลงบัญชีค้างครึ่งทาง — มี {string.Join(", ", artifacts.Take(10))} ที่การลงบัญชีสร้างไว้แล้ว · "
                + "แก้บรรทัด/ยกเลิกรอบโอนตอนนี้ทำให้ของที่ออกแล้วไม่ตรงหรือกลายเป็นของกำพร้า (ลงซ้ำได้) — กด \"ลงบัญชี\" ต่อให้ครบแล้วค่อย "
                + "\"ยกเลิกการลงบัญชี\" หรือยกเลิกเอกสาร/การรับชำระเหล่านั้นที่หน้าเอกสารก่อน", "SETTLEMENT-BATCH-PARTIAL", 409);
        return batch;
    }

    /// <summary>
    /// โหลดรอบโอนสำหรับ<b>ตัดสินการจับคู่/จัดประเภทบรรทัด</b> — ต้องเรียกหลัง <see cref="LockChannelAsync"/> (R-B3) · ลงบัญชีแล้ว/ยกเลิกแล้ว ⇒ ล้มดัง ·
    /// <b>ลงค้างครึ่งทาง ⇒ ได้ พร้อมด่าน</b> (review198-S3 S3-1): คืน <see cref="PartialEditGuard"/> ที่จำแผนก่อนแก้ + ชิ้นที่ออกไปแล้ว ⇒ ผู้เรียกต้อง
    /// <c>Check</c> หลังแก้ก่อนบันทึก (การแก้ที่เปลี่ยนชิ้นที่ออกแล้ว = 409 ทั้งธุรกรรม) · รอบที่ยังไม่มีของ ⇒ guard = null (แก้ได้ตามเดิม)
    /// </summary>
    private async Task<(SettlementBatch Batch, PartialEditGuard? Guard)> LoadRedecidableBatchAsync(Guid companyId, Guid batchId,
        IReadOnlyList<SettlementLine> batchLines, CancellationToken ct)
    {
        var batch = await _db.SettlementBatches.FirstOrDefaultAsync(b => b.Id == batchId && b.CompanyId == companyId, ct)
                    ?? throw new BusinessRuleException("ไม่พบรอบโอนนี้ในบริษัท", "SETTLEMENT-BATCH", 404);
        if (!SettlementSaleMatch.IsEditable(batch.Status))
            throw new BusinessRuleException(
                $"รอบโอน \"{batch.PayoutRef}\" ลงบัญชีแล้ว (สถานะ {batch.Status}) — แก้ไข/ยกเลิกไม่ได้ · กลับรายการลงบัญชีของรอบนี้ก่อน",
                "SETTLEMENT-BATCH-LOCKED");
        var frozen = await FrozenPartsAsync(companyId, batch.Id, ct);
        if (frozen.DocumentComponents.Count == 0 && frozen.ReceivedDocumentIds.Count == 0) return (batch, null);
        var channel = await LoadChannelAsync(companyId, batch.ChannelId, tracked: false, ct);
        var vat = await CompanyVatStatus.IsRegisteredAsync(_db, companyId, ct);
        return (batch, new PartialEditGuard(batch, channel, vat, SettlementBatchMath.Plan(batch, batchLines, channel, vat), frozen));
    }

    /// <summary>แผนก่อนแก้ + ชิ้นที่ออกไปแล้วของรอบที่ลงค้างครึ่งทาง — <see cref="Check"/> ด้วยบรรทัดหลังแก้ (ตัวตัดสิน <see cref="SettlementPartialEdit"/>)</summary>
    private sealed record PartialEditGuard(SettlementBatch Batch, SettlementChannel Channel, bool VatRegistered, SettlementPostingPlan Before,
        SettlementFrozenParts Frozen)
    {
        public void Check(IReadOnlyList<SettlementLine> linesAfter)
        {
            var after = SettlementBatchMath.Plan(Batch, linesAfter, Channel, VatRegistered);
            if (SettlementPartialEdit.Refusal(Before, after, Frozen) is string why)
                throw new BusinessRuleException(why, "SETTLEMENT-BATCH-PARTIAL-FROZEN", 409);
        }
    }

    /// <summary>ชิ้นของรอบที่ออกไปแล้ว (เอกสารที่ยังไม่ถูกยกเลิก → ชิ้นจาก <c>CreatedBy</c> · ใบขายที่มีการรับชำระของรอบนี้) — กุญแจชุดเดียวกับ
    /// <see cref="PostingArtifactsAsync"/> และผู้ลงบัญชี (การรับชำระ = คอลัมน์ <c>Payment.SettlementBatchId</c> · รอบ 201 A-ST1)</summary>
    private async Task<SettlementFrozenParts> FrozenPartsAsync(Guid companyId, Guid batchId, CancellationToken ct)
    {
        var prefix = SettlementPostingKeys.CreatorPrefix(batchId);
        var docRows = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.Status != DocumentStatus.Voided
                    && d.CreatedBy != null && d.CreatedBy.StartsWith(prefix))
                .Select(d => new { d.CreatedBy, d.SettlementPieceFingerprint }).ToListAsync(ct);
        var components = docRows.Select(d => d.CreatedBy!.Substring(prefix.Length)).Distinct(StringComparer.Ordinal).ToList();
        // A-ST8: ลายนิ้วมือที่ประทับตอนออก (ชิ้นที่มีหลายใบ/ไม่มีค่า ⇒ ไม่ใส่ = ไม่รู้ ⇒ ตัวเทียบใช้แผนก่อน/หลังแก้แบบเดิม)
        var issued = docRows.Where(d => d.SettlementPieceFingerprint != null)
            .GroupBy(d => d.CreatedBy!.Substring(prefix.Length), StringComparer.Ordinal)
            .Where(g => g.Select(x => x.SettlementPieceFingerprint).Distinct().Count() == 1
                && docRows.Count(d => d.CreatedBy!.Substring(prefix.Length) == g.Key) == g.Count())
            .ToDictionary(g => g.Key, g => g.First().SettlementPieceFingerprint!, StringComparer.Ordinal);
        var received = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.SettlementBatchId == batchId)
            .Select(p => p.DocumentId).Distinct().ToListAsync(ct);
        return new SettlementFrozenParts(components, received, issued);
    }

    /// <summary>เอกสาร/การรับชำระที่ยังไม่ถูกยกเลิกซึ่งการลงบัญชีสร้างให้รอบโอนนี้ (กุญแจตัวเดียวกับผู้ลงบัญชี — <see cref="SettlementPostingKeys"/> ·
    /// การรับชำระ = คอลัมน์ <c>Payment.SettlementBatchId</c> · รอบ 201 A-ST1)</summary>
    private async Task<List<string>> PostingArtifactsAsync(Guid companyId, Guid batchId, CancellationToken ct)
    {
        var prefix = SettlementPostingKeys.CreatorPrefix(batchId);
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.Status != DocumentStatus.Voided
                && d.CreatedBy != null && d.CreatedBy.StartsWith(prefix))
            .Select(d => "เอกสาร " + d.DocumentNumber).ToListAsync(ct);
        var pays = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.SettlementBatchId == batchId)
            .Select(p => "การรับชำระ " + p.PaymentNumber).ToListAsync(ct);
        return docs.Concat(pays).ToList();
    }

    /// <summary>ล็อกต่อช่องทาง <b>ตัวเดียวกับผู้ลงบัญชี</b> (<see cref="SettlementChannelLock"/> · C-1) — ธุรกรรมต้องเปิดแล้ว · เรียกก่อนโหลด/ตรวจทุกครั้ง (R-B3) ·
    /// review198-S3 S3-9: <b>ลองล็อก ไม่รอ</b> — เดิม <c>pg_advisory_xact_lock</c> รอไม่จำกัดขณะผู้ลงบัญชีถือ session lock ตลอดการสร้าง/อนุมัติเอกสาร
    /// (หลายสิบวินาที) ⇒ คำขอเว็บค้างจน command timeout (500) · ถูกถือ ⇒ 409 ข้อความ "กำลังทำอยู่" ตัวเดียวกับฝั่งลงบัญชี (ธุรกรรม rollback · กดใหม่ได้)</summary>
    private async Task LockChannelAsync(Guid companyId, Guid channelId, CancellationToken ct)
    {
        if (!await JobLock.TryXactLockAsync(_db, SettlementChannelLock.Key(companyId, channelId), ct))
            throw new BusinessRuleException(SettlementChannelLock.BusyMessage, "SETTLEMENT-BUSY", 409);
    }

    /// <summary>ช่องทางของบรรทัด (ไม่เปลี่ยนตลอดอายุบรรทัด — อ่านก่อนล็อกได้เพื่อรู้ว่าจะล็อกคีย์ไหน)</summary>
    private async Task<Guid> LineChannelAsync(Guid companyId, Guid lineId, CancellationToken ct)
        => await _db.SettlementLines.AsNoTracking().Where(l => l.Id == lineId && l.CompanyId == companyId)
               .Select(l => (Guid?)l.ChannelId).FirstOrDefaultAsync(ct)
           ?? throw new BusinessRuleException("ไม่พบบรรทัดนี้ในบริษัท", "SETTLEMENT-LINE", 404);

    /// <summary>ช่องทางของรอบโอน (ไม่เปลี่ยนตลอดอายุรอบโอน — อ่านก่อนล็อกได้เพื่อรู้ว่าจะล็อกคีย์ไหน)</summary>
    private async Task<Guid> BatchChannelAsync(Guid companyId, Guid batchId, CancellationToken ct)
        => await _db.SettlementBatches.AsNoTracking().Where(b => b.Id == batchId && b.CompanyId == companyId)
               .Select(b => (Guid?)b.ChannelId).FirstOrDefaultAsync(ct)
           ?? throw new BusinessRuleException("ไม่พบรอบโอนนี้ในบริษัท", "SETTLEMENT-BATCH", 404);
}

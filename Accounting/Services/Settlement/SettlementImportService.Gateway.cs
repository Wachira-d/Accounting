using Accounting.Helpers;
using Accounting.Models.DTOs.Settlement;
using Accounting.Models.Enums;
using Accounting.Services.Settlement.Adapters;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Settlement;

/// <summary>
/// **settlement เฟส 2 — ประกอบรอบโอนของช่องทาง Gateway จากรายการรับชำระ (PaymentIntent) ในระบบเอง** (รอบ 200 ทีม P2 ·
/// <c>erp-review/2026-09-29/DECISIONS.md</c> ข้อ 12 · กติกาอยู่ใน <see cref="GatewayBatchIntentRules"/>)
///
/// <para>═══ ลำดับ ═══
/// <list type="number">
/// <item>ช่องทางต้องเป็น Gateway ที่ผูก config (<see cref="GatewayBatchIntentRules.ChannelRefusal"/> — marketplace/OTA ไม่ถูกแตะ)</item>
/// <item>โหมด VAT/หัก ณ ที่จ่ายของค่าธรรมเนียม config ↔ ช่องทาง ต้องตรงกัน (<see cref="GatewayBatchIntentRules.ModeMismatch"/>) — ข้อเท็จจริงเดียว</item>
/// <item>ผังพักของช่องทาง = ผังที่ขาเงินเข้าของ intent ลงไว้ (R-A1) — ไม่งั้นบรรทัดที่พก PaymentIntentId ไม่ได้ "อยู่ในผังพักแล้ว"</item>
/// <item>เลือก intent ที่ยังไม่มีเจ้าของรอบโอน (<see cref="GatewayBatchIntentRules.UnclaimedForBatch"/>) ในช่วง [ต้นช่วง, ปลายช่วง] +
/// คืนเงินภายหลังของ intent ที่รอบโอนเส้นนี้เป็นเจ้าของ (<see cref="GatewayBatchIntentRules.LateRefundInBatch"/>)</item>
/// <item>ยอดคืน ณ วันเงินเข้า (<see cref="GatewaySettlementMath.RefundedAsOf"/> · สูตรเดียวกับเส้นเดิม) — แยกไม่ได้ = บล็อก ห้ามเดา</item>
/// <item>บรรทัด: <see cref="PaymentIntentAdapter.BuildRows"/> (ค่าธรรมเนียมจาก <see cref="GatewaySettlementMath.Contribution"/> ตามโหมดของ config) →
/// <c>PersistAsync</c> ร่วมกับทุกทางเข้า (ล็อกช่องทาง + ล็อก gateway · ตรวจซ้ำใต้ล็อก · audit)</item>
/// </list></para>
/// </summary>
public sealed partial class SettlementImportService
{
    public async Task<SettlementImportResult> ImportFromPaymentIntentsAsync(Guid companyId, Guid userId,
        SettlementIntentBatchRequest request, CancellationToken ct = default)
    {
        var h = request.Header;
        var channel = await LoadChannelAsync(companyId, h.ChannelId, tracked: false, ct);
        EnsureActive(channel);
        if (GatewayBatchIntentRules.ChannelRefusal(channel.Kind, channel.PaymentProviderConfigId) is string notGateway)
            throw new BusinessRuleException(notGateway, "SETTLEMENT-NOT-GATEWAY");
        var cfgId = channel.PaymentProviderConfigId!.Value;
        var cfg = await _db.PaymentProviderConfigs.AsNoTracking()
            .Where(c => c.Id == cfgId && c.CompanyId == companyId)
            .Select(c => new { c.ProviderCode, c.FeeVatMode, c.WhtOnFee })
            .FirstOrDefaultAsync(ct)
            ?? throw new BusinessRuleException("ไม่พบการตั้งค่า gateway ที่ช่องทางนี้ผูกไว้ — แก้การผูกในหน้าตั้งค่าช่องทาง", "SETTLEMENT-GATEWAY-MISSING", 404);
        var providerCode = cfg.ProviderCode;

        // ข้อเท็จจริงเดียว: "ผู้ให้บริการคิด VAT บนค่าธรรมเนียมไหม · เราหัก ณ ที่จ่ายไหม" config กับช่องทางต้องตอบตรงกัน —
        // ไม่ตรง = รอบโอนแต่งภาษีซื้อ (config ไม่แยก + ช่องทาง VAT 7%) หรือยอดไม่ลงตัว/ภาษีซื้อหาย (config บวก VAT + ช่องทางไม่มี VAT)
        if (GatewayBatchIntentRules.ModeMismatch(cfg.FeeVatMode, cfg.WhtOnFee, channel.FeeVatMode, channel.FeeWhtMode) is string modeBad)
            throw new BusinessRuleException(modeBad, "SETTLEMENT-GATEWAY-MODE-MISMATCH");

        // R-A1: บรรทัดที่พก PaymentIntentId ถูกนับว่า "อยู่ในผังพักแล้ว" — จริงเฉพาะเมื่อผังพักของช่องทาง = ผังที่ขาเงินเข้าของ intent ลงไว้
        if (!await GatewayClearingMatchesAsync(companyId, channel, providerCode, ct))
            throw new BusinessRuleException(
                "ผังพักของช่องทางนี้ไม่ตรงกับผังที่ gateway ลงรับเงินไว้ (หรือยังไม่ได้ผูก) — บันทึกหน้าตั้งค่าช่องทางอีกครั้งเพื่อให้ระบบผูกผังเดียวกับ gateway "
                + "ก่อนประกอบรอบโอน (ไม่งั้นยอดรับชำระกับยอดโอนจะอยู่คนละผังตลอดไป)", "SETTLEMENT-GATEWAY-CLEARING-MISMATCH");

        // วันเงินเข้าเป็นจุดตัดของยอดคืน (คืนก่อนวันนั้น = หักรอบนี้ · ตั้งแต่วันนั้น = รอบถัดไป) — ต้องรู้ก่อนเลือกบรรทัด
        // (ข้อความ/รหัสเดียวกับ PersistAsync ซึ่งตรวจซ้ำอีกครั้ง)
        if (h.PayoutDate is not DateTime payoutDate)
            throw new BusinessRuleException("ระบุวันที่เงินเข้าธนาคารของรอบโอนนี้", "SETTLEMENT-PAYOUT-DATE");

        var loaded = await LoadIntentRowsAsync(companyId, providerCode, cfg.FeeVatMode, h.PeriodFrom, h.PeriodTo, payoutDate, ct);
        var rows = loaded.Rows;
        var warnings = new List<string>();
        // review198-E2 E2-10: การคืนเงินผลไม่แน่ชัด — ประกอบรอบโอนได้ (เห็นยอด) แต่ลงบัญชีไม่ได้จนกว่าจะตรวจผล (ด่านผู้ลงบัญชี RefundOutcomeUnknown)
        if (loaded.RefundUnknown > 0)
            warnings.Add($"รายการรับชำระ {loaded.RefundUnknown} รายการมีการคืนเงินที่ผลยังไม่แน่ชัด — ตรวจผลการคืนเงินกับผู้ให้บริการก่อน "
                + "(รอบโอนนี้จะลงบัญชีไม่ได้จนกว่าจะตรวจผลแล้ว)");
        if (rows.FeeUnknownCount > 0)
            warnings.Add($"รายการรับชำระ {rows.FeeUnknownCount} รายการยังไม่รู้ค่าธรรมเนียมจริง (ไม่มีบรรทัดค่าธรรมเนียม) — "
                + "ยอดรอบโอนจะไม่ลงตัวจนกว่าจะแก้ค่าธรรมเนียมหรือเพิ่มบรรทัดปรับปรุงที่มีเหตุผล");
        // ต้นช่วงที่ผู้ใช้เลือกมีผลจริง (เดิมถูกเพิกเฉยเงียบ) — รายการเก่ากว่านั้นที่ยังไม่มีเจ้าของต้องมองเห็น ไม่ใช่หายไปจากทุกรอบ
        if (loaded.OlderUnclaimed > 0)
            warnings.Add($"มีรายการรับชำระ {loaded.OlderUnclaimed} รายการก่อนต้นช่วงที่เลือก ที่ยังไม่อยู่ในรอบโอนใด — "
                + "ถ้าผู้ให้บริการโอนรวมมาในรอบนี้ ให้เลื่อนวันต้นช่วงแล้วประกอบใหม่ (ไม่งั้นยอดรอบโอนจะไม่ลงตัว)");
        if (rows.Rows.Count == 0)
            throw new BusinessRuleException("ไม่มีรายการรับชำระที่รอเข้ารอบโอนของ gateway นี้ในช่วงที่เลือก (หรือถูกบันทึกรอบโอนด้วยเส้นเดิมแล้ว)",
                "SETTLEMENT-NO-INTENTS");
        return await PersistAsync(new PersistInput(companyId, userId, channel.Id, h, rows.Rows, Array.Empty<string>(),
            SettlementSourceKind.PaymentIntents, null, null, PaymentIntentAdapter.AdapterCode, rows.NewIntentIds, providerCode,
            warnings), ct);
    }

    /// <summary>แถว intent ที่ตัวประกอบต้องใช้ (projection เดียวของ "ใหม่" และ "คืนภายหลัง")</summary>
    private sealed record GatewayIntentRow(Guid Id, string? ProviderRef, decimal Amount, decimal RefundedAmount, decimal? FeeActual,
        decimal FeeEstimated, DateTime? ConfirmedAt, DateTime? LastRefundedAt, bool OutcomeUnknown);

    /// <summary>ผลโหลด: บรรทัด · intent ที่คืนเงินผลไม่แน่ชัด (E2-10) · intent ที่ยังไม่มีเจ้าของแต่เก่ากว่าต้นช่วง (แจ้ง)</summary>
    private sealed record GatewayIntentLoad(SettlementIntentRows Rows, int RefundUnknown, int OlderUnclaimed);

    private async Task<GatewayIntentLoad> LoadIntentRowsAsync(Guid companyId, string providerCode, GatewayFeeVatMode feeVatMode,
        DateTime? periodFrom, DateTime? periodTo, DateTime payoutDate, CancellationToken ct)
    {
        var from = periodFrom is DateTime pf ? ThaiDate.CalendarDateUtc(pf) : (DateTime?)null;
        var to = periodTo is DateTime pt ? ThaiDate.CalendarDateUtc(pt).AddDays(1) : (DateTime?)null;
        // ยังไม่มีเจ้าของรอบโอน (เส้นเดิม SettlementJournalEntryId · เส้นนี้ SettlementBatchId ว่างทั้งคู่) — ตัวตัดสินตัวเดียว (expression)
        var fresh = await _db.PaymentIntents.AsNoTracking()
            .Where(GatewayBatchIntentRules.UnclaimedForBatch(companyId, providerCode, from, to))
            .Select(i => new GatewayIntentRow(i.Id, i.ProviderRef, i.Amount, i.RefundedAmount, i.FeeActual, i.FeeEstimated,
                i.ConfirmedAt, i.LastRefundedAt, i.RefundOutcomeUnknownSince != null))
            .ToListAsync(ct);
        var olderUnclaimed = from == null ? 0
            : await _db.PaymentIntents.AsNoTracking()
                .Where(GatewayBatchIntentRules.UnclaimedForBatch(companyId, providerCode, null, from))
                .CountAsync(ct);
        // อยู่ในรอบโอนของเส้นนี้แล้ว แต่คืนเงินภายหลัง — ผู้ให้บริการหักจากรอบถัดไป (ของเส้นเดิมไม่เข้า — เส้นเดิมหักเอง)
        var late = await _db.PaymentIntents.AsNoTracking()
            .Where(GatewayBatchIntentRules.LateRefundInBatch(companyId, providerCode))
            .Select(i => new GatewayIntentRow(i.Id, i.ProviderRef, i.Amount, i.RefundedAmount, i.FeeActual, i.FeeEstimated,
                i.ConfirmedAt, i.LastRefundedAt, i.RefundOutcomeUnknownSince != null))
            .ToListAsync(ct);
        // R-B2/R-B17: ยอดคืนที่ถูกนับแล้วในบรรทัดคืนเงินของทุกช่องทาง (intent เป็นของบริษัท ไม่ใช่ของช่องทาง)
        var refundInLines = await RefundInLinesAsync(companyId, late.Select(x => x.Id).ToList(), ct);

        // ยอดคืน ณ วันเงินเข้า (ฝ่ายค้าน R-E2) — สูตรเดียวกับเส้นเดิม: คืนครั้งล่าสุดก่อนจุดตัด = ยอดสะสม · ไม่งั้นต้องมียอดรายครั้งครบ
        var cutoff = GatewaySettlementMath.RefundCutoffUtc(payoutDate);
        var needTimeline = fresh.Concat(late)
            .Where(i => i.RefundedAmount > 0m && (i.LastRefundedAt == null || i.LastRefundedAt >= cutoff))
            .Select(i => i.Id).Distinct().ToList();
        var timeline = new Dictionary<Guid, List<GatewayRefundEntry>>();
        if (needTimeline.Count > 0)
            timeline = (await _db.PaymentIntentEvents.AsNoTracking()
                    .Where(e => e.CompanyId == companyId && needTimeline.Contains(e.IntentId) && e.RefundAmount != null)
                    .Select(e => new { e.IntentId, e.At, Amount = e.RefundAmount!.Value })
                    .ToListAsync(ct))
                .GroupBy(e => e.IntentId)
                .ToDictionary(g => g.Key, g => g.Select(e => new GatewayRefundEntry(e.At, e.Amount)).ToList());
        GatewayRefundAsOf AsOf(GatewayIntentRow i)
            => GatewaySettlementMath.RefundedAsOf(i.RefundedAmount, i.LastRefundedAt,
                timeline.TryGetValue(i.Id, out var list) ? list : new List<GatewayRefundEntry>(), cutoff);

        var freshAsOf = fresh.Select(i => (Row: i, AsOf: AsOf(i))).ToList();
        var lateAsOf = late.Select(i => (Row: i, AsOf: AsOf(i), InLines: refundInLines.GetValueOrDefault(i.Id)))
            .Where(x => x.Row.RefundedAmount - x.InLines > 0m)
            .ToList();
        // แยกไม่ได้ว่ายอดคืนส่วนไหนเกิดก่อนวันเงินเข้า ⇒ บล็อกทั้งรอบ (ตัวเดียวกับที่เส้นเดิมบล็อก — ห้ามเดา)
        var timingUnknown = freshAsOf.Count(x => !x.AsOf.Known) + lateAsOf.Count(x => !x.AsOf.Known);
        if (timingUnknown > 0)
            throw new BusinessRuleException(GatewayBatchIntentRules.RefundTimingRefusal(timingUnknown), "SETTLEMENT-REFUND-TIMING-UNKNOWN");

        var snaps = freshAsOf.Select(x => new SettlementIntentSnapshot(x.Row.Id, x.Row.ProviderRef, x.Row.Amount, x.AsOf.Amount,
                x.Row.FeeActual, x.Row.FeeEstimated, x.Row.ConfirmedAt, false, 0m))
            .Concat(lateAsOf.Select(x => new SettlementIntentSnapshot(x.Row.Id, x.Row.ProviderRef, x.Row.Amount, x.AsOf.Amount,
                x.Row.FeeActual, x.Row.FeeEstimated, x.Row.ConfirmedAt, true, x.InLines)));
        var refundUnknown = fresh.Count(i => i.OutcomeUnknown) + lateAsOf.Count(x => x.Row.OutcomeUnknown);
        return new GatewayIntentLoad(PaymentIntentAdapter.BuildRows(snaps, feeVatMode), refundUnknown, olderUnclaimed);
    }

    /// <summary>Σ |ยอด| ของบรรทัดคืนเงินที่อ้าง intent เหล่านี้ในทุกรอบโอน/ทุกช่องทางของบริษัท (บรรทัดที่ยกเลิกแล้วไม่นับ — query filter)</summary>
    private async Task<Dictionary<Guid, decimal>> RefundInLinesAsync(Guid companyId, IReadOnlyList<Guid> intentIds, CancellationToken ct)
    {
        if (intentIds.Count == 0) return new Dictionary<Guid, decimal>();
        var ids = intentIds.Distinct().ToList();
        return (await _db.SettlementLines.AsNoTracking()
                .Where(l => l.CompanyId == companyId && l.PaymentIntentId != null
                    && ids.Contains(l.PaymentIntentId.Value) && l.LineType == SettlementLineType.Refund)
                .Select(l => new { Id = l.PaymentIntentId!.Value, l.Amount })
                .ToListAsync(ct))
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => -g.Sum(x => x.Amount));
    }

    /// <summary>
    /// ตรวจซ้ำใต้ล็อก (<c>PersistAsync</c> เรียกหลังถือล็อกช่องทาง + ล็อก gateway): บรรทัดคืนเงินที่จะเพิ่ม + ที่มีอยู่แล้วของ intent เดียวกัน
    /// ต้องไม่เกินยอดคืนผ่านระบบ (<see cref="GatewayBatchIntentRules.RefundLinesOverRefunded"/>) — สองคำขอพร้อมกันจากสองช่องทางที่ผูก config
    /// เดียวกันอ่าน "ยอดที่อยู่ในบรรทัดแล้ว" นอกล็อกทั้งคู่ ⇒ ล้มดังให้กดใหม่ ไม่ใช่หักผังพักสองครั้ง
    /// </summary>
    private async Task EnsureIntentRefundCapacityAsync(Guid companyId, IReadOnlyList<SettlementParsedRow> rows, CancellationToken ct)
    {
        var refundRows = rows.Where(r => r.PaymentIntentId != null && r.ExplicitType == SettlementLineType.Refund)
            .Select(r => (IntentId: r.PaymentIntentId!.Value, r.Amount)).ToList();
        if (refundRows.Count == 0) return;
        var ids = refundRows.Select(r => r.IntentId).Distinct().ToList();
        var refunded = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && ids.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.RefundedAmount, ct);
        var over = GatewayBatchIntentRules.RefundLinesOverRefunded(refunded, await RefundInLinesAsync(companyId, ids, ct), refundRows);
        if (over.Count > 0)
            throw new BusinessRuleException(
                $"ยอดคืนเงินของรายการรับชำระ {over.Count} รายการถูกนับในรอบโอนอื่นไปแล้วระหว่างที่ระบบเตรียมรอบนี้ — กดประกอบรอบโอนใหม่อีกครั้ง",
                "SETTLEMENT-INTENT-TAKEN");
    }
}

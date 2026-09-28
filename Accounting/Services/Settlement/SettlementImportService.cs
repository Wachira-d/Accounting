using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Settlement;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Ai.Prompts;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Accounting.Services.Settlement.Adapters;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Settlement;

/// <summary>
/// **นำเข้ารอบโอน settlement** (รอบ 198 เฟส 1 ทีม B · report-S2 §3 · DECISIONS ข้อ 1–4) — ดู <see cref="ISettlementImportService"/>
///
/// <para>═══ ลำดับของการนำเข้า 1 ครั้ง ═══
/// <list type="number">
/// <item>อ่านไฟล์ทั้งไฟล์ด้วย adapter — อ่านไม่ได้ = ล้มดังทั้งไฟล์ (ไม่มีอะไรถูกบันทึก)</item>
/// <item>คีย์กันซ้ำ (<see cref="SettlementTxnKey"/>) · ตัดแถวที่มีในช่องทางนี้แล้ว (อ่านอย่างเดียว — ตรวจซ้ำอีกครั้งใต้ล็อก)</item>
/// <item>ตัด PII จากข้อความรายการ (<see cref="SettlementPiiScrubber"/>) ก่อนเก็บและก่อนป้อนตัวจัดประเภท</item>
/// <item>จัดประเภท: adapter → คลังที่เรียนต่อช่องทาง → seed → ครู/นักเรียน (<see cref="SettlementLineClassification"/> ·
/// <b>นอกธุรกรรม</b> — ห้ามถือล็อกระหว่างรอ AI)</item>
/// <item>ธุรกรรมเดียว + ล็อกต่อช่องทาง: ตรวจซ้ำใต้ล็อก → รอบโอนเดิม (เติม) หรือใหม่ → บรรทัด → จับคู่ใบขาย (อ่านอย่างเดียว) →
/// ประทับ PaymentIntent → ไฟล์ต้นฉบับ (attachment abstraction) → จำการจับคู่คอลัมน์ → audit (hash chain) → commit</item>
/// </list></para>
/// </summary>
public sealed partial class SettlementImportService : ISettlementImportService
{
    /// <summary>ชนิดเจ้าของไฟล์แนบของรอบโอน — ด่านอ่าน/ลบอยู่ใน <c>AttachmentPermissionScope</c> (ผ่าน <c>IAttachmentAccessGate</c>)</summary>
    public const string AttachmentEntityType = "SettlementBatch";
    /// <summary>ป้าย+เครื่องหมายที่ถามครู/นักเรียนได้สูงสุดต่อการนำเข้า 1 ครั้ง (กันไฟล์ขยะเผาโควตา) — ที่เหลือ = Unclassified + แจ้ง</summary>
    private const int MaxAiLabelsPerImport = 25;

    private readonly AccountingDbContext _db;
    private readonly IAiOrchestrator _ai;
    private readonly IAiFeedbackRecorder _recorder;
    private readonly IFileAttachmentService _files;
    private readonly IEnumerable<ISettlementReportAdapter> _adapters;
    private readonly IGatewayAccountResolver _gatewayAccounts;
    private readonly ILogger<SettlementImportService> _logger;

    public SettlementImportService(AccountingDbContext db, IAiOrchestrator ai, IAiFeedbackRecorder recorder,
        IFileAttachmentService files, IEnumerable<ISettlementReportAdapter> adapters, IGatewayAccountResolver gatewayAccounts,
        ILogger<SettlementImportService> logger)
    {
        _db = db; _ai = ai; _recorder = recorder; _files = files; _adapters = adapters; _gatewayAccounts = gatewayAccounts;
        _logger = logger;
    }

    // ═══════════════════ ตรวจไฟล์ ═══════════════════

    public async Task<SettlementFileInspection> InspectFileAsync(Guid companyId, Guid channelId, string fileName, Stream content,
        CancellationToken ct = default)
    {
        var channel = await LoadChannelAsync(companyId, channelId, tracked: false, ct);
        var file = new SettlementFileInput(fileName, await ReadBoundedAsync(content, ct));
        try
        {
            var (headers, samples) = GenericColumnMapAdapter.Peek(file);
            var adapter = ResolveFileAdapter(channel);
            var match = adapter.Detect(headers, samples, channel);
            SettlementColumnMap map;
            IReadOnlyList<string> unmapped, issues;
            var fromSaved = match >= 1m;
            if (fromSaved)
            {
                map = SettlementColumnMap.Parse(channel.ColumnMapJson)!;
                unmapped = Array.Empty<string>();
                issues = map.Validate();
            }
            else
            {
                var s = GenericColumnMapAdapter.Suggest(headers, samples, channel.Kind);
                map = s.Map; unmapped = s.UnmappedHeaders; issues = s.Issues;
            }
            var ignored = new HashSet<string>(map.Ignore.Select(SettlementColumnMap.NormalizeHeader), StringComparer.Ordinal);
            var safeSamples = samples.Select(r => (IReadOnlyList<string>)r.Select((c, i) =>
                    i < headers.Count && ignored.Contains(SettlementColumnMap.NormalizeHeader(headers[i]))
                        ? "[ไม่ใช้]"
                        : SettlementPiiScrubber.Scrub(c) ?? "")
                .ToList()).ToList();
            return new SettlementFileInspection(headers, safeSamples, map.ToJson(), fromSaved, match, unmapped, issues);
        }
        catch (SettlementFormatException ex)
        {
            throw FormatError(ex);
        }
    }

    // ═══════════════════ นำเข้าไฟล์ ═══════════════════

    public async Task<SettlementImportResult> ImportFileAsync(Guid companyId, Guid userId, SettlementFileImportRequest request,
        string fileName, Stream content, CancellationToken ct = default)
    {
        var h = request.Header;
        var channel = await LoadChannelAsync(companyId, h.ChannelId, tracked: false, ct);
        EnsureActive(channel);
        var file = new SettlementFileInput(fileName, await ReadBoundedAsync(content, ct));
        var adapter = ResolveFileAdapter(channel);
        var mapJson = string.IsNullOrWhiteSpace(request.ColumnMapJson) ? channel.ColumnMapJson : request.ColumnMapJson;
        SettlementParseResult parsed;
        try
        {
            parsed = adapter.Parse(file, channel, mapJson);
        }
        catch (SettlementFormatException ex)
        {
            throw FormatError(ex);
        }
        var remember = request.RememberColumnMap && !string.IsNullOrWhiteSpace(request.ColumnMapJson)
            ? SettlementColumnMap.Parse(request.ColumnMapJson)!.ToJson()
            : null;
        return await PersistAsync(new PersistInput(companyId, userId, channel.Id, h, parsed.Rows, parsed.SkippedRows,
            SettlementSourceKind.CsvImport, file, remember, adapter.Code, Array.Empty<Guid>(), null, new List<string>()), ct);
    }

    // ═══════════════════ ประกอบจาก PaymentIntent ═══════════════════

    public async Task<SettlementImportResult> ImportFromPaymentIntentsAsync(Guid companyId, Guid userId,
        SettlementIntentBatchRequest request, CancellationToken ct = default)
    {
        var h = request.Header;
        var channel = await LoadChannelAsync(companyId, h.ChannelId, tracked: false, ct);
        EnsureActive(channel);
        if (channel.Kind != SettlementChannelKind.Gateway || channel.PaymentProviderConfigId is not Guid cfgId)
            throw new BusinessRuleException(
                "ช่องทางนี้ไม่ได้ผูกกับ payment gateway ในระบบ — ประกอบรอบโอนจากรายการรับชำระได้เฉพาะช่องทางชนิด Gateway ที่ผูกการตั้งค่า gateway แล้ว "
                + "(ช่องทางอื่นให้นำเข้าไฟล์ settlement report แทน)", "SETTLEMENT-NOT-GATEWAY");
        var providerCode = await _db.PaymentProviderConfigs.AsNoTracking()
            .Where(c => c.Id == cfgId && c.CompanyId == companyId)
            .Select(c => c.ProviderCode).FirstOrDefaultAsync(ct)
            ?? throw new BusinessRuleException("ไม่พบการตั้งค่า gateway ที่ช่องทางนี้ผูกไว้ — แก้การผูกในหน้าตั้งค่าช่องทาง", "SETTLEMENT-GATEWAY-MISSING", 404);

        // R-A1: บรรทัดที่พก PaymentIntentId ถูกนับว่า "อยู่ในผังพักแล้ว" — จริงเฉพาะเมื่อผังพักของช่องทาง = ผังที่ขาเงินเข้าของ intent ลงไว้
        if (!await GatewayClearingMatchesAsync(companyId, channel, providerCode, ct))
            throw new BusinessRuleException(
                "ผังพักของช่องทางนี้ไม่ตรงกับผังที่ gateway ลงรับเงินไว้ (หรือยังไม่ได้ผูก) — บันทึกหน้าตั้งค่าช่องทางอีกครั้งเพื่อให้ระบบผูกผังเดียวกับ gateway "
                + "ก่อนประกอบรอบโอน (ไม่งั้นยอดรับชำระกับยอดโอนจะอยู่คนละผังตลอดไป)", "SETTLEMENT-GATEWAY-CLEARING-MISMATCH");
        var (rows, refundUnknown) = await LoadIntentRowsAsync(companyId, channel.Id, providerCode, h.PeriodTo, ct);
        var warnings = new List<string>();
        // review198-E2 E2-10: การคืนเงินผลไม่แน่ชัด — ประกอบรอบโอนได้ (เห็นยอด) แต่ลงบัญชีไม่ได้จนกว่าจะตรวจผล (ด่านผู้ลงบัญชี RefundOutcomeUnknown)
        if (refundUnknown > 0)
            warnings.Add($"รายการรับชำระ {refundUnknown} รายการมีการคืนเงินที่ผลยังไม่แน่ชัด — ตรวจผลการคืนเงินกับผู้ให้บริการก่อน "
                + "(รอบโอนนี้จะลงบัญชีไม่ได้จนกว่าจะตรวจผลแล้ว)");
        if (rows.FeeUnknownCount > 0)
            warnings.Add($"รายการรับชำระ {rows.FeeUnknownCount} รายการยังไม่รู้ค่าธรรมเนียมจริง (ไม่มีบรรทัดค่าธรรมเนียม) — "
                + "ยอดรอบโอนจะไม่ลงตัวจนกว่าจะแก้ค่าธรรมเนียมหรือเพิ่มบรรทัดปรับปรุงที่มีเหตุผล");
        if (rows.Rows.Count == 0)
            throw new BusinessRuleException("ไม่มีรายการรับชำระที่รอเข้ารอบโอนของ gateway นี้ (หรือถูกบันทึกรอบโอนด้วยเส้นเดิมแล้ว)",
                "SETTLEMENT-NO-INTENTS");
        return await PersistAsync(new PersistInput(companyId, userId, channel.Id, h, rows.Rows, Array.Empty<string>(),
            SettlementSourceKind.PaymentIntents, null, null, PaymentIntentAdapter.AdapterCode, rows.NewIntentIds, providerCode,
            warnings), ct);
    }

    /// <returns>บรรทัดจาก intent + จำนวน intent ที่การคืนเงินผลยังไม่แน่ชัด (E2-10)</returns>
    private async Task<(SettlementIntentRows Rows, int RefundUnknown)> LoadIntentRowsAsync(Guid companyId, Guid channelId,
        string providerCode, DateTime? periodTo, CancellationToken ct)
    {
        var to = periodTo is DateTime pt ? ThaiDate.CalendarDateUtc(pt).AddDays(1) : (DateTime?)null;
        // เงื่อนไขเดียวกับ GatewaySettlementService.SelectCandidatesAsync (ทีม E) + ยังไม่อยู่ในรอบโอนใด · ไม่แตะรายการที่เส้นเดิมบันทึกรอบโอนแล้ว
        var fresh = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.ProviderCode == providerCode
                && i.SettlementJournalEntryId == null && i.SettlementBatchId == null && i.ConfirmedAt != null
                && (i.Status == PaymentIntentStatus.Succeeded
                    || ((i.Status == PaymentIntentStatus.PartiallyRefunded || i.Status == PaymentIntentStatus.Refunded)
                        && i.RefundedAmount > 0m))
                && (to == null || i.ConfirmedAt < to))
            .Select(i => new { i.Id, i.ProviderRef, i.Amount, i.RefundedAmount, i.FeeActual, i.FeeEstimated, i.ConfirmedAt,
                Unknown = i.RefundOutcomeUnknownSince != null })
            .ToListAsync(ct);
        // อยู่ในรอบโอนของเส้นใหม่แล้ว แต่คืนเงินภายหลัง — ผู้ให้บริการหักจากรอบถัดไป
        var late = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.ProviderCode == providerCode
                && i.SettlementJournalEntryId == null && i.SettlementBatchId != null && i.RefundedAmount > 0m)
            .Select(i => new { i.Id, i.ProviderRef, i.Amount, i.RefundedAmount, i.FeeActual, i.FeeEstimated, i.ConfirmedAt,
                Unknown = i.RefundOutcomeUnknownSince != null })
            .ToListAsync(ct);
        var lateIds = late.Select(x => x.Id).ToList();
        // R-B2/R-B17: ยอดคืนที่ถูกนับแล้วในบรรทัดคืนเงินของทุกช่องทาง (intent เป็นของบริษัท ไม่ใช่ของช่องทาง) — ไม่งั้นคืนเงินเดียวกันเข้าสองช่องทาง
        var refundInLines = lateIds.Count == 0
            ? new Dictionary<Guid, decimal>()
            : (await _db.SettlementLines.AsNoTracking()
                    .Where(l => l.CompanyId == companyId && l.PaymentIntentId != null
                        && lateIds.Contains(l.PaymentIntentId.Value) && l.LineType == SettlementLineType.Refund)
                    .Select(l => new { Id = l.PaymentIntentId!.Value, l.Amount })
                    .ToListAsync(ct))
                .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => -g.Sum(x => x.Amount));

        var snaps = fresh.Select(i => new SettlementIntentSnapshot(i.Id, i.ProviderRef, i.Amount, i.RefundedAmount, i.FeeActual,
                i.FeeEstimated, i.ConfirmedAt, false, 0m))
            .Concat(late.Select(i => new SettlementIntentSnapshot(i.Id, i.ProviderRef, i.Amount, i.RefundedAmount, i.FeeActual,
                i.FeeEstimated, i.ConfirmedAt, true, refundInLines.GetValueOrDefault(i.Id))));
        return (PaymentIntentAdapter.BuildRows(snaps), fresh.Count(i => i.Unknown)
            + late.Count(i => i.Unknown && i.RefundedAmount - refundInLines.GetValueOrDefault(i.Id) > 0m));
    }

    // ═══════════════════ บันทึก (ร่วมทุกทางเข้า) ═══════════════════

    private sealed record PersistInput(
        Guid CompanyId, Guid UserId, Guid ChannelId, SettlementBatchHeaderRequest Header,
        IReadOnlyList<SettlementParsedRow> Rows, IReadOnlyList<string> SkippedRows, SettlementSourceKind SourceKind,
        SettlementFileInput? File, string? RememberMapJson, string AdapterCode, IReadOnlyList<Guid> FreshIntentIds,
        string? GatewayProviderCode, List<string> Warnings);

    /// <summary>บรรทัดที่เตรียมไว้ (ยังไม่ใช่ entity — สร้าง entity ใต้ธุรกรรมเท่านั้น)</summary>
    private sealed class PreparedLine
    {
        public required SettlementParsedRow Row { get; init; }
        public required string Key { get; init; }
        public string? Label { get; init; }
        public string? Description { get; init; }
        public SettlementLineType Type { get; set; } = SettlementLineType.Unclassified;
        public SettlementClassifiedBy By { get; set; } = SettlementClassifiedBy.None;
        public Guid? FeedbackId { get; set; }
        public bool UsedAi { get; set; }
    }

    private async Task<SettlementImportResult> PersistAsync(PersistInput input, CancellationToken ct)
    {
        var (companyId, userId, channelId, h) = (input.CompanyId, input.UserId, input.ChannelId, input.Header);
        var warnings = input.Warnings;

        // ── 1. หัวรอบโอน — ยอดเข้าธนาคารจริงเป็นตัวตั้ง ระบบไม่เดา ──
        if (h.PayoutDate is not DateTime payoutDateRaw)
            throw new BusinessRuleException("ระบุวันที่เงินเข้าธนาคารของรอบโอนนี้", "SETTLEMENT-PAYOUT-DATE");
        if (h.NetPayout is not decimal netRaw)
            throw new BusinessRuleException("ระบุยอดที่โอนเข้าธนาคารจริงของรอบโอนนี้ (0 ได้ถ้ารอบนี้ไม่มีการโอน) — ระบบใช้ยอดนี้เป็นตัวตั้ง ไม่เดาจากไฟล์",
                "SETTLEMENT-NET-PAYOUT");
        if (netRaw < 0m)
            throw new BusinessRuleException("ยอดโอนเข้าธนาคารติดลบไม่ได้ — ถ้าแพลตฟอร์มหักเกินยอดใน wallet ให้ใส่ยอดโอน 0 และยอด wallet ปลายรอบติดลบ",
                "SETTLEMENT-NET-NEGATIVE");
        var (payoutRef, rows) = SelectPayout(h.PayoutRef, input.Rows);
        if (h.BankAccountId is Guid bankId
            && !await _db.BankAccounts.AsNoTracking().AnyAsync(b => b.Id == bankId && b.CompanyId == companyId, ct))
            throw new BusinessRuleException("ไม่พบบัญชีธนาคารที่เลือกในบริษัทนี้", "SETTLEMENT-BANK", 404);

        // ── 2. คีย์กันซ้ำ + ตัดแถวที่มีแล้ว (อ่านอย่างเดียว — ตรวจซ้ำใต้ล็อก) ──
        // คีย์รุ่น v2 (R-B5/R-B6): ป้ายถูกแฮชในคีย์ (ไม่มี PII · ไม่ขึ้นกับตัวตัด PII) · ใส่ยอด+วันที่ · แถวไม่มี id ใส่รอบโอนของแถว
        var keys = SettlementTxnKey.Assign(rows
            .Select(r => new SettlementTxnKeyInput(r.RawTxnId, r.RawTypeLabel, r.ExternalOrderId, r.Amount, r.TxnDate, payoutRef)).ToList());
        var existing = await ExistingKeysAsync(companyId, channelId, keys, ct);
        var prepared = rows.Select((r, i) => new PreparedLine
            {
                Row = r,
                Key = keys[i],
                // ป้ายเป็นอินพุตของ AI ด้วย — ตัด PII ก่อนทั้งเก็บและถาม
                Label = Fit(SettlementPiiScrubber.Scrub(r.RawTypeLabel), 200),
                Description = SettlementPiiScrubber.Scrub(r.Description),
            })
            .Where(p => !existing.Contains(p.Key)).ToList();

        // ── 3. จัดประเภท (นอกธุรกรรม — ครูอาจใช้เวลาหลายวินาที ห้ามถือล็อกไว้) ──
        var channelForClassify = await LoadChannelAsync(companyId, channelId, tracked: false, ct);
        await ClassifyAsync(companyId, channelForClassify, prepared, warnings, ct);

        // ── 4. ธุรกรรมเดียว + ล็อกต่อช่องทาง ──
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            // ล็อกต่อช่องทางตัวเดียวกับผู้ลงบัญชี (C-1) — ก่อนอ่านอะไรใต้ธุรกรรม (R-B3)
            await LockChannelAsync(companyId, channelId, ct);
            if (input.GatewayProviderCode is string pc)
                // ล็อกเดียวกับเส้นบันทึกรอบโอนเดิม (GatewaySettlementService) — สองเส้นห้ามหยิบ intent ชุดเดียวกันพร้อมกัน
                await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                    new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.GatewaySettlement, pc) }, ct);

            var channel = await LoadChannelAsync(companyId, channelId, tracked: true, ct);
            if (input.FreshIntentIds.Count > 0)
            {
                // อ่าน intent นอกล็อก ⇒ ตรวจซ้ำใต้ล็อกว่ายังไม่มีเส้นไหนหยิบไป (รอบโอนอื่น · เส้นบันทึกรอบโอนเดิม)
                var ids = input.FreshIntentIds.ToList();
                var taken = await _db.PaymentIntents.AsNoTracking().CountAsync(i => i.CompanyId == companyId && ids.Contains(i.Id)
                    && (i.SettlementBatchId != null || i.SettlementJournalEntryId != null), ct);
                if (taken > 0)
                    throw new BusinessRuleException(
                        $"รายการรับชำระ {taken} รายการถูกบันทึกรอบโอนไปแล้วระหว่างที่ระบบเตรียมรอบนี้ — กดประกอบรอบโอนใหม่อีกครั้ง",
                        "SETTLEMENT-INTENT-TAKEN");
            }
            var underLock = await ExistingKeysAsync(companyId, channelId, prepared.Select(p => p.Key).ToList(), ct);
            var toAdd = prepared.Where(p => !underLock.Contains(p.Key)).ToList();
            var skippedDup = rows.Count - toAdd.Count;

            var batch = await _db.SettlementBatches
                .FirstOrDefaultAsync(b => b.CompanyId == companyId && b.ChannelId == channelId && b.PayoutRef == payoutRef, ct);
            var created = batch == null;
            // C-1(b): รอบที่ลงบัญชีค้างครึ่งทาง (มีเอกสาร/การรับชำระของการลงบัญชีแล้ว) เติมบรรทัดไม่ได้เหมือนรอบที่ลงบัญชีแล้ว
            var artifacts = batch == null ? new List<string>() : await PostingArtifactsAsync(companyId, batch.Id, ct);
            if (batch != null && !SettlementSaleMatch.IsEditable(batch.Status, artifacts.Count))
            {
                if (toAdd.Count > 0)
                    throw new BusinessRuleException(SettlementSaleMatch.IsEditable(batch.Status)
                        ? $"รอบโอน \"{payoutRef}\" ลงบัญชีค้างครึ่งทาง (มี {string.Join(", ", artifacts.Take(10))}) แต่ไฟล์มีรายการใหม่ {toAdd.Count} รายการ — "
                          + "กด \"ลงบัญชี\" รอบนี้ต่อให้ครบแล้วนำเข้ารายการใหม่เป็นรอบโอนแยก หรือยกเลิกเอกสาร/การรับชำระเหล่านั้นก่อน"
                        : $"รอบโอน \"{payoutRef}\" ลงบัญชีแล้ว แต่ไฟล์มีรายการใหม่ {toAdd.Count} รายการ — แพลตฟอร์มอาจแก้รายงานย้อนหลัง · "
                          + "นำเข้ารายการใหม่เป็นรอบโอนแยก (เลขรอบโอนอื่น) หรือกลับรายการรอบนี้ก่อน", "SETTLEMENT-BATCH-POSTED");
            }
            // R-B5: แถวที่ถูกข้ามเพราะมีอยู่แล้ว ต้องบอกเป็นรายแถว (เดิมบอกแค่จำนวน — แถวจริงที่ชนคีย์หายเงียบ)
            if (skippedDup > 0 && toAdd.Count > 0)
            {
                var addKeys = toAdd.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
                var skipped = rows.Select((r, i) => (r.SourceRow, Key: keys[i])).Where(x => !addKeys.Contains(x.Key)).ToList();
                var refs = await BatchRefsOfKeysAsync(companyId, channelId, skipped.Select(x => x.Key).ToList(), ct);
                warnings.Add($"ข้าม {skippedDup} แถวที่นำเข้าแล้ว (แถวที่ {string.Join(", ", skipped.Take(20).Select(x => x.SourceRow))}"
                    + $"{(skipped.Count > 20 ? " …" : "")} · อยู่ในรอบโอน {string.Join(", ", refs)}) — ถ้าเป็นรายการใหม่จริง ตรวจว่าไฟล์ใส่เลขรายการซ้ำกับรอบก่อนหรือไม่");
            }
            if (batch == null && toAdd.Count == 0)
            {
                var refs = await BatchRefsOfKeysAsync(companyId, channelId, keys, ct);
                throw new BusinessRuleException(
                    $"ทุกรายการในไฟล์ถูกนำเข้าแล้ว (รอบโอน {string.Join(", ", refs)}) — ไม่มีอะไรใหม่ให้สร้างรอบโอน \"{payoutRef}\" · "
                    + "ถ้าต้องการนำเข้าใหม่ ให้ยกเลิกรอบโอนเดิมก่อน", "SETTLEMENT-ALL-DUPLICATE");
            }

            if (batch != null && toAdd.Count == 0)
            {
                // นำเข้าไฟล์เดิมซ้ำ — ไม่มีอะไรเปลี่ยน (idempotent) · ห้ามแตะสถานะ/หัวของรอบที่ลงบัญชีแล้ว
                await tx.RollbackAsync(ct);
                var same = await _db.SettlementLines.AsNoTracking()
                    .Where(l => l.CompanyId == companyId && l.BatchId == batch.Id).OrderBy(l => l.Seq).ToListAsync(ct);
                warnings.Add($"ทุกรายการในไฟล์มีอยู่แล้วในรอบโอน \"{payoutRef}\" ({skippedDup} รายการ) — ไม่มีอะไรเปลี่ยน");
                return new SettlementImportResult(await BuildBatchViewAsync(companyId, channel, batch, same, ct), false, 0, skippedDup,
                    input.SkippedRows, warnings);
            }

            if (batch == null)
            {
                batch = new SettlementBatch
                {
                    CompanyId = companyId,
                    ChannelId = channelId,
                    PayoutRef = payoutRef,
                    PayoutDate = ThaiDate.CalendarDateUtc(payoutDateRaw),
                    PeriodFrom = ThaiDate.CalendarDateUtc(h.PeriodFrom),
                    PeriodTo = ThaiDate.CalendarDateUtc(h.PeriodTo),
                    Currency = channel.Currency,
                    OpeningWalletBalance = R(h.OpeningWalletBalance),
                    ClosingWalletBalance = R(h.ClosingWalletBalance),
                    NetPayout = R(netRaw),
                    Status = SettlementBatchStatus.Imported,
                    SourceKind = input.SourceKind,
                    BankAccountId = h.BankAccountId,
                    Note = Fit(h.Note, 2000),
                    CreatedBy = userId.ToString(),
                };
                _db.SettlementBatches.Add(batch);
            }
            else if (R(netRaw) != batch.NetPayout || ThaiDate.CalendarDateUtc(payoutDateRaw) != batch.PayoutDate)
                warnings.Add($"รอบโอน \"{payoutRef}\" มีอยู่แล้ว — ใช้ยอดโอน/วันที่เดิม ({batch.NetPayout:N2} · {batch.PayoutDate:yyyy-MM-dd}) "
                    + "ไม่ใช่ค่าที่ส่งมาครั้งนี้ · แก้หัวรอบโอนได้จากหน้ารอบโอน");

            var batchLines = created
                ? new List<SettlementLine>()
                : await _db.SettlementLines.Where(l => l.CompanyId == companyId && l.BatchId == batch.Id).ToListAsync(ct);
            var seq = batchLines.Count == 0 ? 0 : batchLines.Max(l => l.Seq);
            var newLines = new List<SettlementLine>();
            foreach (var p in toAdd)
            {
                var line = new SettlementLine
                {
                    CompanyId = companyId,
                    BatchId = batch.Id,
                    ChannelId = channelId,
                    Seq = ++seq,
                    LineType = p.Type,
                    ClassifiedBy = p.By,
                    ClassifyAiFeedbackId = p.FeedbackId,
                    ClassifyUsedAi = p.UsedAi,
                    Description = p.Description,
                    RawTypeLabel = p.Label,
                    TxnDate = p.Row.TxnDate,
                    ExternalOrderId = Fit(p.Row.ExternalOrderId?.Trim(), 200),
                    ExternalTxnId = p.Key,
                    Amount = R(p.Row.Amount),
                    VatAmount = p.Row.VatAmount is decimal v ? R(v) : null,
                    WhtAmount = p.Row.WhtAmount is decimal w ? R(w) : null,
                    PaymentIntentId = p.Row.PaymentIntentId,
                    MatchStatus = SettlementMatchStatus.Unmatched,
                    CreatedBy = userId.ToString(),
                };
                newLines.Add(line);
                _db.SettlementLines.Add(line);
            }
            batchLines.AddRange(newLines);

            // ── จับคู่ใบขาย (อ่านอย่างเดียว) เฉพาะบรรทัดใหม่ — ยอดของออเดอร์นับทั้งรอบโอน ──
            await MatchLinesAsync(companyId, channel, batchLines, newLines, apply: true, ct);
            await SyncIntentStampsAsync(companyId, batch.Id, batchLines, ct);

            if (input.RememberMapJson != null)
            {
                channel.ColumnMapJson = input.RememberMapJson;
                channel.AdapterCode = input.AdapterCode;
                channel.UpdatedAt = DateTime.UtcNow;
                channel.UpdatedBy = userId.ToString();
            }
            batch.Status = SettlementSaleMatch.DeriveImportStatus(batchLines.Select(l => (l.LineType, l.MatchStatus)));
            batch.UpdatedAt = DateTime.UtcNow;

            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = userId,
                EntityType = nameof(SettlementBatch),
                EntityId = batch.Id.ToString(),
                Action = created ? AuditAction.Create : AuditAction.Update,
                NewValues = JsonSerializer.Serialize(new
                {
                    action = "settlement-import",
                    payoutRef,
                    source = input.SourceKind.ToString(),
                    adapter = input.AdapterCode,
                    file = input.File?.FileName,
                    imported = newLines.Count,
                    skippedDuplicates = skippedDup,
                    netPayout = batch.NetPayout,
                }),
                Timestamp = DateTime.UtcNow,
            });

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                // ตาข่ายชั้นสุดท้าย (ล็อกต่อช่องทางกันไว้แล้ว) — ห้ามปล่อยเป็น HTTP 500 ข้อความฐานข้อมูล (R-A9)
                throw new BusinessRuleException(
                    $"รอบโอน \"{payoutRef}\" หรือบางรายการในไฟล์ถูกนำเข้าไปแล้วพร้อมกันจากอีกคำขอ — เปิดรายการรอบโอนเพื่อตรวจ แล้วนำเข้าใหม่ถ้ายังขาด "
                    + "(ระบบกันรายการซ้ำด้วยเลขรายการของแพลตฟอร์ม)", ex, "SETTLEMENT-DUPLICATE");
            }
            // ไฟล์ต้นฉบับ (หลักฐาน 5 ปี · ด่านอ่าน/ลบ = AttachmentPermissionScope "SettlementBatch") — หลังบันทึกบรรทัดผ่านแล้ว
            // ในธุรกรรมเดียวกัน: แถวไฟล์แนบ rollback ไปพร้อมกันถ้าล้ม (ตัวไฟล์ที่เขียนแล้วค้าง = ความเสี่ยงที่รู้ · ไม่ใช่ยอดเงิน)
            if (input.File is SettlementFileInput file && newLines.Count > 0)
            {
                var att = await _files.UploadBytesAsync(companyId, AttachmentEntityType, batch.Id, file.FileName,
                    file.IsExcel ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/csv",
                    file.Content, userId);
                if (batch.SourceFileAttachmentId == null)
                {
                    batch.SourceFileAttachmentId = att.Id;
                    await _db.SaveChangesAsync(ct);
                }
            }
            await tx.CommitAsync(ct);

            var unclassified = newLines.Count(l => l.LineType == SettlementLineType.Unclassified);
            if (unclassified > 0)
                warnings.Add($"{unclassified} บรรทัดยังไม่รู้ประเภท — เลือกประเภทเองก่อนลงบัญชี (ระบบจะจำป้ายนี้ให้ครั้งหน้า)");
            var view = await BuildBatchViewAsync(companyId, channel, batch, batchLines, ct);
            return new SettlementImportResult(view, created, newLines.Count, skippedDup, input.SkippedRows, warnings);
        });
    }

    /// <summary>เลือกรอบโอนของไฟล์: ผู้ใช้ระบุเลขรอบโอน ⇒ ใช้เลขนั้น (ไฟล์มีคอลัมน์เลขรอบโอน ⇒ เฉพาะแถวของรอบนั้น) ·
    /// ไม่ระบุ ⇒ ไฟล์ต้องมีเลขรอบโอนค่าเดียว · หลายค่าโดยไม่ระบุ ⇒ ล้มดังพร้อมรายการเลข</summary>
    private static (string PayoutRef, IReadOnlyList<SettlementParsedRow> Rows) SelectPayout(string? requested,
        IReadOnlyList<SettlementParsedRow> rows)
    {
        var inFile = rows.Select(r => r.PayoutRef?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!)
            .Distinct(StringComparer.Ordinal).ToList();
        var want = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        if (want == null)
        {
            if (inFile.Count == 1) want = inFile[0];
            else if (inFile.Count > 1)
                throw new BusinessRuleException(
                    $"ไฟล์มีหลายรอบโอน ({string.Join(", ", inFile.Take(10))}{(inFile.Count > 10 ? " …" : "")}) — ระบุเลขรอบโอนที่ต้องการนำเข้า "
                    + "(นำเข้าทีละรอบ)", "SETTLEMENT-PAYOUT-MULTI");
            else
                throw new BusinessRuleException("ระบุเลขอ้างอิงรอบโอน (payout ID) ของรอบนี้ — ใช้กันนำเข้ารอบเดียวกันซ้ำ",
                    "SETTLEMENT-PAYOUT-REF");
        }
        if (want.Length > 200) throw new BusinessRuleException("เลขอ้างอิงรอบโอนยาวเกิน 200 ตัวอักษร", "SETTLEMENT-PAYOUT-REF");
        if (inFile.Count == 0) return (want, rows);
        var selected = rows.Where(r => string.Equals(r.PayoutRef?.Trim(), want, StringComparison.Ordinal)).ToList();
        if (selected.Count == 0)
            throw new BusinessRuleException($"ไม่พบแถวของรอบโอน \"{want}\" ในไฟล์ — รอบโอนในไฟล์: {string.Join(", ", inFile.Take(10))}",
                "SETTLEMENT-PAYOUT-NOT-IN-FILE");
        return (want, selected);
    }

    // ═══════════════════ จัดประเภท ═══════════════════

    /// <summary>adapter → คลังที่เรียนต่อช่องทาง → seed → ครู/นักเรียน (ด่าน <see cref="SettlementLineClassification.AcceptModelAnswer"/>) ·
    /// kill-switch / ครูตอบนอกชุด / ต่ำกว่าเกณฑ์ ⇒ คง Unclassified <b>เงียบ</b> (ผู้ใช้เลือกเอง) · FeedbackId เก็บทุกกรณีเพื่อปิดลูปตอนผู้ใช้เลือก</summary>
    private async Task ClassifyAsync(Guid companyId, SettlementChannel channel, IReadOnlyList<PreparedLine> lines,
        List<string> warnings, CancellationToken ct)
    {
        var learned = await LearnedLabelsAsync(companyId, channel.Id, ct);
        var signRejected = 0;
        foreach (var p in lines)
        {
            var norm = SettlementLineClassification.NormalizeLabel(p.Label);
            var learnedType = learned.TryGetValue((norm, p.Row.Amount > 0m), out var lt) ? lt : null;
            var seedType = SettlementLabelSeed.Lookup(p.Label, channel.Kind);
            var local = SettlementLineClassification.ResolveLocal(p.Row.ExplicitType, learnedType, seedType, p.Row.Amount);
            if (p.Row.ExplicitType is SettlementLineType ex && local.Type != ex) signRejected++;
            p.Type = local.Type;
            p.By = local.By;
        }
        if (signRejected > 0)
            warnings.Add($"{signRejected} บรรทัดมีเครื่องหมายยอดไม่เข้ากับประเภทที่กำหนดให้คอลัมน์ — ตรวจตัวเลือก \"กลับเครื่องหมาย\" ของคอลัมน์ยอดเงิน");

        var groups = lines.Where(p => p.Type == SettlementLineType.Unclassified)
            .GroupBy(p => (Label: SettlementLineClassification.NormalizeLabel(p.Label), Positive: p.Row.Amount > 0m))
            .ToList();
        var noLabel = groups.Where(g => g.Key.Label.Length == 0).Sum(g => g.Count());
        if (noLabel > 0) warnings.Add($"{noLabel} บรรทัดไม่มีป้ายประเภทในไฟล์ — เลือกประเภทเอง");
        var asked = 0;
        foreach (var g in groups.Where(g => g.Key.Label.Length > 0))
        {
            if (asked >= MaxAiLabelsPerImport)
            {
                warnings.Add($"ป้ายที่ระบบไม่รู้จักมีมากกว่า {MaxAiLabelsPerImport} แบบ — ป้ายที่เหลือยังไม่ได้จัดประเภท (เลือกเองแล้วระบบจะจำ)");
                break;
            }
            asked++;
            var sample = g.First().Row.Amount;
            // orchestrator ไม่ throw (ทุกเส้นล้มกลับมาที่นักเรียน) — ปิด AI ⇒ PrimaryAnswer ว่าง ⇒ ด่านคืน null ⇒ คง Unclassified
            var resp = await _ai.AskAsync(SettlementLineClassifyPrompt.Build(companyId, channel.Id, g.Key.Label, channel.Kind, sample), ct);
            foreach (var p in g)
            {
                p.FeedbackId = resp.FeedbackId;
                p.UsedAi = resp.UsedAi;
                var t = SettlementLineClassification.AcceptModelAnswer(resp.PrimaryAnswer, resp.Confidence, resp.UsedAi,
                    resp.FromLocalModel, resp.ProviderModel, p.Row.Amount);
                if (t is SettlementLineType ok)
                {
                    p.Type = ok;
                    p.By = resp.UsedAi ? SettlementClassifiedBy.Ai : SettlementClassifiedBy.Learned;
                }
            }
        }
    }

    /// <summary>คลังที่เรียนต่อช่องทาง = บรรทัดที่<b>ผู้ใช้เลือกเอง</b>ในช่องทางนี้ (รวมรอบโอนที่ยกเลิกแล้ว — ความรู้ของผู้ใช้ยังจริง) ·
    /// คีย์ (ป้าย normalize, ยอดบวกไหม) · เสียงแตก ⇒ ไม่รู้ (<see cref="SettlementLineClassification.LearnedVote"/>)</summary>
    private async Task<Dictionary<(string Label, bool Positive), SettlementLineType?>> LearnedLabelsAsync(Guid companyId,
        Guid channelId, CancellationToken ct)
    {
        var hist = await _db.SettlementLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.ChannelId == channelId
                && l.ClassifiedBy == SettlementClassifiedBy.User && l.RawTypeLabel != null)
            .OrderByDescending(l => l.UpdatedAt ?? l.CreatedAt)
            .Take(5000)
            .Select(l => new { l.RawTypeLabel, l.Amount, l.LineType })
            .ToListAsync(ct);
        return hist
            .GroupBy(x => (Label: SettlementLineClassification.NormalizeLabel(x.RawTypeLabel), Positive: x.Amount > 0m))
            .Where(g => g.Key.Label.Length > 0)
            .ToDictionary(g => g.Key, g => SettlementLineClassification.LearnedVote(g.Select(x => x.LineType)));
    }

    // ═══════════════════ ของช่วย ═══════════════════

    private async Task<HashSet<string>> ExistingKeysAsync(Guid companyId, Guid channelId, IReadOnlyList<string> keys,
        CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in keys.Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            var part = chunk.ToList();
            var hit = await _db.SettlementLines.AsNoTracking()
                .Where(l => l.CompanyId == companyId && l.ChannelId == channelId && l.ExternalTxnId != null
                    && part.Contains(l.ExternalTxnId))
                .Select(l => l.ExternalTxnId!)
                .ToListAsync(ct);
            found.UnionWith(hit);
        }
        return found;
    }

    private async Task<List<string>> BatchRefsOfKeysAsync(Guid companyId, Guid channelId, IReadOnlyList<string> keys,
        CancellationToken ct)
    {
        var sample = keys.Take(1000).ToList();
        return await _db.SettlementLines.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.ChannelId == channelId && l.ExternalTxnId != null && sample.Contains(l.ExternalTxnId))
            .Select(l => l.Batch.PayoutRef).Distinct().Take(5).ToListAsync(ct);
    }

    /// <summary>ผังพักของช่องทาง = ผังที่ <c>IGatewayAccountResolver</c> คืนให้ขาเงินเข้าของ gateway นี้ไหม (R-A1) —
    /// false ⇒ ห้ามถือว่าบรรทัดที่ผูก PaymentIntent "อยู่ในผังพักแล้ว"</summary>
    private async Task<bool> GatewayClearingMatchesAsync(Guid companyId, SettlementChannel channel, string providerCode,
        CancellationToken ct)
    {
        if (channel.ClearingAccountId is not Guid clearing) return false;
        var resolved = await _gatewayAccounts.ResolveClearingAccountAsync(companyId, providerCode, channel.PaymentProviderConfigId, ct);
        return resolved == clearing;
    }

    private async Task<SettlementChannel> LoadChannelAsync(Guid companyId, Guid channelId, bool tracked, CancellationToken ct)
    {
        var q = _db.SettlementChannels.Where(c => c.Id == channelId && c.CompanyId == companyId);
        if (!tracked) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(ct)
               ?? throw new BusinessRuleException("ไม่พบช่องทางรับเงินนี้ในบริษัท", "SETTLEMENT-CHANNEL", 404);
    }

    private static void EnsureActive(SettlementChannel channel)
    {
        if (!channel.IsActive)
            throw new BusinessRuleException($"ช่องทาง \"{channel.DisplayName}\" ถูกปิดใช้ — เปิดใช้ในหน้าตั้งค่าช่องทางก่อนนำเข้า",
                "SETTLEMENT-CHANNEL-INACTIVE");
    }

    private ISettlementReportAdapter ResolveFileAdapter(SettlementChannel channel)
    {
        var code = string.IsNullOrWhiteSpace(channel.AdapterCode) || channel.AdapterCode == PaymentIntentAdapter.AdapterCode
            ? GenericColumnMapAdapter.AdapterCode
            : channel.AdapterCode;
        return _adapters.FirstOrDefault(a => string.Equals(a.Code, code, StringComparison.Ordinal))
               ?? throw new BusinessRuleException(
                   $"ไม่รู้จักตัวอ่านไฟล์ \"{code}\" ที่ช่องทางนี้ตั้งไว้ — เปลี่ยนเป็นการจับคู่คอลัมน์ทั่วไปในหน้าตั้งค่าช่องทาง",
                   "SETTLEMENT-ADAPTER");
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream content, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            if (ms.Length + n > SettlementFileReader.MaxFileBytes)
                throw new BusinessRuleException(
                    $"ไฟล์ใหญ่เกิน {SettlementFileReader.MaxFileBytes / (1024 * 1024)} MB — ส่งออกรายงานทีละรอบโอนแล้วนำเข้าทีละไฟล์",
                    "SETTLEMENT-FILE-TOO-LARGE");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    private static BusinessRuleException FormatError(SettlementFormatException ex)
        => new(ex.Message, ex, "SETTLEMENT-FORMAT-" + ex.Code.ToUpperInvariant());

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static string? Fit(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max];
}

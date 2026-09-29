using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Settlement;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Payments;
using Accounting.Services.Settlement.Adapters;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Settlement;

/// <summary>
/// **ตั้งค่าช่องทางรับเงินผ่าน wallet** (รอบ 198 เฟส 1 ทีม B) — ดู <see cref="ISettlementChannelService"/>
///
/// <para>═══ กติกา ═══
/// <list type="bullet">
/// <item>ทุกผังที่อ้าง (พัก · reserve · dispute · ใน <c>FeeAccountMapJson</c>) ต้องเป็นของบริษัทนี้และเปิดใช้ · คีย์ในแผนผังที่ตัวอ่านกลาง
/// (<c>SettlementLineTypeRules.ParseFeeAccountMap</c>) ปฏิเสธ ⇒ ล้มดังพร้อมชื่อคีย์ (ห้ามทิ้งเงียบ)</item>
/// <item>ผู้ติดต่อของแพลตฟอร์ม: Id (ของบริษัทนี้) หรือเลขภาษี+สาขาผ่าน <c>ContactTaxBranchKey.FindAsync</c> — หาไม่เจอ ⇒ ล้มดังให้สร้างผู้ติดต่อก่อน
/// (ไม่สร้างผู้ติดต่อเอง: ผู้ติดต่อคือผู้ออกใบกำกับค่าธรรมเนียม/ผู้รับเงินใน 50 ทวิ ต้องมีชื่อ/ที่อยู่ตามทะเบียน)</item>
/// <item>ผังพัก: gateway ที่ผูก config ⇒ ผังของ <c>IGatewayAccountResolver</c> เท่านั้น (R-A1) · อื่น ๆ ⇒ ผังที่เลือก หรือ 1134x ที่ระบบสร้าง ·
/// ผูกผ่าน <c>SettlementChannelAccounts.EnsureClearingAccountAsync</c> ในธุรกรรมเดียวกับการบันทึกช่องทาง</item>
/// <item>มีรอบโอนแล้ว (ยังไม่ยกเลิก) ⇒ ชนิด · gateway ที่ผูก · ผังพัก <b>แก้ไม่ได้</b> (ยอดพักค้างอยู่ในผังเดิม) — ล้มดังพร้อมเหตุผล ไม่ใช่เพิกเฉยเงียบ</item>
/// <item>คำเตือนที่ไม่บล็อก: บริษัทนิติบุคคล + WHT ค่าธรรมเนียม = None (report-S1 D5) · สกุลเงินต่างประเทศ/รายได้แบบ net (ยังลงบัญชีไม่ได้ในเฟส 1)</item>
/// </list></para>
/// </summary>
public sealed class SettlementChannelService : ISettlementChannelService
{
    private readonly AccountingDbContext _db;
    private readonly IGatewayAccountResolver _gatewayAccounts;

    public SettlementChannelService(AccountingDbContext db, IGatewayAccountResolver gatewayAccounts)
    {
        _db = db; _gatewayAccounts = gatewayAccounts;
    }

    public async Task<IReadOnlyList<SettlementChannelView>> ListAsync(Guid companyId, bool includeInactive, CancellationToken ct = default)
    {
        var q = _db.SettlementChannels.AsNoTracking().Where(c => c.CompanyId == companyId);
        if (!includeInactive) q = q.Where(c => c.IsActive);
        var channels = await q.OrderBy(c => c.DisplayName).ToListAsync(ct);
        var views = new List<SettlementChannelView>(channels.Count);
        foreach (var c in channels) views.Add(await ToViewAsync(companyId, c, ct));
        return views;
    }

    public async Task<SettlementChannelView> GetAsync(Guid companyId, Guid channelId, CancellationToken ct = default)
    {
        var c = await _db.SettlementChannels.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == channelId && x.CompanyId == companyId, ct)
                ?? throw new BusinessRuleException("ไม่พบช่องทางรับเงินนี้ในบริษัท", "SETTLEMENT-CHANNEL", 404);
        return await ToViewAsync(companyId, c, ct);
    }

    public Task<SettlementChannelView> CreateAsync(Guid companyId, Guid userId, SettlementChannelUpsertRequest request,
        CancellationToken ct = default)
        => SaveAsync(companyId, userId, null, request, ct);

    public Task<SettlementChannelView> UpdateAsync(Guid companyId, Guid userId, Guid channelId, SettlementChannelUpsertRequest request,
        CancellationToken ct = default)
        => SaveAsync(companyId, userId, channelId, request, ct);

    private async Task<SettlementChannelView> SaveAsync(Guid companyId, Guid userId, Guid? channelId,
        SettlementChannelUpsertRequest r, CancellationToken ct)
    {
        // ── ตรวจค่าที่ไม่ต้องแตะฐาน ──
        var name = r.DisplayName?.Trim();
        if (string.IsNullOrEmpty(name)) throw new BusinessRuleException("ระบุชื่อช่องทาง (เช่น \"ร้านหลักบน marketplace\")", "SETTLEMENT-CHANNEL-NAME");
        if (name.Length > 200) throw new BusinessRuleException("ชื่อช่องทางยาวเกิน 200 ตัวอักษร", "SETTLEMENT-CHANNEL-NAME");
        // enum ที่ไม่มีค่า 0 — body ที่ไม่ส่ง kind จะได้ 0 ⇒ ต้องตีกลับ ไม่ใช่บันทึกค่าที่ไม่มีความหมาย (R-A13)
        if (!Enum.IsDefined(r.Kind)) throw new BusinessRuleException("เลือกชนิดช่องทาง", "SETTLEMENT-CHANNEL-KIND");
        if (!Enum.IsDefined(r.FeeVatMode)) throw new BusinessRuleException("เลือกโหมด VAT ของค่าธรรมเนียม", "SETTLEMENT-CHANNEL-VAT");
        if (!Enum.IsDefined(r.FeeWhtMode)) throw new BusinessRuleException("เลือกโหมดหัก ณ ที่จ่ายของค่าธรรมเนียม", "SETTLEMENT-CHANNEL-WHT");
        if (!Enum.IsDefined(r.RevenueModel)) throw new BusinessRuleException("เลือกวิธีรับรู้รายได้", "SETTLEMENT-CHANNEL-REVENUE");
        var currency = string.IsNullOrWhiteSpace(r.Currency) ? "THB" : r.Currency.Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            throw new BusinessRuleException("สกุลเงินต้องเป็นรหัส 3 ตัวอักษร (เช่น THB)", "SETTLEMENT-CHANNEL-CURRENCY");
        if (r.PaymentProviderConfigId != null && r.Kind != SettlementChannelKind.Gateway)
            throw new BusinessRuleException("ผูกการตั้งค่า gateway ได้เฉพาะช่องทางชนิด Gateway", "SETTLEMENT-CHANNEL-GATEWAY");
        var adapterCode = string.IsNullOrWhiteSpace(r.AdapterCode) ? null : r.AdapterCode.Trim();
        if (adapterCode != null && adapterCode != GenericColumnMapAdapter.AdapterCode && adapterCode != PaymentIntentAdapter.AdapterCode)
            throw new BusinessRuleException($"ไม่รู้จักตัวอ่านไฟล์ \"{adapterCode}\" — ใช้ \"{GenericColumnMapAdapter.AdapterCode}\" (จับคู่คอลัมน์เอง)",
                "SETTLEMENT-CHANNEL-ADAPTER");
        if (adapterCode == PaymentIntentAdapter.AdapterCode && r.PaymentProviderConfigId == null)
            throw new BusinessRuleException("ประกอบรอบโอนจากรายการรับชำระได้เฉพาะช่องทางที่ผูก gateway ในระบบ", "SETTLEMENT-CHANNEL-ADAPTER");
        var (feeMap, rejected) = SettlementLineTypeRules.ParseFeeAccountMap(r.FeeAccountMapJson);
        if (rejected.Count > 0)
            throw new BusinessRuleException(
                $"การตั้งผังค่าธรรมเนียมมีคีย์/ค่าที่ใช้ไม่ได้: {string.Join(", ", rejected)} — คีย์ที่ตั้งได้: "
                + string.Join(", ", SettlementAccountRoles.Mappable) + " · ค่าต้องเป็นรหัสผัง (Guid)", "SETTLEMENT-CHANNEL-FEEMAP");
        // รอบ 200 ทีม WF (คำตัดสินข้อ 41): ประเภทเงินได้ของค่าธรรมเนียมต่อประเภทบรรทัด — ตัวอ่านตัวเดียวกับผู้คิดแผน · ค่าเสีย = ล้มดังพร้อมชื่อ
        var (incomeMap, incomeRejected) = SettlementWhtIncomeType.ParseMap(r.WhtIncomeTypeMapJson);
        if (incomeRejected.Count > 0)
            throw new BusinessRuleException(
                $"การตั้งประเภทเงินได้ของค่าธรรมเนียมมีคีย์/ค่าที่ใช้ไม่ได้: {string.Join(", ", incomeRejected)} — คีย์ = ประเภทค่าธรรมเนียม ("
                + string.Join(", ", SettlementWhtIncomeType.Options().Select(o => o.LineType)) + ") · ค่า = รหัสประเภทเงินได้ หรือ \""
                + SettlementWhtIncomeType.NoWithholding + "\" (ไม่หัก)", "SETTLEMENT-CHANNEL-INCOMETYPE");
        string? columnMapJson = null;
        if (!string.IsNullOrWhiteSpace(r.ColumnMapJson))
        {
            try
            {
                var map = SettlementColumnMap.Parse(r.ColumnMapJson)!;
                var issues = map.Validate();
                if (issues.Count > 0)
                    throw new BusinessRuleException("การจับคู่คอลัมน์ยังไม่ครบ: " + string.Join(" · ", issues), "SETTLEMENT-CHANNEL-COLUMNMAP");
                columnMapJson = map.ToJson();
            }
            catch (SettlementFormatException ex)
            {
                throw new BusinessRuleException(ex.Message, ex, "SETTLEMENT-CHANNEL-COLUMNMAP");
            }
        }

        // ── ตรวจของที่อ้างในฐาน (tenant ทุกตัว) ──
        foreach (var (label, id) in new (string, Guid?)[]
                 {
                     ("ผังพัก", r.ClearingAccountId), ("ผังเงินที่ถูกกันไว้", r.ReserveAccountId), ("ผังพัก chargeback", r.DisputeAccountId),
                 }.Concat(feeMap.Select(kv => ("ผังของ " + kv.Key, (Guid?)kv.Value))))
        {
            if (id is Guid aid && !await _db.ChartOfAccounts.AsNoTracking().AnyAsync(a => a.Id == aid && a.CompanyId == companyId && a.IsActive, ct))
                throw new BusinessRuleException($"{label}: ไม่พบผังบัญชีที่เลือกในบริษัท (หรือถูกปิดใช้)", "SETTLEMENT-CHANNEL-ACCOUNT", 404);
        }
        if (r.ClearingAccountId is Guid chosenClearing)
        {
            // ผังพักที่เลือกเองต้องเป็นสินทรัพย์ที่ไม่ใช่ลูกหนี้การค้า (ผังพักถูกตัดออกจากรายงานกระทบลูกหนี้ — R-A2) และไม่ใช้ร่วมกับช่องทางอื่น
            var acc = await _db.ChartOfAccounts.AsNoTracking().Where(a => a.Id == chosenClearing && a.CompanyId == companyId)
                .Select(a => new { a.AccountCode, a.AccountType }).FirstOrDefaultAsync(ct);
            if (acc == null || acc.AccountType != AccountType.Asset || acc.AccountCode == TradeReceivableAccount.StandardCode)
                throw new BusinessRuleException(
                    "ผังพักต้องเป็นผังสินทรัพย์แยกของช่องทาง (ไม่ใช่ลูกหนี้การค้า 11310) — เว้นว่างให้ระบบสร้าง 11341–11349 ให้",
                    "SETTLEMENT-CHANNEL-CLEARING");
            if (r.Kind != SettlementChannelKind.Gateway && await _db.SettlementChannels.AsNoTracking().AnyAsync(c => c.CompanyId == companyId
                    && c.ClearingAccountId == chosenClearing && c.Id != (channelId ?? Guid.Empty), ct))
                throw new BusinessRuleException(
                    "ผังพักนี้ถูกใช้กับช่องทางอื่นแล้ว — หนึ่งผังพักต่อหนึ่ง wallet (ยอดผังต้องเท่ายอดใน wallet ของแพลตฟอร์มนั้น)",
                    "SETTLEMENT-CHANNEL-CLEARING");
        }
        string? providerCode = null;
        if (r.PaymentProviderConfigId is Guid cfgId)
        {
            var cfg = await _db.PaymentProviderConfigs.AsNoTracking()
                          .Where(c => c.Id == cfgId && c.CompanyId == companyId)
                          .Select(c => new { c.ProviderCode, c.FeeVatMode, c.WhtOnFee }).FirstOrDefaultAsync(ct)
                      ?? throw new BusinessRuleException("ไม่พบการตั้งค่า gateway ที่เลือกในบริษัท", "SETTLEMENT-CHANNEL-GATEWAY", 404);
            providerCode = cfg.ProviderCode;
            // รอบ 200 ทีม P2: "ค่าธรรมเนียมมี VAT ไหม · เราหัก ณ ที่จ่ายไหม" เก็บสองที่ (config gateway · ช่องทาง) — ต้องตอบตรงกัน
            // ไม่งั้นรอบโอนที่ประกอบจากรายการรับชำระแต่งภาษีซื้อ/ทิ้ง VAT (ตัวตัดสินเดียวกับ ImportFromPaymentIntentsAsync)
            // X-10 (ฝ่ายค้านรอบ 200): ตรวจเฉพาะช่องทางใหม่/เมื่อการผูกหรือโหมดเปลี่ยน — ช่องทางเดิมที่โหมดขัดยังแก้ชื่อ/ปิดใช้งานได้
            // (ด่านนำเข้า/ประกอบ/ลงบัญชีตรวจซ้ำทุกครั้งอยู่แล้ว ⇒ ไม่มีรอบโอนที่ลงด้วยโหมดขัด) · บริษัทไม่จด VAT ⇒ คู่ VAT ไทยให้ผลเท่ากัน (ข้อ 26)
            var prior = channelId is Guid priorId
                ? await _db.SettlementChannels.AsNoTracking().Where(c => c.Id == priorId && c.CompanyId == companyId)
                    .Select(c => new { c.PaymentProviderConfigId, c.FeeVatMode, c.FeeWhtMode }).FirstOrDefaultAsync(ct)
                : null;
            var touched = GatewayBatchIntentRules.ChannelModeTouched(prior?.PaymentProviderConfigId, prior?.FeeVatMode, prior?.FeeWhtMode,
                r.PaymentProviderConfigId, r.FeeVatMode, r.FeeWhtMode);
            var channelVatRegistered = await CompanyVatStatus.IsRegisteredAsync(_db, companyId, ct);
            if (touched && GatewayBatchIntentRules.ModeMismatch(cfg.FeeVatMode, cfg.WhtOnFee, r.FeeVatMode, r.FeeWhtMode,
                    channelVatRegistered) is string modeBad)
                throw new BusinessRuleException(modeBad, "SETTLEMENT-CHANNEL-GATEWAY-MODE");
        }
        var counterpartyId = await ResolveCounterpartyAsync(companyId, r, ct);

        var strategy = _db.Database.CreateExecutionStrategy();
        var savedId = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            SettlementChannel channel;
            var created = channelId == null;
            if (channelId is Guid id)
            {
                channel = await _db.SettlementChannels.FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId, ct)
                          ?? throw new BusinessRuleException("ไม่พบช่องทางรับเงินนี้ในบริษัท", "SETTLEMENT-CHANNEL", 404);
                var hasBatches = await _db.SettlementBatches.AnyAsync(b => b.CompanyId == companyId && b.ChannelId == id, ct);
                if (hasBatches)
                {
                    // ห้าม silent no-op: ค่าที่ล็อกแล้วถูกส่งมาต่าง ⇒ บอกเหตุผล
                    if (channel.Kind != r.Kind)
                        throw Locked("ชนิดช่องทาง");
                    if (channel.PaymentProviderConfigId != r.PaymentProviderConfigId)
                        throw Locked("การตั้งค่า gateway ที่ผูก");
                    if (r.ClearingAccountId != null && channel.ClearingAccountId != r.ClearingAccountId)
                        throw Locked("ผังพัก");
                }
            }
            else
            {
                channel = new SettlementChannel { CompanyId = companyId, CreatedBy = userId.ToString() };
                _db.SettlementChannels.Add(channel);
            }
            var before = created ? null : Snapshot(channel);

            channel.Kind = r.Kind;
            channel.DisplayName = name;
            channel.AdapterCode = adapterCode ?? channel.AdapterCode ?? GenericColumnMapAdapter.AdapterCode;
            if (columnMapJson != null) channel.ColumnMapJson = columnMapJson;
            channel.CounterpartyContactId = counterpartyId;
            channel.PaymentProviderConfigId = r.PaymentProviderConfigId;
            if (r.ClearingAccountId != null) channel.ClearingAccountId = r.ClearingAccountId;
            channel.ReserveAccountId = r.ReserveAccountId;
            channel.DisputeAccountId = r.DisputeAccountId;
            channel.FeeAccountMapJson = feeMap.Count == 0 ? null : JsonSerializer.Serialize(feeMap);
            channel.WhtIncomeTypeMapJson = SettlementWhtIncomeType.Serialize(incomeMap);
            channel.FeeVatMode = r.FeeVatMode;
            channel.FeeWhtMode = r.FeeWhtMode;
            channel.RevenueModel = r.RevenueModel;
            channel.Currency = currency;
            channel.IsActive = r.IsActive;
            if (!created) { channel.UpdatedAt = DateTime.UtcNow; channel.UpdatedBy = userId.ToString(); }
            await _db.SaveChangesAsync(ct);

            // ผังพัก: gateway ⇒ ผังเดียวกับขาเงินเข้าของ intent (resolver) · อื่น ๆ ⇒ ผังที่เลือก หรือ 1134x ใหม่ (ธุรกรรมเดียวกัน)
            Guid? gatewayClearing = providerCode == null ? null
                : await _gatewayAccounts.ResolveClearingAccountAsync(companyId, providerCode, r.PaymentProviderConfigId, ct);
            await SettlementChannelAccounts.EnsureClearingAccountAsync(_db, companyId, channel, gatewayClearing, ct);

            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = userId,
                EntityType = nameof(SettlementChannel),
                EntityId = channel.Id.ToString(),
                Action = created ? AuditAction.Create : AuditAction.Update,
                OldValues = before,
                NewValues = Snapshot(channel),
                Timestamp = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return channel.Id;
        });
        return await GetAsync(companyId, savedId, ct);
    }

    private static BusinessRuleException Locked(string what)
        => new($"ช่องทางนี้มีรอบโอนแล้ว — แก้{what}ไม่ได้ (ยอดพักและรายการเดิมผูกอยู่กับค่าเดิม) · สร้างช่องทางใหม่แทน หรือยกเลิกรอบโอนทั้งหมดของช่องทางนี้ก่อน",
            "SETTLEMENT-CHANNEL-LOCKED");

    private static string Snapshot(SettlementChannel c) => JsonSerializer.Serialize(new
    {
        kind = c.Kind.ToString(), c.DisplayName, c.AdapterCode, c.CounterpartyContactId, c.PaymentProviderConfigId, c.ClearingAccountId,
        c.ReserveAccountId, c.DisputeAccountId, c.FeeAccountMapJson, feeVat = c.FeeVatMode.ToString(), feeWht = c.FeeWhtMode.ToString(),
        revenue = c.RevenueModel.ToString(), c.Currency, c.IsActive, c.WhtIncomeTypeMapJson,
    });

    /// <summary>ผู้ติดต่อของแพลตฟอร์ม — Id ที่ส่งมา (ต้องเป็นของบริษัทนี้) ชนะ · ไม่มี ⇒ เลขภาษี+สาขา (คีย์กลาง) · ไม่ระบุเลย ⇒ null</summary>
    private async Task<Guid?> ResolveCounterpartyAsync(Guid companyId, SettlementChannelUpsertRequest r, CancellationToken ct)
    {
        if (r.CounterpartyContactId is Guid id)
        {
            if (!await _db.Contacts.AsNoTracking().AnyAsync(c => c.Id == id && c.CompanyId == companyId, ct))
                throw new BusinessRuleException("ไม่พบผู้ติดต่อของแพลตฟอร์มที่เลือกในบริษัท", "SETTLEMENT-CHANNEL-CONTACT", 404);
            return id;
        }
        if (!ContactTaxBranchKey.HasTaxId(r.CounterpartyTaxId)) return null;
        var match = await ContactTaxBranchKey.FindAsync(_db.Contacts.AsNoTracking(), companyId, r.CounterpartyTaxId,
            r.CounterpartyBranchCode, ct);
        return match.ContactId ?? throw new BusinessRuleException(
            $"ไม่พบผู้ติดต่อเลขภาษี {r.CounterpartyTaxId}"
            + (string.IsNullOrWhiteSpace(r.CounterpartyBranchCode) ? "" : $" สาขา {r.CounterpartyBranchCode}")
            + " — สร้างผู้ติดต่อของแพลตฟอร์ม (ชื่อ/ที่อยู่ตามทะเบียน) ก่อน แล้วเลือกในหน้าตั้งค่าช่องทาง", "SETTLEMENT-CHANNEL-CONTACT", 404);
    }

    private async Task<SettlementChannelView> ToViewAsync(Guid companyId, SettlementChannel c, CancellationToken ct)
    {
        var clearing = c.ClearingAccountId is Guid cid
            ? await _db.ChartOfAccounts.AsNoTracking().Where(a => a.Id == cid && a.CompanyId == companyId)
                .Select(a => new { a.AccountCode, a.AccountName }).FirstOrDefaultAsync(ct)
            : null;
        var counterparty = c.CounterpartyContactId is Guid pid
            ? await _db.Contacts.AsNoTracking().Where(x => x.Id == pid && x.CompanyId == companyId).Select(x => x.Name).FirstOrDefaultAsync(ct)
            : null;
        var hasBatches = await _db.SettlementBatches.AsNoTracking().AnyAsync(b => b.CompanyId == companyId && b.ChannelId == c.Id, ct);
        var company = await _db.Companies.AsNoTracking().Where(x => x.Id == companyId)
            .Select(x => new { x.TaxId }).FirstOrDefaultAsync(ct);

        var warnings = new List<string>();
        // ผู้ให้บริการต่างประเทศ: ม.70 ใช้กับผู้จ่ายทุกราย (ไม่ใช่เฉพาะนิติบุคคล) และอัตราไม่ใช่อัตราในประเทศ — ตัวเลขจากตัวตัดสินเดียว (ทีม W)
        var foreignChannel = SettlementForeignWht.IsForeignChannel(c.FeeVatMode);
        if (c.FeeWhtMode == SettlementFeeWhtMode.None && foreignChannel)
            warnings.Add("ผู้ให้บริการต่างประเทศ — ค่าธรรมเนียม/ค่านายหน้า (40(2)) ที่จ่ายไปต่างประเทศต้องหัก ณ ที่จ่ายตาม "
                + $"{ForeignWhtRateResolver.Section70Reference} {ForeignWhtRateResolver.Section70GeneralRate:0.##}% ยื่น ภ.ง.ด.54 "
                + "(อัตราอนุสัญญาภาษีซ้อนใช้ได้เมื่อมีหนังสือรับรองถิ่นที่อยู่) · ระบบตั้งค่าคอม/ค่าธรรมเนียมของแพลตฟอร์มต่างประเทศเป็น 40(2) ไว้ก่อน "
                + "(ค่าโฆษณา/ค่าขนส่ง 40(8) ไม่อยู่ใน ม.70) — ตรวจการจำแนกกับนักบัญชีแล้วตั้งโหมดและประเภทเงินได้ของค่าธรรมเนียมให้ตรง");
        else if (c.FeeWhtMode == SettlementFeeWhtMode.None && ThaiTaxId.IsJuristic(company?.TaxId))
            warnings.Add("บริษัทเป็นนิติบุคคล — ค่าธรรมเนียม/ค่าคอมที่จ่ายแพลตฟอร์มอาจต้องหัก ณ ที่จ่าย (3% ค่าบริการ · 2% โฆษณา · 1% ขนส่ง) "
                + "ตรวจกับนักบัญชีว่าแพลตฟอร์มหักแทน/คืนให้/ต้องออกภาษีแทน แล้วตั้งโหมดให้ตรง");
        if (c.Currency != "THB")
            warnings.Add($"สกุลเงิน {c.Currency} — เฟส 1 ลงบัญชีรอบโอนได้เฉพาะ THB (นำเข้าได้ แต่ลงบัญชีจะถูกบล็อก)");
        if (c.RevenueModel == SettlementRevenueModel.NetRate)
            warnings.Add("รายได้แบบราคาสุทธิ (OTA ซื้อมาขายต่อ) ยังลงบัญชีไม่ได้ในเฟส 1");
        if (c.CounterpartyContactId == null)
            warnings.Add("ยังไม่ได้เลือกผู้ติดต่อของแพลตฟอร์ม — ใบค่าธรรมเนียม (เอกสารซื้อ) และ 50 ทวิ ต้องมีผู้ออก/ผู้รับเงิน");
        if (SettlementWhtIncomeType.MapIssue(c) is SettlementPlanIssue badMap)
            warnings.Add(badMap.Message + " — " + badMap.NextStep);

        // ประเภทเงินได้ที่รอบโอนจะใช้จริง (ตัวตัดสินเดียวกับผู้คิดแผน) — หน้าเว็บแสดงอย่างเดียว
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var incomeTypes = SettlementWhtIncomeType.Options()
            .Select(o =>
            {
                var type = Enum.Parse<SettlementLineType>(o.LineType);
                var choice = SettlementWhtIncomeType.For(type, c);
                return new SettlementFeeIncomeTypeView(o.LineType, o.Label, choice.Code, choice.Source.ToString(),
                    SettlementWhtIncomeType.Describe(choice, c.FeeVatMode, today));
            })
            .ToList();

        return new SettlementChannelView(c.Id, c.Kind, c.DisplayName, c.AdapterCode, c.ColumnMapJson, c.CounterpartyContactId, counterparty,
            c.PaymentProviderConfigId, c.ClearingAccountId, clearing?.AccountCode, clearing?.AccountName, c.ReserveAccountId,
            c.DisputeAccountId, c.FeeAccountMapJson, c.FeeVatMode, c.FeeWhtMode, c.RevenueModel, c.Currency, c.IsActive, hasBatches,
            warnings, c.WhtIncomeTypeMapJson, incomeTypes);
    }
}

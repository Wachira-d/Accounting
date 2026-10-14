using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ข้อมูลประกอบกติกา "ที่อยู่ของเงิน" ของบริษัทหนึ่ง (โหลดครั้งเดียวต่อคำขอ)</summary>
public sealed record MoneyAccountContext(
    IReadOnlyList<string> Prefixes,
    bool StandardChart,
    IReadOnlyDictionary<Guid, (Guid BankAccountId, string BankLabel)> LinkedBankGls,
    Guid? CompanyDefaultAccountId);

/// <summary>แหล่งเงินเริ่มต้นของบริษัท — <c>Account</c> = ผังที่จะใช้ · <c>Problem</c> = ใช้ไม่ได้ (ข้อความพร้อมทางไปต่อ) ·
/// <c>FromCompanySetting</c> = มาจาก "แหล่งเงินเริ่มต้น" ที่บริษัทตั้ง (false = ระบบเลือกผังเงินสด/ธนาคารรหัสต่ำสุด)</summary>
public sealed record DefaultMoneySource(PostingAccountFacts? Account, bool FromCompanySetting, string? Problem)
{
    /// <summary>ป้ายสั้นที่หน้าเว็บแสดงข้างตัวเลือก "ค่าเริ่มต้น"</summary>
    public string? Label => Account is null ? null : $"{Account.Code} {Account.Name}";
}

/// <summary>ผลหาผังของบัญชีธนาคาร (ตาราง BankAccounts) — ไม่โยน</summary>
public sealed record BankGlResolution(Guid? GlAccountId, BankLinkProblem Problem, string? Message, string? BankLabel);

/// <summary>
/// **ทะเบียน "ที่อยู่ของเงิน" ของบริษัท — ตัวดึงข้อมูลของ <see cref="MoneyAccountRule"/> + ตัวหาแหล่งเงินตัวเดียว** (2026-10-14 · PC#11/#12/#17 · ID#19)
///
/// <para>═══ ที่มา ═══ ตัวหา "บัญชีธนาคาร → ผัง · ค่าเริ่มต้นบริษัท · ผัง 111x แรก" ถูกเขียนซ้ำใน StatutoryRemittanceService,
/// PayrollService (SSO) และอีกหลายที่ — แต่ละที่แยก "ไม่พบ" กับ "ยังไม่ผูก" ไม่ได้ ไม่ตรวจปิดใช้ และเงินเดือนใช้ค่าเริ่มต้นของตัวเอง
/// (11122 = บัญชีคุมของธนาคารที่สร้างอัตโนมัติ) โดยไม่อ่านค่าตั้งของบริษัท ⇒ รวมไว้ที่นี่ที่เดียว</para>
///
/// <para>ลำดับแหล่งเงิน (<see cref="ResolveSourceGlAsync"/>): ① ผังที่ผู้ใช้เลือกตรง ② บัญชีธนาคารที่เลือก → ผังที่ผูก ③ แหล่งเงินเริ่มต้นของบริษัท
/// ④ ผังเงินสด/ธนาคารรหัสต่ำสุดที่ลงรายการได้ · ①②③ ใช้ไม่ได้ = ล้มดังพร้อมทางไปต่อ (ไม่สลับไปผังอื่นเงียบ ๆ)</para>
/// </summary>
public static class MoneyAccountDirectory
{
    /// <summary>รหัสกฎของความล้มเหลวด้านแหล่งเงิน</summary>
    public const string RuleCode = "MONEY-SOURCE";

    public static async Task<MoneyAccountContext> LoadMoneyContextAsync(
        this AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var settings = await db.Set<CompanySettings>().AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => new { s.MoneyAccountCodePrefixes, s.DefaultPaymentAccountId })
            .FirstOrDefaultAsync(ct);
        var prefixes = MoneyAccountRule.ParsePrefixes(settings?.MoneyAccountCodePrefixes);
        var banks = await db.BankAccounts.AsNoTracking()
            .Where(b => b.CompanyId == companyId && !b.IsDeleted && b.IsActive && b.LinkedAccountId != null)
            .OrderBy(b => b.AccountName)
            .Select(b => new { b.Id, b.LinkedAccountId, b.AccountName, b.BankName, b.AccountNumber })
            .ToListAsync(ct);
        var linked = new Dictionary<Guid, (Guid BankAccountId, string BankLabel)>();
        foreach (var b in banks)
            if (!linked.ContainsKey(b.LinkedAccountId!.Value))
                linked[b.LinkedAccountId!.Value] = (b.Id, BankLabel(b.AccountName, b.BankName, b.AccountNumber));
        return new MoneyAccountContext(prefixes, MoneyAccountRule.IsStandard(prefixes), linked, settings?.DefaultPaymentAccountId);
    }

    /// <summary>ป้ายบัญชีธนาคารที่ผู้ใช้เห็น — ชื่อบัญชี (ธนาคาร xxx-1234) · ไม่แสดงเลขเต็ม</summary>
    public static string BankLabel(string? accountName, string? bankName, string? accountNumber)
    {
        var tail = string.IsNullOrWhiteSpace(accountNumber) ? "" : (accountNumber.Length > 4 ? "xxx-" + accountNumber[^4..] : accountNumber);
        var inner = $"{bankName} {tail}".Trim();
        var name = string.IsNullOrWhiteSpace(accountName) ? (bankName ?? "บัญชีธนาคาร") : accountName;
        return inner.Length == 0 || inner == name ? name : $"{name} ({inner})";
    }

    /// <summary>
    /// รายการช่องทางเงินสำหรับ dropdown — <paramref name="includeBank"/>=false (ค่าเดิมของ endpoint) คืนเฉพาะกลุ่ม "other"
    /// (เงินสด/เงินทดรองกรรมการ/ช่องจ่ายอื่น — หน้าเว็บแสดงคู่กับรายการบัญชีธนาคาร) · true คืนทุกแถวพร้อมป้ายกลุ่ม (หน้า ตั้งค่า)
    /// · ลงรายการได้เท่านั้น (เปิดใช้ · ไม่ใช่บัญชีคุม — ยกเว้นผังที่บัญชีธนาคารผูกอยู่)
    /// </summary>
    public static async Task<List<PaymentChannelAccountResponse>> GetPaymentChannelsAsync(
        this AccountingDbContext db, Guid companyId, bool includeBank, CancellationToken ct = default)
    {
        var ctx = await db.LoadMoneyContextAsync(companyId, ct);
        var rows = await db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive)
            .Select(a => new
            {
                a.Id, a.AccountCode, a.AccountName, a.AccountNameEn, a.AccountType, a.ParentAccountId, a.Level, a.IsActive,
                IsGroup = db.ChartOfAccounts.Any(c => c.CompanyId == companyId && !c.IsDeleted && c.ParentAccountId == a.Id),
            })
            .OrderBy(a => a.AccountCode)
            .ToListAsync(ct);
        var result = new List<PaymentChannelAccountResponse>();
        foreach (var a in rows)
        {
            var linked = ctx.LinkedBankGls.TryGetValue(a.Id, out var bank);
            if (a.IsGroup && !linked) continue;
            var group = MoneyAccountRule.GroupOf(a.AccountCode, linked, ctx.Prefixes);
            if (group is null) continue;
            if (!includeBank && group == MoneyChannelGroup.Bank) continue;
            result.Add(new PaymentChannelAccountResponse(
                a.Id, a.AccountCode, a.AccountName, a.AccountNameEn, a.AccountType, (int)a.AccountType,
                a.ParentAccountId, a.Level, a.IsActive, MoneyAccountRule.GroupName(group.Value),
                linked ? bank.BankAccountId : (Guid?)null, linked ? bank.BankLabel : null));
        }
        return result;
    }

    /// <summary>ผังนี้เป็นที่อยู่ของเงินตามกติกาของบริษัทไหม (ใช้ประกอบคำเตือน — ไม่บล็อก)</summary>
    public static bool IsMoneyAccount(this MoneyAccountContext ctx, Guid accountId, string accountCode)
        => MoneyAccountRule.IsMoneyAccount(accountCode, ctx.LinkedBankGls.ContainsKey(accountId), ctx.Prefixes);

    /// <summary>ปัญหาของผังที่ใช้เป็นแหล่งรับ/จ่ายเงิน — เหมือน <see cref="PostingAccountRule"/> แต่ผังที่บัญชีธนาคารผูกอยู่ยอมเป็นบัญชีคุมได้
    /// (การเชื่อมเดิมของผู้ใช้ · ทะเบียนธนาคารอ่านยอดจากผังนี้ตรง ๆ)</summary>
    public static PostingAccountProblem SourceProblem(this MoneyAccountContext ctx, PostingAccountFacts? facts)
    {
        var p = PostingAccountRule.Check(facts);
        return p == PostingAccountProblem.Group && facts != null && ctx.LinkedBankGls.ContainsKey(facts.Id)
            ? PostingAccountProblem.None : p;
    }

    /// <summary>แหล่งเงินเริ่มต้นของบริษัท: ค่าตั้ง "แหล่งเงินเริ่มต้น" (ต้องลงรายการได้ — ใช้ไม่ได้ = Problem ไม่สลับเงียบ) → ผังเงินสด/ธนาคาร
    /// ที่ลงรายการได้รหัสต่ำสุด (ผังมาตรฐาน = 11111 เงินสด)</summary>
    public static async Task<DefaultMoneySource> ResolveDefaultMoneySourceAsync(
        this AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var ctx = await db.LoadMoneyContextAsync(companyId, ct);
        return await db.ResolveDefaultMoneySourceAsync(companyId, ctx, ct);
    }

    /// <param name="requireExplicitChoice">ฝ่ายค้านรอบ 2 P2(a) — เงินเดือน: ไม่มีค่าตั้งบริษัท ⇒ ใช้ได้เฉพาะเมื่อมีบัญชีธนาคารที่ผูกผัง<b>บัญชีเดียว</b>
    /// (ไม่งั้น Problem ให้ผู้ใช้เลือกเอง) — เดิมตกไป "ผังเงินสดรหัสต่ำสุด" = 11111 เงินสด / 11112 เงินสดย่อย เงียบ ๆ</param>
    public static async Task<DefaultMoneySource> ResolveDefaultMoneySourceAsync(
        this AccountingDbContext db, Guid companyId, MoneyAccountContext ctx, CancellationToken ct = default,
        bool requireExplicitChoice = false)
    {
        if (ctx.CompanyDefaultAccountId is Guid defId && defId != Guid.Empty)
        {
            var facts = await db.LoadPostingFactsAsync(companyId, new[] { defId }, ct);
            facts.TryGetValue(defId, out var f);
            var problem = ctx.SourceProblem(f);
            if (problem == PostingAccountProblem.None) return new DefaultMoneySource(f, true, null);
            return new DefaultMoneySource(null, true,
                $"\"แหล่งเงินเริ่มต้น\" ที่บริษัทตั้งไว้ใช้ไม่ได้ ({PostingAccountRule.ShortReason(problem, f)}) — "
                + "เลือกใหม่ที่หน้า ตั้งค่า → แหล่งเงินเริ่มต้น หรือเลือกแหล่งเงินในหน้าต่างนี้");
        }
        if (requireExplicitChoice)
        {
            var linkedGls = ctx.LinkedBankGls.Keys.ToList();
            if (linkedGls.Count == 1)
            {
                var only = await db.LoadPostingFactsAsync(companyId, linkedGls, ct);
                only.TryGetValue(linkedGls[0], out var bf);
                if (ctx.SourceProblem(bf) == PostingAccountProblem.None) return new DefaultMoneySource(bf, false, null);
            }
            return new DefaultMoneySource(null, false, linkedGls.Count == 0
                ? "ยังไม่ได้ตั้ง \"แหล่งเงินเริ่มต้น\" และยังไม่มีบัญชีธนาคารที่ผูกผังบัญชี — ระบบไม่เลือกเงินสด/เงินสดย่อยให้เอง · "
                  + "ตั้ง \"แหล่งเงินเริ่มต้น\" ที่หน้า ตั้งค่า หรือเลือกแหล่งจ่ายที่ช่อง \"แหล่งจ่าย\""
                : $"ยังไม่ได้ตั้ง \"แหล่งเงินเริ่มต้น\" และมีบัญชีธนาคารที่ผูกผัง {linkedGls.Count} บัญชี — ระบบไม่เดาว่าจ่ายจากบัญชีไหน · "
                  + "ตั้ง \"แหล่งเงินเริ่มต้น\" ที่หน้า ตั้งค่า หรือเลือกแหล่งจ่ายที่ช่อง \"แหล่งจ่าย\"");
        }
        // ฝ่ายค้านรอบ 2 P2(a): ผังของกองทุนเงินสดย่อย / ผังพักของช่องทาง wallet-แพลตฟอร์ม ไม่ใช่ "แหล่งเงินเริ่มต้น" (เงินจริงไม่ได้จ่ายจากที่นั่น)
        var pettyGls = await db.PettyCashFunds.AsNoTracking()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted && f.LinkedAccountId != null)
            .Select(f => f.LinkedAccountId!.Value).ToListAsync(ct);
        var walletGls = await db.SettlementChannels.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => new { c.ClearingAccountId, c.ReserveAccountId }).ToListAsync(ct);
        var excluded = pettyGls
            .Concat(walletGls.SelectMany(w => new[] { w.ClearingAccountId, w.ReserveAccountId }).Where(g => g.HasValue).Select(g => g!.Value))
            .ToHashSet();
        var candidates = await db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive)
            .Select(a => new
            {
                a.Id, a.AccountCode, a.AccountName,
                IsGroup = db.ChartOfAccounts.Any(c => c.CompanyId == companyId && !c.IsDeleted && c.ParentAccountId == a.Id),
            })
            .OrderBy(a => a.AccountCode)
            .ToListAsync(ct);
        var pick = candidates.FirstOrDefault(a =>
            !excluded.Contains(a.Id)
            && !(ctx.StandardChart && a.AccountCode == PettyCashJePlan.DefaultFundAccountCode)
            && (!a.IsGroup || ctx.LinkedBankGls.ContainsKey(a.Id))
            && MoneyAccountRule.IsMoneyAccount(a.AccountCode, ctx.LinkedBankGls.ContainsKey(a.Id), ctx.Prefixes));
        if (pick != null)
            return new DefaultMoneySource(new PostingAccountFacts(pick.Id, pick.AccountCode, pick.AccountName, true, pick.IsGroup), false, null);
        return new DefaultMoneySource(null, false,
            "ยังไม่มีผังบัญชีเงินสด/ธนาคารที่ลงรายการได้ — ตั้ง \"แหล่งเงินเริ่มต้น\" (และรหัสผังเงินสด/ธนาคารถ้าใช้ผังของบริษัทเอง) "
            + "ที่หน้า ตั้งค่า หรือเลือกแหล่งเงินในหน้าต่างนี้");
    }

    /// <summary>บัญชีธนาคาร (ตาราง BankAccounts) → ผังที่ผูก · แยก ไม่พบ / ปิดใช้ / ยังไม่ผูก / ผังที่ผูกใช้ไม่ได้ (ID#19) · ไม่โยน</summary>
    public static async Task<BankGlResolution> TryResolveBankGlAsync(
        this AccountingDbContext db, Guid companyId, Guid bankAccountId, CancellationToken ct = default)
    {
        var bank = await db.BankAccounts.AsNoTracking()
            .Where(b => b.Id == bankAccountId && b.CompanyId == companyId && !b.IsDeleted)
            .Select(b => new { b.AccountName, b.BankName, b.AccountNumber, b.IsActive, b.LinkedAccountId })
            .FirstOrDefaultAsync(ct);
        var label = bank == null ? null : BankLabel(bank.AccountName, bank.BankName, bank.AccountNumber);
        PostingAccountFacts? gl = null;
        var glProblem = PostingAccountProblem.None;
        if (bank?.LinkedAccountId is Guid linkedId)
        {
            var facts = await db.LoadPostingFactsAsync(companyId, new[] { linkedId }, ct);
            facts.TryGetValue(linkedId, out gl);
            glProblem = PostingAccountRule.Check(gl);
            // ผังที่บัญชีธนาคารผูกอยู่ยอมเป็นบัญชีคุมได้ (การเชื่อมเดิม — ทะเบียนธนาคารอ่านยอดจากผังนี้ตรง ๆ)
            if (glProblem == PostingAccountProblem.Group) glProblem = PostingAccountProblem.None;
        }
        var problem = MoneyAccountRule.CheckBank(bank != null, bank?.IsActive ?? false, bank?.LinkedAccountId, glProblem);
        if (problem == BankLinkProblem.None) return new BankGlResolution(bank!.LinkedAccountId, problem, null, label);
        var msg = MoneyAccountRule.BankProblemMessage(problem, label, PostingAccountRule.ShortReason(glProblem, gl));
        return new BankGlResolution(null, problem, msg, label);
    }

    /// <summary>เหมือน <see cref="TryResolveBankGlAsync"/> แต่ล้มดัง (BusinessRuleException พร้อมทางไปต่อ)</summary>
    public static async Task<Guid> ResolveBankGlAsync(
        this AccountingDbContext db, Guid companyId, Guid bankAccountId, CancellationToken ct = default)
    {
        var r = await db.TryResolveBankGlAsync(companyId, bankAccountId, ct);
        return r.GlAccountId ?? throw new BusinessRuleException(r.Message!, RuleCode);
    }

    /// <summary>
    /// แหล่งเงินของรายการรับ/จ่าย: ① ผังที่ผู้ใช้เลือกตรง (<paramref name="glAccountId"/> — ต้องลงรายการได้) ② บัญชีธนาคาร → ผังที่ผูก
    /// ③ แหล่งเงินเริ่มต้นของบริษัท ④ ผังเงินสด/ธนาคารรหัสต่ำสุด · ใช้ไม่ได้ = BusinessRuleException พร้อมทางไปต่อ (ไม่เดา)
    /// </summary>
    public static async Task<Guid> ResolveSourceGlAsync(
        this AccountingDbContext db, Guid companyId, Guid? bankAccountId, Guid? glAccountId,
        string fieldLabel = "บัญชีแหล่งเงิน", CancellationToken ct = default)
    {
        var ctx = await db.LoadMoneyContextAsync(companyId, ct);
        if (glAccountId is Guid gid && gid != Guid.Empty)
        {
            var facts = await db.LoadPostingFactsAsync(companyId, new[] { gid }, ct);
            facts.TryGetValue(gid, out var f);
            var problem = ctx.SourceProblem(f);
            if (problem != PostingAccountProblem.None)
                throw new BusinessRuleException(PostingAccountRule.Message(problem, f, fieldLabel)!, RuleCode);
            return gid;
        }
        if (bankAccountId is Guid bid && bid != Guid.Empty)
            return await db.ResolveBankGlAsync(companyId, bid, ct);
        var def = await db.ResolveDefaultMoneySourceAsync(companyId, ctx, ct);
        return def.Account?.Id ?? throw new BusinessRuleException(def.Problem!, RuleCode);
    }
}

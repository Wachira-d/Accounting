using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ผังพัก "ลูกหนี้แพลตฟอร์ม" ต่อช่องทาง (11341–11349)** — DECISIONS ข้อ 4 · report-S1 §1.2/D1
///
/// <para>ทำไมผังย่อยต่อช่องทาง ไม่ใช่ 11340 + มิติ: ยอดของผังพักต้อง = ยอดใน wallet ของแพลตฟอร์มนั้นทุกวัน (report-S1 §7 ข้อ 1) ·
/// ผังเดียวรวมทุกแพลตฟอร์มทำให้กระทบยอดรายแพลตฟอร์มไม่ได้จากงบทดลอง · ผังย่อยไม่ seed ล่วงหน้า (บริษัทส่วนใหญ่ไม่มี marketplace)
/// — สร้างตอนผูกช่องทางผ่านเมธอดนี้ตัวเดียว</para>
///
/// <para>═══ กติกา ═══
/// <list type="bullet">
/// <item><b>idempotent</b>: ผูกแล้ว (ผังยังอยู่ · ของบริษัทนี้ · เปิดใช้) ⇒ คืนตัวเดิม · ผังที่สร้างไว้แล้วแต่ยังไม่ได้ผูก (ล้มกลางทาง) ⇒ ผูกตัวเดิม</item>
/// <item><b>gateway ในระบบ</b> (ผูก <c>PaymentProviderConfigId</c> ที่มีผังพักอยู่แล้ว) ⇒ ใช้ผังของ config (11340) — PaymentIntent ลงไว้ที่นั่นแล้ว
/// ถ้าแยกผังใหม่ ยอดรับชำระกับยอดโอนจะอยู่คนละผังตลอดไป</item>
/// <item>ผังที่ผูกไว้ถูกลบ/ปิดใช้ ⇒ <b>ล้มดัง</b> (ห้ามผูกผังใหม่เงียบ ๆ — ยอดพักค้างอยู่ในผังเดิม)</item>
/// <item>ครบ 9 ช่องทางแล้ว ⇒ ล้มดังพร้อมทางไปต่อ · ไม่มีกลุ่ม 113 ⇒ ล้มดัง</item>
/// <item>tenant: ทุก query กรอง <c>CompanyId</c> · ล็อก advisory ต่อบริษัท (สองคำขอพร้อมกันได้คนละรหัส ไม่ชน unique)</item>
/// <item><b>บันทึกเอง</b> (SaveChanges + ธุรกรรมของตัวเองเมื่อผู้เรียกไม่มี) — เรียกตอนสร้าง/ผูกช่องทาง ไม่ใช่กลางธุรกรรมลงบัญชี</item>
/// </list></para>
/// </summary>
public static class SettlementChannelAccounts
{
    internal const int FirstClearingSuffix = 1;
    internal const int LastClearingSuffix = 9;
    /// <summary>ต้นรหัสของผังพักย่อย ("1134" + 1..9)</summary>
    internal const string ClearingCodeStem = "1134";
    private const string LockScope = "settlement-clearing";

    /// <summary>คืน AccountId ของผังพักของช่องทาง — สร้าง/ผูกให้ถ้ายังไม่มี (ดูกติกาที่หัวคลาส)</summary>
    /// <exception cref="BusinessRuleException">ช่องทางไม่ใช่ของบริษัทนี้ · ผังที่ผูกไว้ใช้ไม่ได้ · ไม่มีกลุ่ม 113 · ครบ 9 ช่องทาง</exception>
    public static async Task<Guid> EnsureClearingAccountAsync(
        AccountingDbContext db, Guid companyId, SettlementChannel channel, CancellationToken ct = default)
    {
        if (channel.CompanyId != companyId)
            throw new BusinessRuleException("ช่องทางนี้ไม่ใช่ของบริษัทที่เลือก", "SETTLEMENT-TENANT", 403);

        if (channel.ClearingAccountId is Guid linked)
        {
            if (await IsUsableAsync(db, companyId, linked, ct)) return linked;
            throw new BusinessRuleException(
                $"ผังพักของช่องทาง \"{channel.DisplayName}\" ถูกลบหรือปิดใช้ — ยอดที่พักค้างอยู่ในผังเดิม ระบบจึงไม่ผูกผังใหม่ให้เอง · "
                + "เปิดใช้ผังเดิมในหน้าผังบัญชี หรือเลือกผังพักใหม่ในหน้าตั้งค่าช่องทาง (แล้วโอนยอดคงค้างด้วยสมุดรายวัน)",
                "SETTLEMENT-CLEARING-UNUSABLE");
        }

        if (channel.Kind == SettlementChannelKind.Gateway && channel.PaymentProviderConfigId is Guid cfgId)
        {
            var cfgClearing = await db.PaymentProviderConfigs.AsNoTracking()
                .Where(c => c.Id == cfgId && c.CompanyId == companyId)
                .Select(c => c.ClearingAccountId)
                .FirstOrDefaultAsync(ct);
            if (cfgClearing is Guid g && await IsUsableAsync(db, companyId, g, ct))
            {
                channel.ClearingAccountId = g;
                await db.SaveChangesAsync(ct);
                return g;
            }
        }

        if (db.Database.CurrentTransaction != null)
            return await CreateUnderLockAsync(db, companyId, channel, ct);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var id = await CreateUnderLockAsync(db, companyId, channel, ct);
            await tx.CommitAsync(ct);
            return id;
        });
    }

    private static async Task<Guid> CreateUnderLockAsync(
        AccountingDbContext db, Guid companyId, SettlementChannel channel, CancellationToken ct)
    {
        var lockKey = AdvisoryLockKey.For(companyId, LockScope, ClearingCodeStem);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", new object[] { lockKey }, ct);

        var firstCode = ClearingCodeStem + FirstClearingSuffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var parentCode = SettlementChartSeed.ParentGroupCode(firstCode);
        var parent = await db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == parentCode && a.IsActive, ct);
        if (parent == null)
            throw new BusinessRuleException(
                $"ผังบัญชีของบริษัทไม่มีกลุ่ม {parentCode} (ลูกหนี้การค้าและลูกหนี้อื่น) — สร้างผังพักย่อยให้อัตโนมัติไม่ได้ · "
                + "เลือกผังพักที่มีอยู่เองในหน้าตั้งค่าช่องทาง หรือเพิ่มกลุ่ม 113 ในหน้าผังบัญชีก่อน",
                "SETTLEMENT-CLEARING-NO-PARENT");

        // รหัสที่ถูกใช้แล้ว — รวมแถวที่ลบแล้ว (unique index CompanyId+AccountCode ไม่ได้กรอง IsDeleted)
        var used = await db.ChartOfAccounts.IgnoreQueryFilters()
            .Where(a => a.CompanyId == companyId && a.AccountCode.StartsWith(ClearingCodeStem))
            .Select(a => new { a.Id, a.AccountCode, a.AccountName, a.IsDeleted })
            .ToListAsync(ct);

        // ล้มกลางทางรอบก่อน (สร้างผังแล้วแต่ยังไม่ได้ผูก) ⇒ ผูกตัวเดิม ห้ามสร้างซ้ำ
        var name = ClearingAccountName(channel.DisplayName);
        foreach (var cand in used.Where(u => !u.IsDeleted && u.AccountName == name && IsClearingCode(u.AccountCode)))
        {
            var takenByOther = await db.SettlementChannels.AnyAsync(
                c => c.CompanyId == companyId && c.ClearingAccountId == cand.Id && c.Id != channel.Id, ct);
            if (!takenByOther)
            {
                channel.ClearingAccountId = cand.Id;
                await db.SaveChangesAsync(ct);
                return cand.Id;
            }
        }

        var code = NextClearingCode(used.Select(u => u.AccountCode));
        if (code == null)
            throw new BusinessRuleException(
                $"ผังพักย่อย {ClearingCodeStem}{FirstClearingSuffix}–{ClearingCodeStem}{LastClearingSuffix} ถูกใช้ครบ 9 ช่องทางแล้ว — "
                + "ปิดช่องทางที่เลิกใช้แล้วเลือกผังพักเดิมของช่องทางนั้นให้ช่องทางนี้ หรือเลือกผังพักที่สร้างเองในหน้าตั้งค่าช่องทาง",
                "SETTLEMENT-CLEARING-FULL");

        var account = new ChartOfAccount
        {
            CompanyId = companyId,
            AccountCode = code,
            AccountName = name,
            AccountNameEn = ClearingAccountNameEn(channel.DisplayName),
            AccountType = AccountType.Asset,
            ParentAccountId = parent.Id,
            Level = parent.Level + 1,
            IsActive = true,
            IsSystemAccount = true,
            Description = "สร้างอัตโนมัติเมื่อผูกช่องทางรับเงิน (settlement) — ยอดต้องเท่ายอดใน wallet ของแพลตฟอร์ม",
        };
        db.ChartOfAccounts.Add(account);
        channel.ClearingAccountId = account.Id;
        await db.SaveChangesAsync(ct);
        return account.Id;
    }

    private static Task<bool> IsUsableAsync(AccountingDbContext db, Guid companyId, Guid accountId, CancellationToken ct)
        => db.ChartOfAccounts.AnyAsync(a => a.Id == accountId && a.CompanyId == companyId && a.IsActive, ct);

    /// <summary>รหัสถัดไปที่ว่างใน 11341–11349 (ข้ามรหัสที่ถูกใช้ รวมที่ลบแล้ว) · ครบ ⇒ null — เรียงด้วยตัวเลข ไม่ใช่ข้อความ</summary>
    internal static string? NextClearingCode(IEnumerable<string?> usedCodes)
    {
        var used = new HashSet<string>(usedCodes.Where(c => c != null).Select(c => c!.Trim()), StringComparer.Ordinal);
        for (var i = FirstClearingSuffix; i <= LastClearingSuffix; i++)
        {
            var code = ClearingCodeStem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!used.Contains(code)) return code;
        }
        return null;
    }

    internal static bool IsClearingCode(string? code)
    {
        if (code == null || code.Length != ClearingCodeStem.Length + 1 || !code.StartsWith(ClearingCodeStem, StringComparison.Ordinal))
            return false;
        var d = code[^1] - '0';
        return d >= FirstClearingSuffix && d <= LastClearingSuffix;
    }

    /// <summary>ชื่อผังพักของช่องทาง — "ลูกหนี้แพลตฟอร์ม {ชื่อช่องทาง}" (ตัดให้พอดีคอลัมน์ 256)</summary>
    internal static string ClearingAccountName(string? displayName)
    {
        var n = "ลูกหนี้แพลตฟอร์ม " + (string.IsNullOrWhiteSpace(displayName) ? "(ไม่มีชื่อ)" : displayName.Trim());
        return n.Length > 256 ? n[..256] : n;
    }

    private static string ClearingAccountNameEn(string? displayName)
    {
        var n = "Platform Receivable - " + (string.IsNullOrWhiteSpace(displayName) ? "(unnamed)" : displayName.Trim());
        return n.Length > 256 ? n[..256] : n;
    }
}

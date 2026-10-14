using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>หนึ่งบรรทัดงบทดลองยกมาที่จะตรวจ — <see cref="Key"/> = รหัสเดิมในไฟล์ (ใช้บอกผู้ใช้ว่าบรรทัดไหน)</summary>
/// <param name="Account">บัญชีปลายทางที่จับคู่แล้ว (null = ยังไม่จับคู่) · ต้องเป็นบัญชีของบริษัทนี้ (ผู้เรียกโหลดจากผังของบริษัทเท่านั้น)</param>
/// <param name="MappedButMissing">จับคู่ไว้กับ id ที่ไม่มีในผังของบริษัท (ถูกลบ/ปิด/ของบริษัทอื่น)</param>
public sealed record OpeningTbLine(string Key, string? Name, decimal Debit, decimal Credit, ImportChartAccount? Account, bool MappedButMissing = false);

/// <summary>ข้อเท็จจริงรอบงวดปลายทาง</summary>
/// <param name="AccountsWithExistingOpening">บัญชีที่มียอดยกมาในงวดนี้อยู่แล้ว (จะถูกทับ — ย้อนได้)</param>
public sealed record OpeningTbContext(
    bool PeriodExists,
    string? PeriodName,
    FiscalPeriodStatus? PeriodStatus,
    IReadOnlySet<Guid> AccountsWithExistingOpening);

/// <summary>ปัญหาหนึ่งข้อ · <see cref="Keys"/> = รหัสเดิมของบรรทัดที่เกี่ยว (ว่าง = ทั้งชุด)</summary>
public sealed record OpeningTbIssue(string Code, bool IsError, string Message, string? WayForward, IReadOnlyList<string> Keys);

public sealed record OpeningTbVerdict(
    IReadOnlyList<OpeningTbIssue> Errors,
    IReadOnlyList<OpeningTbIssue> Warnings,
    decimal TotalDebit,
    decimal TotalCredit)
{
    /// <summary>เดบิต − เครดิต (2 ตำแหน่ง) — บวก = ต้องลงเครดิตส่วนต่าง</summary>
    public decimal Difference => Math.Round(TotalDebit - TotalCredit, 2, MidpointRounding.AwayFromZero);
    public bool CanCommit => Errors.Count == 0;
}

/// <summary>
/// **กติกาตรวจงบทดลองยกมา — ตัวตัดสินเดียว** (รอบ 205 ทีม D · IMPORT_ETL_REVIEW §2.5 · คำตัดสินข้อ 150)
///
/// <para>ผู้เรียก: <c>MigrationWizardService.ValidateSessionAsync/CommitAsync</c> (หน้า migration wizard) และ
/// <c>OpeningTrialBalanceImportTarget.ValidateAsync</c> (ศูนย์นำเข้า) — สองทางเข้าต้องได้คำตัดสินเดียวกัน (R5) · รหัสคำเตือนชุดเดียวกัน ⇒
/// รับทราบที่ศูนย์นำเข้าแล้วส่งต่อให้ wizard commit ได้ตรงตัว</para>
///
/// <para><b>บล็อก (Error)</b> เฉพาะกรณีของข้อ 150 — ทุกข้อมีทางไปต่อ: ไม่มีบรรทัด · บรรทัดที่ยังไม่มีบัญชีปลายทางหลังทุกชั้นจับคู่ ·
/// บัญชีปลายทางไม่อยู่ในผังของบริษัท (ข้ามบริษัท/ถูกลบ) · <b>เดบิต ≠ เครดิต</b> (ทางไปต่อ: ลงส่วนต่างเข้าบัญชีผลต่างยกมาที่ผู้ใช้เลือก) ·
/// ไม่มีงวด/งวดปิด · บัญชีปลายทางเป็นบัญชีคุม (กติกาเดียวกับ <c>PostingAccountRule</c>) · <b>นอกนั้นเป็นคำเตือน</b>ที่รับทราบตอน commit:
/// หลายรหัสรวมเข้าบัญชีเดียว · ทับยอดยกมาเดิม · หมวดเลขหน้าไม่ตรงชนิดบัญชี · บรรทัดมีทั้งเดบิตและเครดิต · บรรทัดยอดศูนย์</para>
/// <para>G6: pure · ไม่มี I/O</para>
/// </summary>
public static class OpeningTrialBalanceRules
{
    public const string Empty = "tb-empty";
    public const string Unmapped = "tb-unmapped";
    public const string AccountInvalid = "tb-account-invalid";
    public const string Unbalanced = "tb-unbalanced";
    public const string PeriodMissing = "tb-period-missing";
    public const string PeriodClosed = "tb-period-closed";

    public const string DuplicateTarget = "tb-duplicate-target";
    public const string OverwriteExisting = "tb-overwrite-existing";
    public const string ClassMismatch = "tb-class-mismatch";
    public const string HeaderAccount = "tb-header-account";
    public const string BothSides = "tb-both-sides";
    public const string ZeroLine = "tb-zero-line";
    public const string DifferencePosted = "tb-difference-posted";

    /// <summary>รหัสบรรทัดผลต่างที่ระบบเพิ่มให้เมื่อผู้ใช้เลือกบัญชีผลต่างยกมา (ทางไปต่อของ <see cref="Unbalanced"/>)</summary>
    public const string DifferenceLineKey = "ผลต่างยกมา";

    public static OpeningTbVerdict Evaluate(IReadOnlyList<OpeningTbLine> lines, OpeningTbContext ctx)
    {
        var errors = new List<OpeningTbIssue>();
        var warnings = new List<OpeningTbIssue>();
        var dr = Math.Round(lines.Sum(l => l.Debit), 2, MidpointRounding.AwayFromZero);
        var cr = Math.Round(lines.Sum(l => l.Credit), 2, MidpointRounding.AwayFromZero);

        if (lines.Count == 0)
        {
            errors.Add(new(Empty, true, "ยังไม่มีบรรทัดงบทดลอง", "วางข้อมูลหรืออัปโหลดไฟล์งบทดลองก่อน", Array.Empty<string>()));
            return new(errors, warnings, 0m, 0m);
        }

        if (!ctx.PeriodExists)
            errors.Add(new(PeriodMissing, true, "ยังไม่ได้เลือกงวดบัญชีที่จะเก็บยอดยกมา",
                "เลือกงวดบัญชีปลายทาง (ปกติคือเดือนแรกที่เริ่มใช้ระบบ) แล้วตรวจอีกครั้ง", Array.Empty<string>()));
        else if (ctx.PeriodStatus is FiscalPeriodStatus.Closed or FiscalPeriodStatus.Locked)
            errors.Add(new(PeriodClosed, true, $"งวด {ctx.PeriodName} ปิดแล้ว — ลงยอดยกมาไม่ได้",
                "เปิดงวดที่หน้า งวดบัญชี ก่อน หรือเลือกงวดอื่น", Array.Empty<string>()));

        var unmapped = lines.Where(l => l.Account == null && !l.MappedButMissing).Select(l => l.Key).ToList();
        if (unmapped.Count > 0)
            errors.Add(new(Unmapped, true,
                $"ยังไม่ได้เลือกบัญชีปลายทาง {unmapped.Count} บรรทัด: {Preview(unmapped)}",
                "กด \"ใช้คำแนะนำ\" หรือเลือกบัญชีในคอลัมน์ขวา — บัญชีที่ยังไม่มีในผัง ให้สร้างที่หน้าผังบัญชีก่อน", unmapped));

        var missing = lines.Where(l => l.MappedButMissing).Select(l => l.Key).ToList();
        if (missing.Count > 0)
            errors.Add(new(AccountInvalid, true,
                $"บัญชีปลายทางของ {missing.Count} บรรทัดไม่อยู่ในผังบัญชีของบริษัทนี้แล้ว (ถูกลบ/ปิดใช้): {Preview(missing)}",
                "เลือกบัญชีปลายทางใหม่ให้บรรทัดเหล่านี้", missing));

        if (dr != cr)
        {
            var diff = Math.Round(dr - cr, 2, MidpointRounding.AwayFromZero);
            errors.Add(new(Unbalanced, true,
                $"ยอดเดบิต {dr:N2} ไม่เท่ากับเครดิต {cr:N2} (ต่างกัน {Math.Abs(diff):N2})",
                $"แก้ยอดในไฟล์ให้ลงตัว หรือเลือกบัญชีผลต่างยกมา ระบบจะลง{(diff > 0 ? "เครดิต" : "เดบิต")} {Math.Abs(diff):N2} ให้ (คุณเป็นผู้เลือกบัญชี)",
                Array.Empty<string>()));
        }

        // ── คำเตือน (รับทราบตอน commit — ไม่บล็อก) ──
        var dup = lines.Where(l => l.Account != null).GroupBy(l => l.Account!.AccountId).Where(g => g.Count() > 1).ToList();
        foreach (var g in dup)
        {
            var a = g.First().Account!;
            warnings.Add(new(DuplicateTarget, false,
                $"บัญชี {a.Code} {a.Name} รับยอดจาก {g.Count()} รหัสเดิม ({Preview(g.Select(x => x.Key).ToList())}) — ระบบจะรวมยอด",
                "ถ้าไม่ได้ตั้งใจรวม เลือกบัญชีปลายทางแยกกัน", g.Select(x => x.Key).ToList()));
        }

        var overwrite = lines.Where(l => l.Account != null && ctx.AccountsWithExistingOpening.Contains(l.Account.AccountId))
            .Select(l => l.Key).ToList();
        if (overwrite.Count > 0)
            warnings.Add(new(OverwriteExisting, false,
                $"งวด {ctx.PeriodName} มียอดยกมาของ {overwrite.Count} บัญชีอยู่แล้ว ({Preview(overwrite)}) — จะถูกแทนที่ด้วยยอดชุดนี้",
                "ย้อนการนำเข้าได้ — ระบบเก็บยอดเดิมไว้และคืนให้ตอนย้อน", overwrite));

        var mismatch = lines.Where(l => l.Account != null
                && ImportAccountCodeResolver.ClassFromCode(l.Key) is { } cls && cls != l.Account.Type)
            .Select(l => l.Key).ToList();
        if (mismatch.Count > 0)
            warnings.Add(new(ClassMismatch, false,
                $"หมวดตามเลขหน้าของรหัสเดิมไม่ตรงกับชนิดบัญชีปลายทาง {mismatch.Count} บรรทัด: {Preview(mismatch)}",
                "ตรวจว่าเลือกบัญชีถูก (เช่น รหัส 2xxx ควรเป็นหนี้สิน)", mismatch));

        var header = lines.Where(l => l.Account is { IsPostable: false }).Select(l => l.Key).ToList();
        if (header.Count > 0)
            // บัญชีคุม = บล็อก (กติกาเดียวกับ PostingAccountRule ของเส้นหลัก 2026-10-14: ยอดคุมต้องมาจากผลรวมบัญชีย่อย — คำตัดสินข้อ 150 (2))
            errors.Add(new(HeaderAccount, true,
                $"บัญชีปลายทางเป็นบัญชีคุม (มีบัญชีย่อย) {header.Count} บรรทัด: {Preview(header)} — ลงยอดตรงไม่ได้",
                "เลือกบัญชีย่อยใต้บัญชีนั้นแทน", header));

        var both = lines.Where(l => l.Debit > 0 && l.Credit > 0).Select(l => l.Key).ToList();
        if (both.Count > 0)
            warnings.Add(new(BothSides, false,
                $"มีทั้งเดบิตและเครดิตในบรรทัดเดียว {both.Count} บรรทัด: {Preview(both)} — ระบบเก็บทั้งสองข้าง (ยอดสุทธิถูกต้อง)",
                null, both));

        var zero = lines.Where(l => l.Debit == 0 && l.Credit == 0).Select(l => l.Key).ToList();
        if (zero.Count > 0)
            warnings.Add(new(ZeroLine, false, $"บรรทัดยอดศูนย์ {zero.Count} บรรทัด: {Preview(zero)} — จะบันทึกยอด 0", null, zero));

        if (lines.Any(l => l.Key == DifferenceLineKey))
            warnings.Add(new(DifferencePosted, false,
                "มีบรรทัดผลต่างยกมาที่ระบบเพิ่มตามที่คุณเลือก — ตรวจสอบสาเหตุของผลต่างกับระบบเดิมภายหลัง", null,
                new[] { DifferenceLineKey }));

        return new(errors, warnings, dr, cr);
    }

    /// <summary>ยอดบรรทัดผลต่างที่ทำให้ลงตัว (ผู้ใช้เลือกบัญชี) — ส่วนต่างบวก (เดบิตเกิน) ⇒ ลงเครดิต</summary>
    public static (decimal Debit, decimal Credit) BalancingAmounts(decimal totalDebit, decimal totalCredit)
    {
        var diff = Math.Round(totalDebit - totalCredit, 2, MidpointRounding.AwayFromZero);
        return diff > 0 ? (0m, diff) : (-diff, 0m);
    }

    /// <summary>ป้ายสั้นภาษาไทยของรหัส (chip/หัวข้อคำเตือน) — เซิร์ฟเวอร์ตัดสิน</summary>
    public static string Label(string code) => code switch
    {
        Empty => "ไม่มีข้อมูล",
        Unmapped => "ยังไม่เลือกบัญชี",
        AccountInvalid => "บัญชีปลายทางใช้ไม่ได้",
        Unbalanced => "เดบิต ≠ เครดิต",
        PeriodMissing => "ยังไม่เลือกงวด",
        PeriodClosed => "งวดปิดแล้ว",
        DuplicateTarget => "หลายรหัสรวมเป็นบัญชีเดียว",
        OverwriteExisting => "ทับยอดยกมาเดิม",
        ClassMismatch => "หมวดบัญชีไม่ตรง",
        HeaderAccount => "ลงที่บัญชีคุม",
        BothSides => "มีทั้งเดบิตและเครดิต",
        ZeroLine => "ยอดศูนย์",
        DifferencePosted => "มีบรรทัดผลต่างยกมา",
        _ => code,
    };

    /// <summary>คำเตือนที่ยังไม่ได้รับทราบ (ว่าง = commit ได้)</summary>
    public static IReadOnlyList<OpeningTbIssue> Unacknowledged(OpeningTbVerdict v, IEnumerable<string>? acknowledged)
    {
        var ack = new HashSet<string>(acknowledged ?? Array.Empty<string>(), StringComparer.Ordinal);
        return v.Warnings.Where(w => !ack.Contains(w.Code)).ToList();
    }

    private static string Preview(IReadOnlyList<string> keys)
        => string.Join(", ", keys.Take(5)) + (keys.Count > 5 ? $" และอีก {keys.Count - 5}" : "");
}

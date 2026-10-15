using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ยอดยกมาจากการปิดปี — กติกาตัวเดียว** (ฝ่ายค้านรอบส่งออก 2026-10-15 P1-1 · erp-review/2026-10-14/EXPORT_AUDIT.md
/// §"ฝ่ายค้านรอบส่งออก")
///
/// <para><b>บั๊กที่มา:</b> <c>RollOpeningBalancesAsync</c> เดิมคิด "ยอดยกมา ม.ค. ปี N" = รายการทุกใบตั้งแต่เปิดบริษัทก่อน 1 ม.ค. N
/// + ยอดยกมา ม.ค. ปี N−1 ซึ่ง<b>รวมทุกปีก่อนหน้าไว้แล้ว</b> ⇒ ตั้งแต่ปิดปีครั้งที่สอง ปีเก่าถูกนับซ้ำทุกปี · งบทดลอง/บัญชีแยกประเภท/
/// ยอด ณ วันที่ (แดชบอร์ด/งบกระแสเงินสด) อ่านฐานเดียวกันจึงผิด "ตรงกัน" ทั้งระบบ</para>
///
/// <para><b>นิยามใหม่ (เท่ากับงบทดลอง ณ 31 ธ.ค. N−1):</b> ฐาน = งวดล่าสุดที่มียอดยกมาและเริ่ม<b>ก่อน</b> 1 ม.ค. N (ยอดยกมาปิดปีก่อน
/// หรือยอดนำเข้าจากระบบเดิม — รวมยอดนำเข้ากลางปีที่เดิมไม่ถูกนับเลย) + รายการ (สินทรัพย์/หนี้สิน/ทุน) ตั้งแต่วันเริ่มงวดฐานถึงก่อน 1 ม.ค. N ·
/// ไม่มีฐาน = รายการตั้งแต่เปิดบริษัท</para>
///
/// <para><b>ของที่เก็บผิดไปแล้ว</b> (กฎ F2 ข้อ 9): <c>FindOpeningRollMismatchesAsync</c> (รายงานอ่านอย่างเดียว) →
/// <c>RepairOpeningRollAsync</c> (ซ่อมเฉพาะปีที่ผู้ใช้เลือก + audit chain) · ไม่มีงานไหนซ่อมเองตอนเปิดระบบ ·
/// งวดที่มียอดนำเข้า (<see cref="IsImported"/>) ไม่ถูกซ่อมอัตโนมัติ (ผู้ใช้ตั้งใจลงยอดเอง)</para>
///
/// <para><b>รอบ 2 (ฝ่ายค้าน 2026-10-15 P1-2 · P2-6) — ปิดปีและยกยอด "ฐานเดียวกับงบทดลอง":</b>
/// <list type="number">
/// <item><b>ใบปิดปี</b> ปิดรายได้/ค่าใช้จ่ายของ <see cref="PnlCloseWindow"/>: ฐานยอดยกมา (เช่น ยอดนำเข้า 1 ก.ค. ที่มีกำไรขาดทุน ม.ค.–มิ.ย.)
///   ที่เริ่ม<b>ในรอบบัญชีนี้</b> ⇒ ปิดยอดรายได้/ค่าใช้จ่ายของฐาน + รายการตั้งแต่วันเริ่มฐาน · ไม่มีฐานในรอบ ⇒ รายการตั้งแต่ต้นรอบ (เดิม) ·
///   <b>รายการที่ลงวันที่ก่อนฐานในรอบเดียวกัน ไม่ถูกนับทั้งในใบปิดและในยอดยก</b> (ฐานคือยอด ณ วันนั้นแล้ว — งบทดลองก็ไม่นับ)</item>
/// <item><b>ยอดยกมา</b> = งบทดลอง ณ สิ้นรอบ (ฐานล่าสุดก่อนต้นรอบถัดไป + ทุกรายการตั้งแต่วันเริ่มฐาน รวมใบปิด) แล้ว <see cref="CarryForward"/>:
///   ยกเฉพาะสินทรัพย์/หนี้สิน/ทุน · รายได้/ค่าใช้จ่ายที่ยังค้าง (ปีก่อนที่ไม่ได้ปิด · แถวกำไรขาดทุนที่ปิดปีรุ่นเก่ายกมา) รวมเข้ากำไรสะสม
///   ⇒ ยอดยกมาลงตัวเสมอเมื่องบทดลองลงตัว และไม่มีแถวรายได้/ค่าใช้จ่าย</item>
/// <item>งวดเป้าหมาย = งวดแรกของรอบบัญชีถัดไป (<see cref="FiscalYear.RangeFor"/> — รอบ เม.ย.–มี.ค. ยกไป 1 เม.ย. ไม่ใช่ 1 ม.ค.)</item>
/// <item>ปิดปี · ยกยอดด้วยมือ · ซ่อม ใช้ล็อกเดียวกัน (<see cref="LockKey"/>) · ยกยอดด้วยมือต้องปิดปีก่อน และไม่เขียนทับงวดที่มียอดนำเข้า</item>
/// </list></para>
/// </summary>
public static class OpeningRoll
{
    public const string RuleCode = "GL-OPENING-ROLL";

    /// <summary>หมายเหตุที่ตัวลงยอดยกมาจากระบบเดิม (<c>MigrationWizardService.CommitAsync</c>) ประทับ — ตัวตั้งเดียว</summary>
    public const string ImportedNotePrefix = "Migrated via session";

    /// <summary>ต้นข้อความหมายเหตุของแถวที่การปิดปีเขียน (<see cref="RolledNote"/>)</summary>
    private const string RolledNotePrefix = "ยกมาจากการปิดปี ";

    /// <summary>หมายเหตุของแถวที่การปิดปีเขียน (แถวรุ่นเก่าเป็น null)</summary>
    public static string RolledNote(int year) => RolledNotePrefix + (year - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>action ของ audit เมื่อการซ่อมยกเลิกแถวยกยอดรุ่นเก่าของงวด ม.ค. (<see cref="IsLegacyCalendarRollPeriod"/> · รอบ 3 P2-4)</summary>
    public const string LegacyJanNeutralizedAction = "opening-roll-legacy-jan-neutralized";

    /// <summary>ป้ายบรรทัดกำไรสะสมของใบปิดปีที่ปิดกำไรขาดทุน<b>ของปีก่อน</b>ที่ค้างในฐาน ณ ต้นรอบ (<see cref="AnchorPnlScope.PriorYears"/> · รอบ 3 P2-3)</summary>
    public const string PriorYearsPnlLabel = "กำไร(ขาดทุน)ของปีก่อน";

    /// <summary>
    /// แถวยอดยกมานี้เขียนโดย<b>การปิดปี/ยกยอด</b> (ไม่ใช่ยอดนำเข้า) — หมายเหตุ <see cref="RolledNote"/> (825f5c0e เป็นต้นไป) หรือ <c>null</c>
    /// (การยกยอดก่อน 825f5c0e ไม่ประทับหมายเหตุ · ผู้เขียนแถว OpeningBalances มีสองทางเท่านั้น: ตัวย้ายระบบ (ประทับ <see cref="ImportedNotePrefix"/>)
    /// และการยกยอด — ตรวจ 2026-10-15) · ใช้ตัดสินว่าแถวไหน "ระบบเขียน" จึงซ่อมได้ (รอบ 3 P2-4)
    /// </summary>
    public static bool IsRolled(string? notes)
        => notes == null || notes.StartsWith(RolledNotePrefix, StringComparison.Ordinal);

    /// <summary>
    /// งวดนี้คืองวด "ยกยอดรุ่นเก่าไป ม.ค." ของบริษัทที่รอบบัญชีไม่ใช่ปีปฏิทิน (pure · รอบ 3 P2-4): การปิดปีก่อน de823512 ยกยอดไป 1 ม.ค. เสมอ ⇒
    /// รอบ เม.ย.–มี.ค. มีแถวยกยอดค้างกลางรอบ (งบทดลอง ม.ค.–มี.ค. อ่านฐานนี้) · ตอนนี้ยกไปงวดแรกของรอบ ⇒ งวด ม.ค. ที่ไม่ใช่ต้นรอบ +
    /// แถวทุกแถวเป็นแถวยกยอด (<see cref="IsRolled"/> — มีแถวนำเข้าปน = ผู้ใช้ลงเอง ไม่แตะ) = ของรุ่นเก่าที่ต้องยกเลิกตอนซ่อม
    /// </summary>
    public static bool IsLegacyCalendarRollPeriod(int periodMonth, int fiscalYearStartMonth, IEnumerable<string?> rowNotes)
        => periodMonth == 1 && FiscalYear.NormalizeStartMonth(fiscalYearStartMonth) != 1
            && rowNotes.Any() && rowNotes.All(IsRolled);

    /// <summary>
    /// ปีของรายงานตรวจ/ซ่อม (<c>OpeningRollCheckYear.Year</c> = ปีที่ปิด + 1) ที่ยกเลิกงวดยกยอดรุ่นเก่าไป ม.ค. ที่เริ่ม <paramref name="legacyPeriodStart"/>
    /// (pure · ตัวตั้งเดียว — รายงาน/ซ่อมกับด่านปิดปี/ยกยอดด้วยมือใช้ร่วมกัน · รอบ 4 P2-a): งวด ม.ค. ที่การปิดปี N รุ่นเก่าเขียน (ม.ค. N+1) อยู่ในรอบ N
    /// ⇒ ซ่อมปี N+1
    /// </summary>
    public static int LegacyRepairYear(DateTime legacyPeriodStart, int fiscalYearStartMonth)
        => FiscalYear.FiscalYearOf(legacyPeriodStart, fiscalYearStartMonth) + 1;

    /// <summary>
    /// ข้อความของด่านปิดปี/ยกยอดด้วยมือเมื่อยังมีงวดยกยอดรุ่นเก่าไป ม.ค. ที่ยังไม่ซ่อม (pure · รอบ 4 P2-a) — ปิดปีตอนนี้จะใช้งวดนั้นเป็นฐาน (คิดแบบปีปฏิทิน)
    /// ⇒ ยอดยกมาของรอบถัดไปคลาดเท่ากำไรขาดทุนช่วงต้นรอบถึง ธ.ค. · ทางไปต่อ = ซ่อมปีที่ระบุก่อน (หน้าตรวจข้อมูล · ปุ่ม "แก้")
    /// </summary>
    public static string LegacyRollBlockMessage(IEnumerable<(DateTime PeriodStart, int RepairYear, int Rows)> periods)
        => "ยังมียอดยกมารุ่นเก่าที่การปิดปีก่อนหน้าเขียนไว้งวด ม.ค. ทั้งที่รอบบัญชีไม่ใช่ปีปฏิทิน: "
            + string.Join(" · ", periods.Select(p => $"ม.ค. {p.PeriodStart.Year + 543} ({p.Rows} แถว — ซ่อมปี {p.RepairYear + 543})"))
            + " — ปิดปี/ยกยอดตอนนี้จะคิดบนยอดนั้น (คิดแบบปีปฏิทิน) ⇒ ยอดยกมาของรอบถัดไปผิด · ยังไม่มีอะไรถูกบันทึก · "
            + "ทางไปต่อ: ซ่อมยอดยกมารุ่นเก่าก่อน (เจ้าของบริษัท — หน้า \"ตรวจข้อมูลจากบั๊กที่แก้แล้ว\" หมวดยอดยกมาจากการปิดปี กด \"แก้\" ปีที่ระบุ "
            + "⇒ ระบบเขียนยอดยกมางวดแรกของรอบ + ยกเลิกแถวรุ่นเก่า · บันทึกประวัติ) แล้วปิดปี/ยกยอดอีกครั้ง";

    /// <summary>แถวยอดยกมานี้มาจากการนำเข้า (ไม่ใช่จากการปิดปี)</summary>
    public static bool IsImported(string? notes)
        => notes != null && notes.StartsWith(ImportedNotePrefix, StringComparison.Ordinal);

    /// <summary>ยอดยกมาใหม่ = ฐาน + รายการเคลื่อนไหว (ต่อบัญชี · เดบิต/เครดิตรวม) · บัญชีที่เป็น 0 ทั้งสองข้างถูกตัดทิ้ง</summary>
    public static Dictionary<Guid, (decimal Debit, decimal Credit)> Combine(
        IReadOnlyDictionary<Guid, (decimal Debit, decimal Credit)> anchor,
        IReadOnlyDictionary<Guid, (decimal Debit, decimal Credit)> movements)
    {
        var result = new Dictionary<Guid, (decimal Debit, decimal Credit)>();
        foreach (var id in anchor.Keys.Union(movements.Keys))
        {
            anchor.TryGetValue(id, out var a);
            movements.TryGetValue(id, out var m);
            var d = a.Debit + m.Debit;
            var c = a.Credit + m.Credit;
            if (d == 0m && c == 0m) continue;
            result[id] = (d, c);
        }
        return result;
    }

    /// <summary>ล็อกต่อบริษัทของ "ปิดปี · ยกยอดด้วยมือ · ซ่อมยอดยกมา" — ตัวตั้งเดียว (xact lock: ปล่อยเมื่อธุรกรรมจบ)</summary>
    public static long LockKey(Guid companyId) => AdvisoryLockKey.For(companyId, AdvisoryLockKey.OpeningRoll, "ledger");

    /// <summary>
    /// ช่วงที่ใบปิดปีต้องปิดรายได้/ค่าใช้จ่าย (pure) · <paramref name="anchorStart"/> = ฐานล่าสุดที่เริ่มก่อนสิ้นรอบ:
    /// <list type="bullet">
    /// <item>ฐานเริ่ม<b>หลัง</b>ต้นรอบ (ย้ายระบบกลางปี — ยอดนำเข้า 1 ก.ค. มีกำไรขาดทุน ม.ค.–มิ.ย.) ⇒ (วันเริ่มฐาน, <see cref="AnchorPnlScope.CurrentYear"/>):
    ///   กำไรขาดทุนของฐานเป็นของปีนี้ — นับในกำไรของปี</item>
    /// <item>ฐานเริ่ม<b>ตรงต้นรอบ</b> (งบทดลองก่อนปิดบัญชี ณ 31 ธ.ค. ที่นำเข้าวันที่ 1 ม.ค. · แถวกำไรขาดทุนที่การปิดปีรุ่นเก่ายกมา) ⇒
    ///   (ต้นรอบ, <see cref="AnchorPnlScope.PriorYears"/>): กำไรขาดทุนของฐาน<b>เป็นของปีก่อน</b> — ใบปิดปียังปิดเข้ากำไรสะสม (งบทดลองสิ้นปีต้องเป็นศูนย์)
    ///   แต่<b>แยกบรรทัด</b>และไม่นับในกำไรของปี (<c>TransferredAmount</c>) · รอบ 3 P2-3: เดิม <c>&gt;=</c> ⇒ นับเป็นกำไรปีนี้ ⇒ กำไรประจำปีเกินจริง</item>
    /// <item>ไม่มีฐาน/ฐานเริ่มก่อนรอบ ⇒ (ต้นรอบ, <see cref="AnchorPnlScope.None"/>) — ยอดค้างของฐานรวมเข้ากำไรสะสมตอนยกยอด (<see cref="CarryForward"/>)</item>
    /// </list>
    /// </summary>
    public static (DateTime From, AnchorPnlScope AnchorPnl) PnlCloseWindow(DateTime fiscalYearStart, DateTime? anchorStart)
        => anchorStart is not DateTime a || a < fiscalYearStart ? (fiscalYearStart, AnchorPnlScope.None)
            : a > fiscalYearStart ? (a, AnchorPnlScope.CurrentYear)
            : (fiscalYearStart, AnchorPnlScope.PriorYears);

    /// <summary>บัญชีในงบทดลองที่<b>ยกยอดไม่ได้</b> แต่ยอดสุทธิ ≠ 0 (pure · รอบ 3 P2-6 · รอบ 4 P2-b) — ผู้เรียกต้องล้มดังพร้อมชื่อผัง
    /// (<see cref="DeletedWithBalanceMessage"/>) <b>ก่อน</b>เขียนอะไร: ไม่รู้ประเภท (ไม่พบผังในบริษัทเลย) หรือ<b>ผังสินทรัพย์/หนี้สิน/ทุนที่ถูกลบ</b>
    /// (<paramref name="deletedAccountIds"/> — ยกไปผังที่ลบแล้ว = ยอดยกมาที่ไม่มีใครเห็น) · <b>ผังรายได้/ค่าใช้จ่ายที่ถูกลบไม่นับ</b> — ใบปิดปีล้างให้
    /// (รอบ 4 P2-b: เดิมใบปิดปี join ผังผ่านตัวกรอง soft-delete ⇒ ไม่ล้าง ⇒ ปิดปีถัดไปล้มทุกครั้ง) และยอดที่ค้างรวมเข้ากำไรสะสม (<see cref="CarryForward"/>)</summary>
    public static List<Guid> UnknownAccountsWithBalance(
        IReadOnlyDictionary<Guid, (decimal Debit, decimal Credit)> trialBalance, IReadOnlyDictionary<Guid, AccountType> types,
        IReadOnlySet<Guid>? deletedAccountIds = null)
        => trialBalance.Where(kv => kv.Value.Debit - kv.Value.Credit != 0m && !CanCarry(kv.Key, types, deletedAccountIds)).Select(kv => kv.Key).ToList();

    /// <summary>ยกยอด/รวมเข้ากำไรสะสมได้: รู้ประเภท และ (ไม่ถูกลบ หรือเป็นรายได้/ค่าใช้จ่าย)</summary>
    private static bool CanCarry(Guid id, IReadOnlyDictionary<Guid, AccountType> types, IReadOnlySet<Guid>? deletedAccountIds)
        => types.TryGetValue(id, out var t)
            && (deletedAccountIds == null || !deletedAccountIds.Contains(id) || t == AccountType.Revenue || t == AccountType.Expense);

    /// <summary>action ของ audit เมื่อเจ้าของกู้คืนผังที่ถูกลบ (<c>AccountingService.RestoreAccountAsync</c> · รอบ 4 P2-b)</summary>
    public const string AccountRestoreAction = "coa-restore";

    /// <summary>ข้อความ "ผังที่ถูกลบยังมียอด" + ทางไปต่อ (ตัวตั้งเดียว — ปิดปี · ยกยอดด้วยมือ · รายงานตรวจ/ซ่อมใช้ข้อความเดียวกัน) ·
    /// รอบ 4 P2-b: ทางไปต่อชี้ปุ่มกู้คืนที่มีจริง (หน้าตรวจข้อมูล → ปุ่ม "แก้" ของผังนั้น = <c>POST accounting/accounts/{id}/restore</c> · เจ้าของ)</summary>
    public static string DeletedWithBalanceMessage(DateTime asOfExclusive, IEnumerable<(string Code, string Name, decimal NetDebit)> accounts)
        => $"ผังบัญชีที่ถูกลบแล้วยังมียอดคงเหลือ ณ สิ้นวันที่ {ThaiDate.ToThaiDisplayString(asOfExclusive.AddDays(-1))}: "
            + string.Join(" · ", accounts.Select(a => $"{(a.Code.Length == 0 ? "(ไม่พบรหัส)" : a.Code)} {a.Name} ({(a.NetDebit >= 0 ? "เดบิต" : "เครดิต")} {Math.Abs(a.NetDebit):N2})"))
            + " — ยกยอดไม่ได้เพราะยอดนี้จะหายจากยอดยกมา (ยอดยกมาไม่ลงตัว) · ยังไม่มีอะไรถูกบันทึก · ทางไปต่อ: กู้คืนผังนั้น (เจ้าของบริษัท — หน้า \"ตรวจข้อมูลจากบั๊กที่แก้แล้ว\" "
            + "หมวดยอดยกมาจากการปิดปี กดปุ่ม \"แก้\" ของผังนั้น · บันทึกประวัติ) แล้วปิดปี/ยกยอด/ซ่อมอีกครั้ง — ยอดยกไปตามปกติ · "
            + "ถ้าไม่ใช้ผังนั้นแล้ว: หลังกู้คืนโอนยอดไปผังอื่นด้วยสมุดรายวันแล้ว \"ปิดใช้งาน\" แทนการลบ";

    /// <summary>
    /// งบทดลอง ณ สิ้นรอบ → ยอดยกมาของรอบถัดไป (pure): ยกเฉพาะสินทรัพย์/หนี้สิน/ทุน <b>สุทธิฝั่งเดียว</b> · ยอดสุทธิรายได้/ค่าใช้จ่ายที่ยังค้างรวมเข้า
    /// <paramref name="retainedEarningsAccountId"/> · บัญชีที่ยกไม่ได้ (<see cref="UnknownAccountsWithBalance"/>) และยอดสุทธิศูนย์ถูกตัด · <b>ยกไม่ได้แต่มียอด = ล้มดัง</b> (รอบ 3 P2-6 · รอบ 4: ผังสินทรัพย์/หนี้สิน/ทุนที่ถูกลบ —
    /// ผู้เรียกตรวจ <see cref="UnknownAccountsWithBalance"/> ก่อนเพื่อบอกชื่อผัง) · ค้างแต่ไม่มีผังกำไรสะสม = ล้มดัง
    /// </summary>
    public static Dictionary<Guid, (decimal Debit, decimal Credit)> CarryForward(
        IReadOnlyDictionary<Guid, (decimal Debit, decimal Credit)> trialBalance,
        IReadOnlyDictionary<Guid, AccountType> types,
        Guid? retainedEarningsAccountId,
        IReadOnlySet<Guid>? deletedAccountIds = null)
    {
        var result = new Dictionary<Guid, (decimal Debit, decimal Credit)>();
        var pnlNetDebit = 0m;
        foreach (var (id, amount) in trialBalance)
        {
            if (!CanCarry(id, types, deletedAccountIds))
            {
                if (amount.Debit - amount.Credit != 0m)
                    throw new BusinessRuleException(
                        "มีผังบัญชีที่ถูกลบแล้วแต่ยังมียอดคงเหลือ — ยกยอดไม่ได้ (ยอดจะหายจากยอดยกมา) · กู้คืนผังนั้นที่หน้าตรวจข้อมูลก่อน", RuleCode);
                continue;
            }
            var type = types[id];
            if (type == AccountType.Revenue || type == AccountType.Expense)
            {
                pnlNetDebit += amount.Debit - amount.Credit;
                continue;
            }
            result[id] = amount;
        }
        if (pnlNetDebit != 0m)
        {
            if (retainedEarningsAccountId is not Guid re)
                throw new BusinessRuleException(
                    "ยังมียอดรายได้/ค่าใช้จ่ายที่ไม่ได้ปิดเข้ากำไรสะสม แต่ไม่รู้ผังกำไรสะสม — ปิดปีของรอบก่อนหน้าก่อน (ปุ่มปิดปีจะยกยอดให้เอง)", RuleCode);
            result.TryGetValue(re, out var cur);
            result[re] = (cur.Debit + pnlNetDebit, cur.Credit);
        }
        // ยอดยกมาเก็บ "สุทธิฝั่งเดียว" (ยอดยกมาคือยอดคงเหลือ ไม่ใช่ยอดหมุนเวียน — เดิมเก็บเดบิต/เครดิตรวมทั้งสองฝั่ง เช่น เงินสด 110,000/2,000 ·
        // ผู้ใช้แก้ฝั่งเดียวแล้วสุทธิเพี้ยน) · งบทดลอง/รายงานตรวจอ่านสุทธิ ⇒ แถวเก่าที่เก็บสองฝั่งไม่ผิด ไม่ต้องซ่อม
        return result
            .Select(kv => (kv.Key, Net: kv.Value.Debit - kv.Value.Credit))
            .Where(x => x.Net != 0m)
            .ToDictionary(x => x.Key, x => x.Net > 0m ? (x.Net, 0m) : (0m, -x.Net));
    }

    /// <summary>บัญชีที่ยอดสุทธิที่เก็บไว้ ≠ ยอดที่ควรเป็น (เทียบสุทธิ เพราะงบทดลองอ่านสุทธิ — เดบิต/เครดิตรวมต่างกันแต่สุทธิเท่ากัน = ไม่ผิด)</summary>
    public static List<(Guid AccountId, (decimal Debit, decimal Credit) Stored, (decimal Debit, decimal Credit) Expected)> Diff(
        IReadOnlyDictionary<Guid, (decimal Debit, decimal Credit)> stored,
        IReadOnlyDictionary<Guid, (decimal Debit, decimal Credit)> expected)
    {
        var list = new List<(Guid, (decimal, decimal), (decimal, decimal))>();
        foreach (var id in stored.Keys.Union(expected.Keys))
        {
            stored.TryGetValue(id, out var s);
            expected.TryGetValue(id, out var e);
            if (s.Debit - s.Credit != e.Debit - e.Credit) list.Add((id, s, e));
        }
        return list;
    }
}

/// <summary>กำไรขาดทุนในฐานยอดยกมาเป็นของรอบไหน (<see cref="OpeningRoll.PnlCloseWindow"/>)</summary>
public enum AnchorPnlScope
{
    /// <summary>ไม่ปิดในใบปิดปีนี้ (ไม่มีฐาน/ฐานเริ่มก่อนรอบ)</summary>
    None = 0,
    /// <summary>ฐานเริ่มกลางรอบ — กำไรขาดทุนของฐานเป็นของปีนี้</summary>
    CurrentYear = 1,
    /// <summary>ฐานเริ่มตรงต้นรอบ — กำไรขาดทุนของฐานเป็นของปีก่อน (ปิดเข้ากำไรสะสม แยกบรรทัด ไม่นับในกำไรของปี)</summary>
    PriorYears = 2,
}

/// <summary>ผังที่ถูกลบแต่ยังมียอด ⇒ ยกยอดไม่ได้ (รอบ 4 P2-b) — ข้อความเดียวกับ <see cref="OpeningRoll.DeletedWithBalanceMessage"/> + รายชื่อผัง
/// ให้รายงานตรวจ/หน้าตรวจข้อมูลมีปุ่มกู้คืนรายผัง (ไม่ต้องแกะข้อความ)</summary>
public sealed class DeletedAccountsWithBalanceException : BusinessRuleException
{
    public IReadOnlyList<OpeningRollDeletedAccount> Accounts { get; }

    public DeletedAccountsWithBalanceException(string message, IReadOnlyList<OpeningRollDeletedAccount> accounts)
        : base(message, OpeningRoll.RuleCode) => Accounts = accounts;
}

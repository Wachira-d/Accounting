using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 209 · P5 (R-209-C5 · คำตัดสินข้อ 168 ฎ) — สมาชิก "ไม่จำกัดครั้งตามช่วงเวลา" + พักบัตร/ขยายอายุ/โอน — pure ไม่แตะฐานข้อมูล
///
/// <para><b>รับรู้รายได้ (TFRS for NPAEs บทที่ 6 — บริการที่ให้ตลอดช่วงเวลา):</b> ลูกค้าได้สิทธิ์ใช้บริการทุกวันไม่ขึ้นกับจำนวนครั้ง ⇒ รับรู้ตาม<b>เวลาที่ผ่านไป</b>
/// เส้นตรงรายวัน: รายวัน = round(สัญญา ÷ วันใช้งาน, 2, AwayFromZero) · สะสมถึงวันที่ k = รายวัน × k · <b>วันสุดท้ายรับส่วนที่เหลือทั้งหมด</b> (Σ = สัญญาทุกสตางค์) ·
/// วันพัก (freeze) ไม่นับเป็นวันใช้งาน (ไม่รับรู้ช่วงพัก) และเลื่อนวันหมดอายุออกเท่าจำนวนวัน · ขยายอายุ = ตั้งฐานใหม่ (ส่วนที่ยังไม่รับรู้กระจายบนวันที่เหลือ + วันที่เพิ่ม) ·
/// การเข้าเรียน/no-show ของสมาชิกไม่จำกัด <b>ไม่มี JE</b> (ไม่ผูกการใช้) · ลงบัญชีรายเดือน (หนึ่ง JE ต่อเดือนปฏิทินต่อบัตร) ผ่าน <see cref="PeriodRecognitionPlan"/></para>
///
/// <para><b>VAT:</b> เหมือนบัตรครั้ง — ภาษีขายเกิดครบตอนขาย (§78/1 วันรับเงิน) · การรับรู้ตามวันไม่มี VAT เพิ่ม</para>
/// </summary>
public static partial class CourseCreditMath
{
    public const string MembershipRuleCode = "TFRS-NPAE-6-MEMBERSHIP";
    public const string MembershipLegalReference = "TFRS for NPAEs บทที่ 6 (บริการที่ให้ตลอดช่วงเวลา — รับรู้เส้นตรงตามวันที่ผ่านไป) · ป.รัษฎากร §78/1";

    /// <summary>เครดิตที่ประทับต่อการจองของสมาชิกไม่จำกัด (นับ "ครั้งที่มา" ในสมุด — ไม่มีมูลค่าเงิน)</summary>
    public const decimal UnlimitedBookingCredits = 1m;

    /// <summary>พักบัตรได้ครั้งละ/รวมไม่เกินกี่วัน (เพดานระบบ — แพ็กเกจตั้งต่ำกว่าได้)</summary>
    public const int MaxFreezeDaysCap = 365;
    /// <summary>ขยายอายุครั้งละไม่เกินกี่วัน</summary>
    public const int MaxExtendDays = 3650;

    public static bool IsUnlimited(CreditPackageKind kind) => kind == CreditPackageKind.UnlimitedPeriod;

    /// <summary>ช่วงพักบัตรหนึ่งช่วง (วันปฏิทิน รวมหัวท้าย)</summary>
    public sealed record FreezeSpan(DateTime From, DateTime To)
    {
        public int Days => Math.Max(0, (To.Date - From.Date).Days + 1);
        public bool Covers(DateTime day) => day.Date >= From.Date && day.Date <= To.Date;
    }

    /// <summary>วันสุดท้ายที่ใช้ได้ของสมาชิก = วันเริ่ม + วันใช้งาน − 1 + วันพัก (30 วันเริ่ม 1 พ.ย. ⇒ ถึง 30 พ.ย.)</summary>
    public static DateTime UnlimitedExpiresOn(DateTime startsOn, int periodDays, int frozenDays)
        => startsOn.Date.AddDays(Math.Max(1, periodDays) - 1 + Math.Max(0, frozenDays));

    /// <summary>จำนวนวันที่ช่วง [aFrom, aTo] ซ้อนกับ [bFrom, bTo] (รวมหัวท้าย · ไม่ติดลบ)</summary>
    private static int OverlapDays(DateTime aFrom, DateTime aTo, DateTime bFrom, DateTime bTo)
    {
        var startDay = aFrom.Date > bFrom.Date ? aFrom.Date : bFrom.Date;
        var endDay = aTo.Date < bTo.Date ? aTo.Date : bTo.Date;
        return endDay < startDay ? 0 : (endDay - startDay).Days + 1;
    }

    /// <summary>วันใช้งาน (ไม่นับวันพัก) ตั้งแต่ <paramref name="startsOn"/> ถึง <paramref name="through"/> (รวม) · ก่อนวันเริ่ม = 0</summary>
    public static int ActiveDaysThrough(DateTime startsOn, DateTime through, IReadOnlyCollection<FreezeSpan> freezes)
    {
        if (through.Date < startsOn.Date) return 0;
        var days = (through.Date - startsOn.Date).Days + 1;
        foreach (var f in freezes) days -= OverlapDays(f.From, f.To, startsOn, through);
        return Math.Max(0, days);
    }

    /// <summary>เส้นตรงรายวัน: สะสมของ <paramref name="elapsed"/> วันจาก <paramref name="days"/> วัน — รายวันปัด AwayFromZero · ครบวัน = ทั้งก้อน · ไม่เกินก้อน · ไม่ติดลบ</summary>
    public static decimal StraightLine(decimal amount, int days, int elapsed)
    {
        if (amount <= 0m || elapsed <= 0) return 0m;
        if (days <= 0 || elapsed >= days) return amount;
        return Math.Min(amount, R2(R2(amount / days) * elapsed));
    }

    /// <summary>รายได้ต่อวันที่แสดงบนหน้าบัตร (ส่วนที่ยังไม่รับรู้ ÷ วันที่เหลือ หลังฐานล่าสุด)</summary>
    public static decimal PeriodDailyAmount(PeriodFacts f)
    {
        var days = f.PeriodDays - f.BaseActiveDays;
        return days <= 0 ? 0m : R2((f.Contract - f.BaseRecognized) / days);
    }

    /// <summary>ข้อเท็จจริงของบัตรสมาชิกไม่จำกัดที่สูตรรับรู้ต้องใช้ · <paramref name="Contract"/> = สัญญาที่มีผล (หลังคืนเงิน — <see cref="EffectiveContract"/>)</summary>
    public sealed record PeriodFacts(decimal Contract, int PeriodDays, DateTime StartsOn, decimal BaseRecognized, int BaseActiveDays,
        decimal Recognized, DateTime? RecognizedThrough, IReadOnlyCollection<FreezeSpan> Freezes);

    /// <summary>รายได้สะสมที่ควรรับรู้เมื่อผ่านไป <paramref name="activeDays"/> วันใช้งาน (ตัดที่วันใช้งานทั้งหมด) — ฐาน + เส้นตรงของส่วนที่เหลือบนวันที่เหลือ</summary>
    private static decimal PeriodCumulative(PeriodFacts f, int activeDays)
    {
        var k = Math.Min(Math.Max(0, activeDays), Math.Max(0, f.PeriodDays));
        var remainingDays = f.PeriodDays - f.BaseActiveDays;
        if (remainingDays <= 0) return f.Contract;
        if (k <= f.BaseActiveDays) return Math.Min(f.Contract, f.BaseRecognized);
        return f.BaseRecognized + StraightLine(f.Contract - f.BaseRecognized, remainingDays, k - f.BaseActiveDays);
    }

    /// <summary>หนึ่งรายการลงบัญชีของแผนรับรู้ — <paramref name="PostingDate"/> = วันสุดท้ายของช่วง (สิ้นเดือน หรือวันที่ขอ) · ยอด 0 = เลื่อนวันที่รับรู้ถึงอย่างเดียว</summary>
    public sealed record PeriodPosting(DateTime PostingDate, int ActiveDays, decimal Amount);

    /// <summary>
    /// แผนรับรู้รายได้ของสมาชิกไม่จำกัดตั้งแต่วันถัดจาก <see cref="PeriodFacts.RecognizedThrough"/> (หรือวันเริ่ม) ถึง <paramref name="throughDate"/> —
    /// แบ่ง<b>ต่อเดือนปฏิทิน</b> (งานไม่ได้รันข้ามเดือน ⇒ ยอดของเดือนก่อนลงวันสิ้นเดือนก่อน ไม่ไปปนเดือนนี้) · ยอดต่อช่วง = สะสมเป้าหมาย − รับรู้แล้ว (ไม่ติดลบ) ·
    /// รันซ้ำวันเดียวกัน = แผนว่าง (idempotent) · ผลรวมทุกช่วงจนครบวันใช้งาน = สัญญา
    /// </summary>
    public static List<PeriodPosting> PeriodRecognitionPlan(PeriodFacts f, DateTime throughDate)
    {
        var list = new List<PeriodPosting>();
        var startDay = f.RecognizedThrough?.Date.AddDays(1) ?? f.StartsOn.Date;
        if (startDay < f.StartsOn.Date) startDay = f.StartsOn.Date;
        var endDay = throughDate.Date;
        if (endDay < startDay) return list;
        var recognized = f.Recognized;
        var cursor = startDay;
        while (cursor <= endDay)
        {
            var monthEnd = new DateTime(cursor.Year, cursor.Month, DateTime.DaysInMonth(cursor.Year, cursor.Month), 0, 0, 0, throughDate.Kind);
            var segEnd = monthEnd < endDay ? monthEnd : endDay;
            var k = ActiveDaysThrough(f.StartsOn, segEnd, f.Freezes);
            var amount = Math.Max(0m, PeriodCumulative(f, k) - recognized);
            list.Add(new PeriodPosting(segEnd, k, amount));
            recognized += amount;
            cursor = segEnd.AddDays(1);
        }
        return list;
    }

    /// <summary>รายได้ที่รับรู้เมื่อปิดการจองหนึ่งรายการ — บัตรครั้ง/ชั่วโมง = <see cref="RecognizeOnUse"/> (เดิมทุกสตางค์) · สมาชิกไม่จำกัด = 0 (รับรู้ตามวันแทน)</summary>
    public static decimal RecognizeOnUseFor(CreditPackageKind kind, decimal contractAmount, decimal totalCredits,
        decimal creditsUsedBefore, decimal recognizedBefore, decimal creditsNow)
        => IsUnlimited(kind) ? 0m : RecognizeOnUse(contractAmount, totalCredits, creditsUsedBefore, recognizedBefore, creditsNow);

    /// <summary>ข้อเท็จจริงของบัตรสมาชิกไม่จำกัดที่ตัวตัดสินการจองใช้</summary>
    public sealed record UnlimitedFacts(CreditPassStatus Status, DateTime? ExpiresOn, DateTime? StartsOn, int MaxBookingsPerDay, int MaxActiveBookings,
        IReadOnlyCollection<FreezeSpan> Freezes, CreditClassFormat PackageFormat, IReadOnlyCollection<Guid> AllowedServiceIds);

    /// <summary>เหตุที่สมาชิกไม่จำกัดจองไม่ได้ (ไทยถึงผู้ใช้) · null = จองได้ — ไม่ตัดเครดิต แต่เคารพเพดานต่อวัน/จองค้าง/ช่วงพัก</summary>
    public static string? CannotBookUnlimitedReason(UnlimitedFacts pass, DateTime sessionStartUtc, int seats, int bookingsSameDay, int activeBookings,
        Guid? bookingServiceId, CreditClassFormat sessionFormat, DateTime nowUtc)
    {
        if (pass.Status != CreditPassStatus.Active)
            return $"สมาชิกนี้อยู่ในสถานะ {StatusTh(pass.Status)} — ใช้จองไม่ได้";
        if (IsExpired(pass.ExpiresOn, nowUtc))
            return $"สมาชิกหมดอายุแล้ว (ใช้ได้ถึง {ThaiDate.ToThaiDisplayString(pass.ExpiresOn!.Value)})";
        var day = ThaiDate.CalendarDateUtc(sessionStartUtc);
        if (pass.ExpiresOn.HasValue && day > pass.ExpiresOn.Value.Date)
            return $"รอบเรียนอยู่หลังวันหมดอายุของสมาชิก (ใช้ได้ถึง {ThaiDate.ToThaiDisplayString(pass.ExpiresOn.Value)})";
        if (pass.StartsOn.HasValue && day < pass.StartsOn.Value.Date)
            return $"รอบเรียนอยู่ก่อนวันเริ่มสมาชิก ({ThaiDate.ToThaiDisplayString(pass.StartsOn.Value)})";
        if (pass.Freezes.FirstOrDefault(f => f.Covers(day)) is FreezeSpan fz)
            return $"บัตรพักอยู่ช่วง {ThaiDate.ToThaiDisplayString(fz.From)} – {ThaiDate.ToThaiDisplayString(fz.To)} — จองวันนั้นไม่ได้ (ยกเลิกการพักก่อนถ้าพักยังไม่เริ่ม)";
        if (seats > 1)
            return "สมาชิกไม่จำกัดจองได้ครั้งละ 1 ที่ (เพื่อนร่วมเรียนต้องใช้บัตรของตัวเองหรือจองแบบชำระปกติ)";
        var perDay = Math.Max(1, pass.MaxBookingsPerDay);
        if (bookingsSameDay >= perDay)
            return $"สมาชิกนี้จองวันนั้นครบ {perDay} ครั้งแล้ว (เพดานต่อวันของแพ็กเกจ)";
        if (pass.MaxActiveBookings > 0 && activeBookings >= pass.MaxActiveBookings)
            return $"มีการจองค้างครบ {pass.MaxActiveBookings} รายการแล้ว — ใช้/ยกเลิกรายการเดิมก่อนจองเพิ่ม";
        if (pass.PackageFormat != CreditClassFormat.Any && sessionFormat != CreditClassFormat.Any && pass.PackageFormat != sessionFormat)
            return $"สมาชิกนี้ใช้ได้กับคลาสแบบ {FormatTh(pass.PackageFormat)} เท่านั้น";
        if (pass.AllowedServiceIds.Count > 0 && (bookingServiceId is not Guid sid || !pass.AllowedServiceIds.Contains(sid)))
            return "สมาชิกนี้ใช้กับบริการ/คลาสนี้ไม่ได้ (ไม่อยู่ในรายการบริการที่ใช้ได้ของแพ็กเกจ)";
        return null;
    }

    /// <summary>วันที่จองตรงช่วงพักไหม (บัตรครั้งที่พักไว้) — ข้อความไทย · null = ไม่ติดพัก</summary>
    public static string? FrozenOnReason(IReadOnlyCollection<FreezeSpan> freezes, DateTime sessionStartUtc)
    {
        var day = ThaiDate.CalendarDateUtc(sessionStartUtc);
        return freezes.FirstOrDefault(f => f.Covers(day)) is FreezeSpan fz
            ? $"บัตรพักอยู่ช่วง {ThaiDate.ToThaiDisplayString(fz.From)} – {ThaiDate.ToThaiDisplayString(fz.To)} — จองวันนั้นไม่ได้"
            : null;
    }

    /// <summary>
    /// ตัวตัดสิน "พักบัตร" — ช่วงเริ่มได้ตั้งแต่วันนี้ (ไม่ย้อนหลัง) และหลังวันที่รับรู้รายได้ไปแล้ว · วันพักรวม ≤ เพดานของแพ็กเกจ (0 = ไม่อนุญาต) ·
    /// ไม่ซ้อนช่วงพักเดิม · ไม่มีการจองค้างในช่วง · เริ่มไม่เกินวันหมดอายุ · ต้องระบุเหตุผล · null = พักได้
    /// </summary>
    public static string? FreezeProblem(CreditPassStatus status, DateTime startDay, DateTime endDay, DateTime today, DateTime? recognizedThrough,
        DateTime? expiresOn, int frozenDaysUsed, int maxFreezeDays, IReadOnlyCollection<FreezeSpan> existing, int openBookingsInRange, string? reason)
    {
        if (status != CreditPassStatus.Active) return $"บัตรอยู่ในสถานะ {StatusTh(status)} — พักบัตรไม่ได้";
        if (maxFreezeDays <= 0) return "แพ็กเกจนี้ไม่อนุญาตให้พักบัตร (ตั้ง \"พักบัตรได้สูงสุด (วัน)\" ที่แพ็กเกจก่อน)";
        if (expiresOn == null) return "บัตรนี้ไม่มีวันหมดอายุ — ไม่ต้องพักบัตร";
        if (string.IsNullOrWhiteSpace(reason)) return "ระบุเหตุผลการพักบัตร (เช่น ป่วย · เดินทาง)";
        if (endDay.Date < startDay.Date) return "วันสิ้นสุดการพักต้องไม่ก่อนวันเริ่ม";
        if (startDay.Date < today.Date) return "พักบัตรย้อนหลังไม่ได้ — เริ่มได้ตั้งแต่วันนี้";
        if (recognizedThrough.HasValue && startDay.Date <= recognizedThrough.Value.Date)
            return $"รับรู้รายได้ถึง {ThaiDate.ToThaiDisplayString(recognizedThrough.Value)} แล้ว — เริ่มพักได้ตั้งแต่วันถัดไป";
        if (startDay.Date > expiresOn.Value.Date)
            return $"วันเริ่มพักอยู่หลังวันหมดอายุ ({ThaiDate.ToThaiDisplayString(expiresOn.Value)}) — ใช้ \"ขยายอายุ\" แทน";
        var days = (endDay.Date - startDay.Date).Days + 1;
        var cap = Math.Min(maxFreezeDays, MaxFreezeDaysCap);
        if (frozenDaysUsed + days > cap)
            return $"พักได้รวมไม่เกิน {cap} วันต่อบัตร (ใช้ไปแล้ว {frozenDaysUsed} วัน · ขอ {days} วัน · เหลือ {Math.Max(0, cap - frozenDaysUsed)} วัน)";
        if (existing.Any(e => OverlapDays(e.From, e.To, startDay, endDay) > 0))
            return "ช่วงนี้ซ้อนกับการพักที่มีอยู่แล้ว";
        if (openBookingsInRange > 0)
            return $"มีการจองค้าง {openBookingsInRange} รายการในช่วงพัก — ยกเลิกการจองเหล่านั้นก่อน";
        return null;
    }

    /// <summary>ตัวตัดสิน "ขยายอายุ" (ไม่มีเงิน · audit) · null = ขยายได้</summary>
    public static string? ExtendProblem(CreditPassStatus status, DateTime? expiresOn, int days, string? reason)
    {
        if (status != CreditPassStatus.Active) return $"บัตรอยู่ในสถานะ {StatusTh(status)} — ขยายอายุไม่ได้";
        if (expiresOn == null) return "บัตรนี้ไม่มีวันหมดอายุอยู่แล้ว";
        if (days < 1 || days > MaxExtendDays) return $"จำนวนวันที่ขยายต้องอยู่ระหว่าง 1–{MaxExtendDays}";
        if (string.IsNullOrWhiteSpace(reason)) return "ระบุเหตุผลการขยายอายุ";
        return null;
    }

    /// <summary>ระยะเว้นก่อนลองส่งแจ้งเตือนหมดอายุซ้ำหลังส่งล้ม (งานรันทุก 6 ชม.)</summary>
    public static readonly TimeSpan ExpiryReminderRetry = TimeSpan.FromHours(6);

    /// <summary>ถึงเวลาแจ้งเตือน "บัตรใกล้หมดอายุ" หรือยัง — แพ็กเกจเปิด (วัน &gt; 0) · ยังไม่เคยส่งถึง · วันนี้อยู่ในช่วง [หมดอายุ − N วัน, วันหมดอายุ] ·
    /// ส่งล้มครั้งก่อน ⇒ เว้น <see cref="ExpiryReminderRetry"/> ก่อนลองใหม่ (ไม่ยิงซ้ำทุกนาที)</summary>
    public static bool ExpiryReminderDue(DateTime expiresOn, DateTime today, int reminderDays, DateTime? notifiedAt, DateTime? attemptedAt, DateTime nowUtc)
    {
        if (reminderDays <= 0 || notifiedAt.HasValue) return false;
        if (today.Date > expiresOn.Date || today.Date < expiresOn.Date.AddDays(-reminderDays)) return false;
        return attemptedAt == null || nowUtc - attemptedAt.Value >= ExpiryReminderRetry;
    }

    /// <summary>ข้อความแจ้งเตือนบัตรใกล้หมดอายุ (ไทย · ไม่มีข้อมูลการเงินภายใน)</summary>
    public static string ExpiryReminderText(string? customerName, string passNumber, string packageName, DateTime expiresOn, string remainingLabel, string? shopName)
        => $"เรียนคุณ{(string.IsNullOrWhiteSpace(customerName) ? "ลูกค้า" : customerName!.Trim())}\n"
           + $"{packageName} (บัตร {passNumber}) จะหมดอายุวันที่ {ThaiDate.ToThaiDisplayString(expiresOn)} — คงเหลือ {remainingLabel}\n"
           + "จองคลาสก่อนวันหมดอายุเพื่อใช้สิทธิ์ให้ครบ"
           + (string.IsNullOrWhiteSpace(shopName) ? "" : $"\n— {shopName!.Trim()}");

    /// <summary>ผลวางแผนโอนบัตร — บัตรใหม่ได้<b>ยอดที่เหลือเท่าเดิม</b> (สัญญาที่ยังไม่รับรู้ · เครดิต/วันใช้งานที่เหลือ) · ไม่มี JE · ไม่ออกใบกำกับใหม่</summary>
    public sealed record TransferPlan(decimal Contract, decimal CreditsTotal, int PeriodDays, DateTime? ExpiresOn, string? Problem);

    /// <summary>
    /// ตัวตัดสิน + ยอดของการโอนบัตรให้ลูกค้าคนอื่น (คำตัดสินข้อ 168 ฎ "ปิดบัตรเดิม/เปิดบัตรใหม่ยอดเท่าเดิม") — ต้องไม่มีการจองค้าง ·
    /// ไม่มีช่วงพักที่ยังไม่จบ · มียอดเหลือ · ผู้รับต้องไม่ใช่เจ้าของเดิม · สมาชิกไม่จำกัด: ผู้เรียกต้องรับรู้รายได้ถึงเมื่อวานก่อน
    /// (<paramref name="activeDaysRecognized"/> = วันใช้งานที่รับรู้แล้ว) ⇒ บัตรใหม่เริ่มวันนี้ วันใช้งาน = ที่เหลือ
    /// </summary>
    public static TransferPlan PlanTransfer(CreditPackageKind kind, CreditPassStatus status, bool sameContact, decimal effectiveContract, decimal recognized,
        decimal creditsTotal, decimal creditsUsed, int openBookings, bool hasOpenFreeze, DateTime? expiresOn, int periodDays, int activeDaysRecognized,
        DateTime today)
    {
        TransferPlan Fail(string m) => new(0m, 0m, 0, null, m);
        if (status != CreditPassStatus.Active) return Fail($"บัตรอยู่ในสถานะ {StatusTh(status)} — โอนไม่ได้");
        if (sameContact) return Fail("ผู้รับโอนต้องเป็นลูกค้าคนอื่น");
        if (openBookings > 0) return Fail($"มีการจองค้าง {openBookings} รายการ — ปิด/ยกเลิกการจองก่อนโอน");
        if (hasOpenFreeze) return Fail("บัตรมีช่วงพักที่ยังไม่จบ — ยกเลิกการพักที่ยังไม่เริ่ม/รอให้พักจบก่อนโอน");
        if (expiresOn.HasValue && today.Date > expiresOn.Value.Date) return Fail("บัตรหมดอายุแล้ว — โอนไม่ได้");
        var contract = Math.Max(0m, effectiveContract - recognized);
        if (contract <= 0m) return Fail("บัตรรับรู้รายได้ครบแล้ว — ไม่มียอดเหลือให้โอน");
        if (IsUnlimited(kind))
        {
            var remainingDays = periodDays - Math.Max(0, activeDaysRecognized);
            if (remainingDays <= 0) return Fail("สมาชิกไม่มีวันใช้งานเหลือให้โอน");
            return new(contract, 0m, remainingDays, UnlimitedExpiresOn(today, remainingDays, 0), null);
        }
        var credits = Math.Max(0m, creditsTotal - creditsUsed);
        if (credits <= 0m) return Fail("ไม่มีเครดิตเหลือให้โอน");
        return new(contract, credits, 0, expiresOn, null);
    }
}

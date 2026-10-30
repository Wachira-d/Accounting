using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ค่าตั้งการชำระล่วงหน้าหนึ่งชั้น (เว็บ หรือ บริการ) — null ทุกช่อง = "ไม่ได้ตั้งที่ชั้นนี้" (ถอยไปชั้นถัดไป)</summary>
public sealed record BookingPaymentRuleInput(
    BookingPaymentMode? Mode,
    decimal? FixedAmount,
    decimal? Percent,
    decimal? MinAmount,
    decimal? MaxAmount,
    int? DeadlineMinutes)
{
    public static readonly BookingPaymentRuleInput None = new(null, null, null, null, null, null);
}

/// <summary>ที่มาของโหมดที่มีผล — Legacy = ไม่มีชั้นใดตั้ง (พฤติกรรมก่อนรอบ 218)</summary>
public enum BookingPaymentModeSource { Legacy = 0, Site = 1, Service = 2 }

/// <summary>นโยบายที่มีผลกับบริการหนึ่ง (ประกอบโดย <see cref="BookingPaymentPolicy.Resolve"/> เท่านั้น)</summary>
public sealed record BookingPaymentEffective(
    BookingPaymentMode Mode,
    BookingPaymentModeSource Source,
    decimal? FixedAmount,
    decimal? Percent,
    decimal? MinAmount,
    decimal? MaxAmount,
    int DeadlineMinutes,
    string? Problem)
{
    /// <summary>มีการเก็บเงินล่วงหน้า (ไม่ใช่ปิด)</summary>
    public bool Collects => Mode != BookingPaymentMode.Off;
    /// <summary>บังคับจ่ายก่อน (มัดจำบังคับ / เต็มยอด)</summary>
    public bool Required => Mode is BookingPaymentMode.DepositRequired or BookingPaymentMode.FullRequired;
    public bool IsLegacy => Source == BookingPaymentModeSource.Legacy;
}

/// <summary>ข้อเท็จจริงของการจองหนึ่งใบสำหรับป้ายสถานะการชำระ</summary>
public sealed record BookingPaymentFacts(
    BookingStatus Status,
    decimal DepositAmount,
    decimal PaidAmount,
    bool SlipPending,
    DateTime? PaymentHoldUntilUtc,
    string? SlipRejectedReason);

/// <summary>ตัวเลือกโหมดที่หน้าตั้งค่าแสดง (server ส่ง — หน้าเว็บไม่มีสำเนา)</summary>
public sealed record BookingPaymentModeOption(string Value, string Label, string Description);

/// <summary>
/// รอบ 218 (คำตัดสินข้อ 177 · <c>erp-review/2026-10-30/BOOKING_PAYMENT_DESIGN.md</c>) — <b>ตัวตัดสินเดียว</b>ของ "การจองนัดหมายต้องจ่ายล่วงหน้าเท่าไร · ตอนไหน · บังคับไหม"
///
/// <para><b>ที่มา</b>: เจ้าของติ๊ก "รับชำระมัดจำออนไลน์" แล้วตอนจองไม่มีอะไรให้จ่าย — สวิตช์เดิมเปิดแค่ช่องทาง gateway แต่ยอดมาจาก
/// ชนิดบริการ PrePayment + ช่องมัดจำของบริการเท่านั้น (ไม่ตรงทั้งสองข้อ ⇒ ยอด 0 เงียบ) · ไม่มีนโยบายระดับเว็บ · ไม่มีกำหนดเวลาชำระ</para>
///
/// <para><b>ลำดับ</b>: โหมด = บริการ ?? เว็บ ?? Legacy · สูตรยอด = ช่องมัดจำของบริการ (บาท/% ตัวใดตัวหนึ่งมีค่า) ?? ของเว็บ ·
/// ขั้นต่ำ/สูงสุด/กำหนดเวลา ถอยทีละช่อง · <b>เก็บเงินได้เฉพาะชนิด PrePayment</b> (ใบรับมัดจำลงวันที่รับเงิน §78/1 — ชนิดอื่นไม่มีเอกสาร ณ tax point)
/// ⇒ ชนิดอื่นได้โหมด "ปิด" พร้อมเหตุผล (ไม่ silent) · Legacy = พฤติกรรมเดิมทุกตัวอักษร (ไม่มีกำหนดเวลา · ไม่บังคับรอยืนยัน)</para>
/// </summary>
public static class BookingPaymentPolicy
{
    public const string RuleCode = "CMS-BOOKING-PAYMENT-POLICY";
    public const string LegalReference = "RD-78/1";
    /// <summary>กำหนดเวลาชำระสูงสุด (นาที) — 7 วัน</summary>
    public const int MaxDeadlineMinutes = 7 * 24 * 60;
    /// <summary>ปฏิเสธสลิปแล้วต่อเวลาถือให้ส่งใหม่ (นาที)</summary>
    public const int SlipResubmitGraceMinutes = 24 * 60;
    public const string HoldExpiredReason = "ไม่ชำระเงินล่วงหน้าภายในเวลาที่กำหนด — ระบบยกเลิกการจองอัตโนมัติ";

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static string Baht(decimal v) => "฿" + v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    public static BookingPaymentEffective Resolve(BookingType type, BookingPaymentRuleInput site, BookingPaymentRuleInput service)
    {
        site ??= BookingPaymentRuleInput.None;
        service ??= BookingPaymentRuleInput.None;
        var serviceAmount = service.FixedAmount != null || service.Percent != null;
        var mode = service.Mode ?? site.Mode;
        if (mode is not BookingPaymentMode m)
        {
            // Legacy: ช่องมัดจำของบริการตามเดิม · ไม่มี min/max/กำหนดเวลา (ไม่เปลี่ยนพฤติกรรมเว็บที่ไม่เคยตั้ง)
            return new BookingPaymentEffective(type == BookingType.PrePayment ? BookingPaymentMode.DepositRequired : BookingPaymentMode.Off,
                BookingPaymentModeSource.Legacy, service.FixedAmount, service.Percent, null, null, 0, null);
        }
        var source = service.Mode != null ? BookingPaymentModeSource.Service : BookingPaymentModeSource.Site;
        var fixedAmt = serviceAmount ? service.FixedAmount : site.FixedAmount;
        var percent = serviceAmount ? service.Percent : site.Percent;
        var deadline = Math.Clamp(service.DeadlineMinutes ?? site.DeadlineMinutes ?? 0, 0, MaxDeadlineMinutes);
        var eff = new BookingPaymentEffective(m, source, fixedAmt, percent, service.MinAmount ?? site.MinAmount,
            service.MaxAmount ?? site.MaxAmount, deadline, null);
        if (m != BookingPaymentMode.Off && type != BookingType.PrePayment)
            return eff with { Mode = BookingPaymentMode.Off, Problem = TypeMismatchProblem(type) };
        if (m is BookingPaymentMode.DepositRequired or BookingPaymentMode.DepositOptional && fixedAmt == null && percent == null)
            return eff with { Problem = "ยังไม่ได้ตั้งยอดมัดจำ (บาท หรือ %) — ลูกค้าจะไม่ถูกเรียกเก็บเงิน" };
        return eff;
    }

    /// <summary>ข้อความ "ชนิดบริการนี้เก็บเงินล่วงหน้าไม่ได้" — ตัวเดียวกับที่ด่านบันทึกบริการใช้</summary>
    private static string TypeMismatchProblem(BookingType type)
        => $"บริการชนิด “{TypeLabel(type)}” ไม่เก็บเงินล่วงหน้า — เปลี่ยนชนิดบริการเป็น “ชำระมัดจำล่วงหน้า (ใบรับมัดจำ)” ก่อน "
           + "(เงินล่วงหน้าค่าบริการเป็นจุดเกิดภาษี §78/1 ต้องออกใบรับมัดจำลงวันที่รับเงิน)";

    private static string TypeLabel(BookingType t) => t switch
    {
        BookingType.Lead => "รับจอง/รอยืนยัน",
        BookingType.Appointment => "นัดหมาย (ใบเสนอราคา)",
        BookingType.Guaranteed => "จองยืนยัน (ใบกำกับภาษี)",
        _ => "ชำระมัดจำล่วงหน้า",
    };

    /// <summary>ยอดที่ต้องชำระล่วงหน้า (สกุลของบริการ) — decimal · ปัด 2 ตำแหน่งแบบ AwayFromZero · clamp [ขั้นต่ำ, สูงสุด] แล้ว [0, ยอดรวม] ·
    /// มัดจำไม่บังคับ + ลูกค้าไม่เลือกจ่าย ⇒ 0 · เต็มยอด ⇒ ยอดรวม</summary>
    public static decimal AmountDue(BookingPaymentEffective e, decimal total, bool customerOptsIn)
    {
        var t = R2(Math.Max(0m, total));
        switch (e.Mode)
        {
            case BookingPaymentMode.Off: return 0m;
            case BookingPaymentMode.FullRequired: return t;
            case BookingPaymentMode.DepositOptional when !customerOptsIn: return 0m;
        }
        var d = e.FixedAmount ?? (e.Percent is decimal p ? R2(t * p / 100m) : 0m);
        if (d <= 0m) return 0m;   // ไม่ตั้งยอด = ไม่เก็บ (ขั้นต่ำไม่สร้างยอดจากศูนย์)
        if (e.MinAmount is decimal min && min > 0m && d < min) d = min;
        if (e.MaxAmount is decimal max && max > 0m && d > max) d = max;
        return Math.Clamp(R2(d), 0m, t);
    }

    /// <summary>ยอดมัดจำที่บันทึกบนการจอง — Legacy + ชนิดที่ไม่ใช่ PrePayment = สูตรเดิม (<see cref="BookingSlotEngine.Deposit"/> · แสดงอย่างเดียว) ·
    /// นอกนั้น <see cref="AmountDue"/></summary>
    public static decimal BookingDeposit(BookingType type, BookingPaymentEffective e, decimal total, decimal? serviceFixed, decimal? servicePercent,
        bool customerOptsIn)
        => e.IsLegacy && type != BookingType.PrePayment
            ? BookingSlotEngine.Deposit(total, serviceFixed, servicePercent)
            : AmountDue(e, total, customerOptsIn);

    /// <summary>การจองใหม่ต้อง "รอยืนยัน" จนรับเงินครบไหม (ชนะ AutoConfirm) — เฉพาะโหมดบังคับที่ตั้งเอง (ไม่ใช่ Legacy) และมียอด</summary>
    public static bool ForcePendingUntilPaid(BookingPaymentEffective e, decimal due)
        => e.Required && !e.IsLegacy && due > 0.005m;

    /// <summary>ถือรอบให้จ่ายถึงเมื่อไร (UTC) — โหมดบังคับ + ยอด &gt; 0 + มีกำหนดเวลา + ลูกค้าจองเอง (พนักงานจองแทน = ไม่ถือ) ·
    /// ไม่เกินเวลาเริ่มนัด</summary>
    public static DateTime? HoldUntil(BookingPaymentEffective e, decimal due, DateTime nowUtc, DateTime startUtc, bool createdByStaff)
    {
        if (createdByStaff || !ForcePendingUntilPaid(e, due) || e.DeadlineMinutes <= 0) return null;
        var until = nowUtc.AddMinutes(e.DeadlineMinutes);
        return until > startUtc ? startUtc : until;
    }

    /// <summary>job ยกเลิกใบที่ไม่จ่ายในเวลาได้ไหม — รอยืนยัน + พ้นเวลา + ยังไม่มีเงิน + ไม่มีสลิปรอตรวจ (ลูกค้าโอนแล้ว ⇒ ให้คนตัดสิน)</summary>
    public static bool HoldLapsed(BookingStatus status, DateTime? holdUntilUtc, decimal paidAmount, bool slipPending, DateTime nowUtc)
        => status == BookingStatus.Pending && holdUntilUtc is DateTime u && u <= nowUtc && paidAmount <= 0m && !slipPending;

    // ─────────────── ข้อความ ───────────────

    public static string ModeLabel(BookingPaymentMode m) => m switch
    {
        BookingPaymentMode.DepositRequired => "มัดจำ (บังคับ)",
        BookingPaymentMode.DepositOptional => "มัดจำ (ไม่บังคับ — ลูกค้าเลือก)",
        BookingPaymentMode.FullRequired => "ชำระเต็มจำนวนก่อน (บังคับ)",
        _ => "ไม่รับชำระล่วงหน้า",
    };

    public static IReadOnlyList<BookingPaymentModeOption> ModeOptions { get; } = new[]
    {
        new BookingPaymentModeOption("", "ยังไม่ได้ตั้ง (แบบเดิม — ตามชนิดบริการ)",
            "บริการ “ชำระมัดจำล่วงหน้า” เก็บมัดจำตามช่องมัดจำของบริการ · ไม่มีกำหนดเวลา · บริการชนิดอื่นไม่เก็บ"),
        new BookingPaymentModeOption(nameof(BookingPaymentMode.Off), ModeLabel(BookingPaymentMode.Off),
            "ลูกค้าไม่ต้องจ่ายตอนจอง — ชำระที่ร้าน/ตอนรับบริการ"),
        new BookingPaymentModeOption(nameof(BookingPaymentMode.DepositRequired), ModeLabel(BookingPaymentMode.DepositRequired),
            "การจองรอยืนยันจนรับมัดจำครบ · ตั้งกำหนดเวลาแล้วไม่จ่ายในเวลา ⇒ ระบบยกเลิกและปล่อยรอบ · ออกใบรับมัดจำลงวันที่รับเงิน"),
        new BookingPaymentModeOption(nameof(BookingPaymentMode.DepositOptional), ModeLabel(BookingPaymentMode.DepositOptional),
            "ลูกค้าติ๊กเลือกจ่ายมัดจำตอนจองได้ · ไม่จ่ายก็จองได้ (ไม่ยกเลิกอัตโนมัติ)"),
        new BookingPaymentModeOption(nameof(BookingPaymentMode.FullRequired), ModeLabel(BookingPaymentMode.FullRequired),
            "ต้องจ่ายค่าบริการเต็มจำนวนก่อน · การจองรอยืนยันจนรับครบ · ใบรับเงินล่วงหน้าเต็มยอด (ตัดเป็นรายได้เมื่อให้บริการเสร็จ)"),
    };

    /// <summary>"ค่า enum ที่หน้าตั้งค่าส่งมา" → โหมด ("" / "Inherit" = ไม่ตั้ง) · อ่านไม่ออก = ข้อความไทย</summary>
    public static (BookingPaymentMode? Mode, string? Error) ParseMode(string? raw)
    {
        var v = raw?.Trim();
        if (string.IsNullOrEmpty(v) || string.Equals(v, "Inherit", StringComparison.OrdinalIgnoreCase)) return (null, null);
        return Enum.TryParse<BookingPaymentMode>(v, true, out var m) && Enum.IsDefined(m) && !int.TryParse(v, out _)
            ? (m, null)
            : (null, $"โหมดการชำระล่วงหน้า “{v}” ไม่ถูกต้อง");
    }

    /// <summary>ตรวจค่าตั้งหนึ่งชั้น — คืนรายการข้อความไทย (ว่าง = ผ่าน)</summary>
    public static List<string> ValidateRule(BookingPaymentRuleInput r)
    {
        var errs = new List<string>();
        if (r.FixedAmount is < 0m) errs.Add("มัดจำ (บาท) ต้องไม่ติดลบ");
        if (r.FixedAmount is decimal f && decimal.Round(f, 2) != f) errs.Add("มัดจำ (บาท) ทศนิยมไม่เกิน 2 ตำแหน่ง");
        if (r.Percent is < 0m or > 100m) errs.Add("มัดจำ (%) ต้องอยู่ระหว่าง 0–100");
        if (r.MinAmount is < 0m) errs.Add("มัดจำขั้นต่ำต้องไม่ติดลบ");
        if (r.MaxAmount is < 0m) errs.Add("มัดจำสูงสุดต้องไม่ติดลบ");
        if (r.MinAmount is decimal mn && r.MaxAmount is decimal mx && mx > 0m && mn > mx) errs.Add("มัดจำขั้นต่ำต้องไม่มากกว่ามัดจำสูงสุด");
        if (r.DeadlineMinutes is < 0 or > MaxDeadlineMinutes) errs.Add($"กำหนดเวลาชำระต้องอยู่ระหว่าง 0–{MaxDeadlineMinutes} นาที (0 = ไม่กำหนด)");
        return errs;
    }

    /// <summary>ด่านบันทึกค่าตั้งเว็บ: โหมดที่เก็บเงินต้องมีช่องทางชำระอย่างน้อย 1 ทาง · มัดจำต้องมีสูตรยอดตั้งต้น — คืน null = ผ่าน</summary>
    public static string? SiteSaveProblem(BookingPaymentRuleInput site, bool gatewayReady, bool transferReady)
    {
        if (site.Mode is not BookingPaymentMode m || m == BookingPaymentMode.Off) return null;
        if (!gatewayReady && !transferReady)
            return $"โหมด “{ModeLabel(m)}” ต้องมีช่องทางชำระอย่างน้อย 1 ทาง — ใส่เลขบัญชี/พร้อมเพย์/รูป QR ในหัวข้อ “ช่องทางรับเงิน” "
                   + "หรือเปิดชำระออนไลน์ (ต้องตั้งค่าระบบรับชำระเงินของบริษัทก่อน)";
        if (m is BookingPaymentMode.DepositRequired or BookingPaymentMode.DepositOptional && site.FixedAmount == null && site.Percent == null)
            return "โหมดมัดจำต้องตั้งยอดมัดจำตั้งต้นของเว็บ (บาท หรือ %) — บริการที่ตั้งยอดเองใช้ของตัวเอง";
        return null;
    }

    /// <summary>ด่านบันทึกบริการ: ตั้งโหมดเก็บเงินเอง แต่ชนิดบริการเก็บไม่ได้ ⇒ ปฏิเสธ (ไม่บันทึกค่าที่ไม่มีผล)</summary>
    public static string? ServiceSaveProblem(BookingType type, BookingPaymentMode? serviceMode)
        => serviceMode is BookingPaymentMode m && m != BookingPaymentMode.Off && type != BookingType.PrePayment ? TypeMismatchProblem(type) : null;

    /// <summary>สรุปสั้นของนโยบายที่มีผล (หน้าตั้งค่า/รายการบริการ)</summary>
    public static string Describe(BookingPaymentEffective e)
    {
        var src = e.Source switch
        {
            BookingPaymentModeSource.Service => "กำหนดเองที่บริการ",
            BookingPaymentModeSource.Site => "ตามค่าเว็บ",
            _ => "แบบเดิม",
        };
        if (e.Problem != null && !e.Collects) return $"ไม่เก็บเงินล่วงหน้า ({src}) — {e.Problem}";
        if (!e.Collects) return $"{ModeLabel(e.Mode)} ({src})";
        var amount = e.Mode == BookingPaymentMode.FullRequired ? "เต็มจำนวน"
            : e.FixedAmount is decimal f ? Baht(f)
            : e.Percent is decimal p ? $"{p.ToString("0.##", CultureInfo.InvariantCulture)}%" : "ยังไม่ได้ตั้งยอด";
        var bounds = (e.MinAmount is decimal mn && mn > 0m ? $" ขั้นต่ำ {Baht(mn)}" : "") + (e.MaxAmount is decimal mx && mx > 0m ? $" สูงสุด {Baht(mx)}" : "");
        var deadline = e.DeadlineMinutes > 0 ? $" · ชำระภายใน {DeadlineText(e.DeadlineMinutes)}" : "";
        return $"{ModeLabel(e.Mode)} {amount}{bounds}{deadline} ({src})";
    }

    private static string DeadlineText(int minutes)
        => minutes >= 1440 && minutes % 1440 == 0 ? $"{minutes / 1440} วัน"
         : minutes >= 60 && minutes % 60 == 0 ? $"{minutes / 60} ชั่วโมง"
         : $"{minutes} นาที";

    /// <summary>ข้อความถึงลูกค้าก่อน/หลังจอง — ยอดที่ต้องจ่าย + บังคับไหม + กำหนดเวลา (null = ไม่ต้องจ่าย)</summary>
    public static string? CustomerSummary(BookingPaymentEffective e, decimal amountIfPaid, bool hasDeadlineHold)
    {
        if (!e.Collects || amountIfPaid <= 0m) return null;
        var what = e.Mode == BookingPaymentMode.FullRequired ? "ชำระค่าบริการเต็มจำนวน" : "ชำระมัดจำ";
        if (e.Mode == BookingPaymentMode.DepositOptional)
            return $"เลือกชำระมัดจำ {Baht(amountIfPaid)} ตอนนี้ได้ (ไม่บังคับ) — ส่วนที่เหลือชำระเมื่อรับบริการ";
        var deadline = hasDeadlineHold && e.DeadlineMinutes > 0
            ? $" ภายใน {DeadlineText(e.DeadlineMinutes)} หลังจอง — ไม่ชำระในเวลา ระบบยกเลิกการจองอัตโนมัติ"
            : " เพื่อยืนยันการจอง";
        return $"ต้อง{what} {Baht(amountIfPaid)}{deadline}";
    }

    /// <summary>ป้ายสถานะการชำระที่ลูกค้า/พนักงานเห็น (null = ไม่มีเรื่องเงินล่วงหน้า)</summary>
    public static string? StatusLabel(BookingPaymentFacts f)
    {
        if (f.DepositAmount <= 0.005m || f.Status is BookingStatus.Cancelled or BookingStatus.NoShow or BookingStatus.Waitlisted) return null;
        if (f.PaidAmount + 0.005m >= f.DepositAmount) return "ชำระแล้ว";
        if (f.SlipPending) return "รอตรวจสลิป";
        var remain = R2(f.DepositAmount - f.PaidAmount);
        var label = $"รอชำระ {Baht(remain)}";
        if (f.PaymentHoldUntilUtc is DateTime u)
            label += $" ภายใน {u.AddHours(7).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)} น.";
        if (!string.IsNullOrWhiteSpace(f.SlipRejectedReason)) label += $" (สลิปไม่ผ่าน: {f.SlipRejectedReason.Trim()})";
        return label;
    }

    /// <summary>บรรทัดวิธีชำระท้ายข้อความยืนยันการจอง (อีเมล/LINE) — null = ไม่มียอดค้าง</summary>
    public static string? NotifyLine(decimal due, DateTime? holdUntilUtc, bool transferReady, bool gatewayReady)
    {
        if (due <= 0.005m) return null;
        var how = (transferReady, gatewayReady) switch
        {
            (true, true) => "ชำระออนไลน์ หรือโอน/สแกน QR แล้วแนบสลิป ที่ลิงก์จัดการการจองด้านล่าง",
            (true, false) => "โอน/สแกน QR แล้วแนบสลิป ที่ลิงก์จัดการการจองด้านล่าง",
            (false, true) => "ชำระออนไลน์ที่ลิงก์จัดการการจองด้านล่าง",
            _ => "ติดต่อร้านเพื่อชำระเงิน",
        };
        var by = holdUntilUtc is DateTime u ? $" ภายใน {u.AddHours(7).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)} น." : "";
        return $"💳 ยอดที่ต้องชำระ {Baht(due)}{by} — {how}";
    }
}

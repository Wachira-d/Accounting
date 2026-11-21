namespace Accounting.Helpers;

/// <summary>
/// **แหล่งเงินของการบันทึกเงินจากหลักฐาน — ตัวตัดสินเดียว ใช้ทั้งสองฝั่ง** (รอบ 282 · คำสั่งเจ้าของ 282-6 · pure · ไม่มี AI)
/// <para>เจ้าของ (2026-11-21): "ทั้งฝั่ง A และ B ถ้าขั้นตอนไหนไม่สามารถตรวจสอบแหล่งเงินที่ต้องใช้เพื่อบันทึกเอกสารได้ ให้ใช้วิธีสำรองคือส่งแจ้งเตือนให้ยืนยันผ่านช่องทางต่าง ๆ
/// แต่ถ้ามีสลิปก็อ่าน และเช็คจับกับบัญชีที่ตั้งค่าไว้ได้อยู่แล้ว"</para>
/// <list type="bullet">
/// <item>ชั้นที่บันทึกเองได้ (<see cref="AllowsAuto"/>): <see cref="Evidence.BankLine"/> รายการเดินบัญชี (ข้อเท็จจริง) · <see cref="Evidence.SlipUnique"/> คู่กรณีบนสลิป
/// (ธนาคาร + เลขท้ายที่เห็น) ตรงบัญชีเดียวของบริษัทผ่าน <see cref="SlipAccountMatch"/> (ตัวจับคู่ของรอบ 264 P2 — ไม่ fuzzy · ชื่อไม่ใช้)</item>
/// <item>อื่น ๆ (ไม่มีสลิป · สลิปไม่ตรงบัญชีใด · หลายบัญชี · อ่านไม่ได้ · บัญชีที่ตรงใช้รับ/จ่ายไม่ได้) ⇒ <see cref="Evidence.None"/> — <b>ห้ามเดาบัญชี</b>
/// ผู้เรียกส่งเข้าหน้ายืนยัน + แจ้งเตือน (<see cref="ReminderDue"/> = ตารางเตือนซ้ำตัวเดียวของทั้งสองฝั่ง)</item>
/// <item>ผู้ใช้: ฝั่งผู้ขายรับเงิน <c>SlipAutoRecordService</c> (ผู้รับบนสลิป) · ฝั่งผู้ซื้อบันทึกจ่าย <c>PartnerPaySourceAccount.Precedence</c> (ผู้โอนบนสลิป —
/// แถว BankLine/SlipUnique อ่าน <see cref="AllowsAuto"/> จากที่นี่)</item>
/// </list>
/// </summary>
public static class SlipMoneySource
{
    public const string RuleCode = "SLIP-MONEY-SOURCE";
    public const string LegalReference = "พ.ร.บ.การบัญชี 2543 ม.8 (บันทึกตามเอกสารประกอบที่ถูกต้อง) · คำสั่งเจ้าของ 282-6";

    public enum Evidence { None = 0, BankLine = 1, SlipUnique = 2 }

    /// <summary>ชั้นที่บันทึกเองได้ (ตัวตั้งเดียวของทั้งสองฝั่ง)</summary>
    public static bool AllowsAuto(Evidence e) => e is Evidence.BankLine or Evidence.SlipUnique;

    /// <param name="AccountId">บัญชีธนาคารของบริษัทที่หลักฐานชี้ · null = ไม่รู้ (ห้ามเดา)</param>
    public sealed record Verdict(Guid? AccountId, Evidence Evidence, string Reason)
    {
        public bool Auto => AccountId != null && AllowsAuto(Evidence);
        public bool UsedAi => false;
    }

    public const string NoEvidenceReason = "ไม่มีรายการเดินบัญชีและไม่มีสลิปที่อ่านบัญชีได้ — ระบบไม่เดาบัญชี (ยืนยันบัญชีที่หน้ายืนยัน)";

    /// <param name="bankLineAccount">บัญชีจากรายการเดินบัญชีที่ตรงรายการนี้ · null = ไม่มี</param>
    /// <param name="slip">ผล <see cref="SlipAccountMatch.Match"/> ของคู่กรณีฝั่งบริษัทนี้บนสลิป (ผู้รับ = ฝั่งผู้ขาย · ผู้โอน = ฝั่งผู้ซื้อ) · null = ไม่มีสลิป/ไม่มีผลอ่าน</param>
    /// <param name="usable">บัญชีที่ใช้บันทึกได้ (ผู้เรียกกรอง: ของบริษัท · ใช้งาน · ผูกผัง · สกุลตรง)</param>
    public static Verdict Decide(Guid? bankLineAccount, SlipAccountMatch.Result? slip, IReadOnlyCollection<Guid> usable)
    {
        if (bankLineAccount is Guid line && usable.Contains(line))
            return new Verdict(line, Evidence.BankLine, "จากรายการเดินบัญชี");
        if (slip == null) return new Verdict(null, Evidence.None, NoEvidenceReason);
        return slip.Kind switch
        {
            SlipAccountMatch.Kind.Unique when slip.AccountId is Guid id && usable.Contains(id)
                => new Verdict(id, Evidence.SlipUnique, "บัญชีบนสลิป (" + slip.PartyLabel + ") ตรงบัญชีนี้ของบริษัทบัญชีเดียว"),
            SlipAccountMatch.Kind.Unique
                => new Verdict(null, Evidence.None, "บัญชีบนสลิปตรงบัญชีของบริษัทที่ใช้บันทึกไม่ได้ (ปิดใช้/ไม่ผูกผัง/สกุลไม่ตรง) — ระบบไม่เลือกบัญชีอื่นแทน"),
            SlipAccountMatch.Kind.NotOurs
                => new Verdict(null, Evidence.None, "บัญชีบนสลิปไม่ใช่บัญชีของบริษัท (อาจเป็นสลิปปลอม/โอนผิดบริษัท/โอนให้บุคคล) — ตรวจรายการเดินบัญชีก่อนยืนยัน"),
            _ => new Verdict(null, Evidence.None, "ตรวจบัญชีจากสลิปไม่ได้: " + slip.Reason),
        };
    }

    /// <summary>ฝ่ายค้าน MED-2: หลักที่เห็นขั้นต่ำของ "สลิปผู้โอน" ที่บันทึกจ่ายเองได้ (ตัวจับคู่ยังรับ 3 หลักเพื่อเสนอค่า — ไม่ใช่เกณฑ์บันทึกเอง)</summary>
    public const int PayerAutoMinDigits = 4;

    /// <summary>ฝ่ายค้าน MED-2: สลิปผู้โอนแน่นพอให้บันทึกจ่ายเองไหม — เลขท้าย ≥ 4 หลัก <b>และ</b> ชื่อผู้โอนตรงชื่อบริษัท (ไม่ครบ ⇒ แจ้งยืนยัน)</summary>
    public static bool PayerSlipStrong(int visibleDigits, bool payerNameMatches) => visibleDigits >= PayerAutoMinDigits && payerNameMatches;

    public const string PayerSlipWeakReason = "สลิปยืนยันผู้โอนไม่ครบ (ต้องเห็นเลขท้ายบัญชี ≥ 4 หลัก และชื่อผู้โอนตรงชื่อบริษัท — บัญชีส่วนตัวของกรรมการอาจมีเลขท้ายเหมือน)";

    // ═══ วิธีสำรอง: แจ้งเตือนให้ยืนยัน + เตือนซ้ำ (ไม่ถี่ · มีเพดาน) — ตัวเดียวของทั้งสองฝั่ง ═══

    /// <summary>เตือนซ้ำหลังแจ้งครั้งแรก/ครั้งก่อนเมื่อพ้นช่วงนี้ (ครั้งที่ 1 = 1 วัน · ครั้งที่ 2 = 3 วัน)</summary>
    public static readonly IReadOnlyList<TimeSpan> RemindAfter = new[] { TimeSpan.FromDays(1), TimeSpan.FromDays(3) };

    /// <summary>เพดานการเตือนซ้ำต่อรายการ (ไม่นับการแจ้งครั้งแรก)</summary>
    public static int MaxReminders => RemindAfter.Count;

    /// <summary>ถึงเวลาเตือนซ้ำไหม · <paramref name="sent"/> = จำนวนที่เตือนซ้ำไปแล้ว · <paramref name="lastAt"/> = เวลาเตือนล่าสุด (null = ยังไม่เคย ⇒ นับจากแจ้งครั้งแรก)</summary>
    public static bool ReminderDue(DateTime firstNoticeAtUtc, int sent, DateTime? lastAtUtc, DateTime nowUtc)
    {
        if (sent < 0 || sent >= MaxReminders) return false;
        var from = lastAtUtc ?? firstNoticeAtUtc;
        return nowUtc - from >= RemindAfter[sent];
    }
}

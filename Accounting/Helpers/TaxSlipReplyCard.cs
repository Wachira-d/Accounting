using System.Globalization;

namespace Accounting.Helpers;

/// <summary>
/// **ข้อความตอบกลับกลุ่ม LINE/อีเมลของเส้นสลิปภาษี — ตัวสร้างข้อความเดียว** (pure · รอบ 272 · ดีไซน์ §5.6/§6 · คำตัดสิน 272-1-12 (D-12)) ·
/// ทุกผลมีข้อความ (ห้าม silent no-op) · ซื่อสัตย์ต่อสถานะ (ไม่บอกว่า "บันทึกแล้ว" ถ้ายังไม่บันทึก · ไม่บอก "ยื่นแล้ว" — สลิปไม่ใช่เลขรับ 272-1-4) ·
/// <b>ปิดบังเสมอ</b> (กลุ่มมีหลายคน): ชื่อบริษัทปิดบาง · เลขภาษี/บัตรประชาชนปิดบาง · Control/Txn 4 ตัวท้าย · ไม่มีชื่อผู้โอน ·
/// ยอดเงินตามธง <c>ShowAmounts</c> ของปลายทาง (ปิด ⇒ ไม่มีตัวเลขเงินในข้อความเลย) · LINE text ผ่าน <see cref="LineDocumentCard.Clean"/> ที่ผู้ส่ง
/// </summary>
public static class TaxSlipReplyCard
{
    public enum Kind
    {
        Remitted = 1, NeedsChoice, NeedsAccount, NoMatch, TaxIdMismatch, AmountMismatch, Duplicate, UnknownAgency, NotATaxSlip, AwaitingConsent,
        AwaitingConsentMore, Failed, ToStaff, NoTenant, IntakeOff, ConsentGranted, ConsentWithdrawn, ConsentExpired, BoundAskConsent, PaymentRequest,
        StaffRemitted, StaffRejected, ChoiceAccepted, FailedResend,
        // รอบ 275 แพ็ก D
        StaffLinked, OpenExpired, PaymentRequestCancelled, PaymentRequestExpired, TransferNotTax, PaymentRequestReopened,
    }

    /// <param name="CompanyName">ชื่อบริษัทลูกค้า (ถูกปิดบางในข้อความ)</param>
    /// <param name="FormLabel">ป้ายแบบ/งวด เช่น "ภ.พ.30 งวด ต.ค. 2569" (server computes)</param>
    /// <param name="TaxIdOnSlip">เลขผู้เสียภาษีบนสลิป (ถูกปิดบางในข้อความ)</param>
    /// <param name="Choices">ป้ายตัวเลือก (NeedsChoice) — ไม่มียอด</param>
    public sealed record Facts(string? CompanyName = null, string? FormLabel = null, decimal? Amount = null, decimal? Tax = null, decimal? Surcharge = null,
        decimal? Penalty = null, string? AccountLabel = null, DateTime? PayDate = null, string? ControlLast4 = null, string? TaxIdOnSlip = null,
        string? Reason = null, DateTime? RecordedAt = null, int HeldCount = 0, IReadOnlyList<string>? Choices = null, string? Link = null,
        decimal? ExpectedAmount = null, DateTime? DueDate = null, string? GrantedBy = null);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Money(decimal v) => v.ToString("N2", Inv);

    /// <summary>วันที่แบบ "15 พ.ย. 2569" (พ.ศ. — ข้อความถึงลูกค้า)</summary>
    public static string ThaiDay(DateTime d)
    {
        var c = ThaiDate.CalendarDateUtc(d);
        return c.Day.ToString(Inv) + " " + ThaiDate.ShortMonthBe(c.Year, c.Month);
    }

    /// <summary>ชื่อบริษัทปิดบาง (ตัวเดียวกับการ์ดผูกกลุ่ม)</summary>
    private static string MaskCompany(string? name) => ContactLineBindRule.MaskName(name) ?? "บริษัท";

    /// <summary>เลขผู้เสียภาษี: นิติบุคคล (ขึ้นต้น 0) ⇒ <see cref="PiiMask.TaxId"/> · บุคคลธรรมดา ⇒ <see cref="PiiMask.CitizenId"/></summary>
    public static string MaskTaxId(string? taxId)
    {
        var d = ThaiTaxId.Normalize(taxId);
        if (d.Length != 13) return "เลขผู้เสียภาษี (อ่านไม่ได้)";
        return d[0] == '0' ? PiiMask.TaxId(d)! : PiiMask.CitizenId(d)!;
    }

    private static string Cc(Facts f) => string.IsNullOrEmpty(f.ControlLast4) ? "" : " · Control ••" + f.ControlLast4;

    private static string Amt(Facts f, bool show, string prefix = " · ยอด ")
        => show && f.Amount is decimal a ? prefix + Money(a) : "";

    /// <summary>ข้อความของผลนี้ (ไม่มีผลไหนคืนค่าว่าง) · <paramref name="showAmounts"/> = ธง ShowAmounts ของปลายทาง (D-12)</summary>
    public static string Text(Kind kind, Facts f, bool showAmounts)
    {
        var co = MaskCompany(f.CompanyName);
        var form = string.IsNullOrWhiteSpace(f.FormLabel) ? "รายการภาษี" : f.FormLabel;
        // ฝ่ายค้าน 272 M3: เหตุ (จากตัวตัดสิน/RemitAsync) มีตัวเลขเงินได้ — ปิด ShowAmounts ⇒ ปิดตัวเลขในเหตุด้วยตัวเดียวกัน
        var reason = string.IsNullOrWhiteSpace(f.Reason) ? null : showAmounts ? f.Reason!.Trim() : MaskNumbers(f.Reason!.Trim());
        var why = reason == null ? "" : " (" + reason + ")";
        var link = string.IsNullOrWhiteSpace(f.Link) ? "" : "\n" + f.Link;
        switch (kind)
        {
            case Kind.Remitted:
            case Kind.StaffRemitted:
            {
                var parts = new List<string>();
                if (showAmounts && f.Tax is decimal t)
                {
                    var extra = (f.Surcharge ?? 0m) + (f.Penalty ?? 0m);
                    parts.Add("ยอด " + Money(t + extra) + (extra > 0m ? $" (ภาษี {Money(t)}" + (f.Surcharge is > 0m ? " + เงินเพิ่ม " + Money(f.Surcharge.Value) : "")
                        + (f.Penalty is > 0m ? " + เบี้ยปรับ " + Money(f.Penalty.Value) : "") + ")" : ""));
                }
                else if (!showAmounts) parts.Add("ยอดตรงกับใบแจ้งชำระ");
                if (!string.IsNullOrWhiteSpace(f.AccountLabel)) parts.Add("จ่ายจาก " + f.AccountLabel);
                if (f.PayDate is DateTime pd) parts.Add("เมื่อ " + ThaiDay(pd));
                var by = kind == Kind.StaffRemitted ? " โดยพนักงาน NextAcc" : "";
                return "✅ บันทึกการชำระแล้ว" + by + " — " + form + " ของ " + co + (parts.Count > 0 ? " · " + string.Join(" · ", parts) : "") + Cc(f)
                    + "\nยังไม่ได้บันทึกเลขรับการยื่นแบบ (สลิปไม่ใช่เลขรับ) · ไม่ต้องส่งสลิปซ้ำ" + link;
            }
            case Kind.NeedsChoice:
                return "ได้รับสลิปแล้ว" + Amt(f, showAmounts) + Cc(f) + " · ตรงได้ " + (f.Choices?.Count ?? 0) + " รายการ — แบบภาษีไม่ชัด กรุณาเลือกรายการที่ชำระ"
                    + (f.Choices is { Count: > 0 } ch ? ": " + string.Join(" / ", ch) : "") + " (ปุ่มหมดอายุใน 24 ชม. · ไม่มีผู้ตอบ ⇒ พนักงาน NextAcc ตรวจให้)";
            case Kind.ChoiceAccepted:
                return "รับทราบตัวเลือกแล้ว — " + form + " · กำลังบันทึก";
            case Kind.NeedsAccount:
                return "ได้รับสลิปแล้ว ตรง " + form + Cc(f) + " · ยังไม่ได้บันทึก — ระบบระบุบัญชีที่จ่ายจากสลิปไม่ได้" + why
                    + " · พนักงาน NextAcc จะเลือกบัญชีและแจ้งผลในกลุ่มนี้";
            case Kind.NoMatch:
                return "ได้รับสลิปแล้ว แต่ยังไม่พบรายการภาษีที่รอชำระที่ยอดตรง" + (showAmounts && f.Amount is decimal a1 ? " (ยอดบนสลิป " + Money(a1) + ")" : "")
                    + " — ยังไม่ได้บันทึก · พนักงาน NextAcc จะตรวจและแจ้งผลในกลุ่มนี้";
            case Kind.TaxIdMismatch:
                return "เลขผู้เสียภาษีบนสลิป (" + MaskTaxId(f.TaxIdOnSlip) + ") ไม่ตรงบริษัทที่ช่องทางนี้ผูกไว้ — ยังไม่ได้บันทึก · พนักงานจะตรวจ";
            case Kind.AmountMismatch:
                return "ยอดบนสลิปไม่ตรงยอดในใบแจ้งชำระ" + (showAmounts && f.Amount is decimal a2 && f.ExpectedAmount is decimal e ? $" ({Money(a2)} ≠ {Money(e)})" : "")
                    + " — ยังไม่ได้บันทึก (ถ้าจ่ายบางส่วน พนักงานจะตรวจให้)";
            case Kind.Duplicate:
                return "สลิปนี้" + (f.RecordedAt is DateTime r ? "บันทึกไปแล้วเมื่อ " + ThaiDay(r) : "ได้รับไปแล้ว") + (string.IsNullOrWhiteSpace(f.FormLabel) ? "" : " (" + f.FormLabel + ")")
                    + " — ไม่บันทึกซ้ำ" + why;
            case Kind.UnknownAgency:
                return "สลิปนี้ดูเป็นการชำระหน่วยงานรัฐ แต่รูปแบบยังไม่อยู่ในรายการที่ระบบอ่านได้แน่นอน — ยังไม่ได้บันทึก · ส่งให้พนักงานตรวจแล้ว" + why;
            case Kind.NotATaxSlip:
                return "รูปนี้ไม่ใช่สลิปชำระภาษี/ประกันสังคม — ระบบไม่ได้บันทึก";
            case Kind.AwaitingConsent:
                return "ได้รับสลิปชำระภาษีแล้ว" + Amt(f, showAmounts) + Cc(f) + " — ยังไม่ได้บันทึก เพราะบริษัทยังไม่อนุญาตให้ NextAcc บันทึกแทน · พักไว้ 30 วัน · "
                    + "เมื่อเจ้าของบริษัทอนุญาตบนเว็บแล้วระบบบันทึกให้เอง" + link;
            case Kind.AwaitingConsentMore:
                return "พักสลิปไว้เป็นใบที่ " + f.HeldCount.ToString(Inv) + " — รอเจ้าของบริษัทอนุญาต (ยังไม่ได้บันทึก)";
            case Kind.FailedResend:
                return "ได้รับรูปแล้ว แต่ระบบรับสลิปไม่สำเร็จ (ยังไม่ได้บันทึก) — กรุณาส่งรูปอีกครั้ง";
            case Kind.Failed:
                return "ได้รับสลิปแล้ว แต่ระบบบันทึกไม่สำเร็จ — ส่งให้พนักงานตรวจแล้ว (ไม่ต้องส่งซ้ำ)";
            case Kind.ToStaff:
                return "ได้รับสลิปแล้ว" + Cc(f) + " — ยังไม่ได้บันทึก" + why + " · พนักงาน NextAcc จะตรวจและแจ้งผลในกลุ่มนี้";
            case Kind.NoTenant:
                return "ได้รับสลิปแล้ว — ผู้ติดต่อที่ผูกช่องทางนี้ไม่มีบัญชีบริษัทในระบบ จึงบันทึกให้ไม่ได้ · ส่งให้พนักงาน NextAcc ตรวจแล้ว";
            case Kind.IntakeOff:
                return "ระบบปิดรับสลิปชั่วคราว — สลิปนี้ยังไม่ได้บันทึก กรุณาส่งให้พนักงาน NextAcc โดยตรง";
            case Kind.ConsentGranted:
                return "เจ้าของบริษัทอนุญาตแล้ว ✅" + (string.IsNullOrWhiteSpace(f.GrantedBy) ? "" : " (โดย " + f.GrantedBy + (f.RecordedAt is DateTime g ? " · " + ThaiDay(g) : "") + ")")
                    + (f.HeldCount > 0 ? " · กำลังบันทึกสลิปที่พักไว้ " + f.HeldCount.ToString(Inv) + " ใบ (แจ้งผลทีละใบ)" : "");
            case Kind.ConsentWithdrawn:
                return "บริษัทถอนการอนุญาตแล้ว — ต่อจากนี้ระบบจะไม่บันทึกจากสลิป · สลิปที่ส่งมาจะส่งให้พนักงาน NextAcc ตรวจแทน · รายการที่บันทึกไปแล้วไม่ถูกแตะ";
            case Kind.ConsentExpired:
                return "คำขออนุญาตหมดอายุ — สลิปที่พักไว้ " + f.HeldCount.ToString(Inv) + " ใบส่งให้พนักงาน NextAcc ตรวจแล้ว (ไม่ได้ลบ)";
            case Kind.BoundAskConsent:
                return "ต่อไปส่งสลิปชำระภาษีในกลุ่มนี้ได้ · ตอนนี้ระบบยังบันทึกให้ไม่ได้จนกว่าเจ้าของ " + co + " จะอนุญาตบนเว็บ (ครั้งเดียว · ถอนได้ทุกเมื่อ)" + link;
            case Kind.PaymentRequest:
                return "ใบแจ้งชำระ " + form + " ของ " + co + Amt(f, showAmounts) + (f.DueDate is DateTime dd ? " · ชำระภายใน " + ThaiDay(dd) : "") + Cc(f)
                    + " · จ่ายแล้วส่งสลิปกลับในกลุ่มนี้" + link;
            case Kind.StaffRejected:
                return "พนักงาน NextAcc ตรวจสลิปแล้ว — ไม่ได้บันทึก" + why;
            // รอบ 275 แพ็ก D (H1): พนักงานผูกสลิปกับรายการนำส่งที่บันทึกไว้ก่อนแล้ว — การชำระบันทึกแล้วจริง (ไม่ใช่ "ไม่ได้บันทึก")
            case Kind.StaffLinked:
                return "✅ บันทึกการชำระแล้ว — " + form + " ของ " + co + (f.PayDate is DateTime lp ? " · เมื่อ " + ThaiDay(lp) : "") + Cc(f)
                    + " · พนักงาน NextAcc แนบสลิปนี้เป็นหลักฐานของรายการนำส่งที่บันทึกไว้แล้ว · ไม่ต้องส่งสลิปซ้ำ";
            // รอบ 275 แพ็ก D (M4): แถวรอตรวจพ้นกำหนด — บอกตามจริงว่ายังไม่ได้บันทึก
            case Kind.OpenExpired:
                // ฝ่ายค้าน LOW-1: หลายใบในกลุ่มเดียวต่อรอบ ⇒ การ์ดสรุปใบเดียว
                if (f.HeldCount > 1)
                    return "สลิป " + f.HeldCount.ToString(Inv) + " ใบ" + (f.RecordedAt is DateTime oes ? " ที่ส่งตั้งแต่ " + ThaiDay(oes) : "")
                        + " ยังไม่ได้บันทึก และพ้นระยะรอตรวจแล้ว — ระบบปิดรายการเหล่านี้ · ถ้ายังต้องบันทึก กรุณาส่งสลิปอีกครั้งหรือแจ้งพนักงาน NextAcc";
                return "สลิป" + Cc(f) + (f.RecordedAt is DateTime oe ? " ที่ส่งเมื่อ " + ThaiDay(oe) : "") + " ยังไม่ได้บันทึก และพ้นระยะรอตรวจแล้ว — ระบบปิดรายการนี้"
                    + " · ถ้ายังต้องบันทึก กรุณาส่งสลิปอีกครั้งหรือแจ้งพนักงาน NextAcc";
            // รอบ 275 แพ็ก D (M7): ใบแจ้งชำระที่ส่งไปแล้วถูกยกเลิก/หมดอายุ — อย่าจ่ายตาม Control Code นี้
            case Kind.PaymentRequestCancelled:
                return "ยกเลิกใบแจ้งชำระ " + form + " ของ " + co + Cc(f) + " แล้ว — กรุณาอย่าชำระตามใบนี้ · พนักงาน NextAcc จะส่งใบใหม่ให้ถ้ายังต้องชำระ";
            case Kind.PaymentRequestExpired:
                return "ใบแจ้งชำระ" + (f.HeldCount > 1 ? " " + f.HeldCount.ToString(Inv) + " ใบ" : "") + " " + form + " ของ " + co + Cc(f)
                    + " หมดอายุแล้ว — ชำระตาม Control Code นี้ไม่ได้ · ติดต่อพนักงาน NextAcc เพื่อขอใบใหม่ (ถ้าชำระไปแล้ว ส่งสลิปในกลุ่มนี้)";
            // ฝ่ายค้าน LOW-3: รายการนำส่งที่บันทึกตามใบนี้ถูกยกเลิกในบัญชี ⇒ ใบกลับเป็นรอชำระ — บอกตามจริง ไม่สั่งให้จ่ายซ้ำ
            case Kind.PaymentRequestReopened:
                return "บันทึกการชำระ " + form + " ของ " + co + Cc(f) + " ถูกยกเลิกในบัญชีของบริษัท — ใบแจ้งชำระกลับเป็นรอชำระ"
                    + " · ถ้าชำระแล้ว ไม่ต้องจ่ายซ้ำ พนักงาน NextAcc จะตรวจและแจ้งผลในกลุ่มนี้";
            // รอบ 275 แพ็ก D (M5): สลิปโอนเงินทั่วไปในกลุ่มของ OA กลาง — ไม่ใช่งานของเส้นภาษี แต่อาจเป็นค่าบริการ ⇒ บอกทางไปต่อ (พนักงานได้กระดิ่งแล้ว)
            case Kind.TransferNotTax:
                return "รูปนี้เป็นสลิปโอนเงินทั่วไป ไม่ใช่สลิปชำระภาษี/ประกันสังคม — ระบบภาษีไม่ได้บันทึก · ถ้าเป็นการชำระค่าบริการ NextAcc พนักงานได้รับแจ้งแล้วและจะตรวจรายการเงินเข้าให้";
        }
        return "ได้รับสลิปแล้ว — พนักงาน NextAcc จะตรวจและแจ้งผล";
    }

    /// <summary>ข้อความที่ส่งออก (LINE/อีเมล) — ตัดอักขระควบคุมยกเว้นขึ้นบรรทัด · จำกัดความยาวตาม LINE · ว่าง ⇒ "-" (LINE ปฏิเสธ text ว่าง)</summary>
    public static string Clean(string? text)
    {
        var t = new string((text ?? "").Select(ch => ch == '\n' ? ch : char.IsControl(ch) ? ' ' : ch).ToArray()).Trim();
        return t.Length == 0 ? "-" : LineMessageLimits.Truncate(t, 4900);
    }

    private static readonly System.Text.RegularExpressions.Regex NumberRx = new(@"\d[\d,]*(?:\.\d+)?", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>ฝ่ายค้าน 272 M3: ปิดตัวเลขทุกชุดในข้อความ (ยอด · จำนวนใบ · เพดาน) — ใช้กับเหตุในการ์ดเมื่อปลายทางปิด ShowAmounts</summary>
    private static string MaskNumbers(string text) => NumberRx.Replace(text, "•••");

    /// <summary>บรรทัดต่อท้ายใบแจ้งชำระเมื่อบริษัทยังไม่อนุญาต (T2)</summary>
    public const string ConsentHint = "\nส่งสลิปกลับมาในกลุ่มนี้ได้ — ระบบจะบันทึกให้เมื่อเจ้าของบริษัทอนุญาตบนเว็บ";
}

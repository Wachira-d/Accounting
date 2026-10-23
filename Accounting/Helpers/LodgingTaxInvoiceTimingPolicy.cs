using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>เหตุการณ์ของการจองที่พักที่อาจทำให้ต้องออก "ใบกำกับภาษีล่วงหน้า" (โหมด <see cref="BookingInvoiceTiming.TaxInvoiceAtBooking"/>)</summary>
public enum LodgingTaxTrigger
{
    /// <summary>พนักงานกด "ยืนยัน" การจองที่รอชำระ (ไม่มีเงินเข้าในคำขอนั้น)</summary>
    StaffConfirm,
    /// <summary>พนักงานสร้างการจองแบบยืนยันทันที (โทร/walk-in/ตัวแทน — ไม่ใช่เว็บ)</summary>
    StaffCreateConfirmed,
    /// <summary>มีเงินเข้า (ออนไลน์ · บันทึกรับตามสลิป · ยืนยัน+รับมัดจำ · รับชำระเพิ่ม/ระหว่างพัก) — ทุกเส้นเดิน <c>ConfirmCoreAsync</c></summary>
    MoneyReceived,
    /// <summary>พนักงานกดปุ่ม "ออกใบกำกับภาษีล่วงหน้า" เอง</summary>
    ManualIssue,
}

/// <summary>สถานะของใบกำกับภาษีล่วงหน้าที่ผูกกับการจอง (<c>LodgingReservation.AdvanceTaxDocumentId</c>) — ตัดสินจาก "เอกสารที่ผูกจริง"</summary>
public enum LodgingAdvanceState
{
    /// <summary>ไม่เคยออก</summary>
    None,
    /// <summary>ออกแล้วและยังมีผล — ภาษีขายของค่าห้องอยู่ในใบนี้</summary>
    Live,
    /// <summary>เคยออกแต่ถูกยกเลิก/ลบที่หน้าเอกสาร — การจองนี้กลับไปใช้จังหวะปกติ (ไม่ออกใหม่เองอัตโนมัติ)</summary>
    Gone,
}

/// <param name="Timing">ค่าตั้งของที่พัก ณ ตอนนี้ — มีผลเฉพาะ "จะออกใบใหม่ไหม" · ใบที่ผูกแล้วตัดสินจากเอกสาร (สลับค่าตั้งกลางทาง tax point ไม่หลุด)</param>
/// <param name="AccountingMode">โหมดออกเอกสารของที่พัก — ใบกำกับล่วงหน้าใช้ได้เฉพาะ <see cref="LodgingAccountingMode.Full"/></param>
/// <param name="Status">สถานะการจองปัจจุบัน</param>
/// <param name="Source">ช่องทางที่การจองเข้ามา (OTA ไม่ออกเองอัตโนมัติ)</param>
/// <param name="Advance">สถานะใบกำกับล่วงหน้าที่ผูกอยู่</param>
/// <param name="HasVatBearingRoomDeposit">มีใบมัดจำค่าห้องที่ยังมีผลซึ่ง "มี VAT" (รับรู้ทันที/ภาษีรอเรียกเก็บ) — ออกตามจังหวะปกติไปแล้ว ⇒ การจองนี้คงจังหวะเดิม
/// (ใบกำกับเต็มยอดจะซ้อน VAT กับใบมัดจำ) · มัดจำที่ไม่มี VAT (เต็มยอด) ไม่นับ — ใบกำกับล่วงหน้าครอบคลุมและตัดชำระได้</param>
/// <param name="HasFinalDocument">มีใบเช็คเอาต์แล้ว</param>
public readonly record struct LodgingAdvanceFacts(
    BookingInvoiceTiming Timing,
    LodgingAccountingMode AccountingMode,
    LodgingReservationStatus Status,
    LodgingReservationSource Source,
    LodgingAdvanceState Advance,
    bool HasVatBearingRoomDeposit,
    bool HasFinalDocument);

/// <summary>ผลตัดสิน — <see cref="Issue"/> = ออกใบกำกับล่วงหน้าตอนนี้ · <see cref="Notice"/> = เหตุผลที่ไม่ออก (ข้อความไทยถึงผู้ใช้)</summary>
public sealed record LodgingAdvanceDecision(bool Issue, string? Notice = null);

/// <summary>ส่วนต่างของค่าห้อง/บริการเสริม/service charge ระหว่าง "ยอดเข้าพักตอนนี้" กับ "ใบกำกับล่วงหน้าหลังหักใบลดหนี้"</summary>
/// <param name="Gross">ส่วนต่างรวม VAT (บวก = เรียกเก็บเพิ่มในใบเช็คเอาต์ · ลบ = ออกใบลดหนี้อ้างใบล่วงหน้า)</param>
/// <param name="InPriceConvention">ส่วนต่างตามรูปราคาของที่พัก (ราคารวม VAT ⇒ = Gross · ไม่รวม ⇒ ส่วนต่างก่อน VAT) — ใช้เป็นราคาบรรทัดของใบเช็คเอาต์</param>
public readonly record struct LodgingStayDelta(decimal Gross, decimal InPriceConvention)
{
    public bool IsCharge => Gross > 0.005m;
    public bool IsCredit => Gross < -0.005m;
}

/// <summary>
/// <b>จังหวะออกเอกสารภาษีของโมดูลที่พัก — ตัวตัดสินเดียว</b> (คำตัดสินเจ้าของข้อ 156 · 2026-10-23 · ต่อจากข้อ 155 "ต้องเปิดให้ตั้งค่าได้ว่าจะเป็นแบบไหนก็ได้") · pure
///
/// <para><b>ความหมายเดียวกับค่าตั้งของการจองนัดหมาย</b> (<see cref="BookingInvoiceTiming"/> · <see cref="BookingInvoiceTimingPolicy"/>) — ใช้ enum ตัวเดียวกัน
/// (ห้ามมีสองชุดเมื่อความหมายเดียวกัน) แต่ข้อเท็จจริงของเอกสารต่างโมดูล ⇒ ตัวตัดสินแยก:</para>
/// <list type="bullet">
/// <item><see cref="BookingInvoiceTiming.TaxInvoiceOnPaymentOrService"/> = <b>ค่าเริ่มต้นของที่พัก = พฤติกรรมเดิมทุกตัวอักษร</b>: รับเงิน ⇒ ใบรับเงิน/ใบมัดจำ
/// ตาม "ประเภทเงินมัดจำค่าห้อง" (รับรู้ VAT ทันที = ใบกำกับภาษี/ใบเสร็จรับเงิน — tax point = วันรับเงิน) · เช็คเอาต์ ⇒ ใบกำกับเต็มรูปหักมัดจำ (tax point = วันใช้บริการ)</item>
/// <item><see cref="BookingInvoiceTiming.TaxInvoiceAtBooking"/> = ใบกำกับภาษีเต็มยอดการเข้าพัก <b>เมื่อพนักงานยืนยัน หรือรับเงินครั้งแรก (แล้วแต่อย่างไหนก่อน)</b>
/// — ออกใบก่อนรับเงิน ⇒ tax point = วันออกใบ (§78/1(1)) · เงินที่รับหลังจากนั้นเป็นใบรับเงินมัดจำไม่มี VAT (ภาษีอยู่ในใบกำกับแล้ว) ·
/// เช็คเอาต์ออกใบกำกับเฉพาะส่วนเพิ่ม/รายการระหว่างพัก · ยอดลด/ยกเลิก/ไม่มา ⇒ ใบลดหนี้อ้างใบเดิม (§86/10)</item>
/// </list>
/// <para><b>แบบ "ออกใบกำกับภาษีทุกครั้งที่รับเงิน แล้วเช็คเอาต์ออกเฉพาะส่วนต่าง" ไม่ใช่ค่าใหม่</b> — คือค่าเริ่มต้นเมื่อประเภทเงินมัดจำค่าห้องเป็น
/// "รับรู้ VAT ทันที" (ใบมัดจำ = ใบกำกับภาษี/ใบเสร็จรับเงิน · ใบเช็คเอาต์หักฐานมัดจำ คิด VAT เฉพาะส่วนที่เหลือ) ⇒ ตัวตั้งเดียวคือประเภทเงินมัดจำ
/// (<see cref="PaymentInvoiceNote"/> อธิบายบนหน้าตั้งค่า)</para>
/// <para>กติกา: (1) ใบที่ผูกแล้วตัดสินจากเอกสาร ไม่ใช่ค่าตั้งปัจจุบัน · (2) ใบล่วงหน้าที่มีผลแล้ว ⇒ ไม่ออกซ้ำ (webhook/สลิป/กดซ้ำ) · (3) ใบล่วงหน้าถูกยกเลิก ⇒
/// ไม่ออกใหม่เอง (ผู้ยกเลิกมีเหตุผล) แต่เปิดปุ่มให้คนตัดสิน · (4) มีมัดจำที่มี VAT อยู่แล้ว ⇒ คงจังหวะเดิม (กัน VAT ซ้อน) · (5) การจองจากเว็บที่ยืนยันเอง
/// โดยยังไม่จ่ายเงิน และ OTA ไม่ออกเองอัตโนมัติ (ใบเลขจริงจากการจองที่ใครก็สร้างได้ · ผู้ซื้อ/ผู้รับเงินผ่านตัวแทน)</para>
/// </summary>
public static class LodgingTaxInvoiceTimingPolicy
{
    /// <summary>ค่าเริ่มต้นของที่พัก (คอลัมน์ DEFAULT 1) = พฤติกรรมเดิม</summary>
    public const BookingInvoiceTiming Default = BookingInvoiceTiming.TaxInvoiceOnPaymentOrService;

    public const string RuleCode = "LODGING-TAX-TIMING";

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>สถานะใบล่วงหน้าจาก "เอกสารที่ผูกจริง" — ผูกไว้แต่หาใบไม่พบ/ถูกลบ ส่ง status = null พร้อม linked = true ⇒ Gone</summary>
    public static LodgingAdvanceState StateOf(bool linked, DocumentStatus? status)
    {
        if (!linked) return LodgingAdvanceState.None;
        return status is null or DocumentStatus.Voided or DocumentStatus.Rejected ? LodgingAdvanceState.Gone : LodgingAdvanceState.Live;
    }

    /// <summary>เงินที่รับตอนนี้ "ภาษีอยู่ในใบกำกับล่วงหน้าแล้ว" ไหม — จริง ⇒ ใบรับเงินมัดจำไม่มี VAT (ไม่ใช่ตามประเภทเงินมัดจำ)</summary>
    public static bool DepositCoveredByAdvance(LodgingAdvanceState s) => s == LodgingAdvanceState.Live;

    public static LodgingAdvanceDecision Decide(LodgingAdvanceFacts f, LodgingTaxTrigger trigger)
    {
        var manual = trigger == LodgingTaxTrigger.ManualIssue;
        LodgingAdvanceDecision No(string? note) => new(false, manual ? note : null);

        if (f.Timing != BookingInvoiceTiming.TaxInvoiceAtBooking)
            return No("ที่พักนี้ตั้งจังหวะออกเอกสารภาษีแบบค่าเริ่มต้น (ใบมัดจำเมื่อรับเงิน · ใบกำกับตอนเช็คเอาต์) — เปลี่ยนได้ที่ตั้งค่าที่พัก › ภาษี & บัญชี");
        if (f.AccountingMode != LodgingAccountingMode.Full)
            return No("ที่พักนี้ไม่ได้ตั้งให้ระบบออกเอกสารบัญชีเต็มรูป — ออกใบกำกับล่วงหน้าไม่ได้");
        if (f.Advance == LodgingAdvanceState.Live)
            return No("การจองนี้ออกใบกำกับภาษีล่วงหน้าแล้ว — ไม่ออกซ้ำ");
        if (f.HasFinalDocument)
            return No("การจองนี้ออกใบเช็คเอาต์แล้ว — ไม่ออกใบกำกับล่วงหน้า");
        if (f.Status is LodgingReservationStatus.Cancelled or LodgingReservationStatus.NoShow or LodgingReservationStatus.CheckedOut)
            return No("การจองนี้ปิดแล้ว (ยกเลิก/ไม่มา/เช็คเอาต์) — ออกใบกำกับล่วงหน้าไม่ได้");
        if (f.HasVatBearingRoomDeposit)
            return No("การจองนี้รับมัดจำแบบมี VAT ไปแล้ว (ออกตามจังหวะเดิม) — ใบกำกับออกตอนเช็คเอาต์และหักมัดจำตามเดิม (ออกใบกำกับเต็มยอดตอนนี้ = VAT ซ้อนกับใบมัดจำ)");
        if (f.Advance == LodgingAdvanceState.Gone && !manual)
            return No(null);   // ผู้ยกเลิกใบล่วงหน้ามีเหตุผล — ระบบไม่ออกใหม่เอง (ปุ่มยังเปิดให้คนตัดสิน)
        switch (trigger)
        {
            case LodgingTaxTrigger.MoneyReceived:
            case LodgingTaxTrigger.StaffConfirm:
                return new(true);
            case LodgingTaxTrigger.StaffCreateConfirmed:
                if (f.Status != LodgingReservationStatus.Confirmed) return No(null);
                // OTA: ผู้รับเงิน/ผู้ซื้ออาจเป็นตัวแทน (merchant/agency) — ไม่ออกใบกำกับในนามแขกเองอัตโนมัติ (กดปุ่มเองได้)
                return f.Source == LodgingReservationSource.Ota ? No(null) : new(true);
            default:   // ManualIssue
                if (f.Status == LodgingReservationStatus.Pending)
                    return No("การจองยังรอยืนยัน — กด “ยืนยัน” ก่อน (ระบบออกใบกำกับล่วงหน้าให้ตอนยืนยัน)");
                return new(true);
        }
    }

    /// <summary>แสดงปุ่ม "ออกใบกำกับภาษีล่วงหน้า" ไหม — ตัวตัดสินเดียวกับตอนกด</summary>
    public static bool CanOfferManualIssue(LodgingAdvanceFacts f) => Decide(f, LodgingTaxTrigger.ManualIssue).Issue;

    /// <summary>ด่านตอนบันทึกค่าตั้งที่พัก — คืนข้อความปฏิเสธ หรือ null</summary>
    public static string? TimingProblem(BookingInvoiceTiming timing, LodgingAccountingMode mode)
    {
        if (!Enum.IsDefined(timing)) return "จังหวะออกเอกสารภาษีที่เลือกไม่รู้จัก — รีเฟรชหน้าแล้วเลือกใหม่";
        if (timing == BookingInvoiceTiming.TaxInvoiceAtBooking && mode != LodgingAccountingMode.Full)
            return "“ออกใบกำกับภาษีเมื่อยืนยันการจอง” ใช้ได้เฉพาะเมื่อให้ระบบออกเอกสารบัญชีเต็มรูป (โหมดออกเอกสาร = เต็มรูป)";
        return null;
    }

    /// <summary>ส่วนต่างตอนเช็คเอาต์ระหว่างยอดเข้าพักตอนนี้ (ตัวสร้างรายการใบสุดท้ายตัวเดียว · ไม่รวม folio) กับใบล่วงหน้าหลังหักใบลดหนี้ —
    /// ที่มา: เลื่อนวัน (ราคาใหม่) · เช็คอินก่อนวัน (+1 คืน) · ใบลดหนี้ที่ออกเองที่หน้าเอกสาร</summary>
    public static LodgingStayDelta StayDelta(bool pricesIncludeVat, decimal nowNet, decimal nowGross, decimal advanceNet, decimal advanceGross)
    {
        var gross = R2(nowGross - advanceGross);
        return new LodgingStayDelta(gross, pricesIncludeVat ? gross : R2(nowNet - advanceNet));
    }

    /// <summary>ยอดใบลดหนี้ (รวม VAT) ตอนยกเลิก/ไม่มา — ใบล่วงหน้าคงไว้เท่าค่าปรับที่ริบจากมัดจำ (ตัดชำระด้วยมัดจำนั้น) ส่วนที่เหลือลดหนี้ทั้งหมด ·
    /// ค่าปรับไม่เกินมัดจำที่รับไว้ (คำตัดสินข้อ 126) ⇒ ไม่ได้รับเงินเลย = ลดหนี้เต็มใบ</summary>
    public static decimal CancelCreditGross(decimal advanceGrossRemaining, decimal forfeitAppliedToAdvance)
        => Math.Max(0m, R2(advanceGrossRemaining - Math.Max(0m, forfeitAppliedToAdvance)));

    /// <summary>กระจายยอดลดหนี้ (รวม VAT) ลงบรรทัดของใบเดิมตามสัดส่วนยอดรวม VAT ของแต่ละบรรทัด (ลงกลับผัง/อัตราเดิม · §6.2f) ·
    /// ผลรวม = <paramref name="creditGross"/> พอดี (บรรทัดสุดท้ายที่มียอดรับเศษ) · ลดหนี้เต็มใบ ⇒ เท่ายอดเดิมทุกบรรทัด · ห้ามติดลบ/เกินยอดบรรทัด</summary>
    public static IReadOnlyList<decimal> AllocateCredit(IReadOnlyList<decimal> lineGross, decimal creditGross)
    {
        var n = lineGross.Count;
        var result = new decimal[n];
        var total = lineGross.Sum(x => Math.Max(0m, x));
        var credit = Math.Min(R2(Math.Max(0m, creditGross)), total);
        if (n == 0 || total <= 0m || credit <= 0m) return result;
        if (credit >= total) { for (var i = 0; i < n; i++) result[i] = Math.Max(0m, lineGross[i]); return result; }
        var last = -1;
        for (var i = 0; i < n; i++) if (lineGross[i] > 0m) last = i;
        decimal used = 0m;
        for (var i = 0; i < n; i++)
        {
            if (lineGross[i] <= 0m || i == last) continue;
            result[i] = Math.Min(lineGross[i], R2(credit * lineGross[i] / total));
            used += result[i];
        }
        // เศษลงบรรทัดสุดท้าย — ถ้าเกินยอดบรรทัดนั้น (ปัดสะสม) ไหลย้อนกลับไปบรรทัดก่อนหน้า
        var rest = credit - used;
        for (var i = last; i >= 0 && rest != 0m; i--)
        {
            if (lineGross[i] <= 0m) continue;
            var room = lineGross[i] - result[i];
            var take = rest > 0m ? Math.Min(room, rest) : Math.Max(-result[i], rest);
            result[i] += take;
            rest -= take;
        }
        return result;
    }

    /// <summary>ตัวเลือกบนหน้าตั้งค่าที่พัก + ผลทางภาษี (ภาษาไทย · server ประกอบ · หน้าเว็บแสดงตรง ๆ)</summary>
    public static IReadOnlyList<BookingInvoiceTimingOption> Options() => new[]
    {
        new BookingInvoiceTimingOption(nameof(BookingInvoiceTiming.TaxInvoiceOnPaymentOrService),
            "รับเงิน ⇒ ใบรับเงิน/ใบมัดจำ · เช็คเอาต์ ⇒ ใบกำกับภาษีหักมัดจำ (ค่าเริ่มต้น · แบบเดิม)",
            "ยืนยันการจองไม่ออกเอกสาร · เงินที่รับก่อนเช็คเอาต์ออกใบมัดจำตาม “ประเภทเงินมัดจำค่าห้อง” (รับรู้ VAT ทันที = ใบกำกับภาษี/ใบเสร็จรับเงิน "
            + "ภาษีเข้า ภ.พ.30 เดือนที่รับเงิน — มาตรา 78/1) · เช็คเอาต์ออกใบกำกับภาษีของทั้งการเข้าพัก หักมูลค่ามัดจำที่ออกใบกำกับแล้ว "
            + "ภาษีส่วนที่เหลือเข้าเดือนที่เช็คเอาต์ (วันใช้บริการ) · ยกเลิก/ไม่มา = ริบมัดจำเป็นค่าปรับ ส่วนที่เหลือคืนแขก"),
        new BookingInvoiceTimingOption(nameof(BookingInvoiceTiming.TaxInvoiceAtBooking),
            "ออกใบกำกับภาษีเต็มยอดเมื่อยืนยันการจอง หรือรับเงินครั้งแรก (แล้วแต่อย่างไหนก่อน) — ลูกค้าองค์กร/บริษัททัวร์",
            "ภาษีขายของทั้งการเข้าพักเข้า ภ.พ.30 เดือนที่ออกใบ แม้ยังไม่ได้รับเงิน/ยังไม่เข้าพัก (ออกใบก่อน = จุดความรับผิดคือวันออกใบ มาตรา 78/1) · "
            + "เงินที่รับหลังจากนั้นออก “ใบรับเงินมัดจำ” ไม่มี VAT (ภาษีอยู่ในใบกำกับแล้ว) แล้วตัดชำระใบกำกับตอนเช็คเอาต์ · "
            + "เช็คเอาต์ออกใบกำกับเฉพาะค่าใช้จ่ายระหว่างพัก/ส่วนที่เพิ่ม (ยอดลดลง = ใบลดหนี้อ้างใบเดิม มาตรา 86/10) · "
            + "ยกเลิก/ไม่มา = ใบลดหนี้ส่วนที่ไม่เรียกเก็บ (ค่าปรับไม่เกินมัดจำที่รับไว้) · "
            + "การจองจากเว็บที่ยืนยันเองโดยยังไม่จ่ายเงิน และการจองจาก OTA ไม่ออกให้อัตโนมัติ (กด “ออกใบกำกับภาษีล่วงหน้า” ที่หน้าการจองได้) · "
            + "การจองที่รับมัดจำแบบมี VAT ไปก่อนเปลี่ยนค่าตั้ง คงแบบเดิม · ใบกำกับล่วงหน้าเป็นใบเต็มรูปในนามผู้ติดต่อของการจอง (เลขชุดเดียวกับใบกำกับ นับโควตาเอกสาร)"),
    };

    /// <summary>คำอธิบายแบบ "ใบกำกับทุกครั้งที่รับเงิน แล้วเช็คเอาต์ออกเฉพาะส่วนต่าง" — ตัวตั้งคือประเภทเงินมัดจำค่าห้อง (ไม่ใช่ค่าใหม่) · บอกผลของที่พักนี้ตอนนี้</summary>
    /// <param name="roomDepositTreatment">วิธีบันทึกมัดจำค่าห้องที่ใช้จริง (<c>DepositPolicyResolver.ResolveKind</c>)</param>
    /// <param name="nonVatSupply">ประเภทเงินมัดจำเป็น "นอกระบบ VAT"</param>
    /// <param name="vatCharged">ที่พักคิด VAT อยู่ (อัตราที่ใช้จริง &gt; 0)</param>
    public static string PaymentInvoiceNote(DepositVatTreatment roomDepositTreatment, bool nonVatSupply, bool vatCharged)
    {
        const string head = "แบบ “ออกใบกำกับภาษีทุกครั้งที่รับเงิน แล้วเช็คเอาต์ออกเฉพาะส่วนต่าง” คือค่าเริ่มต้นเมื่อประเภทเงินมัดจำค่าห้องเป็น “รับรู้ VAT ทันที” "
            + "(ใบมัดจำ = ใบกำกับภาษี/ใบเสร็จรับเงิน · ใบเช็คเอาต์หักมูลค่ามัดจำ คิด VAT เฉพาะส่วนที่เหลือ) — ไม่แยกเป็นตัวเลือกใหม่เพราะความหมายเดียวกัน · ที่พักนี้ตอนนี้: ";
        if (!vatCharged || nonVatSupply)
            return head + "ไม่คิด VAT ⇒ ออกใบเสร็จ/ใบแจ้งหนี้ (ผู้ไม่ได้คิด VAT ออกใบกำกับภาษีไม่ได้ — มาตรา 90/2)";
        return head + (roomDepositTreatment == DepositVatTreatment.VatImmediate
            ? "รับเงินทุกครั้งออกใบกำกับภาษีแล้ว (ใช้ได้ทันที)"
            : "ใบมัดจำยังไม่ใช่ใบกำกับภาษี (" + DepositPolicyResolver.LabelOf(roomDepositTreatment)
              + ") — ถ้าต้องการให้ทุกการรับเงินเป็นใบกำกับ เลือกประเภทเงินมัดจำค่าห้องที่ “รับรู้ VAT ทันที”");
    }

    /// <summary>หมายเหตุบนการจองเมื่อเลื่อนวันจนยอดเปลี่ยนหลังออกใบกำกับล่วงหน้า</summary>
    public static string RescheduleNote(string advanceNumber, decimal oldTotal, decimal newTotal)
        => $"ยอดเข้าพักเปลี่ยน {oldTotal:N2} → {newTotal:N2} หลังออกใบกำกับภาษีล่วงหน้า {advanceNumber} — ระบบปรับส่วนต่างตอนเช็คเอาต์ "
           + (newTotal > oldTotal ? "(ใบกำกับส่วนเพิ่ม)" : "(ใบลดหนี้อ้างใบเดิม)") + " · ยกเลิก = ใบลดหนี้ส่วนที่ไม่เรียกเก็บ";

    /// <summary>หมายเหตุบนการจองเมื่อใบล่วงหน้าถูกยกเลิกแต่ยังมีเงินที่รับไว้แบบไม่มี VAT (เงินที่รับ = จุดความรับผิด §78/1)</summary>
    public static string GoneWithUntaxedMoneyNote(string advanceNumber)
        => $"ใบกำกับภาษีล่วงหน้า {advanceNumber} ถูกยกเลิกแล้ว แต่เงินที่รับระหว่างนั้นออกเป็นใบรับเงินมัดจำไม่มี VAT — เงินที่รับคือจุดความรับผิด (มาตรา 78/1) · "
           + "กด “ออกใบกำกับภาษีล่วงหน้า” ใหม่ (ครอบคลุมเงินที่รับแล้ว) หรือให้นักบัญชีออกใบกำกับของยอดที่รับเอง";
}

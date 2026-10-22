namespace Accounting.Helpers;

/// <summary>แหล่งของคิวการจองที่ POS ดึงมาเปิดบิล</summary>
public static class PosBookingSource
{
    /// <summary>การจองบริการจากเว็บไซต์ (<c>SiteBooking</c>)</summary>
    public const string CmsBooking = "CmsBooking";
    /// <summary>การจองโต๊ะของ POS (<c>PosReservation</c>)</summary>
    public const string PosReservation = "PosReservation";

    public static bool IsKnown(string? s) => s == CmsBooking || s == PosReservation;
}

/// <summary>สถานะการจองแบบกลาง (สองตารางใช้ enum คนละตัว — ผู้เรียกแปลงก่อนส่งเข้า)</summary>
public enum PosBookingState { Pending = 0, Confirmed = 1, InProgress = 2, Completed = 3, Cancelled = 4, NoShow = 5, Waitlisted = 6 }

/// <summary>ข้อเท็จจริงของการจองหนึ่งรายการที่ตัวตัดสินต้องใช้ — ผู้ดึงข้อมูลกรอง CompanyId แล้ว</summary>
/// <param name="LinkedOpenOrderNumber">เลขบิล POS ที่ผูกอยู่และ<b>ยังไม่ถูกยกเลิก</b> (null = ยังไม่มีบิล)</param>
/// <param name="RevenueAlreadyBilledDocNo">เลขเอกสารรายได้ที่ระบบจองออกไปแล้ว (ใบกำกับ/ใบแจ้งหนี้ที่<b>ไม่ใช่</b>ใบรับมัดจำ) — null = ยังไม่ออก</param>
/// <param name="DepositPaidAmount">มัดจำที่ลูกค้าจ่ายไว้แล้ว (0 = ไม่มี)</param>
public sealed record PosBookingFacts(
    string Source,
    PosBookingState State,
    string? LinkedOpenOrderNumber,
    string? RevenueAlreadyBilledDocNo,
    decimal DepositPaidAmount);

/// <summary>ผลการตัดสิน — <see cref="CanOpen"/> false ⇒ <see cref="BlockReason"/> บอกเหตุ + ทางไปต่อ ·
/// <see cref="Warning"/> = เปิดได้แต่ต้องบอกแคชเชียร์ (เช่น มีมัดจำที่ระบบยังไม่หักให้)</summary>
public sealed record PosBookingDecision(bool CanOpen, string? BlockReason, string? Warning, string StatusLabel);

/// <summary>
/// **ตัวตัดสิน "ดึงคิวการจองมาเปิดบิล POS ได้ไหม"** — ตัวเดียวของทั้งรายการคิว (ป้ายบนจอ) และ endpoint เปิดบิล (ด่านจริง)
///
/// <para>═══ ที่มา (ทีม K · 2026-10-22) ═══ ร้านบริการรับจองทั้งจากเว็บ (<c>SiteBooking</c>) และโต๊ะ/คิวหน้าร้าน
/// (<c>PosReservation</c>) แต่ POS เปิดบิลจากการจองได้แค่ "นั่งโต๊ะ" ซึ่งเติมชื่อลูกค้าแล้ว<b>ไม่ผูก</b>บิลกับการจอง ⇒
/// เปิดซ้ำได้ไม่จำกัด · ระบบจองไม่รู้ว่าลูกค้ามาแล้ว · และการจองที่ระบบจองออกใบกำกับไปแล้วถูกเก็บเงินซ้ำที่ POS</para>
///
/// <para><b>กติกา</b> (เรียงตามลำดับที่ตัดสิน):
/// <list type="number">
/// <item>ยกเลิก / ไม่มาตามนัด / เสร็จสิ้นแล้ว ⇒ เปิดไม่ได้</item>
/// <item>มีบิล POS ที่ผูกอยู่และยังไม่ยกเลิก ⇒ เปิดซ้ำไม่ได้ (ทางไปต่อ: เรียกบิลเดิม) — ด่านเดียวกับ unique index บนฐานข้อมูล</item>
/// <item>ระบบจองออก<b>เอกสารรายได้</b>ไปแล้ว ⇒ เปิดไม่ได้ (เก็บเงินซ้ำ = รายได้/ภาษีขายซ้ำ) — ทางไปต่อ: รับชำระที่เอกสารนั้น</item>
/// <item>มีมัดจำที่จ่ายแล้ว ⇒ เปิดได้ <b>พร้อมคำเตือน</b> — POS ยังไม่หักมัดจำให้อัตโนมัติ (เส้นมัดจำเป็นของ
///   <c>DepositService</c> · ห้าม POS เขียน posting มัดจำเอง) ⇒ แคชเชียร์ต้องรู้ก่อนรับเงินเต็ม</item>
/// </list></para>
/// </summary>
public static class PosBookingIntake
{
    private static string StateLabel(PosBookingState s) => s switch
    {
        PosBookingState.Pending => "รอยืนยัน",
        PosBookingState.Confirmed => "ยืนยันแล้ว",
        PosBookingState.InProgress => "กำลังให้บริการ",
        PosBookingState.Completed => "เสร็จสิ้น",
        PosBookingState.Cancelled => "ยกเลิก",
        PosBookingState.NoShow => "ไม่มาตามนัด",
        PosBookingState.Waitlisted => "คิวสำรอง",
        _ => s.ToString(),
    };

    public static PosBookingDecision Decide(PosBookingFacts f)
    {
        var label = StateLabel(f.State);
        if (!PosBookingSource.IsKnown(f.Source))
            return new PosBookingDecision(false, "ไม่รู้จักแหล่งการจองนี้", null, label);

        switch (f.State)
        {
            case PosBookingState.Cancelled:
            case PosBookingState.NoShow:
                return new PosBookingDecision(false,
                    $"การจองนี้ถูกบันทึกเป็น \"{label}\" แล้ว — ถ้าลูกค้ามาจริง ให้แก้สถานะการจองก่อน หรือเปิดบิลใหม่แบบไม่ผูกการจอง", null, label);
            case PosBookingState.Waitlisted:
                return new PosBookingDecision(false,
                    "การจองนี้อยู่ในคิวสำรอง (รอบเต็ม) — ยืนยันการจองที่หน้าการจองก่อน แล้วค่อยเปิดบิล", null, label);
            case PosBookingState.Completed:
                return new PosBookingDecision(false,
                    "การจองนี้เสร็จสิ้นแล้ว — เปิดบิลซ้ำไม่ได้", null, label);
        }

        if (!string.IsNullOrWhiteSpace(f.LinkedOpenOrderNumber))
            return new PosBookingDecision(false,
                $"การจองนี้เปิดบิลไปแล้ว (#{f.LinkedOpenOrderNumber}) — เรียกบิลเดิมแทนการเปิดใหม่", null, label);

        if (!string.IsNullOrWhiteSpace(f.RevenueAlreadyBilledDocNo))
            return new PosBookingDecision(false,
                $"ระบบจองออกเอกสารรายได้ {f.RevenueAlreadyBilledDocNo} ให้การจองนี้แล้ว — รับชำระที่เอกสารนั้น "
                + "(เปิดบิล POS ซ้ำ = รายได้และภาษีขายซ้ำ)", null, label);

        // ฝ่ายค้านทีม K ข้อ 1 (blocking): POS ยังหักมัดจำในบิลไม่ได้ ⇒ บิลคิดเต็มราคา + ระบบจองรับรู้มัดจำอีกตอนปิดงาน = รายได้ซ้ำ ·
        // เดิม "เปิดได้+เตือน" ⇒ บล็อก พร้อมทางไปต่อ (ปิดงานที่หน้าการจองซึ่งเป็นเส้นที่ตัดมัดจำ / คืนมัดจำก่อน)
        if (f.DepositPaidAmount > 0m)
            return new PosBookingDecision(false,
                $"ลูกค้าชำระมัดจำไว้แล้ว {f.DepositPaidAmount:N2} บาท — POS ยังหักมัดจำในบิลไม่ได้ (เปิดบิลเต็มราคา = รายได้ซ้ำกับมัดจำ) · "
                + "ทางไปต่อ: ปิดงานนี้ที่หน้าการจอง (ระบบตัดมัดจำและเก็บส่วนที่เหลือให้) หรือคืนมัดจำที่หน้า \"มัดจำ\" ก่อนแล้วเปิดบิลที่ POS",
                null, label);

        return new PosBookingDecision(true, null, null, label);
    }

    /// <summary>ฝั่งระบบจอง: ปิดงานแล้วรับรู้มัดจำเป็นรายได้ได้ไหม — การจองที่มี<b>บิล POS ปิดแล้ว</b> (รายได้ถูกรับรู้ที่ POS แล้ว)
    /// ⇒ ห้ามรับรู้มัดจำซ้ำ (มัดจำคงเป็นหนี้สิน ให้คืน/ตัดที่หน้ามัดจำ) · ผู้เรียก: <c>CmsBookingService.UpdateBookingStatusAsync</c></summary>
    public static bool MayRealizeDepositOnComplete(bool hasCompletedPosOrder) => !hasCompletedPosOrder;

    /// <summary>หมายเหตุบนการจองเมื่อข้ามการรับรู้มัดจำเพราะ POS เก็บเงินไปแล้ว</summary>
    public static string SkippedDepositRealizationNote(string? posOrderNumber)
        => $"ไม่รับรู้มัดจำเป็นรายได้ตอนปิดงาน — รายได้ของการจองนี้รับรู้แล้วที่บิล POS #{posOrderNumber} · "
         + "มัดจำยังเป็นหนี้สิน: คืนลูกค้าหรือตัดที่หน้า \"มัดจำ\" ให้ตรงกับเงินที่เก็บจริง";
}

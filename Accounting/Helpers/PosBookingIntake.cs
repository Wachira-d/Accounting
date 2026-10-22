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
public enum PosBookingState { Pending = 0, Confirmed = 1, InProgress = 2, Completed = 3, Cancelled = 4, NoShow = 5 }

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

        string? warning = null;
        if (f.DepositPaidAmount > 0m)
            warning = $"ลูกค้าชำระมัดจำไว้แล้ว {f.DepositPaidAmount:N2} บาท — บิล POS ยังคิดเต็มจำนวนและยังหักมัดจำให้ไม่ได้: "
                + "แจ้งลูกค้าก่อนรับเงิน แล้วคืน/ตัดมัดจำที่หน้า \"มัดจำ\" (ศูนย์มัดจำ) ให้ตรงกับเงินที่เก็บจริง "
                + "หรือปิดงานนี้ที่หน้าการจองแทน (เส้นนั้นตัดมัดจำให้แล้ว)";

        return new PosBookingDecision(true, null, warning, label);
    }
}

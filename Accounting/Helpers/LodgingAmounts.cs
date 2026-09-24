using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ยอดเงินของการจองที่พัก — สูตรอยู่ที่เดียว**
///
/// <para>สูตร <c>Math.Max(0, Total + Folio − Paid)</c> เคยถูกคัดลอกไว้ **3 ที่**
/// (`LodgingService.Lifecycle` · `.Operations` ×2) — ยังตรงกันอยู่วันนี้ แต่เป็น
/// รูปเดียวกับ defect class "สำเนามือที่ drift" ที่เรพนี้เจอซ้ำที่สุด และตอนนี้
/// กำลังจะมีผู้ใช้รายที่สี่คือ **เส้นจ่ายเงินออนไลน์ของแขก** ซึ่งถ้าคำนวณเองแล้ว
/// เพี้ยนแม้สตางค์เดียว = เก็บเงินลูกค้าผิดจำนวน จึงยุบมาที่นี่ก่อนเพิ่มผู้ใช้ใหม่</para>
/// </summary>
public static class LodgingAmounts
{
    /// <summary>ยอดคงเหลือที่แขกยังต้องจ่าย (ไม่ติดลบ)</summary>
    public static decimal BalanceDue(decimal totalAmount, decimal folioTotal, decimal paidAmount)
        => Math.Max(0m, totalAmount + folioTotal - paidAmount);

    /// <summary>
    /// ยอดที่ควรเปิดให้ **แขกจ่ายออนไลน์** ณ สถานะปัจจุบัน — คืน <c>null</c> เมื่อ
    /// สถานะนี้ไม่ควรรับเงินเพิ่ม (ยกเลิก · no-show · เช็คเอาต์แล้ว · ไม่มียอดค้าง)
    ///
    /// <para><b>ยอดต้องมาจากที่นี่เท่านั้น</b> — ห้ามเชื่อตัวเลขที่หน้าเว็บส่งมา
    /// (คลาสเดียวกับ "ค่า default ที่แต่งขึ้น": ถ้าให้ผู้เรียกกำหนดยอดเอง
    /// ใครก็จ่าย 1 บาทแล้วได้ห้อง)</para>
    ///
    /// <para>ระหว่าง <c>Pending</c> เก็บ**เฉพาะมัดจำ**ตามที่ที่พักตั้งไว้ —
    /// ไม่ใช่ยอดเต็ม (ที่พักส่วนใหญ่เก็บ 50% แล้วเก็บที่เหลือตอนเช็คอิน) ·
    /// ที่พักที่ไม่เก็บมัดจำ (<c>DepositRequired = 0</c>) ให้จ่ายยอดค้างทั้งก้อน
    /// เพราะไม่งั้นปุ่มจ่ายจะไม่มีวันโผล่</para>
    /// </summary>
    public static decimal? OnlinePayableAmount(
        LodgingReservationStatus status,
        decimal depositRequired, decimal depositPaid,
        decimal totalAmount, decimal folioTotal, decimal paidAmount)
    {
        if (status is LodgingReservationStatus.Cancelled
            or LodgingReservationStatus.NoShow
            or LodgingReservationStatus.CheckedOut)
            return null;

        var balance = BalanceDue(totalAmount, folioTotal, paidAmount);
        if (balance <= 0.005m) return null;

        if (status == LodgingReservationStatus.Pending)
        {
            var depositLeft = Math.Max(0m, depositRequired - depositPaid);
            // มัดจำค้างอยู่ → เก็บเฉพาะส่วนที่ยังขาด แต่ไม่เกินยอดค้างทั้งหมด
            if (depositLeft > 0.005m) return Math.Min(depositLeft, balance);
        }

        return balance;
    }

    /// <summary>ป้ายสถานะการจองภาษาไทย — ตัวเดียวของหน้าพนักงาน/หน้าแขก/ข้อความ error (C9 รอบ 193 หลังฝ่ายค้าน)
    /// <para>"รอชำระมัดจำ" เป็นจริงเฉพาะเมื่อยังไม่ได้รับมัดจำ — ที่พักปิดยืนยันอัตโนมัติแล้วแขกจ่ายครบ สถานะยังเป็น Pending
    /// แต่ป้ายต้องบอกว่า "ชำระมัดจำแล้ว รอที่พักยืนยัน" (เดิมทั้งสองหน้าบอกให้จ่ายอีก)</para></summary>
    public static string StatusLabel(LodgingReservationStatus status, decimal depositRequired, decimal depositPaid) => status switch
    {
        LodgingReservationStatus.Pending => depositRequired > 0.005m && depositPaid + 0.005m >= depositRequired
            ? "ชำระมัดจำแล้ว รอที่พักยืนยัน" : "รอชำระมัดจำ",
        LodgingReservationStatus.Confirmed => "ยืนยันแล้ว",
        LodgingReservationStatus.CheckedIn => "เช็คอินแล้ว",
        LodgingReservationStatus.CheckedOut => "เช็คเอาต์แล้ว",
        LodgingReservationStatus.Cancelled => "ยกเลิก",
        LodgingReservationStatus.NoShow => "ไม่มาเข้าพัก",
        _ => status.ToString(),
    };

    /// <summary>ข้อความในกล่อง "ชำระออนไลน์" ของหน้าแขก — คู่กับ <see cref="OnlinePayableAmount"/> (ยอดที่ gateway เก็บจริง)
    /// · null = ไม่มีอะไรให้จ่าย · หน้าเว็บแสดงอย่างเดียว ห้ามคิดยอด/ข้อความเอง (F2 ข้อ 5 · C9)</summary>
    public static string? OnlinePaymentNote(
        LodgingReservationStatus status, decimal depositRequired, decimal depositPaid,
        decimal totalAmount, decimal folioTotal, decimal paidAmount, bool autoConfirmOnDeposit)
    {
        if (OnlinePayableAmount(status, depositRequired, depositPaid, totalAmount, folioTotal, paidAmount) is null) return null;
        if (status != LodgingReservationStatus.Pending) return "ชำระยอดคงเหลือของการจอง";
        var depositLeft = depositRequired - depositPaid > 0.005m;
        if (!depositLeft)
            return "ได้รับมัดจำแล้ว — รอที่พักยืนยันการจอง · ชำระยอดคงเหลือได้ตอนนี้หรือที่ที่พัก";
        return autoConfirmOnDeposit
            ? "จ่ายมัดจำแล้วระบบยืนยันการจองอัตโนมัติ ไม่ต้องรอที่พักตรวจสลิป"
            : "จ่ายมัดจำแล้วที่พักจะตรวจสอบและยืนยันการจองให้ (ไม่ได้ยืนยันอัตโนมัติ)";
    }
}

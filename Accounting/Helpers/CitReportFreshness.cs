using System;

namespace Accounting.Helpers;

/// <summary>
/// **"ตัวเลข ภ.ง.ด.50/51 บนหน้าจอยังตรงกับข้อมูลต้นทางไหม"** (pure ไม่มี I/O)
///
/// <para>═══ ที่มา (คำตัดสิน Q12 · DECISION_AUDIT §9.2) ═══ รายงาน CIT ถูก
/// คำนวณ<b>ครั้งเดียวตอนกดสร้าง</b>แล้วเก็บตัวเลขลง <c>TaxReport</c> ·
/// เอกสาร/JE ในรอบบัญชีที่ถูกเพิ่ม/แก้/ยกเลิก<b>หลังจากนั้น</b>ไม่มีผลกับ
/// ตัวเลขที่โชว์ ⇒ นักบัญชีเปิดดูแล้วเชื่อว่าเป็นยอดปัจจุบัน</para>
///
/// <para>═══ ทำไมไม่ regenerate ให้เอง ═══ ตัวเลขบนรายงานที่<b>ยื่นไปแล้ว</b>
/// จะเปลี่ยนเองเงียบ ๆ (ราก R1 ในทรงใหม่) ⇒ กติกาคือ <b>เตือน + เสนอปุ่ม
/// "สร้างใหม่" · ห้ามสร้างใหม่เอง</b></para>
///
/// <para>═══ เกณฑ์ ═══ <b>เทียบยอดที่คำนวณสดกับยอดที่เก็บไว้</b> ไม่ใช่เดาจาก
/// วันที่ — "มีเอกสารถูกแก้หลังสร้างรายงาน" อย่างเดียวไม่พอ เพราะการแก้ที่ไม่
/// กระทบ GL (เช่นแก้หมายเหตุ) ก็ทำให้ <c>UpdatedAt</c> ขยับ ⇒ ถ้าใช้วันที่เป็น
/// เกณฑ์ ผู้ใช้จะเห็นคำเตือนบนรายงานที่ยอดยังตรงเป๊ะ = คำเตือนที่ฟ้องใบถูก
/// (F2 ข้อ 8) · จำนวนรายการที่เปลี่ยนใช้เป็น<b>คำอธิบาย</b>ว่าทำไม ไม่ใช่ตัวตัดสิน</para>
/// </summary>
public static class CitReportFreshness
{
    /// <summary>ยอดจะถือว่า "ต่างกัน" เมื่อห่างเกินค่านี้ (บาท) — 1 สตางค์
    /// เพื่อไม่ให้การปัดเศษของ SQL/decimal กลายเป็นคำเตือน</summary>
    public const decimal AmountTolerance = 0.01m;

    /// <summary>ระดับความสดของรายงาน</summary>
    public enum Level
    {
        /// <summary>ยอดสดตรงกับยอดที่เก็บไว้ — ไม่ต้องทำอะไร</summary>
        UpToDate = 0,
        /// <summary>ยอดยังตรง แต่มีรายการในรอบถูกแก้หลังสร้างรายงาน
        /// (แจ้งให้รู้ ไม่ใช่คำเตือน)</summary>
        SourceTouched = 1,
        /// <summary>ยอดสดไม่ตรงกับที่เก็บไว้ และรายงานยังแก้ได้ ⇒ เสนอให้สร้างใหม่</summary>
        Stale = 2,
        /// <summary>ยอดสดไม่ตรง แต่รายงาน<b>ยื่น/บันทึกว่ายื่นแล้ว</b> ⇒
        /// ห้ามแตะตัวเลขที่ยื่นไปแล้ว ต้องพิจารณายื่นเพิ่มเติม</summary>
        StaleAfterFiling = 3,
    }

    /// <param name="Status">ระดับ</param>
    /// <param name="Message">ประโยคที่โชว์บนรายงาน (null = ไม่ต้องโชว์อะไร)</param>
    /// <param name="CanRegenerate">ปุ่ม "สร้างใหม่" ใช้ได้ไหม</param>
    /// <param name="RevenueDelta">ยอดรายได้สด − ที่เก็บไว้</param>
    /// <param name="ExpenseDelta">ยอดค่าใช้จ่ายสด − ที่เก็บไว้</param>
    public readonly record struct Verdict(
        Level Status, string? Message, bool CanRegenerate,
        decimal RevenueDelta, decimal ExpenseDelta)
    {
        /// <summary>ต้องขึ้นแถบเตือนบนหน้าจอไหม</summary>
        public bool NeedsAttention => Status is Level.Stale or Level.StaleAfterFiling;
    }

    /// <summary>
    /// ตัดสินความสดของรายงาน CIT หนึ่งฉบับ
    /// </summary>
    /// <param name="generatedAt">เวลาที่ตัวเลขชุดนี้ถูกคำนวณ (UTC)</param>
    /// <param name="storedRevenue">รายได้ทั้งรอบที่<b>เก็บไว้</b>ในรายงาน</param>
    /// <param name="freshRevenue">รายได้ทั้งรอบที่คำนวณ<b>สด</b>จาก GL วันนี้</param>
    /// <param name="storedExpense">ค่าใช้จ่ายทั้งรอบที่เก็บไว้</param>
    /// <param name="freshExpense">ค่าใช้จ่ายทั้งรอบที่คำนวณสด</param>
    /// <param name="changedDocuments">จำนวนเอกสารในรอบที่ถูกสร้าง/แก้หลัง <paramref name="generatedAt"/></param>
    /// <param name="changedJournalEntries">จำนวนสมุดรายวันในรอบที่ถูกสร้าง/แก้หลัง <paramref name="generatedAt"/></param>
    /// <param name="alreadyFiled">รายงานถูกยื่น/บันทึกว่ายื่นแล้วหรือยัง
    /// (<c>TaxFilingLockPolicy.DeclaredOrFiled</c>)</param>
    /// <param name="displayTimeZoneOffsetHours">เขตเวลาที่ใช้แสดงวันเวลา (ไทย = +7)</param>
    public static Verdict Evaluate(
        DateTime generatedAt,
        decimal storedRevenue, decimal freshRevenue,
        decimal storedExpense, decimal freshExpense,
        int changedDocuments, int changedJournalEntries,
        bool alreadyFiled,
        int displayTimeZoneOffsetHours = 7)
    {
        var revenueDelta = freshRevenue - storedRevenue;
        var expenseDelta = freshExpense - storedExpense;
        var amountsDiffer = Math.Abs(revenueDelta) > AmountTolerance
                            || Math.Abs(expenseDelta) > AmountTolerance;
        var touched = changedDocuments + changedJournalEntries;
        var stamp = Stamp(generatedAt, displayTimeZoneOffsetHours);

        if (!amountsDiffer)
        {
            if (touched <= 0)
                return new Verdict(Level.UpToDate, null, !alreadyFiled, revenueDelta, expenseDelta);

            // ยอดยังตรง — บอกเฉย ๆ ว่ามีการแก้ ไม่ใช่คำเตือน (ไม่งั้นทุกเดือน
            // ที่มีคนแก้หมายเหตุเอกสารจะกลายเป็นแถบแดงบนรายงานที่ถูกอยู่แล้ว)
            return new Verdict(Level.SourceTouched,
                $"ตัวเลขนี้คำนวณจากข้อมูล ณ {stamp} · มี {touched} รายการในรอบถูกแก้หลังจากนั้น "
                + "แต่ยอดรายได้/ค่าใช้จ่ายรวมยังตรงกับที่คำนวณไว้",
                !alreadyFiled, revenueDelta, expenseDelta);
        }

        var diff = $"ตัวเลขนี้คำนวณจากข้อมูล ณ {stamp} · "
            + (touched > 0 ? $"มี {touched} รายการในรอบเปลี่ยนหลังจากนั้น · " : "")
            + $"คำนวณสดวันนี้ได้รายได้ต่างไป {revenueDelta:N2} บาท "
            + $"และค่าใช้จ่ายต่างไป {expenseDelta:N2} บาท";

        if (alreadyFiled)
            return new Verdict(Level.StaleAfterFiling,
                diff + " — รายงานนี้ยื่น/บันทึกว่ายื่นแล้ว **ห้ามแก้ตัวเลขที่ยื่นไปแล้ว** "
                + "ให้พิจารณายื่นเพิ่มเติม (แบบปรับปรุง) แทน · ถ้าต้องคำนวณใหม่จริง "
                + "ต้องกด \"ปลดล็อก/กลับเป็นร่าง\" ก่อน",
                CanRegenerate: false, revenueDelta, expenseDelta);

        return new Verdict(Level.Stale,
            diff + " — กด \"สร้างใหม่\" เพื่อคำนวณจากข้อมูลปัจจุบัน "
            + "(ระบบไม่คำนวณใหม่ให้เอง เพื่อไม่ให้ตัวเลขบนรายงานเปลี่ยนโดยไม่มีใครสั่ง)",
            CanRegenerate: true, revenueDelta, expenseDelta);
    }

    /// <summary>วันเวลาแบบไทย (พ.ศ. · เวลาท้องถิ่น) สำหรับข้อความที่ผู้ใช้อ่าน</summary>
    private static string Stamp(DateTime utc, int offsetHours)
    {
        var local = utc.AddHours(offsetHours);
        return $"{local:dd/MM/}{local.Year + 543} {local:HH:mm} น.";
    }
}

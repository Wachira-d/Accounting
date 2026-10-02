namespace Accounting.Helpers;

/// <summary>
/// **ซ่อมยอดจ่ายแล้ว/ยอดค้างของใบซื้อบริการต่างประเทศที่บันทึกก่อนคำตัดสินข้อ 131** (รอบ PP36 ทีม F2 · CLAUDE.md F2 หลักการ 9 "Persist แล้วต้องมี migration")
///
/// <para>═══ ที่มา ═══ ก่อนรอบนี้ทุกเส้นตั้ง <c>PaidAmount</c>/<c>BalanceDue</c> จาก <c>TotalAmount</c> ซึ่งของใบ §83/6 รวม VAT ที่<b>ผู้จ่ายประเมินเอง</b>
/// (PV-20260901-0001: 6,321.56 ทั้งที่จ่าย Booking.com 5,908) ⇒ ใบตั้งหนี้ค้างปลอม 413.56 ในอายุเจ้าหนี้ · ใบสำคัญจ่ายเงินสด "จ่ายแล้ว" เกินเงินที่ออกจริง ·
/// แก้โค้ดอย่างเดียวไม่พอเพราะค่าเสียถูกเก็บไว้แล้ว</para>
///
/// <para>═══ แตะเฉพาะแถวที่ "อยู่ในสภาพเดิมแน่นอน" (สูตรเดิมทุกตัวอักษร) ═══
/// <list type="number">
/// <item><b>ใบสำคัญจ่ายเงินสดที่ไม่อ้างใบต้นทาง</b>: <c>PaidAmount = TotalAmount</c> และ <c>BalanceDue = 0</c> ⇒ <c>PaidAmount</c> = ยอดจ่ายผู้รับเงิน ·
/// JE ของใบแบบนี้แยก 21912 มาตลอด (Cr ธนาคาร = ฐาน) ⇒ แก้แค่ตัวเลขระดับเอกสารให้ตรง GL</item>
/// <item><b>ใบตั้งหนี้ (ใบแจ้งหนี้ซื้อ/ค่าใช้จ่าย/ใบสำคัญจ่ายเครดิตเก่าที่ไม่อ้างใบต้นทาง)</b>: <c>BalanceDue = TotalAmount − PaidAmount</c> (สูตรเดิม)
/// และ<b>จ่ายไปแล้วไม่เกินยอดจ่ายผู้รับเงิน</b> ⇒ <c>BalanceDue</c> = ยอดจ่ายผู้รับเงิน − ที่จ่ายแล้ว · ปิดครบ ⇒ สถานะ "ชำระแล้ว" (เฉพาะสถานะที่ตามเงินได้)</item>
/// </list>
/// ใบที่<b>จ่ายเกินยอดจ่ายผู้รับเงินไปแล้ว</b> (เงินออกจริงรวม VAT ประเมินเอง ⇒ GL เจ้าหนี้ติดเดบิต/ธนาคารออกเกิน) และ<b>ใบสำคัญจ่ายที่ปิดหนี้ใบต้นทาง</b>
/// <b>ไม่ถูกแตะ</b> — ต้องให้คนซ่อมด้วยใบสำคัญทั่วไป ⇒ <see cref="OverpaidReportSql"/> นับให้ log ทุกบูตจนกว่าจะหมด</para>
///
/// <para>รันทุกบูตได้ (idempotent): หลังแก้ แถวไม่ตรงเงื่อนไขสูตรเดิมอีก (VAT &gt; 0 ⇒ ยอดใหม่ ≠ ยอดเดิม) · แถวที่โค้ดใหม่เขียนก็ไม่ตรงด้วยเหตุผลเดียวกัน ·
/// งานทั้งฐานแต่จำกัดเฉพาะแถวที่เข้าเงื่อนไขแน่นอน (ไม่มีค่าใดขึ้นกับบริษัท — สูตรเดียวกับ <see cref="ForeignServiceVat.PayeeAmountQuery"/>)</para>
/// </summary>
public static class ForeignServicePayeeBalanceMigration
{
    // ชนิด/สถานะ/ประเภทการชำระในคำสั่งเป็นเลข enum ตามที่เก็บในฐาน (PI 8 · Expense 9 · PV 13 · Approved 2 · Sent 3 · PartiallyPaid 4 · Paid 5 ·
    // Voided 6 · Overdue 7 · Cash 1) — เทสต์ล็อกว่าตรงกับ enum จริง (ForeignServicePayeeBalanceMigrationTests)

    /// <summary>ขอบเขตร่วม: ติ๊กบริการต่างประเทศ · มี VAT · ไม่ถูกลบ · ไม่ยกเลิก</summary>
    private const string Scope =
        "\"IsForeignService\" = true AND \"VatAmount\" > 0 AND \"IsDeleted\" = false AND \"Status\" <> 6";

    /// <summary>(1) ใบสำคัญจ่ายเงินสดตั้งต้น — <c>PaidAmount</c> ที่เท่ายอดรวม VAT ⇒ ยอดจ่ายผู้รับเงิน</summary>
    public static string CashVoucherSql =>
        "UPDATE \"Documents\" SET \"PaidAmount\" = \"TotalAmount\" - \"VatAmount\" "
        + $"WHERE {Scope} AND \"DocumentType\" = 13 AND \"RelatedDocumentId\" IS NULL AND \"PaymentType\" = 1 "
        + "AND \"PaidAmount\" = \"TotalAmount\" AND \"BalanceDue\" = 0;";

    /// <summary>(2) ใบตั้งหนี้ — ยอดค้างตามสูตรเดิม + ยังจ่ายไม่เกินยอดจ่ายผู้รับเงิน ⇒ ยอดค้างใหม่ (+ สถานะชำระแล้วเมื่อปิดครบ)</summary>
    public static string PayableSql =>
        "UPDATE \"Documents\" SET "
        + "\"Status\" = CASE WHEN \"TotalAmount\" - \"VatAmount\" - \"PaidAmount\" <= 0.005 AND \"PaidAmount\" > 0.005 "
        + "AND \"Status\" IN (2, 3, 4, 7) THEN 5 ELSE \"Status\" END, "
        + "\"BalanceDue\" = GREATEST(\"TotalAmount\" - \"VatAmount\" - \"PaidAmount\", 0) "
        + $"WHERE {Scope} "
        + "AND (\"DocumentType\" IN (8, 9) OR (\"DocumentType\" = 13 AND \"RelatedDocumentId\" IS NULL AND \"PaymentType\" IS DISTINCT FROM 1)) "
        + "AND ABS(\"BalanceDue\" - (\"TotalAmount\" - \"PaidAmount\")) <= 0.005 "
        + "AND \"PaidAmount\" <= \"TotalAmount\" - \"VatAmount\" + 0.005;";

    /// <summary>ทุกคำสั่ง (ลำดับไม่มีผลต่อกัน — สองชุดแถวไม่ทับกัน)</summary>
    public static IReadOnlyList<string> Statements() => new[] { CashVoucherSql, PayableSql };

    /// <summary>
    /// นับใบที่ต้องให้คนซ่อม: ใบที่<b>จ่ายแล้วเกินยอดจ่ายผู้รับเงิน</b> — ใบตั้งหนี้ที่จ่าย/ตัดเจ้าหนี้รวม VAT ประเมินเองไปแล้ว ·
    /// ใบสำคัญจ่ายเงินสดที่ปิดหนี้ใบต้นทาง (เงินออกเดิมรวม VAT) — คืนคอลัมน์ "Value" (จำนวน) สำหรับ <c>SqlQueryRaw&lt;int&gt;</c>
    /// </summary>
    public static string OverpaidReportSql =>
        "SELECT COUNT(*)::int AS \"Value\" FROM \"Documents\" "
        + $"WHERE {Scope} AND \"PaidAmount\" > \"TotalAmount\" - \"VatAmount\" + 0.005 "
        + "AND \"DocumentType\" IN (8, 9, 13)";
}

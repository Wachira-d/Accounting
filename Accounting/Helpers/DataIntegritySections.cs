using System.Text.Json;
using Accounting.Services.Implementations;

namespace Accounting.Helpers;

/// <summary>หัวข้อหนึ่งหมวดของรายงานตรวจข้อมูล · <c>Key</c> = ชื่อ property ของ <see cref="DataIntegrityReport"/> ในรูป JSON (camelCase)</summary>
public sealed record DataIntegritySectionInfo(string Key, string Title, string Note);

/// <summary>
/// **ตัวตั้งเดียวของหมวดในรายงานตรวจข้อมูลจากบั๊กที่แก้แล้ว** — ชื่อหมวด + คำอธิบายทางไปต่อ
/// (2026-10-15 ทีม X5 · เดิมอยู่ใน JS ของ data-integrity.html เท่านั้น ⇒ ไฟล์ส่งออกต้องมีสำเนาที่สอง — F2 ข้อ 4/5).
/// หน้าอ่านจาก <see cref="DataIntegrityReport.Sections"/> · ไฟล์ส่งออกอ่านจาก <see cref="Rows"/> ·
/// เทสต์ล็อกว่า "ทุก property ที่เป็นรายการของ <see cref="DataIntegrityReport"/> มีหมวดที่นี่" — เพิ่มหมวดใหม่แล้วลืมที่นี่ = เทสต์แดง
/// </summary>
public static class DataIntegritySections
{
    private static string K(string propertyName) => JsonNamingPolicy.CamelCase.ConvertName(propertyName);

    private static readonly (DataIntegritySectionInfo Info, Func<DataIntegrityReport, List<DataIntegritySuspect>> Rows)[] Defs =
    {
        (new(K(nameof(DataIntegrityReport.PosOrderTotals)), "บิล POS ที่ยอดบิลไม่เท่าผลรวมรายการ",
            "เดิมรายการถูกนับสองครั้งตอนเพิ่มรายการ ⇒ ยอดบิล/VAT อาจเบิ้ล — ตรวจกับเงินที่รับจริงและรายการบัญชีของกะ"),
            r => r.PosOrderTotals),
        (new(K(nameof(DataIntegrityReport.RollupBillingNoteChildren)), "เอกสารที่แปลงจากใบวางบิลรวมใบแจ้งหนี้",
            "ใบเสร็จจากใบวางบิลลงแบบขายสด ⇒ รายได้ซ้ำกับใบแจ้งหนี้ในใบวางบิล และลูกหนี้ใบเดิมไม่ถูกตัด"),
            r => r.RollupBillingNoteChildren),
        (new(K(nameof(DataIntegrityReport.PurchaseInvoicesBilledFromPoWithGrn)), "ใบแจ้งหนี้ซื้อที่ผูกใบสั่งซื้อตรงทั้งที่มีใบรับสินค้า",
            "อาจรับสต็อกเข้าสองรอบ (21240 ค้าง) — ใบสั่งซื้อที่ผสมบริการ/สินค้าอาจถูกต้อง ตรวจรายตัว"),
            r => r.PurchaseInvoicesBilledFromPoWithGrn),
        (new(K(nameof(DataIntegrityReport.PayrollRunTotals)), "รอบเงินเดือนที่ยอดรวมไม่เท่าผลรวมแถว",
            "เดิมเพิ่มพนักงานเข้ารอบแล้วยอดรวมเบิ้ล — รอบที่ยังไม่จ่ายกด \"คำนวณใหม่\" · รอบที่จ่ายแล้วให้ผู้ทำบัญชีตรวจรายการบัญชี"),
            r => r.PayrollRunTotals),
        // รอบ 203 — คำถามเจ้าของ 2026-10-09: ใบเสร็จค่าห้องพักที่พิมพ์ VAT แต่หัว "ใบเสร็จรับเงิน" · เลขออกแล้วแก้ย้อนหลังไม่ได้ (§86/4)
        (new(K(nameof(DataIntegrityReport.PlainReceiptsWithVat)), "ใบเสร็จของผู้จด VAT ที่พิมพ์ VAT แต่หัวไม่ใช่ใบกำกับภาษี",
            "ผู้จด VAT ต้องออกใบกำกับ (เต็มรูป/อย่างย่อ) ทุกครั้งที่รับเงินค่าบริการ (ม.86) — ใบเสร็จเปล่าที่พิมพ์ VAT ไม่มีสถานะทางกฎหมาย · เติมที่อยู่ผู้ซื้อ หรือติ๊ก \"ประกอบกิจการขายปลีก §86/6\" ในข้อมูลบริษัท แล้ว \"ยกเลิกและออกใบแทน\" รายใบ · ใบใหม่มีคำเตือนตอนอนุมัติ (รับทราบได้ — ไม่บล็อก) แล้วมาลิสต์ที่นี่"),
            r => r.PlainReceiptsWithVat),
        // รอบ 204 — คำตัดสินเจ้าของข้อ 148: แบบ ภ.ง.ด.2 · ข้อมูลก่อนหน้านี้ลง ภ.ง.ด.3 ทั้งหมด · ใบที่ยื่นแล้วห้ามแก้ย้อนหลัง
        (new(K(nameof(DataIntegrityReport.Section40_4RecordedAsPnd3)), "ดอกเบี้ย/เงินปันผลจ่ายบุคคลธรรมดาที่บันทึกเป็น ภ.ง.ด.3",
            "ม.50(2) · ม.59: เงินได้ ม.40(4) จ่ายบุคคลธรรมดาต้องยื่น ภ.ง.ด.2 — ระบบเพิ่งรองรับแบบนี้ (รอบ 204) · 50 ทวิ/เอกสารเดิมไม่ถูกย้ายแบบให้อัตโนมัติ (ยื่นไปแล้วแก้ย้อนหลังไม่ได้) · ผู้ทำบัญชีตัดสินรายใบ: ยื่นเพิ่มเติม · ยกเลิกแล้วออกใบแทน · โอนยอดผัง 21916 → 21915 · ใบที่ปนดอกเบี้ยกับค่าบริการให้แยกเป็นสองใบ"),
            r => r.Section40_4RecordedAsPnd3),
        (new(K(nameof(DataIntegrityReport.TipWhtPostedToPnd2Account)), "จ่ายทิปที่ลงภาษีหัก ณ ที่จ่ายไว้ผัง ภ.ง.ด.2 (21915)",
            "ทิป = ม.40(2) ยื่น ภ.ง.ด.3 (50 ทวิ ของทิปเป็น ภ.ง.ด.3 อยู่แล้ว) แต่รายการบัญชีเดิมลง 21915 — โอนยอดไป 21916 ก่อนนำส่ง ภ.ง.ด.3 ของงวดนั้น · การจ่ายทิปครั้งใหม่ลง 21916 แล้ว"),
            r => r.TipWhtPostedToPnd2Account),
        // ทีม E 2026-10-13 — บรรทัดจากสแกนที่ติดส่วนลด % ซ้อนบนราคาที่ลดแล้ว · ปุ่ม "แก้" รายใบ (เซิร์ฟเวอร์ส่ง fixPath มาเฉพาะแถวที่แก้ได้)
        (new(K(nameof(DataIntegrityReport.OcrLineDiscountWouldChangeOnResave)), "เอกสารจากสแกนที่เปิดแก้แล้วบันทึก ยอดจะเปลี่ยนเอง",
            "บรรทัดที่ไม่มีราคาต่อหน่วยบนกระดาษเคยถูกเก็บเป็น \"ราคา = ยอดหลังลด\" พร้อมส่วนลด % ของทั้งใบ ⇒ เปิดแก้แล้วบันทึก ระบบหัก % ซ้ำ · ยอด/VAT ที่บันทึกไว้ถูกตามกระดาษแล้ว (คอลัมน์ \"ค่าที่เก็บไว้\") — คอลัมน์ขวาคือยอดที่จะกลายเป็นถ้าบันทึกซ้ำ · กด \"แก้\" เพื่อตั้งส่วนลด % = 0 โดยยอด/VAT/ยอดรวมไม่เปลี่ยน (ระบบจำลองก่อน ถ้าเงินจะขยับจะไม่แก้และบอกเหตุผล)"),
            r => r.OcrLineDiscountWouldChangeOnResave),
        // DECISIONS ข้อ 111 — อ่านอย่างเดียว ระบบไม่ยกเลิก 50 ทวิ ให้ (อาจยื่นไปแล้ว)
        (new(K(nameof(DataIntegrityReport.VoidedPayrollRunsWithIssuedWhtCerts)), "รอบเงินเดือนที่ยกเลิกแล้วแต่ยังมี 50 ทวิ สถานะออกแล้ว",
            "รอบที่ยกเลิกก่อนระบบยกเลิก 50 ทวิ ตามรอบ — ระบบไม่ยกเลิกให้เพราะใบอาจยื่น/ส่งผู้รับไปแล้ว · ผู้ทำบัญชีตัดสินรายใบ: ยังไม่ยื่น ⇒ ยกเลิกใบที่หน้า 50 ทวิ · ยื่นแล้ว ⇒ ยื่นแบบเพิ่มเติมลดยอด"),
            r => r.VoidedPayrollRunsWithIssuedWhtCerts),
    };

    /// <summary>หัวข้อทุกหมวดตามลำดับที่แสดง</summary>
    public static IReadOnlyList<DataIntegritySectionInfo> All { get; } = Defs.Select(d => d.Info).ToList();

    /// <summary>ชื่อ property (C#) ที่มีหมวดแล้ว — ให้เทสต์เทียบกับ property ทั้งหมดของรายงาน</summary>
    public static IReadOnlyList<string> CoveredKeys => All.Select(i => i.Key).ToList();

    /// <summary>ทุกแถวของรายงานพร้อมหมวด (ลำดับหมวด → ลำดับเดิมในหมวด) — ไฟล์ส่งออกใช้ตัวนี้</summary>
    public static IEnumerable<(DataIntegritySectionInfo Section, DataIntegritySuspect Row)> Rows(DataIntegrityReport report)
    {
        foreach (var (info, rows) in Defs)
            foreach (var r in rows(report) ?? new List<DataIntegritySuspect>())
                yield return (info, r);
    }

    /// <summary>จำนวนแถวต่อหมวด (ลำดับเดียวกับ <see cref="All"/>)</summary>
    public static IReadOnlyList<(DataIntegritySectionInfo Section, int Count)> Counts(DataIntegrityReport report)
        => Defs.Select(d => (d.Info, (d.Rows(report) ?? new List<DataIntegritySuspect>()).Count)).ToList();
}

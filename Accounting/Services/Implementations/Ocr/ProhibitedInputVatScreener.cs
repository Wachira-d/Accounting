namespace Accounting.Services.Implementations.Ocr;

/// <summary>ผลการคัดกรองภาษีซื้อต้องห้าม (§82/5) จากเนื้อหาเอกสาร</summary>
/// <param name="Claimable">false = ต้องห้ามแน่ (default ไม่เคลม) · null = ไม่เข้าข่าย/ตัดสินไม่ได้</param>
/// <param name="RuleCode">อ้างมาตรา เช่น "RD-82/5(6)" — ลง audit ตามกฎ M</param>
/// <param name="Warning">ข้อความอธิบายถึงผู้ใช้ (แสดงเป็น banner + วิธีเคลมได้เมื่อไร)</param>
/// <param name="Vehicle">ผล §82/5(6) ละเอียด (ชื่อ enum ส่งให้หน้าเว็บตัดสินการแสดงผลได้โดยไม่ต้องมีลิสต์คำเอง · C-4)</param>
internal readonly record struct ProhibitedVatVerdict(
    bool? Claimable, string? RuleCode, string? Warning,
    Accounting.Helpers.VehicleVatVerdict Vehicle = Accounting.Helpers.VehicleVatVerdict.NotVehicleCost);

/// <summary>
/// คัดกรอง "ภาษีซื้อต้องห้าม" ตามชนิดรายจ่าย (§82/5(4)/(6)) จากข้อความบนเอกสาร —
/// ชั้นที่ขาดของ OCR pipeline: <see cref="OcrDocumentRoleInferrer"/> ตรวจได้เฉพาะ
/// "รูปแบบใบ" (ใบย่อ §82/5(2) / ไม่ใช่ใบกำกับเต็มรูป §82/5(1)) แต่ใบกำกับ
/// เต็มรูปที่ถูกต้อง 100% ของ**ค่าน้ำมันรถเก๋ง/ค่ารับรอง**ก็เคลมไม่ได้อยู่ดี —
/// เดิมระบบ default เคลมให้ทุกใบที่รูปแบบถูก (ผู้ใช้ทัก)
///
/// <para>หลักกฎหมาย:
/// · §82/5(4) ค่ารับรอง (รวม §65 ตรี(4)) — ต้องห้ามเสมอ ไม่มีข้อยกเว้น
/// · §82/5(6) + ประกาศอธิบดีฯ ฉบับที่ 42 — รถยนต์นั่ง ≤ 10 ที่นั่ง (เก๋ง,
///   กระบะ 4 ประตูตามพิกัดสรรพสามิต) และค่าน้ำมัน/ซ่อม/เช่าของรถพวกนี้ ต้องห้าม
///   — แต่กระบะตอนเดียว/แค็บ, รถบรรทุก, รถตู้ &gt; 10 ที่นั่ง, เครื่องจักร เคลมได้</para>
///
/// <para>นโยบายการเดา (สำคัญ): ค่าน้ำมัน/เช่ารถที่**ไม่รู้ชนิดรถ** → default
/// "ไม่เคลม" + บอกวิธีกลับมาเคลม — เพราะสองทางผิดไม่เท่ากัน: default เคลมแล้ว
/// ผิด = ยื่น ภ.พ.30 เกินสิทธิ์ (โดนประเมิน + เบี้ยปรับ) ส่วน default ไม่เคลม
/// แล้วผิด = เสียสิทธิ์ที่ติ๊กคืนได้ในกรอบ 6 เดือน §82/3. เจอ keyword รถ
/// ประเภทเคลมได้ (กระบะ/บรรทุก/ทะเบียนรถบรรทุก) → ปล่อยเคลม + เตือนเฉย ๆ</para>
/// </summary>
internal static class ProhibitedInputVatScreener
{
    // ── §82/5(4) ค่ารับรอง — ต้องห้ามเสมอ ──
    private static readonly string[] EntertainmentKeywords =
    {
        "ค่ารับรอง", "ค่าเลี้ยงรับรอง", "เลี้ยงรับรองลูกค้า", "entertainment",
        "ของขวัญลูกค้า", "กระเช้าของขวัญ", "ของกำนัล",
    };

    // §82/5(6) รถ — ลิสต์คำ + ตัวตัดสินย้ายไป Helpers/InputVatVehicleRule (รอบ 193 · S-05)
    // เพื่อให้มีลิสต์ชุดเดียวทั้งระบบ และส่งธง IsVehicleDealer เข้ามาได้

    /// <summary>คัดกรองจากข้อความเอกสาร (normalize แล้ว) + ชื่อผู้ขาย +
    /// คำอธิบายรายการ — คืน verdict แรกที่เข้าข่าย (รับรอง > รถ)
    ///
    /// <para>⚠️ <b>ขอบเขตของ haystack สำคัญพอ ๆ กับตัวคำ</b> (ผลตรวจ 2026-09-06 · T1-13):
    /// คำที่บ่งชี้ <b>ผู้ขาย</b> (ปั๊มน้ำมัน) ต้องเทียบกับ<b>ชื่อผู้ขาย</b>เท่านั้น —
    /// เดิมเทียบกับข้อความทั้งหน้า ⇒ คำว่า "Shell"/"PTT"/"pure" ที่โผล่ในโฆษณาท้ายใบ
    /// ที่อยู่ หรือ<b>ชื่อสินค้า</b> ("PURE LIFE" น้ำดื่ม) ทำให้ใบนั้นถูกปิดเคลม VAT
    /// ทั้งใบ = เสียสิทธิ์จริง ไม่ใช่แค่คำเตือน</para>
    ///
    /// <para><paramref name="isVehicleDealer"/> = <c>CompanySettings.IsVehicleDealer</c> ของบริษัทผู้ซื้อ —
    /// <b>ต้องส่งทุกครั้ง</b> (เดิมไม่มีพารามิเตอร์นี้ ⇒ อู่/ผู้ขายรถถูกตั้ง "ไม่เคลม" ทุกใบ · S-05)</para></summary>
    internal static ProhibitedVatVerdict Screen(
        string? rawText, string? vendorName, IEnumerable<string?> lineDescriptions, bool isVehicleDealer)
    {
        var lines = string.Join("\n", lineDescriptions.Where(d => d != null));
        var hay = ((rawText ?? "") + "\n" + (vendorName ?? "") + "\n" + lines);
        if (string.IsNullOrWhiteSpace(hay)) return new(null, null, null);

        // §82/5(4) — ต้องห้ามเสมอ ไม่ต้องถามชนิดอะไรต่อ (ธง dealer ไม่เกี่ยว)
        if (Accounting.Helpers.InputVatVehicleRule.ContainsAny(hay, EntertainmentKeywords))
            return new(false, "RD-82/5(4)",
                "ค่ารับรอง/ของขวัญลูกค้า — ภาษีซื้อต้องห้ามตาม §82/5(4) เสมอ "
                + "(และรายจ่ายถูกจำกัดตาม §65 ตรี(4)) · VAT จะถูกรวมเป็นค่าใช้จ่าย");

        // §82/5(6) — น้ำมัน/ซ่อม/เช่ารถ ตัดสินตามชนิดรถ + ธงผู้ประกอบกิจการขาย/ให้เช่ารถ
        var vehicle = Accounting.Helpers.InputVatVehicleRule.Judge(hay, vendorName, isVehicleDealer);
        var warning = Accounting.Helpers.InputVatVehicleRule.Warning(vehicle);
        if (warning == null) return new(null, null, null);
        return new(Accounting.Helpers.InputVatVehicleRule.DisablesClaim(vehicle) ? false : null,
            Accounting.Helpers.InputVatVehicleRule.RuleCode, warning, vehicle);
    }
}

namespace Accounting.Helpers;

/// <summary>แถวหนึ่งของคลัง "ค่าที่รู้ว่าถูก ต่อผู้ขาย" (<c>VendorKnownGoodValues</c>) ในรูปที่ตัวตัดสินใช้</summary>
public readonly record struct OcrKnownGoodRow(string FieldName, string Value, int ConfirmedCount, string Source);

/// <summary>ผลการตัดสิน — <c>Value = null</c> แปลว่า "ไม่เติม" (เหตุผลอยู่ใน <c>Reason</c>)</summary>
public readonly record struct OcrKnownGoodFillDecision(string? Value, decimal Confidence, string Reason);

/// <summary>
/// **เติม "ที่อยู่ผู้ขาย" ที่ engine อ่านไม่ได้ จากคลังที่ Azure/ผู้ใช้สอนไว้ — ตัวตัดสินตัวเดียว**
///
/// <para>═══ ที่มา (รอบ 190 · เจ้าของข้อ 11: "ยิ่ง Azure ทำงานเยอะ local ยิ่งเก่งขึ้นไหม") ═══
/// <c>AzureDiPatternLearner</c> เขียนที่อยู่ผู้ขายที่ Azure อ่านได้ลงคลังทุกใบ แต่ฝั่งอ่าน
/// (<c>VendorKnownGoodCorrector.ApplyAsync</c>) ใช้คลังนี้แค่ "<b>แทน</b>ค่าที่ engine อ่านเพี้ยน
/// ≥ 0.80" — ถ้า engine ในเครื่อง<b>ไม่ได้ที่อยู่มาเลย</b> (python ไม่มีช่องนี้ · Tesseract หาป้าย
/// "ที่อยู่" ในหัวร้าน POS ไม่เจอ) ค่าที่ Azure สอนไว้<b>ไม่เคยถูกใช้</b> = จ่ายค่า Azure แล้ว local
/// ไม่ได้อะไรกลับมาในช่องนี้ (หลักการ "มี ≠ ถูกเรียก")</para>
///
/// <para>═══ ด่านกันคลังเอียง (DECISION_DOCTRINE §3.1) ═══
/// <list type="number">
/// <item><b>ต้องมีคนยืนยัน หรือเห็นซ้ำ</b>: แถว <c>UserCorrection</c> หรือ Azure อ่านได้ค่าเดิม
///   ≥ <see cref="MinAzureConfirmations"/> ใบ — Azure ใบเดียวที่ยังไม่มีใครยืนยันห้ามกลายเป็นค่าที่เติมเอง</item>
/// <item><b>ต้องมีค่าเดียว</b>: ผู้ขายที่มีที่อยู่ที่เชื่อได้มากกว่าหนึ่งค่า (หลายสาขา/ย้ายที่) = ไม่รู้ว่าใบนี้ของที่ไหน ⇒ ไม่เติม</item>
/// <item><b>สาขาต้องไม่ขัด</b>: ผู้ขายที่เคยเห็นหลายรหัสสาขา หรือใบนี้เป็นสาขาอื่นจากที่เคยเห็น ⇒ ไม่เติม
///   (ที่อยู่สำนักงานใหญ่บนใบของสาขาที่ 8 = ผิด §86/4)</item>
/// <item><b>ห้ามเป็นที่อยู่ของเรา / ของผู้ซื้อในใบนี้</b>: คลังเก่าอาจมีที่อยู่ของเราปนมา (ตัวอ่านที่อยู่ผู้ขาย
///   เดิมหยิบป้าย "ที่อยู่" ตัวแรกของหน้า ซึ่งบนใบ POS คือบล็อกลูกค้า) — กันที่ฝั่งอ่านแทนการ migrate</item>
/// </list>
/// ค่าที่เติมได้คะแนน <see cref="FillConfidence"/> (&lt; 0.85 ⇒ ไฮไลต์เหลือง) และที่มา = ประวัติผู้ขาย</para>
/// </summary>
public static class OcrKnownGoodAddressFill
{
    public const string AddressField = "VendorAddress";
    public const string BranchField = "VendorBranchCode";

    /// <summary>Azure ต้องอ่านได้ค่าเดิมกี่ใบก่อนจะนับว่า "เห็นซ้ำ" (ไม่มีคนยืนยัน)</summary>
    public const int MinAzureConfirmations = 2;

    /// <summary>ความมั่นใจของค่าที่เติมจากประวัติ — ต่ำกว่า 0.85 ตั้งใจ</summary>
    public const decimal FillConfidence = 0.70m;

    /// <param name="currentAddress">ที่อยู่ผู้ขายที่ engine ให้มา (มีค่าแล้ว = ไม่ยุ่ง)</param>
    /// <param name="scanBranchCode">รหัสสาขาผู้ขายที่อ่านได้จากใบนี้ (ถ้ามี)</param>
    /// <param name="rows">แถวในคลังของผู้ขายรายนี้ (ทุกช่อง)</param>
    /// <param name="ourAddress">ที่อยู่จดทะเบียนของบริษัทเรา</param>
    /// <param name="buyerAddress">ที่อยู่ผู้ซื้อในใบนี้ (ถ้าอ่านได้)</param>
    public static OcrKnownGoodFillDecision Decide(
        string? currentAddress, string? scanBranchCode, IReadOnlyList<OcrKnownGoodRow> rows,
        string? ourAddress = null, string? buyerAddress = null)
    {
        if (!string.IsNullOrWhiteSpace(currentAddress))
            return new(null, 0m, "มีที่อยู่ผู้ขายจากใบนี้แล้ว — ไม่เติมจากประวัติ");
        rows ??= Array.Empty<OcrKnownGoodRow>();

        var trusted = rows
            .Where(r => r.FieldName == AddressField && !string.IsNullOrWhiteSpace(r.Value))
            .Where(r => r.Source == "UserCorrection" || r.ConfirmedCount >= MinAzureConfirmations)
            .ToList();
        if (trusted.Count == 0)
            return new(null, 0m, "ยังไม่มีที่อยู่ผู้ขายที่ผู้ใช้ยืนยัน หรือ Azure อ่านได้ซ้ำ ≥ "
                + MinAzureConfirmations + " ใบ");

        // UserCorrection ชนะ Azure — ถ้ามีคำแก้ของผู้ใช้ ใช้เฉพาะชุดนั้น
        var pool = trusted.Any(r => r.Source == "UserCorrection")
            ? trusted.Where(r => r.Source == "UserCorrection").ToList()
            : trusted;
        var distinct = pool.Select(r => r.Value.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count != 1)
            return new(null, 0m, $"ผู้ขายรายนี้มีที่อยู่ที่เชื่อได้ {distinct.Count} แห่ง — ไม่รู้ว่าใบนี้ของที่ไหน");

        var branches = rows
            .Where(r => r.FieldName == BranchField && !string.IsNullOrWhiteSpace(r.Value))
            .Select(r => r.Value.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var scanBranch = scanBranchCode?.Trim();
        if (branches.Count > 1)
            return new(null, 0m, $"ผู้ขายรายนี้เคยออกใบจาก {branches.Count} สาขา — ที่อยู่ต่างกันตามสาขา ไม่เติม");
        if (!string.IsNullOrEmpty(scanBranch) && branches.Count == 1 && branches[0] != scanBranch)
            return new(null, 0m, $"ใบนี้เป็นสาขา {scanBranch} แต่ที่อยู่ในประวัติเป็นของสาขา {branches[0]} — ไม่เติม");

        var value = distinct[0];
        if (OcrPartyResolver.AddressLooksLikeOurs(value, ourAddress))
            return new(null, 0m, "ที่อยู่ในประวัติเป็นเลขที่บ้านของบริษัทเรา (คลังปนที่อยู่ผู้ซื้อ) — ไม่เติม");
        if (OcrBuyerAddressReader.SameAddress(value, buyerAddress))
            return new(null, 0m, "ที่อยู่ในประวัติตรงกับที่อยู่ผู้ซื้อในใบนี้ — ไม่เติม");

        var evidence = pool.Any(r => r.Source == "UserCorrection")
            ? "ผู้ใช้เคยยืนยัน"
            : $"Azure อ่านได้ค่าเดียวกัน {pool.Max(r => r.ConfirmedCount)} ใบ";
        return new(value, FillConfidence,
            $"เติมที่อยู่ผู้ขายจากประวัติของเลขผู้เสียภาษีนี้ ({evidence}) — ตรวจว่าเป็นสาขาเดียวกับใบนี้");
    }
}

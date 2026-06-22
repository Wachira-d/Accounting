namespace Accounting.Services.Implementations.Tax;

/// <summary>
/// จำแนกผังบัญชีสินทรัพย์ → ต้องลงทะเบียนสินทรัพย์ถาวร (FixedAsset register)
/// ไหม + ค่า default สำหรับ auto-registration (อายุใช้งาน, วิธีคิดค่าเสื่อม,
/// ผังค่าเสื่อมสะสม/ค่าเสื่อม). อ้างอิงผังมาตรฐานไทย (ChartOfAccountTemplates):
///
///   12210 อุปกรณ์สำนักงาน    → accum 18310 / dep 56110 / 5 ปี
///   12220 คอมพิวเตอร์         → accum 18320 / dep 56120 / 3 ปี
///   12230 เครื่องตกแต่ง       → accum 18330 / dep 56130 / 5 ปี
///   12240 ยานพาหนะ          → accum 18340 / dep 56140 / 5 ปี
///   12250 สินทรัพย์เช่า (ROU) → accum 18350 / dep 56150 / ตามสัญญา
///   12260 เครื่องจักร         → accum 18360 / dep 56160 / 10 ปี
///   12270 อาคาร             → accum 18370 / dep 56170 / 20 ปี
///   12280 งานระหว่างก่อสร้าง  → register แต่ยังไม่คิดค่าเสื่อม (None)
///   12290 ที่ดิน             → register, ไม่คิดค่าเสื่อม (None — TFRS/§65)
///   12310 ซอฟต์แวร์          → accum (impair 18410) / amort 56210 / 5 ปี
///
/// Land/CIP ลงทะเบียนเพื่อติดตามทรัพย์สิน แต่ DepreciationMethod=None.
/// </summary>
public static class FixedAssetAccountClassifier
{
    public sealed record AssetClass(
        string AssetAccountCode,
        string Category,
        int DefaultUsefulLifeMonths,
        bool Depreciable,
        string? AccumDepAccountCode,
        string? DepExpenseAccountCode);

    private static readonly Dictionary<string, AssetClass> Map = new()
    {
        ["12210"] = new("12210", "อุปกรณ์สำนักงาน", 60, true, "18310", "56110"),
        ["12220"] = new("12220", "คอมพิวเตอร์", 36, true, "18320", "56120"),
        ["12230"] = new("12230", "เครื่องตกแต่งสำนักงาน", 60, true, "18330", "56130"),
        ["12240"] = new("12240", "ยานพาหนะ", 60, true, "18340", "56140"),
        ["12250"] = new("12250", "สินทรัพย์ตามสัญญาเช่า", 36, true, "18350", "56150"),
        ["12260"] = new("12260", "เครื่องจักรและอุปกรณ์", 120, true, "18360", "56160"),
        ["12270"] = new("12270", "อาคาร", 240, true, "18370", "56170"),
        ["12280"] = new("12280", "งานระหว่างก่อสร้าง", 0, false, null, null),
        ["12290"] = new("12290", "ที่ดิน", 0, false, null, null),
        ["12310"] = new("12310", "ซอฟต์แวร์คอมพิวเตอร์", 60, true, "18410", "56210"),
    };

    /// <summary>true = ผังนี้เป็น PPE/intangible ที่ต้องลงทะเบียนสินทรัพย์ถาวร.
    /// รับ accountCode (level-4) หรือ prefix — match exact ก่อน แล้ว 5-หลักแรก.</summary>
    public static bool IsRegisterable(string? accountCode)
        => Resolve(accountCode) != null;

    public static AssetClass? Resolve(string? accountCode)
    {
        if (string.IsNullOrWhiteSpace(accountCode)) return null;
        var code = accountCode.Trim();
        if (Map.TryGetValue(code, out var exact)) return exact;
        // เผื่อผัง custom: ใช้ 5 หลักแรกที่ตรง map (เช่น 122101 → 12210)
        if (code.Length > 5)
        {
            var head = code[..5];
            if (Map.TryGetValue(head, out var byHead)) return byHead;
        }
        return null;
    }
}

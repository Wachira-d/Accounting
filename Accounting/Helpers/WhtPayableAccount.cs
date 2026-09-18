using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ผังบัญชี "ภาษีหัก ณ ที่จ่ายค้างจ่าย" ที่ต้องใช้ — ตัวตัดสินตัวเดียวของระบบ** (pure ไม่มี I/O)
///
/// ═══ ที่มา (ผลตรวจทีมสายข้อมูล รอบ 180) ═══
/// การเลือก 21916 / 21917 / 21918 ถูกตัดสินจาก <b>4 ที่</b> ด้วยกติกาคนละชุด:
/// <list type="bullet">
/// <item><c>DocumentService.ResolveWhtPayableAccountAsync</c> — ผ่าน <see cref="WhtPayeeKind"/> ✅</item>
/// <item><c>PdfGenerationService</c> (พรีวิว GL ก่อนอนุมัติ) — <c>ContactType</c> ดิบ ⇒
/// <b>พรีวิวโชว์บัญชีหนึ่ง แต่ JE จริงลงอีกบัญชี</b> (defect class "สอง renderer ห้าม drift")</item>
/// <item><c>IntegrationService</c> ตั้งหนี้ค่าใช้จ่าย — <c>ContactType</c> ดิบ</item>
/// <item><c>IntegrationService</c> จ่ายเงิน — <c>TaxId.StartsWith("0")</c> ดิบ</item>
/// </list>
/// และ <b>สามที่หลังไม่รู้จัก 21918 (ภ.ง.ด.54) เลย</b> ⇒ WHT ของการจ่ายต่างประเทศ
/// ตกไปกอง ภ.ง.ด.3/53 ⇒ ตอนกดนำส่งจะ Dr บัญชีที่ไม่มียอด ⇒ ยอดค้างที่ล้างไม่ได้ตลอดกาล
///
/// <para>⚠️ คืน <b>ลำดับรหัสที่ต้องลองตามลำดับ</b> ไม่ใช่รหัสเดียว — ผังบัญชีที่ตั้งไม่ครบ
/// ต้องยังลงบัญชีได้ (ตกไปตัวที่มีอยู่) แทนที่จะ throw แล้วทำให้เอกสารลง GL ไม่ได้เลย</para>
/// </summary>
public static class WhtPayableAccount
{
    /// <summary>ภ.ง.ด.3 — ผู้รับเป็นบุคคลธรรมดา</summary>
    public const string Pnd3Code = "21916";
    /// <summary>ภ.ง.ด.53 — ผู้รับเป็นนิติบุคคลไทย</summary>
    public const string Pnd53Code = "21917";
    /// <summary>ภ.ง.ด.54 — จ่ายออกต่างประเทศ (ม.70) · คนละแบบ คนละกำหนดยื่น</summary>
    public const string Pnd54Code = "21918";

    /// <summary>รหัสที่ต้องลองตามลำดับ (ตัวแรก = ที่ถูกต้อง · ที่เหลือ = ตาข่ายกันผังไม่ครบ)</summary>
    public static IReadOnlyList<string> CodeChain(
        bool isForeignService, string? countryCode, string? taxId, ContactType contactType, string? name)
        => Form(isForeignService, countryCode, taxId, contactType, name) switch
        {
            TaxType.WithholdingTax54 => new[] { Pnd54Code, Pnd53Code, Pnd3Code },
            TaxType.WithholdingTax53 => new[] { Pnd53Code, Pnd3Code },
            _ => new[] { Pnd3Code, Pnd53Code },
        };

    /// <summary>ชื่อบัญชีที่ควรแสดงคู่กับรหัสตัวแรกของ <see cref="CodeChain"/>
    /// — ให้พรีวิวกับ JE จริงพูดตรงกันคำต่อคำ</summary>
    public static string PreferredLabel(
        bool isForeignService, string? countryCode, string? taxId, ContactType contactType, string? name)
        => Form(isForeignService, countryCode, taxId, contactType, name) switch
        {
            TaxType.WithholdingTax54 => "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 54",
            TaxType.WithholdingTax53 => "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 53",
            _ => "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 3",
        };

    /// <summary>แบบ ภ.ง.ด. ที่ผู้รับรายนี้อยู่ — ผ่าน <see cref="WhtPayeeKind"/> ตัวเดียว
    /// ห้ามเขียนกติกา "เลขขึ้นต้น 0" หรือ "ContactType ดิบ" ซ้ำที่ไหนอีก</summary>
    private static TaxType Form(
        bool isForeignService, string? countryCode, string? taxId, ContactType contactType, string? name)
        => WhtPayeeKind.ResolveForm(isForeignService, countryCode, taxId, contactType, name);
}

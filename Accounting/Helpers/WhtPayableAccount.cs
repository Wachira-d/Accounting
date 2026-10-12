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
    /// <summary>ภ.ง.ด.2 — ดอกเบี้ย/เงินปันผล ม.40(4) จ่ายบุคคลธรรมดา (ม.50(2) · ม.59) · รอบ 204 คำตัดสินข้อ 148 ·
    /// แถวนี้อยู่ในผังมาตรฐาน (<c>ChartOfAccountTemplates</c>) ตั้งแต่แรก แต่ไม่เคยมีผู้ลงบัญชี (นอกจากทิปที่ลงผิดผัง — แก้แล้ว
    /// <c>TipPayoutService</c>) · บริษัทเดิมที่ไม่มีแถวนี้ได้จาก <see cref="MigrationSeedSql"/></summary>
    public const string Pnd2Code = "21915";
    /// <summary>ภ.ง.ด.3 — ผู้รับเป็นบุคคลธรรมดา</summary>
    public const string Pnd3Code = "21916";
    /// <summary>ภ.ง.ด.53 — ผู้รับเป็นนิติบุคคลไทย</summary>
    public const string Pnd53Code = "21917";
    /// <summary>ภ.ง.ด.54 — จ่ายออกต่างประเทศ (ม.70) · คนละแบบ คนละกำหนดยื่น</summary>
    public const string Pnd54Code = "21918";

    /// <summary>ผังค้างจ่ายภาษีหัก ณ ที่จ่ายของ "ผู้จ่าย" ทุกแบบ (ภ.ง.ด.2/3/53/54) — ชุดเดียวให้ด่าน JE/เครื่องมือย้ายผัง/กระทบยอดอ่าน
    /// (ภ.ง.ด.1 21914 ไม่อยู่ในชุดนี้ — มาจากรอบเงินเดือน ไม่ใช่เอกสารซื้อ)</summary>
    public static readonly IReadOnlyList<string> DocumentPayableCodes = new[] { Pnd2Code, Pnd3Code, Pnd53Code, Pnd54Code };

    /// <summary>รหัสผังค้างจ่ายของแบบ ภ.ง.ด. หนึ่งแบบ (null = ไม่ใช่แบบที่เอกสารซื้อตั้งหนี้)</summary>
    public static string? CodeFor(TaxType form) => form switch
    {
        TaxType.WithholdingTax2 => Pnd2Code,
        TaxType.WithholdingTax3 => Pnd3Code,
        TaxType.WithholdingTax53 => Pnd53Code,
        TaxType.WithholdingTax54 => Pnd54Code,
        _ => null,
    };

    /// <summary>รหัสที่ต้องลองตามลำดับ (ตัวแรก = ที่ถูกต้อง · ที่เหลือ = ตาข่ายกันผังไม่ครบ)</summary>
    /// <param name="withheldIncomeTypeCodes">รหัสเงินได้ของบรรทัดที่หัก (<see cref="WhtIncomeCodes"/>) — ต้องส่งทุกทางที่มีเอกสาร
    /// ไม่งั้นใบดอกเบี้ย/ปันผลของบุคคลธรรมดาลง 21916 แทน 21915 (รอบ 204)</param>
    public static IReadOnlyList<string> CodeChain(
        bool isForeignService, string? countryCode, string? taxId, ContactType contactType, string? name,
        IEnumerable<string?>? withheldIncomeTypeCodes = null)
        => Form(isForeignService, countryCode, taxId, contactType, name, withheldIncomeTypeCodes) switch
        {
            TaxType.WithholdingTax54 => new[] { Pnd54Code, Pnd53Code, Pnd3Code },
            TaxType.WithholdingTax53 => new[] { Pnd53Code, Pnd3Code },
            // ภ.ง.ด.2: ผังไม่ครบ ⇒ ตกไป 21916 (บุคคลธรรมดาเหมือนกัน) — migration ใส่ 21915 ให้บริษัทเดิมที่มีกลุ่ม 219 แล้ว
            TaxType.WithholdingTax2 => new[] { Pnd2Code, Pnd3Code, Pnd53Code },
            _ => new[] { Pnd3Code, Pnd53Code },
        };

    /// <summary>ชื่อบัญชีที่ควรแสดงคู่กับรหัสตัวแรกของ <see cref="CodeChain"/>
    /// — ให้พรีวิวกับ JE จริงพูดตรงกันคำต่อคำ</summary>
    public static string PreferredLabel(
        bool isForeignService, string? countryCode, string? taxId, ContactType contactType, string? name,
        IEnumerable<string?>? withheldIncomeTypeCodes = null)
        => Form(isForeignService, countryCode, taxId, contactType, name, withheldIncomeTypeCodes) switch
        {
            TaxType.WithholdingTax54 => "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 54",
            TaxType.WithholdingTax53 => "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 53",
            TaxType.WithholdingTax2 => "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 2",
            _ => "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 3",
        };

    /// <summary>แบบ ภ.ง.ด. ที่ผู้รับรายนี้อยู่ — ผ่าน <see cref="WhtPayeeKind"/> ตัวเดียว
    /// ห้ามเขียนกติกา "เลขขึ้นต้น 0" หรือ "ContactType ดิบ" ซ้ำที่ไหนอีก</summary>
    private static TaxType Form(
        bool isForeignService, string? countryCode, string? taxId, ContactType contactType, string? name,
        IEnumerable<string?>? withheldIncomeTypeCodes)
        => WhtPayeeKind.ResolveForm(isForeignService, countryCode, taxId, contactType, name, withheldIncomeTypeCodes);

    /// <summary>INSERT ผัง 21915 (ภ.ง.ด.2) ให้<b>บริษัทเดิม</b>ที่มีกลุ่ม 219 แต่ไม่มีแถวนี้ — ตัวสร้าง SQL เดียวกับผังมาตรฐานของ Settlement
    /// (<see cref="SettlementChartSeed.SeedSqlFor"/> · <c>ON CONFLICT DO NOTHING</c> ⇒ รันทุกบูตได้ · ไม่ทับชื่อที่ลูกค้าตั้งเอง · ไม่ย้ายยอด)</summary>
    public static string MigrationSeedSql() => SettlementChartSeed.SeedSqlFor(new[] { Pnd2Code });
}

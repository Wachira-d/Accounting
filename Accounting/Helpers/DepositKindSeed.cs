using System.Globalization;
using System.Text;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>แถว seed หนึ่งแถว (ยังไม่ผูกบริษัท) — ตารางเดียวที่ทั้ง migration และจุดสร้างบริษัทอ่าน</summary>
/// <param name="VatTreatment">null = ตามค่าตั้งต้นบริษัท/ประเภทธุรกิจ (ADVANCE — ปุ่ม "ตั้งค่า → ภาษี" เดิมยังมีผล ไม่มีสำเนาสองที่)</param>
public sealed record DepositKindSeedRow(
    string SeedKey, string Code, string Name, DepositNature Nature, DepositVatTreatment? VatTreatment,
    bool IsDefault, int SortOrder, string Description);

/// <summary>
/// <b>ประเภทเงินมัดจำเริ่มต้นต่อประเภทธุรกิจ</b> (รอบ 194 · spec S8 · ทีมกฎหมาย L2 §7) — tenant ใหม่ห้ามว่าง
/// (ทำนองเดียวกับกฎเหล็ก #1 ข้อ 3 cold-start)
///
/// <para>ทุกบริษัท: <c>ADVANCE</c> "มัดจำ/เงินรับล่วงหน้า" (ส่วนหนึ่งของราคา · โหมด = ตามค่าตั้งต้นบริษัท · เริ่มต้น) +
/// <c>SECURITY</c> "เงินประกัน (ต้องคืน)" (เงินประกัน · มัดจำเต็มยอด) · โรงแรม: ชื่อ "มัดจำค่าห้องพัก" / "เงินประกันความเสียหาย"
/// (บัญชี 21530 เมื่อผังมี — ตัดสินตอนใช้ที่ <see cref="DepositPolicyResolver.SecurityLiabilityAccountCode"/>) ·
/// อสังหาฯ: + <c>RENT-ADV</c> "ค่าเช่าล่วงหน้า (ยกเว้น VAT)" (นอกระบบ VAT)</para>
///
/// <para>ตารางนี้ตัวเดียว: จุดสร้างบริษัท + lazy (<see cref="EnsureSeededAsync"/> — CompanyService/AuthService ×2) ·
/// migration ตอนบูต (<see cref="MigrationSeedSql"/> — สร้าง VALUES จากตารางนี้ ไม่มีสำเนาใน SQL) · idempotent ด้วย
/// <c>SeedKey</c> unique ต่อบริษัท + <c>ON CONFLICT DO NOTHING</c> ⇒ แถวที่ผู้ใช้ลบ (soft) ไม่ถูก seed คืน</para>
/// </summary>
public static class DepositKindSeed
{
    public const string AdvanceSeedKey = "sys:ADVANCE";
    public const string SecuritySeedKey = "sys:SECURITY";
    public const string RentAdvanceSeedKey = "sys:RENT-ADV";
    /// <summary>คำนำหน้ากุญแจของประเภทที่ migration สร้างจากค่าเดิมของที่พัก (<c>lp:{propertyId}</c>)</summary>
    public const string LodgingPropertySeedPrefix = "lp:";

    /// <summary>รายการประเภทเริ่มต้นของประเภทธุรกิจนี้ (pure · ลำดับคงที่ · ตัวแรก = เริ่มต้น)</summary>
    public static IReadOnlyList<DepositKindSeedRow> For(IndustryType industry)
    {
        var hotel = industry == IndustryType.Hotel;
        var rows = new List<DepositKindSeedRow>
        {
            new(AdvanceSeedKey, "ADVANCE", hotel ? "มัดจำค่าห้องพัก" : "มัดจำ/เงินรับล่วงหน้า",
                DepositNature.PartOfPrice, null, IsDefault: true, SortOrder: 10,
                "มัดจำ/เงินจอง/ชำระล่วงหน้าที่เป็นส่วนหนึ่งของราคา — ภาษีขายถึงกำหนดตอนรับเงิน (สินค้า §78(1)(ข) · บริการ §78/1 · ป.73/2541) · "
                + "วิธีบันทึกตามค่าตั้งต้นบริษัท (ตั้งค่า → ภาษี)"),
            new(SecuritySeedKey, "SECURITY", hotel ? "เงินประกันความเสียหาย" : "เงินประกัน (ต้องคืน)",
                DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit, IsDefault: false, SortOrder: 20,
                "เงินประกันที่ต้องคืน (ความเสียหาย/กุญแจ/ภาชนะ/ประกันสัญญา) — ยังไม่ใช่ค่าตอบแทน จึงยังไม่เกิดภาษีขาย · "
                + "ลงบัญชีเงินประกัน (21530 ถ้าผังมี ไม่งั้น 21620) · ห้ามนำไปหักราคา — ตัดชำระหนี้ได้เท่านั้น"),
        };
        if (industry == IndustryType.RealEstate)
            rows.Add(new(RentAdvanceSeedKey, "RENT-ADV", "ค่าเช่าล่วงหน้า (ยกเว้น VAT)",
                DepositNature.NonVatSupply, DepositVatTreatment.FullDeposit, IsDefault: false, SortOrder: 30,
                "ค่าเช่าอสังหาริมทรัพย์ล่วงหน้า/มัดจำค่าเช่า — ยกเว้นภาษีมูลค่าเพิ่ม §81(1)(ต) · ระบบบันทึก VAT 0 เสมอ"));
        return rows;
    }

    internal static DepositKind ToEntity(Guid companyId, DepositKindSeedRow r) => new()
    {
        CompanyId = companyId,
        Code = r.Code,
        Name = r.Name,
        Nature = r.Nature,
        VatTreatment = r.VatTreatment,
        IsDefault = r.IsDefault,
        IsActive = true,
        SortOrder = r.SortOrder,
        Description = r.Description,
        SeedKey = r.SeedKey,
    };

    /// <summary>idempotent: เติมเฉพาะแถว seed ที่บริษัทนี้ยังไม่มี (รวมแถวที่ถูกลบแบบ soft — ไม่ seed คืน) · <c>Add</c>
    /// เข้า <paramref name="db"/> แต่<b>ไม่</b> SaveChanges (ผู้เรียกบันทึกตามจังหวะเดิม) · คืนจำนวนแถวที่เพิ่ม ·
    /// ไม่พบบริษัท = โยน <see cref="KeyNotFoundException"/> (ล้มดัง)
    /// <para>จุดสร้างบริษัท (CompanyService.CreateAsync · AuthService.RegisterAsync/SsoLoginAsync) เรียกประโยคเดียวหลัง
    /// <c>_db.Companies.Add(company)</c> ก่อน SaveChanges — <c>FindAsync</c> เจอบริษัทที่ยังไม่บันทึกจาก change tracker ·
    /// ใช้ lazy ได้ด้วย (บริษัทเก่าที่เกิดก่อน migration — ปกติ migration ตอนบูตครอบแล้ว)</para></summary>
    public static async Task<int> EnsureSeededAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var company = await db.Companies.FindAsync(new object[] { companyId }, ct)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท — สร้างประเภทเงินมัดจำเริ่มต้นไม่ได้");
        var have = await db.DepositKinds.IgnoreQueryFilters()
            .Where(k => k.CompanyId == companyId && k.SeedKey != null)
            .Select(k => k.SeedKey!).ToListAsync(ct);
        var haveSet = new HashSet<string>(have, StringComparer.Ordinal);
        foreach (var k in db.DepositKinds.Local)
            if (k.CompanyId == companyId && k.SeedKey != null) haveSet.Add(k.SeedKey);
        var added = 0;
        foreach (var r in For(company.IndustryType))
        {
            if (!haveSet.Add(r.SeedKey)) continue;
            db.DepositKinds.Add(ToEntity(companyId, r));
            added++;
        }
        return added;
    }

    /// <summary>SQL seed ตอนบูต — VALUES สร้างจาก <see cref="For"/> ทุก <see cref="IndustryType"/> (เลขจาก enum จริง ไม่เดา) ·
    /// บริษัทที่ค่า IndustryType ไม่อยู่ใน enum ได้ชุดของ <see cref="IndustryType.General"/> · <c>ON CONFLICT DO NOTHING</c> อาศัย
    /// <c>UX_DepositKinds_Company_SeedKey</c> ⇒ รันทุกบูตได้ · <b>ไม่แตะตารางอื่น</b></summary>
    public static string MigrationSeedSql()
    {
        var inv = CultureInfo.InvariantCulture;
        var values = new List<string>();
        var industries = Enum.GetValues<IndustryType>();
        foreach (var ind in industries)
            foreach (var r in For(ind))
                values.Add(string.Format(inv, "({0},{1},{2},{3},{4},{5},{6},{7},{8})",
                    (int)ind, Lit(r.SeedKey), Lit(r.Code), Lit(r.Name), (int)r.Nature,
                    r.VatTreatment is { } t ? ((int)t).ToString(inv) : "NULL::integer",
                    r.IsDefault ? "true" : "false", r.SortOrder, Lit(r.Description)));
        var known = string.Join(",", industries.Select(i => ((int)i).ToString(inv)));
        var sb = new StringBuilder();
        sb.Append("INSERT INTO \"DepositKinds\" (\"Id\",\"CompanyId\",\"Code\",\"Name\",\"Nature\",\"VatTreatment\",\"IsDefault\",\"IsActive\",")
          .Append("\"SortOrder\",\"Description\",\"SeedKey\",\"CreatedAt\",\"IsDeleted\") ")
          .Append("SELECT gen_random_uuid(), c.\"Id\", v.code, v.name, v.nature, v.treatment, v.is_default, true, v.sort_order, v.description, v.seed_key, ")
          .Append("now(), false FROM \"Companies\" c JOIN (VALUES ")
          .Append(string.Join(",", values))
          .Append(") AS v(industry, seed_key, code, name, nature, treatment, is_default, sort_order, description) ")
          .Append("ON v.industry = CASE WHEN c.\"IndustryType\" IN (").Append(known).Append(") THEN c.\"IndustryType\" ELSE ")
          .Append(((int)IndustryType.General).ToString(inv)).Append(" END ")
          .Append("WHERE c.\"IsDeleted\" = false ON CONFLICT DO NOTHING;");
        return sb.ToString();
    }

    /// <summary>สตริง SQL แบบ literal (ข้อความในตารางนี้เป็นค่าคงที่ของระบบ — หนี ' ไว้กันพลาดเมื่อมีคนแก้ข้อความทีหลัง)</summary>
    private static string Lit(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
}

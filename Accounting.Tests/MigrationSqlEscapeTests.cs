using Accounting.Data;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ที่มา: log บูตเครื่องเจ้าของ 2026-10-19 — ExecuteSqlRaw ส่ง sql เข้า string.Format เสมอ (แม้ไม่มีพารามิเตอร์) ⇒
/// คำสั่ง migration ที่มี <c>{n}</c> ใน regex ล้มด้วย FormatException ทุกบูต (ย้ายข้อมูลไม่เคยรัน) และ <c>'{{company.phone}}'</c>
/// ถูกยุบเหลือวงเล็บเดี่ยวเงียบ ๆ · VendorCanon เขียน FieldName เกิน 50 ตัว · (seed ดิบที่ล้ม not-null บนฐาน EnsureCreated — รอคำตัดสินเจ้าของ
/// เพราะเปิดแล้วมีผลถึงลูกค้า/บิล: เตือนชำระทางอีเมล · กลุ่มบิล · แคตตาล็อก+ราคาส่วนเสริม รวม documents.overage)
/// </summary>
public class MigrationSqlEscapeTests
{
    private static IEnumerable<string> AllStatements()
        => DatabaseMigrationHelper.GetAlterStatements().Concat(DatabaseMigrationHelper.GetFullTextSearchStatements());

    [Fact]
    public void Every_migration_statement_survives_ExecuteSqlRaw_formatting_verbatim()
    {
        // string.Format แบบเดียวกับ EF RawSqlCommandBuilder (ไม่มีพารามิเตอร์) ต้องคืนคำสั่งเดิมทุกตัวอักษร
        foreach (var sql in AllStatements())
            Assert.Equal(sql, string.Format(DatabaseMigrationHelper.EscapeForExecuteSqlRaw(sql), Array.Empty<object>()));
    }

    [Fact]
    public void Regex_quantifier_statement_breaks_without_escape_and_runs_with_it()
    {
        // ทิศตรงข้าม: ถอด escape ⇒ บั๊กเดิมกลับมา (FormatException) — ด่านนี้ไม่ใช่ผ่านตลอดกาล
        var sql = AllStatements().First(s => s.Contains("[0-9A-F]{6}$", StringComparison.Ordinal));
        Assert.Throws<FormatException>(() => string.Format(sql, Array.Empty<object>()));
        Assert.Equal(sql, string.Format(DatabaseMigrationHelper.EscapeForExecuteSqlRaw(sql), Array.Empty<object>()));
    }

    [Fact]
    public void Cms_token_statement_keeps_double_braces_and_repair_exists()
    {
        var seed = AllStatements().First(s => s.Contains("'02-XXX-XXXX', '{{company.phone}}'", StringComparison.Ordinal));
        // เดิม (ไม่ escape) ฐานได้วงเล็บเดี่ยว ซึ่ง CmsContentTokens ไม่แทนค่า
        Assert.Contains("'{company.phone}'", string.Format(seed, Array.Empty<object>()));
        Assert.Contains("'{{company.phone}}'", string.Format(DatabaseMigrationHelper.EscapeForExecuteSqlRaw(seed), Array.Empty<object>()));
        // ของที่ persist ผิดแล้วต้องมีคำสั่งซ่อม ทั้งสองตาราง
        Assert.Equal(2, AllStatements().Count(s => s.Contains("regexp_replace(\"ConfigJson\"", StringComparison.Ordinal)
                                                   && s.Contains("'{{\\1}}'", StringComparison.Ordinal)));
    }

    [Fact]
    public void VendorKnownGood_field_name_fits_matched_contact_key_and_column_is_widened()
    {
        var key = "MatchedContactId:" + "บริษัท ปิโตรเลียมไทยคอร์ปอเรชั่น จำกัด";   // 55 ตัว — แถวจริงที่ล้นใน log
        Assert.True(key.Length > 50);
        Assert.True(key.Length <= VendorKnownGoodValue.FieldNameMaxLength);
        Assert.Contains(AllStatements(), s => s.Contains(
            $"ALTER TABLE \"VendorKnownGoodValues\" ALTER COLUMN \"FieldName\" TYPE varchar({VendorKnownGoodValue.FieldNameMaxLength})",
            StringComparison.Ordinal));
    }
}

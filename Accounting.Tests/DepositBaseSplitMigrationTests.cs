using System.Globalization;
using Accounting.Data;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 193 ฝ่ายค้านรอบสี่ R4-3 — การย้ายค่า <c>BillDiscountAmount → DepositBaseDeducted</c> ต้องเกิด<b>ครั้งเดียว</b>
/// (ในขั้นเดียวกับที่คอลัมน์ถูกสร้าง) · เดิมเป็น UPDATE ที่รันทุกครั้งที่บูต ⇒ แถวที่โค้ดใหม่สร้าง (อ้างเลขใบมัดจำ + ส่วนลดบาท ·
/// ฐานมัดจำ 0) ถูกย้ายส่วนลดการค้าเป็นฐานมัดจำแล้วถูกรับรู้เป็นมัดจำตอนอนุมัติ (R3-1 กลับมาเงียบ) และสองเครื่องบูตพร้อมกันหักซ้ำ.
/// เรพไม่มี PostgreSQL ในเทสต์ ⇒ ล็อก "รูป" ของคำสั่งที่ระบบรันจริง (ไม่ใช่สำเนา)</summary>
public class DepositBaseSplitMigrationTests
{
    private static string Sql => DatabaseMigrationHelper.DepositBaseSplitMigrationSql();

    [Fact]
    public void คำสั่งที่แตะฐานมัดจำมีคำสั่งเดียว_คือขั้นสร้างคอลัมน์()
    {
        // ห้ามมี UPDATE/ADD COLUMN ของช่องนี้นอกขั้นสร้างคอลัมน์ (คำสั่งที่รันทุกบูต = ย้ายซ้ำ/จับแถวใหม่)
        var touching = DatabaseMigrationHelper.GetAlterStatements()
            .Concat(DatabaseMigrationHelper.GetFullTextSearchStatements())
            .Where(s => s.Contains("DepositBaseDeducted", StringComparison.Ordinal)).ToList();
        Assert.Equal(Sql, Assert.Single(touching));
    }

    [Fact]
    public void ย้ายเฉพาะตอนคอลัมน์ยังไม่มี_ตรวจก่อนสร้าง_และสร้างก่อนย้าย()
    {
        var check = Sql.IndexOf("information_schema.columns", StringComparison.Ordinal);
        var ret = Sql.IndexOf("RETURN;", StringComparison.Ordinal);
        var add = Sql.IndexOf("ADD COLUMN \"DepositBaseDeducted\"", StringComparison.Ordinal);
        var update = Sql.IndexOf("UPDATE \"Documents\"", StringComparison.Ordinal);
        Assert.True(check >= 0 && ret > check && add > ret && update > add, "ลำดับต้องเป็น ตรวจคอลัมน์ → มีแล้วออก → สร้าง → ย้าย");
        // สร้างแบบไม่มี IF NOT EXISTS — ถ้ามีคอลัมน์แล้วต้องไม่มาถึงบรรทัดนี้เลย
        Assert.DoesNotContain("ADD COLUMN IF NOT EXISTS \"DepositBaseDeducted\"", Sql);
    }

    [Fact]
    public void แถวเป้าหมายต้องยังไม่มีฐานมัดจำ_และใบที่ย้ายติดหมายเหตุให้ตรวจ()
    {
        Assert.Contains("t.\"DepositBaseDeducted\" = 0", Sql);
        Assert.Contains("[DEPOSIT-BASE-SPLIT]", Sql);
        Assert.Contains("\"InternalNotes\" =", Sql);
    }

    [Fact]
    public void ล็อกคีย์คงที่ข้ามเครื่อง_ก่อนตรวจคอลัมน์()
    {
        var key = AdvisoryLockKey.For("db-migration", "Documents.DepositBaseDeducted").ToString(CultureInfo.InvariantCulture);
        var lockAt = Sql.IndexOf($"pg_advisory_xact_lock({key})", StringComparison.Ordinal);
        Assert.True(lockAt >= 0, "ต้องล็อกด้วยคีย์จาก AdvisoryLockKey (deterministic)");
        Assert.True(lockAt < Sql.IndexOf("information_schema.columns", StringComparison.Ordinal));
        // คีย์เดิมทุกครั้ง (ไม่ขึ้นกับ process — ต่างจาก GetHashCode)
        Assert.Equal(key, DatabaseMigrationHelper.DepositBaseSplitLockKey);
    }
}

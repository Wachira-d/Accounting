using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Tests.Db;

/// <summary>
/// ฐานข้อมูล PostgreSQL จริงสำหรับเทสต์ trait <c>Category=Db</c> (รอบ 201 ทีม PL · A-PL2 · คำตัดสินข้อ 33) —
/// job CI <c>db-test</c> ใน <c>.github/workflows/ci.yml</c> เปิด service <c>postgres:16</c> แล้วส่ง connection string ผ่าน
/// <c>ACCOUNTING_TEST_PG</c> + ตั้ง <c>ACCOUNTING_DB_TEST_REQUIRED=1</c> (ไม่มีฐาน = ล้มดัง ไม่ใช่ผ่านเงียบ)
///
/// <para><b>ทีมอื่นใช้ร่วมได้</b>: ติด <c>[Trait("Category", "Db")]</c> · เรียก <see cref="TryCreateContext"/> ·
/// แยกข้อมูลด้วย CompanyId สุ่มต่อเทสต์ (ฐานเดียวกันทั้ง job · ไม่ลบข้อมูลทิ้ง) · job <c>test</c> เดิมกรอง
/// <c>Category!=Db</c> จึงไม่ช้าลงและไม่แดงเพราะไม่มีฐาน</para>
///
/// <para>เครื่องนักพัฒนาที่ไม่มีฐาน (ไม่ตั้ง env) ⇒ <see cref="TryCreateContext"/> คืน null แล้วเทสต์จบเองพร้อมข้อความ — เทสต์
/// DB ไม่ใช่หลักฐานบนเครื่องนั้น หลักฐานคือผล job <c>db-test</c></para>
/// </summary>
public static class DbTestDatabase
{
    public const string ConnectionEnv = "ACCOUNTING_TEST_PG";
    public const string RequiredEnv = "ACCOUNTING_DB_TEST_REQUIRED";

    private static readonly object Gate = new();
    private static bool _schemaReady;

    static DbTestDatabase()
    {
        // เหมือน Program.cs — คอลัมน์ timestamp without time zone + DateTime Kind ใดก็ได้ (ต้องตั้งก่อน Npgsql ถูกใช้ครั้งแรก)
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }

    /// <summary>connection string จาก env · ว่าง + ไม่บังคับ = null · ว่าง + บังคับ (job CI) = โยน</summary>
    public static string? ConnectionString
    {
        get
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionEnv);
            if (!string.IsNullOrWhiteSpace(cs)) return cs;
            if (Environment.GetEnvironmentVariable(RequiredEnv) == "1")
                throw new InvalidOperationException(
                    $"job db-test ต้องตั้ง {ConnectionEnv} (ไม่มีฐานข้อมูล = เทสต์ DB ไม่ได้รัน ห้ามผ่านเงียบ)");
            return null;
        }
    }

    /// <summary>context ใหม่ (หนึ่งตัวต่อ "คำขอ") บนฐานที่มี schema ครบจากโมเดล (EnsureCreated ครั้งแรกของ process) ·
    /// null = เครื่องนี้ไม่มีฐาน</summary>
    public static AccountingDbContext? TryCreateContext()
    {
        var cs = ConnectionString;
        if (cs == null) return null;
        var db = new AccountingDbContext(new DbContextOptionsBuilder<AccountingDbContext>().UseNpgsql(cs).Options);
        lock (Gate)
        {
            if (!_schemaReady)
            {
                db.Database.EnsureCreated();
                _schemaReady = true;
            }
        }
        return db;
    }
}

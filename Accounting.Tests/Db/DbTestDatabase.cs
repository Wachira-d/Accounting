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

    /// <summary>รูป FK ของ production (Program.cs สร้างด้วย SQL ตอนบูต: ON DELETE RESTRICT) — ฐานที่สร้างจากโมเดล (EnsureCreated) ได้ CASCADE
    /// ตามธรรมเนียม EF ⇒ จำลองให้ตรงตอนสร้างฐาน มิฉะนั้นเทสต์ลบถาวรผ่านทั้งที่ production ล้ม (idempotent · ย้ายจาก DocumentPurgeDbTests รอบ 250 WP-04)</summary>
    internal const string ProductionFkShapeSql = @"
DO $$
DECLARE r record;
BEGIN
  FOR r IN SELECT c.conname, c.conrelid::regclass AS t FROM pg_constraint c
           WHERE c.contype = 'f' AND c.confdeltype <> 'r'
             AND ((c.conrelid = '""DocumentSignatures""'::regclass AND c.confrelid IN ('""DocumentApprovals""'::regclass, '""Documents""'::regclass))
               OR (c.conrelid = '""DocumentApprovals""'::regclass AND c.confrelid = '""Documents""'::regclass))
  LOOP
    EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', r.t, r.conname);
  END LOOP;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_DocumentSignatures_Approvals') THEN
    ALTER TABLE ""DocumentSignatures"" ADD CONSTRAINT ""FK_DocumentSignatures_Approvals""
      FOREIGN KEY (""DocumentApprovalId"") REFERENCES ""DocumentApprovals""(""Id"") ON DELETE RESTRICT;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_DocumentSignatures_Documents') THEN
    ALTER TABLE ""DocumentSignatures"" ADD CONSTRAINT ""FK_DocumentSignatures_Documents""
      FOREIGN KEY (""DocumentId"") REFERENCES ""Documents""(""Id"") ON DELETE RESTRICT;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_DocumentApprovals_Documents') THEN
    ALTER TABLE ""DocumentApprovals"" ADD CONSTRAINT ""FK_DocumentApprovals_Documents""
      FOREIGN KEY (""DocumentId"") REFERENCES ""Documents""(""Id"") ON DELETE RESTRICT;
  END IF;
END $$;";

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
                // รอบ 250 WP-04 (AUDIT_249 E M-4): ฐานเทสต์ = รูป production เสมอ — ทำครั้งเดียวตอนสร้างฐาน (เดิม DocumentPurgeDbTests ALTER TABLE
                // ทุกเทสต์ ขณะคลาสอื่นรันขนาน ⇒ ล็อก AccessExclusive ชนกับเทสต์อื่น/ฐานรูปไม่คงที่ระหว่างรัน)
                db.Database.ExecuteSqlRaw(Accounting.Data.DatabaseMigrationHelper.OneShotDataFixesTableSql);
                db.Database.ExecuteSqlRaw(ProductionFkShapeSql);
                _schemaReady = true;
            }
        }
        return db;
    }
}

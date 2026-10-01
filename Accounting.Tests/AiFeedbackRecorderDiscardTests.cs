using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม AI · A-AI8 (คำตัดสินข้อ 58 · team-Z Q5) — ตัวบันทึก feedback ใช้ context ร่วมกับผู้เรียก ⇒ SaveChanges ของ
/// ตัวบันทึกล้มแล้วต้อง<b>ไม่ทิ้งของค้าง</b> (Modified/Added) ให้ SaveChanges ถัดไปของผู้เรียก (อนุมัติเอกสาร · AuditMiddleware) ล้มตาม
///
/// <para>เทสต์ระดับ change tracker (ไม่ต้องมีฐานข้อมูล — context ชี้ไปพอร์ตที่ไม่มีใครฟัง ไม่เคยเปิดการเชื่อมต่อ) ·
/// ว่าเมธอดของตัวบันทึก<b>เรียก</b>ตัวถอยจริงในทุก catch ล็อกด้วย <c>tools/required_call_site_check.py</c> (บล็อกรอบ 201 ทีม AI) ·
/// เทสต์ write→fail→continue บน PostgreSQL จริงรอ job <c>db-test</c> ของทีม PL (A-PL2)</para>
/// </summary>
public class AiFeedbackRecorderDiscardTests
{
    private static AccountingDbContext OfflineDb() => new(new DbContextOptionsBuilder<AccountingDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=none;Password=none").Options);

    [Fact]
    public void แถวที่แก้แล้วบันทึกไม่สำเร็จ_ต้องกลับเป็นค่าเดิมและ_Unchanged()
    {
        using var db = OfflineDb();
        var row = new AiUsageDaily { UsageDate = new DateTime(2026, 10, 1), ProviderType = AiProviderType.DeepSeek,
            FeatureKey = "GlAccountSuggestion", CallsAttempted = 1 };
        db.Attach(row);
        row.CallsAttempted = 5;
        db.ChangeTracker.DetectChanges();
        Assert.Equal(EntityState.Modified, db.Entry(row).State);

        AiFeedbackRecorder.DiscardUnsaved(db, row);

        Assert.Equal(EntityState.Unchanged, db.Entry(row).State);
        Assert.Equal(1, row.CallsAttempted);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public void แถวใหม่ที่บันทึกไม่สำเร็จ_ต้องถูกถอดออกจาก_tracker()
    {
        using var db = OfflineDb();
        var row = new AiUsageDailyTenant { UsageDate = new DateTime(2026, 10, 1), CompanyId = Guid.NewGuid(),
            FeatureKey = "BankStatementMatch" };
        db.Add(row);
        Assert.Equal(EntityState.Added, db.Entry(row).State);

        AiFeedbackRecorder.DiscardUnsaved(db, row);

        Assert.Equal(EntityState.Detached, db.Entry(row).State);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public void ไม่แตะแถวที่ไม่มีการแก้_และรับ_null_ได้()
    {
        // ทิศตรงข้าม: ของผู้เรียกที่ยังไม่ได้แก้ต้องไม่ถูกแตะ · catch ที่ล้มก่อนโหลดแถว (row == null) ต้องไม่โยน
        using var db = OfflineDb();
        var row = new AiUsageDaily { UsageDate = new DateTime(2026, 10, 1), FeatureKey = "X", CallsAttempted = 3 };
        db.Attach(row);
        AiFeedbackRecorder.DiscardUnsaved(db, row);
        Assert.Equal(EntityState.Unchanged, db.Entry(row).State);
        Assert.Equal(3, row.CallsAttempted);
        AiFeedbackRecorder.DiscardUnsaved(db, null);
    }
}

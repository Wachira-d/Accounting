using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// Round-trip test ของ audit hash chain — control ตาม พ.ร.บ.การบัญชี ม.11 ทวิ /
/// SOC2 (append-only + tamper-evident)
///
/// ที่มา: ฝั่งเขียน (<c>AccountingDbContext.ApplyAuditHashChain</c>) กับฝั่งตรวจ
/// (<c>AuditTrailService.VerifyHashChainAsync</c>) เคยเขียน canonical string แยกกัน
/// แล้ว drift — ฝั่งหนึ่งใช้ <c>(int)Action</c> + มี <c>OldValues</c>, อีกฝั่งใช้ชื่อ
/// enum + ตก <c>OldValues</c> ⇒ verify คืน invalid ที่แถวแรกของทุกบริษัท**เสมอ**
/// แม้ไม่มีใครแก้ข้อมูล ⇒ งานตรวจรายสัปดาห์แจ้งเตือนหลอกทุก tenant และแยก
/// "โดนแก้จริง" ออกจาก "mismatch ในตัว" ไม่ได้ = ควบคุมล้มเหลวเงียบ ๆ
///
/// เทสต์ชุดนี้จำลอง chain แบบเดียวกับ production (ผูก PrevHash ต่อกัน) แล้ว
/// ตรวจด้วย logic เดียวกับ verify — ถ้ามีใครแยก canonical เป็นสองที่อีก
/// เทสต์นี้จะแดงทันที (กฎเหล็ก #4 C + G)
/// </summary>
public class AuditHashChainTests
{
    private static AuditLog Row(int i, string? prevHash, AuditAction action = AuditAction.Update)
        => new()
        {
            Id = i,
            CompanyId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            UserId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            UserEmail = $"user{i}@example.com",
            Action = action,
            EntityType = "Document",
            EntityId = $"doc-{i}",
            OldValues = $"{{\"amount\":{i * 100}}}",
            NewValues = $"{{\"amount\":{i * 200}}}",
            // เวลาคงที่ (ไม่ใช้ DateTime.UtcNow) — เทสต์ต้อง deterministic
            Timestamp = new DateTime(2026, 1, 1, 0, 0, i, DateTimeKind.Utc),
            PrevHash = prevHash,
        };

    /// <summary>สร้าง chain แบบเดียวกับ ApplyAuditHashChain: PrevHash ของแถว i
    /// = RowHash ของแถว i-1</summary>
    private static List<AuditLog> BuildChain(int count)
    {
        var rows = new List<AuditLog>();
        string? prev = null;
        for (var i = 1; i <= count; i++)
        {
            var r = Row(i, prev);
            r.RowHash = AuditHashChain.ComputeRowHash(r);
            prev = r.RowHash;
            rows.Add(r);
        }
        return rows;
    }

    /// <summary>logic เดียวกับ AuditTrailService.VerifyHashChainAsync —
    /// คืน index ของแถวที่ผิด (-1 = ทั้ง chain ถูกต้อง)</summary>
    private static int VerifyChain(List<AuditLog> rows)
    {
        string? prev = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.PrevHash != prev) return i;
            if (!string.Equals(AuditHashChain.ComputeRowHash(r), r.RowHash,
                    StringComparison.OrdinalIgnoreCase)) return i;
            prev = r.RowHash;
        }
        return -1;
    }

    [Fact]
    public void Chain_ที่ไม่ถูกแก้_ต้องผ่านการตรวจ()
    {
        // นี่คือเคสที่บั๊กเดิมทำพัง: chain สะอาดแต่ verify บอกว่าโดนแก้
        Assert.Equal(-1, VerifyChain(BuildChain(5)));
    }

    [Fact]
    public void แก้ค่าในแถวกลาง_ต้องตรวจจับได้ที่แถวนั้น()
    {
        var rows = BuildChain(5);
        rows[2].NewValues = "{\"amount\":999999}";   // insider แก้ยอดเงินย้อนหลัง
        Assert.Equal(2, VerifyChain(rows));
    }

    [Fact]
    public void ลบแถวกลางออก_ต้องตรวจจับได้จาก_PrevHash_ที่ขาดตอน()
    {
        var rows = BuildChain(5);
        rows.RemoveAt(2);                            // ลบร่องรอยทิ้ง
        Assert.Equal(2, VerifyChain(rows));          // แถวถัดไป PrevHash ไม่ต่อ
    }

    [Fact]
    public void แก้แล้วคำนวณ_RowHash_ใหม่_ยังตรวจจับได้จากแถวถัดไป()
    {
        // ผู้โจมตีที่รู้สูตร แก้ค่า + คำนวณ hash แถวตัวเองใหม่ให้เนียน
        var rows = BuildChain(5);
        rows[1].NewValues = "{\"amount\":777}";
        rows[1].RowHash = AuditHashChain.ComputeRowHash(rows[1]);
        // แถว 1 ผ่าน แต่แถว 2 มี PrevHash เป็นค่าเดิม → ขาดตอน
        Assert.Equal(2, VerifyChain(rows));
    }

    [Fact]
    public void เปลี่ยนชนิด_Action_ต้องเปลี่ยน_hash()
    {
        // กันการ regress กลับไปใช้ชื่อ enum แทนเลข (หรือกลับกัน) แบบเงียบ ๆ
        var a = AuditHashChain.ComputeRowHash(Row(1, null, AuditAction.Create));
        var b = AuditHashChain.ComputeRowHash(Row(1, null, AuditAction.Delete));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void OldValues_ต้องอยู่ใน_canonical()
    {
        // บั๊กเดิมคือฝั่ง verify ตกช่องนี้ไป — ถ้าใครตัดออกอีก เทสต์นี้แดง
        var r1 = Row(1, null);
        var r2 = Row(1, null);
        r2.OldValues = "{\"amount\":0}";
        Assert.NotEqual(AuditHashChain.ComputeRowHash(r1), AuditHashChain.ComputeRowHash(r2));
    }

    [Fact]
    public void PrevHash_ต้องอยู่ใน_canonical_ไม่งั้นสลับลำดับแถวได้()
    {
        var r1 = Row(1, null);
        var r2 = Row(1, "ABCDEF");
        Assert.NotEqual(AuditHashChain.ComputeRowHash(r1), AuditHashChain.ComputeRowHash(r2));
    }

    [Fact]
    public void Canonical_มีครบ_9_ช่อง_ตามลำดับที่ตกลงไว้()
    {
        var canonical = AuditHashChain.Canonical(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Guid.Empty, "a@b.c", AuditAction.Update, "Document", "d1", "new", "old", "PREV");
        Assert.Equal(8, canonical.Count(ch => ch == '|'));      // 9 ช่อง = 8 คั่น
        Assert.Contains($"|{(int)AuditAction.Update}|", canonical);  // เลข ไม่ใช่ชื่อ enum
        Assert.EndsWith("|PREV", canonical);
    }
}

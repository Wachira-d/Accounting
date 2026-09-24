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

    // ==================================================================================
    // Round-trip ผ่าน PostgreSQL (ฝ่ายค้านรอบ 193 PLAUSIBLE-1) — เทสต์ข้างบน hash ค่าในหน่วยความจำทั้งสองฝั่ง
    // จึงผ่านได้ทั้งที่ของจริงตรวจไม่ผ่านทุกแถว. ชุดนี้จำลองสิ่งที่ Npgsql คืนมาจริง: คอลัมน์ timestamp without
    // time zone + EnableLegacyTimestampBehavior ⇒ Kind=Unspecified และความละเอียดเหลือไมโครวินาที
    // ==================================================================================

    /// <summary>สิ่งที่ PostgreSQL เก็บ + Npgsql (legacy) คืนมา: ตัดเหลือไมโครวินาที · Kind=Unspecified</summary>
    private static DateTime PgRoundTrip(DateTime t)
        => DateTime.SpecifyKind(new DateTime(t.Ticks - t.Ticks % 10), DateTimeKind.Unspecified);

    private static AuditLog ReadBack(AuditLog r) => new()
    {
        Id = r.Id, CompanyId = r.CompanyId, UserId = r.UserId, UserEmail = r.UserEmail, Action = r.Action,
        EntityType = r.EntityType, EntityId = r.EntityId, OldValues = r.OldValues, NewValues = r.NewValues,
        Timestamp = PgRoundTrip(r.Timestamp), PrevHash = r.PrevHash, RowHash = r.RowHash,
    };

    /// <summary>เวลามีหลัก 100ns ไม่เป็นศูนย์ — แบบ DateTime.UtcNow จริง (Row() ข้างบนใช้วินาทีเต็มจึงซ่อนบั๊ก)</summary>
    private static AuditLog LiveRow(int i)
    {
        var r = Row(i, null);
        r.Timestamp = new DateTime(2026, 9, 24, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234567 + i);
        return r;
    }

    private static List<AuditLog> SealChainV2(int count)
    {
        var rows = new List<AuditLog>();
        string? prev = null;
        for (var i = 1; i <= count; i++)
        {
            var r = LiveRow(i);
            AuditHashChain.Seal(r, prev);
            prev = r.RowHash;
            rows.Add(r);
        }
        return rows;
    }

    [Fact]
    public void V1_ค่าที่อ่านกลับจากฐาน_ไม่ตรงสูตรเดิมตรง_ๆ_นี่คือบั๊กที่ทำให้ตรวจไม่ผ่านทั้งระบบ()
    {
        // reproduce: hash ตอนเขียน (ในหน่วยความจำ) ≠ hash ของค่าที่อ่านกลับ — ถ้าเทสต์นี้แดง แปลว่าสมมติฐานเรื่อง
        // round-trip ผิด ต้องทบทวนทั้งชุด
        var r = LiveRow(1);
        r.RowHash = AuditHashChain.ComputeRowHash(r);
        var back = ReadBack(r);
        Assert.NotEqual(r.RowHash, AuditHashChain.ComputeRowHash(back));
    }

    [Fact]
    public void V2_เขียนแล้วอ่านกลับจากฐาน_ตรวจผ่านทั้ง_chain()
    {
        var back = SealChainV2(5).Select(ReadBack).ToList();
        Assert.Equal(-1, AuditHashChain.FirstBrokenIndex(back));
        Assert.All(back, r => Assert.StartsWith(AuditHashChain.V2Prefix, r.RowHash));
    }

    [Fact]
    public void V2_เวลาที่อ่านกลับเป็น_Local_ก็ยังตรวจผ่าน()
    {
        // ถ้าวันหนึ่งคอลัมน์ถูกเปลี่ยนเป็น timestamptz (legacy mode คืน Kind=Local) — normalize ต้องคืนเป็น UTC ได้
        var rows = SealChainV2(3).Select(ReadBack).ToList();
        foreach (var r in rows)
            r.Timestamp = DateTime.SpecifyKind(r.Timestamp, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(-1, AuditHashChain.FirstBrokenIndex(rows));
    }

    [Fact]
    public void V2_Seal_ตั้งเวลาที่เก็บ_เท่ากับเวลาที่_hash()
    {
        var r = LiveRow(1);
        AuditHashChain.Seal(r, null);
        Assert.Equal(DateTimeKind.Utc, r.Timestamp.Kind);
        Assert.Equal(0, r.Timestamp.Ticks % 10);            // ไม่มีหลักที่ฐานข้อมูลจะตัดทิ้ง
        Assert.Equal(r.Timestamp.Ticks, ReadBack(r).Timestamp.Ticks);   // เก็บแล้วอ่านกลับ = ค่าที่ hash
    }

    [Fact]
    public void V2_แก้ค่าหลังอ่านกลับ_ยังจับได้ที่แถวนั้น()
    {
        // ทิศตรงข้าม: normalize ต้องไม่ทำให้ด่านหลวม
        var back = SealChainV2(5).Select(ReadBack).ToList();
        back[3].NewValues = "{\"amount\":1}";
        Assert.Equal(3, AuditHashChain.FirstBrokenIndex(back));
    }

    [Fact]
    public void V2_แก้เวลาแม้แต่ไมโครวินาทีเดียว_จับได้()
    {
        var back = SealChainV2(3).Select(ReadBack).ToList();
        back[1].Timestamp = back[1].Timestamp.AddTicks(10);
        Assert.Equal(1, AuditHashChain.FirstBrokenIndex(back));
    }

    [Fact]
    public void V1_แถวเก่าที่อ่านกลับจากฐาน_ตรวจผ่านแบบ_legacy_ไม่ต้องเขียน_hash_ทับ()
    {
        // แถวก่อนรอบ 193 ถูก hash ด้วย v1 จากเวลา 7 หลัก — ตาราง append-only เขียนทับไม่ได้และไม่ควรเขียนทับ
        var rows = new List<AuditLog>();
        string? prev = null;
        for (var i = 1; i <= 4; i++)
        {
            var r = LiveRow(i);
            r.PrevHash = prev;
            r.RowHash = AuditHashChain.ComputeRowHash(r);
            prev = r.RowHash;
            rows.Add(ReadBack(r));
        }
        Assert.Equal(-1, AuditHashChain.FirstBrokenIndex(rows));
    }

    [Fact]
    public void V1_แถวเก่าที่ถูกแก้_legacy_ยังจับได้()
    {
        var r = LiveRow(1);
        r.RowHash = AuditHashChain.ComputeRowHash(r);
        var back = ReadBack(r);
        back.EntityId = "doc-อื่น";
        Assert.False(AuditHashChain.VerifyRow(back));
    }

    [Fact]
    public void Chain_ผสม_v1_แล้วต่อด้วย_v2_ตรวจผ่าน()
    {
        // บริษัทที่มีแถวเก่า (v1) แล้วแถวใหม่หลัง deploy (v2) ผูกต่อจาก RowHash v1 ตัวท้าย
        var old = LiveRow(1);
        old.RowHash = AuditHashChain.ComputeRowHash(old);
        var neu = LiveRow(2);
        AuditHashChain.Seal(neu, old.RowHash);
        Assert.Equal(-1, AuditHashChain.FirstBrokenIndex(new List<AuditLog> { ReadBack(old), ReadBack(neu) }));
    }

    [Fact]
    public void ปลอมป้ายรุ่น_v2_บนแถว_v1_ไม่ผ่าน()
    {
        var r = LiveRow(1);
        r.RowHash = AuditHashChain.V2Prefix + AuditHashChain.ComputeRowHash(r);
        Assert.False(AuditHashChain.VerifyRow(ReadBack(r)));
    }

    // ---------- PLAUSIBLE-2: แถวที่ยังรอบันทึกต้องเป็นปลาย chain ----------

    [Fact]
    public void ResolveTip_แถวที่ยังรอบันทึก_ชนะแถวในฐาน()
    {
        var a = LiveRow(1); AuditHashChain.Seal(a, "DBTIP");
        var b = LiveRow(2); AuditHashChain.Seal(b, a.RowHash);
        Assert.Equal(b.RowHash, AuditHashChain.ResolveTip(new[] { a, b }, "DBTIP"));
    }

    [Fact]
    public void ResolveTip_ไม่มีแถวรอบันทึก_ใช้แถวล่าสุดในฐาน()
    {
        Assert.Equal("DBTIP", AuditHashChain.ResolveTip(Array.Empty<AuditLog>(), "DBTIP"));
        Assert.Null(AuditHashChain.ResolveTip(Array.Empty<AuditLog>(), null));
    }

    [Fact]
    public void เพิ่มสองแถวก่อนบันทึก_ต้องไม่แตกกิ่ง()
    {
        // จำลอง AddChainedAuditLog ×2 ก่อน SaveChanges: เดิมทั้งคู่ได้ PrevHash = ปลายในฐาน ⇒ แถวที่สองตรวจไม่ผ่าน
        var pending = new List<AuditLog>();
        foreach (var i in new[] { 1, 2 })
        {
            var r = LiveRow(i);
            AuditHashChain.Seal(r, AuditHashChain.ResolveTip(pending, null));
            pending.Add(r);
        }
        Assert.Equal(pending[0].RowHash, pending[1].PrevHash);
        Assert.Equal(-1, AuditHashChain.FirstBrokenIndex(pending.Select(ReadBack).ToList()));
    }
}

using System.Security.Cryptography;
using System.Text;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// audit hash chain — control ตาม พ.ร.บ.การบัญชี ม.11 ทวิ / SOC2 (append-only + tamper-evident)
///
/// <para>═══ ขอบเขตที่เทสต์นี้พิสูจน์ได้และ<b>ไม่ได้</b> (ฝ่ายค้านรอบ 193 รอบสอง W2-C2 — เขียนไว้ตรง ๆ) ═══
/// เรพนี้ไม่มี PostgreSQL ในเทสต์ ⇒ ขั้น "อ่านกลับจากฐาน" <b>จำลอง</b>ด้วย <see cref="PgRoundTrip"/> (ตัดเหลือไมโครวินาที + Kind=Unspecified
/// ตามพฤติกรรมของคอลัมน์ timestamp without time zone + EnableLegacyTimestampBehavior) · เทสต์เรียก <c>Seal</c>/<c>Analyze</c> ตรง ๆ
/// <b>ไม่ได้ผ่าน</b> <c>AccountingDbContext.ApplyAuditHashChain</c>, Npgsql หรือลำดับ Id ที่ฐานข้อมูลออกให้ ·
/// การต่อสายของเส้นเขียน/ตรวจล็อกด้วย <c>tools/required_call_site_check.py</c> (ApplyAuditHashChain ต้องเรียก Seal+ResolveTip ·
/// ห้ามสูตรอื่น) และสูตร v1 เป็น private แล้ว (เส้นเขียนเรียกไม่ได้) · เทสต์ผ่าน Npgsql จริง = backlog (Testcontainers/CI ที่มี PostgreSQL)</para>
///
/// <para>ประวัติ: ฝั่งเขียนกับฝั่งตรวจเคยเขียน canonical แยกกันแล้ว drift ⇒ verify ไม่ผ่านที่แถวแรกของทุกบริษัท · รอบ 193 พบอีกชั้น:
/// เวลาที่อ่านกลับจากฐานไม่เท่ากับเวลาที่ hash (สูตร v1) ⇒ สูตร v2 · รอบสอง: chain แตกกิ่งเมื่อคำขอพร้อมกัน ⇒ ตัวตรวจแยกสาเหตุ</para>
/// </summary>
public class AuditHashChainTests
{
    private static readonly Guid Company = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>เวลามีหลัก 100ns ไม่เป็นศูนย์ — แบบ DateTime.UtcNow จริง (เวลาวินาทีเต็มซ่อนบั๊ก round-trip)</summary>
    private static AuditLog Row(int i, AuditAction action = AuditAction.Update) => new()
    {
        Id = i,
        CompanyId = Company,
        UserId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        UserEmail = $"user{i}@example.com",
        Action = action,
        EntityType = "Document",
        EntityId = $"doc-{i}",
        OldValues = $"{{\"amount\":{i * 100}}}",
        NewValues = $"{{\"amount\":{i * 200}}}",
        Timestamp = new DateTime(2026, 9, 24, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234567 + i),
    };

    /// <summary>สิ่งที่ PostgreSQL เก็บ + Npgsql (legacy) คืนมา: ตัดเหลือไมโครวินาที · Kind=Unspecified (<b>จำลอง</b>)</summary>
    private static DateTime PgRoundTrip(DateTime t)
        => DateTime.SpecifyKind(new DateTime(t.Ticks - t.Ticks % 10), DateTimeKind.Unspecified);

    private static AuditLog ReadBack(AuditLog r) => new()
    {
        Id = r.Id, CompanyId = r.CompanyId, UserId = r.UserId, UserEmail = r.UserEmail, Action = r.Action,
        EntityType = r.EntityType, EntityId = r.EntityId, OldValues = r.OldValues, NewValues = r.NewValues,
        Timestamp = PgRoundTrip(r.Timestamp), PrevHash = r.PrevHash, RowHash = r.RowHash,
    };

    /// <summary><b>สเปกที่แช่แข็ง</b>ของสูตร v1 (ก่อนรอบ 193) — สำเนาตั้งใจในเทสต์: ตัวจริงใน Helpers เป็น private แล้ว
    /// (เส้นเขียนต้องเรียกไม่ได้) · ใช้สร้าง "แถวเก่า" ให้ตัวตรวจ legacy เท่านั้น</summary>
    private static string FrozenV1Hash(AuditLog r) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{r.Timestamp:O}|{r.UserId}|{r.UserEmail}|{(int)r.Action}|{r.EntityType}|{r.EntityId}|{r.NewValues}|{r.OldValues}|{r.PrevHash}")));

    /// <summary>ประทับแบบเดียวกับ ApplyAuditHashChain (ResolveTip + Seal ทีละแถว) แล้วอ่านกลับ</summary>
    private static List<AuditLog> SealChain(int count)
    {
        var pending = new List<AuditLog>();
        for (var i = 1; i <= count; i++)
        {
            var r = Row(i);
            AuditHashChain.Seal(r, AuditHashChain.ResolveTip(pending, null));
            pending.Add(r);
        }
        return pending.Select(ReadBack).ToList();
    }

    private static List<AuditLog> V1Chain(int count)
    {
        var rows = new List<AuditLog>();
        string? prev = null;
        for (var i = 1; i <= count; i++)
        {
            var r = Row(i);
            r.PrevHash = prev;
            r.RowHash = FrozenV1Hash(r);
            prev = r.RowHash;
            rows.Add(ReadBack(r));
        }
        return rows;
    }

    // ════════ round-trip (สูตร v2) ════════

    [Fact]
    public void V1_ค่าที่อ่านกลับจากฐาน_ไม่ตรงสูตรเดิมตรง_ๆ_นี่คือบั๊กที่ทำให้ตรวจไม่ผ่านทั้งระบบ()
    {
        var r = Row(1);
        r.RowHash = FrozenV1Hash(r);
        Assert.NotEqual(r.RowHash, FrozenV1Hash(ReadBack(r)));
    }

    [Fact]
    public void V2_เขียนแล้วอ่านกลับ_ตรวจผ่านทั้ง_chain()
    {
        var back = SealChain(5);
        var a = AuditHashChain.Analyze(back);
        Assert.False(a.HasIntegrityFindings);
        Assert.Equal(0, a.ForkCount);
        Assert.Null(AuditHashChain.AlertMessage(a));
        Assert.All(back, r => Assert.StartsWith(AuditHashChain.V2Prefix, r.RowHash));
    }

    [Fact]
    public void V2_เวลาที่อ่านกลับเป็น_Local_ก็ยังตรวจผ่าน()
    {
        var rows = SealChain(3);
        foreach (var r in rows)
            r.Timestamp = DateTime.SpecifyKind(r.Timestamp, DateTimeKind.Utc).ToLocalTime();
        Assert.False(AuditHashChain.Analyze(rows).HasIntegrityFindings);
    }

    [Fact]
    public void V2_Seal_ตั้งเวลาที่เก็บ_เท่ากับเวลาที่_hash()
    {
        var r = Row(1);
        AuditHashChain.Seal(r, null);
        Assert.Equal(DateTimeKind.Utc, r.Timestamp.Kind);
        Assert.Equal(0, r.Timestamp.Ticks % 10);
        Assert.Equal(r.Timestamp.Ticks, ReadBack(r).Timestamp.Ticks);
    }

    [Fact]
    public void เปลี่ยน_Action_OldValues_PrevHash_ต้องเปลี่ยน_hash()
    {
        var baseRow = Row(1);
        AuditHashChain.Seal(baseRow, null);
        var byAction = Row(1, AuditAction.Delete); AuditHashChain.Seal(byAction, null);
        var byOld = Row(1); byOld.OldValues = "{\"amount\":0}"; AuditHashChain.Seal(byOld, null);
        var byPrev = Row(1); AuditHashChain.Seal(byPrev, "ABCDEF");
        Assert.NotEqual(baseRow.RowHash, byAction.RowHash);
        Assert.NotEqual(baseRow.RowHash, byOld.RowHash);
        Assert.NotEqual(baseRow.RowHash, byPrev.RowHash);
    }

    // ════════ ถูกแก้ (tampered) — รายงานทุกแถว ════════

    [Fact]
    public void แก้ค่าหลังอ่านกลับ_จับได้ที่แถวนั้น_และรายงานทุกแถวไม่ใช่แค่แถวแรก()
    {
        var back = SealChain(6);
        back[1].NewValues = "{\"amount\":1}";
        back[4].EntityId = "doc-อื่น";
        var a = AuditHashChain.Analyze(back);
        Assert.Equal(new long[] { 2, 5 }, a.Tampered.Select(r => r.Id).ToArray());
        Assert.Empty(a.Dangling);
        var msg = AuditHashChain.AlertMessage(a)!;
        Assert.Contains("#2", msg);
        Assert.Contains("#5", msg);
        Assert.Contains("ถูกแก้หลังบันทึก", msg);
        Assert.DoesNotContain("raw SQL", msg);   // ไม่อ้างวิธีที่ตรวจไม่ได้ (F2 ข้อ 7)
    }

    [Fact]
    public void แก้เวลาแม้แต่ไมโครวินาทีเดียว_จับได้()
    {
        var back = SealChain(3);
        back[1].Timestamp = back[1].Timestamp.AddTicks(10);
        Assert.Equal(new long[] { 2 }, AuditHashChain.Analyze(back).Tampered.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void แก้แล้วประทับ_RowHash_ใหม่_จับได้จากแถวถัดไปที่ขาดตอน()
    {
        // ผู้แก้ที่รู้สูตรประทับแถวตัวเองใหม่ ⇒ แถวตัวเองผ่าน แต่แถวถัดไปชี้ hash เดิมที่ไม่มีใครถืออยู่แล้ว
        var back = SealChain(4);
        back[1].NewValues = "{\"amount\":777}";
        AuditHashChain.Seal(back[1], back[1].PrevHash);
        var a = AuditHashChain.Analyze(back);
        Assert.Empty(a.Tampered);
        Assert.Equal(new long[] { 3 }, a.Dangling.Select(r => r.Id).ToArray());
        // P4-6 (ฝ่ายค้านรอบสี่): กรณีนี้คือ "แก้แล้วประทับใหม่" ไม่ใช่ "ลบ" — ตรวจแยกไม่ได้ ข้อความต้องครอบทั้งสองและไม่ฟันธงว่า "ถูกลบ"
        var msg = AuditHashChain.AlertMessage(a)!;
        Assert.Contains("ขาดตอน", msg);
        Assert.Contains("ถูกแก้แล้วประทับ hash ใหม่", msg);
        Assert.DoesNotContain("(ถูกลบ)", msg);
    }

    // ════════ ขาดตอน (dangling) ════════

    [Fact]
    public void ลบแถวกลางออก_จับได้เป็นขาดตอนที่แถวถัดไป()
    {
        var back = SealChain(5);
        back.RemoveAt(2);                                // ลบ #3
        var a = AuditHashChain.Analyze(back);
        Assert.True(a.HasIntegrityFindings);
        // ทิศตรงข้ามของ P4-6: กรณีลบจริงก็ต้องยังถูกครอบด้วยถ้อยคำเดียวกัน (ไม่ใช่ถอดคำว่า "ถูกลบ" ทิ้ง)
        Assert.Contains("ถูกลบ", AuditHashChain.AlertMessage(a));
        Assert.Equal(new long[] { 4 }, a.Dangling.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void ลบแถวแรกออก_จับได้()
    {
        var back = SealChain(3);
        back.RemoveAt(0);
        Assert.Equal(new long[] { 2 }, AuditHashChain.Analyze(back).Dangling.Select(r => r.Id).ToArray());
    }

    // ════════ แตกกิ่ง (fork) — ไม่ใช่การแก้ ════════

    [Fact]
    public void สองคำขอพร้อมกันอ่านปลายเดียวกัน_เป็น_fork_ไม่ใช่_tamper_และไม่แจ้งลูกค้า()
    {
        // W2-C1: คำขอ A และ B ของบริษัทเดียวกันอ่านปลาย chain (#1) พร้อมกัน ⇒ ทั้งคู่ได้ PrevHash = #1 · คำขอถัดไปต่อจาก Id สูงสุด
        var r1 = Row(1); AuditHashChain.Seal(r1, null);
        var a2 = Row(2); AuditHashChain.Seal(a2, r1.RowHash);
        var b3 = Row(3); AuditHashChain.Seal(b3, r1.RowHash);
        var c4 = Row(4); AuditHashChain.Seal(c4, b3.RowHash);
        var a = AuditHashChain.Analyze(new[] { r1, a2, b3, c4 }.Select(ReadBack).ToList());
        Assert.Equal(1, a.ForkCount);
        Assert.False(a.HasIntegrityFindings);
        Assert.Null(AuditHashChain.AlertMessage(a));
    }

    [Fact]
    public void Fork_ไม่บังการแก้จริงที่เกิดทีหลัง()
    {
        // เดิมตัวตรวจหยุดที่แถวแรกที่ "พัง" (= จุด fork) ทุกสัปดาห์ ⇒ การแก้จริงหลังจุดนั้นไม่เคยถูกรายงาน
        var r1 = Row(1); AuditHashChain.Seal(r1, null);
        var a2 = Row(2); AuditHashChain.Seal(a2, r1.RowHash);
        var b3 = Row(3); AuditHashChain.Seal(b3, r1.RowHash);
        var c4 = Row(4); AuditHashChain.Seal(c4, b3.RowHash);
        var rows = new[] { r1, a2, b3, c4 }.Select(ReadBack).ToList();
        rows[3].NewValues = "{\"amount\":-1}";
        var a = AuditHashChain.Analyze(rows);
        Assert.Equal(1, a.ForkCount);
        Assert.Equal(new long[] { 4 }, a.Tampered.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void แถวแรกพร้อมกันสองแถว_นับเป็น_fork()
    {
        var x = Row(1); AuditHashChain.Seal(x, null);
        var y = Row(2); AuditHashChain.Seal(y, null);
        var a = AuditHashChain.Analyze(new[] { x, y }.Select(ReadBack).ToList());
        Assert.Equal(1, a.ForkCount);
        Assert.False(a.HasIntegrityFindings);
    }

    [Fact]
    public void ลำดับ_Id_สลับกับลำดับประทับ_ไม่ถูกฟ้อง()
    {
        // W2-P2: EF ไม่รับประกันลำดับ INSERT ในหนึ่ง SaveChanges ⇒ ตัวตรวจต้องไม่พึ่งลำดับ Id
        var back = SealChain(4);
        (back[1].Id, back[2].Id) = (back[2].Id, back[1].Id);
        back.Reverse();
        var a = AuditHashChain.Analyze(back);
        Assert.False(a.HasIntegrityFindings);
        Assert.Equal(0, a.ForkCount);
    }

    // ════════ แถวเก่า (v1) ════════

    [Fact]
    public void V1_แถวเก่าที่อ่านกลับ_ตรวจผ่านแบบ_legacy_ไม่ต้องเขียน_hash_ทับ()
    {
        Assert.False(AuditHashChain.Analyze(V1Chain(4)).HasIntegrityFindings);
    }

    [Fact]
    public void V1_แถวเก่าที่ถูกแก้_ยังจับได้()
    {
        var rows = V1Chain(3);
        rows[1].EntityId = "doc-อื่น";
        Assert.Equal(new long[] { 2 }, AuditHashChain.Analyze(rows).Tampered.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Chain_ผสม_v1_แล้วต่อด้วย_v2_ตรวจผ่าน()
    {
        var old = V1Chain(2);
        var neu = Row(3);
        AuditHashChain.Seal(neu, old[^1].RowHash);
        old.Add(ReadBack(neu));
        Assert.False(AuditHashChain.Analyze(old).HasIntegrityFindings);
    }

    [Fact]
    public void ปลอมป้ายรุ่น_v2_บนแถว_v1_ไม่ผ่าน()
    {
        var r = Row(1);
        r.RowHash = AuditHashChain.V2Prefix + FrozenV1Hash(r);
        Assert.False(AuditHashChain.VerifyRow(ReadBack(r)));
    }

    // ════════ ปลาย chain ของแถวที่รอบันทึก ════════

    [Fact]
    public void ResolveTip_แถวที่ยังรอบันทึก_ชนะแถวในฐาน()
    {
        var a = Row(1); AuditHashChain.Seal(a, "DBTIP");
        var b = Row(2); AuditHashChain.Seal(b, a.RowHash);
        Assert.Equal(b.RowHash, AuditHashChain.ResolveTip(new[] { a, b }, "DBTIP"));
    }

    [Fact]
    public void ResolveTip_ไม่พึ่งลำดับของ_ChangeTracker()
    {
        var a = Row(1); AuditHashChain.Seal(a, "DBTIP");
        var b = Row(2); AuditHashChain.Seal(b, a.RowHash);
        Assert.Equal(b.RowHash, AuditHashChain.ResolveTip(new[] { b, a }, "DBTIP"));
    }

    [Fact]
    public void ResolveTip_ไม่มีแถวรอบันทึก_ใช้แถวล่าสุดในฐาน()
    {
        Assert.Equal("DBTIP", AuditHashChain.ResolveTip(Array.Empty<AuditLog>(), "DBTIP"));
        Assert.Null(AuditHashChain.ResolveTip(Array.Empty<AuditLog>(), null));
        var unsealed = Row(9);                       // แถวที่ AuditLogs.Add ตรง (RowHash=null) ไม่ใช่ปลาย chain
        Assert.Equal("DBTIP", AuditHashChain.ResolveTip(new[] { unsealed }, "DBTIP"));
    }

    [Fact]
    public void เพิ่มสองแถวก่อนบันทึก_ต้องไม่แตกกิ่ง()
    {
        var back = SealChain(2);
        Assert.Equal(back[0].RowHash, back[1].PrevHash);
        Assert.Equal(0, AuditHashChain.Analyze(back).ForkCount);
    }
}

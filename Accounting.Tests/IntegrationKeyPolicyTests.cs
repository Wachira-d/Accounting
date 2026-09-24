using System;
using System.Linq;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **คีย์ integration (int_) ต้องทำได้แค่ที่เจ้าของให้ และสวมได้แค่คนที่เจ้าของผูก** (ผลตรวจ G2-01 · คำตัดสินข้อ 37 · รอบ 193)
///
/// ═══ บั๊กที่ล็อกไว้ ═══
/// <para><c>ApiKeyMiddleware</c> ตั้ง <c>ApiKeyCanRead/Write/Delete = true</c> ตายตัวให้คีย์ int_ ทุกดอก ⇒
/// <c>ApiKeyScopeFilter</c> ไม่กันอะไร · และ <c>X-Acting-User: owner@…</c> จับคู่อีเมลแล้วได้ NameIdentifier ของเจ้าของ
/// ⇒ พนักงาน "ดูอย่างเดียว" ออกคีย์เอง แล้วลบเอกสาร/จ่ายเงินเดือนในนามเจ้าของได้</para>
///
/// <para>ครึ่งแรก = ใบที่พังกลับมาถูก (คีย์ใหม่อ่านอย่างเดียว · อีเมลเจ้าของสวมไม่ได้) ·
/// ครึ่งหลัง = ทิศตรงข้าม: คีย์รุ่นเก่า (TakeTime) ต้อง<b>ทำงานเหมือนเดิมทุกอย่าง</b>จนถึงวันเลิกใช้
/// (คำตัดสินเจ้าของ: "ไม่ทำ TakeTime พัง") — เทสต์ที่มีแต่ครึ่งแรกผ่านได้ทั้งตอนแก้ถูกและตอน "ปิดทุกคีย์ทิ้ง"</para>
/// </summary>
public class IntegrationKeyPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Deadline = Now.AddDays(IntegrationKeyPolicy.LegacyGraceDays);

    // ════════ ครึ่งแรก: คีย์ใหม่ = นโยบายใหม่ ════════

    [Fact]
    public void คีย์ใหม่ที่ไม่ได้เลือกสิทธิ์_ได้อ่านอย่างเดียว_ไม่ใช่ทั้งหมด()
    {
        var s = IntegrationKeyPolicy.ScopesForNewKey(null, null, null);
        Assert.Equal(IntegrationKeyScopes.ReadOnly, s);
        Assert.True(s.CanRead);
        Assert.False(s.CanWrite);
        Assert.False(s.CanDelete);
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    [InlineData("OPTIONS", true)]
    [InlineData("POST", false)]
    [InlineData("PUT", false)]
    [InlineData("PATCH", false)]
    [InlineData("DELETE", false)]
    public void คีย์ใหม่อ่านอย่างเดียว_ทำได้เฉพาะ_GET(string method, bool allowed)
    {
        var eff = IntegrationKeyPolicy.EffectiveScopes(false, null, IntegrationKeyScopes.ReadOnly, Now);
        Assert.Equal(allowed, IntegrationKeyPolicy.Allows(eff, method));
    }

    [Fact]
    public void เขียนได้_ไม่ได้แปลว่าลบได้()
    {
        var s = IntegrationKeyPolicy.ScopesForNewKey(true, true, null);
        Assert.True(IntegrationKeyPolicy.Allows(s, "POST"));
        Assert.True(IntegrationKeyPolicy.Allows(s, "PUT"));
        Assert.False(IntegrationKeyPolicy.Allows(s, "DELETE"));
    }

    [Fact]
    public void method_ที่ไม่รู้จัก_นับเป็นเขียน_fail_closed()
    {
        Assert.Equal(ApiKeyScope.Write, IntegrationKeyPolicy.RequiredScope("PROPFIND"));
        Assert.Equal(ApiKeyScope.Write, IntegrationKeyPolicy.RequiredScope(null));
        Assert.False(IntegrationKeyPolicy.Allows(IntegrationKeyScopes.ReadOnly, "PROPFIND"));
    }

    [Fact]
    public void คีย์ใหม่ส่งอีเมลเจ้าของมา_ต้องไม่สวมเป็นเจ้าของ_แม้หา_email_match_เจอ()
    {
        var owner = Guid.NewGuid();
        // ต่อให้ผู้เรียกเผลอหา email match มาให้ ตัวตัดสินต้องไม่ใช้ (attribution ≠ authorization)
        var d = IntegrationKeyPolicy.ResolveActingUser(true, null, legacyPrivilegeActive: false, owner);
        Assert.Null(d.UserId);
        Assert.Equal(ActingUserPath.Unmapped, d.Path);
        Assert.Equal("unmapped", IntegrationKeyPolicy.ResolvedHeaderValue(d.Path));
        // และ middleware ต้องไม่เสีย query ไปหาด้วยซ้ำ
        Assert.False(IntegrationKeyPolicy.ShouldTryLegacyEmailMatch(false, null, "owner@example.co.th"));
    }

    [Fact]
    public void คีย์ใหม่สวมได้เฉพาะผู้ใช้ที่เจ้าของผูกไว้()
    {
        var clerk = Guid.NewGuid();
        var d = IntegrationKeyPolicy.ResolveActingUser(true, clerk, legacyPrivilegeActive: false, null);
        Assert.Equal(clerk, d.UserId);
        Assert.Equal(ActingUserPath.Mapping, d.Path);
    }

    [Fact]
    public void คีย์รุ่นเก่าที่ไม่รู้วันเลิกใช้_ไม่ได้สิทธิ์ผ่อนผัน_fail_closed()
    {
        Assert.False(IntegrationKeyPolicy.IsLegacyPrivilegeActive(true, null, Now));
        var eff = IntegrationKeyPolicy.EffectiveScopes(true, null, IntegrationKeyScopes.ReadOnly, Now);
        Assert.False(IntegrationKeyPolicy.Allows(eff, "POST"));
    }

    [Fact]
    public void คีย์รุ่นเก่าหลังวันเลิกใช้_เหลือสิทธิ์ที่เก็บไว้_และสวมด้วยอีเมลไม่ได้()
    {
        var after = Deadline.AddSeconds(1);
        Assert.False(IntegrationKeyPolicy.IsLegacyPrivilegeActive(true, Deadline, after));
        var eff = IntegrationKeyPolicy.EffectiveScopes(true, Deadline, IntegrationKeyScopes.ReadOnly, after);
        Assert.True(IntegrationKeyPolicy.Allows(eff, "GET"));
        Assert.False(IntegrationKeyPolicy.Allows(eff, "POST"));
        Assert.False(IntegrationKeyPolicy.Allows(eff, "DELETE"));

        var d = IntegrationKeyPolicy.ResolveActingUser(true, null, legacyPrivilegeActive: false, Guid.NewGuid());
        Assert.Null(d.UserId);
        // ขอบวัน: เท่ากับวันเลิกใช้พอดี = หมดแล้ว
        Assert.False(IntegrationKeyPolicy.IsLegacyPrivilegeActive(true, Deadline, Deadline));
    }

    [Fact]
    public void เจ้าของแก้สิทธิ์คีย์_ย้ายเข้านโยบายใหม่_ช่องที่ไม่ส่งคงค่าเดิม()
    {
        var (s, changed) = IntegrationKeyPolicy.ApplyScopeUpdate(IntegrationKeyScopes.ReadOnly, null, true, null);
        Assert.True(changed);
        Assert.Equal(new IntegrationKeyScopes(true, true, false), s);
    }

    // ════════ ครึ่งหลัง (ทิศตรงข้าม): คีย์รุ่นเก่า/ของที่ถูกอยู่แล้ว ต้องไม่ถูกแตะ ════════

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void คีย์รุ่นเก่าในช่วงผ่อนผัน_ทำได้ทุกอย่างเหมือนเดิม_TakeTime_ไม่พัง(string method)
    {
        // ค่าที่ migration เก็บให้แถวเดิม = อ่านอย่างเดียว แต่ช่วงผ่อนผันต้องให้สิทธิ์เต็ม
        var eff = IntegrationKeyPolicy.EffectiveScopes(true, Deadline, IntegrationKeyScopes.ReadOnly, Now);
        Assert.Equal(IntegrationKeyScopes.Full, eff);
        Assert.True(IntegrationKeyPolicy.Allows(eff, method));
    }

    [Fact]
    public void คีย์รุ่นเก่าในช่วงผ่อนผัน_สวมด้วยอีเมลได้เหมือนเดิม_แต่ติดป้ายให้ทิ้งร่องรอย()
    {
        var user = Guid.NewGuid();
        Assert.True(IntegrationKeyPolicy.ShouldTryLegacyEmailMatch(true, null, "staff@example.co.th"));
        var d = IntegrationKeyPolicy.ResolveActingUser(true, null, legacyPrivilegeActive: true, user);
        Assert.Equal(user, d.UserId);
        Assert.Equal(ActingUserPath.LegacyEmailMatch, d.Path);
        Assert.Equal("legacy-email-match", IntegrationKeyPolicy.ResolvedHeaderValue(d.Path));
    }

    [Fact]
    public void แถวที่ผูกไว้ชนะ_email_match_เสมอ_แม้คีย์รุ่นเก่า()
    {
        var mapped = Guid.NewGuid();
        var byEmail = Guid.NewGuid();
        var d = IntegrationKeyPolicy.ResolveActingUser(true, mapped, legacyPrivilegeActive: true, byEmail);
        Assert.Equal(mapped, d.UserId);
        Assert.Equal(ActingUserPath.Mapping, d.Path);
        Assert.False(IntegrationKeyPolicy.ShouldTryLegacyEmailMatch(true, mapped, "x@example.co.th"));
        // รหัสภายนอกที่ไม่ใช่อีเมล ไม่ต้องเสีย query email match
        Assert.False(IntegrationKeyPolicy.ShouldTryLegacyEmailMatch(true, null, "EMP-001"));
    }

    [Fact]
    public void ไม่ส่ง_X_Acting_User_ไม่สวมใคร_ทั้งสองนโยบาย()
    {
        Assert.Equal(new ActingUserDecision(null, ActingUserPath.None),
            IntegrationKeyPolicy.ResolveActingUser(false, null, true, null));
        Assert.Equal(new ActingUserDecision(null, ActingUserPath.None),
            IntegrationKeyPolicy.ResolveActingUser(false, null, false, null));
    }

    [Fact]
    public void คีย์ใหม่ที่เจ้าของให้สิทธิ์ครบ_ทำได้ครบ_ด่านไม่เข้มเกินที่ตั้ง()
    {
        var s = IntegrationKeyPolicy.ScopesForNewKey(true, true, true);
        var eff = IntegrationKeyPolicy.EffectiveScopes(false, null, s, Now);
        Assert.True(IntegrationKeyPolicy.Allows(eff, "GET"));
        Assert.True(IntegrationKeyPolicy.Allows(eff, "POST"));
        Assert.True(IntegrationKeyPolicy.Allows(eff, "DELETE"));
    }

    [Fact]
    public void แก้ชื่อ_เปิดปิดคีย์_โดยไม่ส่งสิทธิ์_ต้องไม่แตะสิทธิ์และไม่ถอดป้ายรุ่นเก่า()
    {
        var (s, changed) = IntegrationKeyPolicy.ApplyScopeUpdate(IntegrationKeyScopes.ReadOnly, null, null, null);
        Assert.False(changed);
        Assert.Equal(IntegrationKeyScopes.ReadOnly, s);
    }

    [Theory]
    [InlineData("get", ApiKeyScope.Read)]
    [InlineData("Delete", ApiKeyScope.Delete)]
    [InlineData("post", ApiKeyScope.Write)]
    public void ตาราง_method_ต่อสิทธิ์_ตรงกับ_ApiKeyScopeFilter_เดิม(string method, ApiKeyScope expected)
        => Assert.Equal(expected, IntegrationKeyPolicy.RequiredScope(method));

    [Fact]
    public void ชื่อสิทธิ์ใน_403_ตรงกับ_key_ที่_middleware_เขียนลง_HttpContext_Items()
    {
        // ApiKeyScopeFilter อ่าน Items["ApiKey" + ScopeName] — middleware เขียน ApiKeyCanRead/Write/Delete
        Assert.Equal("CanRead", IntegrationKeyPolicy.ScopeName(ApiKeyScope.Read));
        Assert.Equal("CanWrite", IntegrationKeyPolicy.ScopeName(ApiKeyScope.Write));
        Assert.Equal("CanDelete", IntegrationKeyPolicy.ScopeName(ApiKeyScope.Delete));
    }

    // ════════ migration: idempotent + คีย์ใหม่ไม่ถูกติดป้ายรุ่นเก่าย้อนหลัง ════════

    [Fact]
    public void migration_ทุกคำสั่ง_ADD_เป็น_IF_NOT_EXISTS_และ_UPDATE_มีตัวกันรันซ้ำ()
    {
        var sql = IntegrationKeyPolicy.MigrationStatements();
        Assert.NotEmpty(sql);
        foreach (var s in sql.Where(x => x.Contains("ADD COLUMN")))
            Assert.Contains("ADD COLUMN IF NOT EXISTS", s);
        var update = Assert.Single(sql, x => x.StartsWith("UPDATE"));
        // รันซ้ำต้องไม่เลื่อนวันเลิกใช้ และต้องไม่แตะคีย์ที่ไม่ใช่รุ่นเก่า
        Assert.Contains("\"LegacyDeprecatesAt\" IS NULL", update);
        Assert.Contains("\"IsLegacyKey\" = true", update);
        Assert.Contains($"interval '{IntegrationKeyPolicy.LegacyGraceDays} days'", update);
    }

    [Fact]
    public void migration_แถวเดิมเป็นรุ่นเก่า_แต่_default_หลังจากนั้นเป็น_false()
    {
        var sql = IntegrationKeyPolicy.MigrationStatements().ToList();
        var add = sql.FindIndex(x => x.Contains("ADD COLUMN IF NOT EXISTS \"IsLegacyKey\" boolean NOT NULL DEFAULT true"));
        var flip = sql.FindIndex(x => x.Contains("ALTER COLUMN \"IsLegacyKey\" SET DEFAULT false"));
        var stamp = sql.FindIndex(x => x.StartsWith("UPDATE"));
        Assert.True(add >= 0 && flip > add && stamp > flip);
        // สิทธิ์ที่เก็บของแถวเดิม = อ่านอย่างเดียว (ค่าหลังช่วงผ่อนผัน) — ช่วงผ่อนผันให้ผ่าน EffectiveScopes
        Assert.Contains(sql, x => x.Contains("\"CanRead\" boolean NOT NULL DEFAULT true"));
        Assert.Contains(sql, x => x.Contains("\"CanWrite\" boolean NOT NULL DEFAULT false"));
        Assert.Contains(sql, x => x.Contains("\"CanDelete\" boolean NOT NULL DEFAULT false"));
    }
}

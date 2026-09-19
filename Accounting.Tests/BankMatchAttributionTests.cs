using System;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ป้าย "ใครจับคู่ให้" (`DECISION_AUDIT_2026-09-18.md` §3 D4-8 "ป้ายโกหก").
///
/// สองครึ่ง: (ก) ค่าที่เขียนใหม่ต้องได้ป้ายที่ซื่อสัตย์ ·
/// (ข) **ค่าเดิมที่อยู่ในฐานแล้วต้องอ่านได้เหมือนเดิม** — เปลี่ยนความหมาย
/// ย้อนหลังของแถวเก่าคือการโกหกอีกแบบหนึ่ง
/// </summary>
public class BankMatchAttributionTests
{
    private static readonly Guid U = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    // ── (ก) ค่าที่เขียนใหม่ ───────────────────────────────────────────
    [Fact]
    public void คนกดยืนยันเอง_ได้ป้ายคนพร้อมชื่อ()
    {
        var a = BankMatchAttribution.Describe(BankMatchAttribution.Person(U), "สมชาย ใจดี");
        Assert.Equal(BankMatchActorKind.Person, a.Kind);
        Assert.Contains("สมชาย ใจดี", a.Label);
        Assert.DoesNotContain("AI", a.Label);
    }

    [Fact]
    public void คนยืนยันคำแนะนำAI_ยังเป็นคนแต่บอกว่าAIเสนอ()
    {
        var a = BankMatchAttribution.Describe(
            BankMatchAttribution.Person(U, aiAssisted: true), "สมชาย ใจดี");
        Assert.Equal(BankMatchActorKind.Person, a.Kind);
        Assert.Contains("สมชาย ใจดี", a.Label);
        Assert.Contains("AI", a.Label);
    }

    [Fact]
    public void ค้นชื่อไม่เจอ_ห้ามแต่งชื่อ()
    {
        var a = BankMatchAttribution.Describe(BankMatchAttribution.Person(U), null);
        Assert.Equal(BankMatchActorKind.Person, a.Kind);
        Assert.Equal("👤 ผู้ใช้", a.Label);
    }

    [Fact]
    public void เซิร์ฟเวอร์คำนวณเอง_ต้องเป็นระบบไม่ใช่AI()
    {
        var apply = BankMatchAttribution.Describe(BankMatchAttribution.AutoMatch);
        Assert.Equal(BankMatchActorKind.System, apply.Kind);
        Assert.False(apply.IsSuggestionOnly);
        Assert.DoesNotContain("🤖", apply.Label);

        var suggest = BankMatchAttribution.Describe(BankMatchAttribution.AutoMatchSuggested);
        Assert.Equal(BankMatchActorKind.System, suggest.Kind);
        Assert.True(suggest.IsSuggestionOnly);
    }

    [Fact]
    public void เรียกproviderจริง_ถึงจะได้ป้ายAI()
    {
        var a = BankMatchAttribution.Describe(BankMatchAttribution.BankFeedAiSuggested);
        Assert.Equal(BankMatchActorKind.Ai, a.Kind);
        Assert.Contains("🤖", a.Label);
        Assert.True(a.IsSuggestionOnly);
    }

    [Fact]
    public void ระบบภายนอกผ่านApi_แยกจากคนและจากระบบ()
    {
        var a = BankMatchAttribution.Describe(BankMatchAttribution.ApiV1);
        Assert.Equal(BankMatchActorKind.Api, a.Kind);
    }

    // ── (ข) ค่าเดิมในฐานต้องอ่านได้เหมือนเดิม ─────────────────────────
    [Fact]
    public void แถวเก่าที่เก็บGuidดิบ_ยังอ่านเป็นคน()
    {
        var a = BankMatchAttribution.Describe(U.ToString("D"), "สมหญิง");
        Assert.Equal(BankMatchActorKind.Person, a.Kind);
        Assert.Contains("สมหญิง", a.Label);
    }

    [Fact]
    public void แถวเก่าที่เก็บAIBatch_ยังอ่านเป็นAI_ไม่ใช่กลายเป็นไม่รู้()
    {
        // แถวที่บันทึกก่อนรอบนี้เขียน "AI-Batch" ไว้ — ความหมายเดิมคือ
        // "จับคู่เป็นชุดโดยมี AI ช่วย" ⇒ ต้องอ่านออกต่อไป ห้ามกลายเป็น Unknown
        var a = BankMatchAttribution.Describe("AI-Batch");
        Assert.Equal(BankMatchActorKind.Ai, a.Kind);
    }

    [Fact]
    public void แถวเก่าBankFeedและOpenBanking_ยังเป็นระบบ()
    {
        Assert.Equal(BankMatchActorKind.System, BankMatchAttribution.Describe("BankFeed").Kind);
        Assert.Equal(BankMatchActorKind.System, BankMatchAttribution.Describe("OpenBanking").Kind);
        Assert.Equal(BankMatchActorKind.System,
            BankMatchAttribution.Describe("OpenBanking (เสนอ รอยืนยัน)").Kind);
    }

    // ── "ไม่รู้" ต้องเป็นค่าใน enum ไม่ใช่เดาเป็นระบบ (G3) ─────────────
    [Fact]
    public void ว่างหรืออ่านไม่ออก_ต้องเป็นไม่รู้()
    {
        Assert.Equal(BankMatchActorKind.Unknown, BankMatchAttribution.Describe(null).Kind);
        Assert.Equal(BankMatchActorKind.Unknown, BankMatchAttribution.Describe("   ").Kind);
        Assert.Equal(BankMatchActorKind.Unknown, BankMatchAttribution.Describe("???").Kind);
        Assert.Equal("ไม่มีข้อมูล", BankMatchAttribution.Describe(null).Label);
    }

    [Fact]
    public void Guidศูนย์ไม่ใช่คน_ต้องไม่กลายเป็นผู้ใช้ปลอม()
    {
        // `Person(Guid.Empty)` เกิดตอนไม่มี JWT (งานเบื้องหลัง/API)
        var a = BankMatchAttribution.Describe(BankMatchAttribution.Person(Guid.Empty));
        Assert.Equal(BankMatchActorKind.Person, a.Kind);
        Assert.Equal("👤 ผู้ใช้", a.Label);   // ไม่มีชื่อที่แต่งขึ้น
    }
}

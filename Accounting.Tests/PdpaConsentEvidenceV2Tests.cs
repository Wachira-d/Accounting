using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 214 ต่อ (คำตัดสินข้อ 173 ฎ) — หลักฐานความยินยอมสูตร v2 ต้องตรวจกลับได้หลังค่าผ่าน PostgreSQL
/// (timestamp without time zone: Kind หาย · ความละเอียดเหลือไมโครวินาที) — เดิม v1 ใช้ ToString("O") ⇒ ตรวจไม่ผ่านตลอด = ไม่มี control ·
/// แถว v1 เก่าที่ตรวจไม่ได้ต้องรายงานเป็น legacy ไม่ใช่ "ผ่าน" · การแก้หลังบันทึกต้องจับได้ (ทิศตรงข้าม)
/// </summary>
public class PdpaConsentEvidenceV2Tests
{
    private const string Email = "somchai@example.com";
    private const string Purpose = "account-signup:terms-and-privacy";
    // 7 หลักของ tick (100ns) ⇒ หลักสุดท้ายหายเมื่อเก็บใน PostgreSQL
    private static readonly DateTime Written = new DateTime(2026, 10, 30, 3, 14, 15, DateTimeKind.Utc).AddTicks(1234567);

    /// <summary>จำลองค่าที่อ่านกลับจาก PostgreSQL: Kind Unspecified + ตัดเหลือไมโครวินาที</summary>
    private static DateTime RoundTrip(DateTime t) => DateTime.SpecifyKind(new DateTime(t.Ticks - t.Ticks % 10), DateTimeKind.Unspecified);

    [Fact]
    public void V2_verifies_after_postgres_round_trip()
    {
        var h = PdpaConsentEvidence.ComputeHash(Email, Purpose, "1.0", Written, "web-form", "203.0.113.7", "ua");
        Assert.Equal(PdpaConsentEvidence.EvidenceCheck.Valid,
            PdpaConsentEvidence.Check(h, Email, Purpose, "1.0", RoundTrip(Written), "web-form", "203.0.113.7", "ua"));
        Assert.True(PdpaConsentEvidence.Verify(h, Email, Purpose, "1.0", RoundTrip(Written), "web-form", "203.0.113.7", "ua"));
    }

    [Theory]
    [InlineData("email")]
    [InlineData("version")]
    [InlineData("time")]
    [InlineData("ip")]
    public void V2_tampered_field_is_invalid(string field)
    {
        var h = PdpaConsentEvidence.ComputeHash(Email, Purpose, "1.0", Written, "web-form", "203.0.113.7", "ua");
        var check = PdpaConsentEvidence.Check(h,
            field == "email" ? "x@example.com" : Email, Purpose,
            field == "version" ? "2.0" : "1.0",
            field == "time" ? RoundTrip(Written).AddSeconds(1) : RoundTrip(Written),
            "web-form", field == "ip" ? "198.51.100.1" : "203.0.113.7", "ua");
        Assert.Equal(PdpaConsentEvidence.EvidenceCheck.Invalid, check);
    }

    [Fact]
    public void Legacy_v1_rows_are_reported_never_silently_ok()
    {
        var v1 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            PdpaConsentEvidence.Canonical(Email, Purpose, "1.0", Written, "web-form", null, null))));
        // ค่าเวลาเต็มความละเอียด (ยังไม่ผ่านฐาน) — v1 ยังตรวจได้
        Assert.Equal(PdpaConsentEvidence.EvidenceCheck.Valid, PdpaConsentEvidence.Check(v1, Email, Purpose, "1.0", Written, "web-form", null, null));
        // ผ่านฐานแล้ว — ตรวจไม่ได้ ⇒ legacy (ไม่ใช่ Valid · ไม่กล่าวหาว่าถูกแก้)
        Assert.Equal(PdpaConsentEvidence.EvidenceCheck.LegacyUnverifiable,
            PdpaConsentEvidence.Check(v1, Email, Purpose, "1.0", RoundTrip(Written), "web-form", null, null));
        Assert.False(PdpaConsentEvidence.Verify(v1, Email, Purpose, "1.0", RoundTrip(Written), "web-form", null, null));
        Assert.Equal(PdpaConsentEvidence.EvidenceCheck.Missing, PdpaConsentEvidence.Check(null, Email, Purpose, "1.0", Written, null, null, null));
    }

    [Fact]
    public void NormalizeTimestamp_is_utc_microseconds_and_idempotent()
    {
        var n = PdpaConsentEvidence.NormalizeTimestamp(Written);
        Assert.Equal(DateTimeKind.Utc, n.Kind);
        Assert.Equal(0, n.Ticks % 10);
        Assert.Equal(n, PdpaConsentEvidence.NormalizeTimestamp(RoundTrip(Written)));
        Assert.Equal(n, PdpaConsentEvidence.NormalizeTimestamp(n));
    }

    [Fact]
    public void Acceptance_row_stores_every_canonical_field_and_verifies_from_the_row()
    {
        var longUa = new string('u', 900);
        var r = PlatformPolicyAcceptanceRules.Create(Guid.NewGuid(), Email, Purpose, "1.0", " 1.0 ", Written, "web-form", "203.0.113.7", longUa);
        Assert.Equal(0, r.AcceptedAt.Ticks % 10);                       // เก็บเวลาที่ normalize แล้ว (ค่าเดียวกับที่ hash)
        Assert.Equal(PlatformPolicyAcceptanceRules.MaxUserAgent, r.UserAgent!.Length);   // ตัดก่อน hash
        Assert.Equal("1.0", r.VersionShown);
        Assert.Equal(PdpaConsentEvidence.EvidenceCheck.Valid, PlatformPolicyAcceptanceRules.Check(r));
        r.AcceptedAt = RoundTrip(r.AcceptedAt);                         // อ่านกลับจากฐาน
        Assert.Equal(PdpaConsentEvidence.EvidenceCheck.Valid, PlatformPolicyAcceptanceRules.Check(r));
        r.PolicyVersion = "9.9";                                        // ไม่อยู่ใน canonical (ฉบับของระบบ) — ไม่กระทบ
        Assert.Equal(PdpaConsentEvidence.EvidenceCheck.Valid, PlatformPolicyAcceptanceRules.Check(r));
        r.VersionShown = "2.0";                                         // แก้ฉบับที่เห็น ⇒ จับได้
        Assert.Equal(PdpaConsentEvidence.EvidenceCheck.Invalid, PlatformPolicyAcceptanceRules.Check(r));
    }

    [Fact]
    public void Erasure_pseudonymises_but_keeps_legal_hold_row()
    {
        var r = PlatformPolicyAcceptanceRules.Create(Guid.NewGuid(), Email, Purpose, "1.0", "1.0", Written, "web-form", "203.0.113.7", "ua");
        var hash = r.EvidenceHash;
        var now = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        PlatformPolicyAcceptanceRules.Pseudonymise(r, "anon-1@deleted.local", now);
        Assert.Equal("anon-1@deleted.local", r.Email);
        Assert.Null(r.IpAddress);
        Assert.Null(r.UserAgent);
        Assert.Equal(hash, r.EvidenceHash);                              // หลักฐานคงอยู่
        Assert.Equal(now, r.PseudonymisedAt);
        Assert.Equal(PdpaConsentEvidence.EvidenceCheck.LegacyUnverifiable, PlatformPolicyAcceptanceRules.Check(r));
        PlatformPolicyAcceptanceRules.Pseudonymise(r, "anon-2@deleted.local", now.AddDays(1));
        Assert.Equal(now, r.PseudonymisedAt);                            // วันลบครั้งแรกไม่เลื่อน (นับ legal hold จากครั้งแรก)
        Assert.Equal(now.AddYears(-10), PlatformPolicyAcceptanceRules.PurgeCutoff(now));
        Assert.Contains("ตรวจซ้ำไม่ได้", PlatformPolicyAcceptanceRules.CheckLabel(PdpaConsentEvidence.EvidenceCheck.LegacyUnverifiable));
    }

    // ═══════ ฝ่ายค้านรอบ 214 (ฏ) ═══════

    [Fact]
    public void Prefix_is_case_insensitive()
    {
        var h = PdpaConsentEvidence.ComputeHash(Email, Purpose, "1.0", Written, "web-form", null, null);
        var upper = "V2:" + h[PdpaConsentEvidence.V2Prefix.Length..];
        Assert.Equal(PdpaConsentEvidence.EvidenceCheck.Valid, PdpaConsentEvidence.Check(upper, Email, Purpose, "1.0", Written, "web-form", null, null));
    }

    private static PdpaConsentRecord ShopRow()
    {
        var at = PdpaConsentEvidence.NormalizeTimestamp(Written);
        return new PdpaConsentRecord
        {
            SubjectContact = "guest@example.test", Purpose = "cms-order:personal-data", PolicyVersion = "abc", GrantedAt = at,
            Channel = "web-form:WEB-1",
            EvidenceHash = PdpaConsentEvidence.ComputeHash("guest@example.test", "cms-order:personal-data", "abc", at, "web-form:WEB-1", null, null),
            EvidenceSource = PdpaConsentEvidence.SourceSystem,
        };
    }

    /// <summary>S4/N2: แถวความยินยอมบนเว็บร้าน — ตรวจผ่าน · แก้แล้วไม่ตรง · ที่เจ้าหน้าที่กรอก hash เอง = บอกที่มา (ไม่ใช่ "ไม่ตรง") · ลบตามคำขอแล้ว = ตรวจซ้ำไม่ได้</summary>
    [Fact]
    public void Consent_record_labels_by_source_and_state()
    {
        var r = ShopRow();
        Assert.Equal(PlatformPolicyAcceptanceRules.CheckLabel(PdpaConsentEvidence.EvidenceCheck.Valid), PlatformPolicyAcceptanceRules.CheckLabel(r));
        r.GrantedAt = DateTime.SpecifyKind(r.GrantedAt, DateTimeKind.Unspecified);   // อ่านกลับจากฐาน — ยังผ่าน
        Assert.Equal(PlatformPolicyAcceptanceRules.CheckLabel(PdpaConsentEvidence.EvidenceCheck.Valid), PlatformPolicyAcceptanceRules.CheckLabel(r));
        r.SubjectContact = "other@example.test";
        Assert.Equal(PlatformPolicyAcceptanceRules.CheckLabel(PdpaConsentEvidence.EvidenceCheck.Invalid), PlatformPolicyAcceptanceRules.CheckLabel(r));
        var staff = ShopRow();
        staff.EvidenceHash = "ABC123";
        staff.EvidenceSource = PdpaConsentEvidence.SourceStaff;
        Assert.Equal(PlatformPolicyAcceptanceRules.StaffEnteredLabel, PlatformPolicyAcceptanceRules.CheckLabel(staff));
        var erased = ShopRow();
        PlatformPolicyAcceptanceRules.Pseudonymise(erased, "anon@deleted.local", Written);
        Assert.Equal("anon@deleted.local", erased.SubjectContact);
        Assert.Null(erased.IpAddress);
        Assert.NotNull(erased.PseudonymisedAt);
        Assert.Equal(PlatformPolicyAcceptanceRules.CheckLabel(PdpaConsentEvidence.EvidenceCheck.LegacyUnverifiable), PlatformPolicyAcceptanceRules.CheckLabel(erased));
    }
}

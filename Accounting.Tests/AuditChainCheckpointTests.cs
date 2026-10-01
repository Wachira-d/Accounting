using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม PL (A-PL3 · team-R H-2) — watermark ของงานตรวจ audit chain: ตรวจเป็นช่วงต้องไม่ทำให้ "ขาดตอน" หายเงียบ
/// และพบปัญหาต้องไม่ขยับ watermark (ทิศตรงข้าม: chain ปกติต้องขยับ · ตรวจเต็มเมื่อครบรอบ)
/// </summary>
public class AuditChainCheckpointTests
{
    private static readonly Guid Company = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime Now = new(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);

    private static List<AuditLog> Chain(int count)
    {
        var rows = new List<AuditLog>();
        string? prev = null;
        for (var i = 1; i <= count; i++)
        {
            var r = new AuditLog
            {
                Id = i, CompanyId = Company, UserEmail = "u@example.com", Action = AuditAction.Update,
                EntityType = "Document", EntityId = $"d{i}", NewValues = $"{{\"i\":{i}}}",
                Timestamp = new DateTime(2026, 9, 30, 1, 2, 3, DateTimeKind.Utc).AddTicks(1230 + i * 10),
            };
            AuditHashChain.Seal(r, prev);
            prev = r.RowHash;
            rows.Add(r);
        }
        return rows;
    }

    [Fact]
    public void No_checkpoint_means_full_verify()
    {
        var plan = AuditChainCheckpointPolicy.Decide(null, null, 0, Now);
        Assert.Equal(AuditChainCheckpointPolicy.Mode.Full, plan.Mode);
        Assert.Equal(0, plan.AfterId);
    }

    [Fact]
    public void Recent_clean_checkpoint_verifies_only_after_watermark()
    {
        var plan = AuditChainCheckpointPolicy.Decide(500, Now.AddDays(-7), 0, Now);
        Assert.Equal(AuditChainCheckpointPolicy.Mode.Incremental, plan.Mode);
        Assert.Equal(500, plan.AfterId);
    }

    [Fact]
    public void Full_verify_again_when_cycle_due_or_previous_run_found_problems()
    {
        Assert.Equal(AuditChainCheckpointPolicy.Mode.Full,
            AuditChainCheckpointPolicy.Decide(500, Now.AddDays(-28), 0, Now).Mode);
        Assert.Equal(AuditChainCheckpointPolicy.Mode.Full,
            AuditChainCheckpointPolicy.Decide(500, null, 0, Now).Mode);
        Assert.Equal(AuditChainCheckpointPolicy.Mode.Full,
            AuditChainCheckpointPolicy.Decide(500, Now.AddDays(-1), 2, Now).Mode);
    }

    [Fact]
    public void Findings_freeze_the_watermark_and_clean_runs_advance_it()
    {
        var plan = AuditChainCheckpointPolicy.Decide(500, Now.AddDays(-7), 0, Now);
        var broken = AuditChainCheckpointPolicy.Advance(plan, 500, Now.AddDays(-7), 900, integrityFindings: 3, Now);
        Assert.Equal(500, broken.LastVerifiedId);
        Assert.Equal(3, broken.FindingCount);
        Assert.False(broken.Advanced);

        var clean = AuditChainCheckpointPolicy.Advance(plan, 500, Now.AddDays(-7), 900, integrityFindings: 0, Now);
        Assert.Equal(900, clean.LastVerifiedId);
        Assert.True(clean.Advanced);
        Assert.Equal(Now.AddDays(-7), clean.LastFullVerifiedAt);   // ตรวจเป็นช่วงไม่นับเป็นตรวจเต็ม

        var full = AuditChainCheckpointPolicy.Decide(null, null, 0, Now);
        Assert.Equal(Now, AuditChainCheckpointPolicy.Advance(full, null, null, 900, 0, Now).LastFullVerifiedAt);
        // ไม่มีแถวใหม่ ⇒ watermark ไม่ถอยหลัง
        Assert.Equal(500, AuditChainCheckpointPolicy.Advance(plan, 500, Now.AddDays(-7), 0, 0, Now).LastVerifiedId);
    }

    [Fact]
    public void Window_with_proven_anchor_is_clean()
    {
        var rows = Chain(10);
        var window = rows.Where(r => r.Id > 6).ToList();
        var parents = AuditHashChain.ExternalParents(window);
        Assert.Single(parents);
        Assert.Equal(rows[5].RowHash, parents[0]);            // parent ของแถว #7 = แถว #6 (ก่อน watermark)
        var a = AuditHashChain.Analyze(window, parents);       // ผู้เรียกค้นแล้วว่ามีจริง
        Assert.False(a.HasIntegrityFindings);
        Assert.Equal(0, a.ForkCount);
    }

    [Fact]
    public void Window_without_anchor_still_reports_dangling()
    {
        // ทิศตรงข้าม: แถวก่อน watermark ถูกลบ (ค้นในฐานไม่เจอ) ⇒ ผู้เรียกส่ง anchors ว่าง ⇒ ต้องยังเห็น "ขาดตอน"
        var window = Chain(10).Where(r => r.Id > 6).ToList();
        var a = AuditHashChain.Analyze(window, Array.Empty<string>());
        Assert.Single(a.Dangling);
        Assert.Equal(7, a.Dangling[0].Id);
        Assert.NotNull(AuditHashChain.AlertMessage(a));
    }

    [Fact]
    public void Unchained_legacy_rows_are_reported_separately_not_as_tampered()
    {
        // คำตัดสินรอบ 201 (DV Q3): นับแยก "นอก chain รุ่นเก่า (ก่อนวันที่ X)" · ไม่ใช่ถูกแก้ · ไม่เติม hash ย้อนหลัง
        var note = AuditHashChain.UnchainedNote(12, new DateTime(2026, 9, 30, 17, 0, 0, DateTimeKind.Utc))!;
        Assert.Contains("นอก hash chain รุ่นเก่า 12 แถว", note);
        Assert.Contains("01/10/2026 00:00", note);                 // เวลาไทย
        Assert.Contains("ไม่ใช่หลักฐานว่าถูกแก้", note);
        Assert.Null(AuditHashChain.UnchainedNote(0, null));        // ทิศตรงข้าม: ไม่มีแถวนอก chain = ไม่มีข้อความ
    }

    [Fact]
    public void Scope_orphan_rows_join_the_single_tenant_of_the_batch()
    {
        // PL-X1/X6: แถวลูกไม่ผูกบริษัท (Empty) และ NULL ได้บริษัทของ batch — ไม่ใช้ล็อก/chain "บริษัทว่าง" ร่วมกับ tenant อื่น
        var rows = new List<AuditLog>
        {
            new() { CompanyId = Company, EntityType = "Document" },
            new() { CompanyId = Guid.Empty, EntityType = "DocumentLine" },
            new() { CompanyId = null, EntityType = "Http" },
        };
        AuditChainScope.Normalize(rows);
        Assert.All(rows, r => Assert.Equal(Company, r.CompanyId));
    }

    [Fact]
    public void Scope_platform_or_mixed_batches_stay_platform_level_and_never_null()
    {
        // ทิศตรงข้าม: ไม่มีบริษัท / หลายบริษัท ⇒ ไม่เดา — ว่างคงว่าง (ระดับแพลตฟอร์มจริง) · NULL กลายเป็น Empty (ตัวตรวจอ่านได้)
        var platform = new List<AuditLog> { new() { CompanyId = null, EntityType = "User" }, new() { CompanyId = Guid.Empty, EntityType = "User" } };
        AuditChainScope.Normalize(platform);
        Assert.All(platform, r => Assert.Equal(Guid.Empty, r.CompanyId));
        var other = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var mixed = new List<AuditLog>
        {
            new() { CompanyId = Company, EntityType = "A" }, new() { CompanyId = other, EntityType = "B" }, new() { CompanyId = null, EntityType = "C" },
        };
        AuditChainScope.Normalize(mixed);
        Assert.Equal(Company, mixed[0].CompanyId);
        Assert.Equal(other, mixed[1].CompanyId);
        Assert.Equal(Guid.Empty, mixed[2].CompanyId);
    }

    [Fact]
    public void Analyze_without_anchors_keeps_full_chain_behaviour()
    {
        var rows = Chain(5);
        Assert.False(AuditHashChain.Analyze(rows).HasIntegrityFindings);
        rows[2].NewValues = "{\"i\":999}";                    // แก้หลังบันทึก
        var a = AuditHashChain.Analyze(rows);
        Assert.Single(a.Tampered);
        Assert.Equal(3, a.Tampered[0].Id);
    }
}

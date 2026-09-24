using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบสอง 193 → ทีม S2: แกนของด่านสแกนใน <c>AttachmentAccessGate</c> ที่เดิมไม่มีเทสต์ (§5 A5/A6/A7 · R2-C1 · Q3 · Q4)
/// ครึ่งที่ 1 (ช่องที่ปิด): ไฟล์ของรายการอื่นทุกชนิดต้องผ่านด่านอ่านของรายการนั้นก่อนสแกน · ทิศเขียนผ่านหน้าไฟล์แนบใช้คีย์สร้างเอกสาร ·
/// สแกนพี่น้องเลือกเจ้าของแบบกำหนดได้แน่นอน · ไฟล์ของรายการอื่นใช้เป็นหลักฐาน 50 ทวิ ไม่ได้
/// ครึ่งที่ 2 (ทิศตรงข้าม): ลบเอกสารร่างที่สร้างจากสแกนแล้ว สแกนกลับเป็น "ยังไม่ผูก" — แก้/สร้างเอกสารใหม่/ย้ายไฟล์เข้าใบใหม่ได้ ·
/// เอกสารที่ยังอยู่ยังเป็นเจ้าของ (ไม่ถอยกลับไปเปิดกว้าง) · หน้ารีวิวแก้สแกนที่ยังไม่ผูกได้ระดับสมาชิกเหมือนเดิม
/// </summary>
public class AttachmentGateRound2Tests
{
    private static readonly Guid DeletedDraft = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid NewDoc = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LowDoc = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid HighDoc = Guid.Parse("ffffffff-0000-0000-0000-000000000000");
    private static readonly Guid Credit = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherCredit = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // ── R2-C1: เจ้าของไฟล์ที่ถูกลบ ≠ เจ้าของ ──

    [Fact]
    public void ลบร่างที่สร้างจากสแกนแล้ว_สแกนกลับเป็นยังไม่ผูก_ไม่ใช่404ของเอกสารที่ไม่มีแล้ว()
        => Assert.Null(AttachmentPermissionScope.ScanOwner("Document", DeletedDraft, null, createdDocumentExists: false,
            fileOwnerExists: false));

    [Fact]
    public void ลบร่างแล้ว_ย้ายไฟล์เข้าเอกสารใบใหม่ได้()
        => Assert.True(AttachmentPermissionScope.ScanFileRelinkable("Document", DeletedDraft, NewDoc, fileOwnerExists: false));

    [Fact]
    public void ทิศตรงข้าม_เอกสารเจ้าของยังอยู่_ยังเป็นเจ้าของและห้ามย้ายไฟล์ไปใบอื่น()
    {
        Assert.Equal(new ScanOwnerRef("Document", DeletedDraft),
            AttachmentPermissionScope.ScanOwner("Document", DeletedDraft, null, false, fileOwnerExists: true));
        Assert.False(AttachmentPermissionScope.ScanFileRelinkable("Document", DeletedDraft, NewDoc, fileOwnerExists: true));
    }

    [Fact]
    public void ไฟล์ของชนิดอื่น_ไม่ถูกปลดเจ้าของเพราะผลตรวจเอกสาร()
    {
        // เฉพาะไฟล์ของเอกสารที่ต้องตรวจว่าเจ้าของยังอยู่ (ร่างถูก hard-delete) · สลิปเงินเดือน ฯลฯ ด่านของชนิดนั้นตัดสินเอง
        Assert.True(AttachmentPermissionScope.ScanFileOwnerNeedsExistenceCheck("Document"));
        Assert.False(AttachmentPermissionScope.ScanFileOwnerNeedsExistenceCheck("PayrollRun"));
        Assert.False(AttachmentPermissionScope.ScanFileOwnerNeedsExistenceCheck("OcrScan"));
        Assert.False(AttachmentPermissionScope.ScanFileRelinkable("PayrollRun", DeletedDraft, NewDoc, fileOwnerExists: false));
    }

    [Fact]
    public void ลบร่างแล้ว_ลบสแกนทิ้งได้_และลบไฟล์ที่ไม่มีเจ้าของจริง()
        => Assert.Equal(ScanFileDisposalAction.Remove,
            OcrScanFileDisposal.Decide("Document", DeletedDraft, false, null, null, fileOwnerExists: false));

    // ── A6: สแกนไฟล์แนบ — ทุกชนิดที่ไม่ใช่ไฟล์ของสแกน ต้องผ่านด่านอ่านของเจ้าของไฟล์ ──

    [Theory]
    [InlineData("Document")]
    [InlineData("PayrollRun")]
    [InlineData("ExpenseClaim")]
    [InlineData("StatutoryRemittance")]
    [InlineData("ชนิดที่ไม่รู้จัก")]
    [InlineData(null)]
    public void สแกนไฟล์ของรายการอื่นทุกชนิด_ต้องผ่านด่านอ่านของเจ้าของไฟล์(string? type)
        => Assert.Equal(ScanSourceCheck.FileOwnerReadGate, AttachmentPermissionScope.ScanSourceGate(type));

    [Fact]
    public void ทิศตรงข้าม_ไฟล์ของสแกนเอง_ใช้ด่านของสแกนเดิม()
        => Assert.Equal(ScanSourceCheck.ExistingScanGate, AttachmentPermissionScope.ScanSourceGate(" ocrscan "));

    // ── A5: คีย์ของสแกนที่ยังไม่ผูก ──

    [Fact]
    public void ลบไฟล์สแกนผ่านหน้าไฟล์แนบ_ต้องใช้คีย์สร้างเอกสาร()
    {
        var rule = AttachmentPermissionScope.Resolve("OcrScan");
        Assert.NotEmpty(rule.WriteAnyOf);
        Assert.Equal(rule.WriteAnyOf,
            AttachmentPermissionScope.UnlinkedScanKeys(rule, AttachmentAccess.Write, unlinkedEditIsMemberLevel: false));
    }

    [Fact]
    public void ทิศตรงข้าม_หน้ารีวิวแก้สแกนที่ยังไม่ผูก_ระดับสมาชิกเหมือนเดิม_และอ่านก็เช่นกัน()
    {
        var rule = AttachmentPermissionScope.Resolve("OcrScan");
        Assert.Equal(rule.ReadAnyOf,
            AttachmentPermissionScope.UnlinkedScanKeys(rule, AttachmentAccess.Write, unlinkedEditIsMemberLevel: true));
        Assert.Equal(rule.ReadAnyOf,
            AttachmentPermissionScope.UnlinkedScanKeys(rule, AttachmentAccess.Read, unlinkedEditIsMemberLevel: false));
    }

    // ── Q3: สแกนพี่น้องชี้เอกสารคนละใบ — สองประตูต้องได้คำตอบเดียวกัน ──

    [Fact]
    public void แถวของตัวเองที่ยังอยู่_ชนะพี่น้อง()
        => Assert.Equal(HighDoc, AttachmentPermissionScope.PickLinkedOwner(HighDoc, new[] { LowDoc, HighDoc }));

    [Fact]
    public void แถวของตัวเองถูกลบ_เลือกพี่น้องแบบกำหนดได้แน่นอน_ไม่ขึ้นกับลำดับ()
    {
        Assert.Equal(LowDoc, AttachmentPermissionScope.PickLinkedOwner(NewDoc, new[] { HighDoc, LowDoc }));
        Assert.Equal(LowDoc, AttachmentPermissionScope.PickLinkedOwner(NewDoc, new[] { LowDoc, HighDoc }));
    }

    [Fact]
    public void ไม่มีใบที่ยังอยู่_ไม่มีเจ้าของ()
        => Assert.Null(AttachmentPermissionScope.PickLinkedOwner(NewDoc, Array.Empty<Guid>()));

    // ── Q4: หลักฐาน 50 ทวิ ──

    [Theory]
    [InlineData("PayrollRun")]
    [InlineData("Document")]
    [InlineData("OcrScan")]
    [InlineData(null)]
    public void ไฟล์ของรายการชนิดอื่น_ใช้เป็นหนังสือรับรองไม่ได้(string? type)
        => Assert.Equal(WhtCreditFileLinkKind.Foreign, AttachmentPermissionScope.WhtCreditFileLink(type, Credit, Credit));

    [Fact]
    public void ไฟล์50ทวิของอีกรายการ_ใช้ไม่ได้()
        => Assert.Equal(WhtCreditFileLinkKind.Foreign, AttachmentPermissionScope.WhtCreditFileLink("WhtCredit", OtherCredit, Credit));

    [Fact]
    public void ทิศตรงข้าม_ไฟล์ในถังก่อนบันทึกและไฟล์ของรายการนี้_ใช้ได้()
    {
        Assert.Equal(WhtCreditFileLinkKind.AdoptFromBucket, AttachmentPermissionScope.WhtCreditFileLink("WhtCredit", Guid.Empty, Credit));
        Assert.Equal(WhtCreditFileLinkKind.AlreadyThis, AttachmentPermissionScope.WhtCreditFileLink("WhtCredit", Credit, Credit));
    }

    // ── R2-C4: ระยะเก็บไฟล์สแกน ──

    [Fact]
    public void สแกนที่ลงJEหรือผูกเอกสาร_งานpurgeห้ามลบไฟล์()
        => Assert.False(AttachmentRetention.ScanFilePurgeable("OcrScan", linkedToEntry: true));

    [Theory]
    [InlineData("Document")]
    [InlineData("PayrollRun")]
    [InlineData(null)]
    public void ไฟล์ของรายการอื่น_งานpurgeของสแกนไม่แตะ(string? type)
        => Assert.False(AttachmentRetention.ScanFilePurgeable(type, linkedToEntry: false));

    [Fact]
    public void ทิศตรงข้าม_สแกนที่ไม่เคยเป็นรายการบัญชี_ลบไฟล์จริงได้()
        => Assert.True(AttachmentRetention.ScanFilePurgeable("OcrScan", linkedToEntry: false));

    [Fact]
    public void ไฟล์สแกนที่ถูกถอด_ครบระยะผ่อนและไม่มีใครชี้_ถูกกวาด()
    {
        var now = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(AttachmentRetention.DeletedScanFileSweepable("OcrScan", false, now.AddDays(-31), now));
        // ยังไม่ครบระยะผ่อน · สแกนยังชี้อยู่ · ไม่ใช่ไฟล์ของสแกน = ไม่กวาด
        Assert.False(AttachmentRetention.DeletedScanFileSweepable("OcrScan", false, now.AddDays(-5), now));
        Assert.False(AttachmentRetention.DeletedScanFileSweepable("OcrScan", true, now.AddDays(-31), now));
        Assert.False(AttachmentRetention.DeletedScanFileSweepable("Document", false, now.AddDays(-31), now));
    }
}

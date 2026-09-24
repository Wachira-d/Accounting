using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **“สาขาที่ออกใบกำกับภาษีคือ สาขาที่ 8”** — ประโยคประกาศสาขาผู้ออกใบ + ที่อยู่ของสาขานั้น
/// + ที่อยู่ที่ควรเขียนลง Contact (ทะเบียน = สำนักงานใหญ่) · ใบจริง: ใบ B Radisson (รอบ 190)
/// </summary>
public class OcrIssuerBranchTests
{
    private const string PaperB = OcrPartyZoneRealPaperTests.PaperB;

    // ═══ ใบที่พัง: ใบ B ═══
    [Fact]
    public void ใบB_ประโยคประกาศสาขา_ได้รหัส00008_และที่อยู่สาขาภาษาไทย()
    {
        var s = OcrIssuerBranch.Detect(PaperB);
        Assert.NotNull(s);
        Assert.Equal("00008", s!.Code);
        // หัวกระดาษพิมพ์ทั้งอังกฤษและไทย — เลือกไทย (เอกสารภาษีไทย · ทะเบียน RD/DBD เป็นไทย)
        Assert.Equal("854/2 ถนนบุรีรัมย์ ต.ชะอำ อ.ชะอำ จ.เพชรบุรี 76120", s.Address);
    }

    [Fact]
    public void ใบB_ไม่ได้หยิบที่อยู่สำนักงานใหญ่นนทบุรี()
    {
        var s = OcrIssuerBranch.Detect(PaperB)!;
        Assert.DoesNotContain("นนทบุรี", s.Address);
        Assert.DoesNotContain("Nonthaburi", s.Address);
    }

    [Fact]
    public void หัวกระดาษอังกฤษล้วน_ใช้ที่อยู่อังกฤษ_และตัดเบอร์โทรทิ้ง()
    {
        var s = OcrIssuerBranch.Detect(
            "ABC Hotel\nTax invoice issued by branch no. 3\n99/1 Beach Road, Pattaya, Chonburi 20150 Tel 038-123456\n");
        Assert.Equal("00003", s!.Code);
        Assert.Equal("99/1 Beach Road, Pattaya, Chonburi 20150", s.Address);
    }

    [Fact]
    public void ออกโดยสาขาที่_ไม่มีที่อยู่ตามมา_ได้รหัสแต่ที่อยู่ว่าง_ไม่แต่ง()
    {
        var s = OcrIssuerBranch.Detect("ร้าน ก\nออกโดยสาขาที่ 3\nรวม 100.00\n");
        Assert.Equal("00003", s!.Code);
        Assert.Null(s.Address);
    }

    // ═══ ทิศตรงข้าม: ห้ามเดา ═══
    [Fact]
    public void ใบA_ไม่มีประโยคประกาศ_คืนnull_ให้ตัวอ่านสาขาทั่วไปทำงานตามเดิม()
        => Assert.Null(OcrIssuerBranch.Detect(OcrPartyZoneRealPaperTests.PaperA));

    [Fact]
    public void สำนักงานใหญ่หรือสาขาที่เปล่า_ไม่ใช่ประโยคประกาศ()
    {
        Assert.Null(OcrIssuerBranch.Detect("บริษัท ก จำกัด (สำนักงานใหญ่)\nสาขาที่ 3\n"));
        Assert.Null(OcrIssuerBranch.Detect("ABC Co., Ltd. Branch 00012\n"));
    }

    [Fact]
    public void ไทยกับอังกฤษประกาศรหัสขัดกัน_ไม่รู้_คืนnull()
        => Assert.Null(OcrIssuerBranch.Detect("สาขาที่ออกใบกำกับภาษีคือ สาขาที่ 8\nBranch Tax Invoice is Issued no. 9\n"));

    [Fact]
    public void เลขบ้านบรรทัดถัดไป_ห้ามถูกกลืนเป็นรหัสสาขา()
    {
        // “สาขาที่ 8” จบบรรทัด แล้วบรรทัดถัดไปขึ้นต้น “854/2” — ตัวคั่นต้องไม่ข้ามบรรทัด
        var s = OcrIssuerBranch.Detect("สาขาที่ออกใบกำกับภาษีคือ สาขาที่ 8\n854/2 ถนนบุรีรัมย์ ต.ชะอำ อ.ชะอำ จ.เพชรบุรี 76120\n");
        Assert.Equal("00008", s!.Code);
    }

    [Fact]
    public void ค่าว่าง_ไม่พัง()
    {
        Assert.Null(OcrIssuerBranch.Detect(null));
        Assert.Null(OcrIssuerBranch.Detect("   "));
    }

    // ═══ ประโยคประกาศ vs รหัสที่ถืออยู่ ═══
    [Fact]
    public void ใบB_ถืออยู่00000จากคำว่าสำนักงานใหญ่_ประโยคประกาศต้องแทนเป็น00008_และใช้ที่อยู่สาขา()
    {
        var s = OcrIssuerBranch.Detect(PaperB)!;
        var (action, useAddress) = OcrIssuerBranch.Reconcile("00000", s);
        Assert.Equal(OcrIssuerBranchAction.Replace, action);
        Assert.True(useAddress);
        Assert.Equal(OcrIssuerBranchAction.Replace, OcrIssuerBranch.Reconcile(null, s).Action);
    }

    [Fact]
    public void ถืออยู่00008ตรงแล้ว_ไม่แก้_แต่ยังใช้ที่อยู่สาขา()
    {
        var (action, useAddress) = OcrIssuerBranch.Reconcile("8", OcrIssuerBranch.Detect(PaperB)!);
        Assert.Equal(OcrIssuerBranchAction.Keep, action);
        Assert.True(useAddress);
    }

    [Fact]
    public void ถืออยู่สาขาอื่นที่ไม่ใช่00000_ขัดกัน_ห้ามทับและห้ามเปลี่ยนที่อยู่()
    {
        // เช่น e-Tax/ผู้ใช้/ตัวอ่านอื่นให้ 00003 — ไม่รู้ว่าใครถูก ⇒ ไฮไลต์ ไม่ใช่เลือกเอง (G3/G4)
        var (action, useAddress) = OcrIssuerBranch.Reconcile("00003", OcrIssuerBranch.Detect(PaperB)!);
        Assert.Equal(OcrIssuerBranchAction.Conflict, action);
        Assert.False(useAddress);
    }

    // ═══ ที่อยู่ที่เขียนลง Contact ═══
    private const string Dbd = "200 ห้อง 2302A ชั้น 23 อาคาร จัสมิน หมู่ 8 ถนน แจ้งวัฒนะ ตำบล ปากเกร็ด อำเภอ ปากเกร็ด จังหวัด นนทบุรี 11120";
    private const string BranchAddr = "854/2 ถนนบุรีรัมย์ ต.ชะอำ อ.ชะอำ จ.เพชรบุรี 76120";

    [Fact]
    public void ContactสาขาเดียวกับใบB_ใช้ที่อยู่สาขาจากกระดาษ_ไม่ใช่ที่อยู่สำนักงานใหญ่จากทะเบียน()
    {
        var (addr, fromRegistry) = OcrIssuerBranch.ContactAddress("00008", "00008", true, Dbd, BranchAddr, true);
        Assert.Equal(BranchAddr, addr);
        Assert.False(fromRegistry);
    }

    [Fact]
    public void Contactสำนักงานใหญ่_ยังใช้ทะเบียนเหมือนเดิม()
    {
        var (addr, fromRegistry) = OcrIssuerBranch.ContactAddress("00000", "00000", true, Dbd, "ที่อยู่อ่านเพี้ยน 1 ถ.ก จ.ข 10000", false);
        Assert.Equal(Dbd, addr);
        Assert.True(fromRegistry);
        // ไม่มีทะเบียน + ใบของสำนักงานใหญ่/ไม่รู้สาขา → กระดาษ (พฤติกรรมเดิม)
        Assert.Equal("1 ถ.ก จ.ข 10000", OcrIssuerBranch.ContactAddress(null, null, false, null, "1 ถ.ก จ.ข 10000", false).Address);
    }

    [Fact]
    public void ใบของสาขา8_ห้ามเขียนที่อยู่สาขาลงContactสำนักงานใหญ่()
    {
        // Contact สำนักงานใหญ่ไม่มีทะเบียน + ใบนี้ออกโดยสาขาที่ 8 ⇒ ที่อยู่บนใบเป็นของสาขา 8 ไม่ใช่ของ HQ
        var (addr, _) = OcrIssuerBranch.ContactAddress("00000", "00008", false, null, BranchAddr, true);
        Assert.Null(addr);
        // มีทะเบียน ⇒ Contact HQ ได้ที่อยู่ทะเบียน (ถูกสำหรับ HQ)
        Assert.Equal(Dbd, OcrIssuerBranch.ContactAddress("00000", "00008", true, Dbd, BranchAddr, true).Address);
    }

    [Fact]
    public void Contactสาขา_ที่อยู่กระดาษไม่ได้มาจากประโยคประกาศ_ทะเบียนยังชนะ()
    {
        // ที่อยู่ที่ engine อ่านเองอาจเป็นที่อยู่สำนักงานใหญ่ปนสองภาษา (อาการใบ B เดิม) — ไม่ชนะทะเบียน
        var (addr, fromRegistry) = OcrIssuerBranch.ContactAddress("00008", "00008", true, Dbd, "200 Justmine ... Nonthaburi บริษัท ...", false);
        Assert.Equal(Dbd, addr);
        Assert.True(fromRegistry);
    }

    [Fact]
    public void Contactสาขาอื่น_ไม่มีทะเบียน_ไม่เขียนที่อยู่ของสาขาอื่นลงไป()
        => Assert.Null(OcrIssuerBranch.ContactAddress("00003", "00008", false, null, BranchAddr, true).Address);
}

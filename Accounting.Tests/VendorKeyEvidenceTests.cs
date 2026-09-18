using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **"กุญแจถูกไหม" ไม่ใช่ "ชื่อคล้ายกันไหม"** — ด่านตัดสินว่าทะเบียนราชการชนะได้เมื่อไร
///
/// ═══ เคสจริงที่ผู้ใช้รายงาน (2026-09-18) ═══
/// สแกนใบกำกับของดีแคทลอน → ชื่อคู่ค้าในระบบขึ้นว่า <c>"DECATHLON"</c> ซึ่งเป็นคำบน
/// <b>โลโก้</b> ไม่ใช่ชื่อนิติบุคคลที่ §86/4 บังคับ. ระบบค้นทะเบียนถูกแล้วและได้ชื่อ
/// ทางการมา แต่<b>โยนทิ้ง</b> เพราะด่านเดิมถามว่า "ชื่อสองชื่อคล้ายกันไหม" —
/// "DECATHLON" (ละติน) กับชื่อไทย ได้ <b>0.000</b> เท่ากับ "คนละบริษัท" ⇒ ตกด่าน
///
/// ไฟล์นี้ล็อก<b>สองครึ่ง</b>ตามกฎเหล็ก #4 H:
/// <list type="bullet">
/// <item>ครึ่งแรก — ใบที่เคยพัง (แบรนด์ละติน) กลับมาถูก</item>
/// <item>ครึ่งหลัง — <b>ใบที่เคยถูกต้องอยู่แล้วต้องไม่ถูกแตะ</b> โดยเฉพาะเคสที่
///   ด่าน 0.45 เดิมถูกสร้างมากัน: เลข<b>ผู้ซื้อ</b>ถูกหยิบมาเป็นเลขผู้ขาย ·
///   บาร์โค้ดสินค้า · เลขอ่านเพี้ยนจนได้บริษัทอื่น</item>
/// </list>
/// </summary>
public class VendorKeyEvidenceTests
{
    private const string SellerId = "0105535099511";   // นิติบุคคล checksum ถูก
    private const string BuyerId = "0105541045842";   // คนละราย checksum ถูก
    private const string OurId = "0105564045849";   // บริษัทเจ้าของ tenant
    private const string Barcode = "8859991446166";   // ผ่านทั้ง mod-11 ไทย และ EAN-13 (GS1 885)

    /// <summary>หน้ากระดาษสมมติยาว 1000 ตัวอักษร — ตำแหน่ง &lt; 500 = ครึ่งบน</summary>
    private const int PageLength = 1000;

    private static VendorKeyEvidence Judge(string? vendor, bool labelled, int pos,
        int[]? buyerLabels = null, int[]? sellerLabels = null, string? buyer = BuyerId,
        int textLength = PageLength)
        => OcrVendorKeyEvidence.Judge(vendor, buyer, OurId, labelled, pos,
            buyerLabels ?? System.Array.Empty<int>(), sellerLabels ?? System.Array.Empty<int>(),
            textLength);

    // ══════════ ครึ่งแรก: ใบที่เคยพัง ══════════

    [Fact]
    public void เลขผู้ขายมีป้ายกำกับบนกระดาษ_ถือว่ากุญแจพิสูจน์แล้ว()
        => Assert.Equal(VendorKeyEvidence.ProvenSellerKey, Judge(SellerId, labelled: true, pos: 120));

    [Fact]
    public void แบรนด์ละตินกับชื่อนิติบุคคลไทย_เมื่อกุญแจพิสูจน์แล้วทะเบียนต้องชนะ()
    {
        // ความคล้าย = 0 จริง ๆ (คนละตัวอักษร) — ล็อกไว้ด้วยว่าเลขนี้ไม่ได้มาจาก
        // การที่เทสต์ "ใจดี" ใส่คะแนนสูงให้
        const string official = "บริษัท ดีแคทลอน (ประเทศไทย) จำกัด";
        var sim = Accounting.Services.Implementations.Ocr.FuzzyMatcher.Similarity(official, "DECATHLON");
        Assert.True(sim < DbdIdentityGuard.SameCompanyFloor, $"คะแนนความคล้ายที่วัดได้ = {sim:F3}");

        var verdict = DbdIdentityGuard.Judge(official, "DECATHLON", sim, keyProven: true);
        Assert.Equal(DbdTrustVerdict.KeyVerifiedNameDiffers, verdict);
        Assert.True(DbdIdentityGuard.RegistryWins(verdict));
        Assert.True(DbdIdentityGuard.ShouldLearnMismatch(verdict, official, "DECATHLON"),
            "ชื่อโลโก้ต้องถูกเก็บเป็นตัวอย่างเชิงลบ ไม่งั้นครั้งหน้าก็ยังอ่านได้ 'DECATHLON' เหมือนเดิม");
    }

    [Fact]
    public void ใบไทยที่หัวกระดาษไม่มีป้ายผู้ขาย_ต้องยังพิสูจน์กุญแจได้()
        // ใบไทยส่วนใหญ่วางบล็อกผู้ขายไว้หัวกระดาษโดยไม่มีป้ายอะไรเลย —
        // ถ้าด่านบังคับว่า "ต้องมีป้ายผู้ขาย" จะเป็นเท็จเกือบทุกใบ = ปิดด่านทิ้ง
        => Assert.Equal(VendorKeyEvidence.ProvenSellerKey,
            Judge(SellerId, labelled: true, pos: 80, buyerLabels: new[] { 400 }, sellerLabels: null));

    // ══════════ ครึ่งหลัง: ใบที่ต้องยังถูกเหมือนเดิม ══════════

    [Fact]
    public void เลขผู้ซื้อถูกหยิบมาเป็นเลขผู้ขาย_ห้ามนับว่ากุญแจถูก()
        // เลขผู้ซื้อ**ก็มีป้ายกำกับ**เหมือนกันทุกใบ ⇒ ถ้าเหลือแค่เงื่อนไข "มีป้าย"
        // ทะเบียนจะคืนบริษัทผู้ซื้อ แล้วเราจะทับชื่อผู้ขายด้วยความมั่นใจเต็มร้อย
        => Assert.Equal(VendorKeyEvidence.Unproven,
            Judge(BuyerId, labelled: true, pos: 300));

    [Fact]
    public void เลขบริษัทเราเอง_ห้ามนับว่ากุญแจถูก()
        => Assert.Equal(VendorKeyEvidence.Unproven, Judge(OurId, labelled: true, pos: 300));

    [Fact]
    public void เลขลอยไม่มีป้ายกำกับ_ไม่ใช่หลักฐาน()
        => Assert.Equal(VendorKeyEvidence.Unproven, Judge(SellerId, labelled: false, pos: 120));

    [Fact]
    public void บาร์โค้ดสินค้าที่ผ่าน_mod11_ไทย_ห้ามนับว่ากุญแจถูก()
        => Assert.Equal(VendorKeyEvidence.Unproven, Judge(Barcode, labelled: true, pos: 120));

    [Fact]
    public void เลขที่หลักตรวจสอบไม่ผ่าน_ห้ามนับว่ากุญแจถูก()
        => Assert.Equal(VendorKeyEvidence.Unproven, Judge("0105535099512", labelled: true, pos: 120));

    [Fact]
    public void เลขอยู่ใต้ป้ายฝั่งผู้ซื้อ_ห้ามนับว่ากุญแจถูก()
        => Assert.Equal(VendorKeyEvidence.Unproven,
            Judge(SellerId, labelled: true, pos: 420,
                  buyerLabels: new[] { 400 }, sellerLabels: new[] { 30 }));

    [Fact]
    public void เลขอยู่ใต้ป้ายฝั่งผู้ขายที่ใกล้กว่า_ยังพิสูจน์ได้()
        => Assert.Equal(VendorKeyEvidence.ProvenSellerKey,
            Judge(SellerId, labelled: true, pos: 420,
                  buyerLabels: new[] { 200 }, sellerLabels: new[] { 380 }));

    [Fact]
    public void ไม่รู้ตำแหน่งบนกระดาษ_เงื่อนไขตำแหน่งต้องไม่ตัดสินแทน()
        // -1 = หาไม่เจอ/ไม่มีข้อความดิบ → "ไม่มีหลักฐานว่าอยู่ฝั่งผู้ซื้อ"
        // (เงื่อนไขป้ายกำกับยังบังคับอยู่ จึงไม่ได้เปิดรูให้เลขลอย)
        => Assert.Equal(VendorKeyEvidence.ProvenSellerKey,
            Judge(SellerId, labelled: true, pos: -1, buyerLabels: new[] { 10 }));

    [Fact]
    public void กุญแจพิสูจน์ไม่ได้_ชื่อคนละบริษัท_ต้องตกด่านเหมือนเดิม()
    {
        const string official = "บริษัท สยามแม็คโคร จำกัด (มหาชน)";
        const string incoming = "บริษัท คาร์วิน ไทย แอดวานซ์ เทคโนโลยี อินดัสเทรียล จำกัด";
        var sim = Accounting.Services.Implementations.Ocr.FuzzyMatcher.Similarity(official, incoming);
        var verdict = DbdIdentityGuard.Judge(official, incoming, sim, keyProven: false);
        Assert.Equal(DbdTrustVerdict.KeyLooksWrong, verdict);
        Assert.False(DbdIdentityGuard.RegistryWins(verdict));
        Assert.False(DbdIdentityGuard.ShouldLearnMismatch(verdict));
    }

    // ══════════ ชื่อย่อสั้น ๆ ห้ามชนะด้วยเส้นทาง "เป็นส่วนหนึ่งของกันและกัน" ══════════

    [Fact]
    public void ชื่อย่อสั้นที่เป็นส่วนหนึ่งของชื่อบริษัทอื่น_ห้ามนับว่าตรงกัน()
    {
        // "ปตท" อยู่ใน "ปตท น้ำมันและการค้าปลีก" — คนละนิติบุคคล คนละเลขทะเบียน
        // เดิมเส้นทาง Contains ไม่มีขั้นต่ำ ⇒ ได้ ExactMatch แล้ว**ข้ามด่าน** 0.45 ไปเลย
        var v = DbdIdentityGuard.Judge(
            "บริษัท ปตท น้ำมันและการค้าปลีก จำกัด (มหาชน)", "บริษัท ปตท จำกัด (มหาชน)", 0.0);
        Assert.NotEqual(DbdTrustVerdict.ExactMatch, v);
    }

    [Fact]
    public void ชื่อยาวที่เป็นส่วนหนึ่งของชื่อทางการจริง_ต้องยังนับว่าตรงกัน()
        // ทิศตรงข้ามของเทสต์ข้างบน — กระดาษพิมพ์ชื่อสั้นกว่าทะเบียนเป็นเรื่องปกติ
        => Assert.Equal(DbdTrustVerdict.ExactMatch, DbdIdentityGuard.Judge(
            "บริษัท คาร์วิน ไทย แอดวานซ์ เทคโนโลยี จำกัด", "คาร์วิน ไทย แอดวานซ์", 0.0));

    [Fact]
    public void ชื่อตรงกันเป๊ะ_ไม่มีอะไรให้สอน()
        => Assert.False(DbdIdentityGuard.ShouldLearnMismatch(DbdTrustVerdict.ExactMatch));

    [Fact]
    public void ต้นทางไม่ส่งชื่อมา_ไม่มีอะไรให้สอน()
        => Assert.False(DbdIdentityGuard.ShouldLearnMismatch(DbdTrustVerdict.NoIncomingName));

    // ══════════ กระดาษที่ไม่มีป้ายฝั่งใดเลย = ไม่มีหลักฐานเชิงตำแหน่ง (ฝ่ายค้านรอบ 174) ══════════

    [Fact]
    public void ไม่มีป้ายฝั่งใดเลย_เลขอยู่หัวกระดาษ_ยังพิสูจน์ได้()
        // บล็อกผู้ขายของใบไทยอยู่หัวใบเสมอ — เคสดีแคทลอนอยู่กลุ่มนี้
        => Assert.Equal(VendorKeyEvidence.ProvenSellerKey, Judge(SellerId, labelled: true, pos: 120));

    [Fact]
    public void ไม่มีป้ายฝั่งใดเลย_เลขอยู่ท้ายกระดาษ_ห้ามนับว่าพิสูจน์แล้ว()
        // เดิมเคสนี้ "ผ่าน" เพราะไม่มีป้ายผู้ซื้อให้เทียบ ⇒ แปลง "ไม่รู้" เป็น "ใช่"
        // ทั้งที่ป้าย "เลขประจำตัวผู้เสียภาษี" มีอยู่ทั้งสองฝั่งของใบทุกใบ
        => Assert.Equal(VendorKeyEvidence.Unproven, Judge(SellerId, labelled: true, pos: 900));

    [Fact]
    public void ไม่รู้ความยาวหน้ากระดาษ_และไม่มีป้ายเลย_ต้องไม่เดาว่าผ่าน()
        => Assert.Equal(VendorKeyEvidence.Unproven,
            Judge(SellerId, labelled: true, pos: 120, textLength: 0));

    [Fact]
    public void มีป้ายผู้ซื้ออยู่ล่าง_เลขอยู่บน_ไม่ต้องใช้กติกาครึ่งหน้า()
        // ทิศตรงข้าม: พอมีป้ายสักฝั่ง กติกาตำแหน่งปกติ (ป้ายไหนอยู่เหนือ) พอแล้ว
        // ⇒ เลขที่อยู่ท้ายหน้าแต่เหนือป้ายผู้ซื้อ ต้องยังผ่าน
        => Assert.Equal(VendorKeyEvidence.ProvenSellerKey,
            Judge(SellerId, labelled: true, pos: 900, buyerLabels: new[] { 950 }));

    // ══════════ ชื่อย่อที่ถูกต้อง ไม่ใช่ "คำตอบผิด" ที่ต้องเอาไปสอน ══════════

    [Theory]
    [InlineData("บริษัท ซีพี ออลล์ จำกัด (มหาชน)", "บริษัท ซีพี จำกัด")]
    [InlineData("PTT Global Chemical Public Company Limited", "PTT")]
    public void ชื่อย่อที่เป็นส่วนหนึ่งของชื่อทะเบียน_ห้ามเก็บเป็นตัวอย่างเชิงลบ(string official, string incoming)
    {
        // หลังใส่ MinSubstringLength ชื่อย่อสั้น ๆ ไม่ได้ ExactMatch อีกต่อไป จึงตกมาที่
        // สาขาที่เคย "เรียนรู้" ⇒ ถ้าไม่กัน ระบบจะจดชื่อย่อที่ผู้ใช้ใช้ทุกวันว่าผิด
        var sim = Accounting.Services.Implementations.Ocr.FuzzyMatcher.Similarity(official, incoming);
        var verdict = DbdIdentityGuard.Judge(official, incoming, sim, keyProven: true);
        Assert.True(DbdIdentityGuard.RegistryWins(verdict));
        Assert.False(DbdIdentityGuard.ShouldLearnMismatch(verdict, official, incoming));
    }

    [Fact]
    public void ชื่อคนละบริษัทที่กุญแจพิสูจน์แล้ว_ยังต้องเรียนรู้ได้()
        // ทิศตรงข้ามของเทสต์ข้างบน — ไม่ใช่ทุกเคสที่ห้ามสอน
        => Assert.True(DbdIdentityGuard.ShouldLearnMismatch(
            DbdTrustVerdict.SameCompanyMisspelled,
            "บริษัท ไทยเบฟเวอเรจ จำกัด (มหาชน)", "บริษัท ไทยเบฟเวอเรจ จํากัด (มหาซน)"));
}

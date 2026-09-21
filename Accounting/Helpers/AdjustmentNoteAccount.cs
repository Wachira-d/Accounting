using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตั้งตัวเดียวของกติกา "ผังบัญชีบนบรรทัดใบลดหนี้/ใบเพิ่มหนี้"**
///
/// ═══ ที่มา (ผู้ใช้รายงาน 2026-09-21) ═══
/// "หน้าลดหนี้ ผังบัญชีต้องเลือกฝั่งค่าใช้จ่ายได้ด้วย เพราะลดหนี้ฝั่งซื้อ ถ้าลงบัญชี
/// เป็นวัสดุสิ้นเปลือง ก็ต้องไปลดวัสดุสิ้นเปลือง" — ทีมตรวจ 3 ชุดเปิดไฟล์แล้วพบว่า
/// รากไม่ใช่ "ลืมใส่ผังค่าใช้จ่ายในลิสต์" แต่เป็น **ด่านที่หายไปทั้งด่าน**:
///
/// <para><c>DocumentService.EnsureLineAccountMatchesDocSide</c> กันผังข้ามฝั่งให้ทุกชนิด
/// เอกสาร **ยกเว้น CN/DN** (คอมเมนต์ที่ <c>PureSalesSideTypes</c> เขียนเองว่า "จงใจ
/// ไม่อยู่ทั้งสอง list เพราะเป็นได้ทั้งสองฝั่ง") ⇒ ฝั่งขายมีตาข่าย
/// <c>RevenueLegAccountId</c> (ผังค่าใช้จ่ายหลงมา → ตกกลับบัญชีรายได้มาตรฐาน) แต่
/// **ฝั่งซื้อไม่มีตาข่ายอะไรเลย** — <c>BuildPurchaseLineAccountResolverAsync</c> คืน
/// <c>line.AccountId</c> ทันทีโดยไม่ดูหมวด ⇒ ผัง <b>43060 "ส่วนลดรับ" (หมวดรายได้)</b>
/// ที่ผู้ใช้เลือกมา **ถูก Cr เข้า 4xxxx เงียบสนิท** แทนที่จะลดวัสดุสิ้นเปลือง</para>
///
/// ═══ ทำไมผัง "รายได้" บนใบลดหนี้ฝั่งซื้อถึงผิด ไม่ใช่แค่ "ไม่สวย" ═══
/// ใบลดหนี้ §86/10 คือ **การกลับรายการของธุรกรรมเดิม** ไม่ใช่ธุรกรรมใหม่ ⇒ ขา Cr ต้อง
/// กลับไปที่ผังเดิมที่ใบซื้อ Dr ไว้ · การรับรู้เป็น "รายได้อื่น" แทน ทำให้:
/// <list type="number">
/// <item>กำไรสุทธิเท่าเดิมก็จริง แต่ <b>รายได้รวมบวม</b> ซึ่งเป็นตัวตั้งของอีก 3 กติกา
///   ในระบบนี้ — เกณฑ์ SME (รายได้ ≤ 30 ล้าน) · §81/1 threshold 1.8 ล้าน ·
///   §65 ตรี(4) เพดานค่ารับรอง 0.3% ของรายได้ ⇒ ทั้งสามคำนวณจากฐานที่ไม่จริง</item>
/// <item><b>งบ P&amp;L กับ ภ.พ.30 กระทบยอดไม่ได้ถาวร</b> — ยอด Cr 43060 ของใบลดหนี้
///   ฝั่งซื้อไม่มีวันปรากฏในรายงานภาษีขาย (ระบบนับเป็น <c>inputVat -=</c>)
///   ⇒ ผลต่าง "รายได้ในงบ vs ยอดขายใน ภ.พ.30" ที่อธิบายไม่ได้ทุกงวด</item>
/// <item>TFRS for NPAEs บทที่ 8 — ส่วนลดการค้า/ของคืน <b>หักจากต้นทุนซื้อ</b>
///   ไม่ใช่รับรู้เป็นรายได้</item>
/// </list>
///
/// ═══ ขอบเขตโดยเจตนา ═══
/// helper นี้ตอบแค่ <b>"ผังนี้ใช้กับใบนี้ได้ไหม"</b> — <b>ไม่</b>ตอบว่า "ควรใช้ผังไหน"
/// เพราะคำตอบนั้นเป็นของ <c>BuildPurchaseLineAccountResolverAsync</c> (ฝั่งซื้อ) และ
/// <c>RevenueLegAccountId</c> (ฝั่งขาย) ซึ่งมีผู้เรียกทั่วระบบอยู่แล้ว — การย้าย
/// มาที่นี่จะขยับผังของเอกสารที่ post ไปแล้วทั้งฐาน (กฎเหล็ก #4 H)
/// </summary>
public static class AdjustmentNoteAccount
{
    public const string SideRuleCode = "RD-86/9-10-ACCT-SIDE";
    public const string SideLegalReference = "ป.รัษฎากร §86/9-10 (ใบเพิ่มหนี้/ใบลดหนี้)";
    public const string StockRuleCode = "TFRS-NPAE-8-STOCKVAL";
    public const string StockLegalReference = "TFRS for NPAEs บทที่ 8 (สินค้าคงเหลือ)";

    /// <summary>ชนิดเอกสาร**ต้นทาง**ที่ทำให้ใบลดหนี้/ใบเพิ่มหนี้เป็น "ฝั่งซื้อ"
    ///
    /// <para>ชุดนี้เคยถูกพิมพ์มือซ้ำ 6 ที่ (<c>DocumentService</c> 2 · <c>TaxService</c> 2 ·
    /// <c>documents.html</c> 2) — ชุดเดียวกันทุกที่ แต่ไม่มีใครรับประกันว่าจะยังตรงกัน
    /// ถ้ามีชนิดใหม่ · รวมมาไว้ที่นี่เป็นจุดแก้จุดเดียว (F2 ข้อ 4)</para></summary>
    public static readonly IReadOnlyList<DocumentType> PurchaseSourceTypes = new[]
    {
        DocumentType.PurchaseInvoice,
        DocumentType.Expense,
        DocumentType.PaymentVoucher,
        DocumentType.CertificateInLieu,
    };

    /// <summary>ใบต้นทางชนิดนี้ทำให้ใบลดหนี้/ใบเพิ่มหนี้เป็นฝั่งซื้อไหม</summary>
    public static bool SourceIsPurchaseSide(DocumentType sourceType)
        => PurchaseSourceTypes.Contains(sourceType);

    /// <summary>ฝั่งของใบลดหนี้/ใบเพิ่มหนี้ตามหลักฐานที่ผู้เรียกหามาได้
    ///
    /// <para>ลำดับตาม DECISION_DOCTRINE §1 — <b>ใบต้นทางชนะเสมอ</b> (ใกล้ของจริงที่สุด:
    /// ใบลดหนี้ของใบซื้อไม่มีทางเป็นฝั่งขาย) แล้วค่อยถึงสิ่งที่ผู้ใช้เลือกไว้เอง ·
    /// <b>ไม่มีทั้งคู่ = <c>null</c> ("ไม่รู้")</b> ห้ามคืนค่าเดาเป็น true/false
    /// เพราะผู้เรียกแต่ละรายจัดการ "ไม่รู้" คนละแบบ (ด่านนี้ปล่อยผ่าน · รายงาน
    /// ลูกหนี้โชว์ทั้งสองฝั่ง) — G3 "ไม่รู้ต้องเป็นค่าที่เห็นได้"</para></summary>
    public static bool? ResolveSide(DocumentType? sourceType, bool? userOverride)
    {
        if (sourceType.HasValue) return SourceIsPurchaseSide(sourceType.Value);
        return userOverride;
    }

    /// <summary>ฝั่งของใบที่ **ลงบัญชีไปแล้ว** — ใช้กับรายงาน/แบบยื่น (คนละคำถามกับ
    /// <see cref="ResolveSide"/> ซึ่งตอบตอน "กำลังจะสร้าง")
    ///
    /// <para><b>ลำดับต่างจาก <see cref="ResolveSide"/> โดยเจตนา</b>: ที่นี่ค่าที่ผู้ใช้
    /// สั่งย้ายฝั่งเอง (<c>userOverride</c>) <b>ชนะทุกชั้น</b> เพราะการย้ายฝั่งทำให้ระบบ
    /// กลับ JE เดิมแล้วลงใหม่ให้ตรงฝั่ง (<c>ReclassifyCnDnSideAsync</c>) ⇒ รายงานต้อง
    /// เล่าเรื่องเดียวกับ GL · ส่วน <see cref="ResolveSide"/> ให้ใบต้นทางชนะ เพราะตอน
    /// สร้างยังไม่มี JE และการเลือกฝั่งขัดกับใบต้นทางถูกปฏิเสธด้วย error ของตัวเอง</para>
    ///
    /// <para>ลำดับ: ผู้ใช้สั่งย้าย → ชนิดใบต้นทาง (FK) → <b>GL</b> (ผังภาษีที่ JE ลงจริง
    /// — หลักฐานที่ใกล้ของจริงที่สุดรองจากคำสั่งตรง) → คู่ค้าเป็นผู้ขายอย่างเดียว →
    /// <b>ไม่รู้</b> · <c>Unknown = true</c> แปลว่า **ห้ามนับเข้าช่องใดช่องหนึ่งของแบบยื่น**
    /// (G3 — เงื่อนไขที่เป็นเท็จเพราะไม่มีข้อมูล ห้ามตกเป็น "ผ่าน")</para></summary>
    public static (bool IsPurchase, bool Unknown) ResolvePostedSide(
        bool? userOverride,
        DocumentType? sourceType,
        bool? glTouchedInputVat,
        bool contactIsSupplierOnly)
    {
        if (userOverride.HasValue) return (userOverride.Value, false);
        if (sourceType.HasValue) return (SourceIsPurchaseSide(sourceType.Value), false);
        if (glTouchedInputVat.HasValue) return (glTouchedInputVat.Value, false);
        if (contactIsSupplierOnly) return (true, false);
        return (false, true);
    }

    /// <summary>ผังบัญชีบนบรรทัดอยู่ถูกฝั่งไหม — คืน <c>null</c> = ผ่าน ·
    /// คืนข้อความไทย = ต้องบล็อกพร้อมบอกทางไปต่อ
    ///
    /// <para><paramref name="isPurchaseSide"/> เป็น <c>null</c> ("ยังไม่รู้ฝั่ง" — ใบที่
    /// ไม่ได้อ้างใบต้นทางและผู้ใช้ยังไม่ได้เลือก) ⇒ <b>ปล่อยผ่าน</b> โดยเจตนา:
    /// การเข้มขึ้นกับใบที่ระบบเองยังตอบไม่ได้ว่าอยู่ฝั่งไหน จะไปล้มเอกสารเก่าและ
    /// ทางเข้า API ที่ไม่เคยส่งฝั่งมา ทั้งที่ไม่มีหลักฐานว่าผิด (F2 ข้อ 8)</para>
    ///
    /// <para>กติกาสมมาตรกับด่านของเอกสารฝั่งเดียว: ฝั่งซื้อห้ามหมวด <b>รายได้</b> ·
    /// ฝั่งขายห้ามหมวด <b>ค่าใช้จ่าย</b> · หมวดสินทรัพย์/หนี้สินผ่านทั้งสองฝั่ง
    /// (เงินมัดจำ · รับ/จ่ายล่วงหน้า · ภาษีซื้อ-ขาย เป็นผังที่ใช้ได้จริงทั้งคู่)</para></summary>
    public static string? SideViolation(
        bool? isPurchaseSide,
        bool isCreditNote,
        AccountType accountType,
        string accountCode,
        string accountName,
        string? lineDescription)
    {
        if (!isPurchaseSide.HasValue) return null;
        var docWord = isCreditNote ? "ใบลดหนี้" : "ใบเพิ่มหนี้";
        var desc = string.IsNullOrWhiteSpace(lineDescription) ? "(ไม่ระบุรายละเอียด)" : lineDescription;

        if (isPurchaseSide.Value && accountType == AccountType.Revenue)
            return $"รายการ '{desc}' เลือกผังบัญชี {accountCode} {accountName} (หมวดรายได้) "
                + $"ซึ่งใช้กับ{docWord}ฝั่งซื้อไม่ได้ — {docWord}ฝั่งซื้อเป็นการ"
                + $"{(isCreditNote ? "ลด" : "เพิ่ม")}ยอดของผังที่คุณลงไว้ตอนซื้อ "
                + "เช่น ซื้อลงค่าวัสดุสิ้นเปลือง ก็ต้องเลือกผังวัสดุสิ้นเปลืองผังเดิม "
                + "· เลือกผังหมวดค่าใช้จ่าย/สินทรัพย์/หนี้สิน หรือเว้นว่างเพื่อให้ระบบ"
                + "ใช้ผังเดียวกับใบต้นทาง";

        if (!isPurchaseSide.Value && accountType == AccountType.Expense)
            return $"รายการ '{desc}' เลือกผังบัญชี {accountCode} {accountName} (หมวดค่าใช้จ่าย) "
                + $"ซึ่งใช้กับ{docWord}ฝั่งขายไม่ได้ — {docWord}ฝั่งขายเป็นการ"
                + $"{(isCreditNote ? "ลด" : "เพิ่ม")}ยอดของผังรายได้ที่คุณลงไว้ตอนขาย "
                + "· เลือกผังหมวดรายได้ (4xxxx) หรือเว้นว่างเพื่อใช้บัญชีรายได้มาตรฐาน";

        return null;
    }

    /// <summary>บรรทัดที่ "ของเคลื่อนจริง" ผูกผังที่ไม่ใช่บัญชีคุมสต็อกของสินค้านั้น
    /// — คืน <c>null</c> = ไม่มีอะไรต้องเตือน
    ///
    /// <para>ใบลดหนี้เหตุผล "รับคืนสินค้า" ตัด <b>จำนวน</b> ออกจากสต็อกจริง
    /// (<c>ApplyStockMovementsAsync</c>) ⇒ ถ้าขา Cr ไม่ใช่บัญชีคุมสต็อกตัวเดียวกัน
    /// <b>จำนวนลดแต่มูลค่าใน GL ไม่ลด</b> = สองความจริงที่ไม่มีวันหักล้างกัน และ
    /// สะสมทุกใบ</para>
    ///
    /// <para><b>เป็นคำเตือน ไม่ใช่การบล็อก</b> โดยเจตนา — มีเคสที่ผู้ใช้ตั้งใจจริง
    /// (ส่วนลดของสินค้าที่ขายออกไปแล้ว ตามทฤษฎีเข้า COGS ไม่ใช่สินค้าคงเหลือ) และ
    /// ระบบแยกสองเคสนี้จากข้อมูลบนใบไม่ได้ · ทิศนี้ผ่าน G5 เพราะความเสียหายจะ
    /// **ถูกเห็น** ตอนอนุมัติและผู้ใช้กดยืนยันเองว่ารู้ตัว (ต่างจากวันนี้ที่เงียบสนิท)</para></summary>
    public static string? StockValuationWarning(
        bool isPurchaseSide,
        bool lineReturnsGoods,
        bool productTracksStock,
        string? pickedAccountCode,
        string? inventoryControlAccountCode,
        string? lineDescription)
    {
        if (!isPurchaseSide || !lineReturnsGoods || !productTracksStock) return null;
        if (string.IsNullOrWhiteSpace(pickedAccountCode)) return null;
        if (string.IsNullOrWhiteSpace(inventoryControlAccountCode)) return null;
        if (string.Equals(pickedAccountCode, inventoryControlAccountCode, StringComparison.Ordinal))
            return null;

        var desc = string.IsNullOrWhiteSpace(lineDescription) ? "(ไม่ระบุรายละเอียด)" : lineDescription;
        return $"({StockRuleCode}) รายการ '{desc}' เป็นการรับคืนสินค้าที่ตัดสต็อกจริง "
            + $"แต่ผูกผัง {pickedAccountCode} ไว้ ขณะที่บัญชีคุมสต็อกของสินค้านี้คือ "
            + $"{inventoryControlAccountCode} ⇒ จำนวนจะลดแต่มูลค่าสินค้าคงเหลือใน GL ไม่ลด "
            + $"(ตาม {StockLegalReference}) · ถ้าตั้งใจให้เข้าต้นทุนขายเพราะของถูกขายออกไปแล้ว "
            + "ให้กดยืนยันต่อได้ · ถ้าไม่ใช่ ให้เว้นผังว่างเพื่อใช้บัญชีคุมสต็อกของสินค้านั้น";
    }
}

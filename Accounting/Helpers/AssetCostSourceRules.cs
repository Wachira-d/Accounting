using Accounting.Models.Enums;
using Accounting.Services.Implementations.Tax;

namespace Accounting.Helpers;

/// <summary>
/// **สินทรัพย์ 1 ชิ้น ↔ เอกสารซื้อหลายใบ (มัดจำ · งวด · ใบสุดท้าย · ค่าประกอบ · ใบลดหนี้) — ตัวตัดสินเดียว** (pure · ไม่มี I/O)
///
/// ═══ ที่มา (รอบ 249 · คำขอเจ้าของ 2026-11-06 · erp-review/2026-11-06/ASSET_MULTI_DOCUMENT_DESIGN.md) ═══
/// "ใบเสร็จรับเงินที่เป็นงวด หรือแยกใบมัดจำ เท่ากับว่าของ 1 ชิ้นจะมีหลายใบ" — เดิมทะเบียนผูกได้แค่ 1 บรรทัด
/// (<c>FixedAsset.SourceDocumentLineId</c>) ⇒ เครื่องจักรเครื่องเดียวกลายเป็นหลายแถว · มัดจำ 11810 ค้างตลอดไป ·
/// งานระหว่างก่อสร้าง 12280 ไม่มีทางโอน · ค่าเสื่อมเริ่มที่วันที่ใบ ไม่ใช่วันพร้อมใช้ (TFRS for NPAEs บทที่ 10)
///
/// <para>กติกาทั้งหมดของ "บรรทัดไหนผูกได้ · ยอดเท่าไร · ผูกซ้ำได้ไหม · JE โอนหน้าตาอย่างไร · ปรับต้นทุนหลังโอนอย่างไร" อยู่ที่นี่ที่เดียว
/// — <c>FixedAssetService</c> (สร้าง/ผูก/โอน/ปลด) · <c>DocumentService</c> (ยกเลิกใบ/แก้ผัง) เรียกตัวนี้ ไม่เขียนสูตรเอง</para>
/// </summary>
public static class AssetCostSourceRules
{
    public const string RuleCode = "TFRS-NPAE-10-COST-SOURCES";
    public const string LegalReference =
        "TFRS for NPAEs บทที่ 10 (ราคาทุน = ราคาซื้อ + ต้นทุนทางตรงที่ทำให้พร้อมใช้ · เริ่มคิดค่าเสื่อมเมื่อพร้อมใช้) · ป.รัษฎากร §65 ตรี(5) · §82/5";

    /// <summary>ผังงานระหว่างก่อสร้างในผังมาตรฐาน (<c>FixedAssetAccountClassifier</c>: register แต่ไม่คิดค่าเสื่อม)</summary>
    public const string ConstructionInProgressCode = "12280";

    /// <summary>บทบาทของผังที่บรรทัดลงไว้ — ตัดสินว่าบรรทัดนั้นเป็น "ต้นทุนสินทรัพย์" ได้ไหม</summary>
    public enum AccountRole
    {
        /// <summary>ผังค่าใช้จ่าย/อื่น ๆ — ผูกไม่ได้ (ต้อง "แก้ผังบัญชี" ของบรรทัดก่อน · กันทั้งเป็นค่าใช้จ่ายและเป็นต้นทุน)</summary>
        NotEligible = 0,
        /// <summary>เงินมัดจำ/จ่ายล่วงหน้า (118 · 11810 · 11820 และผังย่อย) — ชุดเดียวกับ <see cref="PurchaseDepositAccount"/></summary>
        Deposit = 1,
        /// <summary>งานระหว่างก่อสร้าง 12280</summary>
        ConstructionInProgress = 2,
        /// <summary>ผังสินทรัพย์ที่ต้องขึ้นทะเบียน (ตัวจำแนกเดียว <c>FixedAssetAccountClassifier</c>)</summary>
        Asset = 3,
    }

    /// <summary>บทบาทของผัง — มัดจำใช้ชุดผังของ <see cref="PurchaseDepositAccount.PreferredCodes"/> (ไม่ใช่ prefix 118 ทั้งหมด:
    /// ผังแม่แบบบางชุดมี 11830 "เงินมัดจำรับล่วงหน้า (สุทธิ)" ซึ่งเป็นฝั่งรับ)</summary>
    public static AccountRole RoleOf(string? accountCode)
    {
        if (string.IsNullOrWhiteSpace(accountCode)) return AccountRole.NotEligible;
        var code = accountCode.Trim();
        foreach (var dep in PurchaseDepositAccount.PreferredCodes(null))
        {
            // "118" = ผังแม่ (ตรงตัวเท่านั้น) · 11810/11820 = รับผังย่อยของมันด้วย (118101 …)
            if (dep.Length <= 3 ? code == dep : code.StartsWith(dep, StringComparison.Ordinal))
                return AccountRole.Deposit;
        }
        var cls = FixedAssetAccountClassifier.Resolve(code);
        if (cls == null) return AccountRole.NotEligible;
        return cls.AssetAccountCode == ConstructionInProgressCode ? AccountRole.ConstructionInProgress : AccountRole.Asset;
    }

    /// <summary>เครื่องหมายของยอดต่อชนิดเอกสาร — <c>null</c> = ชนิดนี้เป็นต้นทุนสินทรัพย์ไม่ได้ (ใบขาย · ใบสั่งซื้อ · ใบรับสินค้า …)</summary>
    /// <param name="adjustmentIsPurchaseSide">ฝั่งของใบลดหนี้/เพิ่มหนี้ (<c>AdjustmentNoteAccount.ResolveSide</c>) — ไม่รู้ = null ⇒ ไม่รับ</param>
    public static int? SignFor(DocumentType type, bool? adjustmentIsPurchaseSide)
        => type switch
        {
            DocumentType.PurchaseInvoice or DocumentType.Expense or DocumentType.PaymentVoucher or DocumentType.CertificateInLieu => 1,
            DocumentType.CreditNote when adjustmentIsPurchaseSide == true => -1,
            DocumentType.DebitNote when adjustmentIsPurchaseSide == true => 1,
            _ => null,
        };

    /// <summary>รอบ 250 (WP-02 §2.4 ข้อ 8 · คำตัดสิน §2 #7 · C F2 · E L-3): ชนิดเอกสารที่ "อาจ" ผูกเป็นต้นทุนสินทรัพย์ได้ (ฝั่งของใบลดหนี้/ใบเพิ่มหนี้ยังไม่รู้ ⇒ นับรวม) —
    /// เส้นยกเลิก/ลบถาวรถือคีย์ผู้เขียนต้นทุน<b>เสมอ</b>เมื่อชนิดเข้าข่าย (ไม่ตรวจ "ผูกอยู่ไหม" นอกล็อก — ช่อง TOCTOU: ผูกเข้ามาระหว่างตรวจกับล็อก)</summary>
    public static bool MayBeCostSource(DocumentType type)
        => SignFor(type, true) != null;

    /// <summary>รอบ 250 (WP-02 §2.4 ข้อ 10 · C F6): ป้ายความหมายของ "วันที่ซื้อ" สำหรับสินทรัพย์ที่ต้นทุนมาจากหลายเอกสาร (หน้า/รายงาน/ไฟล์ส่งออก) · null = ใบเดียว (ความหมายเดิม)</summary>
    public static string? PurchaseDateNote(bool fromMultipleDocuments)
        => fromMultipleDocuments ? "วันที่ซื้อ = วันพร้อมใช้งาน (ต้นทุนมาจากหลายเอกสาร — ดูวันที่ของแต่ละใบในแท็บเอกสารต้นทุน)" : null;

    /// <summary>รอบ 250 (WP-02 §2.4 ข้อ 10 · C F6 เคส #4): ใบที่มีทะเบียนรองรับเกินยอดที่ลงผังสินทรัพย์ใน GL อยู่ก่อน — ด่านตอนผูกตั้งใจ "ห้ามแย่ลง"
    /// (<see cref="OverRegistrationIncrease"/>) จึงปล่อยเคสนี้ ⇒ หน้าเอกสารต้นทุนของสินทรัพย์ต้องบอก · null = ไม่เกิน</summary>
    public static string? PreExistingOverRegistration(string documentNumber, decimal registeredMinusGl, int sign)
    {
        var over = Math.Round(Math.Max(0m, sign * registeredMinusGl), 2, MidpointRounding.AwayFromZero);
        return over > AssetRemovalGlCheck.Tolerance
            ? $"เอกสาร {documentNumber} มีทะเบียนสินทรัพย์รองรับเกินยอดที่ใบนี้ลงผังสินทรัพย์ใน GL {over:N2} บาท (เกินอยู่ก่อน — ทะเบียนอื่นอ้างใบเดียวกัน?) · "
              + "ตรวจทะเบียนที่อ้างใบนี้ แล้วปลด/แก้ราคาทุนที่ซ้ำ"
            : null;
    }

    /// <summary>ป้ายชนิดของแถวผูก (แสดงผลเท่านั้น)</summary>
    public static AssetCostSourceKind KindFor(DocumentType type, AccountRole role, string? description)
    {
        if (type == DocumentType.CreditNote) return AssetCostSourceKind.CreditNote;
        if (type == DocumentType.DebitNote) return AssetCostSourceKind.DebitNote;
        if (role == AccountRole.Deposit) return AssetCostSourceKind.Deposit;
        if (AssetRegistrationPlanner.IsAuxiliary(description)) return AssetCostSourceKind.Auxiliary;
        return role == AccountRole.ConstructionInProgress ? AssetCostSourceKind.Progress : AssetCostSourceKind.Final;
    }

    /// <summary>ต้นทุนของบรรทัด (บาท · มีเครื่องหมาย) = ยอดที่บรรทัดลง GL จริง (<see cref="PurchaseLineGlAmount.Posted"/>) × เครื่องหมาย ·
    /// ส่วน VAT ต้องห้ามที่รวมอยู่แยกออกมาเพื่อแสดง/ตรวจ · <paramref name="payerBorneShare"/> = ภาษีหัก ณ ที่จ่ายที่ผู้จ่ายออกแทนส่วนของบรรทัด (สกุลเอกสาร)</summary>
    public static LineCost CostOf(decimal amount, decimal vatAmount, bool isVatClaimable, decimal exchangeRate, int sign,
        bool isCertificateInLieu = false, decimal payerBorneShare = 0m)
    {
        var gl = DocumentFx.ToBaht(PurchaseLineGlAmount.Posted(isCertificateInLieu, amount, vatAmount, isVatClaimable, payerBorneShare), exchangeRate);
        var nc = DocumentFx.ToBaht(PurchaseLineGlAmount.PostedNonClaimableVat(isCertificateInLieu, vatAmount, isVatClaimable), exchangeRate);
        return new LineCost(sign * gl, sign * nc);
    }

    /// <summary>บรรทัดของใบนี้ลง GL ทางไหน (ฝ่ายค้าน 369bc403 L5 — ตัวตัดสินเดียวของ "ใบตัดชำระ/อ้างใบรับสินค้า")</summary>
    public enum PostingRoute
    {
        /// <summary>ลงบรรทัดบนผังของตัวเอง (ใบซื้อ/ค่าใช้จ่าย/ใบสำคัญจ่าย/ใบรับรองแทนที่ไม่ได้ตัดใบตั้งหนี้)</summary>
        OwnLines = 0,
        /// <summary>ใบสำคัญจ่าย/ใบรับรองแทนที่ตัดชำระใบตั้งหนี้ (PI/ค่าใช้จ่าย) — Dr เจ้าหนี้ ไม่ใช่ผังของบรรทัด</summary>
        Settlement = 1,
        /// <summary>ใบซื้ออ้างใบรับสินค้า — ฐานไปตัดบัญชีพักรับของ ผังสินทรัพย์ลงที่ใบรับสินค้า</summary>
        ViaGoodsReceipt = 2,
    }

    /// <summary>เส้นลง GL ของบรรทัด จากชนิดใบ + ชนิดใบที่อ้าง</summary>
    public static PostingRoute RouteOf(DocumentType type, DocumentType? relatedType)
    {
        if (relatedType == DocumentType.GoodsReceiptNote) return PostingRoute.ViaGoodsReceipt;
        if (type is DocumentType.PaymentVoucher or DocumentType.CertificateInLieu
            && relatedType is DocumentType.PurchaseInvoice or DocumentType.Expense)
            return PostingRoute.Settlement;
        return PostingRoute.OwnLines;
    }

    /// <summary>ต้นทุนของบรรทัด — <see cref="Amount"/> มีเครื่องหมาย (ลดหนี้ติดลบ)</summary>
    public readonly record struct LineCost(decimal Amount, decimal NonClaimableVat);

    /// <summary>ด่านว่าบรรทัดนี้เป็นต้นทุนสินทรัพย์ได้ไหม — null = ได้ · ข้อความ = เหตุ + ทางไปต่อ</summary>
    public static string? LineProblem(
        string documentNumber, DocumentType type, int? sign, bool documentEffective,
        PostingRoute route, AccountRole role, string? accountCode, decimal lineCostAbs)
    {
        var isPaymentSettlement = route == PostingRoute.Settlement;
        var viaGoodsReceipt = route == PostingRoute.ViaGoodsReceipt;
        var label = $"เอกสาร {documentNumber}";
        if (sign == null)
            return $"{label} ({type}) ไม่ใช่เอกสารฝั่งซื้อที่เป็นต้นทุนสินทรัพย์ได้ — ผูกได้เฉพาะใบซื้อ/ค่าใช้จ่าย/ใบสำคัญจ่าย/ใบรับรองแทนใบเสร็จ และใบลดหนี้/เพิ่มหนี้ฝั่งซื้อ";
        if (!documentEffective)
            return $"{label} ยังไม่ได้อนุมัติหรือถูกยกเลิกแล้ว — ยังไม่มีรายการบัญชีให้โอนเป็นต้นทุน (อนุมัติใบก่อน)";
        if (isPaymentSettlement)
            return $"{label} เป็นใบจ่ายที่ตัดชำระใบตั้งหนี้ — บรรทัดไม่ได้ลงผังของตัวเอง · ผูกที่ใบตั้งหนี้ต้นทางแทน";
        if (viaGoodsReceipt)
            return $"{label} อ้างใบรับสินค้า (GRN) — ฐานไปตัดบัญชีพักรับของแล้ว ผังสินทรัพย์ลงที่ใบรับสินค้า · รอบนี้ยังไม่รองรับการผูกเส้นนี้ (ผูกด้วยการปรับปรุงมือ)";
        if (role == AccountRole.NotEligible)
            return $"บรรทัดของ {label} ลงผัง {accountCode ?? "(ไม่ระบุ)"} ซึ่งไม่ใช่ผังมัดจำ (118xx) งานระหว่างก่อสร้าง (12280) หรือผังสินทรัพย์ — "
                + "ถ้าเป็นต้นทุนของสินทรัพย์จริง ให้เปิดเอกสาร → '📒 รายการบัญชี' → '✏️ แก้ผังบัญชี' ย้ายเข้าผังสินทรัพย์ก่อน "
                + "(กันรายจ่ายฝ่าทุนค้างในค่าใช้จ่าย · ป.รัษฎากร §65 ตรี(5))";
        if (lineCostAbs <= 0m)
            return $"บรรทัดของ {label} ไม่มียอดลงบัญชี";
        return null;
    }

    /// <summary>ยอดของบรรทัดที่ยังผูกได้ (ค่าบวก) = |ต้นทุนบรรทัด| − Σ|ที่ผูกไว้แล้วทุกสินทรัพย์|</summary>
    public static decimal Remaining(decimal lineCostAbs, decimal alreadyLinkedAbs)
    {
        var left = Math.Round(lineCostAbs - alreadyLinkedAbs, 2, MidpointRounding.AwayFromZero);
        return left > 0m ? left : 0m;
    }

    /// <summary>ด่านกันคิดต้นทุนซ้ำ: Σ ยอดผูกของบรรทัดหนึ่ง (ทุกสินทรัพย์) ≤ ยอดที่บรรทัดลง GL — null = ผ่าน</summary>
    public static string? AllocationProblem(decimal lineCostAbs, decimal alreadyLinkedAbs, decimal requestedAbs, string lineLabel)
    {
        if (requestedAbs <= 0m) return $"ยอดที่ผูกของ “{lineLabel}” ต้องมากกว่า 0";
        if (Math.Round(requestedAbs, 2, MidpointRounding.AwayFromZero) != requestedAbs)
            return $"ยอดที่ผูกของ “{lineLabel}” ต้องมีทศนิยมไม่เกิน 2 ตำแหน่ง";
        var left = Remaining(lineCostAbs, alreadyLinkedAbs);
        if (requestedAbs > left)
            return $"“{lineLabel}” ลงบัญชี {lineCostAbs:N2} บาท ผูกเป็นต้นทุนสินทรัพย์ไปแล้ว {alreadyLinkedAbs:N2} บาท เหลือผูกได้ {left:N2} บาท "
                + $"แต่ขอผูก {requestedAbs:N2} บาท — ยอดเดียวกันเป็นต้นทุนสองครั้งไม่ได้ (ถ้าผูกผิดตัว ให้ปลดจากสินทรัพย์เดิมก่อน)";
        return null;
    }

    /// <summary>ยอดต่อผังบัญชี (บาท · มีเครื่องหมาย)</summary>
    public sealed record AccountAmount(Guid AccountId, decimal Amount);

    /// <summary>ยอดที่ผูกเกินกว่าที่ใบนั้นลงไว้จริงบนผังต้นทาง (Σ ทุกผัง · ค่าบวก) — ตาข่ายกลางของ "ผูกเงินที่ไม่มีอยู่ใน GL"
    /// (ใบสำคัญจ่ายที่ตัดชำระ · ใบอ้างใบรับสินค้า · อัตราแลกเปลี่ยนปัดต่าง · ใบที่ถูกกลับรายการ) โดยไม่ต้องเดาจากชนิดใบ ·
    /// <paramref name="sign"/> = เครื่องหมายของใบ (ใบลดหนี้ −1: GL/ยอดผูกติดลบทั้งคู่)</summary>
    public static decimal ExcessOverGl(IEnumerable<AccountAmount> glByAccount, IEnumerable<AccountAmount> linkedByAccount, int sign)
    {
        var gl = glByAccount.GroupBy(x => x.AccountId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var excess = 0m;
        foreach (var g in linkedByAccount.GroupBy(x => x.AccountId))
        {
            var linked = g.Sum(x => x.Amount);
            gl.TryGetValue(g.Key, out var posted);
            var over = Math.Round(sign * (linked - posted), 2, MidpointRounding.AwayFromZero);
            if (over > AssetRemovalGlCheck.Tolerance) excess += over;
        }
        return excess;
    }

    /// <summary>ทะเบียนต่อใบ "เกิน" GL ผังสินทรัพย์เพิ่มขึ้นเท่าไรจากการผูก (ค่าบวก · 0 = ไม่แย่ลง) — นับเฉพาะ<b>ส่วนที่เกิน</b>
    /// (<c>max(0, เครื่องหมาย × (ทะเบียน − GL))</c>) ก่อน/หลัง
    /// <para>ที่มา (CI 7cb6bd08 · Db test 4 ตัว): รุ่นแรกเทียบผลต่างมีเครื่องหมายตรง ๆ ⇒ ใบงวดที่ <b>ยังไม่มีทะเบียน</b> (ทะเบียน − GL = −400,000)
    /// ผูกครบพอดี (= 0) ถูกนับเป็น "เกินเพิ่ม 400,000" ⇒ ฟ้องเคสถูกทุกเคส · ทิศ "ขาด" ตรวจที่ <see cref="OrphanIncrease"/></para></summary>
    public static decimal OverRegistrationIncrease(decimal registeredMinusGlBefore, decimal registeredMinusGlAfter, int sign)
    {
        var before = Math.Max(0m, sign * registeredMinusGlBefore);
        var after = Math.Max(0m, sign * registeredMinusGlAfter);
        return Math.Round(Math.Max(0m, after - before), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>ยอด GL ผังสินทรัพย์ของใบที่ "ไม่มีทะเบียนรองรับ" เพิ่มขึ้นเท่าไร (ค่าบวก · 0 = ไม่แย่ลง) — ใช้ตอนรวมร่างเข้า
    /// (ร่างนั้นรวมค่าประกอบจากบรรทัดอื่นที่ไม่ได้ผูกมาด้วย)</summary>
    public static decimal OrphanIncrease(decimal registeredMinusGlBefore, decimal registeredMinusGlAfter, int sign)
    {
        var before = Math.Max(0m, -sign * registeredMinusGlBefore);
        var after = Math.Max(0m, -sign * registeredMinusGlAfter);
        return Math.Round(Math.Max(0m, after - before), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>ร่างทะเบียนเดี่ยวที่การอนุมัติสร้างจากบรรทัดนี้ "รวมเข้า" สินทรัพย์หลายใบได้ไหม (ลบร่างในธุรกรรมเดียวกับการผูก) —
    /// เฉพาะร่างที่ยังไม่ยืนยัน · ไม่มีค่าเสื่อม · ไม่ได้เป็นสินทรัพย์หลายใบเอง</summary>
    public static bool CanAbsorbLegacyDraft(bool needsReview, AssetStatus status, decimal accumulatedDepreciation,
        bool hasPostedDepreciation, bool isUnderAcquisition, int ownActiveSources)
        => needsReview && status == AssetStatus.Active && accumulatedDepreciation == 0m && !hasPostedDepreciation
           && !isUnderAcquisition && ownActiveSources == 0;

    /// <summary>ยอดของแถวผูก 1 แถวสำหรับวางแผน JE โอน</summary>
    public sealed record Source(Guid AccountId, string? AccountCode, decimal Amount);

    /// <summary>ขา JE 1 ขา</summary>
    public sealed record Leg(Guid AccountId, string? AccountCode, decimal Debit, decimal Credit);

    /// <summary>JE โอนต้นทุนเข้าผังสินทรัพย์: แต่ละผังต้นทางยกยอดสุทธิออก (Cr · ลดหนี้สุทธิติดลบ = Dr) · ผังสินทรัพย์รับผลรวม ·
    /// ยอดที่อยู่บนผังปลายทางอยู่แล้วไม่ลง · คืนลิสต์ว่าง = ไม่ต้องมี JE · Σ Dr = Σ Cr เสมอ</summary>
    public static List<Leg> TransferLegs(Guid targetAccountId, string? targetAccountCode, IReadOnlyCollection<Source> sources)
    {
        var nets = sources
            .Where(s => s.AccountId != targetAccountId)
            .GroupBy(s => s.AccountId)
            .Select(g => new Source(g.Key, g.First().AccountCode,
                Math.Round(g.Sum(x => x.Amount), 2, MidpointRounding.AwayFromZero)))
            .Where(s => s.Amount != 0m)
            .OrderBy(s => s.AccountCode, StringComparer.Ordinal)
            .ToList();
        var legs = new List<Leg>();
        var total = nets.Sum(s => s.Amount);
        if (total > 0m) legs.Add(new Leg(targetAccountId, targetAccountCode, total, 0m));
        else if (total < 0m) legs.Add(new Leg(targetAccountId, targetAccountCode, 0m, -total));
        foreach (var s in nets)
            legs.Add(s.Amount > 0m
                ? new Leg(s.AccountId, s.AccountCode, 0m, s.Amount)
                : new Leg(s.AccountId, s.AccountCode, -s.Amount, 0m));
        return legs;
    }

    /// <summary>ด่าน "โอนเป็นสินทรัพย์พร้อมใช้" — null = ผ่าน</summary>
    public static string? PlaceInServiceProblem(
        bool isUnderAcquisition, int activeSources, int voidedSourceDocuments, int ineligibleSources,
        decimal cost, decimal salvage, AccountRole targetRole, bool targetDepreciable,
        DepreciationMethod method, int usefulLifeMonths, DateTime availableForUseDate, DateTime todayBangkok,
        DateTime? latestSourceDocumentDate = null)
    {
        if (!isUnderAcquisition)
            return "สินทรัพย์นี้โอนเป็นสินทรัพย์พร้อมใช้ไปแล้ว — ใบที่มาทีหลังให้กด 'ผูกเอกสาร' (ระบบปรับต้นทุนไปข้างหน้าให้)";
        if (activeSources == 0)
            return "ยังไม่มีเอกสารต้นทุนผูกกับสินทรัพย์นี้ — ผูกใบมัดจำ/ใบงวด/ใบสุดท้ายก่อน";
        if (voidedSourceDocuments > 0)
            return $"มีเอกสารต้นทุน {voidedSourceDocuments} ใบที่ถูกยกเลิกแล้ว — ปลดออกจากสินทรัพย์ก่อน (ต้นทุนจะคิดใหม่)";
        if (ineligibleSources > 0)
            return $"มีบรรทัดต้นทุน {ineligibleSources} บรรทัดที่ถูกย้ายไปผังที่ไม่ใช่มัดจำ/งานระหว่างก่อสร้าง/สินทรัพย์ — ปลดออกหรือแก้ผังกลับก่อน";
        if (cost <= 0m)
            return $"ต้นทุนรวม {cost:N2} บาท ต้องมากกว่า 0 (ใบลดหนี้มากกว่าใบซื้อ?)";
        if (targetRole != AccountRole.Asset)
            return "ผังปลายทางต้องเป็นผังสินทรัพย์พร้อมใช้ (เช่น 12260 เครื่องจักร) — ไม่ใช่งานระหว่างก่อสร้าง/มัดจำ/ค่าใช้จ่าย";
        if (!targetDepreciable && method != DepreciationMethod.None)
            return "ผังปลายทางเป็นที่ดิน/สินทรัพย์ที่ไม่คิดค่าเสื่อม — เลือกวิธี 'ไม่คิดค่าเสื่อม' (ค่าเสื่อมที่ดินหักภาษีไม่ได้ · พ.ร.ฎ.145)";
        if (method != DepreciationMethod.None && usefulLifeMonths <= 0)
            return "ระบุอายุการใช้งาน (เดือน) ก่อนเริ่มคิดค่าเสื่อม";
        if (salvage < 0m)
            return "มูลค่าซากติดลบไม่ได้";
        if (method != DepreciationMethod.None && salvage >= cost)
            return $"มูลค่าซาก {salvage:N2} ต้องน้อยกว่าต้นทุน {cost:N2}";
        if (availableForUseDate.Date > todayBangkok.Date)
            return "วันที่พร้อมใช้งานอยู่ในอนาคตไม่ได้ — โอนเมื่อของพร้อมใช้จริง (ค่าเสื่อมเริ่มจากวันนั้น)";
        // ฝ่ายค้าน 369bc403 M1: JE โอนลงวันพร้อมใช้ — ก่อนวันของใบต้นทางล่าสุด = เครดิตผังมัดจำ/งานระหว่างก่อสร้างก่อนเงินเข้าผังนั้น
        if (latestSourceDocumentDate is DateTime latest && availableForUseDate.Date < latest.Date)
            return $"วันที่พร้อมใช้งาน {availableForUseDate:dd/MM/yyyy} อยู่ก่อนวันที่ของเอกสารต้นทุนล่าสุด ({latest:dd/MM/yyyy}) — "
                + "รายการโอนจะเครดิตผังของใบนั้นก่อนที่ยอดจะเข้าผัง · ถ้าใช้งานจริงก่อนใบสุดท้ายมา ให้โอนด้วยใบที่มีอยู่ แล้วผูกใบที่มาทีหลังภายหลัง (ระบบปรับต้นทุนไปข้างหน้า)";
        return null;
    }

    /// <summary>ยอดคงเหลือของผังต้นทาง ณ วันลงรายการ เทียบยอดที่ JE โอนจะเครดิต</summary>
    public sealed record SourceBalance(string? AccountCode, decimal Balance, decimal Credit);

    /// <summary>ฝ่ายค้าน 369bc403 H3: ผังต้นทาง (มัดจำ/งานระหว่างก่อสร้าง) ต้องมียอดคงเหลือทั้งบริษัท ณ วันลงรายการ ≥ ยอดที่จะเครดิต —
    /// มัดจำที่ถูกเคลียร์ด้วยเอกสารอื่นไปแล้ว (เช่นใบซื้อเต็มราคาที่หักมัดจำเอง) แล้วยังผูกอีก ⇒ ต้นทุนซ้ำ + ผังติดลบ ⇒ ล้มดังพร้อมทางไปต่อ · null = ผ่าน</summary>
    public static string? SourceBalanceProblem(IEnumerable<SourceBalance> rows)
    {
        var short_ = rows.Where(r => r.Credit > 0m
                && Math.Round(r.Balance - r.Credit, 2, MidpointRounding.AwayFromZero) < -AssetRemovalGlCheck.Tolerance)
            .ToList();
        if (short_.Count == 0) return null;
        return "ยอดคงเหลือของผังต้นทางไม่พอให้โอน: "
            + string.Join(" · ", short_.Select(r => $"ผัง {r.AccountCode} คงเหลือ {r.Balance:N2} แต่จะเครดิต {r.Credit:N2}"))
            + " — ยอดนี้อาจถูกเคลียร์ด้วยเอกสารอื่นไปแล้ว (เช่น ใบซื้อเต็มราคาที่หักมัดจำเอง ⇒ ต้นทุนจะซ้ำ) · ปลดใบที่ซ้ำออก หรือแก้รายการที่เคลียร์ผังนั้นก่อน";
    }

    /// <summary>ต้นทุนรวมเกินราคาตามสัญญา (คำถามเจ้าของ §6 ข้อ 1 · ข้อเสนอ) — เตือน ไม่บล็อก · null = ไม่เกิน/ไม่ได้ระบุ</summary>
    public static string? ContractPriceWarning(decimal? contractPrice, decimal cost)
    {
        if (contractPrice is not decimal cp || cp <= 0m) return null;
        if (cost <= cp + AssetRemovalGlCheck.Tolerance) return null;
        return $"ต้นทุนรวม {cost:N2} บาท เกินราคาตามสัญญา {cp:N2} บาท ({cost - cp:N2}) — ตรวจว่าใบสุดท้ายลงเต็มราคาทั้งที่ผูกมัดจำไว้แล้วหรือไม่ "
            + "(ถ้าเกินจริงจากค่าขนส่ง/ติดตั้ง ไม่ต้องทำอะไร)";
    }

    /// <summary>ผลการปรับต้นทุนหลังโอนแล้ว (ใบมาทีหลัง/ใบลดหนี้) — <see cref="DepreciableBase"/> null = ยังไม่มีงวดลงบัญชี ⇒ สร้างตารางใหม่จากต้นทุนใหม่ทั้งก้อน</summary>
    public sealed record CostAdjustment(decimal NewCost, decimal NewNetBookValue, decimal? DepreciableBase, int? RemainingLifeMonths, int? EffectiveFromMonthIndex);

    /// <summary>ด่านปรับต้นทุนหลังโอน — null = ผ่าน</summary>
    /// <param name="elapsedPeriods">จำนวนงวดที่ลงบัญชีแล้ว (0 = ยังไม่มี)</param>
    public static string? AdjustmentProblem(decimal netBookValue, decimal delta, decimal salvage,
        DepreciationMethod method, int usefulLifeMonths, int elapsedPeriods)
    {
        if (delta == 0m) return "ยอดปรับเป็น 0";
        if (netBookValue + delta < salvage)
            return $"ยอดลดต้นทุน {-delta:N2} บาท ทำให้มูลค่าสุทธิ ({netBookValue + delta:N2}) ต่ำกว่ามูลค่าซาก ({salvage:N2}) — "
                + "บันทึกเป็นการปรับปรุงด้วยมือ (ผู้สอบบัญชีต้องเห็นเหตุผล) หรือทบทวนมูลค่าซากก่อน";
        if (method != DepreciationMethod.None && elapsedPeriods > 0 && usefulLifeMonths - elapsedPeriods <= 0)
            return "สินทรัพย์นี้คิดค่าเสื่อมครบอายุแล้ว — ต้นทุนที่เปลี่ยนไม่มีงวดเหลือให้กระจาย · 'ปรับอายุ' ก่อนแล้วผูกอีกครั้ง";
        return null;
    }

    /// <summary>ปรับต้นทุน + ฐานค่าเสื่อมแบบไปข้างหน้า (กลไกเดียวกับ "ทบทวนประมาณการ" — <c>EffectiveDepParams</c>) · งวดที่ลงแล้วไม่ถูกแตะ</summary>
    public static CostAdjustment Adjust(decimal cost, decimal netBookValue, decimal delta, decimal salvage, int usefulLifeMonths, int elapsedPeriods)
    {
        var newCost = cost + delta;
        var newNbv = netBookValue + delta;
        if (elapsedPeriods <= 0) return new CostAdjustment(newCost, newNbv, null, null, null);
        return new CostAdjustment(newCost, newNbv,
            DepreciationEstimateChange.RemainingBase(newNbv, salvage), usefulLifeMonths - elapsedPeriods, elapsedPeriods);
    }

    /// <summary>ปลดแถวผูก — null = ปลดได้</summary>
    public static string? ReleaseProblem(bool capitalized, bool alreadyReleased, string? reason)
    {
        if (alreadyReleased) return "แถวนี้ถูกปลดไปแล้ว";
        if (capitalized)
            return "ใบนี้โอนเป็นต้นทุนสินทรัพย์แล้ว (มีรายการบัญชีโอนแล้ว) — ปลดไม่ได้ · ถ้าผู้ขายลดราคา/คืนเงิน ให้บันทึกใบลดหนี้ (ราคาเพิ่ม = ใบเพิ่มหนี้) แล้วผูกเข้ากับสินทรัพย์";
        if (string.IsNullOrWhiteSpace(reason)) return "ระบุเหตุผลที่ปลดใบนี้ออกจากสินทรัพย์ (เก็บลง audit)";
        return null;
    }

    /// <summary>ยกเลิกใบต้นทาง: แถวที่โอนแล้ว ⇒ ห้าม (ข้อความ) · null = ยกเลิกได้ (แถวที่ยังไม่โอนผู้เรียกปลดในธุรกรรมเดียวกัน)</summary>
    public static string? VoidBlock(string documentNumber, IReadOnlyCollection<string> capitalizedAssetCodes, string verb = "ยกเลิก")
    {
        if (capitalizedAssetCodes.Count == 0) return null;
        return $"{verb}เอกสาร {documentNumber} ไม่ได้ — ใบนี้เป็นต้นทุนของสินทรัพย์ {string.Join(", ", capitalizedAssetCodes)} ที่โอนเป็นสินทรัพย์พร้อมใช้แล้ว "
            + $"(รายการบัญชีโอนเครดิตผังของใบนี้ไปแล้ว — {verb}ใบจะทำให้ผังมัดจำ/งานระหว่างก่อสร้างติดลบ และต้นทุนในทะเบียนเกินจริง). "
            + "ทางแก้: ถ้าผู้ขายลดราคา/คืนเงิน ให้บันทึกใบลดหนี้ (ถ้าราคาเพิ่ม = ใบเพิ่มหนี้) แล้วผูกเข้ากับสินทรัพย์";
    }

    /// <summary>แก้ผังบัญชีของบรรทัดที่ผูกเป็นต้นทุนสินทรัพย์อยู่ ⇒ ห้าม (ข้อความ) · null = ไม่มีการผูก</summary>
    public static string? LineChangeBlock(IReadOnlyCollection<string> assetCodes)
    {
        if (assetCodes.Count == 0) return null;
        return $"บรรทัดนี้ผูกเป็นต้นทุนของสินทรัพย์ {string.Join(", ", assetCodes)} — ย้ายผังใต้การผูกจะทำให้รายการโอนเครดิตผังผิด. "
            + "ทางแก้: ที่หน้าทะเบียนสินทรัพย์ → 'เอกสารต้นทุน' ปลดบรรทัดนี้ออกก่อน (ได้เฉพาะสินทรัพย์ที่ยังระหว่างจัดหา) แล้วจึงแก้ผัง";
    }
}

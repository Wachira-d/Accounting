using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// **ทะเบียนนักเรียน (ILocalDistillationModel) ตัวเดียวของทั้งระบบ** — <c>Program.cs</c> เรียก
/// <see cref="AddLocalDistillationModels"/> ที่นี่ และเทสต์ kill-switch (<c>AiKillSwitchTests</c>) ประกอบ DI จาก
/// เมธอดเดียวกัน ⇒ "นักเรียนที่เทสต์เห็น = นักเรียนที่ระบบจริงลงทะเบียน" (รอบ 201 ทีม AI · A-AI6 · H-8)
///
/// <para>เดิมการลงทะเบียนอยู่ใน <c>Program.cs</c> ล้วน และ <c>AiKillSwitchTests</c> เรียกแต่ pure helper ⇒ ถอด
/// นักเรียนตัวไหนออกก็ไม่มีเทสต์ล้ม (ด่านที่ป้อนผลของสูตรที่ตัวเองตรวจ = ผ่านตลอดกาล · F2 ข้อ 6)</para>
///
/// <para>กฎเหล็ก #1 ข้อ 2 (feature parity): ทุก <see cref="AiFeatureKey"/> ที่เรียก AI ต้องมีนักเรียน —
/// <see cref="KnownGapsWithoutStudent"/> คือ<b>รายการค้างที่ยืนยันแล้ว</b> (ratchet: ห้ามเพิ่มแถว · เพิ่มนักเรียน
/// ให้ feature ไหนแล้วต้องลบแถวนั้นออกในคอมมิตเดียวกัน ไม่งั้นเทสต์ล้ม)</para>
/// </summary>
public static class DistillationModelRegistry
{
    /// <summary>นักเรียนแบบ bespoke — คลาสละหนึ่ง feature (คำตอบเป็นโครงสร้าง/ชุดข้อมูล/ต้องใช้ embedding)</summary>
    public static readonly IReadOnlyList<Type> BespokeModels = new[]
    {
        typeof(VendorCanonDistillationModel),
        typeof(GlAccountDistillationModel),
        typeof(BankMatchDistillationModel),
        typeof(DuplicateDocumentDistillationModel),
        typeof(AnomalyExplanationDistillationModel),
        typeof(ApprovalWarningDistillationModel),
        typeof(PaymentTypeDistillationModel),
        // ⚠️ OcrFullReview เคยอยู่ในลิสต์ยกเว้นของ generic ทั้งที่กฎเหล็ก #1 ระบุชื่อ OcrFullReviewDistillationModel
        // ไว้ตรง ๆ ⇒ ปิด provider แล้ว feature ตายเงียบ → เขียน bespoke ที่เรียน "รายช่อง" (ดูไฟล์นั้น)
        typeof(OcrFullReviewDistillationModel),
        // นักเรียนของ OcrLineItemSplit — ตอบสองชั้น: จำโครงบิลประจำที่ผู้ใช้ยืนยันแล้ว → กติกา RawTextLineSplitter
        // (ตอบได้ตั้งแต่ใบแรกของ tenant ใหม่ = cold-start ไม่ว่างเปล่า · กฎเหล็ก #3 ข้อ 6)
        typeof(LineSplitDistillationModel),
        // รอบ 201 ทีม AI · A-AI4 (H-6): PaymentVoucherAccountingSuggestion มีสองรูปคำถาม (รายบรรทัด = คำตอบเดี่ยว ·
        // ทั้งใบ = {"lines":[…]}) ⇒ generic ตอบรูปทั้งใบไม่ได้ (bulk PV ขึ้น "AI ตอบ JSON ไม่ valid" ทั้งที่ไม่ได้เรียก AI)
        typeof(PaymentVoucherAccountingDistillationModel),
    };

    /// <summary>feature ที่คำตอบเป็น <b>คำตอบเดี่ยว</b> — ใช้ <see cref="GenericFeedbackDistillationModel"/> หนึ่งตัวต่อ feature
    /// (กฎเหล็ก #1 ข้อ 2) · free-form/bulk (ImportColumnMatch · BulkBankStatementMatch · AgingExplanation …) ไม่อยู่ที่นี่</summary>
    public static readonly IReadOnlyList<AiFeatureKey> GenericFeatureKeys = new[]
    {
        AiFeatureKey.DocumentTypeClassification,
        // เราเป็นผู้ซื้อ/ผู้ขาย — single answer (Buyer/Seller) ⇒ generic student พอ
        AiFeatureKey.DocumentRoleInference,
        AiFeatureKey.WhtCategoryInference,
        AiFeatureKey.CreditNoteReasonClassification,
        AiFeatureKey.StockMovementValidation,
        AiFeatureKey.DocumentConversionSuggestion,
        AiFeatureKey.OcrProjectMatch,
        // รอบ 198 — ประเภทบรรทัด settlement (คำตอบเดียว = ชื่อ SettlementLineType) · ด่าน SettlementLineTypeRules.ParseClassifierAnswer
        AiFeatureKey.SettlementLineClassify,
    };

    /// <summary>แชตบอต (free-form Q→A — จับคู่คำถามด้วย embedding) → <see cref="ChatAnswerDistillationModel"/></summary>
    public static readonly IReadOnlyList<AiFeatureKey> ChatFeatureKeys = new[]
    {
        AiFeatureKey.PublicFaqChat,
        AiFeatureKey.TenantAssistantChat,
    };

    /// <summary>
    /// **รายการค้างที่ยืนยันแล้ว (รอบ 201 · ratchet)** — feature ที่ยังไม่มีนักเรียน พร้อมเหตุผล ·
    /// "มีจุดเรียก AI" = มี <c>AiRequest</c> ของ feature นี้ในเรพ ⇒ ปิด provider แล้ว feature นี้ไม่มีคำตอบจากนักเรียน
    /// (call site ต้องมีทาง local ของตัวเอง — ยังไม่ได้ตรวจทีละจุดในรอบ 201) · "ไม่มีจุดเรียก" = enum ค้าง ไม่มีผลกับผู้ใช้
    /// </summary>
    public static readonly IReadOnlyDictionary<AiFeatureKey, string> KnownGapsWithoutStudent =
        new Dictionary<AiFeatureKey, string>
        {
            // ── มีจุดเรียก AI แต่ไม่มีนักเรียน (กฎเหล็ก #1 ข้อ 2 ยังไม่ผ่าน — backlog รอบถัดไป) ──
            [AiFeatureKey.AdHocAnalysis] = GapCallsAi,
            [AiFeatureKey.AgingExplanation] = GapCallsAi,
            [AiFeatureKey.ApprovalRoutingSuggestion] = GapCallsAi,
            [AiFeatureKey.AssetCategorySuggestion] = GapCallsAi,
            [AiFeatureKey.BadDebtRiskDetection] = GapCallsAi,
            [AiFeatureKey.BookTaxDifferenceDetection] = GapCallsAi,
            [AiFeatureKey.BulkBankStatementMatch] = GapCallsAi + " · ด่านเซิร์ฟเวอร์ (TryExactOneToOne ฯลฯ) ตอบก่อนเสมอ",
            [AiFeatureKey.ContactFuzzyMatch] = GapCallsAi,
            [AiFeatureKey.CreditLimitSuggestion] = GapCallsAi,
            [AiFeatureKey.CustomerRfmSegmentation] = GapCallsAi,
            [AiFeatureKey.DeadStockDetection] = GapCallsAi,
            [AiFeatureKey.DimensionAllocationSuggestion] = GapCallsAi,
            [AiFeatureKey.DiscountSuggestion] = GapCallsAi,
            [AiFeatureKey.DocumentMemoGeneration] = GapCallsAi,
            [AiFeatureKey.ForecastNarrative] = GapCallsAi,
            [AiFeatureKey.FxRateSuggestion] = GapCallsAi,
            [AiFeatureKey.GlAccountSlotSuggestion] = GapCallsAi,
            [AiFeatureKey.ImportColumnMatch] = GapCallsAi + " · มี heuristic จับคู่คอลัมน์ของตัวเอง",
            [AiFeatureKey.ImportDataReview] = GapCallsAi + " · มี ImportReviewHeuristics",
            [AiFeatureKey.InventoryAbcAnalysis] = GapCallsAi,
            [AiFeatureKey.InventoryReorderPointSuggestion] = GapCallsAi,
            [AiFeatureKey.ManualJeAccountSuggestion] = GapCallsAi,
            [AiFeatureKey.PaymentChannelSuggestion] = GapCallsAi,
            [AiFeatureKey.PaymentTermsSuggestion] = GapCallsAi,
            [AiFeatureKey.PeriodCloseAnomalyCheck] = GapCallsAi,
            [AiFeatureKey.PriceDriftDetection] = GapCallsAi,
            [AiFeatureKey.ProductCategoryTagging] = GapCallsAi,
            [AiFeatureKey.ProjectAllocationSuggestion] = GapCallsAi,
            [AiFeatureKey.VatTypeInference] = GapCallsAi + " · มี ThaiVatTypeRule",
            // ── ไม่มีจุดเรียก AI (enum ค้าง / ถอดการเรียกแล้ว) · ตัวที่ติด [Obsolete(error: true)] (ContactMatch · CurrencyAndFxSuggestion ·
            //    PaymentMethodSuggestion · ProductMatch) อ้างชื่อในโค้ดไม่ได้ (CS0619) ⇒ ไม่อยู่ในรายการนี้ — MissingStudents ข้ามให้เอง ──
            [AiFeatureKey.LineItemStructuredParse] = GapNoCall,
            [AiFeatureKey.ManualJournalSuggestion] = GapNoCall,
            [AiFeatureKey.PayrollIncomeTypeSuggestion] = GapNoCall,
            [AiFeatureKey.ReorderForecast] = GapNoCall + " (รอบ 184 ถอดการเรียก AI · ReorderNarrative ตอบเอง)",
            [AiFeatureKey.TaxFilingPreCheck] = GapNoCall + " (TaxComplianceChecker เป็นกติกา)",
        };

    private const string GapCallsAi = "มีจุดเรียก AI แต่ยังไม่มีนักเรียน — ปิด provider แล้วต้องพึ่งทาง local ของ call site";
    private const string GapNoCall = "ไม่มีจุดเรียก AI ในเรพ";

    /// <summary>ลงทะเบียนนักเรียนทุกตัว (SINGLETON — แต่ละตัวถือคลังในหน่วยความจำที่งานกลางคืนสร้างใหม่ ·
    /// scoped จะทิ้งของที่เรียนทุกคำขอ)</summary>
    public static IServiceCollection AddLocalDistillationModels(this IServiceCollection services)
    {
        foreach (var t in BespokeModels)
            services.AddSingleton(typeof(ILocalDistillationModel), t);
        foreach (var featureKey in GenericFeatureKeys)
        {
            var fk = featureKey;   // per-iteration capture for the factory closure
            services.AddSingleton<ILocalDistillationModel>(sp =>
                new GenericFeedbackDistillationModel(
                    fk, sp, sp.GetRequiredService<ILogger<GenericFeedbackDistillationModel>>()));
        }
        foreach (var chatFeatureKey in ChatFeatureKeys)
        {
            var cfk = chatFeatureKey;
            services.AddSingleton<ILocalDistillationModel>(sp =>
                new ChatAnswerDistillationModel(
                    cfk, sp,
                    sp.GetRequiredService<Embedding.IEmbeddingService>(),
                    sp.GetRequiredService<ILogger<ChatAnswerDistillationModel>>()));
        }
        return services;
    }

    /// <summary>ค่า enum ที่เลิกใช้แล้ว (<c>[Obsolete]</c>) — ไม่มีจุดเรียก AI ได้อีก (error: true = คอมไพล์ไม่ผ่านถ้าอ้างชื่อ) ⇒ ไม่นับเป็นช่องว่างของนักเรียน</summary>
    public static bool IsRetired(AiFeatureKey key)
        => typeof(AiFeatureKey).GetField(key.ToString())?.IsDefined(typeof(ObsoleteAttribute), inherit: false) == true;

    /// <summary>feature ที่ไม่มีนักเรียน<b>และ</b>ไม่อยู่ในรายการค้าง — ต้องว่างเสมอ (เทสต์ kill-switch ล็อก)</summary>
    public static IReadOnlyList<AiFeatureKey> MissingStudents(IEnumerable<AiFeatureKey> registered)
    {
        var have = new HashSet<AiFeatureKey>(registered);
        return Enum.GetValues<AiFeatureKey>()
            .Where(k => !IsRetired(k) && !have.Contains(k) && !KnownGapsWithoutStudent.ContainsKey(k))
            .ToList();
    }
}

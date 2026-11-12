using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 252 (WP-09 · AUTO_SEND_WHT_DESIGN §3.0 · §9 เฟส 243 + 244a) — กล่องรับ 50 ทวิ ขาเข้า: <b>ประตูเดียว</b> <see cref="SubmitAsync"/>
///
/// <para>ลำดับ: (1) กันซ้ำ (ไฟล์เดิม SHA-256 · เลขที่ใบ+ผู้จ่ายเดิม ⇒ คืนแถวเดิม) → (2) จับคู่ <see cref="WhtCertMatchPolicy"/> (เลขคณิต + กุญแจ · ไม่ถาม AI) →
/// (3) ด่านเชิงกำหนด <see cref="WhtCertIntakeVerdict.Gates"/> → (4) ผ่านครบเท่านั้นจึงถามการตรวจของจริง (<see cref="AiFeatureKey.WhtCertAuthenticity"/> ผ่าน
/// <see cref="IAiOrchestrator.AskAsync"/> — นักเรียนก่อน · AI เป็นครู · เก็บ FeedbackId + UsedAi) → (5) ตัดสิน <see cref="WhtCertIntakeVerdict.Decide"/> →
/// (6) เขียนใบ + allocation (+ ลงเครดิตถ้ายืนยันอัตโนมัติ) ในธุรกรรมเดียว + audit chain</para>
///
/// <para>คำตัดสินเจ้าของ §11 ข้อ 1–2: มั่นใจ = ยืนยันอัตโนมัติ · ไม่มั่นใจ = ผู้ใช้กดยืนยัน · ปิด AI ทุกตัว ⇒ นักเรียนไม่มั่นใจ ⇒ รอผู้ใช้ (ไม่ auto · kill-switch) ·
/// ผู้ใช้ยืนยัน/ตีกลับว่าไม่ใช่ของจริง ⇒ <see cref="IAiFeedbackRecorder.RecordUserChoiceAsync"/> (ปิดลูปกฎเหล็ก #1)</para>
/// </summary>
public class WhtCertIntakeService : IWhtCertIntakeService
{
    private readonly AccountingDbContext _db;
    private readonly IAiOrchestrator _ai;
    private readonly IAiFeedbackRecorder _feedback;
    private readonly WhtCreditService _credits;
    private readonly ILogger<WhtCertIntakeService> _logger;
    private readonly Accounting.Services.Storage.IFileStorage? _storage;

    public WhtCertIntakeService(AccountingDbContext db, IAiOrchestrator ai, IAiFeedbackRecorder feedback, WhtCreditService credits,
        ILogger<WhtCertIntakeService> logger, Accounting.Services.Storage.IFileStorage? storage = null, INotificationEngine? notify = null)
    {
        _db = db; _ai = ai; _feedback = feedback; _credits = credits; _logger = logger; _storage = storage; _notify = notify;
    }

    /// <summary>รอบ 258-3 (F-M4): แจ้งทีมเมื่อหนังสือรับรองเข้ากล่องรับแล้วต้องให้คนยืนยัน (เดิมค้างเงียบ) · null = ไม่แจ้ง (เทสต์/เครื่องมือ)</summary>
    private readonly INotificationEngine? _notify;

    private const string RuleCode = "RD-50BIS-INTAKE";
    private const string LegalReference = "ป.รัษฎากร ม.50 ทวิ · ม.69 ทวิ (เครดิตภาษีถูกหัก ณ ที่จ่ายต้องมีหนังสือรับรอง)";

    // ═══════════ ประตูเดียว ═══════════

    public async Task<WhtCertSubmitResult> SubmitAsync(Guid companyId, WhtCertSubmission s, CancellationToken ct = default)
    {
        // (1) กันซ้ำ — ไฟล์เดิม
        var sha = string.IsNullOrWhiteSpace(s.FileSha256) ? null : s.FileSha256.Trim().ToLowerInvariant();
        if (sha == null && s.FileAttachmentId is Guid fid) sha = await FileSha256Async(companyId, fid, ct);
        if (sha != null && await FindLiveByFileAsync(companyId, sha, ct) is { } dupFile)
            return dupFile;   // ฝ่ายค้าน WP-09 L5: ใบที่ถูกตีกลับส่งไฟล์เดิมใหม่ได้ (ไม่นับเป็นซ้ำ)

        // (1b) กันซ้ำ — ใบเดิม (เลขที่ + ผู้จ่าย + ปีเดียวกัน) ที่ยังไม่ถูกตีกลับ · สแกนใบเดียวกันหลังลูกค้าอัปโหลดแล้ว ⇒ ไม่มีเครดิตซ้ำ
        var certNo = Trim(s.CertificateNumber);
        var payerTax = ThaiTaxId.Normalize(s.PayerTaxId);
        var co = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => new { c.TaxId, c.FiscalYearStartMonth }).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");
        // รอบ 257 ชุด 2 (B-M2): อ่านเลขผู้จ่ายไม่ได้ ⇒ ใช้ผู้ติดต่อ (ที่จับคู่ได้ / ลูกค้าของลิงก์) เป็นกุญแจแทน · ไม่รู้ทั้งคู่ ⇒ ไม่ถือว่าซ้ำ (สร้างใบพร้อมด่าน "อาจซ้ำ")
        var payerContact = s.PayerContactId ?? s.ExpectedPayerContactId;
        var possibleDuplicate = false;
        if (certNo != null)
        {
            var live = await FindLiveByCertAsync(companyId, certNo, payerTax, payerContact, s.CertificateDate, co.FiscalYearStartMonth, null, ct);
            if (live.Duplicate is { } dupCert)
                return dupCert;
            possibleDuplicate = live.UnknownPayerSameNumber;
            // ฝ่ายค้าน WP-09 H1 + รอบสอง M-A/M-D: ใบเดียวกัน (ปีเดียวกัน) อยู่ในทะเบียน/ใบที่ยืนยันแล้ว ⇒ 409 — ตัวเดียวกับทะเบียน (ตรวจซ้ำใต้ล็อกในธุรกรรมด้านล่าง)
            await _credits.EnsureCertificateNotRecordedAsync(companyId, certNo, payerTax, s.PayerContactId, s.CertificateDate, null, ct: ct);
        }
        var payeeIsUs = s.PayeeIsUs
            ?? (ThaiTaxId.IsValid(s.PayeeTaxId) && ThaiTaxId.IsValid(co.TaxId) ? ThaiTaxId.Same(s.PayeeTaxId, co.TaxId) : (bool?)null);
        var payeeTax = ThaiTaxId.IsValid(s.PayeeTaxId) ? ThaiTaxId.Normalize(s.PayeeTaxId)
            : payeeIsUs == true && ThaiTaxId.IsValid(co.TaxId) ? ThaiTaxId.Normalize(co.TaxId) : Trim(s.PayeeTaxId);

        // (2) จับคู่ — เลขคณิต + กุญแจ (ห้ามถาม AI · DOCTRINE §2.1)
        var cands = await LoadCandidatesAsync(companyId, null, ct);
        var match = payeeIsUs == false
            ? new WhtCertMatchPolicy.MatchResult(WhtCertMatchLevel.Rejected, Array.Empty<WhtCertMatchPolicy.Allocation>(),
                "ใบนี้ไม่ได้ออกให้บริษัทเรา — บริษัทเราอยู่ช่องผู้มีหน้าที่หักภาษี ไม่ใช่ผู้ถูกหัก")
            : WhtCertMatchPolicy.Match(
                new WhtCertMatchPolicy.Cert(payerTax, s.PayerContactId, payeeTax, s.CertificateDate, s.WhtAmount),
                co.TaxId, cands.Select(c => c.Candidate).ToList(), s.DocumentHints?.ToList());

        // (3) ด่านเชิงกำหนด
        var candById = cands.ToDictionary(c => c.Candidate.CreditId);
        // ปีภาษีของแถว + รอบของวันที่ใบแรกที่ลงแถวแล้ว (ฝ่ายค้าน WP-09 H2: เครดิตของแถวนับตามวันที่ใบแรก)
        var rowYears = match.Allocations.SelectMany(a => candById.TryGetValue(a.CreditId, out var c)
                ? (c.CertificateDate is DateTime rd ? new[] { c.TaxYear, WhtCertIntakeVerdict.FiscalYearOf(rd, co.FiscalYearStartMonth) } : new[] { c.TaxYear })
                : Array.Empty<int>())
            .Where(y => y > 0).Distinct().ToList();
        int? certFy = s.CertificateDate is DateTime cd ? WhtCertIntakeVerdict.FiscalYearOf(cd, co.FiscalYearStartMonth) : null;
        var gates = WhtCertIntakeVerdict.Gates(new WhtCertIntakeVerdict.GateInput(
            certNo, s.CertificateDate, payerTax, payeeTax, payeeIsUs, s.IncomeAmount, s.WhtRate, s.WhtAmount,
            match.Level, s.ExtractionConfidence, certFy, rowYears, DateTime.UtcNow, s.AmountNeedsReview)).ToList();
        // ฝ่ายค้าน WP-10 M2: ช่องลิงก์อัปโหลด (ไม่ล็อกอิน) — ต้องให้คนตรวจเสมอ (ด่าน ⇒ ไม่ถาม AI · ไม่ยืนยันอัตโนมัติ · จัดสรรค้างเป็น "เสนอ") ·
        // ผู้จ่ายต้องเป็นลูกค้าของลิงก์ · ไม่เสนอแถวของผู้ติดต่ออื่น
        if (s.Channel == WhtCertIntakeChannel.UploadLink)
        {
            var linkContact = s.ExpectedPayerContactId ?? Guid.Empty;
            var linkTax = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
                .Where(c => c.Id == linkContact && c.CompanyId == companyId).Select(c => c.TaxId).FirstOrDefaultAsync(ct);
            gates.AddRange(WhtCertUploadLinkPolicy.LinkGates(linkContact, payerTax, linkTax,
                match.Allocations.Select(a => candById.TryGetValue(a.CreditId, out var c) ? c.Candidate.PayerContactId : null)));
            match = match with
            {
                Allocations = match.Allocations
                    .Where(a => candById.TryGetValue(a.CreditId, out var c) && c.Candidate.PayerContactId == linkContact).ToList(),
            };
        }

        // รอบ 257 ปิดท้าย MED-2 (คำตัดสิน 257-5-1): ใบลงวันที่ในปีที่ยื่น ภ.ง.ด.50 แล้ว ⇒ ด่าน (ไม่ถาม AI · ไม่ยืนยันอัตโนมัติ) — รอคนตัดสินพร้อมเหตุ
        if (await WhtCreditFiledYearGuard.LockedYearAsync(_db, companyId, s.CertificateDate, ct) is int filedFy)
            gates.Add(WhtCreditFiledYearGuard.IntakeReviewGate(filedFy));
        if (possibleDuplicate) gates.Add(PossibleDuplicateGate(certNo!));   // B-M2: ไม่ยืนยันอัตโนมัติ · คนตรวจว่าใบซ้ำหรือใบของลูกค้าอีกราย

        // รอบ 264 P5 (C-02 · B8 · คำตัดสิน 264-5-1): ช่องคู่ค้า NextAcc ⇒ พิสูจน์ที่มาด้วยฐาน (แถว 50 ทวิ ที่ผู้จ่ายออกจริง · เลขภาษี/ยอด/อัตรา/วันที่/แบบ ตรงทุกช่อง) ·
        // ไม่ตรง/ไม่พบ/ผู้จ่ายยกเลิก ⇒ เป็นด่าน (รอคน) — ช่องนี้ไม่ถาม AI อีกเลย (ข้อมูลที่ถูกแก้ระหว่างทางห้ามให้ AI รับรอง · ปิด provider ทุกตัวแล้วใบจริงยังยืนยันเอง)
        var provenance = s.Channel == WhtCertIntakeChannel.NextAccLink;
        if (provenance)
        {
            // ฝ่ายค้าน 264-P5 M1: ตัวตนผู้จ่ายของแถวรอใบที่ใบนี้จะปิด (ผู้ติดต่อในสมุดของเรา) — ส่งเข้าไปผูกกับบริษัทต้นทาง
            var allocPayers = match.Allocations
                .Select(a => candById.TryGetValue(a.CreditId, out var c) ? c.Candidate.PayerContactId : null).ToList();
            var problems = WhtCertIntakeVerdict.ProvenanceProblems(
                await LoadProvenanceAsync(companyId, co.TaxId, payerTax, certNo, s, allocPayers, ct));
            gates.AddRange(problems.Select(x => WhtCertIntakeVerdict.ProvenanceGatePrefix + x));
        }

        var intake = new WhtCertIntake
        {
            CompanyId = companyId,
            Channel = s.Channel,
            EvidenceKind = s.EvidenceKind,
            FileAttachmentId = s.FileAttachmentId,
            FileSha256 = sha,
            OcrScanResultId = s.OcrScanResultId,
            CertificateNumber = certNo,
            CertificateDate = s.CertificateDate,
            PayerName = Trim(s.PayerName),
            PayerTaxId = payerTax.Length > 0 ? payerTax : null,
            PayerBranchCode = Trim(s.PayerBranchCode),
            PayerContactId = s.PayerContactId,
            PayeeTaxId = payeeTax,
            IncomeTypeCode = Trim(s.IncomeTypeCode),
            IncomeAmount = R(s.IncomeAmount),
            WhtRate = s.WhtRate,
            WhtAmount = R(s.WhtAmount),
            PayerFormType = s.PayerFormType,
            ExtractionConfidence = s.ExtractionConfidence,
            MatchLevel = match.Level.ToString(),
            MatchReason = match.Reason,
            SubmittedByName = Trim(s.SubmittedBy),
            SubmittedByUserId = s.SubmittedByUserId,
            Notes = Trim(s.Notes),
            CreatedBy = Trim(s.SubmittedBy),
            // รอบ 253 (WP-10 · N4): เลขอ้างอิง e-WHT เป็นข้อความอิสระ (ไม่ตีความ · ไม่ใช้ตัดสินอะไร) — เก็บไม่เกิน 100 ตัวอักษร
            EvidenceReference = WhtEvidenceReference.Normalize(s.EvidenceReference),
        };

        // (4) ตรวจของจริง — เฉพาะเมื่อด่านเชิงกำหนดผ่านครบ (AI ไม่ได้ตัดสินแทนด่าน · ไม่เสีย token กับใบที่ไม่ auto อยู่แล้ว)
        string? answer = null; decimal? confidence = null; var usable = false;
        if (gates.Count == 0)
        {
            if (provenance)
            {
                // ที่มาพิสูจน์แล้ว (ด่านเชิงกำหนดผ่านครบ) — ไม่เรียก orchestrator · ไม่มีแถว feedback (ไม่มีคำตอบ AI ให้สอน) · ป้าย ⚙️ ยืนยันจากระบบของผู้จ่าย
                answer = WhtCertIntakeVerdict.Genuine;
                confidence = WhtCertIntakeVerdict.ProvenanceConfidence;
                usable = true;
                intake.AuthenticityByProvenance = true;
                intake.WhtCertAuthenticityUsedAi = false;
                intake.AuthenticityAnswer = answer;
                intake.AuthenticityConfidence = confidence;
            }
            else
            {
                var resp = await _ai.AskAsync(Accounting.Services.Ai.Prompts.WhtCertAuthenticityPrompt.Build(
                    companyId, intake.Id, s.Channel, intake.PayerName, intake.PayerTaxId, certNo, s.CertificateDate,
                    intake.IncomeTypeCode, intake.IncomeAmount, intake.WhtRate, intake.WhtAmount, s.RawText), ct);
                intake.WhtCertAuthenticityAiFeedbackId = resp.FeedbackId;
                intake.WhtCertAuthenticityUsedAi = resp.UsedAi;
                answer = WhtCertIntakeVerdict.ParseAuthenticity(resp.PrimaryAnswer);   // ด่านกันมั่ว — นอกชุด = ไม่มีคำตอบ
                confidence = resp.Confidence;
                usable = answer != null && (resp.UsedAi || OcrAiAugmenter.IsStudentAnswer(resp));
                intake.AuthenticityAnswer = usable ? answer : null;
                intake.AuthenticityConfidence = usable && confidence is decimal c0 && c0 >= 0m && c0 <= 1m ? c0 : null;
            }
        }

        // (5) ตัดสิน
        var outcome = WhtCertIntakeVerdict.Decide(match.Level, gates, intake.AuthenticityAnswer, intake.AuthenticityConfidence, usable);

        // (6) เขียนในธุรกรรมเดียว · ฝ่ายค้าน WP-09 L10: อัปโหลดไฟล์เดียวกันพร้อมกันสองคำขอ ⇒ unique index ชน ⇒ ตอบ "ซ้ำ" (ไม่ใช่ 500)
        WhtCertSubmitResult? raced = null;
        try
        {
        raced = await InTxAsync<WhtCertSubmitResult?>(async () =>
        {
            // รอบ 257 ปิดท้าย MED-2: ทางยืนยันอัตโนมัติถือล็อกระดับบริษัทก่อน (ลำดับ บริษัท → เลขที่ใบ → แถว) แล้วตรวจปีที่ยื่นแล้วซ้ำใต้ล็อก —
            // ภ.ง.ด.50 ถูกยื่นระหว่างตรวจ ⇒ ไม่ยืนยันอัตโนมัติ (ใบเข้ากล่องรอคน)
            if (outcome == WhtCertIntakeVerdict.Outcome.AutoConfirm)
            {
                await WhtCreditFiledYearGuard.LockCompanyAsync(_db, companyId, ct);
                if (await WhtCreditFiledYearGuard.LockedYearAsync(_db, companyId, s.CertificateDate, ct) is int filedNow)
                {
                    outcome = WhtCertIntakeVerdict.Outcome.AwaitUser;
                    gates.Add(WhtCreditFiledYearGuard.IntakeReviewGate(filedNow));
                }
            }
            // ฝ่ายค้าน WP-09 รอบสอง (races): ตรวจซ้ำใต้ล็อก "เลขที่ใบ + ผู้จ่าย" ตัวเดียวกับการยืนยัน/ทะเบียน — สองคำขอพร้อมกันไม่ผ่านทั้งคู่
            if (certNo != null)
            {
                await _credits.EnsureCertificateNotRecordedAsync(companyId, certNo, payerTax, s.PayerContactId, s.CertificateDate, null, ct: ct);
                if ((await FindLiveByCertAsync(companyId, certNo, payerTax, payerContact, s.CertificateDate, co.FiscalYearStartMonth, null, ct)).Duplicate is { } lost)
                    return lost;
            }
            var allocs = outcome == WhtCertIntakeVerdict.Outcome.Reject
                ? new List<WhtCertMatchPolicy.Allocation>()
                : match.Level is WhtCertMatchLevel.Exact or WhtCertMatchLevel.Partial or WhtCertMatchLevel.Ambiguous
                    ? match.Allocations.ToList() : new List<WhtCertMatchPolicy.Allocation>();   // Ambiguous = ข้อเสนอให้คนยืนยัน (L1)
            var rows = new Dictionary<Guid, WhtCreditReceived>();
            if (outcome == WhtCertIntakeVerdict.Outcome.AutoConfirm)
            {
                foreach (var a in allocs.OrderBy(x => x.CreditId))
                {
                    await LockCreditAsync(companyId, a.CreditId, ct);
                    var row = await LoadFreshAsync(companyId, a.CreditId, ct);
                    // รอบ 257 ชุด 2 (B-H1): แถวที่ตัดสูญส่วนที่เหลือแล้ว ⇒ การลงใบต้องกลับรายการบัญชีตัดสูญ = ผู้มีสิทธิ์ลงบัญชีกดยืนยันเอง (ไม่ auto)
                    if (row != null && WhtCreditWriteOff.ReopensForLateCertificate(row.Status, row.WriteOffJournalEntryId, row.WriteOffReversalJournalEntryId,
                            row.WhtAmount, row.ReceivedAmount))
                    {
                        outcome = WhtCertIntakeVerdict.Outcome.AwaitUser;
                        gates.Add(WrittenOffNeedsReviewGate);
                        break;
                    }
                    // ยอดค้างเปลี่ยนระหว่างตรวจ (อีกใบเพิ่งยืนยัน) ⇒ ไม่ auto — ให้คนดู (ไม่ลงเกิน)
                    if (row == null || row.Status != WhtCreditStatus.Pending
                        || a.Amount > R(row.WhtAmount - row.ReceivedAmount) + WhtCertMatchPolicy.Tolerance)
                    {
                        outcome = WhtCertIntakeVerdict.Outcome.AwaitUser;
                        gates.Add("ยอดค้างของรายการเปลี่ยนระหว่างตรวจ — ตรวจแล้วกดยืนยันอีกครั้ง");
                        break;
                    }
                    // ฝ่ายค้าน WP-09 รอบสอง (races): ใบแรกของแถวอาจเพิ่งลง (อีกใบยืนยันระหว่างตรวจ) — ตรวจรอบบัญชีซ้ำใต้ล็อก (ตัวตัดสินเดียวกับ H2)
                    if (s.CertificateDate is DateTime acd && WhtCertIntakeVerdict.CrossesCreditPeriod(row.CertificateDate, acd, co.FiscalYearStartMonth))
                    {
                        outcome = WhtCertIntakeVerdict.Outcome.AwaitUser;
                        gates.Add("รายการนี้มีหนังสือรับรองใบแรกลงวันที่ในรอบบัญชีอื่น — ใบนี้ลงรวมแถวเดียวไม่ได้ · เลือก \"บันทึกเป็นรายการเครดิตใหม่\"");
                        break;
                    }
                    rows[a.CreditId] = row;
                }
            }

            intake.GateProblems = gates.Count == 0 ? null : string.Join(" · ", gates);
            if (outcome == WhtCertIntakeVerdict.Outcome.Reject)
            {
                intake.Status = WhtCertIntakeRecordStatus.Rejected;
                intake.RejectedReason = match.Reason;
                intake.RejectedAt = DateTime.UtcNow;
            }
            _db.WhtCertIntakes.Add(intake);
            foreach (var a in allocs)
                _db.WhtCertAllocations.Add(new WhtCertAllocation
                {
                    CompanyId = companyId, IntakeId = intake.Id, WhtCreditId = a.CreditId, Amount = R(a.Amount),
                    Status = outcome == WhtCertIntakeVerdict.Outcome.AutoConfirm ? WhtCertAllocationStatus.Confirmed : WhtCertAllocationStatus.Proposed,
                    PriorEvidenceKind = rows.TryGetValue(a.CreditId, out var priorRow) ? priorRow.EvidenceKind : null,
                });
            if (outcome == WhtCertIntakeVerdict.Outcome.AutoConfirm)
            {
                intake.Status = WhtCertIntakeRecordStatus.Confirmed;
                intake.AutoConfirmed = true;
                intake.ConfirmedAt = DateTime.UtcNow;
                foreach (var a in allocs) ApplyToCredit(rows[a.CreditId], R(a.Amount), intake);
                await AdoptBucketFileAsync(companyId, intake.FileAttachmentId, allocs.Select(a => a.CreditId).FirstOrDefault(), ct);
            }
            Audit(companyId, s.SubmittedByUserId, s.SubmittedBy, intake, outcome switch
            {
                WhtCertIntakeVerdict.Outcome.AutoConfirm when intake.AuthenticityByProvenance => "wht-cert-intake-provenance-confirmed",
                WhtCertIntakeVerdict.Outcome.AutoConfirm => "wht-cert-intake-auto-confirmed",
                WhtCertIntakeVerdict.Outcome.Reject => "wht-cert-intake-rejected",
                _ => "wht-cert-intake-submitted",
            }, allocs.Select(a => new { a.CreditId, a.Amount }).ToList());
            await _db.SaveChangesAsync(ct);
            return null;
        });
        }
        catch (DbUpdateException dup) when (sha != null && dup.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            if (await FindLiveByFileAsync(companyId, sha, ct) is { } winner) return winner;
            throw;
        }
        if (raced != null) return raced;

        var msg = outcome switch
        {
            // ฝ่ายค้าน WP-09 M6: นักเรียนตอบจากประวัติผู้จ่าย (ไม่ได้อ่านกระดาษ) ⇒ ข้อความต้องไม่อ้างว่าตรวจกระดาษแล้ว
            WhtCertIntakeVerdict.Outcome.AutoConfirm when intake.AuthenticityByProvenance => WhtCertIntakeVerdict.ProvenanceConfirmedMessage,   // ฝ่ายค้าน L5
            WhtCertIntakeVerdict.Outcome.AutoConfirm => intake.WhtCertAuthenticityUsedAi
                ? "ยืนยันอัตโนมัติแล้ว — ใบตรงกับรายการรอใบ ผ่านด่านทุกข้อ และ AI ตรวจแล้วดูเป็นใบจริง (ใช้เป็นเครดิตได้)"
                : "ยืนยันอัตโนมัติแล้ว — ใบตรงกับรายการรอใบ ผ่านด่านทุกข้อ และผู้จ่ายรายนี้มีประวัติใบที่ยืนยันแล้ว (ระบบไม่ได้ตรวจกระดาษใบนี้ — เปิดดูได้ที่กล่องรับ)",
            WhtCertIntakeVerdict.Outcome.Reject => match.Reason,
            _ => "ได้รับแล้ว — รอตรวจและกดยืนยัน: " + (gates.Count > 0 ? gates[0] : "การตรวจของจริงยังไม่มั่นใจพอจะยืนยันอัตโนมัติ"),
        };
        if (outcome == WhtCertIntakeVerdict.Outcome.AwaitUser) await NotifyAwaitingAsync(companyId, intake.Id, msg, ct);
        return new(intake.Id, intake.Status.ToString(), intake.AutoConfirmed, false, msg);
    }

    /// <summary>รอบ 258-3 (F-M4): หนังสือรับรอง 50 ทวิ มาถึงกล่องรับ (ลูกค้าอัปโหลด · คู่ค้าส่งเข้า · สแกน) แต่ยืนยันอัตโนมัติไม่ได้ ⇒ แจ้งทีม
    /// (คีย์ <c>AutopilotPartnerExchange</c> — ค่าตั้งต้นกระดิ่ง+อีเมลเจ้าของ) · ยืนยันเอง/ปฏิเสธ ⇒ ไม่แจ้ง (ไม่มีอะไรต้องทำ) · แจ้งล้มไม่ย้อนการรับใบ</summary>
    private async Task NotifyAwaitingAsync(Guid companyId, Guid intakeId, string message, CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            await _notify.DispatchAsync(companyId, Accounting.Models.Constants.NotificationEvents.AutopilotPartnerExchange, new NotificationContext
            {
                Title = "📄 หนังสือรับรอง 50 ทวิ เข้ากล่องรับแล้ว — รอตรวจและยืนยัน",
                Message = message,
                ActionUrl = "/pages/wht-credit.html",
                EntityType = "WhtCertIntake", EntityId = intakeId,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งทีมเรื่อง 50 ทวิ เข้ากล่องรับ {Intake} ไม่สำเร็จ", intakeId);
        }
    }

    public async Task<WhtCertSubmitResult?> FindDuplicateFileAsync(Guid companyId, Guid fileAttachmentId, CancellationToken ct = default)
    {
        var sha = await FileSha256Async(companyId, fileAttachmentId, ct);
        return sha == null ? null : await FindLiveByFileAsync(companyId, sha, ct);
    }

    public Task<WhtCertSubmitResult?> FindDuplicateShaAsync(Guid companyId, string sha256, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(sha256) ? Task.FromResult<WhtCertSubmitResult?>(null)
            : FindLiveByFileAsync(companyId, sha256.Trim().ToLowerInvariant(), ct);

    /// <summary>ใบในกล่องรับที่ยังมีผล (ไม่ถูกตีกลับ) ของไฟล์นี้ — ตัวเดียวของการกันไฟล์ซ้ำ (ตรงกับ index UX_WhtCertIntakes_FileLive)</summary>
    private async Task<WhtCertSubmitResult?> FindLiveByFileAsync(Guid companyId, string sha, CancellationToken ct)
    {
        var dup = await _db.WhtCertIntakes.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.FileSha256 == sha && i.Status != WhtCertIntakeRecordStatus.Rejected)
            .Select(i => new { i.Id, i.Status }).FirstOrDefaultAsync(ct);
        return dup == null ? null : new WhtCertSubmitResult(dup.Id, dup.Status.ToString(), false, true, "ได้รับไฟล์นี้แล้ว — ไม่สร้างรายการซ้ำ");
    }

    /// <summary>รอบ 264 P5 (C-02 · คำตัดสิน 264-5-1) — ข้อเท็จจริงของ "ที่มา" ของใบช่องคู่ค้า NextAcc <b>อ่านจากฐาน ไม่ใช่จากผู้ส่ง</b>:
    /// บริษัทผู้จ่าย = บริษัทที่เลขผู้เสียภาษีตรงใบ <b>และ</b>เป็นคู่ค้าที่เชื่อมอยู่ (Accepted) กับเรา · ใบเลขที่นี้ในบริษัทนั้นที่ผู้ถูกหักคือผู้ติดต่อที่ผูก
    /// <c>NextAccTenant</c> = บริษัทเรา · ยอด/อัตรา/วันที่คิดด้วยสูตรเดียวกับฝั่งส่ง (<see cref="PartnerExchangeRules.CertRate"/> · <see cref="PartnerExchangeRules.WhtCertificateDate"/>) ·
    /// อ่านข้ามบริษัทเฉพาะคู่ที่เชื่อมอยู่ (ทุก query ผูก CompanyId ของคู่) · ตัดสินที่ <see cref="WhtCertIntakeVerdict.ProvenanceProblems"/></summary>
    /// <param name="allocatedPayerContactIds">ผู้จ่ายของแถวรอใบที่ใบนี้จับคู่ (ผู้ติดต่อในสมุดของเรา · null = แถวไม่มีผู้ติดต่อ) — ฝ่ายค้าน M1</param>
    private async Task<WhtCertIntakeVerdict.ProvenanceFacts> LoadProvenanceAsync(Guid companyId, string? ourTaxId, string payerTax, string? certNo,
        WhtCertSubmission s, IReadOnlyList<Guid?> allocatedPayerContactIds, CancellationToken ct)
    {
        var submitted = new WhtCertIntakeVerdict.SubmittedCertFacts(s.PayeeTaxId, s.IncomeAmount, s.WhtAmount, s.WhtRate, s.CertificateDate, s.PayerFormType);
        var none = new WhtCertIntakeVerdict.ProvenanceFacts(false, 0, false, null, ourTaxId, submitted);
        if (payerTax.Length != 13 || certNo == null) return none;
        var partners = await _db.TradingPartnerships.AsNoTracking()
            .Where(t => (t.CompanyAId == companyId || t.CompanyBId == companyId) && t.Status == TradingPartnershipStatus.Accepted)
            .Select(t => t.CompanyAId == companyId ? t.CompanyBId : t.CompanyAId)
            .ToListAsync(ct);
        // ฝ่ายค้าน L4: ใบช่องนี้มาพร้อมบริษัทผู้จ่ายที่ส่งจริง (ระบบเติมฝั่งเซิร์ฟเวอร์) ⇒ จำกัดที่บริษัทนั้น — สาขาที่แยกเป็นผู้เช่าเลขภาษีเดียวกันไม่ปนกัน
        if (s.SourcePayerCompanyId is Guid srcCo) partners = partners.Where(x => x == srcCo).ToList();
        if (partners.Count == 0) return none;
        var payerIds = (await _db.Companies.AsNoTracking().IgnoreQueryFilters()
                .Where(c => partners.Contains(c.Id)).Select(c => new { c.Id, c.TaxId }).ToListAsync(ct))
            .Where(c => ThaiTaxId.Same(c.TaxId, payerTax)).Select(c => c.Id).ToList();   // กุญแจ = เลขผู้เสียภาษี (ไม่ใช่ชื่อ)
        if (payerIds.Count == 0) return none;
        var us = companyId.ToString();
        var srcCert = s.SourceWhtCertId;
        var rows = await _db.WithholdingTaxCerts.AsNoTracking().IgnoreQueryFilters()
            .Where(w => payerIds.Contains(w.CompanyId) && w.CertificateNumber == certNo && (srcCert == null || w.Id == srcCert)
                && w.PayeeContact.CompanyId == w.CompanyId
                && w.PayeeContact.ExternalSystem == PartnerWhtCertExchangeService.ExternalSystem && w.PayeeContact.ExternalId == us)
            .Select(w => new
            {
                w.CompanyId, w.IsDeleted, w.Status, w.TotalIncomeAmount, w.TotalTaxAmount, w.TaxFormType, w.IssuedDate, PayeeTaxId = w.PayeeContact.TaxId,
                Lines = w.Lines.Select(l => new { l.TaxRate, l.PaymentDate }).ToList(),
            })
            .ToListAsync(ct);
        var live = rows.Where(r => !r.IsDeleted
            && (r.Status == Accounting.Models.DTOs.Tax.WithholdingTaxCertStatus.Issued || r.Status == Accounting.Models.DTOs.Tax.WithholdingTaxCertStatus.Printed)).ToList();
        // ฝ่ายค้าน L6: "ผู้จ่ายยกเลิก" เฉพาะแถวที่ถูกยกเลิก/ลบจริง (ร่างไม่ใช่การยกเลิก)
        var voided = rows.Any(r => r.IsDeleted || r.Status == Accounting.Models.DTOs.Tax.WithholdingTaxCertStatus.Voided);
        var src = live.Count == 1 ? live[0] : null;
        // ฝ่ายค้าน M1 (คำตัดสิน 264-5-6): ผู้จ่ายของทุกแถวรอใบต้องเป็นผู้ติดต่อของเราที่ผูก NextAccTenant กับบริษัทต้นทาง (ข้อมูลในสมุดของเรา)
        var linked = false;
        if (src != null && allocatedPayerContactIds.Count > 0 && allocatedPayerContactIds.All(x => x != null))
        {
            var ids = allocatedPayerContactIds.Select(x => x!.Value).Distinct().ToList();
            var srcKey = src.CompanyId.ToString();
            var ok = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
                .CountAsync(c => c.CompanyId == companyId && ids.Contains(c.Id) && !c.IsDeleted
                    && c.ExternalSystem == PartnerWhtCertExchangeService.ExternalSystem && c.ExternalId == srcKey, ct);
            linked = ok == ids.Count;
        }
        return new WhtCertIntakeVerdict.ProvenanceFacts(true, live.Count, voided,
            src == null ? null : new WhtCertIntakeVerdict.SourceCertFacts(
                src.TotalIncomeAmount, src.TotalTaxAmount,
                PartnerExchangeRules.CertRate(src.Lines.Select(l => l.TaxRate).ToList(), src.TotalIncomeAmount, src.TotalTaxAmount),
                PartnerExchangeRules.WhtCertificateDate(src.Lines.Select(l => l.PaymentDate), src.IssuedDate),
                src.TaxFormType switch
                {
                    TaxType.WithholdingTax53 => (WhtPayerFormType?)WhtPayerFormType.Pnd53,
                    TaxType.WithholdingTax3 => WhtPayerFormType.Pnd3,
                    _ => null,
                },
                src.PayeeTaxId),
            ourTaxId, submitted, linked);
    }

    /// <param name="Duplicate">ใบเดิม (ผู้จ่ายเดียวกันตามกุญแจ) ⇒ ผลเดิม · null = ไม่ซ้ำ</param>
    /// <param name="UnknownPayerSameNumber">มีใบเลขที่เดียวกันปีเดียวกัน แต่ตัดสินผู้จ่ายไม่ได้ (ไม่รู้เลขผู้จ่ายและผู้ติดต่อ) — อาจซ้ำ</param>
    private sealed record CertLookup(WhtCertSubmitResult? Duplicate, bool UnknownPayerSameNumber);

    /// <summary>ใบในกล่องรับที่ยังมีผล (ไม่ถูกตีกลับ) เลขที่ + ผู้จ่ายเดียวกัน <b>ปีเดียวกัน</b> (ฝ่ายค้าน WP-09 รอบสอง M-D — <see cref="WhtCertIntakeVerdict.SameCertYear"/>)
    /// ⇒ ผลเดิม (ซ้ำ) · รอบ 257 ชุด 2 (B-M2): ผู้จ่ายตัดสินด้วยกุญแจ <see cref="WhtCertIntakeVerdict.SameCertPayer"/> (เลขผู้จ่าย → ผู้ติดต่อ) ·
    /// ไม่รู้ทั้งคู่ ⇒ ไม่คืนใบเดิม (เดิมถือว่าซ้ำ ⇒ ใบของลูกค้าอื่นถูกกลืน) แต่บอกว่า "อาจซ้ำ" ให้ผู้เรียกใส่ด่าน/ขอเลขผู้จ่าย</summary>
    private async Task<CertLookup> FindLiveByCertAsync(Guid companyId, string certNo, string payerTax, Guid? payerContactId, DateTime? certDate, int fyStart,
        Guid? exceptIntakeId, CancellationToken ct)
    {
        var year = WhtCertIntakeVerdict.CertYear(certDate, null, fyStart);
        var sameNo = await _db.WhtCertIntakes.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.CertificateNumber == certNo && i.Status != WhtCertIntakeRecordStatus.Rejected
                && (exceptIntakeId == null || i.Id != exceptIntakeId))
            .Select(i => new { i.Id, i.Status, i.PayerTaxId, i.PayerContactId, i.CertificateDate }).ToListAsync(ct);
        var sameYear = sameNo.Where(i => WhtCertIntakeVerdict.SameCertYear(year, WhtCertIntakeVerdict.CertYear(i.CertificateDate, null, fyStart))).ToList();
        var dup = sameYear.FirstOrDefault(i =>
            WhtCertIntakeVerdict.SameCertPayer(payerTax, payerContactId, i.PayerTaxId, i.PayerContactId) == WhtCertIntakeVerdict.CertPayerMatch.Same);
        if (dup != null)
            return new(new(dup.Id, dup.Status.ToString(), false, true, $"หนังสือรับรองเลขที่ {certNo} ของผู้จ่ายรายนี้อยู่ในกล่องรับแล้ว — ไม่สร้างรายการซ้ำ"), false);
        return new(null, sameYear.Any(i =>
            WhtCertIntakeVerdict.SameCertPayer(payerTax, payerContactId, i.PayerTaxId, i.PayerContactId) == WhtCertIntakeVerdict.CertPayerMatch.Unknown));
    }

    /// <summary>B-M2: ด่าน "อาจซ้ำ" — ใบเลขที่เดียวกัน (ปีเดียวกัน) อยู่ในกล่องรับแล้ว แต่ตัดสินผู้จ่ายไม่ได้</summary>
    internal static string PossibleDuplicateGate(string certNo)
        => $"อาจเป็นใบซ้ำ — หนังสือรับรองเลขที่ {certNo} (ปีเดียวกัน) อยู่ในกล่องรับแล้ว แต่ระบบอ่านเลขผู้เสียภาษีของผู้จ่ายไม่ได้ จึงแยกไม่ได้ว่าเป็นใบเดียวกัน "
           + "หรือใบเลขเดียวกันของลูกค้ารายอื่น · เปิดดูใบ ถ้าซ้ำให้ตีกลับ · ถ้าไม่ซ้ำให้กรอกเลขผู้จ่ายแล้วกดยืนยัน";

    /// <summary>B-H1: แถวที่ใบนี้ครอบตัดสูญส่วนที่เหลือไปแล้ว — ลงใบต้องกลับรายการบัญชีตัดสูญ ⇒ ผู้มีสิทธิ์ลงบัญชีกดยืนยัน (ไม่ยืนยันอัตโนมัติ)</summary>
    internal const string WrittenOffNeedsReviewGate =
        "รายการที่ใบนี้ครอบตัดสูญส่วนที่เหลือพร้อมรายการบัญชีแล้ว — ยืนยันใบนี้ ระบบจะกลับรายการตัดสูญให้ (ต้องมีสิทธิ์จัดการใบสำคัญ Journal.Manage)";

    /// <summary>B-H1: ยืนยันใบที่ต้องกลับรายการตัดสูญ โดยผู้ไม่ถือ Journal.Manage</summary>
    internal const string WrittenOffNeedsJournalMessage =
        "รายการที่เลือกตัดสูญส่วนที่เหลือพร้อมรายการบัญชีแล้ว — การรับใบภายหลังต้องกลับรายการบัญชีตัดสูญ จึงต้องให้ผู้มีสิทธิ์จัดการใบสำคัญ (Journal.Manage) "
        + "กดยืนยันใบนี้ในกล่องรับ (ระบบกลับรายการและคืนเครดิตให้)";

    /// <summary>คำนำหน้าหมายเหตุของแถวที่ "บันทึกเป็นรายการเครดิตใหม่" จากใบในกล่องรับ — ถอนการยืนยันใบนั้น ⇒ ถอดแถวนี้ (ไม่มี GL รองรับ)</summary>
    internal const string StandaloneNotePrefix = "สร้างจากใบ 50 ทวิ ในกล่องรับ";

    public async Task<WhtCertSubmitResult> SubmitFromScanAsync(Guid companyId, Guid scanResultId, WhtCertIntakeChannel channel,
        IReadOnlyList<Guid>? documentHints, string submittedBy, Guid? submittedByUserId, CancellationToken ct = default,
        WhtEvidenceKind evidenceKind = WhtEvidenceKind.PaperCertificate, string? evidenceReference = null, string? fileSha256 = null,
        Guid? expectedPayerContactId = null)
    {
        var scan = await _db.OcrScanResults.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == scanResultId && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบผลสแกนของบริษัทนี้");
        var co = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => new { c.TaxId, c.BusinessType }).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");
        var m = WhtCertScanMapping.Map(scan.ExtractedSubTotal, scan.WhtRate, scan.ExtractedTotalAmount, scan.RawTextContent,
            scan.FieldConfidenceJson, scan.Confidence, co.BusinessType);
        var weAreWithheld = Ocr.OcrDocumentRoleInferrer.InferWhtCertWeAreWithheld(scan.RawTextContent, co.TaxId);
        return await SubmitAsync(companyId, new WhtCertSubmission(
            channel, evidenceKind,
            scan.ExtractedDocumentNumber, scan.ExtractedDate,
            scan.ExtractedVendorName, scan.ExtractedVendorTaxId, null, scan.MatchedContactId,
            null, weAreWithheld,
            m.IncomeTypeCode, m.IncomeAmount, m.WhtRate, m.WhtAmount, m.PayerFormType,
            m.ExtractionConfidence, scan.FileAttachmentId, fileSha256, scan.Id,
            documentHints, scan.RawTextContent, submittedBy, submittedByUserId,
            m.AmountUnknown ? "อ่านยอดภาษีที่หักและนำส่งจากใบไม่ได้ — กรอกยอดแล้วกดยืนยัน" : null,
            AmountNeedsReview: m.AmountNeedsReview || m.AmountUnknown, EvidenceReference: evidenceReference,
            ExpectedPayerContactId: expectedPayerContactId), ct);
    }

    /// <summary>รอบ 257 ชุด 2 (B-L3): ข้อความบนใบที่อ่านไฟล์อัตโนมัติไม่ได้</summary>
    internal const string UnreadableNote = "อ่านไฟล์อัตโนมัติไม่สำเร็จ — เปิดดูไฟล์แล้วกรอกข้อมูลตามหนังสือรับรอง";

    /// <summary>รอบ 257 ชุด 2 (B-L3): OCR ล้ม (ผู้ให้บริการล่ม/ไฟล์อ่านไม่ได้/โควตา) ⇒ ไม่ทิ้งไฟล์ ไม่ตอบ error ดิบ — เข้าประตูเดียวแบบไม่มีค่าที่อ่านได้
    /// (รอคนตรวจจากไฟล์ · ไม่ยืนยันอัตโนมัติ — ด่านเชิงกำหนดไม่ผ่านเอง) · ผู้เรียก: อัปโหลดเอง (<c>WhtCertIntakeController.Upload</c>) + ลิงก์อัปโหลด</summary>
    public Task<WhtCertSubmitResult> SubmitUnreadableAsync(Guid companyId, WhtCertIntakeChannel channel, Guid fileAttachmentId, string? fileSha256,
        IReadOnlyList<Guid>? documentHints, string submittedBy, Guid? submittedByUserId, CancellationToken ct = default,
        Guid? expectedPayerContactId = null, WhtEvidenceKind evidenceKind = WhtEvidenceKind.PaperCertificate, string? evidenceReference = null)
    {
        return SubmitAsync(companyId, new WhtCertSubmission(
            channel, evidenceKind, null, null, null, null, null, expectedPayerContactId, null, null,
            null, 0m, 0m, 0m, WhtPayerFormType.Pnd53, 0m, fileAttachmentId, fileSha256, null, documentHints, null,
            submittedBy, submittedByUserId, UnreadableNote, AmountNeedsReview: true, EvidenceReference: evidenceReference,
            ExpectedPayerContactId: expectedPayerContactId), ct);
    }

    // ═══════════ ผู้ใช้ตัดสิน ═══════════

    /// <param name="journalActor">รอบ 257 ชุด 2 (B-H1): ผู้ลงบัญชี (ผู้ถือ Journal.Manage — controller ตัดสิน) · จำเป็นเมื่อใบครอบแถวที่ตัดสูญส่วนที่เหลือแล้ว
    /// (ยืนยัน = กลับรายการ JE ตัดสูญ) · null + แถวแบบนั้น ⇒ 403 ข้อความไทย</param>
    public async Task<WhtCertIntakeDto> ConfirmAsync(Guid companyId, Guid intakeId, WhtCertConfirmRequest req, Guid userId, string userName,
        bool holdsTaxFile, CancellationToken ct = default, WhtCreditService.WriteOffActor? journalActor = null)
    {
        req ??= new WhtCertConfirmRequest();
        Guid? feedbackId = null; string? aiAnswer = null; var learn = false; string? followUp = null;
        await InTxAsync(async () =>
        {
            // รอบ 257 ชุด 2 ฝ่ายค้าน M1: ล็อกระดับบริษัทก่อนล็อกใบ (ตัวเดียวกับยื่น/ปลดล็อก ภ.ง.ด.50 · ถอนใบ · ตัดสูญ) — ลำดับเดียวทุกทาง
            await WhtCreditFiledYearGuard.LockCompanyAsync(_db, companyId, ct);
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.WhtCreditWriteOff, "intake-" + intakeId.ToString("N")) }, ct);
            var intake = await _db.WhtCertIntakes.FirstOrDefaultAsync(i => i.Id == intakeId && i.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบใบ 50 ทวิ ในกล่องรับ");
            if (_db.Entry(intake).State == EntityState.Unchanged) await _db.Entry(intake).ReloadAsync(ct);
            if (intake.Status == WhtCertIntakeRecordStatus.Confirmed) return true;   // กดซ้ำ = ผลเดิม
            if (intake.Status == WhtCertIntakeRecordStatus.Rejected)
                throw new BusinessRuleException("ใบนี้ถูกตีกลับแล้ว — ยืนยันไม่ได้ · ให้ลูกค้าส่งใบใหม่ หรืออัปโหลดใบที่ถูกต้องอีกครั้ง", RuleCode, 409);

            var before = Snapshot(intake);
            // ช่องที่ผู้ใช้แก้ (null = คงค่าที่อ่านได้)
            if (req.CertificateNumber != null) intake.CertificateNumber = Trim(req.CertificateNumber);
            if (req.CertificateDate != null) intake.CertificateDate = req.CertificateDate;
            if (req.PayerName != null) intake.PayerName = Trim(req.PayerName);
            if (req.PayerTaxId != null) intake.PayerTaxId = ThaiTaxId.Normalize(req.PayerTaxId) is { Length: > 0 } pt ? pt : null;
            if (req.IncomeTypeCode != null) intake.IncomeTypeCode = Trim(req.IncomeTypeCode);
            if (req.IncomeAmount is decimal ia) intake.IncomeAmount = R(ia);
            if (req.WhtRate is decimal wr) intake.WhtRate = wr;
            if (req.WhtAmount is decimal wa) intake.WhtAmount = R(wa);

            if (string.IsNullOrWhiteSpace(intake.CertificateNumber))
                throw new BusinessRuleException("ต้องระบุเลขที่หนังสือรับรอง — กฎหมายให้เครดิตเฉพาะรายการที่มีใบจริง (ม.69 ทวิ)", RuleCode);
            // ฝ่ายค้าน WP-09 L4: วันที่บนใบกำหนดงวดเครดิต ภ.ง.ด.50 (CreditableInPeriod) — ไม่มีวันที่ = เครดิตหาย ⇒ บังคับก่อนยืนยัน
            if (intake.CertificateDate is null)
                throw new BusinessRuleException("ต้องระบุวันที่บนหนังสือรับรอง — ใช้กำหนดรอบที่นำเครดิตไปใช้ใน ภ.ง.ด.50 (ไม่มีวันที่ = นับเป็นเครดิตไม่ได้)", RuleCode);
            // รอบ 257 ปิดท้าย MED-2 (คำตัดสิน 257-5-1): ใบลงวันที่ในปีที่ยื่น ภ.ง.ด.50 แล้ว ⇒ ยืนยัน = เพิ่มเครดิตเข้าปีที่ยื่นไปแล้ว ⇒ 409 พร้อมทางไปต่อ
            // (ปลดล็อกแบบของปีนั้นก่อน · หรือแก้วันที่ตามใบจริง) — ใต้ล็อกระดับบริษัทที่ถือไว้ข้างบน (ไม่ชนกับการยื่นกลางทาง)
            await WhtCreditFiledYearGuard.EnsureNotFiledAsync(_db, companyId, "ยืนยันหนังสือรับรองนี้", ct, intake.CertificateDate);
            if (req.CreateStandalone && req.Allocations is { Count: > 0 })
                throw new BusinessRuleException("เลือกได้อย่างเดียว: จัดสรรเข้ารายการรอใบ หรือ บันทึกเป็นรายการเครดิตใหม่ — ไม่ใช่ทั้งสองอย่าง", RuleCode, 400);
            if (intake.WhtAmount <= 0m)
                throw new BusinessRuleException("ยอดภาษีที่ถูกหักต้องมากกว่า 0 — กรอกยอดตามหนังสือรับรองแล้วกดยืนยันอีกครั้ง", RuleCode);
            var co = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => new { c.TaxId, c.FiscalYearStartMonth })
                .FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException("ไม่พบบริษัท");
            if (ThaiTaxId.IsValid(intake.PayeeTaxId) && ThaiTaxId.IsValid(co.TaxId) && !ThaiTaxId.Same(intake.PayeeTaxId, co.TaxId))
                throw new BusinessRuleException("ผู้ถูกหักบนใบไม่ใช่บริษัทเรา — ใช้เป็นเครดิตไม่ได้ · กด \"ตีกลับ\" แล้วแจ้งลูกค้าให้ออกใบใหม่", RuleCode, 409);

            // ฝ่ายค้าน WP-09 H1: เลขที่ใบ (รวมที่ผู้ใช้แก้) + ผู้จ่าย ซ้ำกับทะเบียน หรือกับใบอื่นในกล่องรับที่ยังมีผล ⇒ 409
            var certNo = intake.CertificateNumber!;
            var payerTax = ThaiTaxId.Normalize(intake.PayerTaxId);
            // ฝ่ายค้าน WP-09 รอบสอง: ตัวเดียวกับการส่ง (ปีเดียวกัน · ใต้ล็อกเลขที่ใบ + ผู้จ่าย ที่ EnsureCertificateNotRecordedAsync ถือ)
            await _credits.EnsureCertificateNotRecordedAsync(companyId, certNo, payerTax, intake.PayerContactId, intake.CertificateDate, null,
                exceptIntakeId: intakeId, ct: ct);
            var live = await FindLiveByCertAsync(companyId, certNo, payerTax, intake.PayerContactId, intake.CertificateDate, co.FiscalYearStartMonth, intakeId, ct);
            if (live.Duplicate is not null)
                throw new BusinessRuleException($"หนังสือรับรองเลขที่ {certNo} ของผู้จ่ายรายนี้อยู่ในกล่องรับอีกใบแล้ว (ปีเดียวกัน) — ตีกลับใบนี้ หรือตรวจเลขที่อีกครั้ง", RuleCode, 409);
            // B-M2: ตัดสินผู้จ่ายไม่ได้ ⇒ ขอเลขผู้จ่ายก่อน (ไม่ลงเครดิตซ้ำ และไม่กลืนใบของลูกค้าอีกราย)
            // ฝ่ายค้าน L5: อีกใบไม่มีเลขผู้จ่าย (ใบนี้มี) ⇒ ทางไปต่อ = ติ๊ก "ยืนยันว่าไม่ใช่ใบซ้ำ" (บันทึกใน audit) — ใบนี้ต้องมีเลขผู้จ่ายที่ถูกต้อง
            if (live.UnknownPayerSameNumber && !(req.NotDuplicate && ThaiTaxId.IsValid(intake.PayerTaxId)))
                throw new BusinessRuleException($"หนังสือรับรองเลขที่ {certNo} (ปีเดียวกัน) อยู่ในกล่องรับอีกใบแล้ว แต่แยกไม่ได้ว่าเป็นผู้จ่ายรายเดียวกัน "
                    + "(ใบใดใบหนึ่งไม่มีเลขผู้เสียภาษีของผู้จ่าย) — กรอกเลขผู้จ่ายตามใบ แล้วติ๊ก \"ยืนยันว่าไม่ใช่ใบซ้ำ\" และกดยืนยันอีกครั้ง · ถ้าเป็นใบซ้ำ ให้ตีกลับ", RuleCode, 409);

            var existing = await _db.WhtCertAllocations.Where(a => a.CompanyId == companyId && a.IntakeId == intakeId).ToListAsync(ct);
            // แก้ CI รอบ 252 (Db H2_…): "บันทึกเป็นรายการใหม่" = ไม่จัดสรรเข้าแถวใดเลย — เดิม Allocations = null ไหลไปใช้ข้อเสนอของระบบ (Proposed) ⇒
            // ใบงวดสองคนละรอบที่ระบบเสนอแถวเดิมไว้ ชนด่าน H2 ทั้งที่ผู้ใช้เลือกทางไปต่อที่ด่านแนะนำเอง
            List<WhtCertAllocationInput> wanted = req.CreateStandalone ? new List<WhtCertAllocationInput>()
                : req.Allocations != null
                ? req.Allocations.Where(a => a.Amount > 0m).GroupBy(a => a.WhtCreditId).Select(g => new WhtCertAllocationInput(g.Key, R(g.Sum(x => x.Amount)))).ToList()
                : existing.Where(a => a.Status == WhtCertAllocationStatus.Proposed).Select(a => new WhtCertAllocationInput(a.WhtCreditId, a.Amount)).ToList();

            if (wanted.Count == 0 && !req.CreateStandalone)
                throw new BusinessRuleException("เลือกเอกสาร/รายการที่ใบนี้ครอบอย่างน้อยหนึ่งรายการ — หรือเลือก \"บันทึกเป็นรายการเครดิตใหม่\" ถ้าไม่มีรายการรอใบที่ตรง", RuleCode);
            if (R(wanted.Sum(a => a.Amount)) > intake.WhtAmount + WhtCertMatchPolicy.Tolerance)
                throw new BusinessRuleException($"ยอดที่จัดสรร {wanted.Sum(a => a.Amount):N2} เกินภาษีบนใบ {intake.WhtAmount:N2} — ลดยอดที่จัดสรรให้ไม่เกินใบ", RuleCode);

            var rows = new List<(WhtCreditReceived Row, decimal Amount)>();
            var reopened = new List<(WhtCreditReceived Row, string ReversalNo, string WriteOffNo)>();
            foreach (var a in wanted.OrderBy(x => x.WhtCreditId))
            {
                await LockCreditAsync(companyId, a.WhtCreditId, ct);
                var row = await LoadFreshAsync(companyId, a.WhtCreditId, ct)
                    ?? throw new KeyNotFoundException("ไม่พบรายการภาษีถูกหักที่เลือก");
                // รอบ 257 ชุด 2 (B-H1): แถวที่ได้ใบบางส่วนแล้วตัดสูญส่วนที่เหลือ ⇒ รับใบถัดไปได้ (กลับรายการตัดสูญใต้ล็อกแถวนี้ ด้านล่าง) — เดิม 409 = ทางตัน
                var reopen = WhtCreditWriteOff.ReopensForLateCertificate(row.Status, row.WriteOffJournalEntryId, row.WriteOffReversalJournalEntryId,
                    row.WhtAmount, row.ReceivedAmount);
                if (row.Status != WhtCreditStatus.Pending && !reopen)
                    throw new BusinessRuleException($"รายการ {row.PayerName} {row.WhtAmount:N2} ไม่ได้อยู่ในสถานะรอใบ ({row.Status}) — เลือกรายการอื่น", RuleCode, 409);
                if (reopen && journalActor == null)
                    throw new BusinessRuleException(WrittenOffNeedsJournalMessage, WhtCreditWriteOff.ReversalRuleCode, 403);
                // ฝ่ายค้าน WP-09 L3: รายการต้องเป็นของผู้จ่ายบนใบ — ต่างผู้จ่าย (รู้ทั้งสองฝั่ง) ⇒ ต้องติ๊กยืนยันโดยตั้งใจ (บันทึกใน audit)
                var rowPayer = ThaiTaxId.Normalize(string.IsNullOrWhiteSpace(row.PayerTaxId)
                    ? await _db.Contacts.AsNoTracking().Where(c => c.Id == row.PayerContactId && c.CompanyId == companyId).Select(c => c.TaxId).FirstOrDefaultAsync(ct)
                    : row.PayerTaxId);
                var payerMismatch = payerTax.Length == 13 && rowPayer.Length == 13 ? payerTax != rowPayer
                    : intake.PayerContactId != null && row.PayerContactId != null && intake.PayerContactId != row.PayerContactId;
                if (payerMismatch && !req.AllowOtherPayer)
                    throw new BusinessRuleException($"รายการ {row.PayerName} ไม่ใช่ของผู้จ่ายบนใบนี้ — ใบ 50 ทวิ ใช้เป็นหลักฐานของผู้จ่ายที่ออกใบเท่านั้น · "
                        + "ถ้าแน่ใจ (เช่น ผู้จ่ายเปลี่ยนชื่อ/สาขา) ให้ติ๊ก \"ยืนยันว่าเป็นผู้จ่ายเดียวกัน\"", RuleCode, 409);
                // ฝ่ายค้าน WP-09 H2: แถวที่มีใบแรกคนละรอบบัญชี ⇒ ลงรวมแล้วเครดิตทั้งแถวไปอยู่รอบของใบแรก (ผิดงวด) — ปฏิเสธ · ทางไปต่อ = รายการเครดิตใหม่
                if (WhtCertIntakeVerdict.CrossesCreditPeriod(row.CertificateDate, intake.CertificateDate.Value, co.FiscalYearStartMonth))
                    throw new BusinessRuleException($"รายการ {row.PayerName} มีหนังสือรับรองใบแรกลงวันที่ในรอบบัญชีอื่นแล้ว — ใบนี้อยู่คนละรอบ ลงรวมแถวเดียวไม่ได้ "
                        + "(ระบบนับงวดเครดิตตามวันที่ใบแรกของแถว) · เลือก \"บันทึกเป็นรายการเครดิตใหม่\" แทน", RuleCode, 409);
                var outstanding = R(row.WhtAmount - row.ReceivedAmount);
                if (a.Amount > outstanding + WhtCertMatchPolicy.Tolerance)
                    throw new BusinessRuleException($"ยอดที่จัดสรรให้รายการ {row.PayerName} ({a.Amount:N2}) เกินยอดที่ยังรอใบ ({outstanding:N2})", RuleCode);
                if (reopen)
                {
                    // ด่านทุกข้อผ่านแล้ว ⇒ กลับ JE ตัดสูญ (ตัวเดียวของทุกทาง · งวดวันนี้ต้องเปิด) → รอใบ → ลงใบด้านล่าง (ApplyToCredit ตัดสินสถานะ)
                    var (revNo, woNo) = await _credits.ReopenWrittenOffAsync(companyId, row, $"ได้รับหนังสือรับรอง {certNo} ผ่านกล่องรับ 50 ทวิ", journalActor!);
                    reopened.Add((row, revNo, woNo));
                }
                rows.Add((row, Math.Min(a.Amount, outstanding)));
            }

            if (req.CreateStandalone && rows.Count == 0)
            {
                // ฝ่ายค้าน WP-09 M5: ผู้จ่ายนี้มีรายการรอใบอยู่ ⇒ รายการใหม่ = ทะเบียนเกิน GL (ไม่มี Dr 11910 คู่) — ต้องระบุเหตุผล ·
                // ยกเว้นกรณี H2 (รายการรอใบของผู้จ่ายนี้มีใบแรกคนละรอบบัญชี — ทางไปต่อที่ระบบแนะนำเอง)
                var payerPending = (await LoadCandidatesAsync(companyId, null, ct)).Where(c =>
                    (payerTax.Length == 13 && ThaiTaxId.Normalize(c.Candidate.PayerTaxId) == payerTax)
                    || (intake.PayerContactId is Guid pc && c.Candidate.PayerContactId == pc)).ToList();
                var h2Case = payerPending.Any(c => WhtCertIntakeVerdict.CrossesCreditPeriod(c.CertificateDate, intake.CertificateDate.Value, co.FiscalYearStartMonth));
                if (payerPending.Count > 0 && !h2Case && (Trim(req.StandaloneReason)?.Length ?? 0) < 10)
                    throw new BusinessRuleException($"ผู้จ่ายรายนี้มีรายการรอ 50 ทวิ อยู่ {payerPending.Count} รายการ — เลือกรายการที่ใบนี้ครอบ · "
                        + "ถ้าเป็นการรับชำระนอกระบบจริง ให้ระบุเหตุผลอย่างน้อย 10 ตัวอักษร (บันทึกใน audit · ทะเบียนจะมากกว่ายอดบัญชี 11910)", RuleCode, 400);
                // ไม่มีการรับชำระในระบบที่ตรงกับใบ (ทางเดิมของการสแกนใบ 50 ทวิ) ⇒ รายการเครดิตใหม่ผ่านตัวสร้างทะเบียนตัวเดียว
                var taxYear = req.TaxYear ?? (intake.CertificateDate is DateTime d ? WhtCertIntakeVerdict.FiscalYearOf(d, co.FiscalYearStartMonth) : DateTime.UtcNow.Year);
                var newId = await _credits.CreateAsync(companyId, new WhtCreditService.UpsertRequest(
                    taxYear, intake.CertificateNumber, intake.CertificateDate, intake.PayerContactId, intake.PayerName, intake.PayerTaxId,
                    intake.PayerFormType, intake.IncomeTypeCode, intake.IncomeAmount, intake.WhtRate, intake.WhtAmount,
                    null, null, $"{StandaloneNotePrefix} ({intake.Channel})" + (Trim(req.StandaloneReason) is string why ? $" — {why}" : "")),
                    new WhtCreditService.AttachmentActor(userId, holdsTaxFile));
                var created = await LoadFreshAsync(companyId, newId, ct) ?? throw new KeyNotFoundException("ไม่พบรายการที่เพิ่งสร้าง");
                created.Status = WhtCreditStatus.Pending;   // ApplyToCredit ตั้ง Received เมื่อครบ — แหล่งเดียวของสถานะจากใบ
                created.ReceivedAmount = 0m;
                created.FirstWithheldAt ??= intake.CertificateDate ?? DateTime.UtcNow;
                created.CreatedFromIntakeId = intakeId;   // ฝ่ายค้าน L2: ถอนใบนี้ ⇒ ถอดแถวนี้ (ตัดสินจากคอลัมน์ ไม่ใช่หมายเหตุ)
                rows.Add((created, intake.WhtAmount));
            }

            foreach (var old in existing.Where(a => a.Status == WhtCertAllocationStatus.Proposed && !rows.Any(r => r.Row.Id == a.WhtCreditId)))
            {
                old.Status = WhtCertAllocationStatus.Reversed;
                old.UpdatedAt = DateTime.UtcNow;
            }
            foreach (var (row, amount) in rows)
            {
                var alloc = existing.FirstOrDefault(a => a.WhtCreditId == row.Id);
                if (alloc == null)
                {
                    alloc = new WhtCertAllocation { CompanyId = companyId, IntakeId = intakeId, WhtCreditId = row.Id };
                    _db.WhtCertAllocations.Add(alloc);
                }
                alloc.Amount = R(amount);
                alloc.Status = WhtCertAllocationStatus.Confirmed;
                alloc.PriorEvidenceKind = row.EvidenceKind;   // ฝ่ายค้าน L7: ถอนใบ ⇒ คืนชนิดหลักฐานเดิม
                alloc.UpdatedAt = DateTime.UtcNow;
                ApplyToCredit(row, R(amount), intake);
            }
            intake.Status = WhtCertIntakeRecordStatus.Confirmed;
            intake.ConfirmedByUserId = userId;
            intake.ConfirmedAt = DateTime.UtcNow;
            intake.UpdatedAt = DateTime.UtcNow;
            intake.UpdatedBy = userName;
            await AdoptBucketFileAsync(companyId, intake.FileAttachmentId, rows.Select(r => r.Row.Id).FirstOrDefault(), ct);
            // B-H1: เสนอตัดสูญส่วนที่ยังขาดใบของแถวที่เพิ่งกลับรายการตัดสูญ (ไม่ตัดเอง — ผู้ทำบัญชีตัดสิน)
            followUp = WrittenOffFollowUp(reopened.Select(x => (x.Row.PayerName, x.WriteOffNo, x.ReversalNo,
                WhtCreditableAmount.Missing(x.Row.Status, x.Row.WhtAmount, x.Row.ReceivedAmount))).ToList());
            Audit(companyId, userId, userName, intake, "wht-cert-intake-confirmed",
                new { before, allocations = rows.Select(r => new { r.Row.Id, r.Amount }).ToList(), req.AllowOtherPayer, standaloneReason = Trim(req.StandaloneReason),
                    viewedFile = req.ViewedFile, notDuplicate = req.NotDuplicate,
                    writeOffReversed = reopened.Select(x => new { x.Row.Id, x.WriteOffNo, x.ReversalNo }).ToList() });
            await _db.SaveChangesAsync(ct);
            feedbackId = intake.WhtCertAuthenticityAiFeedbackId;
            aiAnswer = intake.AuthenticityAnswer;
            // ฝ่ายค้าน WP-09 M6: สอน "ของจริง" เฉพาะเมื่อผู้ใช้เปิดดูใบแล้ว และไม่ได้ยืนยันทับคำตอบ "น่าสงสัย" ของ AI
            // (กดยืนยันโดยไม่ดูใบ = ไม่ใช่หลักฐานเรื่องความจริงของกระดาษ · ทับ Suspect = ความเห็นต่าง ไม่ใช่ป้ายที่เชื่อได้)
            learn = WhtCertIntakeVerdict.ShouldTeachGenuine(req.ViewedFile, intake.AuthenticityAnswer);
            return true;
        });

        // ปิดลูปกฎเหล็ก #1 — ผู้ใช้ยืนยัน = ใบนี้เป็นของจริง (หลัง commit: บันทึกการเรียนรู้ล้มต้องไม่ย้อนการยืนยันเครดิต)
        if (learn && feedbackId is Guid fb)
            await _feedback.RecordUserChoiceAsync(fb, WhtCertIntakeVerdict.Genuine, acceptedAi: aiAnswer == WhtCertIntakeVerdict.Genuine,
                ct, UserChoiceSource.Explicit);
        return (await ListCoreAsync(companyId, null, intakeId, ct)).Single() with { FollowUpNote = followUp };
    }

    /// <summary>B-H1: ข้อความหลังยืนยัน — กลับรายการตัดสูญแล้ว + ส่วนที่ยังขาดใบ (เสนอให้ตัดสูญส่วนนั้นได้ที่ทะเบียน) · ไม่มีแถวที่กลับรายการ ⇒ null</summary>
    internal static string? WrittenOffFollowUp(IReadOnlyList<(string PayerName, string WriteOffNo, string ReversalNo, decimal Missing)> reopened)
    {
        if (reopened.Count == 0) return null;
        return string.Join(" · ", reopened.Select(x =>
            $"รายการ {x.PayerName}: กลับรายการตัดสูญ {x.WriteOffNo} ด้วย {x.ReversalNo} แล้ว"
            + (x.Missing > 0.005m
                ? $" — ยังขาดใบ {x.Missing:N2} ถ้าไม่ได้ใบส่วนนี้ กด \"ตัดสูญ\" ที่รายการนั้นในทะเบียน (ตัดเฉพาะส่วนที่ขาด)"
                : " — ได้ใบครบแล้ว")));
    }

    // ═══════════ ถอนการยืนยัน (รอบ 257 ชุด 2 · B-H2) ═══════════

    /// <summary>
    /// ถอนการยืนยันใบที่ลงเครดิตไปแล้ว (ยืนยันผิด · ผู้จ่ายยกเลิกใบ — แจ้งผ่านคู่ค้า <c>PartnerWhtCertExchangeService.NotifyVoidedAsync</c>) —
    /// กลับการจัดสรรที่ยืนยันของใบนี้ · ลด <c>ReceivedAmount</c> ของแต่ละแถว · สถานะใหม่จาก <see cref="WhtCreditableAmount.StatusAfterUnconfirm"/> ·
    /// เลขที่/วันที่/ไฟล์ของใบนี้บนแถวถูกแทนด้วยใบที่ยังเหลือ (ไม่เหลือ ⇒ ล้าง) · แถวที่ "บันทึกเป็นรายการใหม่" จากใบนี้ (ไม่มี GL รองรับ) ⇒ ถอด ·
    /// แถวตัดสูญส่วนที่เหลือพร้อม JE ⇒ กลับรายการตัดสูญก่อน (ต้องมีผู้ลงบัญชี — 403) · <b>แถวที่ใช้เครดิตในแบบที่ยื่นแล้ว (Claimed) ⇒ 409</b> ·
    /// ใบกลายเป็น "ตีกลับ" พร้อมเหตุ (ส่งไฟล์ใหม่ได้) · ล็อกใบ + ล็อกแถว (คีย์เดียวกับยืนยัน/ตัดสูญ) · ธุรกรรมเดียว · audit chain · ไม่สอนนักเรียน
    /// </summary>
    public async Task<WhtCertIntakeDto> UnconfirmAsync(Guid companyId, Guid intakeId, WhtCertUnconfirmRequest req, Guid userId, string userName,
        CancellationToken ct = default, WhtCreditService.WriteOffActor? journalActor = null)
    {
        var reason = Trim(req?.Reason);
        if (reason == null || reason.Length < 5)
            throw new BusinessRuleException("ระบุเหตุผลที่ถอนการยืนยันอย่างน้อย 5 ตัวอักษร (เช่น ผู้จ่ายยกเลิกใบนี้ · ยืนยันผิดใบ) — เก็บเป็นหลักฐานในประวัติ", RuleCode);
        await InTxAsync(async () =>
        {
            // ฝ่ายค้าน M1: ล็อกระดับบริษัทก่อน (ตัวเดียวกับยื่น/ปลดล็อก ภ.ง.ด.50) → ใบ → แถว
            await WhtCreditFiledYearGuard.LockCompanyAsync(_db, companyId, ct);
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.WhtCreditWriteOff, "intake-" + intakeId.ToString("N")) }, ct);
            var intake = await _db.WhtCertIntakes.FirstOrDefaultAsync(i => i.Id == intakeId && i.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบใบ 50 ทวิ ในกล่องรับ");
            if (_db.Entry(intake).State == EntityState.Unchanged) await _db.Entry(intake).ReloadAsync(ct);
            if (intake.Status == WhtCertIntakeRecordStatus.Rejected) return true;   // ถอน/ตีกลับไปแล้ว = ผลเดิม
            if (intake.Status != WhtCertIntakeRecordStatus.Confirmed)
                throw new BusinessRuleException("ใบนี้ยังไม่ได้ยืนยันเป็นเครดิต — ไม่มีอะไรให้ถอน · ถ้าใบไม่ถูกต้อง ให้กด \"ตีกลับ\"", RuleCode, 409);

            var allocs = await _db.WhtCertAllocations
                .Where(a => a.CompanyId == companyId && a.IntakeId == intakeId && a.Status == WhtCertAllocationStatus.Confirmed)
                .OrderBy(a => a.WhtCreditId).ToListAsync(ct);

            // ฝ่ายค้าน L3: ตรวจทุกแถวก่อนแก้อะไรเลย (ล็อกแถวตามลำดับ Id) — ใช้เครดิตแล้ว / ปีที่ยื่น ภ.ง.ด.50 แล้ว / ต้องกลับตัดสูญ
            var plan = new List<(WhtCertAllocation Alloc, WhtCreditReceived? Row)>();
            foreach (var a in allocs)
            {
                await LockCreditAsync(companyId, a.WhtCreditId, ct);
                var row = await LoadFreshAsync(companyId, a.WhtCreditId, ct);
                plan.Add((a, row));
                if (row == null) continue;   // แถวถูกถอดไปแล้ว (ต้นทางยกเลิก) — ไม่มีเครดิตให้ลด
                if (row.Status == WhtCreditStatus.Claimed)
                    throw new BusinessRuleException($"รายการ {row.PayerName} {row.WhtAmount:N2} ใช้เป็นเครดิตในแบบ ภ.ง.ด.50 ที่ยื่นแล้ว — ถอนการยืนยันใบไม่ได้ · "
                        + "ถ้าต้องถอนจริง ให้ \"ปลดล็อก/กลับเป็นร่าง\" แบบ ภ.ง.ด.50 นั้นก่อน (แล้วยื่นเพิ่มเติมตามยอดใหม่)", WhtCreditClaimStamp.RuleCode, 409);
                // ฝ่ายค้าน H1: ล็อกตามปีภาษี (ไม่ใช่ตามสถานะแถว) — แถวได้ใบบางส่วนในปีที่ยื่นแล้วก็ถอนไม่ได้
                await WhtCreditFiledYearGuard.EnsureNotFiledAsync(_db, companyId, $"ถอนการยืนยันใบ (รายการ {row.PayerName})", ct, row.CertificateDate, intake.CertificateDate);
                if (WhtCreditWriteOff.NeedsReversal(row.Status, row.WriteOffJournalEntryId, row.WriteOffReversalJournalEntryId))
                {
                    if (journalActor == null)
                        throw new BusinessRuleException("รายการ " + row.PayerName + " ตัดสูญส่วนที่เหลือพร้อมรายการบัญชีแล้ว — ถอนใบต้องกลับรายการบัญชีตัดสูญ "
                            + "จึงต้องให้ผู้มีสิทธิ์จัดการใบสำคัญ (Journal.Manage) ทำ", WhtCreditWriteOff.ReversalRuleCode, 403);
                    if (!WhtCreditWriteOff.ReopensForLateCertificate(row.Status, row.WriteOffJournalEntryId, row.WriteOffReversalJournalEntryId,
                            row.WhtAmount, row.ReceivedAmount))
                        throw new BusinessRuleException("รายการ " + row.PayerName + " ตัดสูญทั้งแถวไว้แล้ว — ถอนใบของแถวนี้ไม่ได้ (ตรวจสมุดรายวันก่อน)",
                            WhtCreditWriteOff.ReversalRuleCode, 409);
                }
            }

            var changes = new List<object>();
            var affected = new List<Guid>();
            foreach (var (a, row) in plan)
            {
                a.Status = WhtCertAllocationStatus.Reversed;
                a.UpdatedAt = DateTime.UtcNow;
                if (row == null) continue;
                affected.Add(row.Id);
                var rowBefore = new { row.Id, status = row.Status.ToString(), row.ReceivedAmount, row.CertificateNumber, row.CertificateDate, evidence = row.EvidenceKind.ToString() };
                if (WhtCreditWriteOff.NeedsReversal(row.Status, row.WriteOffJournalEntryId, row.WriteOffReversalJournalEntryId))
                    await _credits.ReopenWrittenOffAsync(companyId, row, $"ถอนการยืนยันหนังสือรับรอง {intake.CertificateNumber}", journalActor!);
                // ใบอื่นที่ยังลงแถวนี้ (ยืนยันแล้ว) — เลขที่/วันที่/ไฟล์ของแถว = ของใบแรกที่ยังเหลือ · ยอดของใบอื่นไม่ถูกลด (ฝ่ายค้าน L4)
                var others = await (from x in _db.WhtCertAllocations.AsNoTracking()
                                    join i in _db.WhtCertIntakes.AsNoTracking() on x.IntakeId equals i.Id
                                    where x.CompanyId == companyId && i.CompanyId == companyId && x.WhtCreditId == row.Id && x.IntakeId != intakeId
                                          && x.Status == WhtCertAllocationStatus.Confirmed && i.Status == WhtCertIntakeRecordStatus.Confirmed
                                    orderby i.CertificateDate, i.ConfirmedAt
                                    select new { x.Amount, i.CertificateNumber, i.CertificateDate, i.FileAttachmentId, i.EvidenceKind }).ToListAsync(ct);
                // ฝ่ายค้าน L4: ถอนเฉพาะยอดที่ใบนี้ลง — ไม่เกิน (ยอดที่ได้ใบตอนนี้ − ยอดของใบอื่นที่ยังลงแถวนี้)
                var take = WhtCreditableAmount.UnconfirmTake(a.Amount, row.ReceivedAmount, others.Sum(o => o.Amount));
                row.ReceivedAmount = R(row.ReceivedAmount - take);
                if (row.Status is WhtCreditStatus.Pending or WhtCreditStatus.Received)
                    row.Status = WhtCreditableAmount.StatusAfterUnconfirm(row.WhtAmount, row.ReceivedAmount);
                var first = others.FirstOrDefault();
                if (row.CertificateNumber == intake.CertificateNumber)
                {
                    row.CertificateNumber = first?.CertificateNumber;
                    row.CertificateDate = first?.CertificateDate;
                }
                if (intake.FileAttachmentId != null && row.AttachmentId == intake.FileAttachmentId)
                    row.AttachmentId = others.Select(o => o.FileAttachmentId).FirstOrDefault(f => f != null);
                // ฝ่ายค้าน L7: ไม่เหลือใบ ⇒ ชนิดหลักฐานก่อนใบนี้ลง (ไม่รู้ ⇒ กระดาษ) · ยังเหลือ ⇒ ของใบแรกที่เหลือ
                row.EvidenceKind = first?.EvidenceKind ?? a.PriorEvidenceKind ?? WhtEvidenceKind.PaperCertificate;
                row.Notes = string.IsNullOrWhiteSpace(row.Notes) ? $"ถอนการยืนยันใบ 50 ทวิ {intake.CertificateNumber} ({take:N2}): {reason}"
                    : $"{row.Notes} | ถอนการยืนยันใบ 50 ทวิ {intake.CertificateNumber} ({take:N2}): {reason}";
                // ฝ่ายค้าน L2: แถวที่ "บันทึกเป็นรายการใหม่" จากใบนี้ (คอลัมน์ CreatedFromIntakeId เท่านั้น) — ไม่มี 11910 รองรับ · ไม่เหลือใบ ⇒ ถอดแถว
                if (row.CreatedFromIntakeId == intakeId && others.Count == 0 && row.ReceivedAmount <= 0.005m)
                    row.IsDeleted = true;
                row.UpdatedAt = DateTime.UtcNow;
                changes.Add(new { before = rowBefore, after = new { status = row.Status.ToString(), row.ReceivedAmount, row.CertificateNumber, removed = row.IsDeleted,
                    evidence = row.EvidenceKind.ToString() }, take });
            }
            // ฝ่ายค้าน L7: ไฟล์ที่ใบนี้ผูกเข้าแถว (จากถังก่อนบันทึก) ⇒ ถอดกลับเข้าถัง (ไม่เป็นหลักฐานของเครดิตที่ถอนแล้ว)
            await DetachBucketFileAsync(companyId, intake.FileAttachmentId, affected, ct);
            intake.Status = WhtCertIntakeRecordStatus.Rejected;
            intake.RejectedReason = "ถอนการยืนยัน: " + reason;
            intake.RejectedByUserId = userId;
            intake.RejectedAt = DateTime.UtcNow;
            intake.UpdatedAt = DateTime.UtcNow;
            intake.UpdatedBy = userName;
            Audit(companyId, userId, userName, intake, "wht-cert-intake-unconfirmed", new { reason, rows = changes });
            await _db.SaveChangesAsync(ct);
            return true;
        });
        return (await ListCoreAsync(companyId, null, intakeId, ct)).Single();
    }

    public async Task<WhtCertIntakeDto> RejectAsync(Guid companyId, Guid intakeId, WhtCertRejectRequest req, Guid userId, string userName,
        CancellationToken ct = default)
    {
        var reason = Trim(req?.Reason);
        if (reason == null || reason.Length < 5)
            throw new BusinessRuleException("ระบุเหตุผลที่ตีกลับอย่างน้อย 5 ตัวอักษร — ลูกค้าจะเห็นเหตุผลนี้เมื่อส่งใบใหม่", RuleCode);
        Guid? feedbackId = null; string? aiAnswer = null; var learn = false;
        await InTxAsync(async () =>
        {
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.WhtCreditWriteOff, "intake-" + intakeId.ToString("N")) }, ct);
            var intake = await _db.WhtCertIntakes.FirstOrDefaultAsync(i => i.Id == intakeId && i.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบใบ 50 ทวิ ในกล่องรับ");
            if (_db.Entry(intake).State == EntityState.Unchanged) await _db.Entry(intake).ReloadAsync(ct);
            if (intake.Status == WhtCertIntakeRecordStatus.Rejected) return true;
            if (intake.Status == WhtCertIntakeRecordStatus.Confirmed)
                throw new BusinessRuleException("ใบนี้ยืนยันเป็นเครดิตแล้ว — ตีกลับไม่ได้ · ถ้าลงผิด ให้แก้ที่รายการภาษีถูกหักโดยตรง", RuleCode, 409);
            intake.Status = WhtCertIntakeRecordStatus.Rejected;
            intake.RejectedReason = reason;
            intake.RejectedByUserId = userId;
            intake.RejectedAt = DateTime.UtcNow;
            intake.UpdatedAt = DateTime.UtcNow;
            intake.UpdatedBy = userName;
            var allocs = await _db.WhtCertAllocations.Where(a => a.CompanyId == companyId && a.IntakeId == intakeId
                && a.Status == WhtCertAllocationStatus.Proposed).ToListAsync(ct);
            foreach (var a in allocs) { a.Status = WhtCertAllocationStatus.Reversed; a.UpdatedAt = DateTime.UtcNow; }
            Audit(companyId, userId, userName, intake, "wht-cert-intake-rejected", new { reason, notGenuine = req!.NotGenuine });
            await _db.SaveChangesAsync(ct);
            feedbackId = intake.WhtCertAuthenticityAiFeedbackId;
            aiAnswer = intake.AuthenticityAnswer;
            learn = req.NotGenuine;
            return true;
        });
        // สอนนักเรียนเฉพาะเมื่อเหตุคือ "ไม่ใช่ของจริง" — เหตุอื่น (อ่านไม่ได้/ไม่ใช่ใบของเรา) ไม่ได้บอกอะไรเรื่องความจริงของใบ
        if (learn && feedbackId is Guid fb)
            await _feedback.RecordUserChoiceAsync(fb, WhtCertIntakeVerdict.Suspect, acceptedAi: aiAnswer == WhtCertIntakeVerdict.Suspect,
                ct, UserChoiceSource.Explicit);
        return (await ListCoreAsync(companyId, null, intakeId, ct)).Single();
    }

    // ═══════════ อ่าน ═══════════

    public Task<List<WhtCertIntakeDto>> ListInboxAsync(Guid companyId, string? status, CancellationToken ct = default)
    {
        WhtCertIntakeRecordStatus? st = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<WhtCertIntakeRecordStatus>(status.Trim(), true, out var parsed))
                throw new BusinessRuleException("ตัวกรองสถานะกล่องรับไม่ถูกต้อง — ใช้ได้เฉพาะ AwaitingConfirm / Confirmed / Rejected");
            st = parsed;
        }
        return ListCoreAsync(companyId, st, null, ct);
    }

    private async Task<List<WhtCertIntakeDto>> ListCoreAsync(Guid companyId, WhtCertIntakeRecordStatus? status, Guid? onlyId, CancellationToken ct)
    {
        var q = _db.WhtCertIntakes.AsNoTracking().Where(i => i.CompanyId == companyId);
        if (status != null) q = q.Where(i => i.Status == status.Value);
        if (onlyId != null) q = q.Where(i => i.Id == onlyId.Value);
        var intakes = await q.OrderBy(i => i.Status).ThenByDescending(i => i.CreatedAt).Take(500).ToListAsync(ct);
        if (intakes.Count == 0)
            return onlyId != null ? throw new KeyNotFoundException("ไม่พบใบ 50 ทวิ ในกล่องรับ") : new List<WhtCertIntakeDto>();
        var ids = intakes.Select(i => i.Id).ToList();
        var allocs = await (from a in _db.WhtCertAllocations.AsNoTracking()
                            join w in _db.WhtCreditsReceived.AsNoTracking() on a.WhtCreditId equals w.Id
                            where a.CompanyId == companyId && w.CompanyId == companyId && ids.Contains(a.IntakeId)
                            select new
                            {
                                a.IntakeId, a.WhtCreditId, a.Amount, a.Status, w.DocumentId, w.WhtAmount, w.ReceivedAmount,
                                DocNo = _db.Documents.Where(d => d.Id == w.DocumentId && d.CompanyId == companyId).Select(d => d.DocumentNumber).FirstOrDefault(),
                            }).ToListAsync(ct);
        // ฝ่ายค้าน WP-09 M6: ป้ายของคำตอบนักเรียน = "ผู้จ่ายรายนี้ยืนยันแล้ว N ใบ" (ไม่อ้างว่าตรวจกระดาษ)
        var payers = intakes.Select(i => i.PayerTaxId).Where(t => t != null).Distinct().ToList();
        var confirmedByPayer = (await _db.WhtCertIntakes.AsNoTracking()
                .Where(i => i.CompanyId == companyId && i.Status == WhtCertIntakeRecordStatus.Confirmed && i.PayerTaxId != null && payers.Contains(i.PayerTaxId))
                .GroupBy(i => i.PayerTaxId!).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.N);
        int PriorConfirmed(WhtCertIntake i) => i.PayerTaxId != null && confirmedByPayer.TryGetValue(i.PayerTaxId, out var n)
            ? n - (i.Status == WhtCertIntakeRecordStatus.Confirmed ? 1 : 0) : 0;
        var byIntake = allocs.GroupBy(a => a.IntakeId).ToDictionary(g => g.Key, g => g.Select(a => new WhtCertAllocationDto(
            a.WhtCreditId, a.Amount, a.Status.ToString(), a.DocumentId, a.DocNo, a.WhtAmount, R(a.WhtAmount - a.ReceivedAmount))).ToList());
        return intakes.Select(i => new WhtCertIntakeDto(
            i.Id, i.Channel.ToString(), i.Status.ToString(), i.EvidenceKind.ToString(),
            i.FileAttachmentId, i.OcrScanResultId, i.CertificateNumber, i.CertificateDate,
            i.PayerName, i.PayerTaxId, i.PayerBranchCode, i.PayerContactId, i.PayeeTaxId,
            i.IncomeTypeCode, i.IncomeAmount, i.WhtRate, i.WhtAmount, i.PayerFormType == WhtPayerFormType.Pnd3 ? "ภ.ง.ด.3" : "ภ.ง.ด.53",
            i.ExtractionConfidence, i.ExtractionConfidence is decimal c && c < WhtCertIntakeVerdict.ExtractionThreshold,
            i.MatchLevel, i.MatchReason,
            string.IsNullOrWhiteSpace(i.GateProblems) ? Array.Empty<string>() : i.GateProblems.Split(" · "),
            i.AuthenticityAnswer, i.AuthenticityConfidence, i.WhtCertAuthenticityUsedAi,
            WhtCertIntakeVerdict.AuthenticityLabel(i.AuthenticityAnswer, i.AuthenticityConfidence, i.WhtCertAuthenticityUsedAi, PriorConfirmed(i),
                byProvenance: i.AuthenticityByProvenance),
            i.AutoConfirmed, i.SubmittedByName, i.CreatedAt, i.ConfirmedAt, i.RejectedReason, i.RejectedAt,
            byIntake.TryGetValue(i.Id, out var list) ? list : new List<WhtCertAllocationDto>(),
            i.EvidenceReference, WhtEvidenceReference.Label(i.EvidenceKind, i.EvidenceReference)))
            .ToList();
    }

    public async Task<List<WhtCertCandidateDto>> CandidatesAsync(Guid companyId, Guid intakeId, CancellationToken ct = default)
    {
        var intake = await _db.WhtCertIntakes.AsNoTracking().FirstOrDefaultAsync(i => i.Id == intakeId && i.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบใบ 50 ทวิ ในกล่องรับ");
        var proposed = await _db.WhtCertAllocations.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.IntakeId == intakeId && a.Status != WhtCertAllocationStatus.Reversed)
            .Select(a => a.WhtCreditId).ToListAsync(ct);
        var all = await LoadCandidatesAsync(companyId, null, ct);
        var payer = ThaiTaxId.Normalize(intake.PayerTaxId);
        // ผู้จ่ายรายนี้ก่อน (เลขตรงเป๊ะ / ผู้ติดต่อเดียวกัน) — ไม่พบเลย ⇒ แสดงทุกรายการรอใบ ให้ผู้ใช้เลือกเอง
        var mine = all.Where(c => (payer.Length == 13 && ThaiTaxId.Normalize(c.Candidate.PayerTaxId) == payer)
            || (intake.PayerContactId is Guid pc && c.Candidate.PayerContactId == pc)).ToList();
        var list = mine.Count > 0 ? mine : all;
        return list.OrderByDescending(c => proposed.Contains(c.Candidate.CreditId)).ThenBy(c => c.Candidate.WithheldAt).Take(200)
            .Select(c => new WhtCertCandidateDto(c.Candidate.CreditId, c.Candidate.DocumentId, c.DocumentNumber, c.DocumentDate,
                c.PayerName, c.Candidate.PayerTaxId, c.TaxYear, c.WhtAmount, c.ReceivedAmount, c.Candidate.Outstanding,
                proposed.Contains(c.Candidate.CreditId), c.WrittenOff))
            .ToList();
    }

    public async Task<WhtCertTrackingDto> TrackingAsync(Guid companyId, Guid? contactId, int? taxYear = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        // ฝ่ายค้าน WP-09 L8: โหลดเฉพาะแถวที่ยังต้องตาม (Pending) ของปีที่ขอ — ไม่ดึงทะเบียนทั้งบริษัททุกปี
        var rows = (await WhtCertTracking.LoadAsync(_db, companyId, now, contactId: contactId, taxYear: taxYear,
                statuses: WhtCertTracking.FollowUpStatuses, ct: ct))
            .Where(r => WhtCertIntakeStatus.NeedsFollowUp(r.Verdict.State)).ToList();
        var awaitingQ = _db.WhtCertIntakes.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.Status == WhtCertIntakeRecordStatus.AwaitingConfirm);
        if (contactId != null) awaitingQ = awaitingQ.Where(i => i.PayerContactId == contactId);
        var awaiting = await awaitingQ.CountAsync(ct);
        decimal Missing(WhtCertTracking.Row r) => WhtCreditableAmount.Missing(r.Status, r.WhtAmount, r.ReceivedAmount);
        var top = rows.GroupBy(r => r.PayerContactId?.ToString() ?? r.PayerName)
            .Select(g => new { Name = g.First().PayerName, Amount = g.Sum(x => Missing(x)), Count = g.Count(), Oldest = g.Max(x => x.Verdict.DaysWaiting ?? 0) })
            .OrderByDescending(x => x.Amount).FirstOrDefault();
        var overdue = rows.Where(r => r.Verdict.IsOverdue).ToList();
        return new WhtCertTrackingDto(
            R(rows.Sum(x => Missing(x))), rows.Count, rows.Select(r => r.PayerContactId?.ToString() ?? r.PayerName).Distinct().Count(),
            overdue.Count, R(overdue.Sum(x => Missing(x))), awaiting,
            top?.Name, R(top?.Amount ?? 0m), top?.Count ?? 0, top?.Oldest ?? 0,
            rows.OrderByDescending(r => r.Verdict.IsOverdue).ThenByDescending(r => r.Verdict.DaysWaiting ?? 0).Take(20)
                .Select(r => new WhtCertTrackingItemDto(r.CreditId, r.DocumentId, r.DocumentNumber, r.PayerName, Missing(r),
                    r.Verdict.State.ToString(), r.Verdict.Label, r.Verdict.Tone, r.Verdict.DaysWaiting, r.Verdict.IsOverdue))
                .ToList());
    }

    public async Task<WhtCertMissingReportDto> MissingReportAsync(Guid companyId, int taxYear, CancellationToken ct = default)
    {
        var rows = await WhtCertTracking.LoadAsync(_db, companyId, DateTime.UtcNow, taxYear: taxYear, ct: ct);
        var missing = rows.Select(r => (Row: r, Missing: WhtCreditableAmount.Missing(r.Status, r.WhtAmount, r.ReceivedAmount)))
            .Where(x => x.Missing > 0.005m)
            .OrderBy(x => x.Row.PayerName).ThenBy(x => x.Row.WithheldSince ?? DateTime.MaxValue)
            .ToList();
        // ตัวเลขกระทบ GL — สูตรเดียวกับหน้าทะเบียน (WhtCreditService.SummaryAsync) ไม่ประกอบเอง
        var summary = await _credits.SummaryAsync(companyId, taxYear);
        // ฝ่ายค้าน WP-09 L9: ยอดที่ได้ใบ/ใช้แล้ว = กติกางวด ภ.ง.ด.50 (ตามวันที่บนใบ) ตัวเดียวกับหน้าทะเบียน/ภ.ง.ด.50 — ไม่รวมตามปีภาษีของแถวเอง
        var (received, claimed) = await _credits.CreditableSplitAsync(companyId, taxYear);
        return new WhtCertMissingReportDto(taxYear, missing.Count, R(missing.Sum(x => x.Missing)), received, claimed,
            summary.GlBalance, summary.Difference,
            missing.Select(x => new WhtCertMissingRowDto(x.Row.CreditId, x.Row.PayerName, x.Row.PayerTaxId, x.Row.DocumentId, x.Row.DocumentNumber,
                x.Row.WithheldSince, x.Row.IncomeAmount, x.Row.WhtAmount, x.Row.ReceivedAmount, x.Missing,
                x.Row.Verdict.State.ToString(), x.Row.Verdict.Label, x.Row.Verdict.DaysWaiting, x.Row.Verdict.IsOverdue, x.Row.LastChannel,
                x.Row.PayerContactId)).ToList());
    }

    // ═══════════ ภายใน ═══════════

    private sealed record Cand(WhtCertMatchPolicy.Candidate Candidate, int TaxYear, string? DocumentNumber, DateTime? DocumentDate,
        string PayerName, decimal WhtAmount, decimal ReceivedAmount, DateTime? CertificateDate, bool WrittenOff = false);

    /// <summary>แถวรอใบ (Pending · ยังค้าง) ของบริษัท — เลขผู้จ่าย = ที่บันทึกบนแถว ไม่มี ⇒ ของผู้ติดต่อ (รวมที่ถูกลบ — ผู้หักจริง) ·
    /// รอบ 257 ชุด 2 (B-H1): รวมแถวที่ได้ใบบางส่วนแล้วตัดสูญส่วนที่เหลือพร้อม JE ที่ยังมีผล (คิวรีเดียวกับ <see cref="WhtCreditWriteOff.ReopensForLateCertificate"/>) —
    /// ใบถัดไปลงแถวเดิมได้ (ยืนยัน = กลับรายการตัดสูญ)</summary>
    private async Task<List<Cand>> LoadCandidatesAsync(Guid companyId, Guid? contactId, CancellationToken ct)
    {
        var q = _db.WhtCreditsReceived.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.WhtAmount - w.ReceivedAmount > 0.005m
                && (w.Status == WhtCreditStatus.Pending
                    || (w.Status == WhtCreditStatus.Expired && w.WriteOffJournalEntryId != null && w.WriteOffReversalJournalEntryId == null
                        && w.ReceivedAmount > 0.005m)));
        if (contactId != null) q = q.Where(w => w.PayerContactId == contactId);
        var rows = await q.Select(w => new
        {
            w.Id, w.DocumentId, w.PayerTaxId, w.PayerContactId, w.PayerName, w.WhtAmount, w.ReceivedAmount, w.FirstWithheldAt, w.CreatedAt, w.TaxYear,
            w.CertificateDate, w.Status,
            ContactTaxId = _db.Contacts.Where(c => c.Id == w.PayerContactId && c.CompanyId == companyId).Select(c => c.TaxId).FirstOrDefault(),
            Doc = _db.Documents.Where(d => d.Id == w.DocumentId && d.CompanyId == companyId).Select(d => new { d.DocumentNumber, d.DocumentDate }).FirstOrDefault(),
        }).Take(2000).ToListAsync(ct);
        return rows.Select(w => new Cand(
            new WhtCertMatchPolicy.Candidate(w.Id, w.DocumentId, string.IsNullOrWhiteSpace(w.PayerTaxId) ? w.ContactTaxId : w.PayerTaxId,
                w.PayerContactId, R(w.WhtAmount - w.ReceivedAmount), w.FirstWithheldAt ?? w.CreatedAt),
            w.TaxYear, w.Doc?.DocumentNumber, w.Doc?.DocumentDate, w.PayerName, w.WhtAmount, w.ReceivedAmount, w.CertificateDate,
            w.Status == WhtCreditStatus.Expired)).ToList();
    }

    /// <summary>ลงใบเข้าแถวเครดิต — ผู้เขียน <c>ReceivedAmount</c> ตัวเดียว · ครบยอด ⇒ Received (เครดิตได้เต็มแถว) · ไม่ครบ ⇒ Pending (เครดิตเฉพาะส่วนที่ได้ใบ)</summary>
    private static void ApplyToCredit(WhtCreditReceived row, decimal amount, WhtCertIntake intake)
    {
        row.ReceivedAmount = R(Math.Min(row.WhtAmount, row.ReceivedAmount + amount));
        if (string.IsNullOrWhiteSpace(row.CertificateNumber)) row.CertificateNumber = intake.CertificateNumber;
        else if (intake.CertificateNumber != null && row.CertificateNumber != intake.CertificateNumber)
            row.Notes = string.IsNullOrWhiteSpace(row.Notes) ? $"ใบ 50 ทวิ เพิ่ม {intake.CertificateNumber} ({amount:N2})"
                : $"{row.Notes} | ใบ 50 ทวิ เพิ่ม {intake.CertificateNumber} ({amount:N2})";
        row.CertificateDate ??= intake.CertificateDate;
        row.AttachmentId ??= intake.FileAttachmentId;
        if (string.IsNullOrWhiteSpace(row.PayerTaxId)) row.PayerTaxId = intake.PayerTaxId;
        if (string.IsNullOrWhiteSpace(row.IncomeTypeCode)) row.IncomeTypeCode = intake.IncomeTypeCode;
        row.EvidenceKind = intake.EvidenceKind;
        // ฝ่ายค้าน WP-09 L2 (DECISIONS 252-WP09-6): ใบต่ำกว่ายอดถูกหักไม่เกิน ฿1 (ปัดเศษของผู้จ่าย) ⇒ ปิดแถวเป็น Received (ไม่ค้างทวงตลอดไป) ·
        // เครดิตยังเท่ายอดบนใบ (WhtCreditableAmount.Of — Received ที่ ReceivedAmount > 0 = ยอดที่ได้ใบ) · เศษที่เหลือตัดสูญได้ (WhtCreditWriteOff.AmountOf)
        var shortBy = R(row.WhtAmount - row.ReceivedAmount);
        row.Status = WhtCreditableAmount.StatusForReceived(row.WhtAmount, row.ReceivedAmount);
        if (row.Status == WhtCreditStatus.Received && shortBy > 0.005m)
            row.Notes = (string.IsNullOrWhiteSpace(row.Notes) ? "" : row.Notes + " | ")
                + $"ใบ 50 ทวิ ต่ำกว่ายอดถูกหัก {shortBy:N2} (ภายในเกณฑ์ ±฿{WhtCertMatchPolicy.Tolerance:N0}) — เครดิตตามยอดบนใบ · ส่วนต่างตัดสูญได้";
        row.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>ไฟล์ที่อัปโหลดไว้ในถังก่อนบันทึก (WhtCredit/0000…) ⇒ ผูกกับแถวเครดิตแรกที่ใบครอบ (สิทธิ์ดูไฟล์ = สิทธิ์ทะเบียนภาษีถูกหัก) ·
    /// ไฟล์ชนิดอื่น (สแกนเอกสาร) คงที่เดิม — แถวเครดิตชี้ไฟล์ผ่าน AttachmentId</summary>
    private async Task AdoptBucketFileAsync(Guid companyId, Guid? fileId, Guid creditId, CancellationToken ct)
    {
        if (fileId is not Guid id || creditId == Guid.Empty) return;
        var file = await _db.FileAttachments.FirstOrDefaultAsync(f => f.Id == id && f.CompanyId == companyId, ct);
        if (file == null || file.EntityId != Guid.Empty
            || AttachmentPermissionScope.WhtCreditFileLink(file.EntityType, file.EntityId, creditId) != WhtCreditFileLinkKind.AdoptFromBucket) return;
        file.EntityId = creditId;
        file.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>ฝ่ายค้าน L7 (คู่ของ <see cref="AdoptBucketFileAsync"/>): ไฟล์ที่ผูกเข้าแถวเครดิตของใบที่ถูกถอน ⇒ กลับเข้าถังก่อนบันทึก (WhtCredit/0000…) ·
    /// ไฟล์ชนิดอื่น/ของแถวอื่นไม่แตะ</summary>
    private async Task DetachBucketFileAsync(Guid companyId, Guid? fileId, IReadOnlyCollection<Guid> creditIds, CancellationToken ct)
    {
        if (fileId is not Guid id || creditIds.Count == 0) return;
        var file = await _db.FileAttachments.FirstOrDefaultAsync(f => f.Id == id && f.CompanyId == companyId, ct);
        if (file == null || !creditIds.Contains(file.EntityId)
            || AttachmentPermissionScope.WhtCreditFileLink(file.EntityType, file.EntityId, file.EntityId) != WhtCreditFileLinkKind.AlreadyThis) return;
        file.EntityId = Guid.Empty;
        file.UpdatedAt = DateTime.UtcNow;
    }

    private async Task<string?> FileSha256Async(Guid companyId, Guid fileId, CancellationToken ct)
    {
        if (_storage == null) return null;
        var path = await _db.FileAttachments.AsNoTracking().Where(f => f.Id == fileId && f.CompanyId == companyId)
            .Select(f => f.StoragePath).FirstOrDefaultAsync(ct);
        if (path == null) return null;
        var bytes = await _storage.ReadAllBytesAsync(path, ct);
        return bytes == null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>ล็อกแถวเครดิต — คีย์เดียวกับการตัดสูญ/แก้แถว (<c>WhtCreditService.LockCreditAsync</c>) ⇒ ยืนยันใบชนกับตัดสูญไม่ได้</summary>
    private Task LockCreditAsync(Guid companyId, Guid creditId, CancellationToken ct)
        => _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.WhtCreditWriteOff, creditId.ToString("N")) }, ct);

    private async Task<WhtCreditReceived?> LoadFreshAsync(Guid companyId, Guid id, CancellationToken ct)
    {
        var e = await _db.WhtCreditsReceived.FirstOrDefaultAsync(w => w.Id == id && w.CompanyId == companyId, ct);
        if (e != null && _db.Entry(e).State == EntityState.Unchanged) await _db.Entry(e).ReloadAsync(ct);
        return e;
    }

    private void Audit(Guid companyId, Guid? userId, string? userName, WhtCertIntake intake, string action, object detail)
        => _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, UserEmail = userName ?? "system",
            EntityType = nameof(WhtCertIntake), EntityId = intake.Id.ToString(),
            Action = action == "wht-cert-intake-rejected" ? AuditAction.Reject
                : action == "wht-cert-intake-unconfirmed" ? AuditAction.Update
                : action == "wht-cert-intake-submitted" ? AuditAction.Create : AuditAction.Approve,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action, ruleCode = RuleCode, legalReference = LegalReference,
                status = intake.Status.ToString(), channel = intake.Channel.ToString(), intake.CertificateNumber, intake.PayerTaxId,
                intake.WhtAmount, matchLevel = intake.MatchLevel, gateProblems = intake.GateProblems,
                authenticity = intake.AuthenticityAnswer, authenticityConfidence = intake.AuthenticityConfidence,
                usedAi = intake.WhtCertAuthenticityUsedAi, byProvenance = intake.AuthenticityByProvenance, autoConfirmed = intake.AutoConfirmed, detail,
            }),
            Timestamp = DateTime.UtcNow,
        });

    private static object Snapshot(WhtCertIntake i) => new
    {
        i.CertificateNumber, i.CertificateDate, i.PayerName, i.PayerTaxId, i.IncomeTypeCode, i.IncomeAmount, i.WhtRate, i.WhtAmount,
    };

    private async Task<T> InTxAsync<T>(Func<Task<T>> work)
    {
        if (_db.Database.CurrentTransaction != null) return await work();
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                var result = await work();
                await tx.CommitAsync();
                return result;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();   // ถอดใบ/allocation/audit ที่ค้าง — ไม่ให้ไหลไปกับ SaveChanges ถัดไป
                throw;
            }
        });
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

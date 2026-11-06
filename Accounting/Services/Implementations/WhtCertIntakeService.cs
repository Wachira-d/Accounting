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
        ILogger<WhtCertIntakeService> logger, Accounting.Services.Storage.IFileStorage? storage = null)
    {
        _db = db; _ai = ai; _feedback = feedback; _credits = credits; _logger = logger; _storage = storage;
    }

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

        // (1b) กันซ้ำ — ใบเดิม (เลขที่ + ผู้จ่าย) ที่ยังไม่ถูกตีกลับ · สแกนใบเดียวกันหลังลูกค้าอัปโหลดแล้ว ⇒ ไม่มีเครดิตซ้ำ
        var certNo = Trim(s.CertificateNumber);
        var payerTax = ThaiTaxId.Normalize(s.PayerTaxId);
        if (certNo != null)
        {
            var sameNo = await _db.WhtCertIntakes.AsNoTracking()
                .Where(i => i.CompanyId == companyId && i.CertificateNumber == certNo && i.Status != WhtCertIntakeRecordStatus.Rejected)
                .Select(i => new { i.Id, i.Status, i.PayerTaxId }).ToListAsync(ct);
            var dupCert = sameNo.FirstOrDefault(i => payerTax.Length != 13 || ThaiTaxId.Normalize(i.PayerTaxId) == payerTax);
            if (dupCert != null)
                return new(dupCert.Id, dupCert.Status.ToString(), false, true, $"หนังสือรับรองเลขที่ {certNo} ของผู้จ่ายรายนี้อยู่ในกล่องรับแล้ว — ไม่สร้างรายการซ้ำ");
            // ฝ่ายค้าน WP-09 H1: ใบเดียวกันอยู่ในทะเบียนแล้ว (กรอกมือ/ได้รับใบแล้ว/รุ่นก่อนกล่องรับ) ⇒ ปฏิเสธ 409 — ไม่ให้เครดิตซ้ำ
            await EnsureNotInRegisterAsync(companyId, certNo, payerTax, s.PayerContactId, ct);
        }

        var co = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => new { c.TaxId, c.FiscalYearStartMonth }).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");
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

        // (5) ตัดสิน
        var outcome = WhtCertIntakeVerdict.Decide(match.Level, gates, intake.AuthenticityAnswer, intake.AuthenticityConfidence, usable);

        // (6) เขียนในธุรกรรมเดียว · ฝ่ายค้าน WP-09 L10: อัปโหลดไฟล์เดียวกันพร้อมกันสองคำขอ ⇒ unique index ชน ⇒ ตอบ "ซ้ำ" (ไม่ใช่ 500)
        try
        {
        await InTxAsync(async () =>
        {
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
                    // ยอดค้างเปลี่ยนระหว่างตรวจ (อีกใบเพิ่งยืนยัน) ⇒ ไม่ auto — ให้คนดู (ไม่ลงเกิน)
                    if (row == null || row.Status != WhtCreditStatus.Pending
                        || a.Amount > R(row.WhtAmount - row.ReceivedAmount) + WhtCertMatchPolicy.Tolerance)
                    {
                        outcome = WhtCertIntakeVerdict.Outcome.AwaitUser;
                        gates.Add("ยอดค้างของรายการเปลี่ยนระหว่างตรวจ — ตรวจแล้วกดยืนยันอีกครั้ง");
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
                WhtCertIntakeVerdict.Outcome.AutoConfirm => "wht-cert-intake-auto-confirmed",
                WhtCertIntakeVerdict.Outcome.Reject => "wht-cert-intake-rejected",
                _ => "wht-cert-intake-submitted",
            }, allocs.Select(a => new { a.CreditId, a.Amount }).ToList());
            await _db.SaveChangesAsync(ct);
            return true;
        });
        }
        catch (DbUpdateException dup) when (sha != null && dup.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            if (await FindLiveByFileAsync(companyId, sha, ct) is { } winner) return winner;
            throw;
        }

        var msg = outcome switch
        {
            // ฝ่ายค้าน WP-09 M6: นักเรียนตอบจากประวัติผู้จ่าย (ไม่ได้อ่านกระดาษ) ⇒ ข้อความต้องไม่อ้างว่าตรวจกระดาษแล้ว
            WhtCertIntakeVerdict.Outcome.AutoConfirm => intake.WhtCertAuthenticityUsedAi
                ? "ยืนยันอัตโนมัติแล้ว — ใบตรงกับรายการรอใบ ผ่านด่านทุกข้อ และ AI ตรวจแล้วดูเป็นใบจริง (ใช้เป็นเครดิตได้)"
                : "ยืนยันอัตโนมัติแล้ว — ใบตรงกับรายการรอใบ ผ่านด่านทุกข้อ และผู้จ่ายรายนี้มีประวัติใบที่ยืนยันแล้ว (ระบบไม่ได้ตรวจกระดาษใบนี้ — เปิดดูได้ที่กล่องรับ)",
            WhtCertIntakeVerdict.Outcome.Reject => match.Reason,
            _ => "ได้รับแล้ว — รอตรวจและกดยืนยัน: " + (gates.Count > 0 ? gates[0] : "การตรวจของจริงยังไม่มั่นใจพอจะยืนยันอัตโนมัติ"),
        };
        return new(intake.Id, intake.Status.ToString(), intake.AutoConfirmed, false, msg);
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

    /// <summary>ฝ่ายค้าน WP-09 H1: ใบ (เลขที่ + ผู้จ่าย) อยู่ในทะเบียนเครดิตแล้ว (ไม่ถูกลบ · ไม่ใช่ตัดสูญ) ⇒ 409 · ผู้จ่ายตัดสินด้วยเลขผู้เสียภาษีของแถว
    /// (ไม่มี ⇒ ของผู้ติดต่อ) · ใบไม่มีเลขผู้จ่าย ⇒ ผู้ติดต่อเดียวกัน · <paramref name="exceptCreditIds"/> = แถวที่ใบนี้เองเพิ่งลงไว้</summary>
    private async Task EnsureNotInRegisterAsync(Guid companyId, string certNo, string payerTax, Guid? payerContactId, CancellationToken ct,
        IReadOnlyCollection<Guid>? exceptCreditIds = null)
    {
        var rows = await _db.WhtCreditsReceived.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.CertificateNumber == certNo && w.Status != WhtCreditStatus.Expired)
            .Select(w => new
            {
                w.Id, w.PayerTaxId, w.PayerContactId, w.WhtAmount,
                ContactTaxId = _db.Contacts.Where(c => c.Id == w.PayerContactId && c.CompanyId == companyId).Select(c => c.TaxId).FirstOrDefault(),
            }).ToListAsync(ct);
        var hit = rows.FirstOrDefault(w => (exceptCreditIds == null || !exceptCreditIds.Contains(w.Id))
            && (payerTax.Length == 13
                ? ThaiTaxId.Normalize(string.IsNullOrWhiteSpace(w.PayerTaxId) ? w.ContactTaxId : w.PayerTaxId) == payerTax
                : payerContactId != null && w.PayerContactId == payerContactId));
        if (hit != null)
            throw new BusinessRuleException($"หนังสือรับรองเลขที่ {certNo} ของผู้จ่ายรายนี้อยู่ในทะเบียนภาษีถูกหักแล้ว ({hit.WhtAmount:N2}) — ไม่บันทึกซ้ำ · "
                + "ถ้าเป็นใบคนละใบ ตรวจเลขที่บนใบอีกครั้ง", RuleCode, 409);
    }

    public async Task<WhtCertSubmitResult> SubmitFromScanAsync(Guid companyId, Guid scanResultId, WhtCertIntakeChannel channel,
        IReadOnlyList<Guid>? documentHints, string submittedBy, Guid? submittedByUserId, CancellationToken ct = default,
        WhtEvidenceKind evidenceKind = WhtEvidenceKind.PaperCertificate, string? evidenceReference = null, string? fileSha256 = null)
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
            AmountNeedsReview: m.AmountNeedsReview || m.AmountUnknown, EvidenceReference: evidenceReference), ct);
    }

    // ═══════════ ผู้ใช้ตัดสิน ═══════════

    public async Task<WhtCertIntakeDto> ConfirmAsync(Guid companyId, Guid intakeId, WhtCertConfirmRequest req, Guid userId, string userName,
        bool holdsTaxFile, CancellationToken ct = default)
    {
        req ??= new WhtCertConfirmRequest();
        Guid? feedbackId = null; string? aiAnswer = null; var learn = false;
        await InTxAsync(async () =>
        {
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
            var otherIntakes = await _db.WhtCertIntakes.AsNoTracking()
                .Where(i => i.CompanyId == companyId && i.Id != intakeId && i.CertificateNumber == certNo && i.Status != WhtCertIntakeRecordStatus.Rejected)
                .Select(i => i.PayerTaxId).ToListAsync(ct);
            if (otherIntakes.Any(t => payerTax.Length != 13 || ThaiTaxId.Normalize(t) == payerTax))
                throw new BusinessRuleException($"หนังสือรับรองเลขที่ {certNo} ของผู้จ่ายรายนี้อยู่ในกล่องรับอีกใบแล้ว — ตีกลับใบนี้ หรือตรวจเลขที่อีกครั้ง", RuleCode, 409);
            await EnsureNotInRegisterAsync(companyId, certNo, payerTax, intake.PayerContactId, ct);
            var certFy = WhtCertIntakeVerdict.FiscalYearOf(intake.CertificateDate.Value, co.FiscalYearStartMonth);

            var existing = await _db.WhtCertAllocations.Where(a => a.CompanyId == companyId && a.IntakeId == intakeId).ToListAsync(ct);
            List<WhtCertAllocationInput> wanted = req.Allocations != null
                ? req.Allocations.Where(a => a.Amount > 0m).GroupBy(a => a.WhtCreditId).Select(g => new WhtCertAllocationInput(g.Key, R(g.Sum(x => x.Amount)))).ToList()
                : existing.Where(a => a.Status == WhtCertAllocationStatus.Proposed).Select(a => new WhtCertAllocationInput(a.WhtCreditId, a.Amount)).ToList();

            if (wanted.Count == 0 && !req.CreateStandalone)
                throw new BusinessRuleException("เลือกเอกสาร/รายการที่ใบนี้ครอบอย่างน้อยหนึ่งรายการ — หรือเลือก \"บันทึกเป็นรายการเครดิตใหม่\" ถ้าไม่มีรายการรอใบที่ตรง", RuleCode);
            if (R(wanted.Sum(a => a.Amount)) > intake.WhtAmount + WhtCertMatchPolicy.Tolerance)
                throw new BusinessRuleException($"ยอดที่จัดสรร {wanted.Sum(a => a.Amount):N2} เกินภาษีบนใบ {intake.WhtAmount:N2} — ลดยอดที่จัดสรรให้ไม่เกินใบ", RuleCode);

            var rows = new List<(WhtCreditReceived Row, decimal Amount)>();
            foreach (var a in wanted.OrderBy(x => x.WhtCreditId))
            {
                await LockCreditAsync(companyId, a.WhtCreditId, ct);
                var row = await LoadFreshAsync(companyId, a.WhtCreditId, ct)
                    ?? throw new KeyNotFoundException("ไม่พบรายการภาษีถูกหักที่เลือก");
                if (row.Status != WhtCreditStatus.Pending)
                    throw new BusinessRuleException($"รายการ {row.PayerName} {row.WhtAmount:N2} ไม่ได้อยู่ในสถานะรอใบ ({row.Status}) — เลือกรายการอื่น", RuleCode, 409);
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
                if (row.CertificateDate is DateTime firstCert && WhtCertIntakeVerdict.FiscalYearOf(firstCert, co.FiscalYearStartMonth) != certFy)
                    throw new BusinessRuleException($"รายการ {row.PayerName} มีหนังสือรับรองใบแรกลงวันที่ในรอบบัญชีอื่นแล้ว — ใบนี้อยู่คนละรอบ ลงรวมแถวเดียวไม่ได้ "
                        + "(ระบบนับงวดเครดิตตามวันที่ใบแรกของแถว) · เลือก \"บันทึกเป็นรายการเครดิตใหม่\" แทน", RuleCode, 409);
                var outstanding = R(row.WhtAmount - row.ReceivedAmount);
                if (a.Amount > outstanding + WhtCertMatchPolicy.Tolerance)
                    throw new BusinessRuleException($"ยอดที่จัดสรรให้รายการ {row.PayerName} ({a.Amount:N2}) เกินยอดที่ยังรอใบ ({outstanding:N2})", RuleCode);
                rows.Add((row, Math.Min(a.Amount, outstanding)));
            }

            if (req.CreateStandalone && rows.Count == 0)
            {
                // ฝ่ายค้าน WP-09 M5: ผู้จ่ายนี้มีรายการรอใบอยู่ ⇒ รายการใหม่ = ทะเบียนเกิน GL (ไม่มี Dr 11910 คู่) — ต้องระบุเหตุผล ·
                // ยกเว้นกรณี H2 (รายการรอใบของผู้จ่ายนี้มีใบแรกคนละรอบบัญชี — ทางไปต่อที่ระบบแนะนำเอง)
                var payerPending = (await LoadCandidatesAsync(companyId, null, ct)).Where(c =>
                    (payerTax.Length == 13 && ThaiTaxId.Normalize(c.Candidate.PayerTaxId) == payerTax)
                    || (intake.PayerContactId is Guid pc && c.Candidate.PayerContactId == pc)).ToList();
                var h2Case = payerPending.Any(c => c.CertificateDate is DateTime fc && WhtCertIntakeVerdict.FiscalYearOf(fc, co.FiscalYearStartMonth) != certFy);
                if (payerPending.Count > 0 && !h2Case && (Trim(req.StandaloneReason)?.Length ?? 0) < 10)
                    throw new BusinessRuleException($"ผู้จ่ายรายนี้มีรายการรอ 50 ทวิ อยู่ {payerPending.Count} รายการ — เลือกรายการที่ใบนี้ครอบ · "
                        + "ถ้าเป็นการรับชำระนอกระบบจริง ให้ระบุเหตุผลอย่างน้อย 10 ตัวอักษร (บันทึกใน audit · ทะเบียนจะมากกว่ายอดบัญชี 11910)", RuleCode, 400);
                // ไม่มีการรับชำระในระบบที่ตรงกับใบ (ทางเดิมของการสแกนใบ 50 ทวิ) ⇒ รายการเครดิตใหม่ผ่านตัวสร้างทะเบียนตัวเดียว
                var taxYear = req.TaxYear ?? (intake.CertificateDate is DateTime d ? WhtCertIntakeVerdict.FiscalYearOf(d, co.FiscalYearStartMonth) : DateTime.UtcNow.Year);
                var newId = await _credits.CreateAsync(companyId, new WhtCreditService.UpsertRequest(
                    taxYear, intake.CertificateNumber, intake.CertificateDate, intake.PayerContactId, intake.PayerName, intake.PayerTaxId,
                    intake.PayerFormType, intake.IncomeTypeCode, intake.IncomeAmount, intake.WhtRate, intake.WhtAmount,
                    null, null, $"สร้างจากใบ 50 ทวิ ในกล่องรับ ({intake.Channel})" + (Trim(req.StandaloneReason) is string why ? $" — {why}" : "")),
                    new WhtCreditService.AttachmentActor(userId, holdsTaxFile));
                var created = await LoadFreshAsync(companyId, newId, ct) ?? throw new KeyNotFoundException("ไม่พบรายการที่เพิ่งสร้าง");
                created.Status = WhtCreditStatus.Pending;   // ApplyToCredit ตั้ง Received เมื่อครบ — แหล่งเดียวของสถานะจากใบ
                created.ReceivedAmount = 0m;
                created.FirstWithheldAt ??= intake.CertificateDate ?? DateTime.UtcNow;
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
                alloc.UpdatedAt = DateTime.UtcNow;
                ApplyToCredit(row, R(amount), intake);
            }
            intake.Status = WhtCertIntakeRecordStatus.Confirmed;
            intake.ConfirmedByUserId = userId;
            intake.ConfirmedAt = DateTime.UtcNow;
            intake.UpdatedAt = DateTime.UtcNow;
            intake.UpdatedBy = userName;
            await AdoptBucketFileAsync(companyId, intake.FileAttachmentId, rows.Select(r => r.Row.Id).FirstOrDefault(), ct);
            Audit(companyId, userId, userName, intake, "wht-cert-intake-confirmed",
                new { before, allocations = rows.Select(r => new { r.Row.Id, r.Amount }).ToList(), req.AllowOtherPayer, standaloneReason = Trim(req.StandaloneReason),
                    viewedFile = req.ViewedFile });
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
            WhtCertIntakeVerdict.AuthenticityLabel(i.AuthenticityAnswer, i.AuthenticityConfidence, i.WhtCertAuthenticityUsedAi, PriorConfirmed(i)),
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
                proposed.Contains(c.Candidate.CreditId)))
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
        string PayerName, decimal WhtAmount, decimal ReceivedAmount, DateTime? CertificateDate);

    /// <summary>แถวรอใบ (Pending · ยังค้าง) ของบริษัท — เลขผู้จ่าย = ที่บันทึกบนแถว ไม่มี ⇒ ของผู้ติดต่อ (รวมที่ถูกลบ — ผู้หักจริง)</summary>
    private async Task<List<Cand>> LoadCandidatesAsync(Guid companyId, Guid? contactId, CancellationToken ct)
    {
        var q = _db.WhtCreditsReceived.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.Status == WhtCreditStatus.Pending && w.WhtAmount - w.ReceivedAmount > 0.005m);
        if (contactId != null) q = q.Where(w => w.PayerContactId == contactId);
        var rows = await q.Select(w => new
        {
            w.Id, w.DocumentId, w.PayerTaxId, w.PayerContactId, w.PayerName, w.WhtAmount, w.ReceivedAmount, w.FirstWithheldAt, w.CreatedAt, w.TaxYear,
            w.CertificateDate,
            ContactTaxId = _db.Contacts.Where(c => c.Id == w.PayerContactId && c.CompanyId == companyId).Select(c => c.TaxId).FirstOrDefault(),
            Doc = _db.Documents.Where(d => d.Id == w.DocumentId && d.CompanyId == companyId).Select(d => new { d.DocumentNumber, d.DocumentDate }).FirstOrDefault(),
        }).Take(2000).ToListAsync(ct);
        return rows.Select(w => new Cand(
            new WhtCertMatchPolicy.Candidate(w.Id, w.DocumentId, string.IsNullOrWhiteSpace(w.PayerTaxId) ? w.ContactTaxId : w.PayerTaxId,
                w.PayerContactId, R(w.WhtAmount - w.ReceivedAmount), w.FirstWithheldAt ?? w.CreatedAt),
            w.TaxYear, w.Doc?.DocumentNumber, w.Doc?.DocumentDate, w.PayerName, w.WhtAmount, w.ReceivedAmount, w.CertificateDate)).ToList();
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
                : action == "wht-cert-intake-submitted" ? AuditAction.Create : AuditAction.Approve,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action, ruleCode = RuleCode, legalReference = LegalReference,
                status = intake.Status.ToString(), channel = intake.Channel.ToString(), intake.CertificateNumber, intake.PayerTaxId,
                intake.WhtAmount, matchLevel = intake.MatchLevel, gateProblems = intake.GateProblems,
                authenticity = intake.AuthenticityAnswer, authenticityConfidence = intake.AuthenticityConfidence,
                usedAi = intake.WhtCertAuthenticityUsedAi, autoConfirmed = intake.AutoConfirmed, detail,
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

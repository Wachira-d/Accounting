using System.Net;
using System.Text;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Accounting.Services.Payments.Providers;
using Accounting.Services.Settlement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 230 (erp-review/2026-11-02/PLATFORM_OMISE_AUDIT.md §5.2/§5.3 · §7.3) บน PostgreSQL จริง — บันทึกบัตร (ผู้ให้บริการปลอมผ่าน HttpMessageHandler) ·
/// หลักฐานความยินยอม round-trip · ตัดบัตรอัตโนมัติ (ครั้งเดียวต่อใบ · แพ็กเกจยกเลิกไม่ตัด) · ดึงรอบโอน → ร่าง (ไม่มีช่องทาง = ไม่ร่าง · ด่านตัวนำเข้าล้ม = ประทับเหตุ)
/// <para>collection เดียวกับเทสต์ที่ตั้ง <c>SiteSettings.PlatformCompanyId</c> + สวิตช์แพลตฟอร์มแถวเดียว (ไม่รันขนาน)</para>
/// </summary>
[Trait("Category", "Db")]
[Collection("SubscriptionExpirySweep")]
public class PlatformCardBillingRound230DbTests
{
    private readonly ITestOutputHelper _out;
    public PlatformCardBillingRound230DbTests(ITestOutputHelper output) => _out = output;

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<string> _replies;
        public List<HttpRequestMessage> Requests { get; } = new();
        public QueueHandler(params string[] replies) => _replies = new(replies);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var body = _replies.Count > 0 ? _replies.Dequeue() : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FakeFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public FakeFactory(HttpMessageHandler h) => _h = h;
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }

    private sealed class PlainSecrets : ISecretProtector
    {
        public string? Protect(string? plaintext) => plaintext;
        public string? Unprotect(string? value) => value;
        public bool IsUsable(string? value) => !string.IsNullOrEmpty(value);
    }

    private static OmisePaymentProvider Omise(HttpMessageHandler h) => new(new FakeFactory(h), new PlainSecrets(), NullLogger<OmisePaymentProvider>.Instance);

    private static ILegalDocumentService Legal() => F4Proxy<ILegalDocumentService>.Create((name, _) =>
        name == nameof(ILegalDocumentService.GetPurchasePolicyVersionAsync) ? new LegalPurchasePolicyVersion("1", "1", "1.0", true) : null);

    private static async Task<PaymentProviderConfig> ConfigAsync(Accounting.Data.AccountingDbContext db, Guid pid, PaymentProviderMode mode = PaymentProviderMode.Live)
    {
        var cfg = new PaymentProviderConfig
        {
            CompanyId = pid, ProviderCode = OmisePaymentProvider.Code, Mode = mode, IsActive = true,
            LiveSecretKeyProtected = "skey_live_bbbb", LivePublicKey = "pkey_live_bbbb", TestSecretKeyProtected = "skey_test_aaaa", TestPublicKey = "pkey_test_aaaa",
            PlatformLiveConfirmedAt = DateTime.UtcNow,
        };
        db.PaymentProviderConfigs.Add(cfg);
        await db.SaveChangesAsync();
        return cfg;
    }

    /// <summary>ช่องทาง NextAcc ปลอม — เปิดรับบัตรจริง · เริ่มชำระ = จดคำขอแล้วคืนรายการที่สำเร็จทันที</summary>
    private static IPlatformPaymentService Platform(Guid pid, PaymentProviderConfig cfg, List<object?[]>? starts = null) =>
        F4Proxy<IPlatformPaymentService>.Create((name, args) => name switch
        {
            nameof(IPlatformPaymentService.PlatformCompanyIdAsync) => (Guid?)pid,
            nameof(IPlatformPaymentService.PlatformConfigAsync) => cfg,
            nameof(IPlatformPaymentService.StatusAsync) => new PlatformPayStatus(true, true, "Live", OmisePaymentProvider.Code,
                new[] { nameof(PaymentMethodKind.Card), nameof(PaymentMethodKind.PromptPay) }, null),
            nameof(IPlatformPaymentService.StartSubscriptionPaymentAsync) => Started(starts, args),
            _ => null,
        });

    private static PaymentIntent Started(List<object?[]>? starts, object?[] args)
    {
        starts?.Add(args);
        return new PaymentIntent { Id = Guid.NewGuid(), Status = PaymentIntentStatus.Succeeded, Amount = 0m, ProviderCode = OmisePaymentProvider.Code };
    }

    private static async Task WithGlobalAsync(Accounting.Data.AccountingDbContext db, bool enabled, Func<Task> body)
    {
        var s = await db.PlatformAutoChargeSettings.FirstOrDefaultAsync(x => x.Id == PlatformAutoChargeSettings.SingletonId);
        var created = s == null;
        if (s == null) { s = new PlatformAutoChargeSettings { Id = PlatformAutoChargeSettings.SingletonId }; db.PlatformAutoChargeSettings.Add(s); }
        var before = s.AutoChargeEnabled;
        s.AutoChargeEnabled = enabled;
        await db.SaveChangesAsync();
        try { await body(); }
        finally
        {
            db.ChangeTracker.Clear();
            var again = await db.PlatformAutoChargeSettings.FirstAsync(x => x.Id == PlatformAutoChargeSettings.SingletonId);
            if (created) db.PlatformAutoChargeSettings.Remove(again); else again.AutoChargeEnabled = before;
            await db.SaveChangesAsync();
        }
    }

    private const string TokenJson = """{"object":"token","id":"tokn_1","card":{"id":"card_t230","fingerprint":"fp230","brand":"Visa","last_digits":"4242"}}""";
    private const string CustomerJson = """{"object":"customer","id":"cust_t230","livemode":true,"cards":{"data":[{"id":"card_t230","fingerprint":"fp230","brand":"Visa","last_digits":"4242","expiration_month":12,"expiration_year":2030,"livemode":true}]}}""";

    [Fact]
    public async Task SaveCard_ต้องยินยอม_หลักฐานตรวจกลับได้หลังผ่านฐาน_ปิดแล้วถอนความยินยอม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var f = await PlatformBillingPhase2DbTests.SeedAsync(db);
        var cfg = await ConfigAsync(db, f.PlatformId);
        await WithGlobalAsync(db, true, async () =>
        {
            var actor = new PlatformCardActor(Guid.NewGuid(), "Owner@Example.com", "10.0.0.1", "ua");
            var h = new QueueHandler(TokenJson, CustomerJson);
            var svc = new PlatformSavedCardService(db, Platform(f.PlatformId, cfg), new IPaymentProvider[] { Omise(h) }, Legal(),
                NullLogger<PlatformSavedCardService>.Instance);
            var version = await svc.CurrentConsentVersionAsync();

            // ผู้จ่ายเป็นนิติบุคคล (เลขภาษีนิติบุคคลที่ checksum ผ่าน) — ฝ่ายค้าน S1
            (await db.Companies.SingleAsync(c => c.Id == f.BuyerId)).TaxId = "0105556000009";
            await db.SaveChangesAsync();
            // ไม่ติ๊กยินยอม / ผู้จ่ายนิติบุคคล ไม่ติ๊กรับทราบภาษีหัก ณ ที่จ่าย ⇒ ปฏิเสธก่อนเรียกผู้ให้บริการ (ไม่มีแถว · ไม่มีคำขอออก)
            Assert.True(await svc.PayerIsJuristicAsync(f.BuyerId));
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SaveFromTokenAsync(f.BuyerId, "tokn_1", false, version, true, actor));
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SaveFromTokenAsync(f.BuyerId, "tokn_1", true, version, false, actor));
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SaveFromTokenAsync(f.BuyerId, "tokn_1", true, null, true, actor));   // S2
            Assert.Empty(h.Requests);
            Assert.False(await db.PlatformSavedCards.AnyAsync(c => c.PayerCompanyId == f.BuyerId));

            var refs = await svc.SaveFromTokenAsync(f.BuyerId, "tokn_1", true, version, true, actor);
            Assert.Equal("cust_t230", refs.CustomerRef);
            Assert.Equal("card_t230", refs.CardRef);
            db.ChangeTracker.Clear();
            var card = await db.PlatformSavedCards.SingleAsync(c => c.Id == refs.SavedCardId);
            Assert.Equal("4242", card.Last4);
            Assert.True(card.IsDefault);
            Assert.Null(card.VerifiedAt);
            Assert.NotNull(card.WhtAcknowledgedAt);
            // ฝ่ายค้าน S3: ยังไม่ชำระครั้งแรก ⇒ คำสั่งตัดยังไม่เปิด
            Assert.False(await db.PlatformAutoChargeMandates.AnyAsync(m => m.PayerCompanyId == f.BuyerId && m.PlatformCompanyId == f.PlatformId && m.Enabled));
            var consent = await db.PdpaConsentRecords.SingleAsync(c => c.Id == card.ConsentRecordId);
            Assert.Equal(f.PlatformId, consent.CompanyId);
            Assert.Equal(PlatformSavedCardRules.ConsentPurpose, consent.Purpose);
            Assert.Equal(version + PlatformSavedCardRules.WhtAckVersionSuffix, consent.PolicyVersion);
            Assert.Equal("ua", consent.UserAgent);
            Assert.Equal(PdpaConsentEvidence.EvidenceCheck.Valid, PdpaConsentEvidence.Check(consent.EvidenceHash, consent.SubjectContact, consent.Purpose,
                consent.PolicyVersion, consent.GrantedAt, consent.Channel, consent.IpAddress, consent.UserAgent));
            Assert.NotEqual(PdpaConsentEvidence.EvidenceCheck.Valid, PdpaConsentEvidence.Check(consent.EvidenceHash, consent.SubjectContact, consent.Purpose,
                consent.PolicyVersion, consent.GrantedAt, consent.Channel, consent.IpAddress, "other-ua"));   // UA อยู่ใน hash
            Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityType == "PlatformSavedCard" && a.EntityId == card.Id.ToString()));
            var summary = await svc.SummaryAsync(f.BuyerId);
            Assert.True(summary.PendingActivation);
            Assert.False(summary.MandateEnabled);

            // ── S3/B2 ทิศตรงข้าม: รายการสำเร็จที่ตัดด้วย "บัตรอื่น" ไม่ยืนยันบัตรนี้ ──
            PaymentIntent Paid(string? cardRef) => new()
            {
                CompanyId = f.PlatformId, PayerCompanyId = f.BuyerId, SourceKind = PaymentSourceKind.SubscriptionPayment, SourceId = Guid.NewGuid(),
                ProviderCode = OmisePaymentProvider.Code, Amount = 990m, Status = PaymentIntentStatus.Succeeded, IsTestMode = false,
                ConfirmedAt = DateTime.UtcNow, SavedCardRef = cardRef, IdempotencyKey = "t230v-" + Guid.NewGuid().ToString("N"),
            };
            var wrong = Paid("card_other");
            db.PaymentIntents.Add(wrong);
            (await db.PlatformSavedCards.SingleAsync(c => c.Id == card.Id)).FirstIntentId = wrong.Id;
            await db.SaveChangesAsync();
            await svc.RefreshVerificationAsync(f.BuyerId);
            db.ChangeTracker.Clear();
            Assert.Null((await db.PlatformSavedCards.SingleAsync(c => c.Id == card.Id)).VerifiedAt);
            Assert.False(await db.PlatformAutoChargeMandates.AnyAsync(m => m.PayerCompanyId == f.BuyerId && m.Enabled));

            // ── รายการที่ตัดด้วยบัตรนี้สำเร็จ ⇒ ยืนยัน + เปิดคำสั่งตัดด้วยความยินยอมตอนบันทึก (รวมการรับทราบภาษีหัก ณ ที่จ่าย) ──
            var right = Paid("card_t230");
            db.PaymentIntents.Add(right);
            (await db.PlatformSavedCards.SingleAsync(c => c.Id == card.Id)).FirstIntentId = right.Id;
            await db.SaveChangesAsync();
            await svc.RefreshVerificationAsync(f.BuyerId);
            db.ChangeTracker.Clear();
            Assert.NotNull((await db.PlatformSavedCards.SingleAsync(c => c.Id == card.Id)).VerifiedAt);
            var mandate = await db.PlatformAutoChargeMandates.SingleAsync(m => m.PayerCompanyId == f.BuyerId && m.PlatformCompanyId == f.PlatformId);
            Assert.True(mandate.Enabled);
            Assert.Equal(version, mandate.ConsentVersion);
            Assert.Equal(consent.Id, mandate.ConsentRecordId);
            Assert.NotNull(mandate.WhtAcknowledgedAt);
            Assert.Null((await svc.SummaryAsync(f.BuyerId)).AutoChargeBlockReason);

            // ── ปิด + ลบบัตรด้วย ⇒ ถอนความยินยอม · บัตรถูกลบ · เปิดใหม่ไม่ได้ (ไม่มีบัตรยืนยันแล้ว) ──
            await svc.SetAutoChargeAsync(f.BuyerId, false, false, null, false, removeCards: true, actor);
            db.ChangeTracker.Clear();
            Assert.False((await db.PlatformAutoChargeMandates.SingleAsync(m => m.Id == mandate.Id)).Enabled);
            Assert.NotNull((await db.PdpaConsentRecords.SingleAsync(c => c.Id == consent.Id)).WithdrawnAt);
            Assert.NotNull((await db.PlatformSavedCards.SingleAsync(c => c.Id == card.Id)).RemovedAt);
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SetAutoChargeAsync(f.BuyerId, true, true, version, true, false, actor));
            // S6: แถวยินยอมอยู่ในรายงาน DSR ของบริษัทนี้ (ไม่ส่ง IP/UA) และหน้า "ข้อมูลของฉัน" (ส่งครบ)
            var dsr = await PlatformSavedCardService.ConsentRowsForUserAsync(db, actor.UserId!.Value, f.BuyerId, includeNetwork: false);
            Assert.Single(dsr);
            Assert.False(dsr[0].ContainsKey("ipAddress"));
            Assert.Equal("10.0.0.1", (await PlatformSavedCardService.ConsentRowsForUserAsync(db, actor.UserId!.Value, null, includeNetwork: true))[0]["ipAddress"]);
        });
    }

    private static async Task<PlatformSavedCard> VerifiedCardAsync(Accounting.Data.AccountingDbContext db, Guid pid, Guid payer, string version)
    {
        var card = new PlatformSavedCard
        {
            PlatformCompanyId = pid, PayerCompanyId = payer, ProviderCode = OmisePaymentProvider.Code, CustomerRef = "cust_" + Guid.NewGuid().ToString("N")[..10],
            CardRef = "card_" + Guid.NewGuid().ToString("N")[..10], Brand = "Visa", Last4 = "4242", ValidUntil = PlatformSavedCardRules.ValidUntil(12, 2030),
            IsDefault = true, VerifiedAt = DateTime.UtcNow.AddDays(-30), WhtAcknowledgedAt = DateTime.UtcNow.AddDays(-30),
        };
        db.PlatformSavedCards.Add(card);
        db.PlatformAutoChargeMandates.Add(new PlatformAutoChargeMandate
        {
            PlatformCompanyId = pid, PayerCompanyId = payer, Enabled = true, ConsentVersion = version, EnabledAt = DateTime.UtcNow.AddDays(-30),
            WhtAcknowledgedAt = DateTime.UtcNow.AddDays(-30),   // (ฝ่ายค้าน S1 — ไม่มีผลกับผู้จ่ายบุคคล/เลขภาษีไม่ผ่าน)
        });
        await db.SaveChangesAsync();
        return card;
    }

    [Fact]
    public async Task AutoCharge_ใบถึงกำหนด_ตัดครั้งเดียว_รอบซ้ำไม่ตัดซ้อน_แพ็กเกจยกเลิกไม่ตัด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var f = await PlatformBillingPhase2DbTests.SeedAsync(db);
        var cancelled = await PlatformBillingPhase2DbTests.SeedAsync(db, f.PlatformId, status: SubscriptionStatus.Cancelled);
        var cfg = await ConfigAsync(db, f.PlatformId);
        var inv = await PlatformBillingPhase2DbTests.InvoiceAsync(db, f, "USG-230-" + Guid.NewGuid().ToString("N")[..6], 500m, DateTime.UtcNow);
        var invCancelled = await PlatformBillingPhase2DbTests.InvoiceAsync(db, cancelled, "USG-230-" + Guid.NewGuid().ToString("N")[..6], 300m, DateTime.UtcNow);
        var notFuture = await PlatformBillingPhase2DbTests.InvoiceAsync(db, f, "USG-230-" + Guid.NewGuid().ToString("N")[..6], 200m, DateTime.UtcNow.AddDays(5));
        await PlatformBillingPhase2DbTests.WithPlatformTenantAsync(db, f.PlatformId, async () =>
        await WithGlobalAsync(db, true, async () =>
        {
            var starts = new List<object?[]>();
            var notices = new List<string>();
            var platform = Platform(f.PlatformId, cfg, starts);
            var cards = new PlatformSavedCardService(db, platform, new IPaymentProvider[] { Omise(new QueueHandler()) }, Legal(),
                NullLogger<PlatformSavedCardService>.Instance);
            var version = await cards.CurrentConsentVersionAsync();
            var card = await VerifiedCardAsync(db, f.PlatformId, f.BuyerId, version);
            await VerifiedCardAsync(db, f.PlatformId, cancelled.BuyerId, version);
            var notifier = F4Proxy<IPlatformBillingNotifier>.Create((name, args) =>
            {
                if (name == nameof(IPlatformBillingNotifier.SendAsync)) { notices.Add((string)args[2]!); return 1; }
                return null;
            });
            IPlatformAutoChargeService Svc() => new PlatformAutoChargeService(db, platform, cards,
                new SubscriptionService(db, F4Proxy<INotificationService>.Create((_, _) => null)), notifier, NullLogger<PlatformAutoChargeService>.Instance);

            await Svc().RunOnceAsync();
            db.ChangeTracker.Clear();
            var attempt = await db.PlatformAutoChargeAttempts.SingleAsync(a => a.PayerCompanyId == f.BuyerId);
            Assert.Equal(inv.Id, attempt.DocumentId);
            Assert.Equal(1, attempt.AttemptNo);
            Assert.Equal(500m, attempt.Amount);
            Assert.Equal(PlatformAutoChargePolicy.OutcomeSucceeded, attempt.Outcome);
            var start = Assert.Single(starts);
            Assert.Equal(PaymentMethodKind.Card, start[2]);
            Assert.Equal(card.Id, Assert.IsType<PlatformCardRefs>(start[6]).SavedCardId);
            // รายการชำระผูกใบ — ยอดจากใบ (เซิร์ฟเวอร์)
            var pay = await db.SubscriptionPayments.SingleAsync(p => p.Id == attempt.SubscriptionPaymentId);
            Assert.Equal(inv.Id, pay.PlatformInvoiceDocumentId);
            Assert.Equal(500m, pay.Amount);
            Assert.Contains(notices, k => k == PlatformAutoChargePolicy.NoticeKey(inv.Id, 1, PlatformAutoChargePolicy.OutcomeSucceeded));
            // ทิศตรงข้าม: ใบยังไม่ถึงกำหนดไม่ถูกตัด · แพ็กเกจยกเลิกไม่ถูกตัด
            Assert.False(await db.PlatformAutoChargeAttempts.AnyAsync(a => a.DocumentId == notFuture.Id));
            Assert.False(await db.PlatformAutoChargeAttempts.AnyAsync(a => a.DocumentId == invCancelled.Id));

            // รอบซ้ำ (อีกเครื่อง/อีกชั่วโมง) ⇒ ไม่ตัดใบเดิมซ้ำ
            await Svc().RunOnceAsync();
            Assert.Single(starts);
            Assert.Equal(1, await db.PlatformAutoChargeAttempts.CountAsync(a => a.DocumentId == inv.Id));
        }));

        // สวิตช์แพลตฟอร์มปิด ⇒ ไม่ทำอะไรเลย
        await WithGlobalAsync(db, false, async () =>
        {
            var starts = new List<object?[]>();
            var platform = Platform(f.PlatformId, cfg, starts);
            var cards = new PlatformSavedCardService(db, platform, Array.Empty<IPaymentProvider>(), Legal(), NullLogger<PlatformSavedCardService>.Instance);
            var r = await new PlatformAutoChargeService(db, platform, cards, new SubscriptionService(db, F4Proxy<INotificationService>.Create((_, _) => null)),
                F4Proxy<IPlatformBillingNotifier>.Create((_, _) => 0), NullLogger<PlatformAutoChargeService>.Instance).RunOnceAsync();
            Assert.Equal(0, r.Attempted);
            Assert.Empty(starts);
        });
    }

    [Fact]
    public async Task Transfers_ดึงซ้ำไม่ซ้ำแถว_ไม่มีช่องทาง_ไม่ร่าง_มีช่องทาง_ส่งรายการที่จับคู่ให้ตัวนำเข้า_ไม่ลงบัญชี()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var f = await PlatformBillingPhase2DbTests.SeedAsync(db);
        var cfg = await ConfigAsync(db, f.PlatformId);
        var t0 = DateTime.UtcNow.AddDays(-3);
        PaymentIntent Paid(decimal amount, decimal fee, int hoursAfter) => new()
        {
            CompanyId = f.PlatformId, PayerCompanyId = f.BuyerId, SourceKind = PaymentSourceKind.SubscriptionPayment, SourceId = Guid.NewGuid(),
            ProviderCode = OmisePaymentProvider.Code, ProviderRef = "chrg_t230_" + Guid.NewGuid().ToString("N")[..8], Amount = amount, FeeActual = fee,
            Status = PaymentIntentStatus.Succeeded, IsTestMode = false, ConfirmedAt = t0.AddHours(hoursAfter), IdempotencyKey = "t230-" + Guid.NewGuid().ToString("N"),
        };
        var a = Paid(990m, 36.13m, 1); var b = Paid(500m, 18.25m, 2);
        db.PaymentIntents.AddRange(a, b);
        await db.SaveChangesAsync();
        var refSuffix = Guid.NewGuid().ToString("N")[..10];
        // ยอดที่หักจากยอดคงเหลือ = (990 − 36.13) + (500 − 18.25) = 1,435.62 · ไม่มีค่าธรรมเนียมโอน · ผู้ให้บริการไม่ระบุ charge
        var createdIso = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var paidIso = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var transfers = $$"""{"object":"list","data":[{"object":"transfer","id":"trsf_t230_{{refSuffix}}","amount":143562,"paid":true,"livemode":true,
            "created_at":"{{createdIso}}","paid_at":"{{paidIso}}"}]}""";
        var imports = new List<ProviderTransferDraftRequest>();
        var import = F4Proxy<ISettlementImportService>.Create((name, args) =>
        {
            if (name == nameof(ISettlementImportService.ImportProviderTransferDraftAsync))
            {
                imports.Add((ProviderTransferDraftRequest)args[2]!);
                throw new BusinessRuleException("ด่านทดสอบของตัวนำเข้า", "TEST");
            }
            if (name.StartsWith("Post", StringComparison.Ordinal)) throw new InvalidOperationException("ห้ามลงบัญชีจากเส้นนี้");
            return null;
        });
        PlatformTransferReconciliationService Svc(string json) => new(db, Platform(f.PlatformId, cfg),
            new IPaymentProvider[] { Omise(new QueueHandler(json)) }, import, NullLogger<PlatformTransferReconciliationService>.Instance);

        // ── ไม่มีช่องทางรอบโอน ⇒ เก็บรอบโอน + ประทับเหตุ ไม่เรียกตัวนำเข้า ──
        var r1 = await Svc(transfers).PullAsync(Guid.Empty);
        Assert.Equal(1, r1.New);
        Assert.Equal(0, r1.Drafted);
        Assert.Empty(imports);
        db.ChangeTracker.Clear();
        var row = await db.PlatformProviderTransfers.SingleAsync(x => x.TransferRef == "trsf_t230_" + refSuffix);
        Assert.Equal(1435.62m, row.NetToBank);
        Assert.Null(row.SettlementBatchId);
        Assert.Contains("ช่องทางรอบโอน", row.Problem);

        // ── สร้างช่องทาง Gateway ที่ผูก config แล้วดึงซ้ำ ⇒ แถวเดิม (ไม่ซ้ำ) · จับคู่ด้วยยอด · ส่งเฉพาะรายการที่จับคู่ · ตัวนำเข้าปฏิเสธ = ประทับเหตุ ไม่มีร่าง ──
        db.SettlementChannels.Add(new SettlementChannel
        {
            CompanyId = f.PlatformId, Kind = SettlementChannelKind.Gateway, DisplayName = "NextAcc gateway 230", PaymentProviderConfigId = cfg.Id, IsActive = true,
        });
        await db.SaveChangesAsync();
        var r2 = await Svc(transfers).PullAsync(Guid.Empty);
        Assert.Equal(0, r2.New);
        Assert.Equal(1, await db.PlatformProviderTransfers.CountAsync(x => x.TransferRef == "trsf_t230_" + refSuffix));
        var req = Assert.Single(imports);
        Assert.Equal("trsf_t230_" + refSuffix, req.PayoutRef);
        Assert.Equal(1435.62m, req.NetToBank);
        Assert.Equal(new[] { a.Id, b.Id }, req.IntentIds);
        db.ChangeTracker.Clear();
        row = await db.PlatformProviderTransfers.SingleAsync(x => x.Id == row.Id);
        Assert.Equal(PlatformTransferMatch.MethodAmountWindow, row.MatchMethod);
        Assert.Null(row.SettlementBatchId);
        Assert.StartsWith("ร่างไม่สำเร็จ: ด่านทดสอบของตัวนำเข้า", row.Problem);
        // รายการรับชำระไม่ถูกแตะ (ไม่มีเจ้าของรอบโอน · ไม่ลงบัญชี)
        Assert.False(await db.PaymentIntents.AnyAsync(i => (i.Id == a.Id || i.Id == b.Id) && (i.SettlementBatchId != null || i.SettlementJournalEntryId != null)));
    }

    // ═══ charge ที่สำเร็จทันทีตอนสร้าง (บัตรที่บันทึก/บัตรไม่ต้อง 3DS) — ต้องยืนยัน + ส่งต่อ handler ═══

    private sealed class SyncProvider : IPaymentProvider
    {
        private readonly PaymentIntentStatus _result;
        public SyncProvider(PaymentIntentStatus result) => _result = result;
        public string ProviderCode => "fake-sync-230";
        public PaymentCapabilities Capabilities { get; } = new(new HashSet<PaymentMethodKind> { PaymentMethodKind.Card }, false, false, false);
        public Task<ProviderCharge> CreateChargeAsync(PaymentIntent intent, ChargeRequest req, PaymentProviderConfig config, CancellationToken ct = default)
            => Task.FromResult(new ProviderCharge("chrg_sync_" + intent.Id.ToString("N")[..8], _result, _result.ToString(), intent.Amount, Fee: 3m));
        public Task<ProviderCharge> GetChargeAsync(PaymentIntent intent, PaymentProviderConfig config, CancellationToken ct = default)
            => Task.FromResult(new ProviderCharge(intent.ProviderRef ?? "", intent.Status, null, intent.Amount));
        public Task<ProviderRefund> RefundAsync(PaymentIntent intent, decimal amount, string reason, string attemptMarker, PaymentProviderConfig config,
            CancellationToken ct = default) => Task.FromResult(new ProviderRefund("", amount, false, "ไม่รองรับ"));
        public Task<VerifiedWebhookEvent?> VerifyWebhookAsync(string rawBody, IReadOnlyDictionary<string, string> headers, PaymentProviderConfig config,
            CancellationToken ct = default) => Task.FromResult<VerifiedWebhookEvent?>(null);
        public Task<ProviderHealth> TestConnectionAsync(PaymentProviderConfig config, CancellationToken ct = default)
            => Task.FromResult(new ProviderHealth(true, true, "ok"));
    }

    private sealed class CountingHandler : IPaymentCompletionHandler
    {
        public int Calls;
        public PaymentSourceKind SourceKind => PaymentSourceKind.SiteOrder;
        public Task HandleSucceededAsync(PaymentIntent intent, CancellationToken ct = default) { Calls++; return Task.CompletedTask; }
    }

    [Fact]
    public async Task SyncCharge_สำเร็จทันที_ยืนยันและส่งต่อhandler_รอผล_ไม่ส่งต่อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var shop = new Company { Name = "ร้านทดสอบ charge ทันที 230", TaxId = "0105556000003" };
        db.Companies.Add(shop);
        await db.SaveChangesAsync();
        foreach (var (result, expectCalls, expectStatus) in new[]
                 {
                     (PaymentIntentStatus.Succeeded, 1, PaymentIntentStatus.Succeeded),
                     (PaymentIntentStatus.Pending, 0, PaymentIntentStatus.Pending),   // ทิศตรงข้าม: 3DS/รอผล ⇒ เดิม (ไม่ยืนยัน)
                 })
        {
            var handler = new CountingHandler();
            var provider = new SyncProvider(result);
            var svc = new PaymentIntentService(db, new IPaymentProvider[] { provider }, new IPaymentCompletionHandler[] { handler },
                NullLogger<PaymentIntentService>.Instance);
            var intent = await svc.StartAsync(shop.Id, new StartPaymentRequest(PaymentSourceKind.SiteOrder, Guid.NewGuid(), 100m, PaymentMethodKind.Card,
                CardToken: "tokn_x"), providerCode: provider.ProviderCode);
            Assert.Equal(expectStatus, intent.Status);
            Assert.Equal(expectCalls, handler.Calls);
            Assert.Equal(result == PaymentIntentStatus.Succeeded, intent.ConfirmedAt != null);
        }
    }

    // ═══ ฝ่ายค้านรอบ 230 B1/B2 — ผลไม่แน่ชัดของการตัดบัตรที่บันทึก · ห้ามใช้รายการบัตรเดิมซ้ำกับโทเคนใหม่ ═══

    private sealed class ScriptProvider : IPaymentProvider
    {
        public Func<PaymentIntent, ChargeRequest, ProviderCharge> Create { get; set; } = (i, _) => new ProviderCharge("chrg_" + i.Id.ToString("N")[..8], PaymentIntentStatus.Pending, "pending", i.Amount);
        public Func<PaymentIntent, ProviderCharge?> Find { get; set; } = _ => null;
        public int Creates;
        public string ProviderCode => "fake-script-230";
        public PaymentCapabilities Capabilities { get; } = new(new HashSet<PaymentMethodKind> { PaymentMethodKind.Card }, false, false, false);
        public Task<ProviderCharge> CreateChargeAsync(PaymentIntent intent, ChargeRequest req, PaymentProviderConfig config, CancellationToken ct = default)
        { Creates++; return Task.FromResult(Create(intent, req)); }
        public Task<ProviderCharge?> FindChargeForIntentAsync(PaymentIntent intent, PaymentProviderConfig config, CancellationToken ct = default)
            => Task.FromResult(Find(intent));
        public Task<ProviderCharge> GetChargeAsync(PaymentIntent intent, PaymentProviderConfig config, CancellationToken ct = default)
            => Task.FromResult(new ProviderCharge(intent.ProviderRef ?? "", intent.Status, null, intent.Amount));
        public Task<ProviderRefund> RefundAsync(PaymentIntent intent, decimal amount, string reason, string attemptMarker, PaymentProviderConfig config,
            CancellationToken ct = default) => Task.FromResult(new ProviderRefund("", amount, false, "ไม่รองรับ"));
        public Task<VerifiedWebhookEvent?> VerifyWebhookAsync(string rawBody, IReadOnlyDictionary<string, string> headers, PaymentProviderConfig config,
            CancellationToken ct = default) => Task.FromResult<VerifiedWebhookEvent?>(null);
        public Task<ProviderHealth> TestConnectionAsync(PaymentProviderConfig config, CancellationToken ct = default)
            => Task.FromResult(new ProviderHealth(true, true, "ok"));
    }

    [Fact]
    public async Task B1_ตัดบัตรที่บันทึกแล้วเครือข่ายขาด_คงรอผล_หาchargeพบ_สำเร็จ_ทิศตรงข้าม_โทเคนล้ม_B2_โทเคนใหม่ไม่ใช้รายการเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var shop = new Company { Name = "ร้านทดสอบผลไม่แน่ชัด 230", TaxId = "0105556000003" };
        db.Companies.Add(shop);
        await db.SaveChangesAsync();
        var provider = new ScriptProvider { Create = (_, _) => throw new HttpRequestException("connection reset") };
        var handler = new CountingHandler();
        var svc = new PaymentIntentService(db, new IPaymentProvider[] { provider }, new IPaymentCompletionHandler[] { handler },
            NullLogger<PaymentIntentService>.Instance);

        // ── บัตรที่บันทึก + เครือข่ายขาด ⇒ ไม่ใช่ล้มเหลว (อาจตัดแล้ว) ──
        var saved = await svc.StartAsync(shop.Id, new StartPaymentRequest(PaymentSourceKind.SiteOrder, Guid.NewGuid(), 100m, PaymentMethodKind.Card,
            CustomerRef: "cust_x1", SavedCardRef: "card_x1"), providerCode: provider.ProviderCode);
        Assert.Equal(PaymentIntentStatus.Pending, saved.Status);
        Assert.Equal(SavedCardChargeOutcome.UnknownCode, saved.FailureCode);
        Assert.Equal("card_x1", saved.SavedCardRef);
        // ยังหาไม่พบ (ยังไม่ถึงกำหนดเลิก) ⇒ คงรอผล
        Assert.Equal(PaymentIntentStatus.Pending, (await svc.RefreshAsync(shop.Id, saved.Id)).Status);
        // พบ charge จาก metadata ⇒ สถานะของผู้ให้บริการ + ส่งต่อ handler ครั้งเดียว
        provider.Find = i => new ProviderCharge("chrg_found_x1", PaymentIntentStatus.Succeeded, "successful", i.Amount);
        var resolved = await svc.RefreshAsync(shop.Id, saved.Id);
        Assert.Equal(PaymentIntentStatus.Succeeded, resolved.Status);
        Assert.Equal("chrg_found_x1", resolved.ProviderRef);
        Assert.Equal(1, handler.Calls);

        // ── ทิศตรงข้าม: โทเคน (ผู้ใช้อยู่ตรงนั้น) + เครือข่ายขาด ⇒ ล้มเหลวตามเดิม (กดใหม่ได้) ──
        var tokenFail = await svc.StartAsync(shop.Id, new StartPaymentRequest(PaymentSourceKind.SiteOrder, Guid.NewGuid(), 100m, PaymentMethodKind.Card,
            CardToken: "tokn_a"), providerCode: provider.ProviderCode);
        Assert.Equal(PaymentIntentStatus.Failed, tokenFail.Status);

        // ── B2: รายการบัตรที่ยังเปิด (รอ 3DS) + โทเคนใหม่ ⇒ สร้างรายการใหม่ (โทเคนใช้ครั้งเดียว) · บัตรที่บันทึกใบเดียวกัน ⇒ ใช้รายการเดิม ──
        provider.Create = (i, _) => new ProviderCharge("chrg_" + i.Id.ToString("N")[..8], PaymentIntentStatus.Pending, "pending", i.Amount, AuthorizeUrl: "https://bank/3ds");
        var source = Guid.NewGuid();
        var first = await svc.StartAsync(shop.Id, new StartPaymentRequest(PaymentSourceKind.SiteOrder, source, 100m, PaymentMethodKind.Card, CardToken: "tokn_1"),
            providerCode: provider.ProviderCode);
        var second = await svc.StartAsync(shop.Id, new StartPaymentRequest(PaymentSourceKind.SiteOrder, source, 100m, PaymentMethodKind.Card, CardToken: "tokn_2"),
            providerCode: provider.ProviderCode);
        Assert.NotEqual(first.Id, second.Id);
        var src2 = Guid.NewGuid();
        var s1 = await svc.StartAsync(shop.Id, new StartPaymentRequest(PaymentSourceKind.SiteOrder, src2, 100m, PaymentMethodKind.Card,
            CustomerRef: "cust_y", SavedCardRef: "card_y"), providerCode: provider.ProviderCode);
        var creates = provider.Creates;
        var s2 = await svc.StartAsync(shop.Id, new StartPaymentRequest(PaymentSourceKind.SiteOrder, src2, 100m, PaymentMethodKind.Card,
            CustomerRef: "cust_y", SavedCardRef: "card_y"), providerCode: provider.ProviderCode);
        Assert.Equal(s1.Id, s2.Id);
        Assert.Equal(creates, provider.Creates);   // ไม่ตัดซ้ำ
        var s3 = await svc.StartAsync(shop.Id, new StartPaymentRequest(PaymentSourceKind.SiteOrder, src2, 100m, PaymentMethodKind.Card,
            CustomerRef: "cust_y", SavedCardRef: "card_z"), providerCode: provider.ProviderCode);
        Assert.NotEqual(s1.Id, s3.Id);   // บัตรคนละใบ ⇒ รายการใหม่
    }
}


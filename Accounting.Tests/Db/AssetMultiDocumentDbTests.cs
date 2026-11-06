using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.FixedAsset;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>รอบ 249 — สินทรัพย์ 1 ชิ้น ↔ เอกสารซื้อหลายใบ (erp-review/2026-11-06/ASSET_MULTI_DOCUMENT_DESIGN.md §4) ·
/// เส้นจริงของ FixedAssetService บน PostgreSQL: สร้างจากมัดจำ+งวด → ผูกใบสุดท้าย (รวมร่างอัตโนมัติ) → โอน (JE) → ใบลดหนี้ ·
/// ด่านกันซ้ำ · ยกเลิกใบต้นทาง · tenant · ตัวคิดค่าเสื่อมข้ามสินทรัพย์ระหว่างจัดหา</summary>
[Trait("Category", "Db")]
public class AssetMultiDocumentDbTests
{
    private readonly ITestOutputHelper _out;
    public AssetMultiDocumentDbTests(ITestOutputHelper output) => _out = output;

    private const string Actor = "11111111-1111-1111-1111-111111111111";

    private sealed class Seed
    {
        public Guid CompanyId;
        public Guid Cash, Deposit, Cip, Machine, Accum, DepExp, Expense, Ap, Computer, Office, Vehicle;
        public Guid DepositDoc, DepositLine, ProgressDoc, ProgressLine, FinalDoc, FinalLine, InstallLine, DraftAssetId, ExpenseDoc, ExpenseLine;
        public Guid ContactId;
    }

    private static async Task<Seed> SeedAsync(AccountingDbContext db)
    {
        var s = new Seed();
        var company = new Company { Name = "บริษัททดสอบสินทรัพย์หลายใบ " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000025" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        s.CompanyId = company.Id;
        Guid Acc(string code, string name, AccountType t)
        {
            var a = new ChartOfAccount { CompanyId = company.Id, AccountCode = code, AccountName = name, AccountType = t, Level = 4, IsActive = true };
            db.ChartOfAccounts.Add(a);
            return a.Id;
        }
        s.Cash = Acc("11110", "เงินสด", AccountType.Asset);
        s.Deposit = Acc("11810", "เงินมัดจำ", AccountType.Asset);
        s.Cip = Acc("12280", "งานระหว่างก่อสร้าง", AccountType.Asset);
        s.Machine = Acc("12260", "เครื่องจักร", AccountType.Asset);
        s.Accum = Acc("18360", "ค่าเสื่อมสะสม-เครื่องจักร", AccountType.Asset);
        s.DepExp = Acc("56160", "ค่าเสื่อม-เครื่องจักร", AccountType.Expense);
        s.Expense = Acc("51800", "วัสดุสิ้นเปลือง", AccountType.Expense);
        s.Ap = Acc("21210", "เจ้าหนี้การค้า", AccountType.Liability);
        s.Computer = Acc("12220", "คอมพิวเตอร์", AccountType.Asset);
        Acc("18320", "ค่าเสื่อมสะสม-คอมพิวเตอร์", AccountType.Asset);
        Acc("56120", "ค่าเสื่อม-คอมพิวเตอร์", AccountType.Expense);
        s.Office = Acc("12210", "อุปกรณ์สำนักงาน", AccountType.Asset);
        s.Vehicle = Acc("12240", "ยานพาหนะ", AccountType.Asset);
        var contact = new Contact { CompanyId = company.Id, Name = "บริษัทเครื่องจักรไทย", IsSupplier = true };
        db.Contacts.Add(contact);
        s.ContactId = contact.Id;

        (s.DepositDoc, s.DepositLine) = AddDoc(db, s, "PV-MD-0001", DocumentType.PaymentVoucher, Y(1, 10),
            ("มัดจำเครื่องจักร 30%", 300_000m, 21_000m, s.Deposit, true));
        PostJe(db, s.CompanyId, s.DepositDoc, s.Deposit, s.Cash, 300_000m);
        (s.ProgressDoc, s.ProgressLine) = AddDoc(db, s, "PI-MD-0002", DocumentType.PurchaseInvoice, Y(2, 10),
            ("งวดที่ 2 ผลิตเครื่องจักร", 400_000m, 28_000m, s.Cip, true));
        PostJe(db, s.CompanyId, s.ProgressDoc, s.Cip, s.Ap, 400_000m);
        var final = AddDocMulti(db, s, "PI-MD-0003", DocumentType.PurchaseInvoice, Y(3, 1),
            ("เครื่องจักร CNC งวดสุดท้าย", 300_000m, 21_000m, s.Machine, true),
            ("ค่าติดตั้ง", 20_000m, 1_400m, s.Machine, true));
        s.FinalDoc = final.DocId; s.FinalLine = final.LineIds[0]; s.InstallLine = final.LineIds[1];
        PostJe(db, s.CompanyId, s.FinalDoc, s.Machine, s.Ap, 320_000m);
        (s.ExpenseDoc, s.ExpenseLine) = AddDoc(db, s, "PV-MD-0004", DocumentType.PaymentVoucher, Y(3, 2),
            ("ค่าขนส่งเครื่องจักร (ลงค่าใช้จ่าย)", 5_000m, 350m, s.Expense, true));
        PostJe(db, s.CompanyId, s.ExpenseDoc, s.Expense, s.Cash, 5_000m);
        await db.SaveChangesAsync();

        // ร่างอัตโนมัติจากการอนุมัติใบสุดท้าย (ระบบเดิม: 1 บรรทัด = 1 สินทรัพย์ · ค่าติดตั้งเฉลี่ยเข้า)
        var draft = new FixedAsset
        {
            CompanyId = company.Id, AssetCode = "DRAFT-" + Guid.NewGuid().ToString("N")[..8], Name = "เครื่องจักร CNC งวดสุดท้าย",
            PurchaseDate = Y(3, 1), PurchaseCost = 320_000m, NetBookValue = 320_000m, UsefulLifeMonths = 120,
            Status = AssetStatus.Active, AssetAccountId = s.Machine, SourceDocumentId = s.FinalDoc, SourceDocumentLineId = s.FinalLine,
            NeedsReview = true,
        };
        db.FixedAssets.Add(draft);
        await db.SaveChangesAsync();
        s.DraftAssetId = draft.Id;
        return s;
    }

    private static (Guid DocId, Guid LineId) AddDoc(AccountingDbContext db, Seed s, string no, DocumentType t, DateTime date,
        (string Desc, decimal Amount, decimal Vat, Guid Account, bool Claimable) line)
    {
        var r = AddDocMulti(db, s, no, t, date, line);
        return (r.DocId, r.LineIds[0]);
    }

    private static (Guid DocId, List<Guid> LineIds) AddDocMulti(AccountingDbContext db, Seed s, string no, DocumentType t, DateTime date,
        params (string Desc, decimal Amount, decimal Vat, Guid Account, bool Claimable)[] lines)
        => AddDocFx(db, s, no, t, date, "THB", 1m, lines);

    private static (Guid DocId, List<Guid> LineIds) AddDocFx(AccountingDbContext db, Seed s, string no, DocumentType t, DateTime date,
        string currency, decimal rate, params (string Desc, decimal Amount, decimal Vat, Guid Account, bool Claimable)[] lines)
    {
        var doc = new Document
        {
            CompanyId = s.CompanyId, DocumentNumber = no + "-" + Guid.NewGuid().ToString("N")[..4], DocumentType = t,
            Status = DocumentStatus.Approved, DocumentDate = DateTime.SpecifyKind(date, DateTimeKind.Utc),
            ContactId = s.ContactId, Currency = currency, ExchangeRate = rate,
            SubTotal = lines.Sum(l => l.Amount), VatAmount = lines.Sum(l => l.Vat), TotalAmount = lines.Sum(l => l.Amount + l.Vat),
        };
        var order = 1;
        foreach (var l in lines)
            doc.Lines.Add(new DocumentLine
            {
                Description = l.Desc, Quantity = 1, Unit = "งาน", UnitPrice = l.Amount, Amount = l.Amount,
                VatRate = 7, VatAmount = l.Vat, IsVatClaimable = l.Claimable, AccountId = l.Account, LineOrder = order++,
            });
        db.Documents.Add(doc);
        return (doc.Id, doc.Lines.OrderBy(x => x.LineOrder).Select(x => x.Id).ToList());
    }

    /// <summary>วันที่ในปีก่อน (ทุกวันอยู่ในอดีตเสมอ — วันพร้อมใช้ต้อง ≤ วันนี้ และ ≥ วันของใบต้นทาง)</summary>
    private static DateTime Y(int month, int day) => new(DateTime.UtcNow.Year - 1, month, day);

    /// <summary>JE ลงวันเดียวกับเอกสาร (ด่านยอดคงเหลือผังต้นทาง ณ วันลงรายการโอนนับจากวันที่ JE)</summary>
    private static void PostJe(AccountingDbContext db, Guid companyId, Guid? docId, Guid dr, Guid cr, decimal amt)
    {
        var docDate = docId.HasValue ? db.Documents.Local.FirstOrDefault(d => d.Id == docId.Value)?.DocumentDate : null;
        var je = new JournalEntry
        {
            CompanyId = companyId, EntryNumber = "JV-SEED-" + Guid.NewGuid().ToString("N")[..8], EntryDate = docDate ?? DateTime.UtcNow,
            Status = JournalEntryStatus.Posted, SourceDocumentId = docId, TotalDebit = amt, TotalCredit = amt, Description = "seed",
        };
        je.Lines.Add(new JournalEntryLine { AccountId = dr, DebitAmount = amt, LineOrder = 1 });
        je.Lines.Add(new JournalEntryLine { AccountId = cr, CreditAmount = amt, LineOrder = 2 });
        db.JournalEntries.Add(je);
    }

    private static DateTime AvailableDate() => new(DateTime.UtcNow.Year - 1, 3, 15);

    private static async Task<(Seed S, Guid AssetId)> BuildPlacedAsync(AccountingDbContext db)
    {
        var s = await SeedAsync(db);
        var svc = new FixedAssetService(db);
        var created = await svc.CreateFromSourcesAsync(s.CompanyId, new CreateAssetFromSourcesRequest(
            "เครื่องจักร CNC", new List<AttachCostSourceItem> { new(s.DepositLine), new(s.ProgressLine) }, AssetAccountId: s.Machine), Actor);
        await svc.AttachCostSourcesAsync(s.CompanyId, created.Asset.Id, new AttachCostSourcesRequest(
            new List<AttachCostSourceItem> { new(s.FinalLine), new(s.InstallLine) }), Actor);
        await svc.PlaceInServiceAsync(s.CompanyId, created.Asset.Id,
            new PlaceInServiceRequest(AvailableDate(), s.Machine, 120), Actor);
        return (s, created.Asset.Id);
    }

    [Fact]
    public async Task Deposit_progress_final_install_become_one_asset_and_transfer_je_clears_deposit_and_cip()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var svc = new FixedAssetService(db);

        var created = await svc.CreateFromSourcesAsync(s.CompanyId, new CreateAssetFromSourcesRequest(
            "เครื่องจักร CNC", new List<AttachCostSourceItem> { new(s.DepositLine), new(s.ProgressLine) }, AssetAccountId: s.Machine), Actor);
        Assert.True(created.Asset.IsUnderAcquisition);
        Assert.Equal(700_000m, created.Asset.PurchaseCost);           // VAT ที่เคลมได้ไม่รวม
        Assert.Equal(DepreciationMethod.None, created.Asset.DepreciationMethod);

        var attached = await svc.AttachCostSourcesAsync(s.CompanyId, created.Asset.Id, new AttachCostSourcesRequest(
            new List<AttachCostSourceItem> { new(s.FinalLine), new(s.InstallLine) }), Actor);
        Assert.Equal(1_020_000m, attached.Asset.PurchaseCost);
        Assert.Single(attached.AbsorbedDraftAssetCodes);              // ร่างเดี่ยวของใบสุดท้ายถูกรวมเข้า ไม่ใช่สองแถว
        Assert.Null(attached.JournalEntryId);                        // ระหว่างจัดหา = ไม่มี JE

        var placed = await svc.PlaceInServiceAsync(s.CompanyId, created.Asset.Id,
            new PlaceInServiceRequest(AvailableDate(), s.Machine, 120), Actor);

        using var check = DbTestDatabase.TryCreateContext()!;
        var asset = await check.FixedAssets.SingleAsync(a => a.Id == created.Asset.Id);
        Assert.False(asset.IsUnderAcquisition);
        Assert.Equal(AvailableDate(), asset.PurchaseDate);           // ค่าเสื่อมเริ่มจากวันพร้อมใช้ ไม่ใช่วันจ่ายมัดจำ
        Assert.Equal(1_020_000m, asset.PurchaseCost);
        Assert.StartsWith("FA-", asset.AssetCode);
        Assert.Equal(s.DepExp, asset.DepreciationExpenseAccountId);   // ตามประเภทผัง 12260
        Assert.Equal(s.Accum, asset.AccumulatedDepreciationAccountId);
        Assert.False(await check.FixedAssets.AnyAsync(a => a.Id == s.DraftAssetId));

        var firstPlan = await check.AssetDepreciations.Where(d => d.FixedAssetId == asset.Id)
            .OrderBy(d => d.Year).ThenBy(d => d.Month).FirstAsync();
        Assert.Equal((AvailableDate().Year, 4), (firstPlan.Year, firstPlan.Month));
        Assert.Equal(8_500m, firstPlan.Amount);                       // 1,020,000 / 120

        var je = await check.JournalEntries.Include(j => j.Lines).SingleAsync(j => j.CompanyId == s.CompanyId && j.Reference == asset.AssetCode);
        Assert.Equal(700_000m, je.Lines.Single(l => l.AccountId == s.Machine).DebitAmount);
        Assert.Equal(300_000m, je.Lines.Single(l => l.AccountId == s.Deposit).CreditAmount);
        Assert.Equal(400_000m, je.Lines.Single(l => l.AccountId == s.Cip).CreditAmount);
        Assert.Equal(je.TotalDebit, je.TotalCredit);
        Assert.Equal(AvailableDate(), je.EntryDate);

        var rows = await check.FixedAssetCostSources.Where(x => x.FixedAssetId == asset.Id).ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.NotNull(r.CapitalizedAt));
        Assert.Equal(placed.PurchaseCost, rows.Sum(r => r.Amount));

        // ใบสุดท้ายยังลง 12260 = 320,000 และทะเบียนรองรับครบผ่านแถวผูก (ด่าน "ลบแล้ว GL ค้าง" นับแถวผูกด้วย)
        Assert.Equal(0m, await FixedAssetService.UnbackedAssetAmountForDocumentAsync(check, s.CompanyId, s.FinalDoc, Array.Empty<Guid>()));
    }

    [Fact]
    public async Task Same_line_cannot_be_capitalized_twice_but_can_be_split_between_assets()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var svc = new FixedAssetService(db);

        await svc.CreateFromSourcesAsync(s.CompanyId, new CreateAssetFromSourcesRequest(
            "เครื่อง A", new List<AttachCostSourceItem> { new(s.DepositLine, 120_000m) }), Actor);
        // ทิศตรงข้าม: ส่วนที่เหลือพอดีผ่าน
        await svc.CreateFromSourcesAsync(s.CompanyId, new CreateAssetFromSourcesRequest(
            "เครื่อง B", new List<AttachCostSourceItem> { new(s.DepositLine, 180_000m) }), Actor);
        // เกินแม้ 1 สตางค์ = ห้าม (ยอดเดียวกันเป็นต้นทุนสองครั้ง)
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.CreateFromSourcesAsync(s.CompanyId,
            new CreateAssetFromSourcesRequest("เครื่อง C", new List<AttachCostSourceItem> { new(s.DepositLine, 0.01m) }), Actor));
        Assert.Contains("สองครั้ง", ex.Message);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(300_000m, await check.FixedAssetCostSources.Where(x => x.DocumentLineId == s.DepositLine && x.ReleasedAt == null).SumAsync(x => x.Amount));
        Assert.False(await check.FixedAssets.AnyAsync(a => a.CompanyId == s.CompanyId && a.Name == "เครื่อง C"));   // ธุรกรรมถอยทั้งก้อน
    }

    [Fact]
    public async Task Expense_coded_line_is_refused_with_way_forward()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new FixedAssetService(db).CreateFromSourcesAsync(s.CompanyId,
            new CreateAssetFromSourcesRequest("เครื่องจักร", new List<AttachCostSourceItem> { new(s.ExpenseLine) }), Actor));
        Assert.Contains("แก้ผังบัญชี", ex.Message);
    }

    [Fact]
    public async Task Line_without_gl_on_its_account_cannot_be_linked()
    {
        // ใบสำคัญจ่ายมัดจำที่ไม่มีรายการบัญชีบนผัง 11810 (เช่นตัดชำระ/ถูกกลับรายการ) — ผูกแล้วโอนจะ Cr 11810 ติดลบ
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var (noGlDoc, noGlLine) = AddDoc(db, s, "PV-MD-NOGL", DocumentType.PaymentVoucher, Y(1, 20),
            ("มัดจำที่ไม่มีรายการบัญชี", 10_000m, 700m, s.Deposit, true));
        await db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new FixedAssetService(db).CreateFromSourcesAsync(s.CompanyId,
            new CreateAssetFromSourcesRequest("เครื่องจักร", new List<AttachCostSourceItem> { new(noGlLine) }), Actor));
        Assert.Contains("ลงบัญชีจริง", ex.Message);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await check.FixedAssetCostSources.AnyAsync(x => x.DocumentId == noGlDoc));
    }

    [Fact]
    public async Task Linking_aux_line_already_averaged_into_a_confirmed_single_asset_is_refused()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var draft = await db.FixedAssets.SingleAsync(a => a.Id == s.DraftAssetId);
        draft.NeedsReview = false;                                    // ผู้ใช้ยืนยันทะเบียนเดี่ยวแล้ว (ต้นทุนรวมค่าติดตั้ง 20,000)
        await db.SaveChangesAsync();
        var svc = new FixedAssetService(db);
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.CreateFromSourcesAsync(s.CompanyId,
            new CreateAssetFromSourcesRequest("ซ้ำ", new List<AttachCostSourceItem> { new(s.InstallLine) }), Actor));
        using var db2 = DbTestDatabase.TryCreateContext()!;
        await Assert.ThrowsAsync<BusinessRuleException>(() => new FixedAssetService(db2).CreateFromSourcesAsync(s.CompanyId,
            new CreateAssetFromSourcesRequest("ซ้ำ", new List<AttachCostSourceItem> { new(s.FinalLine) }), Actor));
    }

    [Fact]
    public async Task Absorbing_draft_without_its_install_line_is_refused_then_passes_with_it()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var svc = new FixedAssetService(db);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.CreateFromSourcesAsync(s.CompanyId,
            new CreateAssetFromSourcesRequest("เครื่องจักร", new List<AttachCostSourceItem> { new(s.FinalLine) }), Actor));
        Assert.Contains("20,000.00", ex.Message);                     // ค่าติดตั้งที่ร่างเฉลี่ยไว้จะค้าง 12260 โดยไม่มีทะเบียน
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.True(await check.FixedAssets.AnyAsync(a => a.Id == s.DraftAssetId));   // ถอยทั้งก้อน ร่างยังอยู่

        // context ใหม่ (หนึ่งคำขอ = หนึ่ง context — คำขอที่ล้มไม่ถูกใช้ต่อ)
        using var db2 = DbTestDatabase.TryCreateContext()!;
        var ok = await new FixedAssetService(db2).CreateFromSourcesAsync(s.CompanyId, new CreateAssetFromSourcesRequest("เครื่องจักร",
            new List<AttachCostSourceItem> { new(s.FinalLine), new(s.InstallLine) }), Actor);
        Assert.Equal(320_000m, ok.Asset.PurchaseCost);
    }

    [Fact]
    public async Task Credit_note_after_placement_reduces_cost_and_rebuilds_plan()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (s, assetId) = await BuildPlacedAsync(db);
        var (cnDoc, cnLine) = AddDoc(db, s, "CN-MD-0005", DocumentType.CreditNote, Y(6, 1),
            ("ส่วนลดเครื่องจักร", 20_400m, 1_428m, s.Machine, true));
        await db.SaveChangesAsync();
        var cn = await db.Documents.SingleAsync(d => d.Id == cnDoc);
        cn.RelatedDocumentId = s.FinalDoc;                            // ใบลดหนี้ของใบซื้อ ⇒ ฝั่งซื้อ
        PostJe(db, s.CompanyId, cnDoc, s.Ap, s.Machine, 20_400m);
        await db.SaveChangesAsync();

        var res = await new FixedAssetService(db).AttachCostSourcesAsync(s.CompanyId, assetId,
            new AttachCostSourcesRequest(new List<AttachCostSourceItem> { new(cnLine) }), Actor);
        Assert.Equal(999_600m, res.Asset.PurchaseCost);
        Assert.Null(res.JournalEntryId);                             // ใบลดหนี้ Cr 12260 อยู่แล้ว — ไม่ต้องย้ายผัง

        using var check = DbTestDatabase.TryCreateContext()!;
        var first = await check.AssetDepreciations.Where(d => d.FixedAssetId == assetId && !d.IsPosted)
            .OrderBy(d => d.Year).ThenBy(d => d.Month).FirstAsync();
        Assert.Equal(8_330m, first.Amount);                           // 999,600 / 120
        var row = await check.FixedAssetCostSources.SingleAsync(x => x.DocumentLineId == cnLine);
        Assert.Equal(-20_400m, row.Amount);
        Assert.Equal(AssetCostSourceKind.CreditNote, row.Kind);
    }

    [Fact]
    public async Task Voiding_a_capitalized_source_is_blocked_but_a_held_source_is_released()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (s, _) = await BuildPlacedAsync(db);
        var block = await FixedAssetService.CostSourceVoidBlockAsync(db, s.CompanyId, s.DepositDoc, "PV-MD-0001");
        Assert.NotNull(block);
        Assert.Contains("ใบลดหนี้", block);

        // ใบที่ผูกกับสินทรัพย์ที่ยังระหว่างจัดหา: ปลด + คิดต้นทุนใหม่ (ทิศตรงข้าม)
        var (otherDoc, otherLine) = AddDoc(db, s, "PV-MD-0006", DocumentType.PaymentVoucher, Y(4, 1),
            ("มัดจำเครื่องที่สอง", 50_000m, 3_500m, s.Deposit, true));
        PostJe(db, s.CompanyId, otherDoc, s.Deposit, s.Cash, 50_000m);
        await db.SaveChangesAsync();
        var svc = new FixedAssetService(db);
        var second = await svc.CreateFromSourcesAsync(s.CompanyId, new CreateAssetFromSourcesRequest(
            "เครื่องที่สอง", new List<AttachCostSourceItem> { new(otherLine) }), Actor);
        Assert.Null(await FixedAssetService.CostSourceVoidBlockAsync(db, s.CompanyId, otherDoc, "PV-MD-0006"));
        Assert.Equal(1, await FixedAssetService.ReleaseCostSourcesForVoidAsync(db, s.CompanyId, otherDoc, "PV-MD-0006"));
        await db.SaveChangesAsync();

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(0m, (await check.FixedAssets.SingleAsync(a => a.Id == second.Asset.Id)).PurchaseCost);
        var row = await check.FixedAssetCostSources.SingleAsync(x => x.DocumentLineId == otherLine);
        Assert.NotNull(row.ReleasedAt);                               // แถวยังอยู่เป็นประวัติ
    }

    [Fact]
    public async Task Approval_auto_register_skips_lines_already_linked()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var svc = new FixedAssetService(db);
        await svc.CreateFromSourcesAsync(s.CompanyId, new CreateAssetFromSourcesRequest(
            "เครื่องจักร", new List<AttachCostSourceItem> { new(s.ProgressLine) }), Actor);
        var r = await svc.RegisterFromDocumentAsync(s.CompanyId, s.ProgressDoc, null, Actor);
        Assert.Equal(0, r.Created);
        Assert.Contains(r.Notes, n => n.Contains("ผูกเป็นต้นทุน"));
    }

    [Fact]
    public async Task Under_acquisition_asset_is_not_depreciated_and_cannot_be_deleted_while_linked()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var svc = new FixedAssetService(db);
        var created = await svc.CreateFromSourcesAsync(s.CompanyId, new CreateAssetFromSourcesRequest(
            "เครื่องจักร", new List<AttachCostSourceItem> { new(s.DepositLine) }, AssetAccountId: s.Machine), Actor);
        var now = DateTime.UtcNow;
        var deps = await svc.CalculateDepreciationAsync(s.CompanyId, new CalculateDepreciationRequest(now.Year, now.Month), Actor);
        Assert.DoesNotContain(deps, d => d.FixedAssetId == created.Asset.Id);
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.DisposeAsync(s.CompanyId, created.Asset.Id,
            new DisposeAssetRequest(now, 0m), Actor));
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.DeleteAsync(s.CompanyId, created.Asset.Id));

        var src = (await svc.GetCostSourcesAsync(s.CompanyId, created.Asset.Id)).Sources.Single();
        await svc.ReleaseCostSourceAsync(s.CompanyId, created.Asset.Id, src.Id, new ReleaseCostSourceRequest("สร้างผิดตัว"), Actor);
        await svc.DeleteAsync(s.CompanyId, created.Asset.Id);         // ปลดแล้วลบได้ (ทิศตรงข้าม)
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await check.FixedAssets.AnyAsync(a => a.Id == created.Asset.Id));
    }

    // ═══ ฝ่ายค้าน 369bc403 — ทิศตรงข้าม + ด่านใหม่ ═══

    [Fact]
    public async Task Approval_still_registers_the_unlinked_line_of_the_same_document()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var two = AddDocMulti(db, s, "PI-MD-TWO", DocumentType.PurchaseInvoice, Y(2, 20),
            ("ตู้เอกสารชุดที่ 1 (งวด)", 40_000m, 2_800m, s.Office, true),
            ("ตู้เอกสารชุดที่ 2", 30_000m, 2_100m, s.Office, true));
        PostJe(db, s.CompanyId, two.DocId, s.Office, s.Ap, 70_000m);
        await db.SaveChangesAsync();
        var svc = new FixedAssetService(db);
        await svc.CreateFromSourcesAsync(s.CompanyId, new CreateAssetFromSourcesRequest(
            "ตู้ชุดที่ 1", new List<AttachCostSourceItem> { new(two.LineIds[0]) }), Actor);
        var r = await svc.RegisterFromDocumentAsync(s.CompanyId, two.DocId, null, Actor);
        Assert.Equal(1, r.Created);                                    // บรรทัดที่ไม่ได้ผูกยังขึ้นทะเบียนตามเดิม
        Assert.Equal(two.LineIds[1], r.Assets.Single().SourceDocumentLineId);
        Assert.Equal(30_000m, r.Assets.Single().PurchaseCost);
    }

    [Fact]
    public async Task Unlinked_document_void_gate_is_silent()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        Assert.Null(await FixedAssetService.CostSourceVoidBlockAsync(db, s.CompanyId, s.ExpenseDoc, "PV-MD-0004"));
        Assert.Equal(0, await FixedAssetService.ReleaseCostSourcesForVoidAsync(db, s.CompanyId, s.ExpenseDoc, "PV-MD-0004"));
        Assert.Null(await FixedAssetService.CostSourceDocumentChangeBlockAsync(db, s.CompanyId, s.ExpenseDoc, "PV-MD-0004", "resync "));
        Assert.Empty(await FixedAssetService.CapitalizedSourceDocumentIdsAsync(db, s.CompanyId, new[] { s.ExpenseDoc }));
    }

    [Fact]
    public async Task Foreign_currency_deposit_placed_into_computer_account()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var usd = AddDocFx(db, s, "PV-MD-USD", DocumentType.PaymentVoucher, Y(2, 1), "USD", 35m,
            ("มัดจำเซิร์ฟเวอร์ (USD)", 1_000m, 0m, s.Deposit, true));
        PostJe(db, s.CompanyId, usd.DocId, s.Deposit, s.Cash, 35_000m);
        await db.SaveChangesAsync();
        var svc = new FixedAssetService(db);
        var created = await svc.CreateFromSourcesAsync(s.CompanyId, new CreateAssetFromSourcesRequest(
            "เซิร์ฟเวอร์", new List<AttachCostSourceItem> { new(usd.LineIds[0]) }, AssetAccountId: s.Computer), Actor);
        Assert.Equal(35_000m, created.Asset.PurchaseCost);             // แปลงบาทตามอัตราของใบ
        await svc.PlaceInServiceAsync(s.CompanyId, created.Asset.Id, new PlaceInServiceRequest(Y(3, 1), s.Computer, 36), Actor);

        using var check = DbTestDatabase.TryCreateContext()!;
        var asset = await check.FixedAssets.SingleAsync(a => a.Id == created.Asset.Id);
        var je = await check.JournalEntries.Include(j => j.Lines).SingleAsync(j => j.CompanyId == s.CompanyId && j.Reference == asset.AssetCode);
        Assert.Equal(35_000m, je.Lines.Single(l => l.AccountId == s.Computer).DebitAmount);
        Assert.Equal(35_000m, je.Lines.Single(l => l.AccountId == s.Deposit).CreditAmount);
        Assert.NotNull(asset.AccumulatedDepreciationAccountId);       // 18320 ตามประเภท 12220
    }

    [Fact]
    public async Task Passenger_car_non_claimable_vat_is_part_of_cost()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var car = AddDocMulti(db, s, "PI-MD-CAR", DocumentType.PurchaseInvoice, Y(2, 15),
            ("รถยนต์นั่ง 5 ที่นั่ง (§82/5(6))", 900_000m, 63_000m, s.Vehicle, false));
        PostJe(db, s.CompanyId, car.DocId, s.Vehicle, s.Ap, 963_000m);   // AutoPost: Dr 12240 = ฐาน + VAT ต้องห้าม
        await db.SaveChangesAsync();
        var res = await new FixedAssetService(db).CreateFromSourcesAsync(s.CompanyId, new CreateAssetFromSourcesRequest(
            "รถผู้บริหาร", new List<AttachCostSourceItem> { new(car.LineIds[0]) }, AssetAccountId: s.Vehicle), Actor);
        Assert.Equal(963_000m, res.Asset.PurchaseCost);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(63_000m, (await check.FixedAssetCostSources.SingleAsync(x => x.DocumentLineId == car.LineIds[0])).NonClaimableVatIncluded);
    }

    [Fact]
    public async Task Credit_note_on_cip_after_placement_posts_transfer_je()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (s, assetId) = await BuildPlacedAsync(db);
        var (cnDoc, cnLine) = AddDoc(db, s, "CN-MD-CIP", DocumentType.CreditNote, Y(6, 10),
            ("ส่วนลดงวดที่ 2", 10_000m, 700m, s.Cip, true));
        await db.SaveChangesAsync();
        (await db.Documents.SingleAsync(d => d.Id == cnDoc)).RelatedDocumentId = s.ProgressDoc;
        PostJe(db, s.CompanyId, cnDoc, s.Ap, s.Cip, 10_000m);
        await db.SaveChangesAsync();

        var res = await new FixedAssetService(db).AttachCostSourcesAsync(s.CompanyId, assetId,
            new AttachCostSourcesRequest(new List<AttachCostSourceItem> { new(cnLine) }), Actor);
        Assert.NotNull(res.JournalEntryId);                            // ใบลดหนี้ Cr 12280 แต่ทุนอยู่ 12260 ⇒ Dr 12280 / Cr 12260
        Assert.Equal(1_010_000m, res.Asset.PurchaseCost);
        using var check = DbTestDatabase.TryCreateContext()!;
        var je = await check.JournalEntries.Include(j => j.Lines).SingleAsync(j => j.Id == res.JournalEntryId);
        Assert.Equal(10_000m, je.Lines.Single(l => l.AccountId == s.Cip).DebitAmount);
        Assert.Equal(10_000m, je.Lines.Single(l => l.AccountId == s.Machine).CreditAmount);
        Assert.Equal(Y(6, 10), je.EntryDate);
        Assert.NotNull(je.Lines);
    }

    [Fact]
    public async Task Deposit_already_cleared_elsewhere_cannot_be_transferred_again()
    {
        // ฝ่ายค้าน H3: ใบสุดท้ายเต็มราคาที่หักมัดจำเอง (Cr 11810) แล้วยังผูกมัดจำ ⇒ ต้นทุนซ้ำ + 11810 ติดลบ
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var (clrDoc, _) = AddDoc(db, s, "PI-MD-CLR", DocumentType.PurchaseInvoice, Y(2, 1),
            ("หักมัดจำ (ใบสุดท้ายเต็มราคา)", 1m, 0m, s.Machine, true));
        PostJe(db, s.CompanyId, clrDoc, s.Machine, s.Deposit, 300_000m);
        await db.SaveChangesAsync();
        var svc = new FixedAssetService(db);
        var created = await svc.CreateFromSourcesAsync(s.CompanyId, new CreateAssetFromSourcesRequest(
            "เครื่องจักร", new List<AttachCostSourceItem> { new(s.DepositLine) }, AssetAccountId: s.Machine), Actor);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.PlaceInServiceAsync(s.CompanyId, created.Asset.Id,
            new PlaceInServiceRequest(Y(3, 1), s.Machine, 120), Actor));
        Assert.Contains("คงเหลือ", ex.Message);
    }

    [Fact]
    public async Task Available_date_before_latest_source_document_is_refused()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var svc = new FixedAssetService(db);
        var created = await svc.CreateFromSourcesAsync(s.CompanyId, new CreateAssetFromSourcesRequest(
            "เครื่องจักร", new List<AttachCostSourceItem> { new(s.DepositLine), new(s.ProgressLine) }, AssetAccountId: s.Machine), Actor);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.PlaceInServiceAsync(s.CompanyId, created.Asset.Id,
            new PlaceInServiceRequest(Y(2, 1), s.Machine, 120), Actor));   // ใบงวดลง 10 ก.พ.
        Assert.Contains("ก่อนวันที่ของเอกสารต้นทุนล่าสุด", ex.Message);
    }

    [Fact]
    public async Task Other_company_line_is_not_found()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var a = await SeedAsync(db);
        var b = await SeedAsync(db);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new FixedAssetService(db).CreateFromSourcesAsync(b.CompanyId,
            new CreateAssetFromSourcesRequest("ข้ามบริษัท", new List<AttachCostSourceItem> { new(a.DepositLine) }), Actor));
        Assert.Empty(await new FixedAssetService(db).GetLinkableCostLinesAsync(b.CompanyId, "PV-MD-0001", a.DepositDoc));
    }
}

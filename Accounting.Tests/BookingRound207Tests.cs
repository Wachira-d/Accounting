using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 207 (คำขอเจ้าของ 9 ข้อ — การจองหน้าเว็บ · ผู้ให้บริการ · เอกสาร · หน้าทำงาน) — ตัวตัดสินแบบ pure ทั้งหมด ·
/// ทุกกลุ่มล็อกทั้ง "ของใหม่ทำงาน" และ "ค่าเริ่มต้น = ของเดิมทุกตัว"
/// </summary>
public class BookingRound207Tests
{
    private static readonly Guid A = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid Svc = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly DateOnly Mon = new(2026, 11, 2);

    // ═════════ BookingProviderAllocation (ข้อ 2) ═════════

    [Theory]
    [InlineData(null, false, false, false)]   // แถวเก่า ไม่มีผู้ให้บริการ ⇒ ปิด (เหมือนเดิม)
    [InlineData(null, true, false, true)]     // แถวเก่า มีผู้ให้บริการ ⇒ เปิด (เหมือนเดิม)
    [InlineData(null, false, true, true)]     // แถวเก่า มีบริการตั้งโหมดผู้ให้บริการ ⇒ เปิด
    [InlineData(false, true, true, false)]    // เจ้าของปิดเอง ⇒ ปิดแม้มีข้อมูล
    [InlineData(true, false, false, true)]    // เจ้าของเปิดเอง
    public void เปิดจัดสรรผู้ให้บริการ_แถวเก่าตัดสินจากข้อมูลเดิม(bool? setting, bool hasRes, bool anySvc, bool expected)
        => Assert.Equal(expected, BookingProviderAllocation.Enabled(setting, hasRes, anySvc));

    [Fact]
    public void ไซต์ปิด_โหมดที่มีผล_None_แต่ไม่ลบค่ารายบริการ()
    {
        Assert.Equal(BookingResourceMode.None, BookingProviderAllocation.EffectiveMode(BookingResourceMode.Auto, siteEnabled: false));
        Assert.Equal(BookingResourceMode.Auto, BookingProviderAllocation.EffectiveMode(BookingResourceMode.Auto, siteEnabled: true));
    }

    [Theory]
    [InlineData(true, 2, true)]
    [InlineData(true, 0, false)]   // ไม่มีใครรับบริการนี้ ⇒ เริ่มได้โดยไม่ระบุ (ไม่ขังผู้ใช้)
    [InlineData(false, 3, false)]
    public void ตอนเริ่มให้บริการ_ต้องเลือกผู้ให้บริการเมื่อไซต์เปิดและมีคนรับบริการ(bool enabled, int capable, bool expected)
        => Assert.Equal(expected, BookingProviderAllocation.RequiresProviderAtStart(enabled, capable));

    [Fact]
    public void ตรรกะแนะนำ_ตัวเลือกครบทุกค่า_enum()
        => Assert.Equal(Enum.GetNames<BookingRecommendStrategy>().OrderBy(x => x),
            BookingProviderAllocation.StrategyOptions().Select(o => o.Value).OrderBy(x => x));

    [Fact]
    public void ค่าเริ่มต้นของ_enum_ใหม่_เท่ากับพฤติกรรมเดิม()
    {
        Assert.Equal(BookingRecommendStrategy.LeastLoadedDay, default(BookingRecommendStrategy));
        // ฝ่ายค้านรอบ 207 ข้อ 2: ค่าเริ่มต้น = ไม่ออกเอกสารตอนเสร็จ (เดิม) — ไซต์เดิม (DEFAULT 0) ไม่เปลี่ยนพฤติกรรม
        Assert.Equal(BookingCompletionDocument.None, default(BookingCompletionDocument));
        Assert.Equal(BookingFieldRequirement.Optional, default(BookingFieldRequirement));
    }

    // ═════════ BookingSlotEngine.RankResources (ข้อ 8) ═════════

    private static BookingSlotRules Rules(BookingRecommendStrategy s) => new()
    {
        ServiceId = Svc, Mode = BookingSlotMode.Interval, DurationMinutes = 60, IntervalMinutes = 60, Capacity = 1,
        ResourceMode = BookingResourceMode.Auto, Strategy = s, MaxAdvanceDays = 365,
    };

    private static BookingOccupancy On(Guid res, string s, string e)
        => new(BookingSlotEngine.ParseTime(s)!.Value, BookingSlotEngine.ParseTime(e)!.Value, 1, 0, 0, Svc, res);

    private static readonly BookingSlotOption TenAm = BookingSlotEngine.SlotOfBooking(new TimeOnly(10, 0), new TimeOnly(11, 0));

    private static BookingResourceInfo R(Guid id, string name, int sort = 0, bool primary = false, int priority = 0, int month = 0, DateTime? last = null)
        => new(id, name, 1, sort, Array.Empty<BookingScheduleRow>(), primary, priority, month, last);

    [Fact]
    public void ค่าเริ่มต้น_งานน้อยสุดของวัน_ลำดับเดิมทุกตัว_PickResource_ไม่เปลี่ยน()
    {
        var res = new[] { R(A, "แอน", 1), R(B, "บี", 2), R(C, "ซี", 3) };
        var existing = new[] { On(A, "08:00", "09:00") };
        var rank = BookingSlotEngine.RankResources(Rules(BookingRecommendStrategy.LeastLoadedDay), TenAm, Mon, res, existing, 1);
        Assert.Equal(new[] { B, C, A }, rank.Select(x => x.Resource.Id));
        Assert.Equal(B, BookingSlotEngine.PickResource(Rules(BookingRecommendStrategy.LeastLoadedDay), TenAm, Mon, res, existing, 1));
    }

    [Fact]
    public void คนที่ไม่ว่าง_อยู่ท้ายเสมอ_ไม่ถูกจัดให้_ไม่ว่าตรรกะไหน()
    {
        var res = new[] { R(A, "แอน", primary: true), R(B, "บี") };
        var busy = new[] { On(A, "10:00", "11:00") };
        foreach (var s in Enum.GetValues<BookingRecommendStrategy>())
        {
            var rank = BookingSlotEngine.RankResources(Rules(s), TenAm, Mon, res, busy, 1);
            Assert.Equal(B, rank[0].Resource.Id);
            Assert.False(rank.Single(x => x.Resource.Id == A).Fits);
            Assert.Equal(B, BookingSlotEngine.PickResource(Rules(s), TenAm, Mon, res, busy, 1));
        }
    }

    [Fact]
    public void คนหลักก่อน_ว่างแล้วได้ก่อนแม้งานวันนี้มากกว่า()
    {
        var res = new[] { R(A, "แอน"), R(B, "บี", primary: true) };
        var existing = new[] { On(B, "08:00", "09:00") };
        Assert.Equal(A, BookingSlotEngine.PickResource(Rules(BookingRecommendStrategy.LeastLoadedDay), TenAm, Mon, res, existing, 1));
        Assert.Equal(B, BookingSlotEngine.PickResource(Rules(BookingRecommendStrategy.PrimaryFirst), TenAm, Mon, res, existing, 1));
    }

    [Fact]
    public void ตามลำดับความสำคัญ_เลขน้อยได้ก่อน()
    {
        var res = new[] { R(A, "แอน", priority: 5), R(B, "บี", priority: 1) };
        Assert.Equal(B, BookingSlotEngine.PickResource(Rules(BookingRecommendStrategy.PriorityOrder), TenAm, Mon, res, Array.Empty<BookingOccupancy>(), 1));
    }

    [Fact]
    public void กระจายเท่าเทียมทั้งเดือน_นับงานสะสมของเดือน()
    {
        var res = new[] { R(A, "แอน", month: 600), R(B, "บี", month: 120) };
        var today = new[] { On(B, "08:00", "09:00") };   // บี +60 วันนี้ = 180 < 600
        Assert.Equal(B, BookingSlotEngine.PickResource(Rules(BookingRecommendStrategy.FairShareMonth), TenAm, Mon, res, today, 1));
        // ทิศตรงข้าม: โหมดเดิมดูแค่วันนี้ ⇒ แอน (วันนี้ 0)
        Assert.Equal(A, BookingSlotEngine.PickResource(Rules(BookingRecommendStrategy.LeastLoadedDay), TenAm, Mon, res, today, 1));
    }

    [Fact]
    public void หมุนเวียน_คนที่ได้งานล่าสุดนานสุดได้ก่อน_ยังไม่เคยได้งานมาก่อนสุด()
    {
        var res = new[] { R(A, "แอน", last: new DateTime(2026, 11, 1, 3, 0, 0)), R(B, "บี", last: new DateTime(2026, 10, 30)), R(C, "ซี") };
        var rank = BookingSlotEngine.RankResources(Rules(BookingRecommendStrategy.RoundRobin), TenAm, Mon, res, Array.Empty<BookingOccupancy>(), 1);
        Assert.Equal(new[] { C, B, A }, rank.Select(x => x.Resource.Id));
    }

    [Fact]
    public void ไม่อยู่ในเวลางาน_ไม่ถูกจัด_และติดธง()
    {
        var hours = new[] { new BookingScheduleRow(DayOfWeek.Monday, null, new TimeOnly(13, 0), new TimeOnly(18, 0), false) };
        var res = new[] { new BookingResourceInfo(A, "แอน", 1, 0, hours), R(B, "บี") };
        var rank = BookingSlotEngine.RankResources(Rules(BookingRecommendStrategy.LeastLoadedDay), TenAm, Mon, res, Array.Empty<BookingOccupancy>(), 1);
        Assert.False(rank.Single(x => x.Resource.Id == A).OnDuty);
        Assert.Equal(B, rank[0].Resource.Id);
    }

    // ═════════ BookingProviderShare (ข้อ 3) ═════════

    [Fact]
    public void แบ่งเท่ากัน_ผลรวมร้อยพอดี_เศษลงคนแรก()
    {
        var parts = BookingProviderShare.Even(new[] { A, B, C });
        Assert.Equal(100m, parts.Sum(p => p.SharePercent));
        Assert.Equal(33.34m, parts[0].SharePercent);
        Assert.Equal(33.33m, parts[1].SharePercent);
    }

    [Fact]
    public void ไม่ส่งเปอร์เซ็นต์ทุกคน_ระบบแบ่งเท่ากัน()
    {
        var (parts, err) = BookingProviderShare.Normalize(new (Guid, decimal?)[] { (A, null), (B, null) });
        Assert.Null(err);
        Assert.Equal(new[] { 50m, 50m }, parts.Select(p => p.SharePercent));
    }

    [Theory]
    [InlineData(60, 30, "รวมต้องเท่ากับ 100")]
    [InlineData(100, 0, "มากกว่า 0")]
    [InlineData(60.555, 39.445, "ทศนิยม")]
    public void สัดส่วนผิด_ปฏิเสธพร้อมเหตุ(double a, double b, string fragment)
    {
        var (_, err) = BookingProviderShare.Normalize(new (Guid, decimal?)[] { (A, (decimal)a), (B, (decimal)b) });
        Assert.NotNull(err);
        Assert.Contains(fragment, err);
    }

    [Fact]
    public void ส่งเปอร์เซ็นต์บางคน_หรือคนซ้ำ_หรือไม่เลือกใคร_ปฏิเสธ()
    {
        Assert.NotNull(BookingProviderShare.Normalize(new (Guid, decimal?)[] { (A, 60m), (B, null) }).Error);
        Assert.NotNull(BookingProviderShare.Normalize(new (Guid, decimal?)[] { (A, 50m), (A, 50m) }).Error);
        Assert.NotNull(BookingProviderShare.Normalize(Array.Empty<(Guid, decimal?)>()).Error);
    }

    [Fact]
    public void คนหลัก_คือสัดส่วนมากสุด_เท่ากันใช้คนแรก()
    {
        Assert.Equal(B, BookingProviderShare.Primary(new[] { new BookingProviderSharePart(A, 40m), new BookingProviderSharePart(B, 60m) }));
        Assert.Equal(A, BookingProviderShare.Primary(new[] { new BookingProviderSharePart(A, 50m), new BookingProviderSharePart(B, 50m) }));
    }

    [Fact]
    public void แบ่งยอดตามสัดส่วน_เศษสตางค์ลงคนหลัก_ผลรวมเท่ายอดจริง()
    {
        var parts = new[] { new BookingProviderSharePart(A, 33.33m), new BookingProviderSharePart(B, 33.33m), new BookingProviderSharePart(C, 33.34m) };
        var split = BookingProviderShare.SplitAmount(1000m, parts);
        Assert.Equal(1000m, split.Values.Sum());
        Assert.Equal(333.40m, split[C]);
    }

    // ═════════ BookingFormFields (ข้อ 7) ═════════

    [Fact]
    public void ค่าเดิม_ชื่อและเบอร์บังคับ_อีเมลหมายเหตุไม่บังคับ()
    {
        var c = BookingFormFields.Parse(null);
        Assert.Equal(BookingFieldRequirement.Required, c.Phone);
        Assert.Equal(BookingFieldRequirement.Optional, c.Email);
        Assert.Empty(BookingFormFields.Check(c, "สมชาย", "0812345678", null, null).Errors);
        Assert.NotEmpty(BookingFormFields.Check(c, "สมชาย", null, null, null).Errors);
        Assert.NotEmpty(BookingFormFields.Check(c, " ", "0812345678", null, null).Errors);
    }

    [Fact]
    public void ตั้งอีเมลบังคับ_เบอร์ไม่บังคับ_ตรวจตามค่าตั้ง()
    {
        var c = new BookingFormFieldsConfig(BookingFieldRequirement.Optional, BookingFieldRequirement.Required, BookingFieldRequirement.Optional);
        Assert.Empty(BookingFormFields.Check(c, "สมชาย", null, "a@b.co", null).Errors);
        Assert.Contains(BookingFormFields.Check(c, "สมชาย", "0812345678", null, null).Errors, e => e.Contains("อีเมล"));
        Assert.Contains(BookingFormFields.Check(c, "สมชาย", null, "not-an-email", null).Errors, e => e.Contains("อีเมลไม่ถูกต้อง"));
    }

    [Fact]
    public void ช่องที่ซ่อน_ค่าที่ส่งมาถูกตัดทิ้ง()
    {
        var c = new BookingFormFieldsConfig(BookingFieldRequirement.Required, BookingFieldRequirement.Hidden, BookingFieldRequirement.Hidden);
        var (errs, v) = BookingFormFields.Check(c, "สมชาย", "0812345678", "x@y.z", "โน้ต");
        Assert.Empty(errs);
        Assert.Null(v.Email);
        Assert.Null(v.Notes);
    }

    [Fact]
    public void ซ่อนทั้งเบอร์และอีเมล_บันทึกค่าตั้งไม่ได้_และ_round_trip_JSON()
    {
        Assert.NotNull(BookingFormFields.ValidateConfig(new(BookingFieldRequirement.Hidden, BookingFieldRequirement.Hidden)));
        var c = new BookingFormFieldsConfig(BookingFieldRequirement.Optional, BookingFieldRequirement.Required, BookingFieldRequirement.Hidden);
        Assert.Null(BookingFormFields.ValidateConfig(c));
        Assert.Equal(c, BookingFormFields.Parse(BookingFormFields.Serialize(c)));
        Assert.Equal(BookingFormFields.Default, BookingFormFields.Parse("{not json"));
    }

    // ═════════ BookingInvoiceTimingPolicy — เอกสารตอนเสร็จ (ข้อ 5) ═════════

    private static BookingInvoiceDecision Done(BookingType type, BookingCompletionDocument completion, bool vat, bool paid,
        DocumentType? linked = DocumentType.Quotation, BookingInvoiceTiming timing = BookingInvoiceTiming.TaxInvoiceAtBooking)
        => BookingInvoiceTimingPolicy.Decide(new BookingInvoiceFacts(type, timing, BookingInvoiceEvent.ServiceCompleted,
            linked, linked == null ? null : DocumentStatus.Approved, false, vat, Completion: completion, PaidInFullNow: paid));

    [Fact]
    public void นัดหมาย_ค่าตั้งไม่ออก_เท่าพฤติกรรมเดิม_ไม่ออกเอกสาร()
        => Assert.Equal(BookingInvoiceAction.None, Done(BookingType.Appointment, BookingCompletionDocument.None, vat: true, paid: true).Action);

    [Fact]
    public void นัดหมาย_อัตโนมัติ_จดVAT_แปลงใบยืนยันเป็นใบกำกับภาษี_แม้รับเงินครบ()
    {
        var d = Done(BookingType.Appointment, BookingCompletionDocument.Auto, vat: true, paid: true);
        Assert.Equal(BookingInvoiceAction.IssueTaxInvoiceFromConfirmation, d.Action);
        Assert.Equal(DocumentType.TaxInvoice, d.DocumentType);
    }

    [Fact]
    public void นัดหมาย_อัตโนมัติ_ไม่จดVAT_รับครบ_ใบเสร็จรับเงิน_ยังไม่รับ_ใบแจ้งหนี้()
    {
        Assert.Equal(DocumentType.Receipt, Done(BookingType.Appointment, BookingCompletionDocument.Auto, vat: false, paid: true).DocumentType);
        Assert.Equal(DocumentType.Invoice, Done(BookingType.Appointment, BookingCompletionDocument.Auto, vat: false, paid: false).DocumentType);
    }

    [Fact]
    public void นัดหมาย_อัตโนมัติ_ไม่มีใบยืนยัน_สร้างเอกสารใหม่()
    {
        var d = Done(BookingType.Appointment, BookingCompletionDocument.Auto, vat: false, paid: true, linked: null);
        Assert.Equal(BookingInvoiceAction.CreateTaxInvoiceNow, d.Action);
        Assert.Equal(DocumentType.Receipt, d.DocumentType);
    }

    [Fact]
    public void การันตี_ใบกำกับตอนจองแล้ว_เสร็จ_ใช้ใบเดิม_ไม่ขึ้นกับค่าตั้งใหม่()
    {
        Assert.Equal(BookingInvoiceAction.AlreadyIssued, Done(BookingType.Guaranteed, BookingCompletionDocument.None, true, true, DocumentType.TaxInvoice).Action);
        Assert.Equal(BookingInvoiceAction.AlreadyIssued, Done(BookingType.Guaranteed, BookingCompletionDocument.Auto, true, true, DocumentType.TaxInvoice).Action);
    }

    [Fact]
    public void ตอนรับเงินของนัดหมาย_ยังปฏิเสธ_ปุ่มเฉพาะการันตี_เหมือนเดิม()
    {
        var d = BookingInvoiceTimingPolicy.Decide(new BookingInvoiceFacts(BookingType.Appointment, BookingInvoiceTiming.TaxInvoiceAtBooking,
            BookingInvoiceEvent.PaymentReceived, DocumentType.Quotation, DocumentStatus.Approved, false, true, Completion: BookingCompletionDocument.Auto));
        Assert.Equal(BookingInvoiceAction.Rejected, d.Action);
    }

    [Fact]
    public void บิลPOSปิดแล้ว_ไม่ออกเอกสารตอนเสร็จแม้ตั้งอัตโนมัติ()
    {
        var d = BookingInvoiceTimingPolicy.Decide(new BookingInvoiceFacts(BookingType.Appointment, BookingInvoiceTiming.TaxInvoiceAtBooking,
            BookingInvoiceEvent.ServiceCompleted, DocumentType.Quotation, DocumentStatus.Approved, false, true,
            PosBill: BookingPosBillState.Closed, Completion: BookingCompletionDocument.Auto, PaidInFullNow: true));
        Assert.Equal(BookingInvoiceAction.SettledByPosBill, d.Action);
    }

    [Fact]
    public void ตารางเอกสารแต่ละขั้น_ถามจากตัวตัดสินจริง()
    {
        var auto = BookingDocumentFlow.Describe(BookingInvoiceTiming.TaxInvoiceAtBooking, BookingCompletionDocument.Auto, vatRegistered: false);
        Assert.Equal(4, auto.Count);
        var appt = auto.Single(r => r.BookingType == nameof(BookingType.Appointment));
        Assert.Contains("ใบเสร็จรับเงิน", appt.OnComplete);
        Assert.Contains("ใบแจ้งหนี้", appt.OnComplete);
        var none = BookingDocumentFlow.Describe(BookingInvoiceTiming.TaxInvoiceAtBooking, BookingCompletionDocument.None, vatRegistered: true);
        var noneAppt = none.Single(r => r.BookingType == nameof(BookingType.Appointment)).OnComplete;
        Assert.Contains("ไม่รับชำระ ⇒ ไม่ออกเอกสาร", noneAppt);   // ค่าเดิม: ปุ่มเสร็จเฉย ๆ ไม่ออก
        Assert.Contains("รับชำระครบ ⇒ ใบกำกับภาษี", noneAppt);     // รับชำระตอนเสร็จ = ออกเอกสารรับเงินเสมอ
        Assert.Equal(Enum.GetNames<BookingCompletionDocument>().OrderBy(x => x), BookingDocumentFlow.CompletionOptions().Select(o => o.Value).OrderBy(x => x));
    }

    [Fact]
    public void ข้อความออกเอกสาร_ใบเสร็จขายสดไม่บอกว่ายังไม่รับชำระ_ใบกำกับคงข้อความเดิม()
    {
        var r = BookingInvoiceTimingPolicy.IssuedNotice(BookingInvoiceEvent.ServiceCompleted, "RC-1", DocumentType.Receipt, new DateTime(2026, 11, 2));
        Assert.Contains("ใบเสร็จรับเงิน", r);
        Assert.DoesNotContain("ยังไม่ได้บันทึกรับชำระ", r);
        var t = BookingInvoiceTimingPolicy.IssuedNotice(BookingInvoiceEvent.ServiceCompleted, "TIV-1", DocumentType.TaxInvoice, new DateTime(2026, 11, 2), datedOnPayment: true);
        Assert.Contains("ยังไม่ได้บันทึกรับชำระ", t);
        Assert.Contains("วันที่รับเงิน", t);
        Assert.Contains("วันที่บันทึกให้บริการเสร็จ", BookingInvoiceTimingPolicy.IssuedNotice(BookingInvoiceEvent.ServiceCompleted, "TIV-1", DocumentType.TaxInvoice, new DateTime(2026, 11, 2)));
    }

    // ═════════ BookingProviderReport (ข้อ 4) ═════════

    private static BookingProviderReportRow Row(BookingStatus st, decimal total, int minutes, params (Guid, string, decimal)[] providers)
        => new(Guid.NewGuid(), "BK-1", new DateTime(2026, 11, 2), "10:00", "11:00", minutes, st, Svc, "นวด", "ลูกค้า", total, providers);

    [Fact]
    public void รายงาน_ยอดตามสัดส่วน_ผลรวมทุกคนเท่ายอดเสร็จ_ยกเลิกไม่มีรายได้()
    {
        var rows = new[]
        {
            Row(BookingStatus.Completed, 1000m, 60, (A, "แอน", 60m), (B, "บี", 40m)),
            Row(BookingStatus.Completed, 500m, 30, (A, "แอน", 100m)),
            Row(BookingStatus.Cancelled, 800m, 60, (B, "บี", 100m)),
            Row(BookingStatus.Confirmed, 300m, 60),
        };
        var (list, totals) = BookingProviderReport.Build(rows);
        var a = list.Single(p => p.ResourceId == A);
        var b = list.Single(p => p.ResourceId == B);
        Assert.Equal(1100m, a.RevenueShare);
        Assert.Equal(400m, b.RevenueShare);
        Assert.Equal(1500m, totals.Revenue);
        Assert.Equal(1500m, list.Sum(p => p.RevenueShare));
        Assert.Equal(1, b.Cancelled);
        Assert.Equal(90, a.WorkedMinutes);
        Assert.Equal(66, a.ShareMinutes);   // 36 + 30
        // การจองที่ยังไม่มอบหมายไม่ถูกซ่อน — อยู่ท้ายสุด
        Assert.Null(list[^1].ResourceId);
        Assert.Equal(BookingProviderReport.UnassignedName, list[^1].Name);
        Assert.Equal(1, totals.Unassigned);
        Assert.Contains(a.Jobs, j => j.CoProviders != null && j.CoProviders.Contains("บี"));
    }

    // ═════════ BookingProductLink (ข้อ 1) ═════════

    [Theory]
    [InlineData(false, 7, null, 0)]      // ไม่จด VAT ⇒ 0 เสมอ
    [InlineData(true, 7, null, 7)]       // ไม่ผูกสินค้า ⇒ อัตราบริษัท (เดิม)
    [InlineData(true, 7, 7, 7)]
    [InlineData(true, 7, 0, 7)]          // ฝ่ายค้านข้อ 8: 0 บนสินค้ากำกวม (สร้างตอนยังไม่จด VAT) ⇒ อัตราบริษัท ไม่ใช่ 0%
    [InlineData(true, 7, -1, -1)]        // สินค้ายกเว้น VAT ⇒ ยกเว้น
    public void อัตราVATบรรทัดการจอง_ตามสินค้าที่ผูก(bool vat, double company, double? product, double expected)
        => Assert.Equal((decimal)expected, BookingProductLink.VatRate(vat, (decimal)company, product is null ? null : (decimal)product.Value));

    [Fact]
    public void คำเตือนผูกสินค้า_และรหัสถัดไป()
    {
        Assert.NotNull(BookingProductLink.Warning(false, false, 500m, null));
        Assert.Null(BookingProductLink.Warning(true, true, 500m, 500m));
        Assert.Contains("ไม่ตรง", BookingProductLink.Warning(true, true, 500m, 450m));
        Assert.Equal("SVC-0004", BookingProductLink.NextCode(new[] { "SVC-0003", "P-0009", "SVC-0001", "SVC-ABC" }));
        Assert.Equal("SVC-0001", BookingProductLink.NextCode(Array.Empty<string>()));
    }

    [Fact]
    public void ป้ายสถานะลูกค้า_ครบทุกสถานะ()
    {
        foreach (var s in Enum.GetValues<BookingStatus>())
            Assert.NotEqual(s.ToString(), BookingStatusLabels.Customer(s));
    }
}

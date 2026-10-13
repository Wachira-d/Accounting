using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.DTOs.TaxCalendar;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class TaxCalendarService : ITaxCalendarService
{
    private readonly AccountingDbContext _db;

    public TaxCalendarService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<List<TaxCalendarEventResponse>> GetEventsAsync(Guid companyId, int year, int? month = null)
    {
        var query = _db.Set<TaxCalendarEvent>()
            .Where(e => e.CompanyId == companyId && e.Year == year && !e.IsDeleted);

        if (month.HasValue)
            query = query.Where(e => e.Month == month.Value);

        var events = await query
            .OrderBy(e => e.DueDate)
            .ToListAsync();

        return events.Select(MapToResponse).ToList();
    }

    public async Task<TaxCalendarEventResponse> GetEventAsync(Guid companyId, Guid eventId)
    {
        var evt = await _db.Set<TaxCalendarEvent>()
            .FirstOrDefaultAsync(e => e.Id == eventId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการปฏิทินภาษี");

        return MapToResponse(evt);
    }

    public async Task InitializeYearAsync(Guid companyId, int year)
    {
        // ⚠️ **เดิม throw เมื่อมีแถวอยู่แล้ว** ⇒ ปฏิทินที่สร้างครั้งเดียวจะค้างเป็น
        // วันที่ของกติกาเก่าตลอดไป ไม่มี endpoint ไหนสร้างใหม่ได้เลย. หลังยุบ
        // ตารางกำหนดยื่นมาที่ Helpers/TaxFilingDeadline (แก้ ภ.พ.36 ที่เคยได้
        // วันที่ 23 · เพิ่ม ภ.ง.ด.54 ที่หายไปทั้งปี · เลื่อนวันหยุด ป.พ.พ. §193/8 ·
        // ภ.ง.ด.50 นับ 150 วันจากสิ้นรอบบัญชี) แถวเก่าจึงเล่าคนละเรื่องกับ
        // หน้านำส่งภาษีที่คำนวณสด — สองหน้าห่างกันหนึ่งคลิก
        // ⇒ เปลี่ยนเป็น **upsert**: แถวที่ยัง Pending อัปเดตวันให้ตรงกติกาปัจจุบัน ·
        // แบบที่ยังไม่มีให้เพิ่ม · แถวที่ผู้ใช้ทำเครื่องหมายว่ายื่นแล้ว **ห้ามแตะ**
        // (เป็นบันทึกของสิ่งที่เกิดขึ้นจริง ไม่ใช่ค่าที่คำนวณได้)
        var existingRows = await _db.Set<TaxCalendarEvent>()
            .Where(e => e.CompanyId == companyId && e.Year == year && !e.IsDeleted)
            .ToListAsync();
        var byKey = existingRows
            .GroupBy(e => (e.TaxFormCode, e.Month))
            .ToDictionary(g => g.Key, g => g.First());

        // กระดาน 2026-10-13 (A12): ภ.ง.ด.2/2ก · ภ.ง.ด.54 · ภ.พ.36 สร้างเฉพาะบริษัทที่มีกิจกรรมจริง (เดิมสร้างให้ทุกบริษัท ⇒ เลยกำหนด/เตือนปลอม)
        // · ตัวตัดสินเดียว Helpers/TaxCalendarSeedScope · แบบอื่นสร้างเสมอเหมือนเดิม
        var activity = await LoadSeedActivityAsync(companyId, year);

        // ⚠️ ภ.ง.ด.50/51 ผูกกับ **รอบบัญชีของบริษัท** ไม่ใช่ปีปฏิทิน —
        // `TaxService.GenerateCitReport` อ่าน `FiscalYearStartMonth` อยู่แล้ว
        // แต่ปฏิทินนี้ hardcode 31 พ.ค. ⇒ บริษัทที่รอบไม่ตรงปีปฏิทินได้วันผิดทั้งปี
        var fiscalStart = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.FiscalYearStartMonth)
            .FirstOrDefaultAsync();
        if (fiscalStart is < 1 or > 12) fiscalStart = 1;
        // สิ้นรอบบัญชีของปีภาษีนี้ · ครึ่งรอบ (ใช้กับ ภ.ง.ด.51)
        var fyStart = new DateTime(year, fiscalStart, 1);
        var fyEnd = fyStart.AddYears(1).AddDays(-1);
        var halfEnd = fyStart.AddMonths(6).AddDays(-1);

        // ⚠️ วันครบกำหนดมาจาก Helpers/TaxFilingDeadline ตัวเดียว — ห้ามพิมพ์เลขวันซ้ำ
        // ที่นี่ (เดิมเป็นตารางชุดที่สองของเรพ) · เพิ่ม **ภ.ง.ด.54** ที่เดิมไม่มีในลิสต์
        // เลย ⇒ บริษัทที่จ่ายเงินได้ให้ผู้รับต่างประเทศ (ม.70) ได้เตือน ภ.พ.36 แต่
        // ไม่ได้เตือน ภ.ง.ด.54 ทั้งที่มาจากการจ่ายครั้งเดียวกัน
        var monthlyForms = new[]
        {
            ("ภ.พ.30", "แบบแสดงรายการภาษีมูลค่าเพิ่ม", "VatPp30"),
            (TaxCalendarSeedScope.Pp36, "แบบนำส่ง VAT จากการจ่ายค่าบริการต่างประเทศ", "VatPp36"),
            ("ภ.ง.ด.1", "แบบยื่นภาษีเงินได้หัก ณ ที่จ่าย (เงินเดือน)", "WhtPnd1"),
            // รอบ 204 (คำตัดสินข้อ 148): ดอกเบี้ย/เงินปันผล ม.40(4) จ่ายบุคคลธรรมดา (ม.50(2) · ม.59)
            (TaxCalendarSeedScope.Pnd2, "แบบยื่นภาษีเงินได้หัก ณ ที่จ่าย (ดอกเบี้ย/เงินปันผล บุคคลธรรมดา)", "WhtPnd2"),
            ("ภ.ง.ด.3", "แบบยื่นภาษีเงินได้หัก ณ ที่จ่าย (บุคคลธรรมดา)", "WhtPnd3"),
            ("ภ.ง.ด.53", "แบบยื่นภาษีเงินได้หัก ณ ที่จ่าย (นิติบุคคล)", "WhtPnd53"),
            (TaxCalendarSeedScope.Pnd54, "แบบนำส่งภาษีเงินได้หัก ณ ที่จ่าย (จ่ายต่างประเทศ ม.70)", "WhtPnd54"),
            ("สปส.1-10", "แบบรายการแสดงการส่งเงินสมทบประกันสังคม", "SsoSps110"),
        };

        var events = new List<TaxCalendarEvent>();
        // รอบ 201 ทีม PL (B-9): วันหยุดราชการของแพลตฟอร์ม (ตาราง PlatformHolidays · ว่าง = เลื่อนเฉพาะเสาร์/อาทิตย์ตามเดิม)
        var holidays = await PlatformHolidayStore.LoadSetAsync(_db, year, year + 2);   // ภ.ง.ด.50 รอบบัญชีไม่ตรงปีปฏิทินตกปี +2 ได้

        // Create monthly events for all 12 months
        foreach (var (code, name, remitKey) in monthlyForms)
        {
            if (!TaxCalendarSeedScope.ShouldSeed(code, activity)) continue;   // A12 — แบบผูกกิจกรรมที่บริษัทไม่มี
            for (var month = 1; month <= 12; month++)
            {
                // ⚠️ e-Filing ต้องนับจากวันครบกำหนด **ก่อนเลื่อนวันหยุด** — เดิมที่นี่
                // เลื่อนกระดาษก่อนแล้วค่อย +8 ⇒ งวดที่วันที่ 7 ตรงเสาร์ ได้ e-Filing
                // วันที่ 17 ซึ่ง **ช้ากว่าที่กฎหมายให้ 2 วัน** (ของจริงคือ 15 แล้วเลื่อน)
                var (dueDate, eFiling) =
                    Accounting.Helpers.TaxFilingDeadline.For(remitKey, year, month, holidays);
                DateTime? eFilingDueDate = eFiling == dueDate ? null : eFiling;

                events.Add(new TaxCalendarEvent
                {
                    CompanyId = companyId,
                    TaxFormCode = code,
                    TaxFormName = name,
                    Year = year,
                    Month = month,
                    DueDate = dueDate,
                    EFilingDueDate = eFilingDueDate,
                    Status = "Pending",
                    IsRecurring = true,
                    ReminderDaysBefore = 7
                });
            }
        }

        // Annual forms
        var annualForms = new[]
        {
            // §69 — ภายใน **150 วัน**นับแต่วันสุดท้ายของรอบบัญชี (ไม่ใช่ 31 พ.ค. ตายตัว:
            // รอบปฏิทินได้ 30 พ.ค. · ปีอธิกสุรทิน 29 พ.ค. · รอบที่ไม่ตรงปีปฏิทินคนละวันเลย)
            ("ภ.ง.ด.50", "แบบแสดงรายการภาษีเงินได้นิติบุคคล (ประจำปี)", fyEnd.AddDays(150), 8),
            // §67 ทวิ — ภายใน 2 เดือนนับแต่วันสุดท้ายของรอบ 6 เดือนแรก
            ("ภ.ง.ด.51", "แบบแสดงรายการภาษีเงินได้นิติบุคคลครึ่งปี", halfEnd.AddMonths(2), 8),
            ("ภ.ง.ด.1ก", "แบบสรุปภาษีเงินได้หัก ณ ที่จ่าย (ประจำปี)", new DateTime(year + 1, 2, 28), 8),
            ("สบช.3", "แบบนำส่งงบการเงิน (กรมพัฒนาธุรกิจการค้า)", new DateTime(year + 1, 5, 31), 0),
            (TaxCalendarSeedScope.Pnd2a, "แบบสรุป WHT เงินปันผล/ดอกเบี้ย ประจำปี", new DateTime(year + 1, 1, 31), 8),
            ("ภ.ง.ด.3ก", "แบบสรุป WHT บุคคลธรรมดา ประจำปี", new DateTime(year + 1, 1, 31), 8),
            ("ภ.ง.ด.53ก", "แบบสรุป WHT นิติบุคคล ประจำปี", new DateTime(year + 1, 1, 31), 8),
        };

        foreach (var (code, name, dueDate, eFilingExtra) in annualForms)
        {
            if (!TaxCalendarSeedScope.ShouldSeed(code, activity)) continue;   // A12 — ภ.ง.ด.2ก ตามกิจกรรม ภ.ง.ด.2
            // ⚠️ e-Filing ต้องนับ +8 จากวันครบกำหนด **ก่อนเลื่อนวันหยุด** แล้วค่อย
            // เลื่อนทั้งคู่ (มาตรการกระทรวงการคลังขยายจากวันตามกฎหมาย ไม่ใช่จากวัน
            // ที่เลื่อนแล้ว) — เดิมบล็อกนี้เลื่อนก่อนแล้วบวก ⇒ ได้ช้ากว่าจริง 1-2 วัน
            // และ **ไม่เลื่อน e-Filing เลย** ⇒ ภ.ง.ด.50/1ก/2ก/3ก/53ก ตกวันเสาร์ได้
            // ⇒ ปฏิทินขึ้น "เลยกำหนด" ก่อนเวลาจริง. ตัวเลข 8 อยู่ที่
            // Helpers/TaxFilingDeadline ที่เดียว — ห้ามพิมพ์ซ้ำ (บล็อกนี้เคยพิมพ์ 6 จุด)
            var adjustedDueDate = Accounting.Helpers.TaxFilingDeadline.RollToBusinessDay(dueDate, holidays);

            DateTime? eFilingDueDate = null;
            if (eFilingExtra > 0)
                eFilingDueDate = Accounting.Helpers.TaxFilingDeadline.RollToBusinessDay(
                    dueDate.AddDays(Accounting.Helpers.TaxFilingDeadline.EFilingExtraDays), holidays);

            events.Add(new TaxCalendarEvent
            {
                CompanyId = companyId,
                TaxFormCode = code,
                TaxFormName = name,
                Year = year,
                Month = 0, // annual
                DueDate = adjustedDueDate,
                EFilingDueDate = eFilingDueDate,
                Status = "Pending",
                IsRecurring = true,
                ReminderDaysBefore = 14 // longer reminder for annual forms
            });
        }

        var added = 0; var refreshed = 0;
        foreach (var e in events)
        {
            if (!byKey.TryGetValue((e.TaxFormCode, e.Month), out var row))
            {
                _db.Set<TaxCalendarEvent>().Add(e);
                added++;
                continue;
            }
            // ยื่นแล้ว/ทำเครื่องหมายเองแล้ว = ข้อเท็จจริง ห้ามเขียนทับด้วยค่าที่คำนวณ
            if (!string.Equals(row.Status, "Pending", StringComparison.OrdinalIgnoreCase)
                || row.FiledDate != null) continue;
            if (row.DueDate == e.DueDate && row.EFilingDueDate == e.EFilingDueDate
                && row.TaxFormName == e.TaxFormName) continue;
            row.DueDate = e.DueDate;
            row.EFilingDueDate = e.EFilingDueDate;
            row.TaxFormName = e.TaxFormName;
            row.UpdatedAt = DateTime.UtcNow;
            refreshed++;
        }

        // A12: สร้างใหม่ ⇒ ถอนแถวของแบบที่ไม่มีกิจกรรม (soft-delete) เฉพาะแถวที่ระบบสร้างแล้วไม่มีใครแตะ — แถวที่ผู้ใช้ทำเครื่องหมาย/กรอกไว้ห้ามแตะ
        // (ข้อมูลเก่าที่สร้างให้ทุกบริษัทไปแล้ว = ของเสียที่ persist ⇒ ต้องซ่อมด้วย ไม่ใช่แค่หยุดสร้างใหม่ · F2 ข้อ 9)
        var retracted = 0;
        foreach (var row in existingRows)
        {
            if (!TaxCalendarSeedScope.ShouldRetract(row, activity)) continue;
            row.IsDeleted = true;
            row.UpdatedAt = DateTime.UtcNow;
            retracted++;
        }
        if (added > 0 || refreshed > 0 || retracted > 0) await _db.SaveChangesAsync();
    }

    /// <summary>
    /// กิจกรรมที่ตัดสินว่าบริษัทต้องมีแถว ภ.ง.ด.2/2ก · ภ.ง.ด.54 · ภ.พ.36 บนปฏิทินไหม (A12) — ช่วงปีจาก <see cref="TaxCalendarSeedScope.ActivityYears"/> ·
    /// ทุก query กรอง <c>CompanyId == companyId</c> + ไม่นับใบ/เอกสารที่ถูกยกเลิกหรือลบ
    /// </summary>
    private async Task<TaxCalendarSeedScope.SeedActivity> LoadSeedActivityAsync(Guid companyId, int year)
    {
        var (fromYear, toYear) = TaxCalendarSeedScope.ActivityYears(year);
        var fromDate = new DateTime(fromYear, 1, 1);
        var toDateExclusive = new DateTime(toYear + 1, 1, 1);

        // 50 ทวิ แบบ ภ.ง.ด.2 / ภ.ง.ด.54 ที่ยังมีผล (ใบร่างนับ — ผู้ใช้กำลังจะจ่าย)
        var certForms = await _db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == companyId && !w.IsDeleted
                && w.Status != WithholdingTaxCertStatus.Voided
                && w.TaxYear >= fromYear && w.TaxYear <= toYear
                && (w.TaxFormType == TaxType.WithholdingTax2 || w.TaxFormType == TaxType.WithholdingTax54))
            .Select(w => w.TaxFormType)
            .Distinct()
            .ToListAsync();

        // เอกสารจ่ายค่าบริการต่างประเทศ (ภ.พ.36 · ม.83/6) — ตัวเดียวกับที่ลง 21912
        var hasForeignService = await _db.Documents.AsNoTracking()
            .AnyAsync(d => d.CompanyId == companyId && !d.IsDeleted
                && d.Status != DocumentStatus.Voided
                && d.IsForeignService
                && d.DocumentDate >= fromDate && d.DocumentDate < toDateExclusive);

        // ผู้รับต่างประเทศที่หักภาษี (ม.70) โดยไม่ได้ติ๊กค่าบริการต่างประเทศ — ทางเดียวกับ WhtPayeeKind.IsForeignPayee (ประเทศของคู่ค้า ≠ TH)
        // · ภ.ง.ด.54 ที่ขาดเตือนแพงกว่าเตือนเกิน ⇒ นับกว้าง
        var hasForeignPayeeWht = hasForeignService || await _db.Documents.AsNoTracking()
            .AnyAsync(d => d.CompanyId == companyId && !d.IsDeleted
                && d.Status != DocumentStatus.Voided
                && d.WithholdingTaxAmount > 0
                && d.Contact.CountryCode != null && d.Contact.CountryCode != ""
                && d.Contact.CountryCode.ToUpper() != "TH"
                && d.DocumentDate >= fromDate && d.DocumentDate < toDateExclusive);

        return new TaxCalendarSeedScope.SeedActivity(
            Pnd2: certForms.Contains(TaxType.WithholdingTax2),
            Pnd54: certForms.Contains(TaxType.WithholdingTax54) || hasForeignPayeeWht,
            Pp36: hasForeignService);
    }

    public async Task<TaxCalendarEventResponse> UpdateEventAsync(Guid companyId, Guid eventId, UpdateTaxCalendarEventRequest request)
    {
        var evt = await _db.Set<TaxCalendarEvent>()
            .FirstOrDefaultAsync(e => e.Id == eventId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการปฏิทินภาษี");

        if (request.Status != null) evt.Status = request.Status;
        if (request.FiledDate.HasValue) evt.FiledDate = request.FiledDate.Value;
        if (request.FilingReference != null) evt.FilingReference = request.FilingReference;
        if (request.TaxAmount.HasValue) evt.TaxAmount = request.TaxAmount.Value;
        if (request.Notes != null) evt.Notes = request.Notes;
        if (request.ReminderDaysBefore.HasValue) evt.ReminderDaysBefore = request.ReminderDaysBefore.Value;

        evt.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return MapToResponse(evt);
    }

    public async Task<List<TaxCalendarEventResponse>> GetUpcomingAsync(Guid companyId, int daysAhead = 30)
    {
        var today = DateTime.UtcNow.Date;
        var cutoff = today.AddDays(daysAhead);

        var events = await _db.Set<TaxCalendarEvent>()
            .Where(e => e.CompanyId == companyId
                && !e.IsDeleted
                && e.Status != "Filed"
                && e.Status != "NotApplicable"
                && e.DueDate >= today
                && e.DueDate <= cutoff)
            .OrderBy(e => e.DueDate)
            .ToListAsync();

        return events.Select(MapToResponse).ToList();
    }

    public async Task<List<TaxCalendarEventResponse>> GetOverdueAsync(Guid companyId)
    {
        var today = DateTime.UtcNow.Date;

        var events = await _db.Set<TaxCalendarEvent>()
            .Where(e => e.CompanyId == companyId
                && !e.IsDeleted
                && e.Status != "Filed"
                && e.Status != "NotApplicable"
                && e.DueDate < today)
            .OrderBy(e => e.DueDate)
            .ToListAsync();

        // Mark overdue events
        foreach (var evt in events.Where(e => e.Status == "Pending" || e.Status == "InProgress"))
        {
            evt.Status = "Overdue";
        }

        if (events.Any(e => e.Status == "Overdue"))
            await _db.SaveChangesAsync();

        return events.Select(MapToResponse).ToList();
    }

    public async Task ProcessRemindersAsync()
    {
        var today = DateTime.UtcNow.Date;

        var events = await _db.Set<TaxCalendarEvent>()
            .Where(e => !e.IsDeleted
                && !e.ReminderSent
                && e.Status != "Filed"
                && e.Status != "NotApplicable"
                && e.DueDate >= today)
            .ToListAsync();

        foreach (var evt in events)
        {
            var reminderDate = evt.DueDate.AddDays(-evt.ReminderDaysBefore);
            if (today >= reminderDate)
            {
                evt.ReminderSent = true;
                // In production, this would trigger a notification via INotificationService
            }
        }

        await _db.SaveChangesAsync();
    }

    // ===== Mapping =====

    private static TaxCalendarEventResponse MapToResponse(TaxCalendarEvent e)
    {
        var daysUntilDue = (int)(e.DueDate.Date - DateTime.UtcNow.Date).TotalDays;
        return new TaxCalendarEventResponse(
            e.Id, e.TaxFormCode, e.TaxFormName, e.Year, e.Month,
            e.DueDate, e.EFilingDueDate, e.Status, e.FiledDate,
            e.FilingReference, e.TaxAmount, e.ReminderDaysBefore,
            e.ReminderSent, daysUntilDue);
    }
}

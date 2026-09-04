using System.Reflection;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ล็อกการคัดลอก "ผลการอ่านกระดาษ" ของสแกนซ้ำ (บั๊กจริงที่ผู้ใช้รายงาน)
///
/// <para>โค้ดเดิมคัดลอกด้วย **รายการช่องที่เขียนมือ 14 ช่องจาก 63** ⇒ ข้อความดิบ ·
/// รายการสินค้า · ช่อง §86/4 ฝั่งผู้ซื้อ · <c>TargetDocumentType</c> · หมวดค่าใช้จ่าย ·
/// WHT · ค่าความมั่นใจรายช่อง หายทุกครั้งที่อัปโหลดไฟล์เดิมซ้ำ — เงียบสนิท
/// เพราะไม่มีอะไรพัง มีแต่ช่องว่าง</para>
///
/// <para><b>ทำไมต้องเป็นเทสต์ ไม่ใช่ checker</b> — ตัวที่กันบั๊กคลาสนี้ได้จริงคือ
/// "เติมค่าทุกช่องแล้วพิสูจน์ว่าไม่มีช่องไหนหลุด" ซึ่งต้องรันโค้ดจริง + reflection
/// regex มองไม่เห็นว่าช่องไหนถูกคัดลอกบ้าง</para>
/// </summary>
public class OcrScanSnapshotTests
{
    private static PropertyInfo[] Declared() =>
        typeof(OcrScanResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.CanRead && p.CanWrite)
            .ToArray();

    [Fact]
    public void ทุกช่องต้องถูกตัดสินแล้วว่าคัดลอกหรือไม่คัดลอก()
    {
        var declared = Declared().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var accounted = OcrScanSnapshot.CopiedFieldNames
            .Concat(OcrScanSnapshot.RowIdentityFields)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(declared, accounted);
    }

    [Fact]
    public void ลิสต์ช่องที่ห้ามคัดลอกต้องอ้างชื่อที่มีอยู่จริง()
    {
        // จับการเปลี่ยนชื่อพร็อพเพอร์ตี้: ถ้ามีคน rename แล้วลืมแก้ลิสต์
        // ช่องนั้นจะกลายเป็น "คัดลอก" เงียบ ๆ ซึ่งอาจทำให้แถวใหม่อ้างเอกสารของแถวเก่า
        var declared = Declared().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in OcrScanSnapshot.RowIdentityFields)
            Assert.True(declared.Contains(name), $"RowIdentityFields อ้าง '{name}' ที่ไม่มีบน OcrScanResult แล้ว");
    }

    [Fact]
    public void ช่องที่เคยหายไปกับบั๊กจริงต้องถูกคัดลอก()
    {
        // ลิสต์นี้คือ "อาการที่ผู้ใช้เห็นบนหน้าจอ" แปลงเป็นข้อเท็จจริงที่ทดสอบได้
        var mustCopy = new[]
        {
            nameof(OcrScanResult.RawTextContent),
            nameof(OcrScanResult.ExtractedItemsJson),
            nameof(OcrScanResult.FieldConfidenceJson),
            nameof(OcrScanResult.ContentFingerprint),
            nameof(OcrScanResult.ScannedDocumentType),
            nameof(OcrScanResult.OurRole),
            nameof(OcrScanResult.TargetDocumentType),
            nameof(OcrScanResult.BuyerName),
            nameof(OcrScanResult.BuyerTaxId),
            nameof(OcrScanResult.BuyerAddress),
            nameof(OcrScanResult.BuyerBranchCode),
            nameof(OcrScanResult.VendorAddress),
            nameof(OcrScanResult.VendorBranchCode),
            nameof(OcrScanResult.ExtractedDiscountAmount),
            nameof(OcrScanResult.ExpenseCategory),
            nameof(OcrScanResult.SuggestedAccountsJson),
            nameof(OcrScanResult.HasWht),
            nameof(OcrScanResult.WhtRate),
            nameof(OcrScanResult.PaymentTermsDays),
        };
        var copied = OcrScanSnapshot.CopiedFieldNames.ToHashSet(StringComparer.Ordinal);
        foreach (var name in mustCopy)
            Assert.True(copied.Contains(name), $"'{name}' ต้องถูกคัดลอกไปยังสแกนสำเนา");
    }

    [Fact]
    public void เติมค่าทุกช่องแล้วคัดลอกต้องไม่มีช่องไหนหลุด()
    {
        // เทสต์ตัวสำคัญที่สุด: พิสูจน์ว่า **ไม่มีช่องใดในชุดคัดลอกถูกข้ามเงียบ ๆ**
        // เพิ่มพร็อพเพอร์ตี้ใหม่โดยไม่ตัดสินใจ = ตกเทสต์นี้ทันที
        var source = new OcrScanResult();
        var copyable = Declared()
            .Where(p => !OcrScanSnapshot.RowIdentityFields.Contains(p.Name))
            .ToArray();
        foreach (var p in copyable) p.SetValue(source, NonDefault(p.PropertyType));

        var target = new OcrScanResult();
        OcrScanSnapshot.CopyExtractionFrom(source, target);

        foreach (var p in copyable)
            Assert.True(Equals(p.GetValue(source), p.GetValue(target)),
                $"'{p.Name}' ไม่ถูกคัดลอกไปยังสแกนสำเนา");
    }

    [Fact]
    public void ช่องที่เป็นตัวตนของแถวปลายทางต้องไม่ถูกแตะ()
    {
        var source = new OcrScanResult();
        var identity = Declared().Where(p => OcrScanSnapshot.RowIdentityFields.Contains(p.Name)).ToArray();
        foreach (var p in identity) p.SetValue(source, NonDefault(p.PropertyType));

        // ค่าของแถวปลายทางที่ต้องรอดจากการคัดลอก — โดยเฉพาะ CreatedDocumentId
        // (คัดลอกมา = สำเนาอ้างว่าสร้างเอกสารของต้นฉบับไปแล้ว ⇒ ปุ่มสร้างเอกสารหาย)
        // และ StockImportedAt (เครื่องหมายกันนำสต็อกเข้าซ้ำของอีกแถว)
        var target = new OcrScanResult();
        var before = identity.ToDictionary(p => p.Name, p => p.GetValue(target));

        OcrScanSnapshot.CopyExtractionFrom(source, target);

        foreach (var p in identity)
            Assert.True(Equals(before[p.Name], p.GetValue(target)),
                $"'{p.Name}' เป็นตัวตนของแถวปลายทาง ห้ามรับค่าจากสแกนอื่น");
    }

    [Fact]
    public void คัดลอกทับตัวเองต้องไม่ทำให้ข้อมูลเพี้ยน()
    {
        var scan = new OcrScanResult { RawTextContent = "ใบกำกับภาษี" };
        OcrScanSnapshot.CopyExtractionFrom(scan, scan);
        Assert.Equal("ใบกำกับภาษี", scan.RawTextContent);
    }

    /// <summary>ค่าที่ไม่ใช่ default ของชนิดนั้น — ใช้พิสูจน์ว่าการคัดลอกเกิดขึ้นจริง</summary>
    private static object NonDefault(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(string)) return "x";
        if (u == typeof(bool)) return true;
        if (u == typeof(int)) return 7;
        if (u == typeof(long)) return 7L;
        if (u == typeof(decimal)) return 7.77m;
        if (u == typeof(double)) return 7.77d;
        if (u == typeof(Guid)) return Guid.NewGuid();
        if (u == typeof(DateTime)) return new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);
        if (u.IsEnum) return Enum.GetValues(u).GetValue(0)!;
        throw new NotSupportedException(
            $"ชนิด {u.Name} ยังไม่มีค่าทดสอบ — เพิ่มใน NonDefault ก่อน ไม่งั้นช่องใหม่จะไม่ถูกตรวจ");
    }
}

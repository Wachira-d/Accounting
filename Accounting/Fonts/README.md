# Bundled Thai fonts (Sarabun)

วางไฟล์ฟอนต์ตระกูล **Sarabun** ที่นี่เพื่อคุมหน้าตา PDF ที่ generate ฝั่ง server
(โดยเฉพาะ **หนังสือรับรองการหักภาษี ณ ที่จ่าย / 50 ทวิ**) ให้เหมือนกันทุกเครื่อง/OS
และตรงกับไฟล์ที่ผู้ใช้ download จากเบราว์เซอร์ (ซึ่งใช้ Sarabun).

## ไฟล์ที่ควรวาง (ดาวน์โหลด OFL ฟรีจาก Google Fonts: Sarabun)

```
Sarabun-Regular.ttf
Sarabun-Bold.ttf
```

(option) หรือ TH Sarabun New:
```
THSarabunNew.ttf
THSarabunNew Bold.ttf
```

## ทำไมต้องมี

`EnsureThaiFontsRegistered()` ใน `PdfGenerationService.PdfA3.cs` จะ register ฟอนต์
เหล่านี้ก่อน system font. เดิม register แต่ path Linux (`/usr/share/fonts/...`) —
บน **Windows** หาไม่เจอ → QuestPDF fallback เป็น **Leelawadee** ทำให้ใบหัก ณ ที่จ่าย
หน้าตาต่างจากที่ download (Sarabun).

ถ้าเครื่อง server มี **TH Sarabun New** ติดตั้งอยู่แล้ว (`C:\Windows\Fonts\THSarabunNew.ttf`)
ระบบจะ register ให้อัตโนมัติ — ไม่ต้องวางไฟล์ที่นี่ก็ได้. แต่การ bundle ไว้ที่นี่
รับประกันว่าเหมือนกันทุกเครื่อง.

> ฟอนต์ Sarabun เป็น **SIL Open Font License (OFL)** — แจกจ่าย/bundle ได้ฟรี.

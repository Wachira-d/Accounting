namespace Accounting.Services.Implementations;

/// <summary>สไตล์ของแบบ 50 ทวิ ฝั่ง HTML (เส้น Chromium) — <b>สำเนาตรงตัว</b>ของ <c>&lt;style&gt;</c> ในหน้าพิมพ์ <c>wht.html printCert</c>
/// (ทีม J 2026-10-22 · WYSIWYG) · เดิม HTML ฝั่งเซิร์ฟเวอร์ใช้ชุดเก่า (ตัวอักษร 15px · ขอบหน้า 6×10 มม. · ตรา 78px) ⇒ ไฟล์จาก Chromium
/// หน้าตาไม่เหมือนหน้ากดพิมพ์ ขณะที่ QuestPDF ปรับตามหน้าพิมพ์ไปแล้ว (2026-10-16) · แก้ที่หนึ่งต้องแก้อีกที่ในคอมมิตเดียว —
/// <c>WhtCertStampTests.ServerCss_IsIdenticalTo_PrintPageCss</c> ล็อกว่าสองชุดเท่ากันทุกบรรทัด</summary>
public partial class PdfGenerationService
{
    internal const string WhtCertCss = @"  @page { size: A4; margin: 4mm 6mm; }
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body {
    font-family: 'TH Sarabun New', 'TH SarabunPSK', 'Sarabun', 'Noto Sans Thai', sans-serif;
    font-size: 13.5px;
    line-height: 1.2;
    color: #000;
    background: #fff;
  }

  /* PAGE */
  .wht-page { width: 198mm; max-width:100%; margin:0 auto; page-break-after:always; }
  .wht-page:last-child { page-break-after:auto; }

  /* COPY LABELS - top-left, OUTSIDE frame */
  .copy-labels { font-size:11px; line-height:1.2; margin-bottom:1px; padding-left:2px; }
  .copy-labels div { color:#666; }
  .copy-labels .copy-active { color:#000; font-weight:700; }

  /* MAIN FRAME TABLE — outer thick frame, each section is its own bordered
     box (กรอบในแต่ละหมวด). With border-collapse: collapse, the cell borders
     between adjacent sections combine into a single 1.5px line, while the
     outer table border (3px) wins on the four outer edges. */
  .F { width:100%; border:3px solid #000; border-collapse:collapse; }
  .F > tbody > tr > td { border:1.5px solid #000; }

  /* inner utility table */
  .inner { width:100%; border:none; border-collapse:collapse; }
  .inner td { border:none; padding:1px 0; vertical-align:baseline; }

  /* TITLE */
  .title-cell { padding:3px 8px; border-bottom:1.5px solid #000 !important; }
  .title-main { font-size:18px; font-weight:700; letter-spacing:1.5px; text-align:center; }
  .title-sub { font-size:13px; text-align:center; margin-top:-1px; }
  /* Top-right เล่มที่/เลขที่ — wider so long numbers don't break the line.
     Long values shrink with auto font-size scaling via the wrapper class. */
  .num-cell { width:160px; vertical-align:top; text-align:left; font-size:12px; line-height:1.4; padding-top:2px; }
  .num-cell .num-val { display:inline-block; max-width:120px; overflow:hidden; white-space:nowrap; text-overflow:ellipsis; vertical-align:bottom; border-bottom:1px dotted #000; padding:0 2px; }
  .num-cell .num-val.long { font-size:10px; letter-spacing:-0.3px; }

  /* HINT TEXT (italic helper under fields) */
  .hint-text { font-size:10px; font-style:italic; color:#444; padding-left:42px; margin-top:-1px; line-height:1.25; }

  /* SECTIONS (payer/payee) */
  .sec-cell { padding:2px 8px 3px; }
  /* Header label MUST stay on one line — never let it wrap or get squeezed
     by the tax-ID column on the right. */
  .sec-hdr { font-weight:700; font-size:13.5px; white-space:nowrap; }
  .f-lbl { font-weight:600; white-space:nowrap; padding-right:4px !important; width:40px; }
  .f-val { }
  .f-branch { white-space:nowrap; padding-left:6px !important; }
  .ul { text-decoration:none; border-bottom:1px dotted #000; padding:0 2px; }

  /* Tax ID boxes — slightly smaller to fit alongside the header on one row */
  .tb { display:inline-block; width:15px; height:19px; border:1.2px solid #000; text-align:center; line-height:19px; font-size:13px; font-weight:700; margin:0 0.5px; background:#fff; vertical-align:middle; }
  .ts { display:inline-block; width:5px; text-align:center; font-weight:700; font-size:11px; vertical-align:middle; }

  .ck { font-size:15px; vertical-align:-2px; }

  /* FORM TYPE ROW */
  .ftype-cell { padding:3px 8px; font-size:13px; }
  .ftype-cell td { font-size:13px; padding:1px 4px !important; }
  .ftype-grid td { padding:1px 6px !important; white-space:nowrap; }
  .sq { display:inline-block; border:1px solid #000; min-width:50px; height:17px; text-align:center; line-height:17px; font-size:12px; vertical-align:middle; padding:0 3px; }

  /* INCOME TABLE */
  .IT { width:100%; border-collapse:collapse; font-size:13px; }
  .IT th, .IT td { border:1px solid #000; padding:1px 4px; vertical-align:top; }
  .IT th { text-align:center; font-weight:700; font-size:12px; }
  .IT .amt { text-align:right; }
  .IT .dtc { text-align:center; font-size:12px; }
  .it-type { padding-left:5px !important; font-size:12px; line-height:1.25; }
  .it-s5 { font-size:11px; line-height:1.2; }
  .total-row { font-weight:700; }
  .total-row td { border-top:2px solid #000 !important; }
  .div-indent { padding-left:14px; font-size:11px; line-height:1.3; }

  /* TOTAL TEXT (with shaded background like official) */
  .totxt-cell { padding:3px 8px; font-size:13.5px; background:#f0f0f0; }

  /* FUND ROW — must fit on a single line like the official form */
  .fund-cell { padding:3px 8px; font-size:10.5px; line-height:1.4; white-space:nowrap; }
  .fund-blank { display:inline-block; min-width:46px; border-bottom:1px dotted #000; vertical-align:bottom; }

  /* CONDITIONS */
  .cond-cell { padding:2px 8px; font-size:13px; }
  .cond-cell td { font-size:13px; }

  /* BOTTOM SPLIT: warning left, signature+stamp right */
  .bottom-split { }
  .bottom-split td { vertical-align:top; padding:3px 8px; }
  .bottom-left { width:35%; font-size:11px; line-height:1.4; border-right:1.5px solid #000 !important; }
  .bottom-right { width:65%; font-size:12px; line-height:1.4; position:relative; padding-right:84px !important; }
  .sig-block { padding:3px 12px 0; }
  .sig-line { margin-bottom:4px; }
  .sig-dots { display:inline-block; min-width:200px; border-bottom:1px dotted #000; vertical-align:bottom; position:relative; height:34px; }
  .sig-img { position:absolute; left:50%; bottom:1px; transform:translateX(-50%); max-height:32px; max-width:190px; object-fit:contain; }
  .sig-date { text-align:center; margin:3px 0 0; font-size:13px; }
  .sig-dots-sm { display:inline-block; min-width:36px; border-bottom:1px dotted #000; text-align:center; padding:0 4px; }
  .stamp-area {
    position:absolute; right:6px; top:50%; transform:translateY(-50%);
    width:70px; height:70px;
    border:1.2px solid #000; border-radius:50%;
    text-align:center; font-size:10px; line-height:1.3;
    display:flex; align-items:center; justify-content:center;
    font-style:italic; color:#222;
  }
  /* ตรานิติบุคคล (ทีม J 2026-10-22) — วงกลมเปล่า + รูปตรากึ่งกลางวง ขนาด/ระยะเลื่อนจากเซิร์ฟเวอร์ (DocumentStampPolicy) · ตรงกับ PDF ทั้งสอง */
  .stamp-area.has-stamp { font-size:0; }
  .stamp-img { position:absolute; left:50%; top:50%; object-fit:contain; z-index:5; }

  /* FOOTNOTE outside frame */
  .footnote { font-size:9.5px; line-height:1.25; margin-top:1px; padding:0 2px; }

  /* PRINT / SCREEN */
  @media print {
    body {
      margin:0; padding:0;
      -webkit-print-color-adjust: exact;
      print-color-adjust: exact;
    }
    .wht-page { width:100%; }
    .noprint { display:none !important; }
    .F { border:3px solid #000 !important; }
    .F > tbody > tr > td { border:1.5px solid #000 !important; }
  }
  @media screen {
    body { background:#c8c8c8; padding:20px 0; }
    .wht-page { background:#fff; padding:8mm 10mm; margin-bottom:20px; box-shadow:0 2px 12px rgba(0,0,0,.2); }
    .noprint { text-align:center; margin:0 auto 16px; }
    .noprint button { padding:10px 32px; font-size:16px; font-family:inherit; background:#1d4ed8; color:#fff; border:none; border-radius:6px; cursor:pointer; }
    .noprint button:hover { background:#1e40af; }
  }
  /* ===== ลายน้ำ ""ยกเลิก"" สำหรับเอกสารที่ถูก void ===== */
  .wht-page { position: relative; }
  .void-watermark {
    position: absolute; top: 50%; left: 50%;
    transform: translate(-50%, -50%) rotate(-30deg);
    font-size: 100px; font-weight: 800; color: rgba(220, 38, 38, 0.18);
    letter-spacing: 8px; white-space: nowrap; pointer-events: none;
    z-index: 50; text-align: center; line-height: 1.1;
  }
  .void-watermark small { display:block; font-size:34px; letter-spacing:14px; }
  @media print { .void-watermark { color: rgba(220, 38, 38, 0.22); -webkit-print-color-adjust: exact; print-color-adjust: exact; } }
";
}

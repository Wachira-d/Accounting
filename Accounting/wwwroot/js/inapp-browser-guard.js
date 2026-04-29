// ===== In-App Browser Guard =====
// Detects Facebook / Instagram / Line / TikTok / Messenger in-app browsers
// and replaces the page with instructions to open in a real browser.
// IAB browsers have known issues with: cookies, localStorage persistence,
// 3rd-party SDKs (Google/Facebook SSO), and modern JS APIs.

(function () {
  const ua = navigator.userAgent || '';

  const detectors = [
    { re: /FBAN|FBAV|FB_IAB|FB4A|FBIOS/i, name: 'Facebook' },
    { re: /Instagram/i, name: 'Instagram' },
    { re: /Line\//i, name: 'LINE' },
    { re: /Messenger|MessengerLite/i, name: 'Messenger' },
    { re: /TikTok|musical_ly|BytedanceWebview/i, name: 'TikTok' },
    { re: /Twitter/i, name: 'Twitter' },
  ];

  const hit = detectors.find(d => d.re.test(ua));
  if (!hit) return;

  // Allow override via ?force_iab=1 (for QA/testing)
  if (location.search.includes('force_iab=1')) return;

  const isIOS = /iPad|iPhone|iPod/i.test(ua);
  const fullUrl = location.href;

  // Replace entire page
  document.documentElement.innerHTML = `
    <head>
      <meta charset="UTF-8">
      <meta name="viewport" content="width=device-width, initial-scale=1.0">
      <title>กรุณาเปิดในเบราว์เซอร์ปกติ</title>
      <link href="https://fonts.googleapis.com/css2?family=Noto+Sans+Thai:wght@400;500;600;700&display=swap" rel="stylesheet">
      <style>
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
          font-family: 'Noto Sans Thai', -apple-system, sans-serif;
          background: linear-gradient(135deg, #6366f1, #4f46e5);
          min-height: 100vh; display: flex; align-items: center; justify-content: center;
          padding: 20px; color: #1f2937;
        }
        .iab-card {
          background: #fff; max-width: 480px; width: 100%; border-radius: 20px;
          padding: 32px 24px; box-shadow: 0 20px 50px rgba(0,0,0,.25); text-align: center;
        }
        .iab-icon { font-size: 56px; margin-bottom: 12px; }
        h1 { font-size: 22px; color: #4f46e5; margin-bottom: 8px; }
        .iab-sub { color: #6b7280; font-size: 14px; margin-bottom: 24px; line-height: 1.6; }
        .iab-steps {
          background: #f9fafb; border-radius: 12px; padding: 16px;
          text-align: left; font-size: 14px; line-height: 1.8; color: #374151;
          margin-bottom: 20px;
        }
        .iab-steps b { color: #4f46e5; }
        .iab-url {
          background: #eff6ff; border: 1px solid #bfdbfe; border-radius: 10px;
          padding: 12px; word-break: break-all; font-size: 12px; color: #1e40af;
          margin-bottom: 16px; font-family: 'Courier New', monospace;
        }
        .iab-btn {
          display: block; width: 100%; padding: 14px;
          background: linear-gradient(135deg, #6366f1, #4f46e5);
          color: #fff; border: none; border-radius: 10px;
          font-size: 15px; font-weight: 600; cursor: pointer;
          font-family: inherit; margin-bottom: 10px; text-decoration: none; text-align: center;
        }
        .iab-btn-outline {
          background: #fff; color: #4f46e5; border: 1.5px solid #4f46e5;
        }
        .iab-tag {
          display: inline-block; background: #fef3c7; color: #92400e;
          padding: 4px 10px; border-radius: 99px; font-size: 12px; font-weight: 600;
          margin-bottom: 16px;
        }
      </style>
    </head>
    <body>
      <div class="iab-card">
        <div class="iab-icon">🌐</div>
        <div class="iab-tag">ตรวจพบ ${hit.name} In-App Browser</div>
        <h1>กรุณาเปิดในเบราว์เซอร์ปกติ</h1>
        <p class="iab-sub">เบราว์เซอร์ในแอป ${hit.name} ไม่รองรับการเข้าสู่ระบบและ SSO อย่างสมบูรณ์ กรุณาเปิดในเบราว์เซอร์เต็ม (Chrome / Safari) เพื่อใช้งานได้ปกติ</p>

        <div class="iab-steps">
          ${isIOS ? `
            <b>วิธีเปิดใน Safari (iOS):</b><br>
            1. แตะปุ่ม <b>•••</b> ที่มุมขวาล่าง<br>
            2. เลือก <b>"เปิดใน Safari"</b> หรือ <b>"Open in Safari"</b><br>
            หรือคัดลอกลิงก์ด้านล่างไปวางในเบราว์เซอร์เอง
          ` : `
            <b>วิธีเปิดใน Chrome (Android):</b><br>
            1. แตะปุ่ม <b>⋮</b> (3 จุด) ที่มุมขวาบน<br>
            2. เลือก <b>"เปิดในเบราว์เซอร์"</b> หรือ <b>"Open in browser"</b><br>
            หรือคัดลอกลิงก์ด้านล่างไปวางในเบราว์เซอร์เอง
          `}
        </div>

        <div class="iab-url" id="iabUrl">${fullUrl.replace(/</g, '&lt;')}</div>

        <button onclick="copyUrl()" class="iab-btn">📋 คัดลอกลิงก์</button>
        ${isIOS ? '' : `<a href="googlechrome://navigate?url=${encodeURIComponent(fullUrl)}" class="iab-btn iab-btn-outline">เปิดใน Chrome</a>`}
      </div>
      <script>
        function copyUrl() {
          const url = ${JSON.stringify(fullUrl)};
          if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(url).then(() => {
              alert('คัดลอกลิงก์แล้ว! กรุณาเปิดเบราว์เซอร์ปกติแล้ววางลิงก์');
            }).catch(() => fallbackCopy(url));
          } else {
            fallbackCopy(url);
          }
        }
        function fallbackCopy(text) {
          const ta = document.createElement('textarea');
          ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
          document.body.appendChild(ta); ta.select();
          try { document.execCommand('copy'); alert('คัดลอกลิงก์แล้ว!'); }
          catch (e) { alert('กรุณาคัดลอกลิงก์ด้วยตนเอง'); }
          document.body.removeChild(ta);
        }
      </script>
    </body>
  `;

  // Stop further script execution by throwing
  throw new Error('IAB_BLOCKED');
})();

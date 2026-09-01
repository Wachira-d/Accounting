/** ตัวกลาง SSO (authorization-code redirect flow) — ตัวเดียวของทั้งระบบ
 *
 *  ใช้ร่วมกันที่ /login.html และ /register.html: เดิมทั้งการสร้าง authorize URL,
 *  การเก็บ state กัน CSRF และการอ่าน callback ถูก **คัดลอกไว้สองหน้า** ⇒ แก้ที่
 *  เดียวอีกหน้าค้างรุ่นเก่าแน่นอน (CLAUDE.md — "รายการที่คัดลอกมาด้วยมือ = drift
 *  แน่นอน แค่รอเวลา")
 *
 *  ทำไมเป็น redirect ไม่ใช่ SDK/popup: สคริปต์ของ Google (One Tap) และ Facebook
 *  ต้องผ่าน CSP ของเราเอง + ตัวบล็อกโฆษณา + นโยบายคุกกี้บุคคลที่สาม/FedCM จึงจะ
 *  ทำงาน — พังได้หลายทางโดยหน้าเว็บไม่รู้สาเหตุ (เคสจริง: CSP ไม่มี
 *  accounts.google.com ⇒ ปุ่ม Google ขึ้น "โหลดบริการไม่สำเร็จ" ตลอด) ส่วน
 *  redirect ใช้แค่การเปลี่ยนหน้า ไม่มีอะไรมาขวางได้ และทำงานใน in-app browser ด้วย
 */
window.Sso = {
  KEY_STATE: 'ssoState',
  KEY_PROVIDER: 'ssoProvider',
  KEY_SIGNUP: 'ssoSignup',

  AUTHORIZE: {
    Line: 'https://access.line.me/oauth2/v2.1/authorize',
    Google: 'https://accounts.google.com/o/oauth2/v2/auth',
  },
  SCOPE: {
    Line: 'profile openid email',
    Google: 'openid email profile',
  },

  /** ⚠️ ต้องเป็น **ค่าเดียวกับที่เซิร์ฟเวอร์ใช้ตอนแลก code** (มาจาก AppBaseUrl)
   *  ห้ามคำนวณจาก location.origin เอง ⇒ เปิดด้วย www. แต่ AppBaseUrl ไม่มี www
   *  (หรือกลับกัน) = คนละ URL ⇒ provider ตอบ invalid_grant/redirect_uri_mismatch
   *  ว่าง = ผู้ดูแลยังไม่ได้ตั้งค่า → ต้องบอกผู้ใช้ ห้ามพาไปแล้วไปตายปลายทาง */
  callbackUrl(provider, cfg) {
    cfg = cfg || {};
    return (provider === 'Google' ? cfg.googleCallbackUrl : cfg.lineCallbackUrl) || '';
  },

  clientId(provider, cfg) {
    cfg = cfg || {};
    return (provider === 'Google' ? cfg.google : cfg.line) || '';
  },

  supportsRedirect(provider, cfg) {
    return !!(this.clientId(provider, cfg) && this.callbackUrl(provider, cfg));
  },

  /** พาไปหน้าอนุญาตของ provider · signup = ข้อมูลที่กรอกไว้ที่หน้าสมัคร
   *  (ชื่อบริษัท/แพ็กเกจ/หลักฐานการยอมรับข้อกำหนด) ซึ่งต้องพกข้ามไปด้วย ไม่งั้น
   *  ตอนวนกลับมาที่ /login.html จะกลายเป็นสมัครโดยไม่มีหลักฐานยินยอม (PDPA ม.19)
   *  คืน false = ยังไม่พร้อม (ผู้เรียกต้องแสดงเหตุผล ห้ามเงียบ) */
  begin(provider, cfg, signup) {
    if (!this.supportsRedirect(provider, cfg)) return false;
    const state = Math.random().toString(36).slice(2) + Date.now().toString(36);
    try {
      sessionStorage.setItem(this.KEY_STATE, state);
      sessionStorage.setItem(this.KEY_PROVIDER, provider);
      if (signup) sessionStorage.setItem(this.KEY_SIGNUP, JSON.stringify(signup));
      else sessionStorage.removeItem(this.KEY_SIGNUP);
    } catch (e) {}
    let url = this.AUTHORIZE[provider] + '?response_type=code'
      + '&client_id=' + encodeURIComponent(this.clientId(provider, cfg))
      + '&redirect_uri=' + encodeURIComponent(this.callbackUrl(provider, cfg))
      + '&state=' + encodeURIComponent(state)
      + '&scope=' + encodeURIComponent(this.SCOPE[provider]);
    // Google: บังคับให้เลือกบัญชีทุกครั้ง — เครื่องที่ค้างล็อกอิน Google ไว้จะได้
    // ไม่เด้งเข้าบัญชีเดิมเงียบ ๆ โดยผู้ใช้ไม่ทันเห็นว่ากำลังใช้บัญชีไหน
    if (provider === 'Google') url += '&prompt=select_account';
    window.location.href = url;
    return true;
  },

  /** อ่านผลที่ provider ส่งกลับมาที่หน้านี้
   *  null = ไม่ใช่การกลับจาก SSO · {error} = ยกเลิก/state ไม่ตรง ·
   *  {provider, code, signup} = พร้อมส่งให้ backend แลก token */
  readCallback() {
    const p = new URLSearchParams(location.search);
    const code = p.get('code'), state = p.get('state'), oauthErr = p.get('error');
    if (!code && !oauthErr) return null;

    // คีย์รุ่นก่อน ('lineState'/'lineSignup') **ไม่อ่านต่อ** — จะกลายเป็นคีย์ที่
    // อ่านแต่ไม่มีใครเขียน ซึ่งเป็น defect class ที่เรพนี้มี checker ดักไว้
    // (tools/localstorage_key_check.py). ผู้ใช้ที่กำลังวนอยู่พอดีตอน deploy จะ
    // เจอ "สถานะไม่ตรงกัน — ลองใหม่อีกครั้ง" ครั้งเดียวแล้วกดใหม่ได้ทันที
    let saved = null, provider = 'Line', signup = null;
    try {
      saved = sessionStorage.getItem(this.KEY_STATE);
      provider = sessionStorage.getItem(this.KEY_PROVIDER) || 'Line';
      const raw = sessionStorage.getItem(this.KEY_SIGNUP);
      if (raw) signup = JSON.parse(raw);
    } catch (e) {}
    try {
      [this.KEY_STATE, this.KEY_PROVIDER, this.KEY_SIGNUP]
        .forEach(k => sessionStorage.removeItem(k));
    } catch (e) {}
    // ล้าง query ทิ้งทันที — กันกด refresh แล้วยิง code ซ้ำ (code ใช้ได้ครั้งเดียว)
    history.replaceState({}, '', location.pathname);

    if (oauthErr) {
      return {
        error: oauthErr === 'access_denied'
          ? ('คุณยกเลิกการเข้าสู่ระบบด้วย ' + provider)
          : ('เข้าสู่ระบบด้วย ' + provider + ' ไม่สำเร็จ (' + oauthErr + ')'),
      };
    }
    if (!saved || saved !== state)
      return { error: 'สถานะการเข้าสู่ระบบ ' + provider + ' ไม่ตรงกัน — ลองใหม่อีกครั้ง' };
    return { provider: provider, code: code, signup: signup };
  },

  /** ข้อความเดียวกันทุกหน้าเมื่อ redirect flow ยังไม่พร้อม — บอก**ทางแก้** เสมอ
   *  (ห้ามโทษตัวบล็อกโฆษณาแบบเดิม ซึ่งพาไล่ต้นเหตุผิดทาง) */
  notReadyMessage(provider) {
    return provider === 'Google'
      ? 'ยังเข้าสู่ระบบด้วย Google ไม่ได้ — ผู้ดูแลระบบต้องตั้ง "Google Client Secret" '
        + 'และ "URL ของระบบ" ในหน้าแอดมิน แล้วเพิ่ม Callback URL เดียวกันใน Google Cloud Console'
      : 'ยังเข้าสู่ระบบด้วย ' + provider + ' ไม่ได้ — ผู้ดูแลระบบต้องตั้ง "URL ของระบบ" '
        + 'ในหน้าแอดมิน ให้ตรงกับ Callback URL ที่ลงทะเบียนไว้';
  },
};

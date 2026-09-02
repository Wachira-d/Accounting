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
  /** 'login' (ค่าเริ่มต้น) | 'link' = ผูกบัญชีเพิ่มให้ผู้ใช้ที่ล็อกอินอยู่แล้ว
   *  ต้องเก็บไว้เพราะ provider อนุญาต callback URL ได้ชุดเดียว (/login.html)
   *  ⇒ ขากลับต้องรู้เองว่า "กดมาจากหน้าไหน เพื่อทำอะไร" */
  KEY_MODE: 'ssoMode',

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

  /** ชื่อที่โชว์ผู้ใช้ — ต้องตรงกับ SsoIdentityPolicy.DisplayName ฝั่งเซิร์ฟเวอร์
   *  (เดิมแต่ละหน้าพิมพ์ 'LINE' เอง ⇒ บางที่ได้ "Line" บางที่ได้ "LINE") */
  DISPLAY: { Line: 'LINE', Google: 'Google', Facebook: 'Facebook' },
  displayName(provider) {
    return this.DISPLAY[provider] || provider || 'ผู้ให้บริการภายนอก';
  },

  /** state กัน CSRF — ต้องเดาไม่ได้. Math.random() ไม่ใช่ตัวสุ่มเชิงรหัสลับ
   *  (ทำนายค่าถัดไปได้จากค่าก่อนหน้า) ⇒ ใช้ crypto.getRandomValues เป็นหลัก */
  _newState() {
    const g = window.crypto || window.msCrypto;
    if (g && g.getRandomValues) {
      const buf = new Uint8Array(16);
      g.getRandomValues(buf);
      return Array.from(buf, b => b.toString(16).padStart(2, '0')).join('');
    }
    // เบราว์เซอร์เก่าที่ไม่มี WebCrypto — ยอมให้เข้าระบบได้ แต่ต้องไม่เงียบ
    console.warn('[SSO] ไม่มี crypto.getRandomValues — state กัน CSRF อ่อนกว่าปกติ');
    return Math.random().toString(36).slice(2) + Date.now().toString(36);
  },

  supportsRedirect(provider, cfg) {
    return !!(this.clientId(provider, cfg) && this.callbackUrl(provider, cfg));
  },

  /** พาไปหน้าอนุญาตของ provider · signup = ข้อมูลที่กรอกไว้ที่หน้าสมัคร
   *  (ชื่อบริษัท/แพ็กเกจ/หลักฐานการยอมรับข้อกำหนด) ซึ่งต้องพกข้ามไปด้วย ไม่งั้น
   *  ตอนวนกลับมาที่ /login.html จะกลายเป็นสมัครโดยไม่มีหลักฐานยินยอม (PDPA ม.19)
   *  คืน false = ยังไม่พร้อม (ผู้เรียกต้องแสดงเหตุผล ห้ามเงียบ) */
  begin(provider, cfg, signup, mode) {
    this.lastError = null;
    if (!this.supportsRedirect(provider, cfg)) return false;
    const state = this._newState();
    try {
      sessionStorage.setItem(this.KEY_STATE, state);
      sessionStorage.setItem(this.KEY_PROVIDER, provider);
      sessionStorage.setItem(this.KEY_MODE, mode || 'login');
      if (signup) sessionStorage.setItem(this.KEY_SIGNUP, JSON.stringify(signup));
      else sessionStorage.removeItem(this.KEY_SIGNUP);
    } catch (e) {
      // เขียน sessionStorage ไม่ได้ (โหมดส่วนตัวบางตัว / บล็อกที่เก็บข้อมูลเว็บ)
      // ⇒ ขากลับจะไม่มี state ให้เทียบ = ตกด่าน CSRF แน่นอน. เดิมกลืน error
      // แล้วพาไปต่อ ผู้ใช้จึงเดินครบรอบแล้วเจอ "สถานะไม่ตรงกัน" โดยไม่รู้สาเหตุ
      // — หยุดตรงนี้พร้อมบอกทางแก้ ดีกว่าพาไปตายปลายทาง
      this.lastError = 'เบราว์เซอร์นี้ปิดการเก็บข้อมูลเว็บไว้ จึงเข้าสู่ระบบด้วย '
        + this.displayName(provider) + ' ไม่ได้ — เปิดการอนุญาตคุกกี้/ที่เก็บข้อมูล '
        + 'ของเว็บนี้ หรือลองในหน้าต่างปกติ (ไม่ใช่โหมดส่วนตัว)';
      return false;
    }
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
    // provider เริ่มต้นเป็น null — ห้ามเดาว่าเป็น LINE เพราะข้อความที่ขึ้นจะโทษ
    // ผู้ให้บริการผิดตัว ("เข้าสู่ระบบด้วย LINE ไม่สำเร็จ" ทั้งที่กด Google)
    // (CLAUDE.md — "ค่า default ที่แต่งขึ้นอันตรายกว่าการไม่ตอบ")
    let saved = null, provider = null, signup = null, mode = 'login';
    try {
      saved = sessionStorage.getItem(this.KEY_STATE);
      provider = sessionStorage.getItem(this.KEY_PROVIDER);
      mode = sessionStorage.getItem(this.KEY_MODE) || 'login';
      const raw = sessionStorage.getItem(this.KEY_SIGNUP);
      if (raw) signup = JSON.parse(raw);
    } catch (e) {
      saved = null;   // อ่านไม่ได้ = เทียบ state ไม่ได้ → ตกด่านข้างล่างตามปกติ
    }
    try {
      [this.KEY_STATE, this.KEY_PROVIDER, this.KEY_SIGNUP, this.KEY_MODE]
        .forEach(k => sessionStorage.removeItem(k));
    } catch (e) {}
    // ล้าง query ทิ้งทันที — กันกด refresh แล้วยิง code ซ้ำ (code ใช้ได้ครั้งเดียว)
    history.replaceState({}, '', location.pathname);

    const name = this.displayName(provider);
    if (oauthErr) {
      return {
        mode: mode,
        error: oauthErr === 'access_denied'
          ? ('คุณยกเลิกการเข้าสู่ระบบด้วย ' + name)
          : ('เข้าสู่ระบบด้วย ' + name + ' ไม่สำเร็จ (' + oauthErr + ')'),
      };
    }
    if (!saved || saved !== state)
      return { mode: mode, error: 'สถานะการเข้าสู่ระบบ ' + name + ' ไม่ตรงกัน — ลองใหม่อีกครั้ง' };
    // ผ่านด่าน state แล้วแต่ไม่รู้ว่า provider ไหน = อ่านค่าที่เก็บไว้ไม่ครบ
    // ส่งต่อให้ backend ด้วยชื่อที่เดาเอาเองไม่ได้ (จะแลก code ผิดผู้ให้บริการ)
    if (!provider)
      return { mode: mode, error: 'ไม่ทราบผู้ให้บริการที่ใช้เข้าสู่ระบบ — กดปุ่มเข้าสู่ระบบอีกครั้ง' };
    return { provider: provider, code: code, signup: signup, mode: mode };
  },

  /** ข้อความเดียวกันทุกหน้าเมื่อ redirect flow ยังไม่พร้อม — บอก**ทางแก้** เสมอ
   *  (ห้ามโทษตัวบล็อกโฆษณาแบบเดิม ซึ่งพาไล่ต้นเหตุผิดทาง) */
  notReadyMessage(provider) {
    // เหตุผลที่ begin() เจอกับตัว (เช่น sessionStorage ถูกปิด) ชนะข้อความทั่วไป
    if (this.lastError) return this.lastError;
    const name = this.displayName(provider);
    return provider === 'Google'
      ? 'ยังเข้าสู่ระบบด้วย Google ไม่ได้ — ผู้ดูแลระบบต้องตั้ง "Google Client Secret" '
        + 'และ "URL ของระบบ" ในหน้าแอดมิน แล้วเพิ่ม Callback URL เดียวกันใน Google Cloud Console'
      : 'ยังเข้าสู่ระบบด้วย ' + name + ' ไม่ได้ — ผู้ดูแลระบบต้องตั้ง "URL ของระบบ" '
        + 'ในหน้าแอดมิน ให้ตรงกับ Callback URL ที่ลงทะเบียนไว้';
  },

  /** เหตุผลล่าสุดที่ begin() คืน false (null = ยังไม่มี) */
  lastError: null,
};

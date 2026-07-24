// Admin API client
const AdminAPI = {
  base: '/api/admin',
  token: localStorage.getItem('admin_token'),

  async request(method, path, body) {
    const opts = {
      method,
      headers: { 'Content-Type': 'application/json' }
    };
    // Re-read token from localStorage in case it was refreshed after page load
    const token = localStorage.getItem('admin_token');
    if (token) opts.headers['Authorization'] = `Bearer ${token}`;
    if (body) opts.body = JSON.stringify(body);

    const res = await fetch(`${this.base}${path}`, opts);
    if (res.status === 401) {
      localStorage.removeItem('admin_token');
      localStorage.removeItem('admin_user');
      window.location.href = '/admin/login.html';
      throw new Error('Unauthorized');
    }
    if (res.status === 403) throw new Error('ไม่มีสิทธิ์เข้าถึง (ต้องเป็น System Admin)');

    // Check content-type to avoid parsing HTML as JSON
    const contentType = res.headers.get('content-type') || '';
    if (!contentType.includes('application/json')) {
      const errMsg = `Server returned non-JSON response (HTTP ${res.status}) for ${method} ${path}. กรุณา rebuild + restart server`;
      this.logError(method, path, res.status, errMsg);
      throw new Error(errMsg);
    }

    const data = await res.json();
    if (!data.success) {
      this.logError(method, path, res.status, data.message || 'Unknown error');
      throw new Error(data.message || 'เกิดข้อผิดพลาด');
    }
    return data;
  },

  get(path) { return this.request('GET', path); },
  post(path, body) { return this.request('POST', path, body); },
  put(path, body) { return this.request('PUT', path, body); },
  del(path) { return this.request('DELETE', path); },

  async upload(path, formData) {
    const token = localStorage.getItem('admin_token');
    const opts = { method: 'POST', body: formData, headers: {} };
    if (token) opts.headers['Authorization'] = `Bearer ${token}`;
    const res = await fetch(`${this.base}${path}`, opts);
    if (res.status === 401) { window.location.href = '/admin/login.html'; throw new Error('Unauthorized'); }
    const data = await res.json();
    if (!data.success) throw new Error(data.message || 'อัพโหลดไม่สำเร็จ');
    return data;
  },

  // Binary download (adds auth header, triggers browser save)
  async downloadFile(path, fallbackName) {
    const token = localStorage.getItem('admin_token');
    const res = await fetch(`${this.base}${path}`, { headers: token ? { Authorization: `Bearer ${token}` } : {} });
    if (res.status === 401) { window.location.href = '/admin/login.html'; throw new Error('Unauthorized'); }
    if (!res.ok) {
      let msg = 'ดาวน์โหลดไม่สำเร็จ';
      try { const j = await res.json(); msg = j.message || msg; } catch {}
      throw new Error(msg);
    }
    const blob = await res.blob();
    const cd = res.headers.get('content-disposition') || '';
    const m = cd.match(/filename\*?=(?:UTF-8''|")?([^";]+)/i);
    const name = m ? decodeURIComponent(m[1]) : (fallbackName || 'download');
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = name; document.body.appendChild(a); a.click();
    a.remove(); URL.revokeObjectURL(url);
  },
  downloadPaymentReceipt(paymentId) { return this.downloadFile(`/subscription-payments/${paymentId}/receipt`, `receipt-${paymentId}.pdf`); },
  issueRenewalInvoice(companyId) { return this.post(`/companies/${companyId}/renewal-invoice`, {}); },
  downloadRenewalInvoice(companyId) { return this.downloadFile(`/companies/${companyId}/renewal-invoice`, `invoice-${companyId}.pdf`); },

  // Auth
  async login(email, password) {
    const res = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password })
    });
    const data = await res.json();
    if (!data.success) throw new Error(data.message || 'เข้าสู่ระบบไม่สำเร็จ');
    if (!data.data.user.isSystemAdmin) throw new Error('บัญชีนี้ไม่มีสิทธิ์ Admin');
    return data;
  },

  // Dashboard
  dashboard() { return this.get('/dashboard'); },

  // Customers
  customers(params = '') { return this.get(`/customers${params}`); },
  customer(id) { return this.get(`/customers/${id}`); },
  updateCustomerStatus(id, status) { return this.put(`/customers/${id}/status`, { status }); },

  // Users
  users(params = '') { return this.get(`/users${params}`); },
  updateUserStatus(id, status) { return this.put(`/users/${id}/status`, { status }); },
  toggleAdmin(id, isAdmin) { return this.put(`/users/${id}/admin`, { isAdmin }); },

  // Plans
  plans(includeInactive = true) { return this.get(`/plans?includeInactive=${includeInactive}`); },
  createPlan(data) { return this.post('/plans', data); },
  updatePlan(id, data) { return this.put(`/plans/${id}`, data); },
  resyncPlanSubscriptions(id) { return this.post(`/plans/${id}/resync-subscriptions`, {}); },
  features() { return this.get('/features'); },

  // Payments
  pendingPayments() { return this.get('/subscription-payments/pending'); },
  allPayments(params = '') { return this.get(`/subscription-payments/all${params}`); },
  paymentDetail(id) { return this.get(`/subscription-payments/${id}`); },
  reviewPayment(id, approve, notes) { return this.post(`/subscription-payments/${id}/review`, { approve, reviewNotes: notes }); },
  recordManualPayment(companyId, data) { return this.post(`/companies/${companyId}/subscription-payments/record`, data); },

  // Company specifics
  companyTrial(id) { return this.get(`/companies/${id}/trial`); },
  extendTrial(companyId, data) { return this.post(`/companies/${companyId}/trial/extend`, data); },
  expireTrial(companyId) { return this.post(`/companies/${companyId}/trial/expire`); },
  companyPayments(id) { return this.get(`/companies/${id}/subscription-payments`); },

  // Admin: Direct Subscription Management
  changePlan(companyId, data) { return this.put(`/companies/${companyId}/subscription/plan`, data); },
  changeSubStatus(companyId, data) { return this.put(`/companies/${companyId}/subscription/status`, data); },
  changeDates(companyId, data) { return this.put(`/companies/${companyId}/subscription/dates`, data); },
  changeLimits(companyId, data) { return this.put(`/companies/${companyId}/subscription/limits`, data); },
  subHistory(companyId) { return this.get(`/companies/${companyId}/subscription/history`); },
  updateTrialConfig(companyId, data) { return this.put(`/companies/${companyId}/trial`, data); },

  // Company User Role Management (Admin override)
  changeCompanyUserRole(companyId, userId, role) { return this.put(`/companies/${companyId}/users/${userId}/role`, { role }); },

  // Background processing
  processExpiredTrials() { return this.post('/trial/process-expired'); },
  processExpiredSubs() { return this.post('/subscription/process-expired'); },
  processNotifications() { return this.post('/subscription/process-notifications'); },
  processRecurring() { return this.post('/recurring/process'); },

  // Master Chart of Accounts template (qs = scope query string, e.g. '?businessType=Partnership')
  coaTemplateScopes() { return this.get('/coa-template/scopes'); },
  coaTemplate(qs = '') { return this.get('/coa-template' + qs); },
  saveCoaTemplate(rows, qs = '') { return this.put('/coa-template' + qs, rows); },
  seedCoaTemplateBuiltin(qs = '') { return this.post('/coa-template/seed-builtin' + qs, {}); },

  // Audit log (system-wide)
  auditLogs(params = '') { return this.get(`/audit-logs${params}`); },

  // Error log (system-wide)
  errorLogs(params = '') { return this.get(`/error-logs${params}`); },
  purgeErrorLogs(days) { return this.del(`/error-logs/purge?olderThanDays=${days}`); },
  // ลบบริษัท (soft-delete, SystemAdmin) — ต้องส่งชื่อบริษัทตรงทุกตัวอักษร
  deleteCompany(companyId, confirmName) { return this.del(`/companies/${companyId}?confirmName=${encodeURIComponent(confirmName)}`); },

  // Site Settings
  siteSettings() { return this.get('/site-settings'); },
  platformBilling() { return this.get('/platform-billing-settings'); },
  updatePlatformBilling(data) { return this.put('/platform-billing-settings', data); },
  updateSiteSettings(data) { return this.put('/site-settings', data); },

  // System Email Configuration
  systemEmail() { return this.get('/system-email'); },
  updateSystemEmail(data) { return this.put('/system-email', data); },
  testSystemEmail(toAddress) { return this.post('/system-email/test', { toAddress }); },

  // Integrations
  integrations() { return this.get('/integrations'); },

  // Error logging — fire-and-forget, never throws
  logError(method, path, statusCode, message) {
    try {
      fetch('/api/error-log/client', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ requestPath: `${this.base}${path}`, httpMethod: method, statusCode, message, source: 'AdminPanel' })
      }).catch(() => {});
    } catch {}
  },
};

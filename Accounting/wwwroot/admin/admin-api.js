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

  // Payments
  pendingPayments() { return this.get('/subscription-payments/pending'); },
  allPayments(params = '') { return this.get(`/subscription-payments/all${params}`); },
  paymentDetail(id) { return this.get(`/subscription-payments/${id}`); },
  reviewPayment(id, approve, notes) { return this.post(`/subscription-payments/${id}/review`, { approve, reviewNotes: notes }); },

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

  // Background processing
  processExpiredTrials() { return this.post('/trial/process-expired'); },
  processExpiredSubs() { return this.post('/subscription/process-expired'); },
  processNotifications() { return this.post('/subscription/process-notifications'); },

  // Site Settings
  siteSettings() { return this.get('/site-settings'); },
  updateSiteSettings(data) { return this.put('/site-settings', data); },

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

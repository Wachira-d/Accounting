// ===== API Client =====
const API = {
  baseUrl: '',
  token: null,

  init() {
    this.token = localStorage.getItem('token');
  },

  async request(method, url, data = null, isFormData = false) {
    const headers = {};
    if (this.token) headers['Authorization'] = `Bearer ${this.token}`;
    if (!isFormData) headers['Content-Type'] = 'application/json';

    const options = { method, headers };
    if (data && !isFormData) options.body = JSON.stringify(data);
    if (data && isFormData) options.body = data;

    try {
      const res = await fetch(`${this.baseUrl}${url}`, options);
      if (res.status === 401) {
        localStorage.removeItem('token');
        localStorage.removeItem('user');
        window.location.href = '/login.html';
        return;
      }
      const json = await res.json();
      if (!res.ok) throw new Error(json.message || json.title || `Error ${res.status}`);
      return json;
    } catch (err) {
      if (err.message === 'Failed to fetch') throw new Error('ไม่สามารถเชื่อมต่อเซิร์ฟเวอร์ได้');
      throw err;
    }
  },

  get(url) { return this.request('GET', url); },
  post(url, data) { return this.request('POST', url, data); },
  put(url, data) { return this.request('PUT', url, data); },
  del(url) { return this.request('DELETE', url); },
  upload(url, formData) { return this.request('POST', url, formData, true); },

  // Auth
  login(email, password) { return this.post('/api/auth/login', { email, password }); },
  register(data) { return this.post('/api/auth/register', data); },
  changePassword(data) { return this.post('/api/auth/change-password', data); },

  // Company scoped
  c(companyId) {
    const base = `/api/companies/${companyId}`;
    return {
      // Dashboard
      dashboard: (q = '') => API.get(`${base}/dashboard${q}`),
      // Accounting
      getAccounts: () => API.get(`${base}/accounting/accounts`),
      createAccount: (d) => API.post(`${base}/accounting/accounts`, d),
      updateAccount: (id, d) => API.put(`${base}/accounting/accounts/${id}`, d),
      getJournals: (q = '') => API.get(`${base}/accounting/journals${q}`),
      getJournal: (id) => API.get(`${base}/accounting/journals/${id}`),
      createJournal: (d) => API.post(`${base}/accounting/journals`, d),
      postJournal: (id) => API.post(`${base}/accounting/journals/${id}/post`),
      voidJournal: (id) => API.post(`${base}/accounting/journals/${id}/void`),
      trialBalance: (q = '') => API.get(`${base}/accounting/reports/trial-balance${q}`),
      balanceSheet: (q = '') => API.get(`${base}/accounting/reports/balance-sheet${q}`),
      profitLoss: (q = '') => API.get(`${base}/accounting/reports/profit-loss${q}`),
      cashFlow: (q = '') => API.get(`${base}/accounting/reports/cash-flow${q}`),
      getFiscalPeriods: () => API.get(`${base}/accounting/fiscal-periods`),
      createFiscalPeriod: (d) => API.post(`${base}/accounting/fiscal-periods`, d),
      closeFiscalPeriod: (id) => API.post(`${base}/accounting/fiscal-periods/${id}/close`),
      // Documents
      getDocuments: (q = '') => API.get(`${base}/document${q}`),
      getDocument: (id) => API.get(`${base}/document/${id}`),
      createDocument: (d) => API.post(`${base}/document`, d),
      updateDocument: (id, d) => API.put(`${base}/document/${id}`, d),
      approveDocument: (id) => API.post(`${base}/document/${id}/approve`),
      voidDocument: (id) => API.post(`${base}/document/${id}/void`),
      convertDocument: (id, t) => API.post(`${base}/document/${id}/convert/${t}`),
      // Contacts
      getContacts: (q = '') => API.get(`${base}/document/contacts${q}`),
      createContact: (d) => API.post(`${base}/document/contacts`, d),
      updateContact: (id, d) => API.put(`${base}/document/contacts/${id}`, d),
      // Payments
      getPayments: (q = '') => API.get(`${base}/document/payments${q}`),
      createPayment: (d) => API.post(`${base}/document/payments`, d),
      // Products
      getProducts: (q = '') => API.get(`${base}/product${q}`),
      createProduct: (d) => API.post(`${base}/product`, d),
      updateProduct: (id, d) => API.put(`${base}/product/${id}`, d),
      deleteProduct: (id) => API.del(`${base}/product/${id}`),
      // Bank
      getBankAccounts: () => API.get(`${base}/bank/accounts`),
      createBankAccount: (d) => API.post(`${base}/bank/accounts`, d),
      getTransactions: (id, q = '') => API.get(`${base}/bank/accounts/${id}/transactions${q}`),
      reconcile: (d) => API.post(`${base}/bank/reconcile`, d),
      autoMatch: (id) => API.post(`${base}/bank/accounts/${id}/auto-match`),
      // Tax
      getTaxReports: (q = '') => API.get(`${base}/tax${q}`),
      generateTaxReport: (d) => API.post(`${base}/tax/generate`, d),
      // Fixed Assets
      getAssets: (q = '') => API.get(`${base}/fixedasset${q}`),
      createAsset: (d) => API.post(`${base}/fixedasset`, d),
      // Budget
      getBudgets: (q = '') => API.get(`${base}/budget${q}`),
      createBudget: (d) => API.post(`${base}/budget`, d),
      getBudgetVsActual: (id) => API.get(`${base}/budget/${id}/vs-actual`),
      // Payroll
      getEmployees: (q = '') => API.get(`${base}/payroll/employees${q}`),
      createEmployee: (d) => API.post(`${base}/payroll/employees`, d),
      getPayrollRuns: (q = '') => API.get(`${base}/payroll/runs${q}`),
      createPayrollRun: (d) => API.post(`${base}/payroll/runs`, d),
      calculatePayroll: (id) => API.post(`${base}/payroll/runs/${id}/calculate`),
      approvePayroll: (id) => API.post(`${base}/payroll/runs/${id}/approve`),
      // Expense Claims
      getExpenseClaims: (q = '') => API.get(`${base}/expense-claims${q}`),
      createExpenseClaim: (d) => API.post(`${base}/expense-claims`, d),
      // Projects
      getProjects: (q = '') => API.get(`${base}/projects${q}`),
      createProject: (d) => API.post(`${base}/projects`, d),
      // Approval
      getPendingApprovals: () => API.get(`${base}/approval/pending`),
      submitAction: (id, d) => API.post(`${base}/approval/requests/${id}/action`, d),
      // Settings
      getSettings: () => API.get(`${base}/settings`),
      updateSettings: (d) => API.put(`${base}/settings`, d),
      getNumberSeries: () => API.get(`${base}/settings/number-series`),
      // Audit
      getAuditLogs: (q = '') => API.get(`${base}/audit/logs${q}`),
      // Notifications
      getNotifications: () => API.get('/api/notification'),
      getNotificationCount: () => API.get('/api/notification/count'),
      markRead: (ids) => API.post('/api/notification/mark-read', { notificationIds: ids }),
    };
  },

  // Company management
  getCompanies: () => API.get('/api/company'),
  createCompany: (d) => API.post('/api/company', d),
  getCompany: (id) => API.get(`/api/company/${id}`),
  updateCompany: (id, d) => API.put(`/api/company/${id}`, d),

  // Subscription
  getPlans: () => API.get('/api/subscription/plans'),
  startTrial: (d) => API.post('/api/subscription/trial/start', d),
};

API.init();

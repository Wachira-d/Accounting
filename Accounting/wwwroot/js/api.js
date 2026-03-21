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
      updateBankAccount: (id, d) => API.put(`${base}/bank/accounts/${id}`, d),
      getTransactions: (id, q = '') => API.get(`${base}/bank/accounts/${id}/transactions${q}`),
      createTransaction: (d) => API.post(`${base}/bank/transactions`, d),
      reconcile: (d) => API.post(`${base}/bank/reconcile`, d),
      autoMatch: (id) => API.post(`${base}/bank/accounts/${id}/auto-match`),
      getUnreconciled: (id) => API.get(`${base}/bank/accounts/${id}/unreconciled`),
      // Open Banking
      getConnections: () => API.get(`${base}/open-banking/connections`),
      createConnection: (d) => API.post(`${base}/open-banking/connections`, d),
      updateConnection: (id, d) => API.put(`${base}/open-banking/connections/${id}`, d),
      deleteConnection: (id) => API.del(`${base}/open-banking/connections/${id}`),
      syncConnection: (id, q = '') => API.post(`${base}/open-banking/connections/${id}/sync${q}`),
      getConnectionImports: (id) => API.get(`${base}/open-banking/connections/${id}/imports`),
      importBankFile: (accountId, format, base64) => API.post(`${base}/open-banking/import-file?bankAccountId=${accountId}&fileFormat=${format}`, base64),
      // Tax
      getTaxReports: (q = '') => API.get(`${base}/tax${q}`),
      getTaxReport: (id) => API.get(`${base}/tax/${id}`),
      generateTaxReport: (d) => API.post(`${base}/tax/generate`, d),
      fileTaxReport: (id) => API.post(`${base}/tax/${id}/file`),
      // WHT
      getWhtCerts: (q = '') => API.get(`${base}/withholding-tax-certs${q}`),
      getWhtCert: (id) => API.get(`${base}/withholding-tax-certs/${id}`),
      createWhtCert: (d) => API.post(`${base}/withholding-tax-certs`, d),
      issueWhtCert: (id) => API.post(`${base}/withholding-tax-certs/${id}/issue`),
      voidWhtCert: (id) => API.post(`${base}/withholding-tax-certs/${id}/void`),
      getWhtByContact: (contactId, q = '') => API.get(`${base}/withholding-tax-certs/contacts/${contactId}${q}`),
      // Fixed Assets
      getAssets: (q = '') => API.get(`${base}/fixedasset${q}`),
      getAsset: (id) => API.get(`${base}/fixedasset/${id}`),
      createAsset: (d) => API.post(`${base}/fixedasset`, d),
      updateAsset: (id, d) => API.put(`${base}/fixedasset/${id}`, d),
      disposeAsset: (id, d) => API.post(`${base}/fixedasset/${id}/dispose`, d),
      getDepreciations: (id) => API.get(`${base}/fixedasset/${id}/depreciations`),
      runDepreciation: (d) => API.post(`${base}/fixedasset/depreciate`, d),
      // Budget
      getBudgets: (q = '') => API.get(`${base}/budget${q}`),
      getBudget: (id) => API.get(`${base}/budget/${id}`),
      createBudget: (d) => API.post(`${base}/budget`, d),
      updateBudget: (id, d) => API.put(`${base}/budget/${id}`, d),
      deleteBudget: (id) => API.del(`${base}/budget/${id}`),
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
      getExpenseClaim: (id) => API.get(`${base}/expense-claims/${id}`),
      createExpenseClaim: (d) => API.post(`${base}/expense-claims`, d),
      updateExpenseClaim: (id, d) => API.put(`${base}/expense-claims/${id}`, d),
      submitExpenseClaim: (id) => API.post(`${base}/expense-claims/${id}/submit`),
      approveExpenseClaim: (id, d) => API.post(`${base}/expense-claims/${id}/approve`, d),
      rejectExpenseClaim: (id, d) => API.post(`${base}/expense-claims/${id}/reject`, d),
      payExpenseClaim: (id) => API.post(`${base}/expense-claims/${id}/pay`),
      voidExpenseClaim: (id) => API.post(`${base}/expense-claims/${id}/void`),
      // Projects
      getProjects: (q = '') => API.get(`${base}/projects${q}`),
      getProject: (id) => API.get(`${base}/projects/${id}`),
      createProject: (d) => API.post(`${base}/projects`, d),
      updateProject: (id, d) => API.put(`${base}/projects/${id}`, d),
      completeProject: (id) => API.post(`${base}/projects/${id}/complete`),
      getProjectTasks: (id) => API.get(`${base}/projects/${id}/tasks`),
      createProjectTask: (id, d) => API.post(`${base}/projects/${id}/tasks`, d),
      getProjectCosts: (id, q = '') => API.get(`${base}/projects/${id}/costs${q}`),
      addProjectCost: (id, d) => API.post(`${base}/projects/${id}/costs`, d),
      getProjectProfit: (id) => API.get(`${base}/projects/${id}/profitability`),
      // Loans
      getLoans: (q = '') => API.get(`${base}/loans${q}`),
      getLoan: (id) => API.get(`${base}/loans/${id}`),
      createLoan: (d) => API.post(`${base}/loans`, d),
      generateSchedule: (id) => API.post(`${base}/loans/${id}/generate-schedule`),
      getLoanSchedule: (id) => API.get(`${base}/loans/${id}/schedule`),
      makeLoanPayment: (id, d) => API.post(`${base}/loans/${id}/payments`, d),
      getLoanPayments: (id) => API.get(`${base}/loans/${id}/payments`),
      getLoanSummary: () => API.get(`${base}/loans/summary`),
      // Warehouse
      getWarehouses: () => API.get(`${base}/warehouses`),
      createWarehouse: (d) => API.post(`${base}/warehouses`, d),
      updateWarehouse: (id, d) => API.put(`${base}/warehouses/${id}`, d),
      getWarehouseStock: (id) => API.get(`${base}/warehouses/${id}/stock`),
      getTransfers: (q = '') => API.get(`${base}/warehouses/transfers${q}`),
      createTransfer: (d) => API.post(`${base}/warehouses/transfers`, d),
      shipTransfer: (id) => API.post(`${base}/warehouses/transfers/${id}/ship`),
      receiveTransfer: (id, d) => API.post(`${base}/warehouses/transfers/${id}/receive`, d),
      // Payroll - extended
      getEmployee: (id) => API.get(`${base}/payroll/employees/${id}`),
      updateEmployee: (id, d) => API.put(`${base}/payroll/employees/${id}`, d),
      terminateEmployee: (id, date) => API.post(`${base}/payroll/employees/${id}/terminate?endDate=${date}`),
      getPayrollRun: (id) => API.get(`${base}/payroll/runs/${id}`),
      payPayroll: (id) => API.post(`${base}/payroll/runs/${id}/pay`),
      getPayrollDetail: (runId, empId) => API.get(`${base}/payroll/runs/${runId}/employees/${empId}`),
      getPayslip: (runId, empId) => `${base}/payroll/runs/${runId}/employees/${empId}/payslip`,
      getPayrollItems: () => API.get(`${base}/payroll/items`),
      createPayrollItem: (d) => API.post(`${base}/payroll/items`, d),
      getLeaves: (q = '') => API.get(`${base}/payroll/leaves${q}`),
      createLeave: (d) => API.post(`${base}/payroll/leaves`, d),
      approveLeave: (id) => API.post(`${base}/payroll/leaves/${id}/approve`),
      getPnd1: (y, m) => API.get(`${base}/payroll/pnd1/${y}/${m}`),
      getSso: (y, m) => API.get(`${base}/payroll/sso/${y}/${m}`),
      // Tax Calendar
      getTaxEvents: (q = '') => API.get(`${base}/tax-calendar${q}`),
      getTaxEvent: (id) => API.get(`${base}/tax-calendar/${id}`),
      initTaxCalendar: (year) => API.post(`${base}/tax-calendar/initialize/${year}`),
      updateTaxEvent: (id, d) => API.put(`${base}/tax-calendar/${id}`, d),
      getUpcomingTax: (days = 30) => API.get(`${base}/tax-calendar/upcoming?daysAhead=${days}`),
      getOverdueTax: () => API.get(`${base}/tax-calendar/overdue`),
      // Recurring
      getRecurring: (q = '') => API.get(`${base}/recurring${q}`),
      getRecurringItem: (id) => API.get(`${base}/recurring/${id}`),
      createRecurring: (d) => API.post(`${base}/recurring`, d),
      updateRecurring: (id, d) => API.put(`${base}/recurring/${id}`, d),
      deleteRecurring: (id) => API.del(`${base}/recurring/${id}`),
      pauseRecurring: (id) => API.post(`${base}/recurring/${id}/pause`),
      resumeRecurring: (id) => API.post(`${base}/recurring/${id}/resume`),
      runRecurringNow: (id) => API.post(`${base}/recurring/${id}/run-now`),
      // Import/Export
      importData: (formData) => API.upload(`${base}/import-export/import`, formData),
      validateImport: (formData) => API.upload(`${base}/import-export/validate`, formData),
      getImportTemplate: (entity) => `${base}/import-export/templates/${entity}/download`,
      exportData: (d) => API.post(`${base}/import-export/export`, d),
      getExportableEntities: () => API.get(`${base}/import-export/exportable-entities`),
      getImportableEntities: () => API.get(`${base}/import-export/importable-entities`),
      smartImportUpload: (formData) => API.upload(`${base}/import-export/smart-import/upload`, formData),
      smartImportSession: (sid) => API.get(`${base}/import-export/smart-import/sessions/${sid}`),
      smartImportMapping: (d) => API.post(`${base}/import-export/smart-import/manual-mapping`, d),
      smartImportConfirm: (d) => API.post(`${base}/import-export/smart-import/confirm`, d),
      // Currency
      getCurrencies: () => API.get(`${base}/currency`),
      addCurrency: (d) => API.post(`${base}/currency`, d),
      updateCurrency: (id, d) => API.put(`${base}/currency/${id}`, d),
      getExchangeRates: (q = '') => API.get(`${base}/currency/rates${q}`),
      addExchangeRate: (d) => API.post(`${base}/currency/rates`, d),
      getLatestRate: (from, to) => API.get(`${base}/currency/rates/latest?from=${from}&to=${to}`),
      // Portal
      createPortalAccess: (d) => API.post(`${base}/portal/access`, d),
      getPortalAccess: () => API.get(`${base}/portal/access`),
      updatePortalAccess: (id, d) => API.put(`${base}/portal/access/${id}`, d),
      deactivatePortalAccess: (id) => API.post(`${base}/portal/access/${id}/deactivate`),
      // Dimensions & Branches
      getDimensions: () => API.get(`${base}/dimensions`),
      getDimension: (id) => API.get(`${base}/dimensions/${id}`),
      createDimension: (d) => API.post(`${base}/dimensions`, d),
      updateDimension: (id, d) => API.put(`${base}/dimensions/${id}`, d),
      deleteDimension: (id) => API.del(`${base}/dimensions/${id}`),
      getDimensionSummary: () => API.get(`${base}/dimensions/summary`),
      getDimensionPnl: (id) => API.get(`${base}/dimensions/${id}/pnl`),
      getBranches: () => API.get(`${base}/dimensions/branches`),
      createBranch: (d) => API.post(`${base}/dimensions/branches`, d),
      updateBranch: (id, d) => API.put(`${base}/dimensions/branches/${id}`, d),
      // Intercompany
      getIntercompanyTxns: (q = '') => API.get(`${base}/intercompany${q}`),
      getIntercompanyTxn: (id) => API.get(`${base}/intercompany/${id}`),
      createIntercompanyTxn: (d) => API.post(`${base}/intercompany`, d),
      confirmIntercompanyTxn: (id) => API.post(`${base}/intercompany/${id}/confirm`),
      voidIntercompanyTxn: (id) => API.post(`${base}/intercompany/${id}/void`),
      getIntercompanyBalances: () => API.get(`${base}/intercompany/balances`),
      // Consolidation
      getConsolidationGroups: () => API.get('/api/consolidation/groups'),
      getConsolidationGroup: (id) => API.get(`/api/consolidation/groups/${id}`),
      createConsolidationGroup: (d) => API.post('/api/consolidation/groups', d),
      addConsolidationMember: (gid, d) => API.post(`/api/consolidation/groups/${gid}/members`, d),
      removeConsolidationMember: (gid, mid) => API.del(`/api/consolidation/groups/${gid}/members/${mid}`),
      getConsolidatedBS: (gid) => API.get(`/api/consolidation/groups/${gid}/balance-sheet`),
      getConsolidatedPnl: (gid) => API.get(`/api/consolidation/groups/${gid}/pnl`),
      getEliminations: (gid) => API.get(`/api/consolidation/groups/${gid}/eliminations`),
      // Commission
      getCommissionPlans: () => API.get(`${base}/commissions/plans`),
      createCommissionPlan: (d) => API.post(`${base}/commissions/plans`, d),
      updateCommissionPlan: (id, d) => API.put(`${base}/commissions/plans/${id}`, d),
      assignCommissionPlan: (id, d) => API.post(`${base}/commissions/plans/${id}/assign`, d),
      calculateCommissions: (y, m) => API.post(`${base}/commissions/calculate/${y}/${m}`),
      getCommissionCalcs: (y, m) => API.get(`${base}/commissions/calculations/${y}/${m}`),
      approveCommissions: (y, m) => API.post(`${base}/commissions/approve/${y}/${m}`),
      // Revenue Recognition
      getRevenueContracts: (q = '') => API.get(`${base}/revenue-recognition/contracts${q}`),
      getRevenueContract: (id) => API.get(`${base}/revenue-recognition/contracts/${id}`),
      createRevenueContract: (d) => API.post(`${base}/revenue-recognition/contracts`, d),
      addObligation: (id, d) => API.post(`${base}/revenue-recognition/contracts/${id}/obligations`, d),
      updateObligationProgress: (id, d) => API.put(`${base}/revenue-recognition/obligations/${id}/progress`, d),
      generateRevenueSchedule: (id) => API.post(`${base}/revenue-recognition/contracts/${id}/generate-schedule`),
      recognizeRevenue: (id) => API.post(`${base}/revenue-recognition/schedules/${id}/recognize`),
      getDeferredRevenue: () => API.get(`${base}/revenue-recognition/deferred-revenue`),
      // Time & Billing
      getTimeEntries: (q = '') => API.get(`${base}/time-billing/entries${q}`),
      getTimeEntry: (id) => API.get(`${base}/time-billing/entries/${id}`),
      createTimeEntry: (d) => API.post(`${base}/time-billing/entries`, d),
      updateTimeEntry: (id, d) => API.put(`${base}/time-billing/entries/${id}`, d),
      submitTimeEntry: (id) => API.post(`${base}/time-billing/entries/${id}/submit`),
      approveTimeEntry: (id) => API.post(`${base}/time-billing/entries/${id}/approve`),
      getBillingRates: () => API.get(`${base}/time-billing/rates`),
      createBillingRate: (d) => API.post(`${base}/time-billing/rates`, d),
      generateTimeInvoice: (d) => API.post(`${base}/time-billing/generate-invoice`, d),
      getTimeSummary: (q = '') => API.get(`${base}/time-billing/summary${q}`),
      getUtilization: (q = '') => API.get(`${base}/time-billing/utilization${q}`),
      // AI
      aiCategorize: (entityType, entityId) => API.post(`${base}/ai/categorize/${entityType}/${entityId}`),
      aiBatchCategorize: (d) => API.post(`${base}/ai/categorize/batch`, d),
      aiAcceptCategory: (id) => API.post(`${base}/ai/categorize/${id}/accept`),
      getAiRules: () => API.get(`${base}/ai/rules`),
      createAiRule: (d) => API.post(`${base}/ai/rules`, d),
      aiLearnRules: () => API.post(`${base}/ai/rules/learn`),
      aiDetectAnomalies: (d) => API.post(`${base}/ai/anomalies/detect`, d),
      getAnomalies: (q = '') => API.get(`${base}/ai/anomalies${q}`),
      resolveAnomaly: (id) => API.post(`${base}/ai/anomalies/${id}/resolve`),
      createForecast: (d) => API.post(`${base}/ai/forecast`, d),
      getForecasts: () => API.get(`${base}/ai/forecasts`),
      // OCR
      ocrScan: (fileId) => API.post(`${base}/ocr/scan/${fileId}`),
      getOcrResult: (id) => API.get(`${base}/ocr/${id}`),
      getOcrResults: () => API.get(`${base}/ocr`),
      ocrCreateDocument: (id) => API.post(`${base}/ocr/${id}/create-document`),
      // Freelance
      inviteFreelancer: (d) => API.post(`${base}/freelance/invite`, d),
      getFreelanceInvitations: () => API.get(`${base}/freelance/invitations`),
      revokeInvitation: (id) => API.del(`${base}/freelance/invitations/${id}`),
      getFreelancers: () => API.get(`${base}/freelance`),
      getFreelanceAccess: (id) => API.get(`${base}/freelance/${id}`),
      updateFreelanceAccess: (id, d) => API.put(`${base}/freelance/${id}`, d),
      deactivateFreelance: (id) => API.post(`${base}/freelance/${id}/deactivate`),
      createFreelanceTask: (d) => API.post(`${base}/freelance/tasks`, d),
      getFreelanceTasks: (q = '') => API.get(`${base}/freelance/tasks${q}`),
      getFreelanceTask: (id) => API.get(`${base}/freelance/tasks/${id}`),
      updateFreelanceTask: (id, d) => API.put(`${base}/freelance/tasks/${id}`, d),
      submitFreelanceTask: (id) => API.post(`${base}/freelance/tasks/${id}/submit`),
      reviewFreelanceTask: (id, d) => API.post(`${base}/freelance/tasks/${id}/review`, d),
      // Webhooks
      getWebhooks: () => API.get(`${base}/webhooks`),
      createWebhook: (d) => API.post(`${base}/webhooks`, d),
      updateWebhook: (id, d) => API.put(`${base}/webhooks/${id}`, d),
      deleteWebhook: (id) => API.del(`${base}/webhooks/${id}`),
      testWebhook: (id) => API.post(`${base}/webhooks/${id}/test`),
      getWebhookDeliveries: (id) => API.get(`${base}/webhooks/${id}/deliveries`),
      retryDelivery: (id) => API.post(`${base}/webhooks/deliveries/${id}/retry`),
      getWebhookEventTypes: () => API.get(`${base}/webhooks/event-types`),
      // API Keys
      getApiKeys: () => API.get(`${base}/settings/api-keys`),
      createApiKey: (d) => API.post(`${base}/settings/api-keys`, d),
      revokeApiKey: (id) => API.del(`${base}/settings/api-keys/${id}`),
      // Attachments
      uploadAttachment: (entityType, entityId, formData) => API.upload(`${base}/attachments/${entityType}/${entityId}`, formData),
      getAttachments: (entityType, entityId) => API.get(`${base}/attachments/${entityType}/${entityId}`),
      deleteAttachment: (id) => API.del(`${base}/attachments/${id}`),
      // Approval
      getApprovalRules: () => API.get(`${base}/approval/rules`),
      createApprovalRule: (d) => API.post(`${base}/approval/rules`, d),
      updateApprovalRule: (id, d) => API.put(`${base}/approval/rules/${id}`, d),
      deleteApprovalRule: (id) => API.del(`${base}/approval/rules/${id}`),
      submitForApproval: (entityType, entityId) => API.post(`${base}/approval/submit?entityType=${entityType}&entityId=${entityId}`),
      getApprovalRequest: (id) => API.get(`${base}/approval/requests/${id}`),
      getPendingApprovals: () => API.get(`${base}/approval/pending`),
      submitAction: (id, d) => API.post(`${base}/approval/requests/${id}/action`, d),
      // Settings
      getSettings: () => API.get(`${base}/settings`),
      updateSettings: (d) => API.put(`${base}/settings`, d),
      getNumberSeries: () => API.get(`${base}/settings/number-series`),
      // Audit
      getAuditLogs: (q = '') => API.get(`${base}/audit/logs${q}`),
      getAuditSummary: (q = '') => API.get(`${base}/audit/summary${q}`),
      getEntityHistory: (entityType, entityId) => API.get(`${base}/audit/entity/${entityType}/${entityId}`),
      getUserActivity: (userId, limit = 100) => API.get(`${base}/audit/users/${userId}/activity?limit=${limit}`),
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

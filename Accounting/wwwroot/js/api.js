// ===== API Client =====
const API = {
  baseUrl: '',
  token: null,

  init() {
    this.token = localStorage.getItem('token');
  },

  async request(method, url, data = null, isFormData = false) {
    // Re-read token from localStorage on each request (handles token refresh by other tabs)
    this.token = localStorage.getItem('token');
    const headers = {};
    if (this.token) headers['Authorization'] = `Bearer ${this.token}`;
    if (!isFormData) headers['Content-Type'] = 'application/json';

    const options = { method, headers };
    if (data && !isFormData) options.body = JSON.stringify(data);
    if (data && isFormData) options.body = data;

    try {
      const res = await fetch(`${this.baseUrl}${url}`, options);
      if (res.status === 401) {
        // Auth API calls (login/register/sso) should throw error, not redirect
        const isAuthCall = url.startsWith('/api/auth/');
        if (!isAuthCall) {
          localStorage.removeItem('token');
          localStorage.removeItem('user');
          window.location.href = '/login.html';
          return;
        }
        const json = await res.json();
        throw new Error(json.message || 'อีเมลหรือรหัสผ่านไม่ถูกต้อง');
      }
      if (res.status === 403) {
        throw new Error('คุณไม่มีสิทธิ์เข้าถึงข้อมูลนี้');
      }
      if (res.status === 429) {
        // Rate limited - silently skip, don't show error to user
        console.warn('Rate limited:', url);
        return { success: false, data: null, message: 'กรุณารอสักครู่' };
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
  ssoLogin(provider, idToken, companyName) { return this.post('/api/auth/sso', { provider, idToken, companyName }); },
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
      seedAccounts: (businessType, industryType) => {
        const params = [];
        if (businessType) params.push('businessType=' + businessType);
        if (industryType) params.push('industryType=' + industryType);
        return API.post(`${base}/accounting/accounts/seed${params.length ? '?' + params.join('&') : ''}`);
      },
      previewAccountTemplate: (businessType, industryType) => API.get(`${base}/accounting/accounts/template-preview?businessType=${businessType || 'JuristicPerson'}&industryType=${industryType || 'General'}`),
      getBusinessTypes: () => API.get(`${base}/accounting/business-types`),
      getJournals: (q = '') => API.get(`${base}/accounting/journals${q}`),
      getJournal: (id) => API.get(`${base}/accounting/journals/${id}`),
      createJournal: (d) => API.post(`${base}/accounting/journals`, d),
      postJournal: (id) => API.post(`${base}/accounting/journals/${id}/post`),
      voidJournal: (id) => API.post(`${base}/accounting/journals/${id}/void`),
      generalLedger: (q = '') => API.get(`${base}/accounting/reports/general-ledger${q}`),
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
      getProduct: (id) => API.get(`${base}/product/${id}`),
      createProduct: (d) => API.post(`${base}/product`, d),
      updateProduct: (id, d) => API.put(`${base}/product/${id}`, d),
      deleteProduct: (id) => API.del(`${base}/product/${id}`),
      adjustStock: (d) => API.post(`${base}/product/stock/adjust`, d),
      getStockMovements: (productId) => API.get(`${base}/product/${productId}/stock/movements`),
      getLowStock: () => API.get(`${base}/product/stock/low`),
      // Unit Conversions
      getUnitConversions: (productId) => API.get(`${base}/product/${productId}/unit-conversions`),
      createUnitConversion: (productId, d) => API.post(`${base}/product/${productId}/unit-conversions`, d),
      deleteUnitConversion: (id) => API.del(`${base}/product/unit-conversions/${id}`),
      convertUnit: (d) => API.post(`${base}/product/unit-conversions/convert`, d),
      // Product Categories
      getProductCategories: () => API.get(`${base}/product/categories`),
      createProductCategory: (d) => API.post(`${base}/product/categories`, d),
      deleteProductCategory: (id) => API.del(`${base}/product/categories/${id}`),
      // Stock Count
      getStockCounts: () => API.get(`${base}/product/stock-counts`),
      getStockCount: (id) => API.get(`${base}/product/stock-counts/${id}`),
      createStockCount: (d) => API.post(`${base}/product/stock-counts`, d),
      updateStockCountLines: (id, d) => API.put(`${base}/product/stock-counts/${id}/lines`, d),
      applyStockCount: (id) => API.post(`${base}/product/stock-counts/${id}/apply`),
      // Inventory Valuation
      getInventoryValuation: () => API.get(`${base}/product/inventory/valuation`),
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
      writeOffAsset: (id, d) => API.post(`${base}/fixedasset/${id}/writeoff`, d),
      adjustAssetLife: (id, d) => API.put(`${base}/fixedasset/${id}/adjust-life`, d),
      getDepreciations: (id) => API.get(`${base}/fixedasset/${id}/depreciations`),
      runDepreciation: (d) => API.post(`${base}/fixedasset/depreciate`, d),
      getAssetCategories: () => API.get(`${base}/fixedasset/categories`),
      getAssetRegisterReport: () => API.get(`${base}/fixedasset/report/register`),
      getDepreciationSchedule: (id) => API.get(`${base}/fixedasset/${id}/report/depreciation-schedule`),
      importAssets: (d) => API.post(`${base}/fixedasset/import`, d),
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
      importData: (d) => API.post(`${base}/import-export/import`, d),
      validateImport: (d) => API.post(`${base}/import-export/validate`, d),
      getImportTemplate: (entity) => `${base}/import-export/templates/${entity}/download`,
      exportData: (d) => API.post(`${base}/import-export/export`, d),
      getExportableEntities: () => API.get(`${base}/import-export/exportable-entities`),
      getImportableEntities: () => API.get(`${base}/import-export/importable-entities`),
      smartImportUpload: (d) => API.post(`${base}/import-export/smart-import/upload`, d),
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
      aiDetectAnomalies: (fromDate, toDate) => API.post(`${base}/ai/anomalies/detect?${fromDate ? 'fromDate='+fromDate+'&' : ''}${toDate ? 'toDate='+toDate : ''}`),
      getAnomalies: (q = '') => API.get(`${base}/ai/anomalies${q}`),
      resolveAnomaly: (id, notes = '') => API.post(`${base}/ai/anomalies/${id}/resolve?notes=${encodeURIComponent(notes)}`),
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
      uploadLogo: (formData) => API.upload(`${base}/settings/logo`, formData),
      deleteLogo: () => API.del(`${base}/settings/logo`),
      getNumberSeries: () => API.get(`${base}/settings/number-series`),
      // Aging
      getAgingReceivables: (q = '') => API.get(`${base}/aging/receivables${q}`),
      getAgingPayables: (q = '') => API.get(`${base}/aging/payables${q}`),
      getContactReceivables: (contactId, q = '') => API.get(`${base}/aging/contacts/${contactId}/receivables${q}`),
      getContactPayables: (contactId, q = '') => API.get(`${base}/aging/contacts/${contactId}/payables${q}`),
      // AR/AP Analysis
      getArApOverview: () => API.get(`${base}/arap-analysis/overview`),
      getArApContactDetail: (contactId, type = 'ar') => API.get(`${base}/arap-analysis/contacts/${contactId}?type=${type}`),
      getBadDebtAnalysis: () => API.get(`${base}/arap-analysis/bad-debt`),
      // Audit
      getAuditLogs: (q = '') => API.get(`${base}/audit/logs${q}`),
      getAuditSummary: (q = '') => API.get(`${base}/audit/summary${q}`),
      getEntityHistory: (entityType, entityId) => API.get(`${base}/audit/entity/${entityType}/${entityId}`),
      getUserActivity: (userId, limit = 100) => API.get(`${base}/audit/users/${userId}/activity?limit=${limit}`),
      // Notifications
      getNotifications: () => API.get('/api/notification'),
      getNotificationCount: () => API.get('/api/notification/count'),
      markRead: (ids) => API.post('/api/notification/mark-read', { notificationIds: ids }),
      markAllRead: () => API.post('/api/notification/mark-all-read'),
      // Dimension allocations
      addDimensionAllocation: (lineId, d) => API.post(`${base}/dimensions/journal-lines/${lineId}/allocations`, d),
      getDimensionAllocations: (lineId) => API.get(`${base}/dimensions/journal-lines/${lineId}/allocations`),
      // Product stock by warehouse
      getProductStock: (productId) => API.get(`${base}/warehouses/products/${productId}/stock`),
      // Fixed Asset Revaluation
      revalueAsset: (id, d) => API.post(`${base}/fixedasset/${id}/revalue`, d),
      // FPA - Financial Planning & Analysis
      getScenarios: () => API.get(`${base}/fpa/scenarios`),
      getScenario: (id) => API.get(`${base}/fpa/scenarios/${id}`),
      createScenario: (d) => API.post(`${base}/fpa/scenarios`, d),
      updateScenario: (id, d) => API.put(`${base}/fpa/scenarios/${id}`, d),
      deleteScenario: (id) => API.del(`${base}/fpa/scenarios/${id}`),
      addAssumption: (id, d) => API.post(`${base}/fpa/scenarios/${id}/assumptions`, d),
      removeAssumption: (id) => API.del(`${base}/fpa/assumptions/${id}`),
      calculateScenario: (id) => API.post(`${base}/fpa/scenarios/${id}/calculate`),
      compareScenarios: (d) => API.post(`${base}/fpa/scenarios/compare`, d),
      createKpi: (d) => API.post(`${base}/fpa/kpis`, d),
      getKpis: () => API.get(`${base}/fpa/kpis`),
      getKpiHistory: (id) => API.get(`${base}/fpa/kpis/${id}/history`),
      calculateKpiSnapshots: (y, m) => API.post(`${base}/fpa/kpis/snapshots/${y}/${m}`),
      getFinancialRatios: (q = '') => API.get(`${base}/fpa/ratios${q}`),
      getBreakEven: (fy) => API.get(`${base}/fpa/break-even/${fy}`),
      // Custom Report Builder
      getCustomReports: () => API.get(`${base}/reports`),
      getCustomReport: (id) => API.get(`${base}/reports/${id}`),
      createCustomReport: (d) => API.post(`${base}/reports`, d),
      updateCustomReport: (id, d) => API.put(`${base}/reports/${id}`, d),
      deleteCustomReport: (id) => API.del(`${base}/reports/${id}`),
      duplicateCustomReport: (id) => API.post(`${base}/reports/${id}/duplicate`),
      executeCustomReport: (id, d) => API.post(`${base}/reports/${id}/execute`, d),
      getReportDataSources: () => API.get(`${base}/reports/data-sources`),
      getReportColumns: (ds) => API.get(`${base}/reports/data-sources/${ds}/columns`),
      // Compliance
      getComplianceFilings: (q = '') => API.get(`${base}/compliance/filings${q}`),
      createComplianceFiling: (d) => API.post(`${base}/compliance/filings`, d),
      submitComplianceFiling: (id) => API.post(`${base}/compliance/filings/${id}/submit`),
      // e-Tax extended
      etaxSignAndSubmit: (id) => API.post(`${base}/etax/${id}/sign-and-submit`),
      etaxQuickSubmit: (d) => API.post(`${base}/etax/quick-submit`, d),
      // POS - Terminal
      getPosTerminals: () => API.get(`${base}/pos/terminals`),
      createPosTerminal: (d) => API.post(`${base}/pos/terminals`, d),
      updatePosTerminal: (id, d) => API.put(`${base}/pos/terminals/${id}`, d),
      // POS - Session
      getPosSessions: (q = '') => API.get(`${base}/pos/sessions${q}`),
      getPosSession: (id) => API.get(`${base}/pos/sessions/${id}`),
      openPosSession: (d) => API.post(`${base}/pos/sessions/open`, d),
      closePosSession: (id, d) => API.post(`${base}/pos/sessions/${id}/close`, d),
      // POS - Order
      getPosOrders: (q = '') => API.get(`${base}/pos/orders${q}`),
      getPosOrder: (id) => API.get(`${base}/pos/orders/${id}`),
      createPosOrder: (d) => API.post(`${base}/pos/orders`, d),
      updatePosOrder: (id, d) => API.put(`${base}/pos/orders/${id}`, d),
      updatePosOrderStatus: (id, d) => API.post(`${base}/pos/orders/${id}/status`, d),
      voidPosOrder: (id) => API.post(`${base}/pos/orders/${id}/void`),
      completePosOrder: (id) => API.post(`${base}/pos/orders/${id}/complete`),
      // POS - Order Items
      addPosOrderItem: (orderId, d) => API.post(`${base}/pos/orders/${orderId}/items`, d),
      removePosOrderItem: (orderId, itemId) => API.del(`${base}/pos/orders/${orderId}/items/${itemId}`),
      updatePosItemStatus: (orderId, itemId, d) => API.post(`${base}/pos/orders/${orderId}/items/${itemId}/status`, d),
      // POS - Payment
      addPosPayment: (d) => API.post(`${base}/pos/payments`, d),
      // POS - Service Package
      getPosPackages: (q = '') => API.get(`${base}/pos/packages${q}`),
      getPosPackage: (id) => API.get(`${base}/pos/packages/${id}`),
      createPosPackage: (d) => API.post(`${base}/pos/packages`, d),
      updatePosPackage: (id, d) => API.put(`${base}/pos/packages/${id}`, d),
      deletePosPackage: (id) => API.del(`${base}/pos/packages/${id}`),
      // POS - Service Component
      addPosComponent: (pkgId, d) => API.post(`${base}/pos/packages/${pkgId}/components`, d),
      updatePosComponent: (pkgId, compId, d) => API.put(`${base}/pos/packages/${pkgId}/components/${compId}`, d),
      removePosComponent: (pkgId, compId) => API.del(`${base}/pos/packages/${pkgId}/components/${compId}`),
      // POS - Service Activity
      updatePosActivity: (actId, d) => API.put(`${base}/pos/activities/${actId}`, d),
      // POS - Modifier Group
      getPosModifierGroups: (q = '') => API.get(`${base}/pos/modifier-groups${q}`),
      createPosModifierGroup: (d) => API.post(`${base}/pos/modifier-groups`, d),
      updatePosModifierGroup: (id, d) => API.put(`${base}/pos/modifier-groups/${id}`, d),
      deletePosModifierGroup: (id) => API.del(`${base}/pos/modifier-groups/${id}`),
      // POS - Modifier Option
      addPosModifierOption: (groupId, d) => API.post(`${base}/pos/modifier-groups/${groupId}/options`, d),
      updatePosModifierOption: (groupId, optId, d) => API.put(`${base}/pos/modifier-groups/${groupId}/options/${optId}`, d),
      removePosModifierOption: (groupId, optId) => API.del(`${base}/pos/modifier-groups/${groupId}/options/${optId}`),
      // POS - Reports
      getPosDailySummary: (q = '') => API.get(`${base}/pos/daily-summary${q}`),
      getPosCommissionSummary: (q) => API.get(`${base}/pos/commission-summary${q}`),
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

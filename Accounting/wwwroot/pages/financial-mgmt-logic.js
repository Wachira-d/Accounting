const Page = {
  accounts: [],
  _refundId: null,
  _payAcrId: null,
  _sellId: null,

  async init() {
    if (!Layout.init('financial-mgmt')) return;
    API.init();
    this.api = Layout.api();
    if (!this.api) return;
    await this.loadAccounts();
    await this.loadPrepaid();
  },

  switchTab(tab) {
    document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
    document.querySelectorAll('.tab-content').forEach(t => { t.classList.remove('active'); t.style.display = 'none'; });
    document.querySelector(`[data-tab="${tab}"]`).classList.add('active');
    const el = document.getElementById(`tab-${tab}`);
    el.classList.add('active'); el.style.display = '';
    const loaders = {
      prepaid: 'loadPrepaid', deposits: 'loadDeposits', accrued: 'loadAccrued',
      baddebt: 'loadBadDebt', obsolescence: 'loadObsolescence', cit: 'loadCIT',
      dividend: 'loadDividend', capital: 'loadCapital', investment: 'loadInvestment'
    };
    if (loaders[tab]) this[loaders[tab]]();
  },

  showModal(id) { document.getElementById(id).style.display = 'flex'; },
  hideModal(id) { document.getElementById(id).style.display = 'none'; },
  fmt(n) { return (n || 0).toLocaleString('th-TH', { minimumFractionDigits: 2, maximumFractionDigits: 2 }); },
  fmtDate(d) { return d ? new Date(d).toLocaleDateString('th-TH') : '-'; },
  statusBadge(s) {
    const map = { Active: 'info', Draft: 'warning', Accrued: 'warning', Posted: 'success', Filed: 'success',
      FullyAmortized: 'success', FullyRefunded: 'success', Paid: 'success', Completed: 'success',
      Distributed: 'success', Sold: 'success', PartiallyPaid: 'info', PartiallyRefunded: 'info' };
    return `<span class="badge badge-${map[s] || 'info'}">${s}</span>`;
  },

  async loadAccounts() {
    try {
      const res = await this.api.getAccounts();
      if (res?.success) this.accounts = res.data || [];
    } catch (e) { console.error('loadAccounts:', e); }
  },

  fillAccountSelect(selId, prefix) {
    const sel = document.getElementById(selId);
    sel.innerHTML = '<option value="">เลือกบัญชี</option>';
    this.accounts.filter(a => a.accountCode.startsWith(prefix) && a.level >= 4)
      .forEach(a => { const o = document.createElement('option'); o.value = a.id; o.textContent = `${a.accountCode} ${a.accountName}`; sel.appendChild(o); });
  },

  // ===== 1. PREPAID =====
  async loadPrepaid() {
    try {
      const res = await this.api.getPrepaids();
      if (!res?.success) return;
      document.getElementById('prepaidBody').innerHTML = (res.data || []).length === 0
        ? '<tr><td colspan="9" class="text-center">ไม่มีข้อมูล</td></tr>'
        : res.data.map(p => `<tr>
          <td>${p.referenceNo}</td><td>${p.description}</td><td>${this.fmtDate(p.startDate)}</td><td>${this.fmtDate(p.endDate)}</td>
          <td>${p.totalPeriods}</td><td class="text-right">${this.fmt(p.totalAmount)}</td>
          <td class="text-right">${this.fmt(p.amortizedAmount)}</td><td class="text-right">${this.fmt(p.remainingAmount)}</td>
          <td>${this.statusBadge(p.status)}</td></tr>`).join('');
    } catch (e) { console.error('loadPrepaid:', e); }
  },
  async submitPrepaid() {
    try {
      const res = await this.api.createPrepaid({
        description: document.getElementById('ppDesc').value,
        startDate: document.getElementById('ppStart').value,
        endDate: document.getElementById('ppEnd').value,
        totalAmount: parseFloat(document.getElementById('ppAmount').value),
        totalPeriods: parseInt(document.getElementById('ppPeriods').value),
        prepaidAccountId: document.getElementById('ppPrepaidAcc').value,
        expenseAccountId: document.getElementById('ppExpenseAcc').value
      });
      if (res?.success) { Layout.toast('บันทึกสำเร็จ', 'success'); this.hideModal('prepaidModal'); this.loadPrepaid(); }
      else Layout.toast(res?.message || 'error', 'error');
    } catch (e) { Layout.toast(e.message, 'error'); }
  },
  async processAmortization() {
    const date = new Date().toISOString().slice(0, 10);
    try {
      const res = await this.api.processAmortization(date);
      if (res?.success) { Layout.toast(res.message || 'ตัดจ่ายสำเร็จ', 'success'); this.loadPrepaid(); }
    } catch (e) { Layout.toast(e.message, 'error'); }
  },

  // ===== 2. DEPOSITS =====
  async loadDeposits() {
    try {
      const res = await this.api.getDeposits();
      if (!res?.success) return;
      document.getElementById('depositBody').innerHTML = (res.data || []).length === 0
        ? '<tr><td colspan="10" class="text-center">ไม่มีข้อมูล</td></tr>'
        : res.data.map(d => `<tr>
          <td>${d.referenceNo}</td><td>${d.description}</td><td>${d.depositType}</td>
          <td>${d.direction === 'Paid' ? 'จ่าย' : 'รับ'}</td><td>${this.fmtDate(d.transactionDate)}</td>
          <td class="text-right">${this.fmt(d.amount)}</td><td class="text-right">${this.fmt(d.refundedAmount)}</td>
          <td class="text-right">${this.fmt(d.remainingAmount)}</td><td>${this.statusBadge(d.status)}</td>
          <td>${d.remainingAmount > 0 ? `<button class="btn btn-sm" onclick="Page.showRefund('${d.id}',${d.remainingAmount})">คืน</button>` : ''}</td>
        </tr>`).join('');
    } catch (e) { console.error('loadDeposits:', e); }
  },
  async submitDeposit() {
    try {
      const res = await this.api.createDeposit({
        description: document.getElementById('depDesc').value,
        direction: document.getElementById('depDir').value,
        depositType: document.getElementById('depType').value,
        amount: parseFloat(document.getElementById('depAmount').value),
        transactionDate: document.getElementById('depDate').value,
        contactName: document.getElementById('depContact').value || null,
        depositAccountId: document.getElementById('depAcc').value,
        cashAccountId: document.getElementById('depCashAcc').value || null
      });
      if (res?.success) { Layout.toast('บันทึกสำเร็จ', 'success'); this.hideModal('depositModal'); this.loadDeposits(); }
      else Layout.toast(res?.message || 'error', 'error');
    } catch (e) { Layout.toast(e.message, 'error'); }
  },
  showRefund(id, max) { this._refundId = id; document.getElementById('refundAmt').value = max; this.showModal('refundModal'); },
  async submitRefund() {
    try {
      const res = await this.api.refundDeposit(this._refundId, {
        amount: parseFloat(document.getElementById('refundAmt').value),
        notes: document.getElementById('refundNotes').value || null
      });
      if (res?.success) { Layout.toast('คืนเงินสำเร็จ', 'success'); this.hideModal('refundModal'); this.loadDeposits(); }
    } catch (e) { Layout.toast(e.message, 'error'); }
  },

  // ===== 3. ACCRUED =====
  async loadAccrued() {
    try {
      const res = await this.api.getAccrueds();
      if (!res?.success) return;
      document.getElementById('accruedBody').innerHTML = (res.data || []).length === 0
        ? '<tr><td colspan="9" class="text-center">ไม่มีข้อมูล</td></tr>'
        : res.data.map(a => `<tr>
          <td>${a.referenceNo}</td><td>${a.description}</td><td>${a.expenseType}</td><td>${this.fmtDate(a.accrualDate)}</td>
          <td class="text-right">${this.fmt(a.amount)}</td><td class="text-right">${this.fmt(a.paidAmount)}</td>
          <td class="text-right">${this.fmt(a.remainingAmount)}</td><td>${this.statusBadge(a.status)}</td>
          <td>${a.remainingAmount > 0 ? `<button class="btn btn-sm" onclick="Page.showPayAccrued('${a.id}',${a.remainingAmount})">จ่าย</button>` : ''}</td>
        </tr>`).join('');
    } catch (e) { console.error('loadAccrued:', e); }
  },
  async submitAccrued() {
    try {
      const res = await this.api.createAccrued({
        description: document.getElementById('acrDesc').value,
        expenseType: document.getElementById('acrType').value,
        amount: parseFloat(document.getElementById('acrAmount').value),
        expenseAccountId: document.getElementById('acrExpAcc').value,
        accruedAccountId: document.getElementById('acrAccAcc').value
      });
      if (res?.success) { Layout.toast('บันทึกสำเร็จ', 'success'); this.hideModal('accruedModal'); this.loadAccrued(); }
      else Layout.toast(res?.message || 'error', 'error');
    } catch (e) { Layout.toast(e.message, 'error'); }
  },
  showPayAccrued(id, max) {
    this._payAcrId = id;
    document.getElementById('payAcrAmt').value = max;
    this.fillAccountSelect('payAcrCashAcc', '111');
    this.showModal('payAccruedModal');
  },
  async submitPayAccrued() {
    try {
      const cashAccId = document.getElementById('payAcrCashAcc').value || null;
      const res = await this.api.payAccrued(this._payAcrId, {
        amount: parseFloat(document.getElementById('payAcrAmt').value),
        cashAccountId: cashAccId
      });
      if (res?.success) { Layout.toast('จ่ายสำเร็จ', 'success'); this.hideModal('payAccruedModal'); this.loadAccrued(); }
    } catch (e) { Layout.toast(e.message, 'error'); }
  },

  // ===== 4. BAD DEBT =====
  async loadBadDebt() {
    try {
      const res = await this.api.getBadDebts();
      if (!res?.success) return;
      document.getElementById('baddebtBody').innerHTML = (res.data || []).length === 0
        ? '<tr><td colspan="8" class="text-center">ไม่มีข้อมูล</td></tr>'
        : res.data.map(b => `<tr>
          <td>${b.referenceNo}</td><td>${this.fmtDate(b.allowanceDate)}</td><td>${b.method}</td>
          <td class="text-right">${this.fmt(b.totalReceivable)}</td><td class="text-right">${this.fmt(b.allowanceAmount)}</td>
          <td class="text-right">${this.fmt(b.adjustmentAmount)}</td><td>${this.statusBadge(b.status)}</td>
          <td>${b.status === 'Draft' ? `<button class="btn btn-sm btn-primary" onclick="Page.postBadDebt('${b.id}')">บันทึกบัญชี</button>` : ''}</td>
        </tr>`).join('');
    } catch (e) { console.error('loadBadDebt:', e); }
  },
  async createBadDebt() {
    try {
      const res = await this.api.createBadDebt({ method: 'Aging' });
      if (res?.success) { Layout.toast('คำนวณสำเร็จ', 'success'); this.loadBadDebt(); }
    } catch (e) { Layout.toast(e.message, 'error'); }
  },
  async postBadDebt(id) {
    try {
      const res = await this.api.postBadDebt(id);
      if (res?.success) { Layout.toast('บันทึกบัญชีสำเร็จ', 'success'); this.loadBadDebt(); }
    } catch (e) { Layout.toast(e.message, 'error'); }
  },

  // ===== 5. OBSOLESCENCE =====
  async loadObsolescence() {
    try {
      const res = await this.api.getObsolescences();
      if (!res?.success) return;
      document.getElementById('obsolescenceBody').innerHTML = (res.data || []).length === 0
        ? '<tr><td colspan="7" class="text-center">ไม่มีข้อมูล</td></tr>'
        : res.data.map(i => `<tr>
          <td>${i.referenceNo}</td><td>${this.fmtDate(i.allowanceDate)}</td>
          <td class="text-right">${this.fmt(i.totalInventoryValue)}</td><td class="text-right">${this.fmt(i.allowanceAmount)}</td>
          <td class="text-right">${this.fmt(i.adjustmentAmount)}</td><td>${this.statusBadge(i.status)}</td>
          <td>${i.status === 'Draft' ? `<button class="btn btn-sm btn-primary" onclick="Page.postObsolescence('${i.id}')">บันทึกบัญชี</button>` : ''}</td>
        </tr>`).join('');
    } catch (e) { console.error('loadObsolescence:', e); }
  },
  async createObsolescence() {
    try {
      const res = await this.api.createObsolescence({ method: 'Aging' });
      if (res?.success) { Layout.toast('คำนวณสำเร็จ', 'success'); this.loadObsolescence(); }
    } catch (e) { Layout.toast(e.message, 'error'); }
  },
  async postObsolescence(id) {
    try {
      const res = await this.api.postObsolescence(id);
      if (res?.success) { Layout.toast('บันทึกบัญชีสำเร็จ', 'success'); this.loadObsolescence(); }
    } catch (e) { Layout.toast(e.message, 'error'); }
  },

  // ===== 6. CIT =====
  async loadCIT() {
    try {
      const res = await this.api.getCITs();
      if (!res?.success) return;
      document.getElementById('citBody').innerHTML = (res.data || []).length === 0
        ? '<tr><td colspan="9" class="text-center">ไม่มีข้อมูล</td></tr>'
        : res.data.map(c => `<tr>
          <td>${c.taxYear}</td><td>${c.taxPeriod === 'Annual' ? 'ประจำปี' : 'ครึ่งปี'}</td>
          <td class="text-right">${this.fmt(c.totalRevenue)}</td><td class="text-right">${this.fmt(c.totalExpenses)}</td>
          <td class="text-right">${this.fmt(c.taxableProfit)}</td><td class="text-right">${this.fmt(c.taxAmount)}</td>
          <td class="text-right">${this.fmt(c.netTaxPayable)}</td><td>${this.statusBadge(c.status)}</td>
          <td>${c.status === 'Draft' ? `<button class="btn btn-sm btn-primary" onclick="Page.postCIT('${c.id}')">บันทึก</button>` : ''}</td>
        </tr>`).join('');
    } catch (e) { console.error('loadCIT:', e); }
  },
  async submitCIT() {
    try {
      const res = await this.api.calculateCIT({
        taxYear: document.getElementById('citYear').value,
        taxPeriod: document.getElementById('citPeriod').value,
        addBackItems: parseFloat(document.getElementById('citAddBack').value) || 0,
        deductionItems: parseFloat(document.getElementById('citDeduct').value) || 0,
        withholdingTaxCredit: parseFloat(document.getElementById('citWht').value) || 0,
        prepaidTaxCredit: parseFloat(document.getElementById('citPrepaid').value) || 0
      });
      if (res?.success) { Layout.toast('คำนวณสำเร็จ', 'success'); this.hideModal('citModal'); this.loadCIT(); }
    } catch (e) { Layout.toast(e.message, 'error'); }
  },
  async postCIT(id) {
    try {
      const res = await this.api.postCIT(id);
      if (res?.success) { Layout.toast('บันทึกภาษีสำเร็จ', 'success'); this.loadCIT(); }
    } catch (e) { Layout.toast(e.message, 'error'); }
  },

  // ===== 7. DIVIDEND =====
  async loadDividend() {
    try {
      const res = await this.api.getAppropriations();
      if (!res?.success) return;
      document.getElementById('dividendBody').innerHTML = (res.data || []).length === 0
        ? '<tr><td colspan="8" class="text-center">ไม่มีข้อมูล</td></tr>'
        : res.data.map(p => `<tr>
          <td>${p.referenceNo}</td><td>${p.fiscalYear}</td>
          <td class="text-right">${this.fmt(p.netProfit)}</td><td class="text-right">${this.fmt(p.legalReserve)}</td>
          <td class="text-right">${this.fmt(p.dividendAmount)}</td><td class="text-right">${this.fmt(p.retainedAmount)}</td>
          <td>${this.statusBadge(p.status)}</td>
          <td>${p.status === 'Draft' ? `<button class="btn btn-sm btn-primary" onclick="Page.approveDividend('${p.id}')">อนุมัติ</button>` : ''}</td>
        </tr>`).join('');
    } catch (e) { console.error('loadDividend:', e); }
  },
  async submitDividend() {
    try {
      const res = await this.api.createAppropriation({
        fiscalYear: document.getElementById('divYear').value,
        legalReserve: parseFloat(document.getElementById('divReserve').value) || 0,
        dividendAmount: parseFloat(document.getElementById('divAmount').value) || 0,
        dividendPerShare: parseFloat(document.getElementById('divPerShare').value) || 0
      });
      if (res?.success) { Layout.toast('บันทึกสำเร็จ', 'success'); this.hideModal('dividendModal'); this.loadDividend(); }
    } catch (e) { Layout.toast(e.message, 'error'); }
  },
  async approveDividend(id) {
    try {
      const res = await this.api.approveAppropriation(id);
      if (res?.success) { Layout.toast('อนุมัติสำเร็จ', 'success'); this.loadDividend(); }
    } catch (e) { Layout.toast(e.message, 'error'); }
  },

  // ===== 8. CAPITAL =====
  async loadCapital() {
    try {
      const res = await this.api.getCapitals();
      if (!res?.success) return;
      document.getElementById('capitalBody').innerHTML = (res.data || []).length === 0
        ? '<tr><td colspan="9" class="text-center">ไม่มีข้อมูล</td></tr>'
        : res.data.map(c => `<tr>
          <td>${c.referenceNo}</td><td>${this.fmtDate(c.transactionDate)}</td>
          <td>${c.transactionType === 'Increase' ? 'เพิ่มทุน' : 'ลดทุน'}</td>
          <td class="text-right">${this.fmt(c.shareQuantity)}</td><td class="text-right">${this.fmt(c.parValue)}</td>
          <td class="text-right">${this.fmt(c.paidAmount)}</td><td class="text-right">${this.fmt(c.sharePremium)}</td>
          <td>${this.statusBadge(c.status)}</td>
          <td>${c.status === 'Draft' ? `<button class="btn btn-sm btn-primary" onclick="Page.completeCapital('${c.id}')">ดำเนินการ</button>` : ''}</td>
        </tr>`).join('');
    } catch (e) { console.error('loadCapital:', e); }
  },
  async submitCapital() {
    try {
      const res = await this.api.createCapital({
        transactionType: document.getElementById('capType').value,
        transactionDate: document.getElementById('capDate').value || null,
        shareQuantity: parseFloat(document.getElementById('capShares').value),
        parValue: parseFloat(document.getElementById('capPar').value),
        paidAmount: parseFloat(document.getElementById('capPaid').value),
        boardResolutionRef: document.getElementById('capBoard').value || null
      });
      if (res?.success) { Layout.toast('บันทึกสำเร็จ', 'success'); this.hideModal('capitalModal'); this.loadCapital(); }
    } catch (e) { Layout.toast(e.message, 'error'); }
  },
  async completeCapital(id) {
    try {
      const res = await this.api.completeCapital(id);
      if (res?.success) { Layout.toast('ดำเนินการสำเร็จ', 'success'); this.loadCapital(); }
    } catch (e) { Layout.toast(e.message, 'error'); }
  },

  // ===== 9. INVESTMENT =====
  async loadInvestment() {
    try {
      const res = await this.api.getInvestments();
      if (!res?.success) return;
      document.getElementById('investmentBody').innerHTML = (res.data || []).length === 0
        ? '<tr><td colspan="9" class="text-center">ไม่มีข้อมูล</td></tr>'
        : res.data.map(i => `<tr>
          <td>${i.referenceNo}</td><td>${i.investmentType}</td><td>${i.description}</td>
          <td>${this.fmtDate(i.purchaseDate)}</td><td>${this.fmtDate(i.maturityDate)}</td>
          <td class="text-right">${this.fmt(i.purchaseCost)}</td><td class="text-right">${i.interestRate}%</td>
          <td>${this.statusBadge(i.status)}</td>
          <td>${i.status === 'Active' ? `<button class="btn btn-sm" onclick="Page.showSell('${i.id}',${i.purchaseCost})">ขาย</button>` : ''}</td>
        </tr>`).join('');
    } catch (e) { console.error('loadInvestment:', e); }
  },
  async submitInvestment() {
    try {
      const res = await this.api.createInvestment({
        investmentType: document.getElementById('invType').value,
        description: document.getElementById('invDesc').value,
        purchaseDate: document.getElementById('invDate').value,
        maturityDate: document.getElementById('invMaturity').value || null,
        purchaseCost: parseFloat(document.getElementById('invCost').value),
        interestRate: parseFloat(document.getElementById('invRate').value) || 0,
        investmentAccountId: document.getElementById('invAcc').value,
        institutionName: document.getElementById('invInst').value || null
      });
      if (res?.success) { Layout.toast('บันทึกสำเร็จ', 'success'); this.hideModal('investmentModal'); this.loadInvestment(); }
    } catch (e) { Layout.toast(e.message, 'error'); }
  },
  updateDepositAccounts() {
    const dir = document.getElementById('depDir').value;
    this.fillAccountSelect('depAcc', dir === 'Paid' ? '11' : '216');
  },

  showSell(id, cost) { this._sellId = id; document.getElementById('sellAmt').value = cost; this.showModal('sellModal'); },
  async submitSell() {
    try {
      const res = await this.api.sellInvestment(this._sellId, { saleProceeds: parseFloat(document.getElementById('sellAmt').value) });
      if (res?.success) { Layout.toast('ขายสำเร็จ', 'success'); this.hideModal('sellModal'); this.loadInvestment(); }
    } catch (e) { Layout.toast(e.message, 'error'); }
  }
};

// Populate account dropdowns when modals open
const origShowModal = Page.showModal.bind(Page);
Page.showModal = function(id) {
  origShowModal(id);
  if (id === 'prepaidModal') { Page.fillAccountSelect('ppPrepaidAcc', '117'); Page.fillAccountSelect('ppExpenseAcc', '54'); }
  if (id === 'depositModal') {
    Page.fillAccountSelect('depCashAcc', '111');
    const dir = document.getElementById('depDir').value;
    Page.fillAccountSelect('depAcc', dir === 'Paid' ? '11' : '216');
    document.getElementById('depDate').value = new Date().toISOString().slice(0, 10);
  }
  if (id === 'accruedModal') { Page.fillAccountSelect('acrExpAcc', '54'); Page.fillAccountSelect('acrAccAcc', '215'); }
  if (id === 'capitalModal') { document.getElementById('capDate').value = new Date().toISOString().slice(0, 10); }
  if (id === 'investmentModal') { Page.fillAccountSelect('invAcc', '112'); document.getElementById('invDate').value = new Date().toISOString().slice(0, 10); }
};

document.addEventListener('DOMContentLoaded', () => Page.init());

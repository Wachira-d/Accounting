const Page = {
  signatures: [],
  pendingApprovals: [],
  currentApprovalId: null,
  ctx: null,
  drawing: false,
  hasDrawn: false,
  fileData: null,

  async init() {
    this.setupCanvas();
    await this.loadSignatures();
    await this.loadPending();
  },

  // ===== Tab Management =====
  setTab(tab, el) {
    document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
    el.classList.add('active');
    document.getElementById('tabSignatures').style.display = tab === 'signatures' ? '' : 'none';
    document.getElementById('tabPending').style.display = tab === 'pending' ? '' : 'none';
    document.getElementById('tabHistory').style.display = tab === 'history' ? '' : 'none';
    if (tab === 'pending') this.loadPending();
  },

  // ===== Signatures =====
  async loadSignatures() {
    try {
      const res = await API.getSignatures();
      this.signatures = res.data || [];
      this.renderSignatures();
    } catch (e) {
      document.getElementById('signatureCards').innerHTML = '<div class="text-center text-danger" style="grid-column:1/-1;padding:40px">โหลดข้อมูลไม่สำเร็จ</div>';
    }
  },

  renderSignatures() {
    const container = document.getElementById('signatureCards');
    if (!this.signatures.length) {
      container.innerHTML = '<div class="text-center text-gray-400" style="padding:40px;grid-column:1/-1">ยังไม่มีลายเซ็น คลิก "อัพโหลดลายเซ็น" เพื่อเพิ่ม</div>';
      return;
    }
    container.innerHTML = this.signatures.map(s => `
      <div class="card" style="padding:16px;text-align:center;position:relative${s.isDefault ? ';border:2px solid var(--primary)' : ''}">
        ${s.isDefault ? '<span class="badge badge-info" style="position:absolute;top:8px;right:8px">หลัก</span>' : ''}
        <div style="margin:12px 0;background:#fff;border-radius:8px;padding:8px;min-height:80px;display:flex;align-items:center;justify-content:center">
          <img src="data:image/${s.signatureFormat.toLowerCase()};base64,${s.signatureData}" style="max-width:100%;max-height:80px" alt="ลายเซ็น">
        </div>
        <div style="font-weight:500;margin-bottom:4px">${s.label || 'ลายเซ็น'}</div>
        <div style="font-size:12px;color:var(--text-secondary);margin-bottom:12px">${new Date(s.createdAt).toLocaleDateString('th-TH')}</div>
        <div style="display:flex;gap:8px;justify-content:center">
          ${!s.isDefault ? `<button class="btn btn-sm btn-secondary" onclick="Page.setDefault('${s.id}')">ตั้งเป็นหลัก</button>` : ''}
          <button class="btn btn-sm btn-danger" onclick="Page.deleteSignature('${s.id}')">ลบ</button>
        </div>
      </div>
    `).join('');
  },

  async setDefault(id) {
    try {
      await API.setDefaultSignature(id);
      await this.loadSignatures();
    } catch (e) { alert('เกิดข้อผิดพลาด: ' + (e.message || e)); }
  },

  async deleteSignature(id) {
    if (!confirm('ต้องการลบลายเซ็นนี้?')) return;
    try {
      await API.deleteSignature(id);
      await this.loadSignatures();
    } catch (e) { alert('เกิดข้อผิดพลาด: ' + (e.message || e)); }
  },

  // ===== Canvas Drawing =====
  /** คืน ctx ที่ใช้ได้เสมอ — เตรียมให้ตอนแรกไม่สำเร็จก็ลองใหม่ตอนใช้จริง
   *
   *  เดิม setupCanvas() ทำงานครั้งเดียวตอน init ถ้าตอนนั้นหา canvas ไม่เจอ
   *  (เช่น modal ถูกลบ/ยังไม่ถูก render) this.ctx จะค้างเป็น null ตลอดอายุ
   *  หน้า แล้วทุกฟังก์ชันที่เรียก this.ctx.* พังหมด — ผู้ใช้เห็นแค่ error
   *  ดิบ ๆ ไม่รู้ว่าต้องทำอะไร */
  ensureCtx() {
    if (this.ctx) return this.ctx;
    this.setupCanvas();
    return this.ctx;
  },

  setupCanvas() {
    const canvas = document.getElementById('sigCanvas');
    if (!canvas) return;
    this.ctx = canvas.getContext('2d');
    if (!this.ctx) return;
    this.ctx.strokeStyle = '#000';
    this.ctx.lineWidth = 2;
    this.ctx.lineCap = 'round';

    canvas.addEventListener('mousedown', e => { this.drawing = true; this.ctx.beginPath(); this.ctx.moveTo(e.offsetX, e.offsetY); });
    canvas.addEventListener('mousemove', e => { if (!this.drawing) return; this.hasDrawn = true; this.ctx.lineTo(e.offsetX, e.offsetY); this.ctx.stroke(); });
    canvas.addEventListener('mouseup', () => { this.drawing = false; });
    canvas.addEventListener('mouseleave', () => { this.drawing = false; });

    // Touch support
    canvas.addEventListener('touchstart', e => { e.preventDefault(); const r = canvas.getBoundingClientRect(); const t = e.touches[0]; this.drawing = true; this.ctx.beginPath(); this.ctx.moveTo(t.clientX - r.left, t.clientY - r.top); });
    canvas.addEventListener('touchmove', e => { e.preventDefault(); if (!this.drawing) return; this.hasDrawn = true; const r = canvas.getBoundingClientRect(); const t = e.touches[0]; this.ctx.lineTo(t.clientX - r.left, t.clientY - r.top); this.ctx.stroke(); });
    canvas.addEventListener('touchend', () => { this.drawing = false; });
  },

  clearCanvas() {
    const canvas = document.getElementById('sigCanvas');
    const ctx = this.ensureCtx();
    // ไม่มี canvas ก็แค่รีเซ็ตสถานะ — ห้าม throw เพราะ showUploadModal()
    // เรียกตัวนี้ก่อนเปิด modal ถ้าพังตรงนี้ modal จะไม่มีวันเปิดเลย
    if (canvas && ctx) ctx.clearRect(0, 0, canvas.width, canvas.height);
    this.hasDrawn = false;
    this.fileData = null;
  },

  loadFile(e) {
    const file = e.target.files[0];
    if (!file) return;
    const reader = new FileReader();
    reader.onload = (ev) => {
      const img = new Image();
      img.onload = () => {
        const canvas = document.getElementById('sigCanvas');
        const ctx = this.ensureCtx();
        // แสดงตัวอย่างบน canvas ไม่ได้ก็ไม่เป็นไร — fileData ถูกเก็บไว้แล้ว
        // ด้านล่าง การอัปโหลดจึงยังทำงานได้ (อย่าให้พรีวิวพังการอัปโหลด)
        if (!canvas || !ctx) return;
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        const scale = Math.min(canvas.width / img.width, canvas.height / img.height);
        const w = img.width * scale, h = img.height * scale;
        ctx.drawImage(img, (canvas.width - w) / 2, (canvas.height - h) / 2, w, h);
        this.hasDrawn = true;
      };
      img.src = ev.target.result;
      this.fileData = ev.target.result.split(',')[1];
    };
    reader.readAsDataURL(file);
  },

  // ===== Upload Modal =====
  showUploadModal() {
    this.clearCanvas();
    document.getElementById('sigLabel').value = '';
    document.getElementById('sigDefault').checked = true;
    document.getElementById('sigFile').value = '';
    document.getElementById('uploadModal').style.display = 'flex';
  },

  async submitSignature() {
    let data;
    if (this.fileData) {
      data = this.fileData;
    } else if (this.hasDrawn) {
      const canvas = document.getElementById('sigCanvas');
      if (!canvas) { alert('ไม่พบพื้นที่วาดลายเซ็น — กรุณารีเฟรชหน้าแล้วลองใหม่'); return; }
      data = canvas.toDataURL('image/png').split(',')[1];
    } else {
      alert('กรุณาวาดหรืออัพโหลดลายเซ็น');
      return;
    }
    try {
      await API.uploadSignature({
        signatureData: data,
        signatureFormat: 'PNG',
        label: document.getElementById('sigLabel').value || null,
        isDefault: document.getElementById('sigDefault').checked
      });
      this.closeModal('uploadModal');
      await this.loadSignatures();
    } catch (e) { alert('เกิดข้อผิดพลาด: ' + (e.message || e)); }
  },

  // ===== Pending Approvals =====
  async loadPending() {
    const cid = localStorage.getItem('selectedCompanyId');
    if (!cid) { document.getElementById('pendingBody').innerHTML = '<tr><td colspan="6" class="text-center text-gray-400" style="padding:40px">กรุณาเลือกบริษัทก่อน</td></tr>'; return; }
    try {
      const res = await API.c(cid).getPendingDocApprovals();
      this.pendingApprovals = res.data || [];
      this.renderPending();
    } catch (e) {
      document.getElementById('pendingBody').innerHTML = '<tr><td colspan="6" class="text-center text-danger" style="padding:40px">โหลดข้อมูลไม่สำเร็จ</td></tr>';
    }
  },

  renderPending() {
    const tbody = document.getElementById('pendingBody');
    if (!this.pendingApprovals.length) {
      tbody.innerHTML = '<tr><td colspan="6" class="text-center text-gray-400" style="padding:40px">ไม่มีรายการรออนุมัติ</td></tr>';
      return;
    }
    const statusMap = { 0: 'รออนุมัติ', 1: 'อนุมัติแล้ว', 2: 'ปฏิเสธ' };
    const statusClass = { 0: 'badge-warning', 1: 'badge-success', 2: 'badge-danger' };
    tbody.innerHTML = this.pendingApprovals.map(a => `
      <tr>
        <td>${a.documentNumber}</td>
        <td>${a.approvalType}</td>
        <td>${a.approverRole}</td>
        <td>${a.stepOrder}</td>
        <td><span class="badge ${statusClass[a.status] || 'badge-secondary'}">${statusMap[a.status] || a.status}</span></td>
        <td>
          <button class="btn btn-sm btn-success" onclick="Page.showApproveModal('${a.id}','${a.documentNumber}')">อนุมัติ</button>
          <button class="btn btn-sm btn-danger" onclick="Page.showRejectModal('${a.id}','${a.documentNumber}')">ปฏิเสธ</button>
        </td>
      </tr>
    `).join('');
  },

  // ===== Approve =====
  async showApproveModal(approvalId, docNumber) {
    this.currentApprovalId = approvalId;
    document.getElementById('approveInfo').textContent = `อนุมัติเอกสาร: ${docNumber}`;
    document.getElementById('approveComments').value = '';
    // Populate signature options
    const select = document.getElementById('approveSignatureId');
    select.innerHTML = '<option value="">ใช้ลายเซ็นหลัก</option>';
    if (!this.signatures.length) await this.loadSignatures();
    this.signatures.forEach(s => {
      select.innerHTML += `<option value="${s.id}">${s.label || 'ลายเซ็น'} ${s.isDefault ? '(หลัก)' : ''}</option>`;
    });
    document.getElementById('approveModal').style.display = 'flex';
  },

  async submitApprove() {
    const cid = localStorage.getItem('selectedCompanyId');
    if (!cid) return;
    const sigId = document.getElementById('approveSignatureId').value || null;
    const comments = document.getElementById('approveComments').value || null;
    try {
      await API.c(cid).approveDoc(this.currentApprovalId, { signatureId: sigId, comments });
      this.closeModal('approveModal');
      await this.loadPending();
      alert('อนุมัติสำเร็จ');
    } catch (e) { alert('เกิดข้อผิดพลาด: ' + (e.message || e)); }
  },

  // ===== Reject =====
  showRejectModal(approvalId, docNumber) {
    this.currentApprovalId = approvalId;
    document.getElementById('rejectInfo').textContent = `ปฏิเสธเอกสาร: ${docNumber}`;
    document.getElementById('rejectComments').value = '';
    document.getElementById('rejectModal').style.display = 'flex';
  },

  async submitReject() {
    const cid = localStorage.getItem('selectedCompanyId');
    if (!cid) return;
    const comments = document.getElementById('rejectComments').value;
    if (!comments) { alert('กรุณาระบุเหตุผล'); return; }
    try {
      await API.c(cid).rejectDoc(this.currentApprovalId, { comments });
      this.closeModal('rejectModal');
      await this.loadPending();
      alert('ปฏิเสธสำเร็จ');
    } catch (e) { alert('เกิดข้อผิดพลาด: ' + (e.message || e)); }
  },

  // ===== History (placeholder - uses same pending data for now) =====
  filterHistory() {
    // Future: load approval history with search
  },

  // ===== Helpers =====
  closeModal(id) { document.getElementById(id).style.display = 'none'; },
};

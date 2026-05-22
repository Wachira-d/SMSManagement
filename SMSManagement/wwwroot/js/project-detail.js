// Project detail page — handles every tab.
// Each tab lazy-loads when its `shown.bs.tab` event fires the first time.

const projectId = document.getElementById('projectId').value;
const api_proj  = `/api/projects/${projectId}`;
let project;        // last-known project payload
const loaded = {};  // tab-name -> bool, so we only fetch once per tab

const ACTION_LABEL = { 0: 'Archive', 1: 'Delete', 2: 'Leave' };
const DUP_LABEL    = { 0: 'Skip', 1: 'Fail', 2: 'Reprocess' };
const SMS_STATUS_COLOR = {
    Queued: 'secondary', Sending: 'info', Sent: 'primary',
    Delivered: 'success', Failed: 'danger', Rejected: 'danger',
    Expired: 'dark'
};
// SmsStatus enum order — the API serialises enums as numbers, so normalise
// a numeric status back to its name before the UI compares against names.
const SMS_STATUS_NAME = ['Queued', 'Sending', 'Sent', 'Delivered', 'Failed', 'Rejected', 'Expired'];
function smsStatusName(s) {
    return typeof s === 'number' ? (SMS_STATUS_NAME[s] ?? String(s)) : s;
}

// ---------- bootstrap on load ----------
(async function init() {
    try {
        project = await api.get(api_proj);
        document.getElementById('projName').textContent = project.name;
        document.getElementById('projCode').textContent = project.code;
        document.getElementById('bcName').textContent   = project.name;
        fillSettings(project);
        if (project.members.some(m => m.accessLevel === 'Owner')) {
            document.getElementById('dangerZone').style.display = '';
        }
        // Don't block — run alongside; failure just hides the card.
        renderOnboardingChecklist().catch(() => {});
    } catch (e) {
        toast('Failed to load project: ' + e.message, 'danger');
    }
})();

// Surface the prerequisites the project still needs before ingestion will
// actually produce messages. Each row is a quick-jump to the relevant tab.
// When every step is green the whole card hides itself.
async function renderOnboardingChecklist() {
    const checks = await Promise.allSettled([
        api.get(`${api_proj}/column-mappings`),
        api.get(`${api_proj}/workflows`),
        api.get(`${api_proj}/ingestion-sources`),
        api.get(`${api_proj}/sms-providers`)
    ]);
    const [mappings, workflows, sources, smsProviders] = checks.map(r =>
        r.status === 'fulfilled' ? r.value : []);

    const hasMapping  = Array.isArray(mappings) && mappings.length > 0;
    const hasPhone    = hasMapping && mappings.some(m => m.canonicalField === 'phone');
    const hasActiveWf = Array.isArray(workflows) && workflows.some(w => w.active);
    const hasSource   = Array.isArray(sources) && sources.length > 0;
    const hasProvider = Array.isArray(smsProviders) && smsProviders.some(p => p.hasOverride);

    const items = [
        { ok: hasMapping,  label: 'Map columns from your source file',
          hint: 'Pick which columns are phone/name/url/... in the Mappings tab.',
          tab: '#tab-mappings' },
        { ok: hasPhone,    label: 'At least one mapping targets <code>phone</code>',
          hint: 'Without phone, no SMS can be dispatched.', tab: '#tab-mappings' },
        { ok: hasActiveWf, label: 'Have an active workflow definition',
          hint: 'Use the Quick-start template — pick a scenario and fill 3 fields.',
          tab: '#tab-workflows' },
        { ok: hasSource,   label: 'Configure at least one source (SFTP / manual / etc.)',
          hint: 'Optional if you only ever upload manually; recommended otherwise.',
          tab: '#tab-sources', soft: true },
        { ok: hasProvider, label: 'Per-project SMS provider credentials (optional)',
          hint: 'Falls back to the platform-wide defaults when not set.',
          tab: '#tab-sms', soft: true }
    ];

    const missingHard = items.filter(i => !i.ok && !i.soft);
    const missingSoft = items.filter(i => !i.ok && i.soft);
    if (missingHard.length === 0 && missingSoft.length === 0) {
        // Everything green — hide the card (it may have been shown earlier
        // and this is a re-render after the operator finished setup).
        document.getElementById('onboardingChecklist').style.display = 'none';
        return;
    }

    const list = document.getElementById('onboardingList');
    list.innerHTML = items.map(i => {
        const icon = i.ok
            ? '<i class="bi bi-check-circle-fill text-success"></i>'
            : (i.soft ? '<i class="bi bi-dash-circle text-muted"></i>'
                      : '<i class="bi bi-circle text-warning"></i>');
        const click = i.ok ? '' :
            `onclick="bootstrap.Tab.getOrCreateInstance(document.querySelector('a[href=\\'${i.tab}\\']')).show();return false;"`;
        return `<li class="py-1">
            ${icon}
            <a href="#" ${click} class="text-decoration-none ms-1">${i.label}</a>
            ${i.ok ? '' : `<span class="text-muted small ms-2">— ${i.hint}</span>`}
        </li>`;
    }).join('');
    document.getElementById('onboardingChecklist').style.display = '';
}

// Lazy tab loaders
document.querySelectorAll('[data-bs-toggle="tab"]').forEach(el => {
    el.addEventListener('shown.bs.tab', (ev) => {
        const target = ev.target.getAttribute('href');
        switch (target) {
            case '#tab-pipeline':   loadPipeline(); loadPipelineRuns(); break;
            case '#tab-members':    if (!loaded.members)    { loaded.members    = true; loadMembers();    } break;
            case '#tab-mappings':   if (!loaded.mappings)   { loaded.mappings   = true; loadMapScope(); loadMappings(); loadRules(); } break;
            case '#tab-sources':    if (!loaded.sources)    { loaded.sources    = true; loadSources(); loadBatches(); } break;
            case '#tab-workflows':  if (!loaded.workflows)  { loaded.workflows  = true; loadWorkflows(); loadWfInstances(); loadWfCouponBatches(); } break;
            case '#tab-shortlinks': if (!loaded.shortlinks) { loaded.shortlinks = true; loadShortlinks(); } break;
            case '#tab-coupons':    if (!loaded.coupons)    { loaded.coupons    = true; loadCouponBrands(); loadCouponBatches(); initCouponDomain(); } break;
            case '#tab-sms':        if (!loaded.sms)        { loaded.sms        = true; loadSmsList(); loadProviderConfig('etracker'); loadProviderConfig('infobip'); } break;
        }
    });
});

// ==================== SETTINGS ====================
function fillSettings(p) {
    document.getElementById('setName').value     = p.name || '';
    document.getElementById('setProvider').value = p.defaultProvider || 'etracker';

    document.getElementById('featSms').checked       = p.features.sms;
    document.getElementById('featShortlink').checked = p.features.shortlink;
    document.getElementById('featWorkflow').checked  = p.features.workflow;
    document.getElementById('featIngestion').checked = p.features.ingestion;
    document.getElementById('featEmail').checked     = p.features.emailAlerts;
    document.getElementById('featCouponCheck').checked = p.features.couponCheck !== false;

    document.getElementById('slLength').value  = p.shortlink.shortlinkSlugLength ?? '';
    document.getElementById('slAlpha').value   = p.shortlink.shortlinkAlphabet ?? '';
    document.getElementById('slBaseUrl').value = p.shortlink.shortlinkBaseUrl ?? '';

    document.getElementById('notifEmails').value  = (p.notifications.recipients || []).join(', ');
    document.getElementById('notifPrefix').value  = (p.notifications.subjectPrefix || '');
    document.getElementById('notifSuccess').checked = p.notifications.triggers.onIngestSuccess;
    document.getElementById('notifPartial').checked = p.notifications.triggers.onIngestPartial;
    document.getElementById('notifFailure').checked = p.notifications.triggers.onIngestFailure;
    document.getElementById('notifSmsRound').checked = p.notifications.triggers.onSmsRoundComplete;
}

async function saveAndReload(body, successMsg) {
    try {
        await api.put(api_proj, body);
        toast(successMsg);
        project = await api.get(api_proj);
        fillSettings(project);
    } catch (e) { toast(e.message, 'danger'); }
}

document.getElementById('formInfo').addEventListener('submit', (ev) => {
    ev.preventDefault();
    saveAndReload({
        name: document.getElementById('setName').value.trim(),
        defaultProvider: document.getElementById('setProvider').value
    }, 'Info saved.');
});
document.getElementById('formFeatures').addEventListener('submit', (ev) => {
    ev.preventDefault();
    saveAndReload({
        smsEnabled:        document.getElementById('featSms').checked,
        shortlinkEnabled:  document.getElementById('featShortlink').checked,
        workflowEnabled:   document.getElementById('featWorkflow').checked,
        ingestionEnabled:  document.getElementById('featIngestion').checked,
        emailAlertsEnabled:document.getElementById('featEmail').checked,
        couponCheckEnabled:document.getElementById('featCouponCheck').checked
    }, 'Features saved.');
});
document.getElementById('formShortlink').addEventListener('submit', (ev) => {
    ev.preventDefault();
    const len = document.getElementById('slLength').value;
    const alpha = document.getElementById('slAlpha').value;
    saveAndReload({
        shortlinkSlugLength: len === '' ? null : parseInt(len, 10),
        shortlinkAlphabet:   alpha || '',
        shortlinkBaseUrl:    document.getElementById('slBaseUrl').value.trim()
    }, 'Shortlink config saved.');
});
document.getElementById('formNotif').addEventListener('submit', (ev) => {
    ev.preventDefault();
    saveAndReload({
        notificationEmails:       document.getElementById('notifEmails').value,
        notificationSubjectPrefix:document.getElementById('notifPrefix').value,
        notifyOnIngestSuccess:    document.getElementById('notifSuccess').checked,
        notifyOnIngestPartial:    document.getElementById('notifPartial').checked,
        notifyOnIngestFailure:    document.getElementById('notifFailure').checked,
        notifyOnSmsRoundComplete: document.getElementById('notifSmsRound').checked
    }, 'Notifications saved.');
});
// ============ ARCHIVE PROJECT ============
// Two-factor confirmation: the operator must answer a fresh math sum (a+b)
// generated when the modal opens. Defeats double-click and stale browser-
// session "yes I really clicked archive" mistakes. The sum is two small
// numbers (5–19 + 1–9) so it's solvable in <2s.
(function initArchiveFlow() {
    const btnOpen   = document.getElementById('btnArchive');
    const modal     = document.getElementById('archiveModal');
    const mathEl    = document.getElementById('archMath');
    const ansEl     = document.getElementById('archAnswer');
    const hintEl    = document.getElementById('archHint');
    const btnGo     = document.getElementById('btnArchiveGo');
    if (!btnOpen) return;

    let expected = 0;
    function refreshChallenge() {
        const a = 5 + Math.floor(Math.random() * 15);
        const b = 1 + Math.floor(Math.random() * 9);
        expected = a + b;
        mathEl.textContent = `${a} + ${b}`;
        ansEl.value = '';
        hintEl.textContent = '';
        btnGo.disabled = true;
    }

    btnOpen.addEventListener('click', () => {
        refreshChallenge();
        new bootstrap.Modal(modal).show();
        setTimeout(() => ansEl.focus(), 200);
    });
    modal.addEventListener('hidden.bs.modal', () => {
        // Clear state so the next open re-generates fresh numbers.
        ansEl.value = ''; hintEl.textContent = ''; btnGo.disabled = true;
    });
    ansEl.addEventListener('input', () => {
        const v = Number(ansEl.value);
        if (Number.isFinite(v) && v === expected) {
            btnGo.disabled = false;
            hintEl.textContent = '';
        } else {
            btnGo.disabled = true;
            hintEl.textContent = ansEl.value === '' ? '' : 'Not quite.';
        }
    });
    ansEl.addEventListener('keydown', (ev) => {
        if (ev.key === 'Enter' && !btnGo.disabled) btnGo.click();
    });
    btnGo.addEventListener('click', async () => {
        btnGo.disabled = true;
        try {
            await api.delete(api_proj);
            bootstrap.Modal.getInstance(modal).hide();
            toast('Project archived.');
            setTimeout(() => window.location = '/Projects/Index', 800);
        } catch (e) {
            toast(e.message, 'danger');
            btnGo.disabled = false;
        }
    });
})();

// Transfer ownership picker (debounced typeahead)
(function initTransferPicker() {
    const input   = document.getElementById('xferUser');
    const userId  = document.getElementById('xferUserId');
    const results = document.getElementById('xferResults');
    const btn     = document.getElementById('btnTransfer');
    if (!input) return;
    let timer;
    input.addEventListener('input', () => {
        clearTimeout(timer);
        userId.value = ''; btn.disabled = true; results.innerHTML = '';
        const q = input.value.trim();
        if (q.length < 2) return;
        timer = setTimeout(async () => {
            try {
                const rows = await api.get(`/api/users/search?q=${encodeURIComponent(q)}&take=5`);
                results.innerHTML = rows.map(u => `
                    <a href="#" class="badge bg-light text-dark me-1 mb-1 text-decoration-none"
                       data-id="${esc(u.id)}" data-label="${esc(u.email)}">
                      ${esc(u.email)}
                    </a>`).join('');
            } catch { /* silent */ }
        }, 250);
    });
    results.addEventListener('click', (ev) => {
        const a = ev.target.closest('a[data-id]'); if (!a) return;
        ev.preventDefault();
        userId.value = a.dataset.id;
        input.value = a.dataset.label;
        results.innerHTML = '';
        btn.disabled = false;
    });
    btn.addEventListener('click', async () => {
        const uid = userId.value;
        if (!uid) return;
        if (!confirm(`Transfer ownership to ${input.value}? You will become Admin.`)) return;
        try {
            await api.post(`${api_proj}/transfer-ownership`, uid);
            toast('Ownership transferred.');
            setTimeout(() => location.reload(), 800);
        } catch (e) { toast(e.message, 'danger'); }
    });
})();

// ==================== MEMBERS ====================
async function loadMembers() {
    try {
        const p = await api.get(api_proj);
        const body = document.getElementById('membersBody');
        if (!p.members.length) {
            body.innerHTML = '<tr><td colspan="5" class="text-muted text-center py-4"><i class="bi bi-people fs-3 d-block"></i>Just you so far. Add teammates by email below to share access.</td></tr>';
            return;
        }
        body.innerHTML = p.members.map(m => `
            <tr>
                <td><code class="small">${esc((m.id||'').slice(0,8))}…</code></td>
                <td>${esc(m.email||'')}</td>
                <td><span class="badge bg-secondary">${esc(m.accessLevel)}</span></td>
                <td>${fmtDate(m.grantedAt)}</td>
                <td>
                    ${m.accessLevel === 'Owner' ? '' :
                        `<button class="btn btn-link btn-sm text-danger p-0"
                                 onclick="revokeMember('${esc(m.id)}')">Revoke</button>`}
                </td>
            </tr>`).join('');
    } catch (e) { toast(e.message, 'danger'); }
}
window.revokeMember = async function (userId) {
    if (!confirm('Revoke this member?')) return;
    try {
        await api.delete(`${api_proj}/members/${userId}`);
        toast('Revoked.');
        loadMembers();
    } catch (e) { toast(e.message, 'danger'); }
};

// User picker — debounced typeahead against /api/users/search
(function initSharePicker() {
    const input   = document.getElementById('shareQuery');
    const results = document.getElementById('shareResults');
    const picked  = document.getElementById('sharePicked');
    const userId  = document.getElementById('shareUserId');
    const btn     = document.getElementById('btnShare');
    if (!input) return;
    let timer;
    input.addEventListener('input', () => {
        clearTimeout(timer);
        userId.value = ''; picked.textContent = ''; btn.disabled = true;
        const q = input.value.trim();
        if (q.length < 2) { results.innerHTML = ''; return; }
        timer = setTimeout(async () => {
            try {
                const rows = await api.get(`/api/users/search?q=${encodeURIComponent(q)}&take=8`);
                results.innerHTML = rows.length
                    ? rows.map(u => `
                        <a href="#" class="list-group-item list-group-item-action small"
                           data-id="${esc(u.id)}" data-label="${esc(u.email || u.displayName)}">
                          <strong>${esc(u.displayName || u.email)}</strong>
                          <span class="text-muted ms-2">${esc(u.email)}</span>
                        </a>`).join('')
                    : '<div class="list-group-item small text-muted">No matches.</div>';
            } catch (e) {
                results.innerHTML = `<div class="list-group-item small text-danger">${esc(e.message)}</div>`;
            }
        }, 250);
    });
    results.addEventListener('click', (ev) => {
        const a = ev.target.closest('a[data-id]'); if (!a) return;
        ev.preventDefault();
        userId.value = a.dataset.id;
        picked.textContent = '✓ Selected: ' + a.dataset.label;
        results.innerHTML = '';
        input.value = a.dataset.label;
        btn.disabled = false;
    });
    document.addEventListener('click', (ev) => {
        if (!ev.target.closest('#tab-members .position-relative')) results.innerHTML = '';
    });
})();

document.getElementById('formShare').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const uid = document.getElementById('shareUserId').value;
    if (!uid) { toast('Pick a user from the dropdown first', 'warning'); return; }
    try {
        await api.post(`${api_proj}/members`, {
            userId: uid,
            level:  document.getElementById('shareLevel').value
        });
        toast('Shared.');
        document.getElementById('shareQuery').value = '';
        document.getElementById('shareUserId').value = '';
        document.getElementById('sharePicked').textContent = '';
        document.getElementById('btnShare').disabled = true;
        loadMembers();
    } catch (e) { toast(e.message, 'danger'); }
});

// ==================== CANONICAL FIELD RULES ====================
// Per-project, per-canonical validation. A row that violates any rule is
// rejected; the rest of the file still completes. The built-in phone
// format check (8-15 digits) applies only when no custom rule for "phone"
// exists, so projects that opt in fully take over.
async function loadRules() {
    try {
        const rows = await api.get(`${api_proj}/canonical-rules${mapScopeQs()}`);
        const body = document.getElementById('rulesBody');
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="5" class="text-muted small">No custom rules yet. The built-in phone check still applies.</td></tr>';
            return;
        }
        body.innerHTML = rows.map(r => {
            const lengthCell = (r.minLength != null || r.maxLength != null)
                ? `${r.minLength ?? '0'}–${r.maxLength ?? '∞'}` : '—';
            const other = [
                r.startsWithAny    ? `starts: ${esc(r.startsWithAny)}`   : null,
                r.endsWithAny      ? `ends: ${esc(r.endsWithAny)}`       : null,
                r.pattern          ? `regex: <code class="small">${esc(r.pattern)}</code>` : null,
                r.allowedValues    ? `in: ${esc(r.allowedValues)}`       : null
            ].filter(Boolean).join('<br>');
            return `<tr>
                <td><a href="#" onclick="editRule(${esc(JSON.stringify(r))});return false">
                        <span class="badge bg-info">${esc(r.canonicalField)}</span></a></td>
                <td>${r.required ? '<i class="bi bi-check-circle-fill text-success"></i>' : ''}</td>
                <td class="small">${lengthCell}</td>
                <td class="small">${other || '—'}</td>
                <td><button class="btn btn-link btn-sm text-danger p-0"
                       onclick="deleteRule('${esc(r.canonicalField)}')">Delete</button></td>
            </tr>`;
        }).join('');
    } catch (e) { toast(e.message, 'danger'); }
}

window.editRule = function (r) {
    document.getElementById('ruleField').value     = r.canonicalField;
    document.getElementById('ruleRequired').checked = !!r.required;
    document.getElementById('ruleMinLen').value    = r.minLength  ?? '';
    document.getElementById('ruleMaxLen').value    = r.maxLength  ?? '';
    document.getElementById('ruleStarts').value    = r.startsWithAny ?? '';
    document.getElementById('ruleEnds').value      = r.endsWithAny   ?? '';
    document.getElementById('rulePattern').value   = r.pattern        ?? '';
    document.getElementById('ruleAllowed').value   = r.allowedValues  ?? '';
    document.getElementById('btnRuleDelete').disabled = false;
};

window.deleteRule = async function (canonical) {
    if (!confirm(`Delete rule for "${canonical}"?`)) return;
    try {
        await api.delete(`${api_proj}/canonical-rules/${encodeURIComponent(canonical)}${mapScopeQs()}`);
        toast('Rule removed.');
        loadRules();
    } catch (e) { toast(e.message, 'danger'); }
};

document.getElementById('formRule').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const numOrNull = (id) => {
        const v = document.getElementById(id).value;
        return v === '' ? null : Number(v);
    };
    const trimOrNull = (id) => {
        const v = document.getElementById(id).value.trim();
        return v ? v : null;
    };
    try {
        await api.put(`${api_proj}/canonical-rules`, {
            canonicalField: document.getElementById('ruleField').value,
            required:       document.getElementById('ruleRequired').checked,
            minLength:      numOrNull('ruleMinLen'),
            maxLength:      numOrNull('ruleMaxLen'),
            startsWithAny:  trimOrNull('ruleStarts'),
            endsWithAny:    trimOrNull('ruleEnds'),
            pattern:        trimOrNull('rulePattern'),
            allowedValues:  trimOrNull('ruleAllowed'),
            sourceId:       mapScopeId()
        });
        toast('Rule saved.');
        loadRules();
    } catch (e) { toast(e.message, 'danger'); }
});

document.getElementById('btnRuleDelete').addEventListener('click', () => {
    const f = document.getElementById('ruleField').value;
    if (f) window.deleteRule(f);
});

// Presets fill the form fields — operator still hits "Save rule" to commit.
document.querySelectorAll('#formRule [data-preset]').forEach(btn => {
    btn.addEventListener('click', () => {
        const p = btn.dataset.preset;
        const set = (id, v) => document.getElementById(id).value = v;
        set('ruleRequired', false); document.getElementById('ruleRequired').checked = false;
        ['ruleMinLen','ruleMaxLen','ruleStarts','ruleEnds','rulePattern','ruleAllowed']
            .forEach(id => set(id, ''));
        if (p === 'thai-mobile') {
            set('ruleField', 'phone');
            document.getElementById('ruleRequired').checked = true;
            set('ruleMinLen', '10'); set('ruleMaxLen', '10');
            set('ruleStarts', '08,06,09');
            set('rulePattern', '^0[689]\\d{8}$');
        } else if (p === 'email') {
            set('ruleField', 'email');
            set('rulePattern', '^[\\w.+-]+@[\\w.-]+\\.\\w{2,}$');
        } else if (p === 'https-url') {
            set('ruleField', 'url');
            set('rulePattern', '^https://.+');
        }
        // 'clear' leaves everything blank (set() above already wiped them).
    });
});

// ==================== CAMPAIGN PIPELINE OVERVIEW ====================
function pipelineStageHtml(s, n, isLast) {
    const badge = {
        ok:   '<span class="badge bg-success">พร้อม</span>',
        warn: '<span class="badge bg-warning text-dark">ยังไม่ตั้งค่า</span>',
        info: '<span class="badge bg-secondary">—</span>',
    }[s.state] || '';
    const card = `<div class="card" style="min-width:150px;flex:1 1 150px">
        <div class="card-body text-center p-2">
            <div style="font-size:1.7rem">${s.icon}</div>
            <div class="fw-semibold small">${n}. ${esc(s.name)}</div>
            <div class="small text-muted mb-1">${esc(s.desc)}</div>
            ${badge}
            <div class="mt-1"><a href="#" onclick="gotoTab('${s.tab}');return false;" class="small text-decoration-none">ตั้งค่า ›</a></div>
        </div>
    </div>`;
    const arrow = isLast ? ''
        : '<div class="d-flex align-items-center text-muted" style="font-size:1.3rem">→</div>';
    return card + arrow;
}

// Activate a real nav tab. The pipeline "ตั้งค่า" links live inside cards, not
// the .nav, so Bootstrap's data-bs-toggle="tab" no-ops there — drive the
// matching nav-tabs link instead.
window.gotoTab = function (target) {
    const nav = document.querySelector(`.nav-tabs a[data-bs-toggle="tab"][href="${target}"]`);
    if (nav) bootstrap.Tab.getOrCreateInstance(nav).show();
};

async function loadPipeline() {
    const el = document.getElementById('pipelineStages');
    el.innerHTML = '<span class="text-muted small">กำลังโหลด…</span>';
    try {
        const [sources, mappings, rules, workflows] = await Promise.all([
            api.get(`${api_proj}/ingestion-sources`).catch(() => []),
            api.get(`${api_proj}/column-mappings`).catch(() => []),
            api.get(`${api_proj}/canonical-rules`).catch(() => []),
            api.get(`${api_proj}/workflows`).catch(() => []),
        ]);
        const feat  = (project && project.features) || {};
        const notif = (project && project.notifications) || { recipients: [], triggers: {} };
        const activeWf = (workflows || []).find(w => w.active);

        const stages = [
            { icon: '📁', name: 'แหล่งไฟล์', tab: '#tab-sources',
              ...(sources.length
                  ? { state: 'ok',   desc: `${sources.length} แหล่ง (SFTP/อัตโนมัติ)` }
                  : { state: 'info', desc: 'อัปโหลดเอง (manual)' }) },
            { icon: '🧹', name: 'ทำความสะอาด', tab: '#tab-mappings',
              ...(mappings.length
                  ? { state: 'ok',   desc: `${mappings.length} คอลัมน์` }
                  : { state: 'warn', desc: 'ยังไม่ได้แมปคอลัมน์' }) },
            { icon: '✅', name: 'ตรวจสอบ', tab: '#tab-mappings',
              ...(rules.length
                  ? { state: 'ok',   desc: `${rules.length} กฎ` }
                  : { state: 'info', desc: 'ใช้กฎพื้นฐาน (เบอร์โทร)' }) },
            { icon: '🔗', name: 'ย่อลิงก์', tab: '#tab-settings',
              ...(feat.shortlink
                  ? { state: 'ok',   desc: 'เปิดใช้งาน' }
                  : { state: 'info', desc: 'ปิด' }) },
            { icon: '📤', name: 'ส่ง SMS', tab: '#tab-workflows',
              ...(activeWf
                  ? { state: 'ok',   desc: `workflow: ${activeWf.name}` }
                  : { state: 'warn', desc: 'ยังไม่มี workflow ใช้งาน' }) },
            { icon: '📧', name: 'แจ้งผลทางอีเมล', tab: '#tab-settings',
              ...(((notif.recipients || []).length && notif.triggers && notif.triggers.onSmsRoundComplete)
                  ? { state: 'ok',   desc: `ส่งสรุปไป ${notif.recipients.length} อีเมล` }
                  : { state: 'info', desc: 'ปิด / ยังไม่ตั้งอีเมล' }) },
        ];
        el.innerHTML = stages.map((s, i) =>
            pipelineStageHtml(s, i + 1, i === stages.length - 1)).join('');
    } catch (e) {
        el.innerHTML = `<span class="text-danger small">${esc(e.message)}</span>`;
    }
}
let pipelineRunsPage = 1;
async function loadPipelineRuns() {
    const el = document.getElementById('pipelineRuns');
    if (!el) return;
    el.innerHTML = '<span class="text-muted small">กำลังโหลด…</span>';
    try {
        const data = await api.get(
            `${api_proj}/ingestion-batches/runs?page=${pipelineRunsPage}&pageSize=20`);
        const runs = data.runs || [];
        if (!runs.length) {
            el.innerHTML = '<div class="text-muted small">ยังไม่มีการรัน — '
                + 'อัปโหลดไฟล์ หรือรอ SFTP poll รอบแรก</div>';
            return;
        }
        let html = `<div class="small mb-2">flow นี้ทำงานไปแล้ว `
            + `<strong>${data.total}</strong> รอบ</div>`;
        html += '<div class="table-responsive"><table class="table table-sm small align-middle">'
            + '<thead><tr><th>รอบ (เวลา)</th><th>แหล่ง</th><th>① นำเข้า</th>'
            + '<th>② Workflow</th><th>③ SMS</th><th>สถานะ</th></tr></thead><tbody>';
        runs.forEach(r => {
            const ing = r.ingestion, wf = r.workflow, sms = r.sms;
            html += `<tr>
                <td class="text-nowrap"><a href="#" onclick="showRunDetail('${esc(r.id)}');return false;"
                    class="text-decoration-none">${fmtDate(r.ingestedAt)}
                    <i class="bi bi-search small"></i></a></td>
                <td>${esc(r.sourceType)}</td>
                <td>${ing.totalRows} → <span class="text-success">รับ ${ing.acceptedRows}</span>`
                + (ing.rejectedRows ? ` <span class="text-danger">ตก ${ing.rejectedRows}</span>` : '')
                + `</td>
                <td>${wf.total} → <span class="text-success">จบ ${wf.completed}</span>`
                + (wf.failed ? ` <span class="text-danger">ล้ม ${wf.failed}</span>` : '')
                + (wf.active ? ` <span class="text-warning">ค้าง ${wf.active}</span>` : '')
                + `</td>
                <td>${sms.total} → <span class="text-success">ถึง ${sms.delivered}</span>`
                + (sms.sent ? ` <span class="text-primary">ส่ง ${sms.sent}</span>` : '')
                + (sms.failed ? ` <span class="text-danger">พลาด ${sms.failed}</span>` : '')
                + (sms.pending ? ` <span class="text-muted">รอ ${sms.pending}</span>` : '')
                + `</td>
                <td><a href="#" onclick="showRejections('${esc(r.id)}');return false;"
                       class="text-decoration-none">${esc(r.status)}
                    ${r.status === 'Failed' || r.status === 'Partial'
                        ? '<i class="bi bi-info-circle"></i>' : ''}</a></td>
            </tr>`;
        });
        html += '</tbody></table></div>';
        html += '<div id="pipelineRunsPager" class="d-flex align-items-center gap-2"></div>';
        el.innerHTML = html;
        renderPager(document.getElementById('pipelineRunsPager'), data,
            p => { pipelineRunsPage = p; loadPipelineRuns(); });
    } catch (e) {
        el.innerHTML = `<span class="text-danger small">${esc(e.message)}</span>`;
    }
}
document.getElementById('btnPipelineRefresh')?.addEventListener('click', () => {
    loadPipeline();
    loadPipelineRuns();
});

// ---- Run detail drill-down: ingestion → workflow timeline → SMS ----
const WF_STATE_COLOR = {
    Pending: 'secondary', Scheduled: 'info', Dispatching: 'info',
    AwaitingAction: 'warning', ReminderDue: 'warning',
    Completed: 'success', Failed: 'danger', Expired: 'dark',
};
function wfStateBadge(st) {
    return `<span class="badge bg-${WF_STATE_COLOR[st] || 'secondary'}">${esc(st)}</span>`;
}
function smsStatusBadge(st) {
    const n = smsStatusName(st);
    return `<span class="badge bg-${SMS_STATUS_COLOR[n] || 'secondary'}">${esc(n)}</span>`;
}

window.showRunDetail = async function (batchId) {
    const modalEl = document.getElementById('runDetailModal');
    const body    = document.getElementById('runDetailBody');
    body.innerHTML = '<div class="text-muted small">กำลังโหลด…</div>';
    new bootstrap.Modal(modalEl).show();
    try {
        const d = await api.get(`${api_proj}/ingestion-batches/${batchId}/detail`);
        const ing = d.ingestion || {};
        let html = `<div class="card mb-3"><div class="card-body p-2 small">
            <div class="d-flex justify-content-between flex-wrap">
              <div><strong>① นำเข้า</strong> — แหล่ง ${esc(d.sourceType)}
                ${d.sourceRef ? `<span class="text-muted">(${esc(d.sourceRef)})</span>` : ''}</div>
              <div class="text-muted">${fmtDate(d.ingestedAt)} · สถานะ ${esc(d.status)}</div>
            </div>
            <div class="mt-1">${ing.totalRows} แถว →
              <span class="text-success">รับ ${ing.acceptedRows}</span>`
            + (ing.rejectedRows
                ? ` · <a href="#" onclick="showRejections('${esc(d.id)}');return false;"
                       class="text-danger text-decoration-none">ตก ${ing.rejectedRows} (ดูเหตุผล)</a>`
                : '')
            + `</div></div></div>`;

        const insts = d.instances || [];
        if (!insts.length) {
            html += '<div class="text-muted small">รอบนี้ยังไม่มี workflow instance — '
                + 'อาจถูกปฏิเสธทั้งหมดในขั้นนำเข้า หรือยังไม่ได้ผูก workflow</div>';
            body.innerHTML = html;
            return;
        }
        html += `<div class="small mb-2"><strong>② Workflow + ③ SMS</strong> — `
            + `${insts.length} รายการ`
            + (d.truncated ? ' (แสดงบางส่วน)' : '') + '</div>';
        html += '<div class="accordion" id="runInstAcc">';
        insts.forEach((it, idx) => {
            const smsList = it.sms || [];
            const txList  = it.transitions || [];
            const smsBadges = smsList.map(s => smsStatusBadge(s.status)).join(' ');
            html += `<div class="accordion-item">
              <h2 class="accordion-header">
                <button class="accordion-button collapsed py-2 small" type="button"
                    data-bs-toggle="collapse" data-bs-target="#runInst${idx}">
                  <span class="me-2">${esc(it.maskedPhone || '(ไม่มีเบอร์)')}</span>
                  ${wfStateBadge(it.state)}
                  <span class="text-muted ms-2">ขั้น: ${esc(it.currentStep || '—')}</span>
                  <span class="ms-2">${smsBadges}</span>
                </button>
              </h2>
              <div id="runInst${idx}" class="accordion-collapse collapse"
                   data-bs-parent="#runInstAcc">
                <div class="accordion-body p-2">`;

            html += '<div class="small fw-semibold mb-1">ไทม์ไลน์ workflow</div>';
            if (txList.length) {
                html += '<table class="table table-sm small mb-3"><tbody>';
                txList.forEach(t => {
                    html += `<tr>
                      <td class="text-nowrap text-muted" style="width:140px">${fmtDate(t.at)}</td>
                      <td>${wfStateBadge(t.from)} → ${wfStateBadge(t.to)}</td>
                      <td><code class="small">${esc(t.trigger || '')}</code></td>
                      <td class="text-muted small">${t.dataJson ? esc(t.dataJson) : ''}</td>
                    </tr>`;
                });
                html += '</tbody></table>';
            } else {
                html += '<div class="text-muted small mb-3">ไม่มีการเปลี่ยนสถานะที่บันทึกไว้</div>';
            }

            html += '<div class="small fw-semibold mb-1">SMS ของรายการนี้</div>';
            if (smsList.length) {
                smsList.forEach(s => {
                    html += `<div class="border rounded p-2 mb-2 small">
                      <div class="d-flex justify-content-between">
                        <div>${smsStatusBadge(s.status)}
                          <span class="ms-1">${esc(s.maskedTo)}</span>
                          <span class="text-muted ms-1">· ${esc(s.provider || '')}</span>
                          ${s.senderId ? `<span class="text-muted">· จาก ${esc(s.senderId)}</span>` : ''}
                        </div>
                        <div class="text-muted">${fmtDate(s.createdAt)}</div>
                      </div>
                      <div class="mt-1"><span class="text-muted">ข้อความ:</span> ${esc(s.body || '')}</div>`;
                    const meta = [];
                    if (s.sentAt)      meta.push(`ส่ง ${fmtDate(s.sentAt)}`);
                    if (s.deliveredAt) meta.push(`ถึง ${fmtDate(s.deliveredAt)}`);
                    if (s.attempts)    meta.push(`พยายาม ${s.attempts} ครั้ง`);
                    if (s.providerMessageId) meta.push(`id: ${esc(s.providerMessageId)}`);
                    if (meta.length)
                        html += `<div class="text-muted mt-1">${meta.join(' · ')}</div>`;
                    if (s.errorCode)
                        html += `<div class="text-danger mt-1">ข้อผิดพลาด: ${esc(s.errorCode)}</div>`;
                    if (s.rawProviderResponse)
                        html += `<div class="mt-1"><span class="text-muted">คำตอบจากผู้ให้บริการ:</span>
                          <pre class="small bg-light p-1 mb-0 mt-1"
                               style="white-space:pre-wrap">${esc(s.rawProviderResponse)}</pre></div>`;
                    html += '</div>';
                });
            } else {
                html += '<div class="text-muted small">ยังไม่มี SMS</div>';
            }
            html += '</div></div></div>';
        });
        html += '</div>';
        body.innerHTML = html;
    } catch (e) {
        body.innerHTML = `<div class="text-danger">${esc(e.message)}</div>`;
    }
};

// ==================== EASY SETUP (friendly file config) ====================
const EASY_TYPES = [
    { v: 'phone',   t: '📱 เบอร์โทร' },
    { v: 'message', t: '💬 ข้อความ' },
    { v: 'name',    t: '👤 ชื่อ' },
    { v: 'email',   t: '✉️ อีเมล' },
    { v: 'url',     t: '🔗 ลิงก์' },
    { v: 'other',   t: '📋 ข้อมูลอื่น' },
    { v: 'ignore',  t: '🚫 ไม่ใช้คอลัมน์นี้' },
];

function easyCanonToType(c) {
    if (!c) return 'ignore';
    return c === 'custom' ? 'other' : c;
}

function easyToggles(type, st) {
    st = st || {};
    const cb = (key, label, def) =>
        `<label class="me-3 small text-nowrap"><input type="checkbox" class="easy-opt" data-opt="${key}"`
        + ` ${(st[key] ?? def) ? 'checked' : ''}> ${label}</label>`;
    if (type === 'phone')
        return cb('autoClean', 'จัดรูปแบบเบอร์อัตโนมัติ', true)
             + cb('validFormat', 'ต้องเป็นมือถือไทยที่ถูกต้อง', true)
             + cb('required', 'ห้ามว่าง', false);
    if (type === 'email')
        return cb('autoClean', 'ตัดช่องว่าง + ตัวพิมพ์เล็ก', true)
             + cb('validFormat', 'ต้องเป็นอีเมลที่ถูกต้อง', true)
             + cb('required', 'ห้ามว่าง', false);
    if (type === 'message')
        return cb('required', 'ห้ามว่าง', false)
             + `<label class="small text-nowrap">ความยาวสูงสุด`
             + ` <input type="number" min="1" class="easy-maxlen form-control form-control-sm d-inline-block"`
             + ` style="width:90px" value="${st.maxLength ?? ''}" placeholder="ไม่จำกัด"> ตัว</label>`;
    if (type === 'name' || type === 'url' || type === 'other')
        return cb('required', 'ห้ามว่าง', false);
    return '<span class="text-muted small">— คอลัมน์นี้จะไม่ถูกใช้ —</span>';
}

function easyRowHtml(col) {
    const opts = EASY_TYPES.map(o =>
        `<option value="${o.v}" ${o.v === col.type ? 'selected' : ''}>${o.t}</option>`).join('');
    return `<tr data-header="${esc(col.header)}">
        <td class="small fw-semibold">${esc(col.header)}</td>
        <td class="small text-muted text-truncate" style="max-width:140px">${esc(col.sample || '')}</td>
        <td><select class="form-select form-select-sm easy-type">${opts}</select></td>
        <td class="easy-toggle-cell">${easyToggles(col.type, col.state)}</td>
    </tr>`;
}

function renderEasyCols(data) {
    const sample0 = (data.sample && data.sample[0]) || {};
    const sugg = {};
    (data.suggestions || []).forEach(s => { sugg[s.header] = s; });
    const cols = (data.headers || []).map(h => {
        const s = sugg[h] || {};
        let type = 'ignore', state = {};
        if (s.savedPreset) {
            try {
                const p = JSON.parse(s.savedPreset);
                type = p.fieldType || 'ignore';
                state = { autoClean: p.autoClean, required: p.required,
                          validFormat: p.validFormat, maxLength: p.maxLength };
            } catch { /* fall back to heuristic */ }
        } else {
            type = easyCanonToType(s.alreadyMapped || s.suggested);
        }
        return { header: h, sample: sample0[h] || '', type, state };
    });
    document.getElementById('easyColsBody').innerHTML = cols.map(easyRowHtml).join('');
    document.getElementById('easyCols').classList.remove('d-none');
    document.getElementById('easyPreview').innerHTML = '';
    document.getElementById('easyStatus').textContent = '';
}

function easyGather() {
    return Array.from(document.querySelectorAll('#easyColsBody tr')).map(tr => {
        const item = {
            sourceColumn: tr.dataset.header,
            fieldType: tr.querySelector('.easy-type').value
        };
        tr.querySelectorAll('.easy-opt').forEach(cb => { item[cb.dataset.opt] = cb.checked; });
        const ml = tr.querySelector('.easy-maxlen');
        if (ml && ml.value) item.maxLength = parseInt(ml.value, 10);
        return item;
    });
}

function renderEasyPreview(data) {
    const out = document.getElementById('easyPreview');
    const rows = data.rows || [];
    if (!rows.length) { out.innerHTML = '<span class="text-muted small">ไม่มีข้อมูลตัวอย่าง</span>'; return; }
    const beforeKeys = [...new Set(rows.flatMap(r => Object.keys(r.before || {})))];
    const afterKeys  = [...new Set(rows.flatMap(r => Object.keys(r.after  || {})))];

    let html = `<div class="alert alert-info py-2 small mb-2">ตัวอย่าง ${rows.length} แถว — `
        + `<span class="text-success fw-semibold">ผ่าน ${data.accepted}</span>, `
        + `<span class="text-danger fw-semibold">ไม่ผ่าน ${data.rejected}</span></div>`;
    html += '<div class="table-responsive"><table class="table table-sm table-bordered small mb-0"><thead><tr>'
        + '<th>ผล</th>'
        + beforeKeys.map(k => `<th class="text-muted">${esc(k)} (เดิม)</th>`).join('')
        + afterKeys.map(k => `<th class="text-primary">${esc(k)} (หลังจัดรูปแบบ)</th>`).join('')
        + '<th>เหตุผลที่ไม่ผ่าน</th></tr></thead><tbody>';
    rows.forEach(r => {
        html += `<tr class="${r.ok ? 'table-success' : 'table-danger'}">`
            + `<td>${r.ok ? '✅' : '❌'}</td>`
            + beforeKeys.map(k => `<td class="text-muted">${esc((r.before || {})[k] || '')}</td>`).join('')
            + afterKeys.map(k => `<td><code>${esc((r.after || {})[k] || '')}</code></td>`).join('')
            + `<td class="text-danger">${esc((r.reasons || []).join(', '))}</td></tr>`;
    });
    html += '</tbody></table></div>';
    out.innerHTML = html;
}

(function initEasySetup() {
    const fileInput = document.getElementById('easyFile');
    if (!fileInput) return;

    fileInput.addEventListener('change', async (ev) => {
        const f = ev.target.files[0];
        if (!f) return;
        const fd = new FormData();
        fd.append('file', f);
        try {
            const resp = await fetch(`${api_proj}/column-mappings/preview-headers${mapScopeQs()}`,
                { method: 'POST', body: fd, credentials: 'include' });
            const data = await resp.json();
            if (!resp.ok) { toast(data.message || 'อ่านไฟล์ไม่สำเร็จ', 'danger'); return; }
            renderEasyCols(data);
        } catch (e) { toast(e.message, 'danger'); }
    });

    document.getElementById('easyColsBody').addEventListener('change', (ev) => {
        if (!ev.target.classList.contains('easy-type')) return;
        const tr = ev.target.closest('tr');
        tr.querySelector('.easy-toggle-cell').innerHTML = easyToggles(ev.target.value, {});
    });

    document.getElementById('btnEasyPreview').addEventListener('click', async () => {
        const f = fileInput.files[0];
        if (!f) { toast('เลือกไฟล์ก่อน', 'warning'); return; }
        const out = document.getElementById('easyPreview');
        out.innerHTML = '<span class="text-muted small">กำลังประมวลผล…</span>';
        const fd = new FormData();
        fd.append('file', f);
        fd.append('columns', JSON.stringify(easyGather()));
        try {
            const resp = await fetch(`${api_proj}/column-mappings/preview-cleansing`,
                { method: 'POST', body: fd, credentials: 'include' });
            const data = await resp.json();
            if (!resp.ok) {
                out.innerHTML = `<span class="text-danger small">${esc(data.message || 'ดูตัวอย่างไม่สำเร็จ')}</span>`;
                return;
            }
            renderEasyPreview(data);
        } catch (e) {
            out.innerHTML = `<span class="text-danger small">${esc(e.message)}</span>`;
        }
    });

    document.getElementById('btnEasySave').addEventListener('click', async () => {
        const status = document.getElementById('easyStatus');
        status.textContent = 'กำลังบันทึก…';
        status.className = 'small ms-2 text-muted';
        try {
            await api.put(`${api_proj}/column-mappings/setup`,
                { columns: easyGather(), sourceId: mapScopeId() });
            status.textContent = '✓ บันทึกการตั้งค่าแล้ว';
            status.className = 'small ms-2 text-success';
            if (loaded.mappings) loadMappings();
        } catch (e) {
            status.textContent = e.message;
            status.className = 'small ms-2 text-danger';
        }
    });
})();

// ---- Pipeline scope: cleansing/validation per ingestion source ----
function mapScopeId() { return document.getElementById('mapScope')?.value || null; }
function mapScopeQs() {
    const v = mapScopeId();
    return v ? `?sourceId=${encodeURIComponent(v)}` : '';
}
async function loadMapScope() {
    const sel = document.getElementById('mapScope');
    if (!sel) return;
    try {
        const sources = await api.get(`${api_proj}/ingestion-sources`);
        const cur = sel.value;
        sel.innerHTML = '<option value="">📋 ใช้ร่วมทุก source (shared)</option>'
            + (sources || []).map(s =>
                `<option value="${esc(s.id)}">${esc(s.sourceType)} — ${esc(String(s.id).slice(0, 8))}…</option>`
              ).join('');
        sel.value = cur;
    } catch { /* keep the shared option only */ }
}
document.getElementById('mapScope')?.addEventListener('change', () => {
    loadMappings();
    loadRules();
    // The easy-setup panel's columns belong to the previous scope — reset it.
    document.getElementById('easyCols')?.classList.add('d-none');
    const ef = document.getElementById('easyFile');
    if (ef) ef.value = '';
});

// ==================== MAPPINGS ====================
async function loadMappings() {
    try {
        const rows = await api.get(`${api_proj}/column-mappings${mapScopeQs()}`);
        const body = document.getElementById('mapBody');
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="4" class="text-muted text-center py-4"><i class="bi bi-arrow-left-right fs-3 d-block"></i>No mappings yet. <strong>Drop a sample file above</strong> to auto-detect headers, or add one manually.</td></tr>';
            return;
        }
        // Group by canonical so the operator sees "first_name + last_name → name"
        // rather than two unrelated rows. Within each canonical we sort by joinOrder.
        const groups = {};
        for (const m of rows) (groups[m.canonicalField] ||= []).push(m);

        body.innerHTML = Object.entries(groups).map(([canon, entries]) => {
            entries.sort((a, b) => (a.joinOrder ?? 0) - (b.joinOrder ?? 0)
                                || a.sourceColumn.localeCompare(b.sourceColumn));
            // Render each source-column as a chip with its join-separator hint.
            // The chip itself is clickable → loads the row into the edit form.
            const sourcePart = entries.map((m, i) => {
                const sep = i === 0
                    ? ''
                    : `<small class="text-muted ms-1">+ "${esc(m.joinSeparator ?? ' ')}" +</small> `;
                const transforms = (m.transformChain || []).join(', ');
                return `${sep}<a href="#" class="text-decoration-none"
                          onclick="editMap(${esc(JSON.stringify(m))});return false"
                          title="Edit · join order ${m.joinOrder ?? 0}${transforms ? ' · ' + transforms : ''}">
                          <code class="badge bg-light text-dark border">${esc(m.sourceColumn)}</code>
                        </a>`;
            }).join(' ');

            const transformsCombined = [...new Set(entries.flatMap(m => m.transformChain || []))].join(', ');
            const deleteBtns = entries.map(m =>
                `<button class="btn btn-link btn-sm text-danger p-0 me-1"
                         onclick="deleteMap('${esc(m.id)}')"
                         title="Delete ${esc(m.sourceColumn)}">✕ ${esc(m.sourceColumn)}</button>`
            ).join('');

            return `<tr>
                <td><span class="badge bg-info">${esc(canon)}</span>
                    <code class="small text-muted ms-1">{{${esc(canon)}}}</code></td>
                <td>${sourcePart}</td>
                <td><code class="small">${esc(transformsCombined)}</code></td>
                <td class="small">${deleteBtns}</td>
            </tr>`;
        }).join('');
    } catch (e) { toast(e.message, 'danger'); }
}
window.deleteMap = async function (id) {
    if (!confirm('Delete this mapping?')) return;
    try {
        await api.delete(`${api_proj}/column-mappings/${id}`);
        toast('Deleted.');
        loadMappings();
    } catch (e) { toast(e.message, 'danger'); }
};
window.editMap = function (m) {
    // Pre-fill the form so the operator can tweak transforms or join settings.
    document.getElementById('mapSource').value    = m.sourceColumn;
    document.getElementById('mapField').value     = m.canonicalField;
    document.getElementById('mapChain').value     = (m.transformChain || []).join(', ');
    document.getElementById('mapJoinOrder').value = m.joinOrder ?? 0;
    document.getElementById('mapJoinSep').value   = m.joinSeparator ?? '';
    document.getElementById('mapSource').focus();
};
document.getElementById('formMap').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    try {
        const chain = document.getElementById('mapChain').value
            .split(',').map(s => s.trim()).filter(Boolean);
        const sepRaw = document.getElementById('mapJoinSep').value;
        await api.post(`${api_proj}/column-mappings`, {
            sourceColumn:   document.getElementById('mapSource').value.trim(),
            canonicalField: document.getElementById('mapField').value,
            transformChain: chain,
            joinOrder:      parseInt(document.getElementById('mapJoinOrder').value, 10) || 0,
            // Empty input → null → defaults to " " server-side. Operators can
            // still set an explicit empty separator by typing a single space then deleting it
            // and re-saving — but the common case is "I forgot to fill it in".
            joinSeparator:  sepRaw.length > 0 ? sepRaw : null,
            sourceId:       mapScopeId()
        });
        toast('Saved.');
        loadMappings();
    } catch (e) { toast(e.message, 'danger'); }
});

// ============ SAMPLE-FILE HEADER DETECTION ============
// Drop or pick a CSV/XLSX → server parses headers + first 5 rows, comes back
// with: existing mappings flagged, naming-heuristic suggestions for the rest.
// Operator ticks the columns they want and clicks "Save selected" to upsert
// in one batch.
(function initMapHeaderDetect() {
    const drop     = document.getElementById('mapDrop');
    const fileInp  = document.getElementById('mapFile');
    const pickLink = document.getElementById('mapPickFile');
    const preview  = document.getElementById('mapPreview');
    const fileName = document.getElementById('mapFileName');
    const tbody    = document.getElementById('mapPreviewBody');
    const btnApply = document.getElementById('btnMapApplyAll');
    if (!drop) return;

    const CANONICALS = ['phone', 'message', 'url', 'name', 'email', 'custom'];

    pickLink.addEventListener('click', (e) => { e.preventDefault(); fileInp.click(); });
    drop.addEventListener('click', (e) => {
        // Don't double-trigger when the user clicks the "browse" link inside the box.
        if (e.target.tagName !== 'A') fileInp.click();
    });
    drop.addEventListener('dragover', (e) => {
        e.preventDefault();
        drop.classList.add('border-primary', 'bg-light');
    });
    drop.addEventListener('dragleave', () => drop.classList.remove('border-primary', 'bg-light'));
    drop.addEventListener('drop', (e) => {
        e.preventDefault();
        drop.classList.remove('border-primary', 'bg-light');
        if (e.dataTransfer.files.length) uploadSample(e.dataTransfer.files[0]);
    });
    fileInp.addEventListener('change', () => {
        if (fileInp.files.length) uploadSample(fileInp.files[0]);
    });

    async function uploadSample(file) {
        const fd = new FormData();
        fd.append('file', file);
        try {
            // Bypass api.req — it always sets Content-Type: application/json.
            // FormData needs the browser to set its own multipart boundary.
            const resp = await fetch(`${api_proj}/column-mappings/preview-headers${mapScopeQs()}`, {
                method: 'POST', body: fd, credentials: 'include'
            });
            const data = await resp.json();
            if (!resp.ok) { toast(data?.message || 'Upload failed.', 'danger'); return; }
            renderPreview(data);
        } catch (e) { toast(e.message, 'danger'); }
        finally { fileInp.value = ''; }
    }

    function renderPreview(data) {
        fileName.textContent = data.fileName;
        preview.classList.remove('d-none');

        tbody.innerHTML = data.suggestions.map((s, i) => {
            const samples = (data.sample || [])
                .map(row => row[s.header]).filter(v => v && v.length)
                .slice(0, 3).join(' · ');
            const opts = ['<option value="">— skip —</option>']
                .concat(CANONICALS.map(c =>
                    `<option value="${c}" ${s.suggested === c ? 'selected' : ''}>${c}</option>`))
                .join('');
            const lockedBadge = s.alreadyMapped
                ? `<span class="badge bg-secondary">already → ${esc(s.alreadyMapped)}</span>`
                : '';
            return `<tr data-header="${esc(s.header)}">
                <td><strong>${esc(s.header)}</strong> ${lockedBadge}</td>
                <td class="small text-muted">${esc(samples) || '<em>(blank)</em>'}</td>
                <td>
                    <select class="form-select form-select-sm mp-field" ${s.alreadyMapped ? 'disabled' : ''}>
                        ${opts}
                    </select>
                </td>
                <td>
                    <input class="form-control form-control-sm mp-chain"
                           placeholder="trim, digits"
                           ${s.alreadyMapped ? 'disabled' : ''} />
                </td>
            </tr>`;
        }).join('');

        // "Save selected" enables once at least one row has a non-empty canonical pick.
        const refresh = () => {
            const any = Array.from(tbody.querySelectorAll('.mp-field'))
                .some(s => !s.disabled && s.value);
            btnApply.disabled = !any;
        };
        tbody.querySelectorAll('.mp-field').forEach(s => s.addEventListener('change', refresh));
        refresh();
    }

    btnApply.addEventListener('click', async () => {
        const rows = Array.from(tbody.querySelectorAll('tr'))
            .map(tr => ({
                header: tr.dataset.header,
                field:  tr.querySelector('.mp-field').value,
                chain:  tr.querySelector('.mp-chain').value
            }))
            .filter(r => r.field);
        if (!rows.length) return;
        btnApply.disabled = true;
        let ok = 0, fail = 0;
        for (const r of rows) {
            try {
                await api.post(`${api_proj}/column-mappings`, {
                    sourceColumn:   r.header,
                    canonicalField: r.field,
                    transformChain: r.chain.split(',').map(s => s.trim()).filter(Boolean),
                    sourceId:       mapScopeId()
                });
                ok++;
            } catch { fail++; }
        }
        toast(`Saved ${ok}${fail ? ` (${fail} failed)` : ''}.`, fail ? 'warning' : 'success');
        loadMappings();
    });
})();

// ==================== SOURCES ====================
// Populates the "Workflow to start" picker in the source editor with the
// project's distinct workflow names.
async function loadSrcWorkflows() {
    const sel = document.getElementById('srcWorkflow');
    if (!sel) return;
    try {
        const defs = await api.get(`${api_proj}/workflows`);
        const names = [...new Set((defs || []).map(d => d.name).filter(Boolean))].sort();
        const current = sel.value;
        sel.innerHTML = '<option value="">— use the active workflow (default) —</option>'
            + names.map(n => `<option value="${esc(n)}">${esc(n)}</option>`).join('');
        sel.value = current;
    } catch { /* keep the default option only */ }
}

async function loadSources() {
    loadSrcWorkflows();
    try {
        const rows = await api.get(`${api_proj}/ingestion-sources`);
        const body = document.getElementById('srcBody');
        const sel  = document.getElementById('upSrcSelect');
        sel.innerHTML = '<option value="">— pick from your bindings —</option>';
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="6" class="text-muted text-center py-4"><i class="bi bi-cloud-download fs-3 d-block"></i>No automatic sources. Click <strong>+ New</strong> to add SFTP / SharePoint / REST, or skip this tab if you only upload manually.</td></tr>';
            return;
        }
        body.innerHTML = rows.map(s => {
            const cron = s.pollingSchedule || '*/5 * * * *';
            const human = humanizeCron(cron);
            const next = s.enabled ? nextCronTime(cron) : null;
            const nextLabel = !s.enabled ? '<span class="text-muted">paused</span>'
                : (next ? `<span title="${esc(next.toLocaleString())}">in ${relTime(next)}</span>`
                       : '<span class="text-muted">—</span>');
            return `<tr>
                <td>${esc(s.sourceType)}</td>
                <td class="small">${esc(human)}<br><code class="text-muted">${esc(cron)}</code></td>
                <td class="small">${nextLabel}</td>
                <td>${ACTION_LABEL[s.action] || s.action}</td>
                <td>${s.enabled ? '<span class="text-success">●</span>' : '<span class="text-muted">○</span>'}</td>
                <td>
                    <button class="btn btn-link btn-sm p-0"
                            onclick="runSrc('${esc(s.id)}')">Run now</button>
                    <button class="btn btn-link btn-sm p-0"
                            onclick='editSrc(${JSON.stringify(s).replace(/'/g, "&apos;")})'>Edit</button>
                    <button class="btn btn-link btn-sm p-0 text-danger"
                            onclick="deleteSrc('${esc(s.id)}')">Delete</button>
                </td>
            </tr>`;
        }).join('');
        rows.forEach(s => sel.insertAdjacentHTML('beforeend',
            `<option value="${esc(s.id)}">${esc(s.sourceType)} — ${esc(s.id.slice(0,8))}…</option>`));
    } catch (e) { toast(e.message, 'danger'); }
}

function showSrcEditor(reset) {
    const editor = document.getElementById('srcEditor');
    if (editor) editor.style.display = '';
    const testResult = document.getElementById('srcTestResult');
    if (testResult) testResult.textContent = '';
    const title = document.getElementById('srcEditTitle');
    if (title) title.textContent = reset ? 'New source binding' : 'Edit source binding';
    if (reset) {
        document.getElementById('srcId').value = '';
        document.getElementById('srcType').value = 'SFTP';
        if (window.scheduleFromCron) window.scheduleFromCron('*/5 * * * *');
        document.getElementById('srcArchive').value = '';
        document.getElementById('srcRejected').value = '';
        document.getElementById('srcAction').value = '0';
        document.getElementById('srcDup').value = '0';
        document.getElementById('srcWorkflow').value = '';
        document.getElementById('srcEnabled').checked = true;
        document.getElementById('srcConfig').value = JSON.stringify({
            host: 'sftp.example.com', port: 22, username: 'campaign',
            // Supply ONE of: privateKeyPem (+ optional passphrase) OR password.
            privateKeyPem: '-----BEGIN OPENSSH PRIVATE KEY-----\n…\n-----END OPENSSH PRIVATE KEY-----',
            password: '',
            remoteDirectory: '/incoming', filePattern: '*.csv',
            retryAttempts: 3
        }, null, 2);
        document.getElementById('srcConfig').placeholder = '';
    }
}
document.getElementById('btnSrcNew').addEventListener('click', () => showSrcEditor(true));
document.getElementById('btnSrcCancel').addEventListener('click', () => {
    document.getElementById('srcEditor').style.display = 'none';
});

document.getElementById('btnSrcTest').addEventListener('click', async () => {
    const btn = document.getElementById('btnSrcTest');
    const out = document.getElementById('srcTestResult');
    const sourceType = document.getElementById('srcType').value;
    const srcId = document.getElementById('srcId').value;
    const rawConfig = (document.getElementById('srcConfig').value || '').trim();

    btn.disabled = true;
    out.textContent = 'Testing…';
    out.className = 'small ms-2 text-muted';
    try {
        let res;
        if (rawConfig) {
            let config;
            try {
                config = JSON.parse(rawConfig);
            } catch (e) {
                out.textContent = 'Config is not valid JSON: ' + e.message;
                out.className = 'small ms-2 text-danger';
                return;
            }
            res = await api.post(`${api_proj}/ingestion-sources/test-connection`,
                { sourceType, config });
        } else if (srcId) {
            // Editing a saved binding with no new config pasted — test the
            // stored (encrypted) config server-side.
            res = await api.post(`${api_proj}/ingestion-sources/${srcId}/test-connection`, {});
        } else {
            out.textContent = 'Paste config JSON to test, or save the binding first.';
            out.className = 'small ms-2 text-danger';
            return;
        }
        out.textContent = res.message;
        out.className = 'small ms-2 ' + (res.ok ? 'text-success' : 'text-danger');
    } catch (e) {
        out.textContent = e.message;
        out.className = 'small ms-2 text-danger';
    } finally {
        btn.disabled = false;
    }
});

// ============ POLLING SCHEDULE PICKER ============
// Bridges the friendly mode-based UI with the cron string the backend stores.
// scheduleToCron() reads whichever sub-form is active and produces the cron.
// scheduleFromCron() tries to recognise a preset shape and switches mode to
// match; anything we can't recognise falls back to "advanced" and shows the
// raw expression untouched.
(function initSchedulePicker() {
    const modeEl    = document.getElementById('srcSchedMode');
    const minN      = document.getElementById('srcSchedMinN');
    const hourN     = document.getElementById('srcSchedHourN');
    const dailyT    = document.getElementById('srcSchedDailyTime');
    const dow       = document.getElementById('srcSchedDow');
    const weeklyT   = document.getElementById('srcSchedWeeklyTime');
    const advCron   = document.getElementById('srcSchedCron');
    const preview   = document.getElementById('srcCronPreview');
    if (!modeEl) return;

    const panes = {
        minutes:  document.getElementById('srcSchedMinutes'),
        hours:    document.getElementById('srcSchedHours'),
        daily:    document.getElementById('srcSchedDaily'),
        weekly:   document.getElementById('srcSchedWeekly'),
        advanced: document.getElementById('srcSchedAdvanced')
    };

    function showMode(m) {
        for (const [k, el] of Object.entries(panes)) {
            if (!el) continue;
            const active = (k === m);
            el.style.display = active ? '' : 'none';
            // Disable inputs in hidden panes: a hidden <input min="1"> left at
            // an out-of-range value (e.g. "every N minutes" while you're on
            // "Weekly") otherwise fails HTML5 validation and silently blocks
            // the Save — even though that field isn't part of the chosen
            // schedule. Disabled inputs are exempt from validation.
            el.querySelectorAll('input,select')
              .forEach(i => { i.disabled = !active; });
        }
        modeEl.value = m;
    }

    window.scheduleToCron = function () {
        const mode = modeEl.value;
        switch (mode) {
            case 'minutes': {
                const n = Math.max(1, Math.min(59, Number(minN.value) || 5));
                return `*/${n} * * * *`;
            }
            case 'hours': {
                const n = Math.max(1, Math.min(23, Number(hourN.value) || 1));
                return n === 1 ? `0 * * * *` : `0 */${n} * * *`;
            }
            case 'daily': {
                const [h, m] = (dailyT.value || '09:00').split(':').map(Number);
                return `${m} ${h} * * *`;
            }
            case 'weekly': {
                const [h, m] = (weeklyT.value || '09:00').split(':').map(Number);
                return `${m} ${h} * * ${dow.value}`;
            }
            default:
                return (advCron.value || '*/5 * * * *').trim();
        }
    };

    // Try to recognise the cron as one of our presets so editing an existing
    // source opens in the friendly mode it was created with. Anything outside
    // the preset shapes falls back to Advanced — the operator sees their
    // expression untouched.
    window.scheduleFromCron = function (cron) {
        cron = (cron || '*/5 * * * *').trim();
        const parts = cron.split(/\s+/);
        if (parts.length === 5) {
            const [m, h, dom, mon, d] = parts;
            const everyN = /^\*\/(\d+)$/;
            // every N minutes:  */N * * * *
            let mm;
            if ((mm = m.match(everyN)) && h === '*' && dom === '*' && mon === '*' && d === '*') {
                showMode('minutes');
                minN.value = mm[1];
                preview.textContent = cron;
                return;
            }
            // every hour:       0 * * * *
            if (m === '0' && h === '*' && dom === '*' && mon === '*' && d === '*') {
                showMode('hours');
                hourN.value = 1;
                preview.textContent = cron;
                return;
            }
            // every N hours:    0 */N * * *
            if (m === '0' && (mm = h.match(everyN)) && dom === '*' && mon === '*' && d === '*') {
                showMode('hours');
                hourN.value = mm[1];
                preview.textContent = cron;
                return;
            }
            // daily at HH:MM:   M H * * *
            if (/^\d+$/.test(m) && /^\d+$/.test(h) && dom === '*' && mon === '*' && d === '*') {
                showMode('daily');
                dailyT.value = `${String(h).padStart(2,'0')}:${String(m).padStart(2,'0')}`;
                preview.textContent = cron;
                return;
            }
            // weekly:           M H * * D
            if (/^\d+$/.test(m) && /^\d+$/.test(h) && dom === '*' && mon === '*' && /^[0-6]$/.test(d)) {
                showMode('weekly');
                weeklyT.value = `${String(h).padStart(2,'0')}:${String(m).padStart(2,'0')}`;
                dow.value = d;
                preview.textContent = cron;
                return;
            }
        }
        // Fallback — show raw cron in advanced mode.
        showMode('advanced');
        advCron.value = cron;
        preview.textContent = cron;
    };

    function refreshPreview() {
        preview.textContent = window.scheduleToCron();
    }
    modeEl.addEventListener('change', () => { showMode(modeEl.value); refreshPreview(); });
    [minN, hourN, dailyT, dow, weeklyT, advCron]
        .forEach(el => el.addEventListener('input', refreshPreview));
    // Initial state.
    showMode('minutes'); refreshPreview();
})();

// Resilient setter — a missing element (e.g. a stale cached page) is logged
// instead of throwing, so opening the editor never dies half-way.
function srcSet(id, value) {
    const el = document.getElementById(id);
    if (!el) {
        console.warn(`Source editor: #${id} not found — try a hard refresh (Ctrl+Shift+R).`);
        return;
    }
    if (el.type === 'checkbox') el.checked = !!value;
    else el.value = value;
}

window.editSrc = function (s) {
    showSrcEditor(false);
    srcSet('srcId', s.id);
    srcSet('srcType', s.sourceType);
    if (window.scheduleFromCron) window.scheduleFromCron(s.pollingSchedule || '*/5 * * * *');
    srcSet('srcArchive', s.archiveDirectory || '');
    srcSet('srcRejected', s.rejectedDirectory || '');
    srcSet('srcAction', String(s.action));
    srcSet('srcDup', String(s.duplicatePolicy));
    // Make sure the bound workflow shows even if the picker list is stale.
    const wfSel = document.getElementById('srcWorkflow');
    const wfName = s.workflowName || '';
    if (wfSel && wfName && !Array.from(wfSel.options).some(o => o.value === wfName))
        wfSel.add(new Option(wfName, wfName));
    srcSet('srcWorkflow', wfName);
    srcSet('srcEnabled', !!s.enabled);
    srcSet('srcConfig', '');
    const cfg = document.getElementById('srcConfig');
    if (cfg) cfg.placeholder =
        '(Existing config is encrypted on the server. Paste JSON to overwrite it.)';
};

document.getElementById('formSrc').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    try {
        const srcId = document.getElementById('srcId').value;
        const raw = document.getElementById('srcConfig').value.trim();
        // When editing, a blank config means "keep the stored one" — only a
        // brand-new source must supply config.
        if (!raw && !srcId) { toast('Paste the JSON config (or {} to clear).', 'warning'); return; }
        let config;
        if (raw) {
            try { config = JSON.parse(raw); }
            catch { toast('Config must be valid JSON', 'danger'); return; }
        }

        const body = {
            id: srcId || null,
            sourceType: document.getElementById('srcType').value,
            archiveDirectory:  document.getElementById('srcArchive').value || null,
            rejectedDirectory: document.getElementById('srcRejected').value || null,
            action:            parseInt(document.getElementById('srcAction').value, 10),
            duplicatePolicy:   parseInt(document.getElementById('srcDup').value, 10),
            enabled:           document.getElementById('srcEnabled').checked,
            pollingSchedule:   window.scheduleToCron(),
            workflowName:      document.getElementById('srcWorkflow').value || null
        };
        if (config !== undefined) body.config = config;   // omit → keep stored config
        await api.post(`${api_proj}/ingestion-sources`, body);
        toast('Saved.');
        document.getElementById('srcEditor').style.display = 'none';
        loadSources();
    } catch (e) { toast(e.message, 'danger'); }
});

window.deleteSrc = async function (id) {
    if (!confirm('Delete this source binding?')) return;
    try {
        await api.delete(`${api_proj}/ingestion-sources/${id}`);
        toast('Deleted.');
        loadSources();
    } catch (e) { toast(e.message, 'danger'); }
};

window.runSrc = async function (id) {
    try {
        await api.post(`${api_proj}/ingestion-sources/${id}/run`, {});
        toast('Manual run queued — new files appear under Batches shortly.');
    } catch (e) { toast(e.message, 'danger'); }
};

document.getElementById('formUpload').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const out = document.getElementById('upResult');
    out.classList.add('d-none');
    try {
        const settingsId = document.getElementById('upSrcSelect').value;
        if (!settingsId) { toast('Pick a source binding first', 'warning'); return; }
        const fd = new FormData();
        fd.append('settingsId', settingsId);
        fd.append('file', document.getElementById('upFile').files[0]);
        const force = document.getElementById('upForce').checked;
        const resp = await fetch(`${api_proj}/ingest?force=${force}`, {
            method: 'POST', credentials: 'include', body: fd
        });
        const data = await resp.json();
        out.classList.remove('d-none');
        out.textContent = JSON.stringify(data, null, 2);
        if (resp.ok) {
            toast(`Ingested ${data.acceptedRows}/${data.totalRows} rows.`);
            loadBatches();
        } else {
            toast(data.message || 'Upload failed', 'danger');
        }
    } catch (e) { toast(e.message, 'danger'); }
});

async function loadBatches() {
    try {
        const rows = await api.get(`${api_proj}/ingestion-batches?take=20`);
        const body = document.getElementById('batchBody');
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="7" class="text-muted">No batches yet.</td></tr>';
            return;
        }
        body.innerHTML = rows.map(b => {
            const cls = b.status === 'Completed' ? 'success'
                : (b.status === 'Failed' ? 'danger' : 'warning');
            const rejectedCell = b.hasRejections
                ? `<a href="#" onclick="showRejections('${esc(b.id)}');return false"
                       class="text-danger" title="View rejection sample">
                       ${b.rejectedRows} <i class="bi bi-search small"></i></a>`
                : `<span class="text-danger">${b.rejectedRows}</span>`;
            return `<tr>
                <td class="small">${fmtDate(b.ingestedAt)}</td>
                <td><code class="small">${esc(b.sourceType)}</code></td>
                <td class="small">${esc(b.sourceRef)}</td>
                <td>${b.totalRows}</td>
                <td class="text-success">${b.acceptedRows}</td>
                <td>${rejectedCell}</td>
                <td><span class="badge bg-${cls}">${esc(b.status)}</span></td>
            </tr>`;
        }).join('');
    } catch (e) { toast(e.message, 'danger'); }
}
document.getElementById('btnBatchRefresh').addEventListener('click', loadBatches);

// Per-batch rejection sample — bounded to 50 by the pipeline. Renders the
// raw error codes so operators can fix the data and re-upload. Same modal
// element is reused for each batch.
window.showRejections = async function (batchId) {
    const modalEl = document.getElementById('rejectionsModal');
    const body    = document.getElementById('rejectionsBody');
    body.innerHTML = '<div class="text-muted small">Loading…</div>';
    new bootstrap.Modal(modalEl).show();
    try {
        const d = await api.get(`${api_proj}/ingestion-batches/${batchId}/rejections`);
        // Whole-round failure (ProcessRowsAsync threw before any row) — the
        // pipeline stores the reason in "fatal".
        if (d.fatal) {
            body.innerHTML = `<div class="alert alert-danger small mb-0">
                <strong>ทั้งรอบนี้ล้มเหลว</strong> — ไฟล์ไม่ถูกประมวลผลเลย<br>
                <span class="text-muted">เหตุผล:</span> ${esc(d.fatal)}</div>`;
            return;
        }
        if (!d.items || d.items.length === 0) {
            body.innerHTML = '<div class="text-muted small">No rejection sample stored for this batch.</div>';
            return;
        }
        const header = d.truncated
            ? `<div class="alert alert-warning small mb-2">
                 Showing first ${d.shown} of ${d.total} rejected rows. Re-upload after fixing to retry the rest.
               </div>`
            : `<div class="small text-muted mb-2">${d.shown} rejected row(s).</div>`;
        body.innerHTML = header + `
            <table class="table table-sm">
                <thead><tr><th style="width:70px">แถวที่</th><th>เหตุผล</th>
                    <th class="text-muted" style="width:200px">รหัส (สำหรับ IT)</th></tr></thead>
                <tbody>${d.items.map(r => {
                    const reasons = (r.reasons && r.reasons.length) ? r.reasons : (r.errors || []);
                    return `<tr><td>${r.rowIndex}</td>
                        <td class="text-danger">${reasons.map(x => esc(x)).join(', ')}</td>
                        <td>${(r.errors || []).map(e =>
                            `<code class="small text-muted me-1">${esc(e)}</code>`).join('')}</td>
                    </tr>`;
                }).join('')}
                </tbody>
            </table>`;
    } catch (e) {
        body.innerHTML = `<div class="text-danger">${esc(e.message)}</div>`;
    }
};

// ==================== WORKFLOWS ====================
// Internal model: an object {name, expiration, initialStep, steps: {…}}
// Form view edits the model directly; JSON view serialises it; Diagram view
// renders it via Mermaid. Source of truth = the model object; views sync
// to/from it.
let wfModel = newWorkflowModel();
let wfActiveView = 'form';

// ============ DURATION HELPERS ============
// Workflow durations are stored as .NET TimeSpan strings "[d.]HH:MM:SS".
// Operators don't read those — they read "2 hours" or "3 days". These two
// functions convert between the human-friendly {n, unit} shape and the
// canonical string. Anything we can't recognise round-trips as-is so
// hand-edited specs aren't clobbered.
function tsToFriendly(ts) {
    if (!ts) return { n: 0, unit: 'minutes', raw: '' };
    const m = String(ts).trim().match(/^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})$/);
    if (!m) return { n: 0, unit: 'minutes', raw: ts };
    const days = parseInt(m[1] || '0', 10);
    const hours = parseInt(m[2], 10);
    const mins  = parseInt(m[3], 10);
    const total = (days * 1440) + (hours * 60) + mins;
    if (total === 0)                  return { n: 0,         unit: 'minutes', raw: ts };
    if (total % 1440 === 0)           return { n: total / 1440, unit: 'days',    raw: ts };
    if (total % 60 === 0)             return { n: total / 60,   unit: 'hours',   raw: ts };
    return { n: total, unit: 'minutes', raw: ts };
}
function friendlyToTs(n, unit) {
    n = Math.max(0, Number(n) || 0);
    const mins = unit === 'days' ? n * 1440 : unit === 'hours' ? n * 60 : n;
    const days = Math.floor(mins / 1440);
    const hours = Math.floor((mins % 1440) / 60);
    const m = mins % 60;
    const hhmmss = `${String(hours).padStart(2,'0')}:${String(m).padStart(2,'0')}:00`;
    return days > 0 ? `${days}.${hhmmss}` : hhmmss;
}

// ============ CRON HUMAN-READABLE + NEXT-FIRE ============
// Recognises the same preset shapes the schedule picker emits. Anything
// outside those falls back to "(custom cron)" / null — operator sees the
// raw expression in the cell below.
function humanizeCron(cron) {
    const parts = (cron || '').trim().split(/\s+/);
    if (parts.length !== 5) return '(custom cron)';
    const [m, h, dom, mon, dow] = parts;
    let mm;
    if ((mm = m.match(/^\*\/(\d+)$/)) && h === '*' && dom === '*' && mon === '*' && dow === '*')
        return `Every ${mm[1]} minute${mm[1] === '1' ? '' : 's'}`;
    if (m === '0' && h === '*' && dom === '*' && mon === '*' && dow === '*')
        return 'Every hour';
    if (m === '0' && (mm = h.match(/^\*\/(\d+)$/)) && dom === '*' && mon === '*' && dow === '*')
        return `Every ${mm[1]} hours`;
    if (/^\d+$/.test(m) && /^\d+$/.test(h) && dom === '*' && mon === '*' && dow === '*')
        return `Daily at ${pad(h)}:${pad(m)}`;
    if (/^\d+$/.test(m) && /^\d+$/.test(h) && dom === '*' && mon === '*' && /^[0-6]$/.test(dow))
        return `Weekly ${['Sun','Mon','Tue','Wed','Thu','Fri','Sat'][Number(dow)]} at ${pad(h)}:${pad(m)}`;
    return '(custom cron)';
}
function nextCronTime(cron, from = new Date()) {
    const parts = (cron || '').trim().split(/\s+/);
    if (parts.length !== 5) return null;
    const [m, h, dom, mon, dow] = parts;
    const d = new Date(from); d.setSeconds(0, 0);
    let mm;
    if ((mm = m.match(/^\*\/(\d+)$/)) && h === '*' && dom === '*' && mon === '*' && dow === '*') {
        const n = Number(mm[1]);
        const next = Math.ceil((d.getMinutes() + 1) / n) * n;
        d.setMinutes(next, 0, 0);
        return d;
    }
    if (m === '0' && h === '*' && dom === '*' && mon === '*' && dow === '*') {
        d.setHours(d.getHours() + 1, 0, 0, 0); return d;
    }
    if (m === '0' && (mm = h.match(/^\*\/(\d+)$/)) && dom === '*' && mon === '*' && dow === '*') {
        const n = Number(mm[1]);
        const next = Math.ceil((d.getHours() + 1) / n) * n;
        if (next >= 24) { d.setDate(d.getDate() + 1); d.setHours(0, 0, 0, 0); }
        else { d.setHours(next, 0, 0, 0); }
        return d;
    }
    if (/^\d+$/.test(m) && /^\d+$/.test(h) && dom === '*' && mon === '*' && dow === '*') {
        d.setHours(Number(h), Number(m), 0, 0);
        if (d <= from) d.setDate(d.getDate() + 1);
        return d;
    }
    if (/^\d+$/.test(m) && /^\d+$/.test(h) && dom === '*' && mon === '*' && /^[0-6]$/.test(dow)) {
        d.setHours(Number(h), Number(m), 0, 0);
        const targetDow = Number(dow);
        while (d <= from || d.getDay() !== targetDow) d.setDate(d.getDate() + 1);
        return d;
    }
    return null;
}
function pad(s) { return String(Number(s)).padStart(2, '0'); }
function relTime(future) {
    const ms = future - new Date();
    if (ms <= 0) return 'now';
    const s = Math.floor(ms / 1000);
    if (s < 60) return `${s}s`;
    const m = Math.floor(s / 60);
    if (m < 60) return `${m}m`;
    const h = Math.floor(m / 60);
    if (h < 24) return `${h}h ${m % 60}m`;
    const d = Math.floor(h / 24);
    return `${d}d ${h % 24}h`;
}

function newWorkflowModel() {
    return {
        name: '',
        expiration: '60.00:00:00',
        initialStep: 'send',
        steps: {
            send: {
                type: 'send_sms',
                template: 'Hello {{name}}, visit https://example.com/landing',
                wait: '2.00:00:00',
                maxRepeats: 3,
                onSignal: { 'shortlink.clicked': 'complete' },
                onTimeout: 'send'
            },
            complete: { type: 'complete' }
        }
    };
}

// ============ WORKFLOW INSTANCES SUMMARY ============
// Server returns one row per (definitionId, state, count). We join against
// the loaded workflow definitions to label the rows; the state palette
// matches the WorkflowState enum colours used elsewhere.
const WF_STATE_LABEL = ['Pending','Scheduled','Dispatching','AwaitingAction','ReminderDue','Completed','Failed','Expired'];
const WF_STATE_BG    = ['secondary','info','primary','warning','warning','success','danger','dark'];

async function loadWfInstances() {
    try {
        const [defs, summary] = await Promise.all([
            api.get(`${api_proj}/workflows`),
            api.get(`${api_proj}/workflow-instances/summary`)
        ]);
        const byDef = new Map();
        for (const r of summary) {
            const arr = byDef.get(r.definitionId) || [];
            arr.push(r);
            byDef.set(r.definitionId, arr);
        }
        const container = document.getElementById('wfInstSummary');
        if (!defs.length) {
            container.innerHTML = '<span class="text-muted">No definitions yet.</span>';
            return;
        }
        container.innerHTML = defs.map(d => {
            const rows = byDef.get(d.id) || [];
            const total = rows.reduce((a, r) => a + r.count, 0);
            const badges = rows.map(r => {
                const label = typeof r.state === 'number' ? WF_STATE_LABEL[r.state] : r.state;
                const idx   = typeof r.state === 'number' ? r.state : WF_STATE_LABEL.indexOf(label);
                const bg    = WF_STATE_BG[idx] || 'secondary';
                // Click → drill-down modal listing instances + abort buttons.
                return `<a href="#" class="badge bg-${bg} me-1 text-decoration-none text-white"
                          onclick="showWfInstances('${esc(d.id)}','${esc(d.name)}',${idx});return false"
                          title="Show ${esc(label)} instances">${esc(label)}: ${r.count}</a>`;
            }).join('');
            return `<div class="py-2 border-bottom">
                <div><strong>${esc(d.name)}</strong> v${d.version}
                    ${d.active ? '<span class="badge bg-success ms-1">active</span>' : ''}
                    <span class="text-muted small ms-1">${total} total</span></div>
                <div class="mt-1">${badges || '<span class="text-muted small">no instances</span>'}</div>
            </div>`;
        }).join('');
    } catch (e) { toast(e.message, 'danger'); }
}
document.getElementById('btnWfInstRefresh')?.addEventListener('click', loadWfInstances);

// Drill-down — opens a modal listing instances in the chosen state, with
// abort buttons for non-terminal states. Reuses the same backend list
// endpoint as the API, filtered by (definitionId, state).
window.showWfInstances = async function (definitionId, definitionName, stateIdx, page) {
    page = page || 1;
    const modalEl = document.getElementById('wfInstModal') || buildWfInstModal();
    document.getElementById('wfInstTitle').textContent =
        `${definitionName} — ${WF_STATE_LABEL[stateIdx]} instances`;
    const body = document.getElementById('wfInstBody');
    body.innerHTML = '<div class="text-muted small">Loading…</div>';
    new bootstrap.Modal(modalEl).show();
    try {
        const data = await api.get(
            `${api_proj}/workflow-instances?definitionId=${encodeURIComponent(definitionId)}`
            + `&state=${stateIdx}&page=${page}&pageSize=50`);
        const rows = data.items || [];
        const isTerminal = stateIdx === 5 || stateIdx === 6 || stateIdx === 7;
        if (!rows.length) {
            body.innerHTML = '<div class="text-muted small">No instances in this state.</div>';
            return;
        }
        const bulkBar = isTerminal ? '' :
            `<div class="d-flex justify-content-between align-items-center mb-2">
               <span class="small text-muted">${data.total} รายการที่ยังค้าง — แต่ละรายการคือ 1 ผู้รับ</span>
               <button class="btn btn-sm btn-outline-danger"
                  onclick="abortAllWfInstances('${esc(definitionId)}',${stateIdx},'${esc(definitionName)}')">
                  หยุด/เคลียร์ทั้งหมด (${data.total})</button>
             </div>`;
        body.innerHTML = bulkBar + `<table class="table table-sm align-middle">
            <thead><tr>
                <th>Created</th><th>Recipient</th><th>Current step</th>
                <th>Repeats</th><th>Expires</th><th></th>
            </tr></thead>
            <tbody>${rows.map((i, n) => `<tr>
                <td class="small">${fmtDate(i.createdAt)}</td>
                <td><code class="small">${esc(i.maskedPhone || '')}</code></td>
                <td class="small">${esc(i.currentStep)}</td>
                <td>${i.stepRepeatCount}</td>
                <td class="small">${fmtDate(i.expiresAt)}</td>
                <td class="text-nowrap">
                    <button class="btn btn-link btn-sm p-0 me-2"
                       onclick="toggleWfInstanceDetail('${esc(i.id)}',${n})">รายละเอียด</button>
                    ${isTerminal ? '' :
                    `<button class="btn btn-link btn-sm p-0 text-danger"
                       onclick="abortWfInstance('${esc(i.id)}', '${esc(definitionId)}', ${stateIdx}, '${esc(definitionName)}')"
                       title="Force this instance to Expired">Abort</button>`}</td>
            </tr>
            <tr id="wfInstDetail${n}" style="display:none"><td colspan="6" class="bg-light"></td></tr>`).join('')}</tbody></table>
            <div id="wfInstPager" class="d-flex align-items-center gap-2"></div>`;
        renderPager(document.getElementById('wfInstPager'), data,
            p => showWfInstances(definitionId, definitionName, stateIdx, p));
    } catch (e) {
        body.innerHTML = `<div class="text-danger">${esc(e.message)}</div>`;
    }
};

// Toggle the per-instance detail row — lazy-loads the transition timeline +
// SMS the first time it's opened.
window.toggleWfInstanceDetail = async function (instanceId, n) {
    const row  = document.getElementById('wfInstDetail' + n);
    if (!row) return;
    if (row.style.display !== 'none') { row.style.display = 'none'; return; }
    row.style.display = '';
    const cell = row.firstElementChild;
    if (cell.dataset.loaded === '1') return;
    cell.innerHTML = '<div class="text-muted small p-2">กำลังโหลด…</div>';
    try {
        const d = await api.get(`${api_proj}/workflow-instances/${instanceId}/detail`);
        cell.innerHTML = wfInstanceDetailHtml(d);
        cell.dataset.loaded = '1';
    } catch (e) {
        cell.innerHTML = `<div class="text-danger small p-2">${esc(e.message)}</div>`;
    }
};

// Renders one instance's timeline + SMS — shared by the instance drill-down.
function wfInstanceDetailHtml(d) {
    let html = '<div class="p-2">';
    html += '<div class="small fw-semibold mb-1">ไทม์ไลน์ workflow</div>';
    const tx = d.transitions || [];
    if (tx.length) {
        html += '<table class="table table-sm small mb-3"><tbody>';
        tx.forEach(t => {
            html += `<tr>
              <td class="text-nowrap text-muted" style="width:140px">${fmtDate(t.at)}</td>
              <td>${wfStateBadge(t.from)} → ${wfStateBadge(t.to)}</td>
              <td><code class="small">${esc(t.trigger || '')}</code></td>
              <td class="text-muted small">${t.dataJson ? esc(t.dataJson) : ''}</td>
            </tr>`;
        });
        html += '</tbody></table>';
    } else {
        html += '<div class="text-muted small mb-3">ไม่มีการเปลี่ยนสถานะที่บันทึกไว้</div>';
    }
    html += '<div class="small fw-semibold mb-1">SMS ของรายการนี้</div>';
    const sms = d.sms || [];
    if (sms.length) {
        sms.forEach(s => {
            html += `<div class="border rounded p-2 mb-2 small bg-white">
              <div class="d-flex justify-content-between">
                <div>${smsStatusBadge(s.status)}
                  <span class="ms-1">${esc(s.maskedTo)}</span>
                  <span class="text-muted ms-1">· ${esc(s.provider || '')}</span></div>
                <div class="text-muted">${fmtDate(s.createdAt)}</div>
              </div>
              <div class="mt-1"><span class="text-muted">ข้อความ:</span> ${esc(s.body || '')}</div>`;
            const meta = [];
            if (s.sentAt)      meta.push(`ส่ง ${fmtDate(s.sentAt)}`);
            if (s.deliveredAt) meta.push(`ถึง ${fmtDate(s.deliveredAt)}`);
            if (s.attempts)    meta.push(`พยายาม ${s.attempts} ครั้ง`);
            if (meta.length) html += `<div class="text-muted mt-1">${meta.join(' · ')}</div>`;
            if (s.errorCode)
                html += `<div class="text-danger mt-1">ข้อผิดพลาด: ${esc(s.errorCode)}</div>`;
            if (s.rawProviderResponse)
                html += `<div class="mt-1"><span class="text-muted">คำตอบจากผู้ให้บริการ:</span>
                  <pre class="small bg-light p-1 mb-0 mt-1"
                       style="white-space:pre-wrap">${esc(s.rawProviderResponse)}</pre></div>`;
            html += '</div>';
        });
    } else {
        html += '<div class="text-muted small">ยังไม่มี SMS</div>';
    }
    return html + '</div>';
}

window.abortAllWfInstances = async function (definitionId, stateIdx, definitionName) {
    const stateName = WF_STATE_LABEL[stateIdx];
    const ok = await window.confirmAction({
        title: 'เคลียร์งานที่ค้าง', mode: 'simple',
        message: `บังคับให้ทุก instance สถานะ <strong>${esc(stateName)}</strong> `
            + `ของ <strong>${esc(definitionName)}</strong> กลายเป็น <strong>Expired</strong>? `
            + 'ทั้งหมดจะหยุดส่ง SMS ทันทีและกู้คืนไม่ได้',
        okLabel: 'เคลียร์ทั้งหมด'
    });
    if (!ok) return;
    try {
        const r = await api.post(`${api_proj}/workflow-instances/abort-all`,
            { definitionId, state: stateIdx });
        toast(`เคลียร์แล้ว ${r.aborted} รายการ`);
        showWfInstances(definitionId, definitionName, stateIdx);
        loadWfInstances();
    } catch (e) { toast(e.message, 'danger'); }
};

function buildWfInstModal() {
    const wrap = document.createElement('div');
    wrap.innerHTML = `
    <div class="modal fade" id="wfInstModal" tabindex="-1">
      <div class="modal-dialog modal-xl modal-dialog-scrollable">
        <div class="modal-content">
          <div class="modal-header">
            <h5 class="modal-title" id="wfInstTitle">Instances</h5>
            <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
          </div>
          <div class="modal-body" id="wfInstBody"></div>
        </div>
      </div>
    </div>`;
    document.body.appendChild(wrap.firstElementChild);
    return document.getElementById('wfInstModal');
}

window.abortWfInstance = async function (instanceId, definitionId, stateIdx, definitionName) {
    const ok = await window.confirmAction({
        title: 'Abort instance', mode: 'simple',
        message: 'Force this workflow instance to <strong>Expired</strong>? It will stop sending immediately and cannot be resumed.',
        okLabel: 'Abort'
    });
    if (!ok) return;
    try {
        await api.post(`${api_proj}/workflow-instances/${instanceId}/abort`, {});
        toast('Instance aborted.');
        // Re-open the drill-down to show the updated state, plus refresh the
        // summary so the badge counts update.
        showWfInstances(definitionId, definitionName, stateIdx);
        loadWfInstances();
    } catch (e) { toast(e.message, 'danger'); }
};

// Coupon batches cached for the issue_coupon step's batch picker.
async function loadWfCouponBatches() {
    try {
        const rows = await api.get(`${api_proj}/coupons/batches`);
        window._wfCouponBatches = rows.map(b => ({ id: b.id, name: b.name, brandName: b.brandName }));
    } catch { window._wfCouponBatches = []; }
}

async function loadWorkflows() {
    try {
        const rows = await api.get(`${api_proj}/workflows`);
        const body = document.getElementById('wfBody');
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="4" class="text-muted text-center py-4"><i class="bi bi-diagram-3 fs-3 d-block"></i>No workflows yet. <strong>Try the Quick-start panel</strong> on the right &mdash; pick a scenario, fill three fields, done.</td></tr>';
        } else {
            body.innerHTML = rows.map(w => `
                <tr>
                    <td><a href="#" onclick="loadWfDef('${esc(w.id)}');return false">${esc(w.name)}</a></td>
                    <td>${w.version}</td>
                    <td>${w.active ? '<i class="bi bi-check-circle-fill text-success"></i>' : ''}</td>
                    <td>${w.active
                        ? `<button class="btn btn-link btn-sm p-0" onclick="toggleWf('${esc(w.id)}',false)">Deactivate</button>`
                        : `<button class="btn btn-link btn-sm p-0" onclick="toggleWf('${esc(w.id)}',true)">Activate</button>`}</td>
                </tr>`).join('');
        }
        renderOnboardingChecklist().catch(() => {});   // re-evaluate after activate/save
    } catch (e) { toast(e.message, 'danger'); }
}

// Id of the definition currently loaded in the editor — drives whether
// "Update this version" (PUT, in-place) is offered alongside "Save as new".
let wfEditingId = null;
function wfRefreshButtons() {
    const u = document.getElementById('btnWfUpdate');
    if (u) u.style.display = wfEditingId ? '' : 'none';
}

window.loadWfDef = async function (id) {
    try {
        const d = await api.get(`${api_proj}/workflows/${id}`);
        wfModel = {
            name: d.name,
            expiration: d.spec.expiration ?? '60.00:00:00',
            initialStep: d.spec.initialStep,
            steps: d.spec.steps || {}
        };
        wfEditingId = d.id;
        document.getElementById('wfEditTitle').textContent = `Editor — ${d.name} v${d.version}`;
        wfRefreshButtons();
        renderWf();
    } catch (e) { toast(e.message, 'danger'); }
};

window.toggleWf = async function (id, activate) {
    try {
        await api.post(`${api_proj}/workflows/${id}/${activate ? 'activate' : 'deactivate'}`);
        toast(activate ? 'Activated.' : 'Deactivated.');
        loadWorkflows();
    } catch (e) { toast(e.message, 'danger'); }
};

document.getElementById('btnWfNew').addEventListener('click', () => {
    wfModel = newWorkflowModel();
    wfEditingId = null;
    document.getElementById('wfEditTitle').textContent = 'Editor — new';
    wfRefreshButtons();
    renderWf();
});

// --- View switcher ---
document.querySelectorAll('#wfViewSwitch button').forEach(btn => {
    btn.addEventListener('click', () => {
        // Sync the current view's edits back to the model before switching.
        if (wfActiveView === 'form') captureFormToModel();
        else if (wfActiveView === 'json') {
            try { captureJsonToModel(); }
            catch (e) { toast('JSON invalid: ' + e.message, 'danger'); return; }
        }
        wfActiveView = btn.dataset.view;
        document.querySelectorAll('#wfViewSwitch button').forEach(b =>
            b.classList.toggle('active', b === btn));
        renderWf();
    });
});

function renderWf() {
    document.getElementById('wfName').value = wfModel.name;
    document.getElementById('wfExp').value  = wfModel.expiration;
    // Sync the friendly N+unit inputs from the canonical timespan string.
    const ef = tsToFriendly(wfModel.expiration);
    document.getElementById('wfExpN').value = ef.n || 60;
    document.getElementById('wfExpU').value = ef.unit;

    document.getElementById('wfFormView').style.display    = wfActiveView === 'form'    ? '' : 'none';
    document.getElementById('wfJsonView').style.display    = wfActiveView === 'json'    ? '' : 'none';
    document.getElementById('wfDiagramView').style.display = wfActiveView === 'diagram' ? '' : 'none';

    if (wfActiveView === 'form')    renderFormView();
    if (wfActiveView === 'json')    renderJsonView();
    if (wfActiveView === 'diagram') renderDiagramView();
}

// --- FORM VIEW ---
function renderFormView() {
    const stepNames = Object.keys(wfModel.steps);

    // Initial step dropdown
    const init = document.getElementById('wfInitial');
    init.innerHTML = stepNames.map(n =>
        `<option value="${esc(n)}" ${n === wfModel.initialStep ? 'selected' : ''}>${esc(n)}</option>`).join('');
    init.onchange = () => { wfModel.initialStep = init.value; };

    // Step cards
    const list = document.getElementById('wfStepsList');
    list.innerHTML = stepNames.map(name => stepCardHtml(name, wfModel.steps[name], stepNames)).join('') || '';

    // Bind events on each card
    list.querySelectorAll('[data-step]').forEach(card => bindStepCard(card));
}

// Known workflow signals shown as plain-language events. The model still
// stores the raw signal name (e.g. "shortlink.clicked"); only the label is
// friendly. An unknown signal already in the spec is preserved as-is.
const WF_SIGNALS = {
    'shortlink.clicked':  '📲 ผู้รับคลิกลิงก์ในข้อความ',
    'sms.sent':           '✅ SMS ถูกส่งออกเรียบร้อย',
    'delivery.confirmed': '📬 ยืนยันส่งถึงเครื่องแล้ว',
};
function wfSigSelectHtml(currentSig) {
    let opts = Object.entries(WF_SIGNALS)
        .map(([v, l]) => `<option value="${v}" ${v === currentSig ? 'selected' : ''}>${l}</option>`)
        .join('');
    if (currentSig && !WF_SIGNALS[currentSig])
        opts += `<option value="${esc(currentSig)}" selected>${esc(currentSig)}</option>`;
    return `<select class="form-select form-select-sm sig-name">${opts}</select>`;
}

function stepCardHtml(name, step, allSteps) {
    const signalRows = Object.entries(step.onSignal || {}).map(([sig, tgt]) => `
        <tr data-sig="${esc(sig)}">
            <td>${wfSigSelectHtml(sig)}</td>
            <td>→ <select class="form-select form-select-sm sig-target">
                ${allSteps.map(s => `<option value="${esc(s)}" ${s===tgt?'selected':''}>${esc(s)}</option>`).join('')}
            </select></td>
            <td><button type="button" class="btn btn-link btn-sm text-danger p-0 sig-del">✕</button></td>
        </tr>`).join('');

    return `
    <div class="card mb-2" data-step="${esc(name)}" data-coupon-batch="${esc(step.couponBatchId||'')}">
      <div class="card-body p-2">
        <div class="row g-2">
          <div class="col-md-4">
            <label class="form-label small">ชื่อขั้นตอน</label>
            <input class="form-control form-control-sm step-name" value="${esc(name)}" />
          </div>
          <div class="col-md-3">
            <label class="form-label small">ขั้นตอนนี้ทำอะไร</label>
            <select class="form-select form-select-sm step-type">
              <option value="send_sms"     ${step.type==='send_sms'    ?'selected':''}>📤 ส่ง SMS</option>
              <option value="issue_coupon" ${step.type==='issue_coupon'?'selected':''}>🎟 ออกคูปอง</option>
              <option value="wait"         ${step.type==='wait'        ?'selected':''}>⏳ รอ (ไม่ส่งอะไร)</option>
              <option value="complete"     ${step.type==='complete'    ?'selected':''}>🏁 จบ workflow</option>
            </select>
          </div>
          <div class="col-md-3 step-wait-col" style="${step.type==='complete'||step.type==='issue_coupon'?'display:none':''}">
            <label class="form-label small">รอ (วัน.ชม:นาที:วินาที)</label>
            <input class="form-control form-control-sm step-wait" value="${esc(step.wait||'')}" placeholder="2.00:00:00" />
          </div>
          <div class="col-md-2 step-maxrep-col" style="${step.type==='complete'||step.type==='issue_coupon'?'display:none':''}">
            <label class="form-label small">ทำซ้ำได้สูงสุด</label>
            <input type="number" min="1" max="20" class="form-control form-control-sm step-maxrep"
                   value="${step.maxRepeats||1}" />
          </div>
          <div class="col-12 step-template-col" style="${step.type==='send_sms'?'':'display:none'}">
            <label class="form-label small">ข้อความ SMS — ใช้ <code>{{ชื่อคอลัมน์}}</code> แทนค่าจากไฟล์ เช่น {{name}} {{url}}</label>
            <textarea class="form-control form-control-sm step-template" rows="2">${esc(step.template||'')}</textarea>
          </div>
          <div class="col-12 step-coupon-col" style="${step.type==='issue_coupon'?'':'display:none'}">
            <label class="form-label small">ชุดคูปองที่จะแจก</label>
            <select class="form-select form-select-sm step-coupon-batch">
              <option value="">— เลือกชุดคูปอง —</option>
            </select>
            <div class="form-text">
              แจกคูปอง 1 ใบต่อผู้รับ 1 คน และส่งค่า
              <code>{{coupon_code}}</code> / <code>{{coupon_url}}</code> ให้ขั้นส่ง SMS ถัดไปใช้
            </div>
          </div>
          <div class="col-12 step-transitions-col" style="${step.type==='complete'?'display:none':''}">
            <label class="form-label small">เงื่อนไข — เมื่อเกิดเหตุการณ์ ให้ข้ามไปขั้นตอนใด</label>
            <table class="table table-sm mb-1">
              <thead><tr><th style="width:52%">เมื่อเกิดเหตุการณ์</th><th>→ ไปขั้นตอน</th><th></th></tr></thead>
              <tbody class="sig-body">${signalRows}</tbody>
            </table>
            <button type="button" class="btn btn-link btn-sm p-0 add-sig">+ เพิ่มเงื่อนไข</button>
            <div class="small mt-1 text-muted">ถ้าไม่มีเหตุการณ์ใดเกิดภายในเวลา "รอ" ข้างบน → ไปขั้นตอน
              <select class="form-select form-select-sm d-inline-block w-auto step-ontimeout">
                <option value="">(จบ — ไม่ทำต่อ)</option>
                ${allSteps.map(s => `<option value="${esc(s)}" ${s===step.onTimeout?'selected':''}>${esc(s)}</option>`).join('')}
              </select>
            </div>
            <div class="small mt-1 text-danger step-repeat-warn" style="display:none">
              <i class="bi bi-exclamation-triangle"></i>
              ตั้ง "ทำซ้ำได้สูงสุด" มากกว่า 1 แต่ timeout ไม่ได้วนกลับมาขั้นนี้ —
              การทำซ้ำจะไม่ทำงาน ให้เลือก timeout → ขั้นนี้
            </div>
          </div>
          <div class="col-12 text-end">
            <button type="button" class="btn btn-link btn-sm text-danger p-0 step-del">
              <i class="bi bi-trash"></i> ลบขั้นตอนนี้
            </button>
          </div>
        </div>
      </div>
    </div>`;
}

function bindStepCard(card) {
    // Populate the coupon-batch picker (cached list loaded once per editor).
    const couponSel = card.querySelector('.step-coupon-batch');
    if (couponSel) {
        const selected = card.dataset.couponBatch || '';
        for (const b of (window._wfCouponBatches || [])) {
            const opt = document.createElement('option');
            opt.value = b.id;
            opt.textContent = `${b.name} (${b.brandName})`;
            if (b.id === selected) opt.selected = true;
            couponSel.appendChild(opt);
        }
    }

    // Type change toggles which fields show
    const typeEl = card.querySelector('.step-type');
    typeEl.addEventListener('change', () => {
        const isComplete = typeEl.value === 'complete';
        const isSms      = typeEl.value === 'send_sms';
        const isCoupon   = typeEl.value === 'issue_coupon';
        // issue_coupon is a pass-through: no wait, no repeats.
        card.querySelector('.step-wait-col').style.display        = (isComplete||isCoupon) ? 'none' : '';
        card.querySelector('.step-maxrep-col').style.display      = (isComplete||isCoupon) ? 'none' : '';
        card.querySelector('.step-template-col').style.display    = isSms ? '' : 'none';
        card.querySelector('.step-coupon-col').style.display      = isCoupon ? '' : 'none';
        card.querySelector('.step-transitions-col').style.display = isComplete ? 'none' : '';
    });

    card.querySelector('.add-sig').addEventListener('click', () => {
        const tbody = card.querySelector('.sig-body');
        const others = Object.keys(wfModel.steps);
        tbody.insertAdjacentHTML('beforeend', `
            <tr data-sig="">
                <td>${wfSigSelectHtml('')}</td>
                <td>→ <select class="form-select form-select-sm sig-target">
                    ${others.map(s => `<option value="${esc(s)}">${esc(s)}</option>`).join('')}
                </select></td>
                <td><button type="button" class="btn btn-link btn-sm text-danger p-0 sig-del">✕</button></td>
            </tr>`);
        // Bind the new delete
        const newRow = tbody.lastElementChild;
        newRow.querySelector('.sig-del').addEventListener('click', () => newRow.remove());
    });
    card.querySelectorAll('.sig-del').forEach(b => b.addEventListener('click', (ev) =>
        ev.target.closest('tr').remove()));
    // Live warning: "ทำซ้ำ > 1" only works when the timeout self-loops.
    const warnEl = card.querySelector('.step-repeat-warn');
    function refreshRepeatWarn() {
        if (!warnEl) return;
        const reps = parseInt(card.querySelector('.step-maxrep')?.value, 10) || 1;
        const tmo  = card.querySelector('.step-ontimeout')?.value || '';
        const self = card.querySelector('.step-name')?.value.trim() || '';
        warnEl.style.display = (reps > 1 && tmo !== self) ? '' : 'none';
    }
    card.querySelector('.step-maxrep')?.addEventListener('input', refreshRepeatWarn);
    card.querySelector('.step-ontimeout')?.addEventListener('change', refreshRepeatWarn);
    card.querySelector('.step-name')?.addEventListener('input', refreshRepeatWarn);
    refreshRepeatWarn();

    card.querySelector('.step-del').addEventListener('click', () => {
        if (!confirm('Delete this step?')) return;
        // Capture current state first so other unsaved edits aren't lost.
        captureFormToModel();
        const stepName = card.dataset.step;
        delete wfModel.steps[stepName];
        if (wfModel.initialStep === stepName) {
            wfModel.initialStep = Object.keys(wfModel.steps)[0] || '';
        }
        renderFormView();
    });
}

document.getElementById('btnWfAddStep').addEventListener('click', () => {
    captureFormToModel();
    let name = 'step', i = 1;
    while (wfModel.steps[name]) name = 'step' + (++i);
    wfModel.steps[name] = { type: 'wait', wait: '1.00:00:00', maxRepeats: 1, onSignal: {} };
    renderFormView();
});

function captureFormToModel() {
    const list = document.getElementById('wfStepsList');
    if (!list || !list.children.length) return;
    const newSteps = {};
    let firstName = null;
    list.querySelectorAll('[data-step]').forEach(card => {
        const oldName = card.dataset.step;
        const newName = card.querySelector('.step-name').value.trim() || oldName;
        if (!firstName) firstName = newName;
        const type = card.querySelector('.step-type').value;
        const step = { type };
        if (type !== 'complete') {
            step.wait = card.querySelector('.step-wait').value.trim() || undefined;
            const mx = parseInt(card.querySelector('.step-maxrep').value, 10);
            if (mx > 0) step.maxRepeats = mx;
            step.onSignal = {};
            card.querySelectorAll('.sig-body tr').forEach(tr => {
                const sig = tr.querySelector('.sig-name').value.trim();
                const tgt = tr.querySelector('.sig-target').value;
                if (sig && tgt) step.onSignal[sig] = tgt;
            });
            const tmo = card.querySelector('.step-ontimeout').value;
            if (tmo) step.onTimeout = tmo;
        }
        if (type === 'send_sms') {
            step.template = card.querySelector('.step-template').value;
        }
        if (type === 'issue_coupon') {
            const bid = card.querySelector('.step-coupon-batch')?.value;
            if (bid) step.couponBatchId = bid;
        }
        newSteps[newName] = step;
    });
    wfModel.steps = newSteps;
    wfModel.name = document.getElementById('wfName').value.trim();
    // Friendly N+unit inputs are the source of truth; the hidden timespan
    // string follows them. Falls back to whatever was in the hidden input
    // (preserves hand-typed exotic values like "0.06:30:00").
    const expN = Number(document.getElementById('wfExpN').value) || 0;
    const expU = document.getElementById('wfExpU').value;
    wfModel.expiration = expN > 0
        ? friendlyToTs(expN, expU)
        : (document.getElementById('wfExp').value.trim() || '60.00:00:00');
    if (!(wfModel.initialStep in newSteps)) wfModel.initialStep = firstName;
}

// ============ QUICK-START WIZARD ============
// Three preset workflow shapes that cover ~90% of real campaigns. Operator
// picks a scenario, fills in the message + waits, and we synthesise the
// step graph so they never touch the JSON spec for simple cases.
(function initWfQuickStart() {
    const openBtn   = document.getElementById('btnWfQuickOpen');
    const form      = document.getElementById('wfQuickForm');
    const genBtn    = document.getElementById('btnWfQuickGen');
    const cancelBtn = document.getElementById('btnWfQuickCancel');
    const scenario  = document.getElementById('qsScenario');
    const bodyEl    = document.getElementById('qsBody');
    const escBodyEl = document.getElementById('qsEscBody');
    const countEl   = document.getElementById('qsBodyCount');
    if (!openBtn) return;

    function toggleScenarioFields() {
        const isOnce     = scenario.value === 'once';
        const isEscalate = scenario.value === 'escalate';
        const isDrip     = scenario.value === 'drip';
        document.querySelectorAll('.qs-not-once').forEach(el =>
            el.style.display = (isOnce || isDrip) ? 'none' : '');
        document.querySelectorAll('.qs-escalate-only').forEach(el =>
            el.style.display = isEscalate ? '' : 'none');
        document.querySelectorAll('.qs-drip-only').forEach(el =>
            el.style.display = isDrip ? '' : 'none');
        if (isDrip && document.getElementById('qsDripList').children.length === 0) {
            // Seed two initial stages so the new scenario isn't empty.
            addDripStage(2, 'hours', 'Hi {{name}}, visit {{url}}');
            addDripStage(5, 'days',  'Reminder {{attempt}}: {{name}}, please open {{url}}');
        }
    }
    scenario.addEventListener('change', toggleScenarioFields);

    // Drip stages — each row is { n, unit, template }. JS keeps them in a
    // tiny in-DOM list; generation reads them in document order.
    function addDripStage(n = 2, unit = 'hours', template = '') {
        const list = document.getElementById('qsDripList');
        const idx = list.children.length + 1;
        const row = document.createElement('div');
        row.className = 'card mb-2';
        row.innerHTML = `
            <div class="card-body p-2">
                <div class="d-flex justify-content-between mb-1">
                    <strong class="small">Stage ${idx}</strong>
                    <button type="button" class="btn-close btn-sm" aria-label="Remove stage"></button>
                </div>
                <div class="d-flex gap-1 align-items-center mb-1">
                    <span class="small text-muted">wait</span>
                    <input type="number" min="1" max="999" value="${n}"
                           class="form-control form-control-sm drip-n" style="width:80px" />
                    <select class="form-select form-select-sm drip-u" style="width:auto">
                        <option value="minutes"${unit==='minutes'?' selected':''}>minutes</option>
                        <option value="hours"  ${unit==='hours'  ?' selected':''}>hours</option>
                        <option value="days"   ${unit==='days'   ?' selected':''}>days</option>
                    </select>
                    <span class="small text-muted">then send</span>
                </div>
                <textarea class="form-control form-control-sm drip-template" rows="2"
                          placeholder="Use {{name}}, {{url}}, {{attempt}}…">${esc(template)}</textarea>
            </div>`;
        row.querySelector('.btn-close').addEventListener('click', () => {
            row.remove();
            // Renumber labels.
            list.querySelectorAll('strong').forEach((el, i) => el.textContent = `Stage ${i + 1}`);
        });
        list.appendChild(row);
    }
    document.getElementById('btnQsDripAdd').addEventListener('click', () => addDripStage());

    openBtn.addEventListener('click', () => {
        form.style.display = '';
        openBtn.style.display = 'none';
        toggleScenarioFields();
        setTimeout(() => bodyEl.focus(), 100);
    });
    cancelBtn.addEventListener('click', () => {
        form.style.display = 'none';
        openBtn.style.display = '';
    });

    // Placeholder helper buttons — inserts at cursor in the focused textarea.
    document.getElementById('qsPlaceholders').addEventListener('click', (ev) => {
        const btn = ev.target.closest('button[data-ph]');
        if (!btn) return;
        const ph = btn.dataset.ph;
        // Insert into whichever message textarea has focus, default to main body.
        const target = (document.activeElement === escBodyEl) ? escBodyEl : bodyEl;
        const start = target.selectionStart ?? target.value.length;
        const end   = target.selectionEnd ?? target.value.length;
        target.value = target.value.slice(0, start) + ph + target.value.slice(end);
        target.focus();
        target.selectionStart = target.selectionEnd = start + ph.length;
        bodyEl.dispatchEvent(new Event('input'));
    });

    // SMS body char counter — encourages keeping the body under 160.
    bodyEl.addEventListener('input', () => {
        const n = bodyEl.value.length;
        countEl.textContent = n;
        countEl.className = n > 320 ? 'text-danger' : n > 160 ? 'text-warning' : '';
    });

    // Preview — server renders with sample values so the operator sees both
    // the final body and any placeholder typos that didn't resolve.
    document.getElementById('btnQsPreview').addEventListener('click', async () => {
        const out = document.getElementById('qsPreviewOut');
        out.classList.remove('d-none');
        out.textContent = 'Rendering…';
        try {
            const r = await api.post(`${api_proj}/workflows/preview`, {
                template: bodyEl.value, sample: null
            });
            const partsLine = `${r.charCount} chars · ${r.smsParts} SMS part${r.smsParts === 1 ? '' : 's'}`;
            const missingLine = r.missingPlaceholders.length
                ? `\n\n⚠ Unresolved placeholders: ${r.missingPlaceholders.join(', ')}`
                : '';
            out.textContent = `${partsLine}\n\n${r.rendered}${missingLine}`;
        } catch (e) { out.textContent = e.message; }
    });

    genBtn.addEventListener('click', () => {
        const body = bodyEl.value.trim();
        if (!body) { toast('Enter an SMS body first.', 'warning'); bodyEl.focus(); return; }
        const wait = friendlyToTs(
            Number(document.getElementById('qsWaitN').value) || 2,
            document.getElementById('qsWaitU').value);
        const exp = friendlyToTs(
            Number(document.getElementById('qsExpN').value) || 60,
            document.getElementById('qsExpU').value);
        const retries = Math.max(1, Math.min(10,
            parseInt(document.getElementById('qsMaxRetries').value, 10) || 3));

        switch (scenario.value) {
            case 'once':
                // One-shot send. Workflow completes the moment the SMS leaves.
                wfModel = {
                    name: wfModel.name || 'send-once',
                    expiration: exp,
                    initialStep: 'send',
                    steps: {
                        send: {
                            type: 'send_sms',
                            template: body,
                            wait: '00:01:00',  // brief settling window for provider DLR
                            maxRepeats: 1,
                            onSignal: { 'sms.sent': 'done' },
                            onTimeout: 'done'
                        },
                        done: { type: 'complete' }
                    }
                };
                break;
            case 'escalate': {
                const esc = (escBodyEl.value || body).trim();
                wfModel = {
                    name: wfModel.name || 'send-then-escalate',
                    expiration: exp,
                    initialStep: 'send',
                    steps: {
                        send: {
                            type: 'send_sms',
                            template: body,
                            wait,
                            maxRepeats: 1,
                            onSignal: { 'shortlink.clicked': 'done' },
                            onTimeout: 'escalate'
                        },
                        escalate: {
                            // Self-loops so maxRepeats actually repeats — the
                            // escalation message is re-sent up to retries times.
                            type: 'send_sms',
                            template: esc,
                            wait,
                            maxRepeats: retries,
                            onSignal: { 'shortlink.clicked': 'done' },
                            onTimeout: 'escalate'
                        },
                        done: { type: 'complete' }
                    }
                };
                break;
            }
            case 'drip': {
                // Chain of N stages — stage[i] waits then sends template[i],
                // exiting any stage on shortlink.clicked. Last stage hands
                // off to 'done'.
                const stages = Array.from(document.querySelectorAll('#qsDripList .card'))
                    .map(card => ({
                        n: Number(card.querySelector('.drip-n').value) || 1,
                        unit: card.querySelector('.drip-u').value,
                        template: card.querySelector('.drip-template').value.trim()
                    }))
                    .filter(s => s.template);
                if (stages.length === 0) {
                    toast('Add at least one drip stage.', 'warning');
                    return;
                }
                const steps = { done: { type: 'complete' } };
                stages.forEach((s, i) => {
                    const name = 'stage' + (i + 1);
                    const next = i + 1 < stages.length ? 'stage' + (i + 2) : 'done';
                    steps[name] = {
                        type: 'send_sms',
                        template: s.template,
                        wait: friendlyToTs(s.n, s.unit),
                        maxRepeats: 1,
                        onSignal: { 'shortlink.clicked': 'done' },
                        onTimeout: next
                    };
                });
                wfModel = {
                    name: wfModel.name || `drip-${stages.length}-stage`,
                    expiration: exp,
                    initialStep: 'stage1',
                    steps
                };
                break;
            }
            default: // 'reminder'
                wfModel = {
                    name: wfModel.name || 'send-reminder',
                    expiration: exp,
                    initialStep: 'send',
                    steps: {
                        send: {
                            // Self-loops up to retries times when nothing happens
                            // (onTimeout points back at THIS step — that is what
                            // makes maxRepeats actually repeat). Exits early on a
                            // click signal; the engine completes the instance once
                            // the repeats are exhausted.
                            type: 'send_sms',
                            template: body,
                            wait,
                            maxRepeats: retries,
                            onSignal: { 'shortlink.clicked': 'done' },
                            onTimeout: 'send'
                        },
                        done: { type: 'complete' }
                    }
                };
        }

        renderWf();
        form.style.display = 'none';
        openBtn.style.display = '';
        toast('Workflow drafted from template — review and save.');
    });
})();

// --- JSON VIEW ---
function renderJsonView() {
    const spec = {
        initialStep: wfModel.initialStep,
        expiration:  wfModel.expiration,
        steps:       wfModel.steps
    };
    document.getElementById('wfSpec').value = JSON.stringify(spec, null, 2);
}
function captureJsonToModel() {
    const spec = JSON.parse(document.getElementById('wfSpec').value);
    if (!spec || typeof spec !== 'object') throw new Error('Spec must be an object.');
    if (!spec.steps || typeof spec.steps !== 'object') throw new Error('Spec.steps required.');
    wfModel.name        = document.getElementById('wfName').value.trim();
    wfModel.expiration  = spec.expiration ?? '60.00:00:00';
    wfModel.initialStep = spec.initialStep;
    wfModel.steps       = spec.steps;
}

// --- DIAGRAM VIEW (Mermaid) ---
let mermaidCounter = 0;
async function renderDiagramView() {
    const out = document.getElementById('wfDiagram');
    if (!Object.keys(wfModel.steps).length) {
        out.innerHTML = '<p class="text-muted small">No steps to render.</p>';
        return;
    }
    const mermaid = window.mermaid;
    if (!mermaid) { out.textContent = 'Mermaid library not loaded.'; return; }
    mermaid.initialize({ startOnLoad: false, theme: 'default' });

    // Build a state diagram from the model.
    const lines = ['stateDiagram-v2'];
    if (wfModel.initialStep) lines.push(`    [*] --> ${safeId(wfModel.initialStep)}`);
    for (const [name, s] of Object.entries(wfModel.steps)) {
        const id = safeId(name);
        if (s.type === 'complete') {
            lines.push(`    ${id} --> [*]`);
            continue;
        }
        for (const [sig, tgt] of Object.entries(s.onSignal || {})) {
            lines.push(`    ${id} --> ${safeId(tgt)} : ${sig}`);
        }
        if (s.onTimeout) {
            lines.push(`    ${id} --> ${safeId(s.onTimeout)} : timeout`);
        }
    }
    const src = lines.join('\n');
    try {
        const { svg } = await mermaid.render('wf-d-' + (++mermaidCounter), src);
        out.innerHTML = svg;
    } catch (e) {
        out.innerHTML =
            `<div class="text-danger small">Diagram error: ${esc(e.message)}</div>` +
            `<pre class="small">${esc(src)}</pre>`;
    }
}
function safeId(name) {
    // Mermaid state names: alphanumeric + underscore. Replace anything else.
    return (name || 'unnamed').replace(/[^A-Za-z0-9_]/g, '_');
}

// --- Save handler ---
// Captures the editor into a {name, spec} payload, or null if invalid
// (a toast explains why).
function wfBuildPayload() {
    if (wfActiveView === 'form') captureFormToModel();
    else if (wfActiveView === 'json') captureJsonToModel();
    const spec = {
        initialStep: wfModel.initialStep,
        expiration:  wfModel.expiration,
        steps:       wfModel.steps
    };
    if (!wfModel.name) { toast('Workflow name is required.', 'warning'); return null; }
    if (!spec.initialStep || !(spec.initialStep in spec.steps)) {
        toast('Initial step must reference an existing step.', 'warning'); return null;
    }
    return { name: wfModel.name, spec };
}

document.getElementById('btnWfSave').addEventListener('click', async () => {
    try {
        const payload = wfBuildPayload();
        if (!payload) return;
        const r = await api.post(`${api_proj}/workflows`, payload);
        wfEditingId = r.id;   // keep editing — further saves can Update this version
        wfRefreshButtons();
        toast(`Saved v${r.version}. Activate it from the list.`);
        loadWorkflows();
    } catch (e) { toast('Save failed: ' + e.message, 'danger'); }
});

document.getElementById('btnWfUpdate').addEventListener('click', async () => {
    if (!wfEditingId) return;
    try {
        const payload = wfBuildPayload();
        if (!payload) return;
        const r = await api.put(`${api_proj}/workflows/${wfEditingId}`, payload);
        toast(`Updated "${r.name}" v${r.version} in place.`);
        loadWorkflows();
    } catch (e) { toast('Update failed: ' + e.message, 'danger'); }
});

// Render an empty model on first paint of the workflows tab
document.querySelector('a[href="#tab-workflows"]').addEventListener('shown.bs.tab', () => {
    if (!loaded.wfRendered) { loaded.wfRendered = true; renderWf(); }
});

// ==================== SMS ====================
document.getElementById('formSms').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const out = document.getElementById('smsResult');
    const btn = ev.target.querySelector('button[type=submit]');
    out.classList.add('d-none');
    btn.disabled = true;   // guard against an accidental double-click
    try {
        const sched = document.getElementById('smsSched').value;
        const r = await api.post(`${api_proj}/sms/send`, {
            recipient: document.getElementById('smsTo').value.trim(),
            body:      document.getElementById('smsBody').value,
            senderId:  document.getElementById('smsFrom').value.trim() || null,
            scheduledFor: sched ? new Date(sched).toISOString() : null
        });
        const st = smsStatusName(r.status);
        const accepted = st === 'Sent' || st === 'Delivered';
        const scheduled = !!sched && st === 'Queued';

        out.classList.remove('d-none');
        out.className = 'mt-3 small alert py-2 '
            + (accepted ? 'alert-success' : scheduled ? 'alert-info' : 'alert-warning');
        out.textContent = accepted
            ? `Sent to provider — message id ${r.providerMessageId || '—'}. `
              + 'Note: this confirms the gateway accepted it, not handset delivery.'
            : scheduled
                ? 'Scheduled — it will be dispatched at the chosen time.'
                : `Not accepted — status ${st}${r.errorCode ? ' (' + r.errorCode + ')' : ''}.`;

        toast(accepted ? 'SMS dispatched.'
              : scheduled ? 'SMS scheduled.'
              : `SMS not accepted: ${r.errorCode || st}`,
              accepted || scheduled ? 'success' : 'warning');

        // Clear the body so it is visually obvious the message went out;
        // keep the recipient/sender for a quick repeat send.
        if (accepted || scheduled) {
            document.getElementById('smsBody').value = '';
            document.getElementById('smsSched').value = '';
        }
        loadSmsList();
    } catch (e) {
        out.classList.remove('d-none');
        out.className = 'mt-3 small alert alert-danger py-2';
        out.textContent = e.message;
        toast(e.message, 'danger');
    } finally {
        btn.disabled = false;
    }
});

// Shared pager bar — renders Prev/Next + "page X of Y" into `el`.
// data = { page, pageSize, total, totalPages }; onGo(page) reloads.
window.renderPager = function (el, data, onGo) {
    if (!el) return;
    const tp = data.totalPages || 0;
    if (!data.total) { el.innerHTML = '<span class="small text-muted">ไม่มีข้อมูล</span>'; return; }
    el.innerHTML =
        `<button class="btn btn-outline-secondary btn-sm" ${data.page<=1?'disabled':''} data-pg="prev">&laquo; ก่อนหน้า</button>
         <button class="btn btn-outline-secondary btn-sm" ${data.page>=tp?'disabled':''} data-pg="next">ถัดไป &raquo;</button>
         <span class="small text-muted">หน้า ${data.page} / ${tp} — ${data.total} รายการ</span>`;
    const prev = el.querySelector('[data-pg=prev]'), next = el.querySelector('[data-pg=next]');
    if (prev) prev.addEventListener('click', () => { if (data.page > 1)  onGo(data.page - 1); });
    if (next) next.addEventListener('click', () => { if (data.page < tp) onGo(data.page + 1); });
};

let smsPage = 1;
async function loadSmsList() {
    try {
        const filter   = document.getElementById('smsFilter').value;
        const pageSize = document.getElementById('smsPageSize').value;
        const qs = new URLSearchParams({ page: smsPage, pageSize: pageSize });
        if (filter) qs.set('status', filter);
        const data = await api.get(`${api_proj}/sms?` + qs);
        const rows = data.items || [];
        const body = document.getElementById('smsListBody');
        renderPager(document.getElementById('smsPager'), data,
            p => { smsPage = p; loadSmsList(); });
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="6" class="text-muted text-center py-4"><i class="bi bi-send fs-3 d-block"></i>No SMS sent yet. Workflow dispatches go here, plus manual sends from the form on the left.</td></tr>';
            return;
        }
        body.innerHTML = rows.map(s => {
            const status = smsStatusName(s.status);
            const c = SMS_STATUS_COLOR[status] || 'secondary';
            // Only failed-class states are retryable. Sent/Delivered intentionally
            // can't be retried — that would be a duplicate send.
            const retryable = ['Failed','Rejected','Expired'].includes(status);
            const retryBtn = retryable
                ? `<button class="btn btn-sm btn-link p-0 text-warning"
                       title="Retry this message" aria-label="Retry message"
                       onclick="event.stopPropagation(); retrySms('${esc(s.id)}')">
                       <i class="bi bi-arrow-clockwise" aria-hidden="true"></i></button>`
                : '';
            const cb = retryable
                ? `<input type="checkbox" class="sms-row-check" value="${esc(s.id)}"
                          onclick="event.stopPropagation()"
                          aria-label="Select message for bulk action" />`
                : '';
            // "Sent" only means the provider gateway accepted the message —
            // handset delivery is confirmed separately by a DN. Make that
            // explicit so operators don't read Sent as "delivered".
            const sentUnconfirmed = status === 'Sent' && !s.deliveredAt;
            const statusTitle = status === 'Sent'
                ? ' title="Accepted by the provider gateway. Delivery to the handset is confirmed separately by a delivery notification (DN)."'
                : '';
            return `<tr style="cursor:pointer" onclick="showSmsDetail('${esc(s.id)}')">
                <td>${cb}</td>
                <td class="small">${fmtDate(s.createdAt)}</td>
                <td><code class="small">${esc(s.maskedTo)}</code></td>
                <td class="small">${esc(s.provider)}</td>
                <td><span class="badge bg-${c}"${statusTitle}>${esc(status)}</span>
                    ${sentUnconfirmed ? '<small class="text-muted ms-1">delivery unconfirmed</small>' : ''}
                    ${s.errorCode ? `<small class="text-danger ms-1">${esc(s.errorCode)}</small>` : ''}
                </td>
                <td>${s.attempts} ${retryBtn}</td>
            </tr>`;
        }).join('');
        updateSmsBulkBar();
    } catch (e) { toast(e.message, 'danger'); }
}

window.retrySms = async function (id) {
    if (!confirm('Re-send this message? Attempt counter resets to zero.')) return;
    try {
        const r = await api.post(`${api_proj}/sms/${id}/retry`, {});
        const st = smsStatusName(r.status);
        toast(`Retry ${st === 'Sent' || st === 'Delivered' ? 'succeeded' : 'attempted'}: ${st}`,
              st === 'Failed' || st === 'Rejected' ? 'warning' : 'success');
        loadSmsList();
    } catch (e) { toast(e.message, 'danger'); }
};

// ============ SMS BULK ACTIONS ============
function updateSmsBulkBar() {
    const checks = document.querySelectorAll('.sms-row-check:checked');
    const bar = document.getElementById('smsBulkBar');
    document.getElementById('smsBulkCount').textContent = `${checks.length} selected`;
    bar.classList.toggle('d-none', checks.length === 0);
}

document.addEventListener('change', (ev) => {
    if (ev.target.classList?.contains('sms-row-check')) updateSmsBulkBar();
    if (ev.target.id === 'smsSelectAll') {
        document.querySelectorAll('.sms-row-check').forEach(cb => cb.checked = ev.target.checked);
        updateSmsBulkBar();
    }
});

document.getElementById('btnSmsBulkRetry')?.addEventListener('click', async () => {
    const ids = Array.from(document.querySelectorAll('.sms-row-check:checked')).map(cb => cb.value);
    if (!ids.length) return;
    const ok = await window.confirmAction({
        title: 'Retry selected', mode: 'math',
        message: `Re-send <strong>${ids.length}</strong> message${ids.length === 1 ? '' : 's'}? Each attempt counter resets to zero.`,
        okLabel: 'Retry all'
    });
    if (!ok) return;
    let success = 0, fail = 0;
    for (const id of ids) {
        try { await api.post(`${api_proj}/sms/${id}/retry`, {}); success++; }
        catch { fail++; }
    }
    toast(`Bulk retry: ${success} ok${fail ? `, ${fail} failed` : ''}.`, fail ? 'warning' : 'success');
    loadSmsList();
});

window.showSmsDetail = async function (id) {
    const body = document.getElementById('smsDetailBody');
    body.textContent = 'Loading…';
    new bootstrap.Modal(document.getElementById('smsDetailModal')).show();
    try {
        const d = await api.get(`${api_proj}/sms/${id}`);
        body.textContent = JSON.stringify(d, null, 2);
    } catch (e) { body.textContent = 'Error: ' + e.message; }
};
document.getElementById('btnSmsRefresh').addEventListener('click', loadSmsList);
// Filter / page-size change → back to page 1.
document.getElementById('smsFilter').addEventListener('change', () => { smsPage = 1; loadSmsList(); });
document.getElementById('smsPageSize').addEventListener('change', () => { smsPage = 1; loadSmsList(); });
document.getElementById('btnSmsExport').addEventListener('click', () => {
    const filter = document.getElementById('smsFilter').value;
    const qs = filter ? '?status=' + encodeURIComponent(filter) : '';
    window.open(`${api_proj}/sms/export.csv${qs}`, '_blank');
});

// ==================== AUTO-POLLING ====================
// Light-touch real-time: poll the active list every 10s while:
//   (a) the relevant tab is shown, AND
//   (b) the browser tab itself is visible (no work if user switched away)
// Mirrors the cheapest possible SignalR-equivalent without WebSocket overhead.
const POLL_MS = 10_000;
const pollers = {};

function startPoll(name, fn) {
    if (pollers[name]) return;
    pollers[name] = setInterval(() => {
        if (document.visibilityState === 'visible') fn();
    }, POLL_MS);
}
function stopPoll(name) {
    if (pollers[name]) { clearInterval(pollers[name]); pollers[name] = null; }
}

document.querySelectorAll('[data-bs-toggle="tab"]').forEach(el => {
    el.addEventListener('shown.bs.tab', (ev) => {
        // Stop every poller, then start the one(s) for the visible tab.
        Object.keys(pollers).forEach(stopPoll);
        const target = ev.target.getAttribute('href');
        if (target === '#tab-sms')     startPoll('sms',     loadSmsList);
        if (target === '#tab-sources') startPoll('batches', loadBatches);
    });
});
// Stop polling when user backgrounds the tab (saves bandwidth + DB hits).
document.addEventListener('visibilitychange', () => {
    if (document.visibilityState !== 'visible') Object.keys(pollers).forEach(stopPoll);
});

// ==================== SHORTLINKS ====================
let slPage = 1;
async function loadShortlinks() {
    try {
        const data = await api.get(`${api_proj}/shortlinks?page=${slPage}&pageSize=50`);
        const rows = data.items || [];
        const body = document.getElementById('slBody');
        renderPager(document.getElementById('slPager'), data,
            p => { slPage = p; loadShortlinks(); });
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="5" class="text-muted text-center py-4"><i class="bi bi-link-45deg fs-3 d-block"></i>No shortlinks yet. They&rsquo;re created automatically when a workflow SMS template contains a long URL (or via the form on the right).</td></tr>';
            return;
        }
        body.innerHTML = rows.map(s => `
            <tr>
                <td>${s.fullUrl
                    ? `<a href="${esc(s.fullUrl)}" target="_blank" rel="noopener"
                          class="text-decoration-none">${esc(s.fullUrl)}</a>`
                    : `<code>${esc(s.slug)}</code>`}</td>
                <td>
                    ${s.clickCount}${s.maxClicks ? ' / ' + s.maxClicks : ''}
                    ${s.clickCount > 0
                        ? `<button class="btn btn-link btn-sm p-0 ms-1"
                                   onclick="showSlClicks('${esc(s.id)}')">history</button>`
                        : ''}
                </td>
                <td>${s.expiresAt ? fmtDate(s.expiresAt) : '—'}</td>
                <td>${s.disabled ? '<span class="text-danger">yes</span>' : ''}</td>
                <td>${s.disabled ? '' :
                    `<button class="btn btn-link btn-sm text-danger p-0"
                             onclick="disableSl('${esc(s.id)}')">Disable</button>`}</td>
            </tr>`).join('');
    } catch (e) { toast(e.message, 'danger'); }
}
document.getElementById('btnSlExport').addEventListener('click', () => {
    window.open(`${api_proj}/shortlinks/export.csv`, '_blank');
});
document.getElementById('btnSlImport').addEventListener('click', () =>
    document.getElementById('slImportFile').click());
document.getElementById('slImportFile').addEventListener('change', async (ev) => {
    const f = ev.target.files[0];
    if (!f) return;
    const out = document.getElementById('slImportResult');
    out.textContent = 'กำลังนำเข้า…';
    const fd = new FormData();
    fd.append('file', f);
    try {
        // Raw fetch — api.post forces application/json; FormData needs the
        // browser to set the multipart boundary.
        const resp = await fetch(`${api_proj}/shortlinks/import`, {
            method: 'POST', body: fd, credentials: 'include'
        });
        const d = await resp.json().catch(() => ({}));
        if (!resp.ok) {
            out.innerHTML = `<span class="text-danger">${esc(d.message || 'นำเข้าไม่สำเร็จ')}</span>`;
            return;
        }
        out.innerHTML = `<span class="text-success">นำเข้าสำเร็จ ${d.created} ลิงก์</span>`
            + (d.failed ? ` <span class="text-danger">ล้มเหลว ${d.failed} (ดูสาเหตุในไฟล์)</span>` : '');
        loadShortlinks();
    } catch (e) { out.innerHTML = `<span class="text-danger">${esc(e.message)}</span>`; }
    finally { ev.target.value = ''; }
});

window.disableSl = async function (id) {
    if (!confirm('Disable this shortlink?')) return;
    try {
        await api.post(`${api_proj}/shortlinks/${id}/disable`);
        toast('Disabled.');
        loadShortlinks();
    } catch (e) { toast(e.message, 'danger'); }
};

window.showSlClicks = async function (id) {
    const body = document.getElementById('slClicksBody');
    body.innerHTML = '<tr><td colspan="4" class="text-muted">Loading…</td></tr>';
    new bootstrap.Modal(document.getElementById('slClicksModal')).show();
    try {
        const rows = await api.get(`${api_proj}/shortlinks/${id}/clicks?take=200`);
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="4" class="text-muted">No clicks yet.</td></tr>';
            return;
        }
        body.innerHTML = rows.map(c => `
            <tr>
                <td class="small">${fmtDate(c.clickedAt)}</td>
                <td class="small">${esc(c.deviceClass||'unknown')}</td>
                <td class="small">${esc(c.country||'')}</td>
                <td class="small text-muted text-truncate" style="max-width:380px">${esc(c.userAgent||'')}</td>
            </tr>`).join('');
    } catch (e) {
        body.innerHTML = `<tr><td colspan="4" class="text-danger">${esc(e.message)}</td></tr>`;
    }
};
document.getElementById('formSlCreate').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    try {
        const days = document.getElementById('slDays').value;
        const max  = document.getElementById('slMax').value;
        const r = await api.post(`${api_proj}/shortlinks`, {
            targetUrl: document.getElementById('slUrl').value.trim(),
            lifetime:  days ? `${days}.00:00:00` : null,
            maxClicks: max  ? parseInt(max, 10)  : null
        });
        document.getElementById('slCreated').innerHTML =
            `<div class="alert alert-success small mb-0">Created slug: <code>${esc(r.slug)}</code></div>`;
        toast('Shortlink created.');
        loadShortlinks();
    } catch (e) { toast(e.message, 'danger'); }
});

// ==================== REPORTS ====================
function defaultRange() {
    const to = new Date();
    const from = new Date(); from.setDate(from.getDate() - 30);
    document.getElementById('rptFrom').value = from.toISOString().slice(0,10);
    document.getElementById('rptTo').value   = to.toISOString().slice(0,10);
    document.getElementById('audFrom').value = from.toISOString().slice(0,10);
    document.getElementById('audTo').value   = to.toISOString().slice(0,10);
}
defaultRange();

document.getElementById('btnRpt').addEventListener('click', async () => {
    const kind = document.getElementById('rptKind').value;
    const from = document.getElementById('rptFrom').value;
    const to   = document.getElementById('rptTo').value;
    const qs   = `?from=${from}T00:00:00Z&to=${to}T23:59:59Z`;
    try {
        const data = await api.get(`${api_proj}/reports/${kind}${qs}`);
        renderReport(kind, data);
    } catch (e) { toast(e.message, 'danger'); }
});
document.getElementById('btnCsv').addEventListener('click', () => {
    const kind = document.getElementById('rptKind').value;
    const from = document.getElementById('rptFrom').value;
    const to   = document.getElementById('rptTo').value;
    const qs   = `?from=${from}T00:00:00Z&to=${to}T23:59:59Z`;
    document.getElementById('btnCsv').href = `${api_proj}/reports/${kind}/export.csv${qs}`;
});

function renderReport(kind, data) {
    const out = document.getElementById('rptOut');
    if (Array.isArray(data) && !data.length) {
        out.innerHTML = '<div class="card-body text-muted small">No data in this range.</div>';
        return;
    }
    if (!Array.isArray(data)) data = [data];
    const cols = Object.keys(data[0]);
    out.innerHTML = `
      <div class="table-responsive">
        <table class="table table-sm mb-0">
          <thead><tr>${cols.map(c => `<th>${esc(c)}</th>`).join('')}</tr></thead>
          <tbody>${data.map(r => `<tr>${cols.map(c =>
              `<td>${typeof r[c] === 'number' && r[c] < 1 && r[c] > 0
                  ? (r[c]*100).toFixed(1) + '%'
                  : esc(String(r[c] ?? ''))}</td>`).join('')}</tr>`).join('')}
          </tbody>
        </table>
      </div>`;
}

// ==================== AUDIT ====================
// Wire presets once on first DOM ready — the buttons drive audFrom/audTo and
// fire 'rangechange' which we hook below to auto-reload.
window.initDateRangePresets('#audRangeGroup', '#audFrom', '#audTo');
document.getElementById('audFrom').addEventListener('rangechange', () => {
    // Only auto-reload if we've already loaded once (i.e. user has clicked
    // Load at least once). Otherwise wait for the button click.
    if (document.getElementById('audBody').dataset.loaded === '1') {
        document.getElementById('btnAud').click();
    }
});

// Cached audit rows so the dropdowns can filter without a re-fetch.
let _auditRows = [];
function renderAudit() {
    const userFilter   = document.getElementById('audUserPick').value;
    const actionFilter = document.getElementById('audActionPick').value;
    const body         = document.getElementById('audBody');
    const filtered = _auditRows.filter(r =>
        (!userFilter   || (r.userEmail || r.userId) === userFilter) &&
        (!actionFilter || r.action === actionFilter));
    if (!filtered.length) {
        body.innerHTML = '<tr><td colspan="5" class="text-muted">No audit entries match filters.</td></tr>';
        return;
    }
    body.innerHTML = filtered.map(r => `
        <tr>
            <td class="small">${fmtDate(r.at)}</td>
            <td><span class="small">${esc(r.userEmail || (r.userId||'').slice(0,8))}</span></td>
            <td><code class="small">${esc(r.action)}</code></td>
            <td class="small">${esc(r.entityType)}#${esc((r.entityId||'').slice(0,8))}</td>
            <td class="small text-muted">${esc(r.ipAddress||'')}</td>
        </tr>`).join('');
    window.initTable(document.getElementById('tab-audit'));
}

document.getElementById('btnAud').addEventListener('click', async () => {
    const from = document.getElementById('audFrom').value;
    const to   = document.getElementById('audTo').value;
    try {
        _auditRows = await api.get(`${api_proj}/reports/audit-trail` +
            `?from=${from}T00:00:00Z&to=${to}T23:59:59Z&take=500`);
        document.getElementById('audBody').dataset.loaded = '1';

        // Populate filter dropdowns from the loaded set — no extra endpoint
        // needed. Dedup + sort alphabetically; preserves any current selection.
        const userSel   = document.getElementById('audUserPick');
        const actionSel = document.getElementById('audActionPick');
        const prevUser   = userSel.value;
        const prevAction = actionSel.value;
        const users   = [...new Set(_auditRows.map(r => r.userEmail || r.userId).filter(Boolean))].sort();
        const actions = [...new Set(_auditRows.map(r => r.action).filter(Boolean))].sort();
        userSel.innerHTML   = '<option value="">All users</option>'   + users.map(u   => `<option ${u===prevUser?'selected':''}>${esc(u)}</option>`).join('');
        actionSel.innerHTML = '<option value="">All actions</option>' + actions.map(a => `<option ${a===prevAction?'selected':''}>${esc(a)}</option>`).join('');

        renderAudit();
    } catch (e) { toast(e.message, 'danger'); }
});
document.getElementById('audUserPick').addEventListener('change', renderAudit);
document.getElementById('audActionPick').addEventListener('change', renderAudit);

// ==================== PROVIDER CREDENTIALS ====================
// Loaded the first time the SMS tab is shown (alongside loadSmsList). The GET
// returns metadata + masked indicators only — no plaintext secrets — so we
// surface "stored" badges and a leave-blank-to-keep hint.
async function loadProviderConfig(provider) {
    try {
        const r = await api.get(`${api_proj}/sms-providers/${provider}`);
        if (provider === 'etracker') {
            document.getElementById('etBaseUrl').value = r.baseUrl ?? '';
            document.getElementById('etUser').value    = r.username ?? '';
            document.getElementById('etSender').value  = r.defaultSenderId ?? '';
            document.getElementById('etServid').value  = r.serviceId ?? '';
            document.getElementById('etType').value    = r.defaultType ?? '';
            document.getElementById('etPwdMark').classList.toggle('d-none', !r.passwordSet);
            document.getElementById('etStatus').textContent = r.hasOverride
                ? `Override active · updated ${fmtDate(r.updatedAt)}`
                : 'No override — using global defaults.';
        } else {
            document.getElementById('ibBaseUrl').value = r.baseUrl ?? '';
            document.getElementById('ibSender').value  = r.defaultSenderId ?? '';
            document.getElementById('ibKeyMark').classList.toggle('d-none', !r.apiKeySet);
            document.getElementById('ibStatus').textContent = r.hasOverride
                ? `Override active · updated ${fmtDate(r.updatedAt)}`
                : 'No override — using global defaults.';
        }
    } catch (e) { toast(e.message, 'danger'); }
}

document.getElementById('formProvEtracker').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const body = {
        baseUrl:         document.getElementById('etBaseUrl').value || null,
        username:        document.getElementById('etUser').value    || null,
        password:        document.getElementById('etPwd').value     || null,
        defaultSenderId: document.getElementById('etSender').value  || null,
        serviceId:       document.getElementById('etServid').value  || null,
        defaultType:     document.getElementById('etType').value    || null
    };
    try {
        await api.put(`${api_proj}/sms-providers/etracker`, body);
        document.getElementById('etPwd').value = '';
        toast('MacroKiosk credentials saved.');
        await loadProviderConfig('etracker');
    } catch (e) { toast(e.message, 'danger'); }
});

document.getElementById('formProvInfobip').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const body = {
        baseUrl:         document.getElementById('ibBaseUrl').value || null,
        apiKey:          document.getElementById('ibKey').value     || null,
        defaultSenderId: document.getElementById('ibSender').value  || null
    };
    try {
        await api.put(`${api_proj}/sms-providers/infobip`, body);
        document.getElementById('ibKey').value = '';
        toast('Infobip credentials saved.');
        await loadProviderConfig('infobip');
    } catch (e) { toast(e.message, 'danger'); }
});

async function testProvider(provider, btnId, statusId) {
    const btn = document.getElementById(btnId);
    const out = document.getElementById(statusId);
    btn.disabled = true;
    out.className = 'ms-auto small text-muted';
    out.textContent = 'Testing…';
    try {
        const r = await api.post(`${api_proj}/sms-providers/${provider}/test`, {});
        out.textContent = r.message;
        out.className = 'ms-auto small ' + (r.ok ? 'text-success' : 'text-danger');
    } catch (e) {
        out.textContent = e.message;
        out.className = 'ms-auto small text-danger';
    } finally {
        btn.disabled = false;
    }
}
document.getElementById('btnEtTest').addEventListener('click',
    () => testProvider('etracker', 'btnEtTest', 'etStatus'));
document.getElementById('btnIbTest').addEventListener('click',
    () => testProvider('infobip', 'btnIbTest', 'ibStatus'));

document.getElementById('btnEtClear').addEventListener('click', async () => {
    if (!confirm('Remove project override and revert to global MacroKiosk defaults?')) return;
    try {
        await api.delete(`${api_proj}/sms-providers/etracker`);
        toast('Override removed.');
        await loadProviderConfig('etracker');
    } catch (e) { toast(e.message, 'danger'); }
});

document.getElementById('btnIbClear').addEventListener('click', async () => {
    if (!confirm('Remove project override and revert to global Infobip defaults?')) return;
    try {
        await api.delete(`${api_proj}/sms-providers/infobip`);
        toast('Override removed.');
        await loadProviderConfig('infobip');
    } catch (e) { toast(e.message, 'danger'); }
});

// ==================== COUPONS ====================
async function loadCouponBrands() {
    try {
        const rows = await api.get(`${api_proj}/coupons/brands`);
        const body = document.getElementById('cpnBrandBody');
        const sel  = document.getElementById('cpnImpBrand');
        sel.innerHTML = '<option value="">— pick a brand —</option>';
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="3" class="text-muted text-center py-3">'
                + 'No brands yet. Click + New to add Lotus / Central / etc.</td></tr>';
        } else {
            body.innerHTML = rows.map(b => `
                <tr>
                    <td><span class="badge" style="background:${esc(b.themeColor)}">${esc(b.displayName)}</span>
                        ${b.enabled ? '' : '<span class="text-muted small ms-1">(off)</span>'}</td>
                    <td class="small">${esc(b.barcodeFormat)}</td>
                    <td class="text-end">
                        <button class="btn btn-link btn-sm p-0" onclick='editCpnBrand(${JSON.stringify(b).replace(/'/g,"&apos;")})'>Edit</button>
                        <button class="btn btn-link btn-sm p-0 text-danger ms-1" onclick="deleteCpnBrand('${esc(b.id)}')">Del</button>
                    </td>
                </tr>`).join('');
        }
        rows.filter(b => b.enabled).forEach(b =>
            sel.insertAdjacentHTML('beforeend', `<option value="${esc(b.id)}">${esc(b.displayName)}</option>`));
    } catch (e) { toast(e.message, 'danger'); }
}

// Reflects the brand's current logo into the preview + hidden field, and
// enables/disables the uploader (upload needs a saved brand id).
function setCpnLogo(url) {
    document.getElementById('cpnBrandLogo').value = url || '';
    const img  = document.getElementById('cpnBrandLogoPreview');
    const none = document.getElementById('cpnBrandLogoNone');
    if (url) { img.src = url; img.style.display = ''; none.style.display = 'none'; }
    else     { img.removeAttribute('src'); img.style.display = 'none'; none.style.display = ''; }
}
function refreshCpnLogoUploader() {
    const hasBrand = !!document.getElementById('cpnBrandId').value;
    document.getElementById('cpnBrandLogoFile').disabled = !hasBrand;
    document.getElementById('btnCpnBrandLogoUpload').disabled = !hasBrand;
    document.getElementById('cpnBrandLogoHint').style.display = hasBrand ? 'none' : '';
}

document.getElementById('btnCpnBrandNew').addEventListener('click', () => {
    document.getElementById('cpnBrandEditor').style.display = '';
    document.getElementById('formCpnBrand').reset();
    document.getElementById('cpnBrandId').value = '';
    document.getElementById('cpnBrandColor').value = '#0066cc';
    document.getElementById('cpnBrandEnabled').checked = true;
    setCpnLogo('');
    refreshCpnLogoUploader();
});

document.getElementById('btnCpnBrandLogoUpload').addEventListener('click', async () => {
    const id = document.getElementById('cpnBrandId').value;
    if (!id) { toast('บันทึกแบรนด์ก่อนจึงจะอัปโหลดโลโก้ได้', 'warning'); return; }
    const f = document.getElementById('cpnBrandLogoFile').files[0];
    if (!f) { toast('เลือกไฟล์ก่อน', 'warning'); return; }
    const fd = new FormData();
    fd.append('file', f);
    try {
        // Raw fetch — api.post forces Content-Type: application/json, but
        // FormData needs the browser to set its own multipart boundary.
        const resp = await fetch(`${api_proj}/coupons/brands/${id}/logo`, {
            method: 'POST', body: fd, credentials: 'include'
        });
        const data = await resp.json().catch(() => ({}));
        if (!resp.ok) { toast(data?.message || 'อัปโหลดไม่สำเร็จ', 'danger'); return; }
        setCpnLogo(data.logoUrl);
        document.getElementById('cpnBrandLogoFile').value = '';
        toast('อัปโหลดโลโก้แล้ว');
        loadCouponBrands();
    } catch (e) { toast(e.message, 'danger'); }
});
document.getElementById('btnCpnBrandCancel').addEventListener('click', () =>
    document.getElementById('cpnBrandEditor').style.display = 'none');

window.editCpnBrand = function (b) {
    document.getElementById('cpnBrandEditor').style.display = '';
    document.getElementById('cpnBrandId').value      = b.id;
    document.getElementById('cpnBrandName').value    = b.name;
    document.getElementById('cpnBrandDisplay').value = b.displayName;
    setCpnLogo(b.logoUrl || '');
    refreshCpnLogoUploader();
    document.getElementById('cpnBrandColor').value   = b.themeColor || '#0066cc';
    document.getElementById('cpnBrandBarcode').value = b.barcodeFormat || 'code128';
    document.getElementById('cpnBrandInstr').value   = b.redemptionInstructions || '';
    document.getElementById('cpnBrandEnabled').checked = !!b.enabled;
};

window.deleteCpnBrand = async function (id) {
    if (!confirm('Delete this brand?')) return;
    try {
        await api.delete(`${api_proj}/coupons/brands/${id}`);
        toast('Brand deleted.');
        loadCouponBrands();
    } catch (e) { toast(e.message, 'danger'); }
};

document.getElementById('formCpnBrand').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const idVal = document.getElementById('cpnBrandId').value;
    try {
        await api.put(`${api_proj}/coupons/brands`, {
            id:                     idVal || null,
            name:                   document.getElementById('cpnBrandName').value.trim(),
            displayName:            document.getElementById('cpnBrandDisplay').value.trim(),
            logoUrl:                document.getElementById('cpnBrandLogo').value.trim() || null,
            themeColor:             document.getElementById('cpnBrandColor').value,
            redemptionInstructions: document.getElementById('cpnBrandInstr').value.trim() || null,
            barcodeFormat:          document.getElementById('cpnBrandBarcode').value,
            enabled:                document.getElementById('cpnBrandEnabled').checked
        });
        toast('Brand saved.');
        document.getElementById('cpnBrandEditor').style.display = 'none';
        loadCouponBrands();
    } catch (e) { toast(e.message, 'danger'); }
});

async function loadCouponBatches() {
    try {
        const rows = await api.get(`${api_proj}/coupons/batches`);
        const body = document.getElementById('cpnBatchBody');
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="6" class="text-muted text-center py-3">'
                + 'No batches imported yet.</td></tr>';
            return;
        }
        body.innerHTML = rows.map(b => {
            const rate = b.totalCount > 0
                ? ((b.redeemedCount / b.totalCount) * 100).toFixed(1) + '%' : '—';
            return `<tr style="cursor:pointer"
                        onclick="showCouponInventory('${esc(b.id)}','${esc(b.name)}')">
                <td>${esc(b.name)}</td>
                <td class="small">${esc(b.brandName)}</td>
                <td class="text-end">${b.totalCount}</td>
                <td class="text-end">${b.allocatedCount}</td>
                <td class="text-end text-success">${b.redeemedCount}</td>
                <td class="text-end">${rate}</td>
            </tr>`;
        }).join('');
    } catch (e) { toast(e.message, 'danger'); }
}

const CPN_STATUS = ['Available','Allocated','Redeemed','Expired','Void'];
const CPN_STATUS_BG = ['secondary','info','success','warning','dark'];
let _cpnInvBatchId = null;

window.showCouponInventory = function (batchId, batchName) {
    _cpnInvBatchId = batchId;
    document.getElementById('cpnInventoryCard').style.display = '';
    document.getElementById('cpnInvBatchName').textContent = batchName;
    document.getElementById('cpnInvStatus').value = '';
    loadCouponInventory();
};

async function loadCouponInventory() {
    if (!_cpnInvBatchId) return;
    const status = document.getElementById('cpnInvStatus').value;
    const qs = `?batchId=${encodeURIComponent(_cpnInvBatchId)}&take=300`
        + (status !== '' ? `&status=${status}` : '');
    try {
        const rows = await api.get(`${api_proj}/coupons${qs}`);
        const body = document.getElementById('cpnInvBody');
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="6" class="text-muted text-center py-3">No coupons.</td></tr>';
            return;
        }
        body.innerHTML = rows.map(c => {
            const si = typeof c.status === 'number' ? c.status : CPN_STATUS.indexOf(c.status);
            const voidable = si === 0 || si === 1; // Available / Allocated
            return `<tr>
                <td><code class="small">${esc(c.token)}</code></td>
                <td><span class="badge bg-${CPN_STATUS_BG[si]||'secondary'}">${esc(CPN_STATUS[si]||c.status)}</span></td>
                <td class="text-end">${Number(c.value).toLocaleString()}</td>
                <td class="small">${c.allocatedAt ? fmtDate(c.allocatedAt) : '—'}</td>
                <td class="small">${c.redeemedAt ? fmtDate(c.redeemedAt) : '—'}</td>
                <td class="text-end">${voidable
                    ? `<button class="btn btn-link btn-sm p-0 text-danger" onclick="voidCoupon('${esc(c.id)}')">Void</button>`
                    : ''}</td>
            </tr>`;
        }).join('');
    } catch (e) { toast(e.message, 'danger'); }
}
document.getElementById('cpnInvStatus')?.addEventListener('change', loadCouponInventory);

window.voidCoupon = async function (id) {
    if (!confirm('Void this coupon? It can no longer be allocated or redeemed.')) return;
    try {
        await api.post(`${api_proj}/coupons/${id}/void`, {});
        toast('Coupon voided.');
        loadCouponInventory();
        loadCouponBatches();
    } catch (e) { toast(e.message, 'danger'); }
};

document.getElementById('formCpnImport').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const fd = new FormData();
    fd.append('brandId',   document.getElementById('cpnImpBrand').value);
    fd.append('batchName', document.getElementById('cpnImpName').value.trim());
    fd.append('value',     document.getElementById('cpnImpValue').value);
    const exp = document.getElementById('cpnImpExpiry').value;
    if (exp) fd.append('expiresAt', new Date(exp).toISOString());
    fd.append('tokenLength',   document.getElementById('cpnImpTokenLen').value || '10');
    fd.append('tokenAlphabet', document.getElementById('cpnImpAlpha').value.trim());
    const col = document.getElementById('cpnImpCol').value.trim();
    if (col) fd.append('codeColumn', col);
    const file = document.getElementById('cpnImpFile').files[0];
    if (!file) { toast('Pick a file.', 'warning'); return; }
    fd.append('file', file);

    const out = document.getElementById('cpnImportResult');
    out.innerHTML = '<div class="text-muted small">Importing…</div>';
    try {
        const resp = await fetch(`${api_proj}/coupons/import`, {
            method: 'POST', body: fd, credentials: 'include'
        });
        const d = await resp.json();
        if (!resp.ok) { out.innerHTML = `<div class="text-danger small">${esc(d.message || 'Import failed')}</div>`; return; }
        let html = `<div class="alert alert-${d.rejected ? 'warning' : 'success'} small mb-1">
            Imported <strong>${d.accepted}</strong> · rejected <strong>${d.rejected}</strong></div>`;
        if (d.rejections && d.rejections.length) {
            html += `<details><summary class="small text-danger">View ${d.rejections.length} rejection(s)</summary>
                <table class="table table-sm small mt-1"><thead><tr><th>Row</th><th>Code</th><th>Reason</th></tr></thead>
                <tbody>${d.rejections.map(r => `<tr><td>${r.rowIndex}</td>
                    <td><code>${esc(r.code)}</code></td><td>${esc(r.reason)}</td></tr>`).join('')}</tbody></table></details>`;
        }
        out.innerHTML = html;
        document.getElementById('formCpnImport').reset();
        document.getElementById('cpnImpTokenLen').value = '10';
        document.getElementById('cpnImpAlpha').value = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
        loadCouponBatches();
    } catch (e) { out.innerHTML = `<div class="text-danger small">${esc(e.message)}</div>`; }
});

// Coupon redemption-link config (dedicated domain vs shared running number).
function couponUrlPreview() {
    const domain = document.getElementById('cpnDomain').value.trim();
    const token = 'A7K9PQ';
    if (domain) return `https://${domain.replace(/^https?:\/\//,'').replace(/\/+$/,'')}/redeem/${token}`;
    const n = (project && project.runningNumber) || 1;
    return `https://<platform>/r/${n}/${token}`;
}
function initCouponDomain() {
    const inp = document.getElementById('cpnDomain');
    if (!inp) return;
    inp.value = (project && project.couponRedeemDomain) || '';
    document.getElementById('cpnUrlPreview').textContent = couponUrlPreview();
    inp.addEventListener('input', () => {
        document.getElementById('cpnUrlPreview').textContent = couponUrlPreview();
    });
}
document.getElementById('formCpnDomain')?.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    try {
        await api.put(api_proj, { couponRedeemDomain: document.getElementById('cpnDomain').value.trim() });
        if (project) project.couponRedeemDomain = document.getElementById('cpnDomain').value.trim() || null;
        toast('Redemption domain saved.');
        document.getElementById('cpnUrlPreview').textContent = couponUrlPreview();
    } catch (e) { toast(e.message, 'danger'); }
});

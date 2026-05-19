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
    } catch (e) {
        toast('Failed to load project: ' + e.message, 'danger');
    }
})();

// Lazy tab loaders
document.querySelectorAll('[data-bs-toggle="tab"]').forEach(el => {
    el.addEventListener('shown.bs.tab', (ev) => {
        const target = ev.target.getAttribute('href');
        switch (target) {
            case '#tab-members':    if (!loaded.members)    { loaded.members    = true; loadMembers();    } break;
            case '#tab-mappings':   if (!loaded.mappings)   { loaded.mappings   = true; loadMappings();   } break;
            case '#tab-sources':    if (!loaded.sources)    { loaded.sources    = true; loadSources(); loadBatches(); } break;
            case '#tab-workflows':  if (!loaded.workflows)  { loaded.workflows  = true; loadWorkflows();  } break;
            case '#tab-shortlinks': if (!loaded.shortlinks) { loaded.shortlinks = true; loadShortlinks(); } break;
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

    document.getElementById('slLength').value = p.shortlink.shortlinkSlugLength ?? '';
    document.getElementById('slAlpha').value  = p.shortlink.shortlinkAlphabet ?? '';

    document.getElementById('notifEmails').value  = (p.notifications.recipients || []).join(', ');
    document.getElementById('notifPrefix').value  = (p.notifications.subjectPrefix || '');
    document.getElementById('notifSuccess').checked = p.notifications.triggers.onIngestSuccess;
    document.getElementById('notifPartial').checked = p.notifications.triggers.onIngestPartial;
    document.getElementById('notifFailure').checked = p.notifications.triggers.onIngestFailure;
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
        emailAlertsEnabled:document.getElementById('featEmail').checked
    }, 'Features saved.');
});
document.getElementById('formShortlink').addEventListener('submit', (ev) => {
    ev.preventDefault();
    const len = document.getElementById('slLength').value;
    const alpha = document.getElementById('slAlpha').value;
    saveAndReload({
        shortlinkSlugLength: len === '' ? null : parseInt(len, 10),
        shortlinkAlphabet:   alpha || ''
    }, 'Shortlink config saved.');
});
document.getElementById('formNotif').addEventListener('submit', (ev) => {
    ev.preventDefault();
    saveAndReload({
        notificationEmails:       document.getElementById('notifEmails').value,
        notificationSubjectPrefix:document.getElementById('notifPrefix').value,
        notifyOnIngestSuccess:    document.getElementById('notifSuccess').checked,
        notifyOnIngestPartial:    document.getElementById('notifPartial').checked,
        notifyOnIngestFailure:    document.getElementById('notifFailure').checked
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
            body.innerHTML = '<tr><td colspan="5" class="text-muted">No members.</td></tr>';
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

// ==================== MAPPINGS ====================
async function loadMappings() {
    try {
        const rows = await api.get(`${api_proj}/column-mappings`);
        const body = document.getElementById('mapBody');
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="5" class="text-muted">No mappings yet.</td></tr>';
            return;
        }
        body.innerHTML = rows.map(m => `
            <tr>
                <td><code>${esc(m.sourceColumn)}</code></td>
                <td>→</td>
                <td><span class="badge bg-info">${esc(m.canonicalField)}</span></td>
                <td><code class="small">${esc((m.transformChain||[]).join(','))}</code></td>
                <td>
                    <button class="btn btn-link btn-sm text-danger p-0"
                            onclick="deleteMap('${esc(m.id)}')">Delete</button>
                </td>
            </tr>`).join('');
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
document.getElementById('formMap').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    try {
        const chain = document.getElementById('mapChain').value
            .split(',').map(s => s.trim()).filter(Boolean);
        await api.post(`${api_proj}/column-mappings`, {
            sourceColumn:   document.getElementById('mapSource').value.trim(),
            canonicalField: document.getElementById('mapField').value,
            transformChain: chain
        });
        toast('Saved.');
        loadMappings();
    } catch (e) { toast(e.message, 'danger'); }
});

// ==================== SOURCES ====================
async function loadSources() {
    try {
        const rows = await api.get(`${api_proj}/ingestion-sources`);
        const body = document.getElementById('srcBody');
        const sel  = document.getElementById('upSrcSelect');
        sel.innerHTML = '<option value="">— pick from your bindings —</option>';
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="6" class="text-muted">No source bindings yet. Click "+ New".</td></tr>';
            return;
        }
        body.innerHTML = rows.map(s => `
            <tr>
                <td>${esc(s.sourceType)}</td>
                <td><code class="small">${esc(s.archiveDirectory||'-')}</code></td>
                <td>${ACTION_LABEL[s.action] || s.action}</td>
                <td>${DUP_LABEL[s.duplicatePolicy] || s.duplicatePolicy}</td>
                <td>${s.enabled ? '<span class="text-success">●</span>' : '<span class="text-muted">○</span>'}</td>
                <td>
                    <button class="btn btn-link btn-sm p-0"
                            onclick='editSrc(${JSON.stringify(s).replace(/'/g, "&apos;")})'>Edit</button>
                    <button class="btn btn-link btn-sm p-0 text-danger"
                            onclick="deleteSrc('${esc(s.id)}')">Delete</button>
                </td>
            </tr>`).join('');
        rows.forEach(s => sel.insertAdjacentHTML('beforeend',
            `<option value="${esc(s.id)}">${esc(s.sourceType)} — ${esc(s.id.slice(0,8))}…</option>`));
    } catch (e) { toast(e.message, 'danger'); }
}

function showSrcEditor(reset) {
    document.getElementById('srcEditor').style.display = '';
    document.getElementById('srcEditTitle').textContent = reset ? 'New source binding' : 'Edit source binding';
    if (reset) {
        document.getElementById('srcId').value = '';
        document.getElementById('srcType').value = 'SFTP';
        if (window.scheduleFromCron) window.scheduleFromCron('*/5 * * * *');
        document.getElementById('srcArchive').value = '';
        document.getElementById('srcRejected').value = '';
        document.getElementById('srcAction').value = '0';
        document.getElementById('srcDup').value = '0';
        document.getElementById('srcEnabled').checked = true;
        document.getElementById('srcConfig').value = JSON.stringify({
            host: 'sftp.example.com', port: 22, username: 'campaign',
            privateKeyPem: '-----BEGIN OPENSSH PRIVATE KEY-----\n…\n-----END OPENSSH PRIVATE KEY-----',
            remoteDirectory: '/incoming', filePattern: '*.csv'
        }, null, 2);
        document.getElementById('srcConfig').placeholder = '';
    }
}
document.getElementById('btnSrcNew').addEventListener('click', () => showSrcEditor(true));
document.getElementById('btnSrcCancel').addEventListener('click', () => {
    document.getElementById('srcEditor').style.display = 'none';
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
        for (const [k, el] of Object.entries(panes))
            el.style.display = (k === m) ? '' : 'none';
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

window.editSrc = function (s) {
    showSrcEditor(false);
    document.getElementById('srcId').value = s.id;
    document.getElementById('srcType').value = s.sourceType;
    window.scheduleFromCron(s.pollingSchedule || '*/5 * * * *');
    document.getElementById('srcArchive').value = s.archiveDirectory || '';
    document.getElementById('srcRejected').value = s.rejectedDirectory || '';
    document.getElementById('srcAction').value = String(s.action);
    document.getElementById('srcDup').value = String(s.duplicatePolicy);
    document.getElementById('srcEnabled').checked = !!s.enabled;
    document.getElementById('srcConfig').value = '';
    document.getElementById('srcConfig').placeholder =
        '(Existing config is encrypted on the server. Paste JSON to overwrite it.)';
};

document.getElementById('formSrc').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    try {
        const raw = document.getElementById('srcConfig').value.trim();
        if (!raw) { toast('Paste the JSON config (or {} to clear).', 'warning'); return; }
        let config;
        try { config = JSON.parse(raw); }
        catch { toast('Config must be valid JSON', 'danger'); return; }

        const body = {
            id: document.getElementById('srcId').value || null,
            sourceType: document.getElementById('srcType').value,
            config,
            archiveDirectory:  document.getElementById('srcArchive').value || null,
            rejectedDirectory: document.getElementById('srcRejected').value || null,
            action:            parseInt(document.getElementById('srcAction').value, 10),
            duplicatePolicy:   parseInt(document.getElementById('srcDup').value, 10),
            enabled:           document.getElementById('srcEnabled').checked,
            pollingSchedule:   window.scheduleToCron()
        };
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
            return `<tr>
                <td class="small">${fmtDate(b.ingestedAt)}</td>
                <td><code class="small">${esc(b.sourceType)}</code></td>
                <td class="small">${esc(b.sourceRef)}</td>
                <td>${b.totalRows}</td>
                <td class="text-success">${b.acceptedRows}</td>
                <td class="text-danger">${b.rejectedRows}</td>
                <td><span class="badge bg-${cls}">${esc(b.status)}</span></td>
            </tr>`;
        }).join('');
    } catch (e) { toast(e.message, 'danger'); }
}
document.getElementById('btnBatchRefresh').addEventListener('click', loadBatches);

// ==================== WORKFLOWS ====================
// Internal model: an object {name, expiration, initialStep, steps: {…}}
// Form view edits the model directly; JSON view serialises it; Diagram view
// renders it via Mermaid. Source of truth = the model object; views sync
// to/from it.
let wfModel = newWorkflowModel();
let wfActiveView = 'form';

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

async function loadWorkflows() {
    try {
        const rows = await api.get(`${api_proj}/workflows`);
        const body = document.getElementById('wfBody');
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="4" class="text-muted">No workflows yet.</td></tr>';
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
    } catch (e) { toast(e.message, 'danger'); }
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
        document.getElementById('wfEditTitle').textContent = `Editor — ${d.name} v${d.version}`;
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
    document.getElementById('wfEditTitle').textContent = 'Editor — new';
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

function stepCardHtml(name, step, allSteps) {
    const otherSteps = allSteps.filter(s => s !== name);
    const signalRows = Object.entries(step.onSignal || {}).map(([sig, tgt]) => `
        <tr data-sig="${esc(sig)}">
            <td><input class="form-control form-control-sm sig-name" value="${esc(sig)}" /></td>
            <td>→ <select class="form-select form-select-sm sig-target">
                ${allSteps.map(s => `<option value="${esc(s)}" ${s===tgt?'selected':''}>${esc(s)}</option>`).join('')}
            </select></td>
            <td><button type="button" class="btn btn-link btn-sm text-danger p-0 sig-del">✕</button></td>
        </tr>`).join('');

    return `
    <div class="card mb-2" data-step="${esc(name)}">
      <div class="card-body p-2">
        <div class="row g-2">
          <div class="col-md-4">
            <label class="form-label small">Step name</label>
            <input class="form-control form-control-sm step-name" value="${esc(name)}" />
          </div>
          <div class="col-md-3">
            <label class="form-label small">Type</label>
            <select class="form-select form-select-sm step-type">
              <option value="send_sms" ${step.type==='send_sms'?'selected':''}>send_sms</option>
              <option value="wait"     ${step.type==='wait'    ?'selected':''}>wait</option>
              <option value="complete" ${step.type==='complete'?'selected':''}>complete</option>
            </select>
          </div>
          <div class="col-md-3 step-wait-col" style="${step.type==='complete'?'display:none':''}">
            <label class="form-label small">Wait (d.HH:MM:SS)</label>
            <input class="form-control form-control-sm step-wait" value="${esc(step.wait||'')}" placeholder="2.00:00:00" />
          </div>
          <div class="col-md-2 step-maxrep-col" style="${step.type==='complete'?'display:none':''}">
            <label class="form-label small">Max repeats</label>
            <input type="number" min="1" max="20" class="form-control form-control-sm step-maxrep"
                   value="${step.maxRepeats||1}" />
          </div>
          <div class="col-12 step-template-col" style="${step.type==='send_sms'?'':'display:none'}">
            <label class="form-label small">SMS body template (use {{column}} placeholders)</label>
            <textarea class="form-control form-control-sm step-template" rows="2">${esc(step.template||'')}</textarea>
          </div>
          <div class="col-12 step-transitions-col" style="${step.type==='complete'?'display:none':''}">
            <label class="form-label small">Transitions</label>
            <table class="table table-sm mb-1">
              <thead><tr><th>On signal</th><th>Target</th><th></th></tr></thead>
              <tbody class="sig-body">${signalRows}</tbody>
            </table>
            <button type="button" class="btn btn-link btn-sm p-0 add-sig">+ Add signal</button>
            <span class="ms-3 small">On timeout →
              <select class="form-select form-select-sm d-inline-block w-auto step-ontimeout">
                <option value="">(none)</option>
                ${allSteps.map(s => `<option value="${esc(s)}" ${s===step.onTimeout?'selected':''}>${esc(s)}</option>`).join('')}
              </select>
            </span>
          </div>
          <div class="col-12 text-end">
            <button type="button" class="btn btn-link btn-sm text-danger p-0 step-del">
              <i class="bi bi-trash"></i> Delete step
            </button>
          </div>
        </div>
      </div>
    </div>`;
}

function bindStepCard(card) {
    // Type change toggles which fields show
    const typeEl = card.querySelector('.step-type');
    typeEl.addEventListener('change', () => {
        const isComplete = typeEl.value === 'complete';
        const isSms      = typeEl.value === 'send_sms';
        card.querySelector('.step-wait-col').style.display        = isComplete ? 'none' : '';
        card.querySelector('.step-maxrep-col').style.display      = isComplete ? 'none' : '';
        card.querySelector('.step-template-col').style.display    = isSms ? '' : 'none';
        card.querySelector('.step-transitions-col').style.display = isComplete ? 'none' : '';
    });

    card.querySelector('.add-sig').addEventListener('click', () => {
        const tbody = card.querySelector('.sig-body');
        const others = Object.keys(wfModel.steps);
        tbody.insertAdjacentHTML('beforeend', `
            <tr data-sig="">
                <td><input class="form-control form-control-sm sig-name" value="" /></td>
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
        newSteps[newName] = step;
    });
    wfModel.steps = newSteps;
    wfModel.name = document.getElementById('wfName').value.trim();
    wfModel.expiration = document.getElementById('wfExp').value.trim() || '60.00:00:00';
    if (!(wfModel.initialStep in newSteps)) wfModel.initialStep = firstName;
}

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
document.getElementById('btnWfSave').addEventListener('click', async () => {
    try {
        if (wfActiveView === 'form') captureFormToModel();
        else if (wfActiveView === 'json') captureJsonToModel();
        const spec = {
            initialStep: wfModel.initialStep,
            expiration:  wfModel.expiration,
            steps:       wfModel.steps
        };
        if (!wfModel.name) { toast('Workflow name is required.', 'warning'); return; }
        if (!spec.initialStep || !(spec.initialStep in spec.steps)) {
            toast('Initial step must reference an existing step.', 'warning'); return;
        }
        const r = await api.post(`${api_proj}/workflows`, { name: wfModel.name, spec });
        toast(`Saved v${r.version}. Activate it from the list.`);
        loadWorkflows();
    } catch (e) { toast('Save failed: ' + e.message, 'danger'); }
});

// Render an empty model on first paint of the workflows tab
document.querySelector('a[href="#tab-workflows"]').addEventListener('shown.bs.tab', () => {
    if (!loaded.wfRendered) { loaded.wfRendered = true; renderWf(); }
});

// ==================== SMS ====================
document.getElementById('formSms').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const out = document.getElementById('smsResult');
    out.classList.add('d-none');
    try {
        const sched = document.getElementById('smsSched').value;
        const r = await api.post(`${api_proj}/sms/send`, {
            recipient: document.getElementById('smsTo').value.trim(),
            body:      document.getElementById('smsBody').value,
            senderId:  document.getElementById('smsFrom').value.trim() || null,
            scheduledFor: sched ? new Date(sched).toISOString() : null
        });
        out.classList.remove('d-none');
        out.textContent = JSON.stringify(r, null, 2);
        toast(`Dispatched: ${r.status}`);
        loadSmsList();
    } catch (e) { toast(e.message, 'danger'); }
});

async function loadSmsList() {
    try {
        const filter = document.getElementById('smsFilter').value;
        const qs = filter ? `?take=50&status=${encodeURIComponent(filter)}` : '?take=50';
        const rows = await api.get(`${api_proj}/sms${qs}`);
        const body = document.getElementById('smsListBody');
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="5" class="text-muted">No SMS sent yet.</td></tr>';
            return;
        }
        body.innerHTML = rows.map(s => {
            const c = SMS_STATUS_COLOR[s.status] || 'secondary';
            return `<tr style="cursor:pointer" onclick="showSmsDetail('${esc(s.id)}')">
                <td class="small">${fmtDate(s.createdAt)}</td>
                <td><code class="small">${esc(s.maskedTo)}</code></td>
                <td class="small">${esc(s.provider)}</td>
                <td><span class="badge bg-${c}">${esc(s.status)}</span>
                    ${s.errorCode ? `<small class="text-danger ms-1">${esc(s.errorCode)}</small>` : ''}
                </td>
                <td>${s.attempts}</td>
            </tr>`;
        }).join('');
    } catch (e) { toast(e.message, 'danger'); }
}

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
document.getElementById('smsFilter').addEventListener('change', loadSmsList);

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
async function loadShortlinks() {
    try {
        const rows = await api.get(`${api_proj}/shortlinks`);
        const body = document.getElementById('slBody');
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="5" class="text-muted">No shortlinks yet.</td></tr>';
            return;
        }
        body.innerHTML = rows.map(s => `
            <tr>
                <td><code>${esc(s.slug)}</code></td>
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
document.getElementById('btnAud').addEventListener('click', async () => {
    const from = document.getElementById('audFrom').value;
    const to   = document.getElementById('audTo').value;
    try {
        const rows = await api.get(`${api_proj}/reports/audit-trail` +
            `?from=${from}T00:00:00Z&to=${to}T23:59:59Z&take=500`);
        const body = document.getElementById('audBody');
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="5" class="text-muted">No audit entries in range.</td></tr>';
            return;
        }
        body.innerHTML = rows.map(r => `
            <tr>
                <td class="small">${fmtDate(r.at)}</td>
                <td><span class="small">${esc(r.userEmail || (r.userId||'').slice(0,8))}</span></td>
                <td><code class="small">${esc(r.action)}</code></td>
                <td class="small">${esc(r.entityType)}#${esc((r.entityId||'').slice(0,8))}</td>
                <td class="small text-muted">${esc(r.ipAddress||'')}</td>
            </tr>`).join('');
    } catch (e) { toast(e.message, 'danger'); }
});

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

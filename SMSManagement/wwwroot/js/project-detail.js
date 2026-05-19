// Project detail page — handles every tab.
// Each tab lazy-loads when its `shown.bs.tab` event fires the first time.

const projectId = document.getElementById('projectId').value;
const api_proj  = `/api/projects/${projectId}`;
let project;        // last-known project payload
const loaded = {};  // tab-name -> bool, so we only fetch once per tab

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
            case '#tab-sources':    if (!loaded.sources)    { loaded.sources    = true; loadSources();    } break;
            case '#tab-workflows':  if (!loaded.workflows)  { loaded.workflows  = true; loadWorkflows();  } break;
            case '#tab-shortlinks': if (!loaded.shortlinks) { loaded.shortlinks = true; loadShortlinks(); } break;
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
document.getElementById('btnArchive').addEventListener('click', async () => {
    if (!confirm('Archive this project? This cancels in-flight workflows and queued SMS. Reversible by an admin.')) return;
    try {
        await api.delete(api_proj);
        toast('Project archived.');
        setTimeout(() => window.location = '/Projects/Index', 800);
    } catch (e) { toast(e.message, 'danger'); }
});

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
document.getElementById('formShare').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    try {
        await api.post(`${api_proj}/members`, {
            userId: document.getElementById('shareUserId').value.trim(),
            level:  document.getElementById('shareLevel').value
        });
        toast('Shared.');
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
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="6" class="text-muted">No source bindings.</td></tr>';
            return;
        }
        body.innerHTML = rows.map(s => `
            <tr>
                <td>${esc(s.sourceType)}</td>
                <td><code class="small">${esc(s.archiveDirectory||'-')}</code></td>
                <td>${s.action}</td>
                <td>${s.duplicatePolicy}</td>
                <td>${s.enabled ? '<span class="text-success">●</span>' : '<span class="text-muted">○</span>'}</td>
                <td><code class="small">${esc(s.id.slice(0,8))}…</code></td>
            </tr>`).join('');
    } catch (e) { toast(e.message, 'danger'); }
}
document.getElementById('formUpload').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const out = document.getElementById('upResult');
    out.classList.add('d-none');
    try {
        const fd = new FormData();
        fd.append('settingsId', document.getElementById('upSrcId').value.trim());
        fd.append('file', document.getElementById('upFile').files[0]);
        const force = document.getElementById('upForce').checked;
        const resp = await fetch(`${api_proj}/ingest?force=${force}`, {
            method: 'POST', credentials: 'include', body: fd
        });
        const data = await resp.json();
        out.classList.remove('d-none');
        out.textContent = JSON.stringify(data, null, 2);
        if (resp.ok) toast(`Ingested ${data.acceptedRows}/${data.totalRows} rows.`);
        else toast(data.message || 'Upload failed', 'danger');
    } catch (e) { toast(e.message, 'danger'); }
});

// ==================== WORKFLOWS ====================
async function loadWorkflows() {
    try {
        const rows = await api.get(`${api_proj}/workflows`);
        const body = document.getElementById('wfBody');
        if (!rows.length) {
            body.innerHTML = '<tr><td colspan="4" class="text-muted">No workflows yet.</td></tr>';
        } else {
            body.innerHTML = rows.map(w => `
                <tr>
                    <td><a href="#" onclick="loadWfDef('${esc(w.id)}','${esc(w.name)}');return false">${esc(w.name)}</a></td>
                    <td>${w.version}</td>
                    <td>${w.active ? '<i class="bi bi-check-circle-fill text-success"></i>' : ''}</td>
                    <td>${w.active
                        ? `<button class="btn btn-link btn-sm p-0" onclick="toggleWf('${esc(w.id)}',false)">Deactivate</button>`
                        : `<button class="btn btn-link btn-sm p-0" onclick="toggleWf('${esc(w.id)}',true)">Activate</button>`}</td>
                </tr>`).join('');
        }
    } catch (e) { toast(e.message, 'danger'); }
}
window.loadWfDef = async function (id, name) {
    try {
        const d = await api.get(`${api_proj}/workflows/${id}`);
        document.getElementById('wfName').value = d.name;
        document.getElementById('wfSpec').value = JSON.stringify(d.spec, null, 2);
        document.getElementById('wfEditTitle').textContent = `Editor — ${d.name} v${d.version}`;
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
    document.getElementById('wfName').value = '';
    document.getElementById('wfSpec').value = JSON.stringify({
        initialStep: 'send',
        expiration: '60.00:00:00',
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
    }, null, 2);
    document.getElementById('wfEditTitle').textContent = 'Editor — new';
});
document.getElementById('btnWfSave').addEventListener('click', async () => {
    try {
        const spec = JSON.parse(document.getElementById('wfSpec').value);
        const r = await api.post(`${api_proj}/workflows`, {
            name: document.getElementById('wfName').value.trim(),
            spec
        });
        toast(`Saved v${r.version}. Activate it from the list.`);
        loadWorkflows();
    } catch (e) {
        toast('Save failed: ' + e.message, 'danger');
    }
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
    } catch (e) { toast(e.message, 'danger'); }
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
                <td>${s.clickCount}${s.maxClicks ? ' / ' + s.maxClicks : ''}</td>
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

// Tiny helpers shared by all Razor pages — vanilla fetch + Bootstrap toasts,
// no build pipeline.
window.api = {
    async req(method, url, body) {
        const opts = {
            method,
            headers: { 'Accept': 'application/json' },
            credentials: 'include'
        };
        if (body !== undefined) {
            opts.headers['Content-Type'] = 'application/json';
            opts.body = JSON.stringify(body);
        }
        const resp = await fetch(url, opts);
        const text = await resp.text();
        let data = null;
        if (text) { try { data = JSON.parse(text); } catch { data = text; } }
        if (!resp.ok) {
            const msg = (data && (data.message || data.title)) ||
                resp.statusText || ('HTTP ' + resp.status);
            const err = new Error(msg);
            err.status = resp.status;
            err.body = data;
            throw err;
        }
        return data;
    },
    get:    (u)    => window.api.req('GET',    u),
    post:   (u, b) => window.api.req('POST',   u, b),
    put:    (u, b) => window.api.req('PUT',    u, b),
    delete: (u)    => window.api.req('DELETE', u)
};

window.toast = function (message, variant = 'success') {
    const container = document.querySelector('.toast-container') || document.body;
    const el = document.createElement('div');
    el.className = `toast align-items-center text-bg-${variant} border-0`;
    el.setAttribute('role', 'alert');
    el.innerHTML = `
        <div class="d-flex">
            <div class="toast-body">${message}</div>
            <button type="button" class="btn-close btn-close-white me-2 m-auto"
                    data-bs-dismiss="toast" aria-label="Close"></button>
        </div>`;
    container.appendChild(el);
    new bootstrap.Toast(el, { delay: 4000 }).show();
    el.addEventListener('hidden.bs.toast', () => el.remove());

    // Append to history. Bounded to 20 — covers a normal working session
    // without growing unbounded for long-running tabs.
    const hist = window._toastHistory ||= [];
    hist.unshift({ at: new Date(), variant, message });
    if (hist.length > 20) hist.length = 20;
    const badge = document.getElementById('toastHistoryCount');
    if (badge) {
        badge.textContent = hist.length;
        badge.classList.remove('d-none');
    }
};

window.fmtDate = function (iso) {
    if (!iso) return '';
    const d = new Date(iso);
    return isNaN(d.getTime()) ? iso : d.toLocaleString();
};

window.esc = function (s) {
    if (s === null || s === undefined) return '';
    return String(s)
        .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;').replace(/'/g, '&#039;');
};

// ============ UNIFIED CONFIRM MODAL ============
// Single Bootstrap modal reused for every destructive op so behaviour and
// styling stay consistent. Caller picks the friction level:
//   simple     — typed confirm: hit Enter or click OK
//   math       — solve a small addition problem (defeats double-click /
//                stale-tab "yes I really meant it" mistakes)
//   typeName   — type a literal token (e.g. project code) to enable OK
// Returns a Promise<boolean>.
window.confirmAction = function (opts) {
    return new Promise((resolve) => {
        let modal = document.getElementById('globalConfirmModal');
        if (!modal) {
            // Lazy-build on first use so layout HTML stays clean.
            const wrap = document.createElement('div');
            wrap.innerHTML = `
            <div class="modal fade" id="globalConfirmModal" tabindex="-1">
              <div class="modal-dialog">
                <div class="modal-content border-danger">
                  <div class="modal-header bg-danger text-white">
                    <h5 class="modal-title" id="gcmTitle">Confirm</h5>
                    <button type="button" class="btn-close btn-close-white" data-bs-dismiss="modal"></button>
                  </div>
                  <div class="modal-body">
                    <div id="gcmBody"></div>
                    <div id="gcmExtra" class="mt-3"></div>
                  </div>
                  <div class="modal-footer">
                    <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button>
                    <button type="button" class="btn btn-danger" id="gcmOk">OK</button>
                  </div>
                </div>
              </div>
            </div>`;
            document.body.appendChild(wrap.firstElementChild);
            modal = document.getElementById('globalConfirmModal');
        }

        const titleEl = document.getElementById('gcmTitle');
        const bodyEl  = document.getElementById('gcmBody');
        const extra   = document.getElementById('gcmExtra');
        const ok      = document.getElementById('gcmOk');

        titleEl.textContent = opts.title || 'Confirm';
        bodyEl.innerHTML    = opts.message || 'Are you sure?';
        ok.textContent      = opts.okLabel || 'Confirm';
        ok.className        = 'btn ' + (opts.okClass || 'btn-danger');
        extra.innerHTML     = '';

        let challengePassed = (opts.mode || 'simple') === 'simple';
        ok.disabled = !challengePassed;

        const mode = opts.mode || 'simple';
        if (mode === 'math') {
            const a = 5 + Math.floor(Math.random() * 15);
            const b = 1 + Math.floor(Math.random() * 9);
            extra.innerHTML = `
                <p class="mb-2"><strong>To confirm, solve:</strong></p>
                <div class="d-flex align-items-center gap-2">
                    <span class="fw-bold font-monospace fs-5">${a} + ${b}</span>
                    <span>=</span>
                    <input id="gcmAns" type="number" class="form-control form-control-sm"
                           style="width:90px" autocomplete="off" />
                </div>`;
            setTimeout(() => document.getElementById('gcmAns').focus(), 200);
            document.getElementById('gcmAns').addEventListener('input', (e) => {
                challengePassed = Number(e.target.value) === a + b;
                ok.disabled = !challengePassed;
            });
        } else if (mode === 'typeName') {
            extra.innerHTML = `
                <p class="mb-2 small text-muted">Type <code>${window.esc(opts.expect)}</code> to confirm:</p>
                <input id="gcmType" class="form-control form-control-sm" autocomplete="off" />`;
            setTimeout(() => document.getElementById('gcmType').focus(), 200);
            document.getElementById('gcmType').addEventListener('input', (e) => {
                challengePassed = e.target.value === opts.expect;
                ok.disabled = !challengePassed;
            });
        }

        const bs = new bootstrap.Modal(modal);
        const cleanup = (val) => {
            ok.removeEventListener('click', onOk);
            modal.removeEventListener('hidden.bs.modal', onCancel);
            bs.hide();
            resolve(val);
        };
        const onOk     = () => { if (challengePassed) cleanup(true); };
        const onCancel = () => cleanup(false);
        ok.addEventListener('click', onOk);
        modal.addEventListener('hidden.bs.modal', onCancel, { once: true });
        bs.show();
    });
};

// ============ TABLE SORT + FILTER ============
// Attaches click handlers to <th data-sort="num|str|date"> and an input with
// data-filter-target="#tbody". One <table> call covers both. Re-runnable.
window.initTable = function (root = document) {
    root.querySelectorAll('table[data-sortable]').forEach(table => {
        if (table.dataset.sortableInit === '1') return;
        table.dataset.sortableInit = '1';
        const tbody = table.tBodies[0];
        if (!tbody) return;

        const headers = table.querySelectorAll('th[data-sort]');
        headers.forEach((th, idx) => {
            th.style.cursor = 'pointer';
            th.title = 'Click to sort';
            // Subtle indicator arrow appended once.
            if (!th.querySelector('.sort-ind')) {
                const span = document.createElement('span');
                span.className = 'sort-ind text-muted small ms-1';
                span.textContent = '↕';
                th.appendChild(span);
            }
            th.addEventListener('click', () => {
                const dir = th.dataset.dir === 'asc' ? 'desc' : 'asc';
                headers.forEach(h => {
                    h.dataset.dir = '';
                    const s = h.querySelector('.sort-ind'); if (s) s.textContent = '↕';
                });
                th.dataset.dir = dir;
                const ind = th.querySelector('.sort-ind');
                if (ind) ind.textContent = dir === 'asc' ? '↑' : '↓';

                const kind = th.dataset.sort || 'str';
                const rows = Array.from(tbody.rows);
                rows.sort((a, b) => compare(
                    cellValue(a.cells[idx], kind),
                    cellValue(b.cells[idx], kind),
                    kind) * (dir === 'asc' ? 1 : -1));
                rows.forEach(r => tbody.appendChild(r));
            });
        });
    });

    root.querySelectorAll('input[data-filter-target]').forEach(input => {
        if (input.dataset.filterInit === '1') return;
        input.dataset.filterInit = '1';
        input.addEventListener('input', () => {
            const tgt = document.querySelector(input.dataset.filterTarget);
            if (!tgt) return;
            const needle = input.value.trim().toLowerCase();
            for (const row of tgt.rows) {
                row.style.display = !needle || row.textContent.toLowerCase().includes(needle)
                    ? '' : 'none';
            }
        });
    });
};

function cellValue(cell, kind) {
    const txt = cell ? cell.textContent.trim() : '';
    if (kind === 'num')  return Number(txt.replace(/[^\d.\-]/g, '')) || 0;
    if (kind === 'date') { const d = new Date(txt); return isNaN(d) ? 0 : d.getTime(); }
    return txt.toLowerCase();
}
function compare(a, b) { return a < b ? -1 : a > b ? 1 : 0; }

// ============ DATE-RANGE PRESETS ============
// Wires a button-group of presets to two <input type="date"> elements.
// Trigger element fires a 'rangechange' custom event after each click.
window.initDateRangePresets = function (groupSelector, fromSelector, toSelector) {
    const group = document.querySelector(groupSelector);
    const from  = document.querySelector(fromSelector);
    const to    = document.querySelector(toSelector);
    if (!group || !from || !to) return;

    const fmt = (d) => d.toISOString().slice(0, 10);
    const set = (a, b) => {
        from.value = fmt(a);
        to.value   = fmt(b);
        from.dispatchEvent(new CustomEvent('rangechange', { bubbles: true, detail: { from: a, to: b } }));
    };
    const today    = () => { const d = new Date(); d.setHours(0,0,0,0); return d; };
    const yest     = () => { const d = today(); d.setDate(d.getDate() - 1); return d; };
    const daysAgo  = (n) => { const d = today(); d.setDate(d.getDate() - n); return d; };
    const startMo  = () => { const d = today(); d.setDate(1); return d; };
    const startQt  = () => { const d = today(); d.setMonth(d.getMonth() - (d.getMonth() % 3), 1); return d; };
    const eod      = () => { const d = today(); d.setDate(d.getDate() + 1); return d; };

    group.addEventListener('click', (e) => {
        const btn = e.target.closest('[data-range]');
        if (!btn) return;
        group.querySelectorAll('[data-range]').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        switch (btn.dataset.range) {
            case 'today':       set(today(), eod()); break;
            case 'yesterday':   set(yest(), today()); break;
            case 'last7':       set(daysAgo(6), eod()); break;
            case 'last30':      set(daysAgo(29), eod()); break;
            case 'mtd':         set(startMo(), eod()); break;
            case 'qtd':         set(startQt(), eod()); break;
        }
    });
};

// Initialise Bootstrap popovers + tooltips for any element with the right
// data attributes. Re-runnable: subsequent calls only attach to elements that
// don't have an instance yet, so AJAX-rendered content can call this too.
window.initBsHints = function () {
    document.querySelectorAll('[data-bs-toggle="popover"]').forEach(el => {
        if (!bootstrap.Popover.getInstance(el)) new bootstrap.Popover(el);
    });
    document.querySelectorAll('[data-bs-toggle="tooltip"]').forEach(el => {
        if (!bootstrap.Tooltip.getInstance(el)) new bootstrap.Tooltip(el);
    });
};

// ============ TOAST HISTORY DRAWER ============
// Bell icon in the layout opens this; lists the same toasts that flashed
// past, with relative time + variant colour. Click 'X' next to a row to
// dismiss; "Clear" empties the lot.
window.openToastHistory = function () {
    const hist = window._toastHistory || [];
    let drawer = document.getElementById('toastHistoryOffcanvas');
    if (!drawer) {
        const wrap = document.createElement('div');
        wrap.innerHTML = `
        <div class="offcanvas offcanvas-end" tabindex="-1" id="toastHistoryOffcanvas">
          <div class="offcanvas-header">
            <h5 class="offcanvas-title"><i class="bi bi-bell"></i> Notifications</h5>
            <button type="button" id="thClear" class="btn btn-link btn-sm">Clear</button>
            <button type="button" class="btn-close" data-bs-dismiss="offcanvas"></button>
          </div>
          <div class="offcanvas-body" id="thBody"></div>
        </div>`;
        document.body.appendChild(wrap.firstElementChild);
        drawer = document.getElementById('toastHistoryOffcanvas');
        document.getElementById('thClear').addEventListener('click', () => {
            window._toastHistory = [];
            document.getElementById('toastHistoryCount')?.classList.add('d-none');
            window.openToastHistory(); // rerender
        });
    }
    const body = document.getElementById('thBody');
    if (!hist.length) {
        body.innerHTML = '<p class="text-muted small">No notifications yet.</p>';
    } else {
        body.innerHTML = hist.map(h => `
            <div class="border-bottom py-2">
                <span class="badge bg-${esc(h.variant === 'success' ? 'success' : h.variant === 'danger' ? 'danger' : h.variant === 'warning' ? 'warning text-dark' : 'secondary')} me-2">
                    ${esc(h.variant)}
                </span>
                <small class="text-muted">${fmtDate(h.at.toISOString())}</small>
                <div class="small">${esc(h.message)}</div>
            </div>`).join('');
    }
    new bootstrap.Offcanvas(drawer).show();
};

document.addEventListener('DOMContentLoaded', () => {
    window.initBsHints();
    window.initTable();
});

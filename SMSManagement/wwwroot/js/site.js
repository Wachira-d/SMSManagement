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
document.addEventListener('DOMContentLoaded', window.initBsHints);

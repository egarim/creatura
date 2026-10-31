// What a person sees on the page, as the web world reads it: the visible text and the controls on screen.
// Run in the page by WebWorld; returns { url, title, text, controls: [...] }. Each control is stamped with
// data-creatura-ref so the world can act on exactly the element it showed. Viewport first: controls inside the
// viewport come before the ones below it, each group top to bottom, left to right.
(maxText) => {
  const ROLES = new Set(['button', 'link', 'checkbox', 'radio', 'switch', 'tab', 'menuitem', 'menuitemcheckbox',
    'menuitemradio', 'option', 'combobox', 'textbox', 'searchbox', 'slider', 'spinbutton', 'treeitem']);
  const TEXT_INPUTS = new Set(['text', 'email', 'password', 'search', 'tel', 'url', 'number', 'date', 'datetime-local',
    'time', 'month', 'week', '']);
  const vw = window.innerWidth, vh = window.innerHeight;

  for (const old of document.querySelectorAll('[data-creatura-ref]')) old.removeAttribute('data-creatura-ref');

  const visible = (el) => {
    const r = el.getBoundingClientRect();
    if (r.width <= 0 || r.height <= 0) return false;
    const s = getComputedStyle(el);
    return s.visibility !== 'hidden' && s.display !== 'none' && parseFloat(s.opacity || '1') > 0;
  };
  const clean = (s) => (s || '').replace(/\s+/g, ' ').trim();
  const roleOf = (el) => {
    const explicit = el.getAttribute('role');
    if (explicit && ROLES.has(explicit)) return explicit;
    const tag = el.tagName.toLowerCase();
    if (tag === 'a' && el.hasAttribute('href')) return 'link';
    if (tag === 'button' || tag === 'summary') return 'button';
    if (tag === 'select') return 'combobox';
    if (tag === 'textarea') return 'textbox';
    if (tag === 'input') {
      const t = (el.getAttribute('type') || '').toLowerCase();
      if (t === 'hidden') return null;
      if (t === 'checkbox') return 'checkbox';
      if (t === 'radio') return 'radio';
      if (t === 'submit' || t === 'button' || t === 'reset' || t === 'image') return 'button';
      if (t === 'search') return 'searchbox';
      if (TEXT_INPUTS.has(t)) return 'textbox';
      return 'button';
    }
    if (el.isContentEditable) return 'textbox';
    return null;
  };
  const labelOf = (el) => {
    const aria = el.getAttribute('aria-label');
    if (aria) return clean(aria);
    const by = el.getAttribute('aria-labelledby');
    if (by) {
      const t = by.split(/\s+/).map(id => document.getElementById(id)).filter(Boolean).map(n => n.innerText).join(' ');
      if (clean(t)) return clean(t);
    }
    if (el.labels && el.labels.length) return clean(Array.from(el.labels).map(l => l.innerText).join(' '));
    const tag = el.tagName.toLowerCase();
    if (tag !== 'select' && tag !== 'textarea' && tag !== 'input') {
      const t = clean(el.innerText);
      if (t) return t.slice(0, 80);
    }
    return clean(el.getAttribute('placeholder') || el.getAttribute('title') || el.getAttribute('alt')
      || (tag === 'input' ? el.value : '') || el.getAttribute('name') || '');
  };

  const all = Array.from(document.body ? document.body.querySelectorAll('*') : []);
  const picked = [];
  const pickedSet = new Set();
  for (const el of all) {
    if (!visible(el) || el.disabled) continue;
    let role = roleOf(el);
    if (!role && (el.hasAttribute('onclick') || el.tabIndex >= 0 && el.hasAttribute('tabindex'))) role = 'button';
    if (!role) {
      // A cursor:pointer leaf: clickable by look, with no control inside it and not inside one already taken.
      if (getComputedStyle(el).cursor !== 'pointer') continue;
      if (el.querySelector('a,button,input,select,textarea,[role]')) continue;
      let p = el.parentElement, inside = false;
      while (p) { if (pickedSet.has(p)) { inside = true; break; } p = p.parentElement; }
      if (inside || !clean(el.innerText)) continue;
      role = 'button';
    } else {
      let p = el.parentElement, inside = false;
      while (p) { if (pickedSet.has(p) && roleOf(p) && roleOf(p) !== 'textbox') { inside = true; break; } p = p.parentElement; }
      if (inside) continue;
    }
    pickedSet.add(el);
    picked.push({ el, role });
  }

  const rows = picked.map(({ el, role }) => {
    const r = el.getBoundingClientRect();
    const tag = el.tagName.toLowerCase();
    const kind = role === 'combobox' && tag === 'select' ? 'choose'
      : (role === 'textbox' || role === 'searchbox' || role === 'spinbutton') ? 'type' : 'click';
    return {
      el, role, kind, tag,
      name: labelOf(el),
      value: kind === 'type' ? (el.isContentEditable ? clean(el.innerText) : (el.value || '')) : (kind === 'choose' ? (el.selectedOptions[0]?.text || '') : ''),
      checked: role === 'checkbox' || role === 'radio' ? !!el.checked : null,
      options: kind === 'choose' ? Array.from(el.options).map(o => clean(o.text)) : [],
      inViewport: r.bottom > 0 && r.right > 0 && r.top < vh && r.left < vw,
      top: Math.round(r.top + window.scrollY), left: Math.round(r.left + window.scrollX),
    };
  });
  rows.sort((a, b) => (a.inViewport === b.inViewport ? 0 : a.inViewport ? -1 : 1) || a.top - b.top || a.left - b.left);
  rows.forEach((row, i) => row.el.setAttribute('data-creatura-ref', String(i)));

  // The visible text in reading order (innerText leaves out what is hidden), one line per block, capped.
  let text = (document.body ? document.body.innerText : '').split('\n').map(clean).filter(Boolean).join('\n');
  if (text.length > maxText) text = text.slice(0, maxText) + '…';

  return {
    url: location.href,
    title: document.title || '',
    text,
    controls: rows.map((row, i) => ({
      ref: i, role: row.role, kind: row.kind, tag: row.tag, name: row.name, value: row.value,
      checked: row.checked, options: row.options, inViewport: row.inViewport,
    })),
  };
}

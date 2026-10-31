using System.Text.Json;

namespace Creatura.Worlds.Web;

/// <summary>
/// The page barriers' scripts (docs/design-web-worlds.md, phase 3), put into every page of a world's context before the
/// app's own scripts run. Each finds its control by visible name the way the observation does (aria-label, its label,
/// its text, its placeholder), counts what it did through <see cref="FiredFunction"/>, and stops after
/// <see cref="UxBarrier.Times"/> when that is set (counted per tab, across the tab's pages).
/// </summary>
public static class PageBarriers
{
    /// <summary>The function the world exposes to the page, which a barrier calls with its id each time it acts.</summary>
    public const string FiredFunction = "__creaturaBarrierFired";

    private const string Common = """
        const clean = (s) => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
        const nameOf = (el) => {
          const aria = el.getAttribute && el.getAttribute('aria-label');
          if (aria) return clean(aria);
          if (el.labels && el.labels.length) return clean(Array.from(el.labels).map(l => l.innerText).join(' '));
          const tag = el.tagName ? el.tagName.toLowerCase() : '';
          if (tag !== 'input' && tag !== 'textarea' && tag !== 'select') { const t = clean(el.innerText); if (t) return t; }
          return clean(el.getAttribute && (el.getAttribute('placeholder') || el.getAttribute('title') || el.value || ''));
        };
        const key = '__creatura_barrier_' + id;
        const left = () => times === null || Number(sessionStorage.getItem(key) || '0') < times;
        const fired = () => {
          sessionStorage.setItem(key, String(Number(sessionStorage.getItem(key) || '0') + 1));
          try { window.__creaturaBarrierFired(id); } catch (e) { }
        };
        """;

    /// <summary>The script for a page barrier, or null for a network one.</summary>
    public static string? Script(UxBarrier barrier)
    {
        var head = $"const id = {JsonSerializer.Serialize(barrier.Id)}; const target = {JsonSerializer.Serialize(barrier.Target.Trim().ToLowerInvariant())}; " +
            $"const times = {(barrier.Times is { } t ? t.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}; const text = {JsonSerializer.Serialize(barrier.Text)};";
        var body = barrier.Kind switch
        {
            // The click is caught on its way down, before the page sees it: the button moves to the end of its parent and
            // nothing else happens. The page made no claim, so the world reads it as a click that did nothing.
            UxBarrierKind.Dodge => """
                document.addEventListener('click', (e) => {
                  const el = e.target && e.target.closest ? e.target.closest('button, a, [role=button], input[type=submit], input[type=button]') : null;
                  if (!el || nameOf(el) !== target || !left()) return;
                  e.preventDefault(); e.stopImmediatePropagation();
                  el.parentElement && el.parentElement.appendChild(el);
                  fired();
                }, true);
                """,

            // A banner over the whole page, until its Accept is pressed; accepted, it stays away for the tab.
            UxBarrierKind.Cover => """
                const show = () => {
                  if (sessionStorage.getItem(key + '_accepted') || !left() || document.getElementById('__creatura_cover')) return;
                  const veil = document.createElement('div');
                  veil.id = '__creatura_cover';
                  veil.setAttribute('role', 'dialog');
                  veil.setAttribute('aria-label', 'Cookie notice');
                  veil.style.cssText = 'position:fixed;inset:0;z-index:2147483647;background:rgba(0,0,0,.45);display:flex;align-items:flex-end;justify-content:center;';
                  const box = document.createElement('div');
                  box.style.cssText = 'background:#fff;padding:1rem;margin:1rem;max-width:30rem;font:16px system-ui;';
                  const p = document.createElement('p'); p.textContent = text;
                  const ok = document.createElement('button'); ok.textContent = 'Accept';
                  ok.addEventListener('click', () => { sessionStorage.setItem(key + '_accepted', '1'); veil.remove(); });
                  box.appendChild(p); box.appendChild(ok); veil.appendChild(box);
                  document.body.appendChild(veil);
                  fired();
                };
                if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', show); else show();
                """,

            // What is typed arrives one letter short: the second letter is dropped as the field takes it.
            UxBarrierKind.DropLetter => """
                document.addEventListener('input', (e) => {
                  const el = e.target;
                  if (!el || !('value' in el) || nameOf(el) !== target || el.value.length < 2 || !left()) return;
                  el.value = el.value.slice(0, 1) + el.value.slice(2);
                  fired();
                }, true);
                """,

            _ => null,
        };
        return body is null ? null : "(() => {\n" + head + "\n" + Common + "\n" + body + "\n})();";
    }
}

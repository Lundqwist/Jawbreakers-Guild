// Shared by every page: header, footer and small helpers.

const CLASS_COLORS = {
  Warrior: '#C69B6D', Paladin: '#F48CBA', Hunter: '#AAD372', Rogue: '#FFF468', Priest: '#FFFFFF',
  Shaman: '#3B8DFF', Mage: '#3FC7EB', Warlock: '#8788EE', Druid: '#FF7C0A'
};

/** Builds an element. Text always goes in as text, never as HTML. */
function el(tag, props = {}, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (v === null || v === undefined || v === false) continue;
    if (k === 'class') e.className = v;
    else if (k === 'text') e.textContent = v;
    else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
    else e.setAttribute(k, v === true ? '' : v);
  }
  for (const kid of kids.flat()) {
    if (kid === null || kid === undefined || kid === false) continue;
    e.append(kid.nodeType ? kid : document.createTextNode(kid));
  }
  return e;
}

async function api(path, method = 'GET', body) {
  // Relative on purpose (like every address on the site): it also works under /jawbreakers/
  const r = await fetch('api/' + path, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : {},
    body: body ? JSON.stringify(body) : undefined
  });
  if (!r.ok) {
    let message = r.status === 401 ? 'Sign in as admin first.' : `Something went wrong (${r.status}).`;
    try { const j = await r.json(); if (j.error) message = j.error; } catch { /* no JSON body */ }
    throw new Error(message);
  }
  return r.status === 204 ? null : r.json();
}

const metaPromise = api('meta');

// Event times are realm time: shown exactly as the admin typed them.
function eventParts(iso) {
  const d = new Date(iso);
  return {
    date: d,
    day: d.getDate(),
    month: d.toLocaleDateString('en-GB', { month: 'short' }),
    time: d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' }),
    full: d.toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' })
  };
}

function requirementChips(ev) {
  const items = [];
  if (ev.minLevel) items.push(`Level ${ev.minLevel}+`);
  items.push(...ev.requirements);
  return items.length ? el('ul', { class: 'chips', 'aria-label': 'Requirements' }, items.map(t => el('li', { text: t }))) : null;
}

function eventCard(ev) {
  const p = eventParts(ev.startsAt);
  return el('article', { class: 'panel event' },
    el('div', { class: 'when' },
      el('span', { class: 'day', text: p.day }),
      el('span', { class: 'month', text: `${p.month} ${p.date.getFullYear()}` }),
      el('span', { class: 'time', text: p.time })),
    el('div', { class: 'body' },
      el('h2', { text: ev.title }),
      el('div', { class: 'where', text: `${p.full}, ${p.time} realm time · Starts at ${ev.startingPoint}` }),
      ev.description && el('p', { class: 'desc', text: ev.description }),
      requirementChips(ev)));
}

(function buildChrome() {
  const here = location.pathname.split('/').pop() || 'index.html';
  const links = [
    ['index.html', 'Home'], ['members.html', 'Guild members'], ['events.html', 'Events'],
    ['crafting.html', 'Crafting'], ['about.html', 'About'], ['admin.html', 'Admin']
  ];
  document.body.prepend(el('header', { class: 'site-header' },
    el('div', { class: 'inner' },
      el('a', { class: 'brand', href: './' }, 'Jaw', el('span', { text: 'breakers' })),
      el('nav', { class: 'nav', 'aria-label': 'Main' },
        links.map(([href, text]) => el('a', {
          href, text, class: href === here && 'current', 'aria-current': href === here && 'page',
          // The Admin link is only shown to the signed-in admin (and on the admin page itself); the page is still at admin.html
          id: href === 'admin.html' && 'nav-admin', hidden: href === 'admin.html' && here !== 'admin.html'
        }))))));
  metaPromise.then(meta => { if (meta.admin) document.getElementById('nav-admin').hidden = false; }).catch(() => {});
  document.body.append(el('footer', { class: 'site-footer', text: 'Jawbreakers · Horde · EU · PvP · For the Horde!' }));
})();

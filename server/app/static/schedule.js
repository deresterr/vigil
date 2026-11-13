// Weekly schedule (D19, #48, #102): the day grid of Settings → Weekly schedule and of the setup wizard.
// pages.py inlines this after i18n.js, with DEFAULT_WEEK (a new child's week, config.default_schedule) before it.
// The grid lives in an element with id="schedule"; inputs sLim0..6, sFrom0..6, sTo0..6, Monday first like the API.

const DAYS = t('Pn Wt Śr Cz Pt So Nd').split(' ');
const DAYS_FULL = [...Array(7)].map((_, i) => {  // 2026-11-02 is a Monday
  const name = new Date(2026, 10, 2 + i).toLocaleDateString(LOCALE, {weekday: 'long'});
  return name.charAt(0).toUpperCase() + name.slice(1);
});

// today: 0–6 marks that row (the panel), null marks none (the wizard)
function renderSchedule(week, today = null) {
  document.getElementById('schedule').innerHTML =
    `<span></span><span class="hd">${t('Limit (min)')}</span><span class="hd">${t('Cisza nocna od')}</span><span class="hd">${t('do')}</span>` +
    week.map((d, i) => (i === 5 ? '<div class="sep"></div>' : '') +  // weekend below the line
      `<span class="day${i >= 5 ? ' weekend' : ''}${i === today ? ' today' : ''}" title="${i === today ? t('{day} (dziś)', {day: DAYS_FULL[i]}) : DAYS_FULL[i]}">${DAYS[i]}</span>` +
      // Text, not type="number": Chromium lets "e", ".", "+", "-" through ("12e0" saved 12, "1e3" 1000; #106).
      // Four digits: the server takes 0–1440.
      `<input type="text" inputmode="numeric" maxlength="4" data-kind="min" id="sLim${i}" value="${d.daily_limit}" aria-label="${t('{day}: limit w minutach', {day: DAYS_FULL[i]})}">` +
      // Text, not type="time": with a 12-hour browser locale the narrow column cut "PM" off (21:30 read 09:30).
      `<input type="text" inputmode="numeric" maxlength="5" data-kind="time" placeholder="21:30" id="sFrom${i}" value="${d.night_start}" aria-label="${t('{day}: początek ciszy nocnej (GG:MM)', {day: DAYS_FULL[i]})}">` +
      `<input type="text" inputmode="numeric" maxlength="5" data-kind="time" placeholder="06:30" id="sTo${i}" value="${d.night_end}" aria-label="${t('{day}: koniec ciszy nocnej (GG:MM)', {day: DAYS_FULL[i]})}">`).join('');
}

// A time as it is typed: digits only, the ":" put in after the hour, since a phone's numeric keyboard (inputmode)
// has none. A first digit 3–9, or one digit before a typed ":", is a one-digit hour: "630" and "6:30" give 06:30.
function typedTime(raw) {
  let d = raw.replace(/\D/g, '');
  if (/^[3-9]/.test(d) || /^\d:/.test(raw)) d = '0' + d;
  d = d.slice(0, 4);
  return d.length > 2 ? d.slice(0, 2) + ':' + d.slice(2) : d;
}

// As it is typed: a limit keeps digits only, a time goes through typedTime; an edited field loses its error mark.
document.addEventListener('input', e => {
  const f = e.target;
  if (!f.dataset || !f.dataset.kind || !f.closest('#schedule')) return;
  const clean = f.dataset.kind === 'min' ? f.value.replace(/\D/g, '') : typedTime(f.value);
  if (clean !== f.value) f.value = clean;
  f.classList.remove('invalid');
});

// Fills the grid: 'default' = DEFAULT_WEEK, 'monday' = Monday's row (as typed) on every day. Nothing is saved.
function scheduleTemplate(name) {
  const row = i => ({daily_limit: document.getElementById('sLim' + i).value,
                     night_start: document.getElementById('sFrom' + i).value,
                     night_end: document.getElementById('sTo' + i).value});
  const week = name === 'default' ? DEFAULT_WEEK : Array(7).fill(row(0));
  week.forEach((d, i) => {
    document.getElementById('sLim' + i).value = d.daily_limit;
    document.getElementById('sFrom' + i).value = d.night_start;
    document.getElementById('sTo' + i).value = d.night_end;
  });
}

// The grid as the API takes it, checked by the server's rules: {week} or {error: 'Day: what is wrong'}, with the
// field in error marked and focused.
function readSchedule() {
  const hhmm = /^([01]\d|2[0-3]):[0-5]\d$/;
  const week = [];
  for (let i = 0; i < 7; i++) {
    const [lim, from, to] = ['sLim', 'sFrom', 'sTo'].map(f => document.getElementById(f + i));
    const raw = lim.value.trim();
    const d = {daily_limit: Number(raw), night_start: from.value.trim(), night_end: to.value.trim()};
    const [field, err] = !/^\d{1,4}$/.test(raw) || d.daily_limit > 1440
      ? [lim, t('limit to liczba całkowita minut 0–1440 (0 = bez limitu)')]
      : !hhmm.test(d.night_start) ? [from, t('godziny w formacie GG:MM, np. 21:30')]
      : !hhmm.test(d.night_end) ? [to, t('godziny w formacie GG:MM, np. 21:30')]
      : d.night_start === d.night_end ? [to, t('początek i koniec ciszy nocnej nie mogą być takie same')] : [];
    if (err) {
      field.classList.add('invalid');
      field.focus();
      return {error: DAYS_FULL[i] + ': ' + err};
    }
    week.push(d);
  }
  return {week};
}

// A short form of the week: runs of equal days, e.g. [['Pn–Cz', '120 min · 21:30–06:30'], ['Pt–Nd', …]].
function scheduleSummary(week) {
  const text = d => (d.daily_limit ? d.daily_limit + ' min' : t('Bez limitu')) + ' · ' + d.night_start + '–' + d.night_end;
  const rows = [];
  week.forEach((d, i) => {
    const last = rows[rows.length - 1];
    if (last && last.text === text(d)) last.to = i;
    else rows.push({from: i, to: i, text: text(d)});
  });
  return rows.map(r => [r.from === r.to ? DAYS[r.from] : DAYS[r.from] + '–' + DAYS[r.to], r.text]);
}

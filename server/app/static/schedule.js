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
      `<input type="number" id="sLim${i}" min="0" max="1440" step="1" value="${d.daily_limit}" aria-label="${t('{day}: limit w minutach', {day: DAYS_FULL[i]})}">` +
      // Text, not type="time": with a 12-hour browser locale the narrow column cut "PM" off (21:30 read 09:30).
      `<input type="text" inputmode="numeric" maxlength="5" placeholder="21:30" id="sFrom${i}" value="${d.night_start}" aria-label="${t('{day}: początek ciszy nocnej (GG:MM)', {day: DAYS_FULL[i]})}">` +
      `<input type="text" inputmode="numeric" maxlength="5" placeholder="06:30" id="sTo${i}" value="${d.night_end}" aria-label="${t('{day}: koniec ciszy nocnej (GG:MM)', {day: DAYS_FULL[i]})}">`).join('');
}

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

// The grid as the API takes it, checked by the server's rules: {week} or {error: 'Day: what is wrong'}.
function readSchedule() {
  const hhmm = /^([01]\d|2[0-3]):[0-5]\d$/;
  const week = [];
  for (let i = 0; i < 7; i++) {
    const raw = document.getElementById('sLim' + i).value;
    const d = {daily_limit: Number(raw), night_start: document.getElementById('sFrom' + i).value.trim(),
               night_end: document.getElementById('sTo' + i).value.trim()};
    const err = raw === '' || !Number.isInteger(d.daily_limit) || d.daily_limit < 0 || d.daily_limit > 1440
      ? t('limit to liczba całkowita 0–1440 minut (1440 = całą dobę)')
      : !hhmm.test(d.night_start) || !hhmm.test(d.night_end) ? t('godziny w formacie GG:MM, np. 21:30')
      : d.night_start === d.night_end ? t('początek i koniec ciszy nocnej nie mogą być takie same') : null;
    if (err) return {error: DAYS_FULL[i] + ': ' + err};
    week.push(d);
  }
  return {week};
}

// A short form of the week: runs of equal days, e.g. [['Pn–Cz', '120 min · 21:30–06:30'], ['Pt–Nd', …]].
function scheduleSummary(week) {
  const text = d => d.daily_limit + ' min · ' + d.night_start + '–' + d.night_end;
  const rows = [];
  week.forEach((d, i) => {
    const last = rows[rows.length - 1];
    if (last && last.text === text(d)) last.to = i;
    else rows.push({from: i, to: i, text: text(d)});
  });
  return rows.map(r => [r.from === r.to ? DAYS[r.from] : DAYS[r.from] + '–' + DAYS[r.to], r.text]);
}

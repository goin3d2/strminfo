define(['loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (loading) {
    'use strict';

    var pluginId = '3f9b6a1e-5c2d-4e8f-a7b4-91d0c6e2f853';
    var PHASES = { idle: 'Idle', listing: 'Finding items', probing: 'Probing', 'waiting for library scan': 'Waiting for the library scan' };
    var RESULTS = { ok: 'OK', failed: 'Failed', timeout: 'Timed out' };
    var BOOLS = ['IncludeMovies', 'IncludeEpisodes', 'IncludeOtherVideos', 'StrmOnly', 'NewestFirst', 'PauseDuringLibraryScan', 'ProbeNewItems', 'ExtractChapters'];
    var NUMS = ['Concurrency', 'TimeoutSeconds', 'DelayMs', 'MaxItemsPerRun', 'MaxRunMinutes', 'StopAfterFailuresInRow', 'RetryFailedAfterDays'];

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
        });
    }
    function fmtN(n) { return n == null ? '–' : Number(n).toLocaleString(); }
    function ago(t, now) {
        if (!t) { return '–'; }
        var d = Math.max(0, (now || Date.now() / 1000) - t);
        if (d < 60) { return Math.round(d) + 's ago'; }
        if (d < 3600) { return Math.round(d / 60) + ' min ago'; }
        if (d < 86400) { return (d / 3600).toFixed(1) + ' h ago'; }
        return (d / 86400).toFixed(1) + ' days ago';
    }
    function dur(s) {
        if (s == null) { return ''; }
        s = Math.round(s);
        if (s < 90) { return s + ' s'; }
        if (s < 5400) { return Math.round(s / 60) + ' min'; }
        return (s / 3600).toFixed(1) + ' h';
    }
    function toast(msg) { require(['toast'], function (t) { t(msg); }); }
    function getJson(path) { return ApiClient.getJSON(ApiClient.getUrl(path)); }
    function postJson(path, body) {
        return ApiClient.ajax({
            type: 'POST', url: ApiClient.getUrl(path), data: JSON.stringify(body || {}),
            contentType: 'application/json', dataType: 'json'
        });
    }

    return function (view) {
        var $ = function (sel) { return view.querySelector(sel); };
        var $$ = function (sel) { return Array.prototype.slice.call(view.querySelectorAll(sel)); };
        var timer = null, tab = 'status', config = null, libs = null, status = null, libDraft = null;
        var logAfter = 0, logLines = [];

        // ---- tabs ---------------------------------------------------------------
        $$('.smi-tab').forEach(function (b) {
            b.addEventListener('click', function () {
                tab = b.getAttribute('data-tab');
                $$('.smi-tab').forEach(function (x) { x.classList.toggle('on', x === b); });
                $$('.smi-pane').forEach(function (p) { p.classList.toggle('on', p.getAttribute('data-pane') === tab); });
                if (tab === 'libraries') { loadLibs(); }
                if (tab === 'settings') { loadConfig(); }
                if (tab === 'failures') { loadFailures(); }
                if (tab === 'log') { renderLog(); }
            });
        });
        function goTab(name) { var b = $('.smi-tab[data-tab="' + name + '"]'); if (b) { b.click(); } }

        // ---- configuration --------------------------------------------------------
        function fetchConfig() { return ApiClient.getPluginConfiguration(pluginId); }
        function loadConfig() {
            return fetchConfig().then(function (c) {
                config = c;
                BOOLS.forEach(function (k) { $('#' + k).checked = !!c[k]; });
                NUMS.forEach(function (k) { if (document.activeElement !== $('#' + k)) { $('#' + k).value = c[k]; } });
                if (document.activeElement !== $('#UserAgent')) { $('#UserAgent').value = c.UserAgent || ''; }
                return c;
            });
        }
        function saveConfig(c, msg) {
            loading.show();
            return ApiClient.updatePluginConfiguration(pluginId, c).then(function (r) {
                loading.hide();
                Dashboard.processPluginConfigurationUpdateResult(r);
                config = c;
                if (msg) { toast(msg); }
            }, function () { loading.hide(); toast('Saving failed.'); });
        }
        $('#smiSettingsForm').addEventListener('submit', function (e) {
            e.preventDefault();
            fetchConfig().then(function (c) {
                BOOLS.forEach(function (k) { c[k] = $('#' + k).checked; });
                NUMS.forEach(function (k) { var v = parseInt($('#' + k).value, 10); c[k] = isNaN(v) ? 0 : v; });
                c.Concurrency = Math.max(1, Math.min(10, c.Concurrency || 1));
                c.TimeoutSeconds = Math.max(5, c.TimeoutSeconds || 30);
                c.UserAgent = $('#UserAgent').value.trim();
                saveConfig(c, 'Saved. The next run uses these settings.').then(loadConfig);
            });
            return false;
        });

        // ---- libraries ------------------------------------------------------------
        function selectedIds(c) {
            return (c && c.LibraryIds ? c.LibraryIds.split(',') : []).map(function (x) { return x.trim(); }).filter(Boolean);
        }
        function renderLibs() {
            if (!libs || !config) { return; }
            // The ticks being edited (the table is redrawn every few seconds with fresh counts).
            if (!libDraft) { libDraft = selectedIds(config); }
            var sel = libDraft, counts = (status && status.counts) || {};
            $('#smiLibs').innerHTML = libs.map(function (l) {
                var k = counts[l.id] || {};
                return '<tr><td><input type="checkbox" class="smi-lib" data-id="' + esc(l.id) + '"' + (sel.indexOf(l.id) >= 0 ? ' checked' : '') + ' /></td>' +
                    '<td>' + esc(l.name) + '<div class="smi-paths">' + esc((l.locations || []).join(', ')) + '</div></td>' +
                    '<td class="smi-muted" style="white-space:nowrap">' + esc(({ movies: 'Movies', tvshows: 'TV shows', homevideos: 'Home videos', musicvideos: 'Music videos', mixed: 'Mixed' })[l.type || 'mixed'] || l.type) + '</td>' +
                    '<td class="num">' + fmtN(k.total) + '</td><td class="num">' + fmtN(k.pending) + '</td><td class="num">' + fmtN(k.failed) + '</td></tr>';
            }).join('') || '<tr><td colspan="6" class="smi-muted">No libraries found.</td></tr>';
            var note = status && status.counting ? 'Counting…' :
                (status && status.counted_at ? 'Counted ' + ago(status.counted_at, status.now) + '.' : 'Select Count items to see how many still need media info.');
            $('#smiCountNote').textContent = note;
        }
        function loadLibs() {
            return Promise.all([getJson('strminfo/libraries'), fetchConfig()]).then(function (r) {
                libs = r[0]; config = r[1]; libDraft = selectedIds(config); renderLibs();
            });
        }
        $('#smiLibs').addEventListener('change', function (e) {
            var t = e.target;
            if (!t.classList || !t.classList.contains('smi-lib')) { return; }
            var id = t.getAttribute('data-id');
            libDraft = (libDraft || []).filter(function (x) { return x !== id; });
            if (t.checked) { libDraft.push(id); }
        });
        $('#smiCount').addEventListener('click', function () {
            postJson('strminfo/count').then(function () { toast('Counting… this can take a minute on big libraries.'); setTimeout(loadStatus, 500); });
        });
        $('#smiLibSave').addEventListener('click', function () {
            fetchConfig().then(function (c) {
                c.LibraryIds = (libDraft || []).join(',');
                saveConfig(c, c.LibraryIds ? 'Saved.' : 'Saved. No libraries are selected, so runs will do nothing.');
            });
        });

        // ---- status -----------------------------------------------------------------
        function loadStatus() {
            return getJson('strminfo/status').then(function (s) {
                status = s;
                var now = s.now, busy = s.running;
                var label = busy ? (PHASES[s.phase] || s.phase) : 'Idle';
                if (busy && s.stopping) { label = 'Stopping'; }
                $('#smiPill').textContent = label;
                $('#smiWave').classList.toggle('busy', busy);
                $('#smiPhase').textContent = label;
                var pct = s.total ? Math.round(s.done * 100 / s.total) : 0, detail = '', meta = '';
                if (busy) {
                    if (s.total) { detail = fmtN(s.done) + ' of ' + fmtN(s.total) + ' items (' + pct + '%)' + (s.scope === 'new items' ? ', newly added' : ''); }
                    meta = 'Started ' + ago(s.started, now) + (s.eta != null ? ', about ' + dur(s.eta) + ' left' : '');
                } else if (s.last) {
                    var l = s.last;
                    detail = 'Last run ' + ago(l.time, now) + ': ' + fmtN(l.ok) + ' read, ' + fmtN(l.failed) + ' failed, ' + fmtN(l.timed_out) + ' timed out' +
                        (l.seconds ? ' in ' + dur(l.seconds) : '') + (l.stopped ? '<br><span class="smi-muted">Stopped: ' + esc(l.stopped) + '</span>' : '');
                }
                $('#smiDetail').innerHTML = detail;
                $('#smiMeta').textContent = meta;
                $('#smiFill').style.width = (busy ? (s.total ? pct : 3) : 0) + '%';
                $('#smiBar').classList.toggle('running', busy);
                $('#smiRun').style.display = busy ? 'none' : '';
                $('#smiStop').style.display = busy ? '' : 'none';
                var cur = s.current || [];
                $('#smiCurrentWrap').style.display = busy && cur.length ? '' : 'none';
                $('#smiCurrent').innerHTML = cur.map(function (n) { return '<li>' + esc(n) + '</li>'; }).join('');

                var al = '';
                if (config && !selectedIds(config).length) {
                    al += '<div class="smi-alert warn"><b>No libraries selected yet.</b> Pick the libraries to probe on the <a href="#" data-go="libraries" class="button-link">Libraries</a> tab.</div>';
                }
                if (busy && s.phase === 'waiting for library scan') {
                    al += '<div class="smi-alert">An Emby library scan is running; probing continues when it finishes.</div>';
                }
                $('#smiAlerts').innerHTML = al;

                var src = busy ? s : (s.last || {});
                var cards = [
                    [busy ? 'Read this run' : 'Read last run', '<span class="smi-num-ok">' + fmtN(src.ok) + '</span>', 'items now have media info'],
                    ['Failed', '<span class="smi-num-bad">' + fmtN(src.failed) + '</span>', 'no usable stream'],
                    ['Timed out', '<span class="smi-num-warn">' + fmtN(src.timed_out) + '</span>', 'no answer in time'],
                    ['Skipped', fmtN(src.skipped), 'already had media info'],
                    ['On the Failed list', fmtN(s.failures), '<a href="#" data-go="failures" class="button-link">See them</a>'],
                ];
                if (s.new_waiting) { cards.push(['New items waiting', fmtN(s.new_waiting), 'probed within a couple of minutes']); }
                $('#smiCards').innerHTML = cards.map(function (c) {
                    return '<div class="smi-card"><h3>' + c[0] + '</h3><div class="smi-big">' + c[1] + '</div><div class="smi-muted smi-small">' + c[2] + '</div></div>';
                }).join('');

                var rec = s.recent || [];
                $('#smiRecentEmpty').style.display = rec.length ? 'none' : '';
                $('#smiRecent').innerHTML = rec.map(function (r) {
                    return '<tr><td class="smi-muted smi-small" style="white-space:nowrap">' + ago(r.time, now) + '</td><td>' + esc(r.name) + '</td>' +
                        '<td><span class="smi-badge ' + esc(r.result) + '">' + (RESULTS[r.result] || esc(r.result)) + '</span></td><td class="smi-small">' + esc(r.detail) + '</td></tr>';
                }).join('');
                if (tab === 'libraries') { renderLibs(); }
            });
        }
        view.addEventListener('click', function (e) {
            var a = e.target.closest ? e.target.closest('[data-go]') : null;
            if (a) { e.preventDefault(); goTab(a.getAttribute('data-go')); }
        });
        $('#smiRun').addEventListener('click', function () {
            postJson('strminfo/run').then(function (r) {
                toast(r.ok ? 'Run started.' : r.error);
                setTimeout(loadStatus, 800);
            });
        });
        $('#smiStop').addEventListener('click', function () {
            postJson('strminfo/stop').then(function () { toast('Stopping after the current probes finish.'); setTimeout(loadStatus, 500); });
        });

        // ---- failures ----------------------------------------------------------------
        function loadFailures() {
            return getJson('strminfo/failures').then(function (f) {
                var now = Date.now() / 1000;
                $('#smiFailsEmpty').style.display = f.length ? 'none' : '';
                $('#smiFails').innerHTML = f.map(function (x) {
                    return '<tr><td class="smi-muted smi-small" style="white-space:nowrap">' + ago(x.time, now) + '</td><td>' + esc(x.name) + '</td>' +
                        '<td class="num">' + fmtN(x.count) + '</td><td class="smi-small">' + esc(x.reason) + '</td></tr>';
                }).join('');
            });
        }
        $('#smiClear').addEventListener('click', function () {
            require(['confirm'], function (confirm) {
                confirm('All failed items will be tried again at the next run.', 'Retry failed items?').then(function () {
                    postJson('strminfo/failures/clear').then(function () { toast('Done.'); loadFailures(); loadStatus(); });
                });
            });
        });

        // ---- log -----------------------------------------------------------------------
        function loadLog() {
            return getJson('strminfo/log?After=' + logAfter).then(function (d) {
                if (d.lines.length) {
                    logLines = logLines.concat(d.lines).slice(-1500);
                    logAfter = d.last;
                    if (tab === 'log') { renderLog(); }
                }
            });
        }
        function renderLog() {
            var el = $('#smiLog');
            var atEnd = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
            el.innerHTML = logLines.map(function (l) { return '<div class="' + esc(l[1]) + '">' + esc(l[2]) + '</div>'; }).join('');
            if (atEnd) { el.scrollTop = el.scrollHeight; }
        }

        // ---- polling -------------------------------------------------------------------
        function tick() { loadStatus(); loadLog(); if (tab === 'failures') { loadFailures(); } }
        view.addEventListener('viewshow', function () {
            loadConfig().then(tick);
            if (timer) { clearInterval(timer); }
            timer = setInterval(tick, 2000);
        });
        view.addEventListener('viewhide', function () {
            if (timer) { clearInterval(timer); timer = null; }
        });
    };
});

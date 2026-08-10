// Attendance Monitoring — heartbeat + live dashboard refresh (ASP.NET version)
(function () {
    const HEARTBEAT_MS = 15000;   // tell server we're online every 15s
    const REFRESH_MS = 15000;     // re-fetch dashboard data every 15s
    const IDLE_THRESHOLD_MS = 15 * 60 * 1000; // 15 min → Away
    const DEBUG = true;           // log heartbeat lifecycle to the console

    // Client-tracked presence. Sent on every heartbeat so the server
    // knows whether the user is Online (active), Away (system idle >15m),
    // or Offline (Windows locked / tab closing). Minimizing or switching
    // tabs is NOT a state change — the user is still "online".
    let currentState = "online";

    // -----------------------------------------------------------------
    // Heartbeat-driven state mirrored from the server. These are kept at
    // the top of the IIFE because applyServerState() (called by the very
    // first sendHeartbeat / worker tick) writes into them — the deeper
    // button-wiring blocks just read / re-render from them. Without the
    // hoist, the initial heartbeat would hit a `let`-TDZ ReferenceError
    // and silently fail, leaving the KPI box stuck on its hardcoded
    // "Online" placeholder regardless of actual server state.
    // -----------------------------------------------------------------
    let lunchEndsAt = null;
    let breakEndsAt = null;
    let lunchesUsed = 0;
    let breaksUsed = 0;
    let lunchesMax = 1; // overwritten from the lunch button's data-lunch-max
    let breaksMax = 2;  // overwritten from the break button's data-break-max

    function setState(next) {
        if (next !== "online" && next !== "away" && next !== "offline" && next !== "lunch" && next !== "break") return;
        if (currentState === next) return;
        if (DEBUG) console.log(`[presence] ${currentState} → ${next}`);
        currentState = next;
        // Send an immediate heartbeat so the dashboard reflects the new
        // state without waiting for the next 15s tick.
        pingHeartbeat();
    }

    function jsonHeaders() {
        return { "Content-Type": "application/json", "X-Requested-With": "fetch" };
    }

    // -----------------------------------------------------------------
    // Reconcile client state with the server's canonical view returned by
    // /api/heartbeat (and the lunch/break start endpoints). Called from:
    //   * sendHeartbeat()                    — main-thread watchdog
    //   * heartbeatWorker.onmessage           — worker tick
    //   * the initial break-state sync block  — page-load priming
    // The server payload is the source of truth for lunch / break /
    // online state, the active countdown anchors, and the daily quotas.
    // -----------------------------------------------------------------
    function applyServerState(data) {
        if (!data || typeof data !== "object") {
            // No payload — keep the UI showing whatever it shows.
            return;
        }
        const serverState = (data.state || "online").toLowerCase();

        if (serverState === "lunch") {
            if (data.lunch_ends_at) {
                lunchEndsAt = new Date(data.lunch_ends_at).getTime();
            }
            currentState = "lunch";
        } else if (serverState === "break") {
            if (data.break_ends_at) {
                breakEndsAt = new Date(data.break_ends_at).getTime();
            }
            currentState = "break";
        } else if (serverState === "offline") {
            // Server flagged us offline (lock-screen path). Honour it.
            currentState = "offline";
            lunchEndsAt = null;
            breakEndsAt = null;
        } else {
            // "online" (or anything else — default to online). Clear the
            // countdown anchors so the KPI sub-text and button labels
            // don't keep dangling stale minutes.
            currentState = "online";
            lunchEndsAt = null;
            breakEndsAt = null;
        }

        if (typeof data.lunches_used === "number") lunchesUsed = data.lunches_used;
        if (typeof data.lunches_max === "number")  lunchesMax  = data.lunches_max;
        if (typeof data.breaks_used === "number")  breaksUsed  = data.breaks_used;
        if (typeof data.breaks_max === "number")   breaksMax   = data.breaks_max;

        // Re-render the KPI dot/label and — if present — the topbar buttons
        // so the page reflects the new state in one round-trip.
        setSelfDot(currentState);
        if (typeof renderLunchButton === "function") renderLunchButton();
        if (typeof renderBreakButton === "function") renderBreakButton();
        if (currentState === "lunch" && typeof startLunchCountdown === "function") {
            startLunchCountdown();
        }
        if (currentState === "break" && typeof startBreakCountdown === "function") {
            startBreakCountdown();
        }
    }

    async function sendHeartbeat() {
        try {
            const r = await fetch("/api/heartbeat", {
                method: "POST",
                headers: jsonHeaders(),
                credentials: "same-origin",
                body: JSON.stringify({ state: currentState }),
            });
            if (!r.ok) {
                // 401 / 500 etc. — don't flash the user as offline based on a
                // transient response. The next worker tick (or watchdog) will
                // retry; keep the current UI state until then.
                if (DEBUG) console.warn("[heartbeat] non-ok", r.status);
                return;
            }
            let data = null;
            try { data = await r.json(); } catch { /* non-json */ }
            applyServerState(data);
        } catch (err) {
            console.warn("heartbeat failed", err);
            // Network-level failure (offline, DNS, etc.) — but don't yank the
            // KPI to "Offline" on a single dropped request; let the watchdog
            // retry. We only mark explicit offline on lock-screen / pagehide.
        }
    }

    // -------------------------------------------------------------------
    // One-shot precise-location request
    // -------------------------------------------------------------------
    // IP-based geolocation in PH (Converge / PLDT / Globe) often misses
    // the actual city by 30–100 km because ISPs pool subscriber IPs
    // across whole regions. This bumps the device-location display to
    // browser GPS / Wi-Fi triangulation when the user grants permission.
    //   * Only fires once per browser session (sessionStorage flag).
    //   * Only fires for authenticated pages (body[data-authed=1]).
    //   * Silent on denial — the IP-based label stays in place.
    function requestPreciseLocationOnce() {
        try {
            if (document.body?.dataset?.authed !== "1") return;
            if (!("geolocation" in navigator)) return;
            if (sessionStorage.getItem("loc_sent") === "1") return;
        } catch { return; }

        // Some browsers reject geolocation on http://; we deploy under
        // https so this is just a defensive check for local dev.
        if (location.protocol !== "https:" && location.hostname !== "localhost"
            && location.hostname !== "127.0.0.1") {
            return;
        }

        navigator.geolocation.getCurrentPosition(
            async (pos) => {
                try {
                    const body = {
                        latitude: pos.coords.latitude,
                        longitude: pos.coords.longitude,
                        accuracy: pos.coords.accuracy,
                    };
                    const res = await fetch("/api/location/set", {
                        method: "POST",
                        headers: jsonHeaders(),
                        credentials: "same-origin",
                        body: JSON.stringify(body),
                    });
                    if (res.ok) {
                        sessionStorage.setItem("loc_sent", "1");
                        if (DEBUG) console.log("[location] precise location stored");
                    } else if (DEBUG) {
                        console.warn("[location] set non-ok", res.status);
                    }
                } catch (err) {
                    if (DEBUG) console.warn("[location] post failed", err);
                }
            },
            (err) => {
                // PERMISSION_DENIED = 1, POSITION_UNAVAILABLE = 2, TIMEOUT = 3.
                // For permission-denied we set the sentinel so we don't keep
                // re-prompting on every navigation; for transient errors we
                // leave it unset so a later page can retry.
                if (err && err.code === 1) {
                    try { sessionStorage.setItem("loc_sent", "1"); } catch {}
                }
                if (DEBUG) console.log("[location] unavailable", err?.code, err?.message);
            },
            { enableHighAccuracy: true, maximumAge: 5 * 60 * 1000, timeout: 12000 },
        );
    }

    // -------------------------------------------------------------------
    // Heartbeat Web Worker
    // -------------------------------------------------------------------
    // Main-thread setInterval gets clamped to ~1 minute on hidden tabs
    // (Chromium "intensive throttling"). With HEARTBEAT_MS=20s and the
    // server threshold at ~120s, an unlucky minimize can drift LastSeen
    // past the threshold and flash the user as "offline" on the admin
    // dashboard. Dedicated workers run on their own thread and are far
    // less aggressively throttled, so we drive the cadence from there.
    // The worker performs the fetch directly (same-origin cookies are
    // included) and reports success/failure back for the topbar dot.
    let heartbeatWorker = null;

    function createHeartbeatWorker() {
        if (typeof Worker !== "function") {
            if (DEBUG) console.warn("[heartbeat] Worker API unavailable; using main-thread fallback.");
            return null;
        }
        try {
            const src = `
                let timer = null;
                let state = "online";
                async function tick() {
                    try {
                        const r = await fetch("/api/heartbeat", {
                            method: "POST",
                            credentials: "same-origin",
                            headers: {
                                "Content-Type": "application/json",
                                "X-Requested-With": "fetch"
                            },
                            body: JSON.stringify({ state })
                        });
                        let server = null;
                        try { server = await r.json(); } catch (_) {}
                        postMessage({ ok: r.ok, status: r.status, ts: Date.now(), state, server });
                    } catch (e) {
                        postMessage({ ok: false, error: String(e), ts: Date.now(), state });
                    }
                }
                self.onmessage = (e) => {
                    const cmd = e.data;
                    if (cmd && typeof cmd === "object" && cmd.type === "state") {
                        state = cmd.value;
                        return;
                    }
                    if (cmd === "start") {
                        if (timer === null) {
                            tick();
                            timer = setInterval(tick, ${HEARTBEAT_MS});
                        }
                    } else if (cmd === "stop") {
                        if (timer !== null) { clearInterval(timer); timer = null; }
                    } else if (cmd === "ping") {
                        tick();
                    }
                };
            `;
            const blob = new Blob([src], { type: "application/javascript" });
            const w = new Worker(URL.createObjectURL(blob));
            w.onmessage = (e) => {
                const ok = !!(e.data && e.data.ok);
                if (ok && e.data.server) {
                    applyServerState(e.data.server);
                } else if (ok) {
                    // Server responded 2xx but body wasn't parseable — keep
                    // current UI state, don't flip to offline.
                }
                if (DEBUG) {
                    if (ok) console.log("[heartbeat] tick ok", e.data);
                    else console.warn("[heartbeat] tick failed", e.data);
                }
            };
            w.onerror = (err) => {
                if (DEBUG) console.error("[heartbeat] worker error", err);
                // Don't flip the UI to "offline" on a worker-internal error.
                // The main-thread watchdog will continue heartbeating, and the
                // real offline transition is driven by the lock-screen path.
            };
            if (DEBUG) console.log("[heartbeat] worker created");
            return w;
        } catch (err) {
            if (DEBUG) console.error("[heartbeat] worker creation failed", err);
            return null;
        }
    }

    // Anchor heartbeat — invoked from main thread on visibility hide so the
    // server records LastSeen at the moment of minimize, removing the
    // window where a throttled tick could land past the online threshold.
    function pingHeartbeat() {
        if (heartbeatWorker) {
            // Push the latest state into the worker so the immediate tick
            // it sends carries the right label.
            heartbeatWorker.postMessage({ type: "state", value: currentState });
            heartbeatWorker.postMessage("ping");
        } else {
            sendHeartbeat();
        }
    }

    const PH_TZ = "Asia/Manila";
    const phDateTimeFmt = new Intl.DateTimeFormat("en-CA", {
        timeZone: PH_TZ,
        year: "numeric", month: "2-digit", day: "2-digit",
        hour: "2-digit", minute: "2-digit",
        hour12: false,
    });
    const phTimeFmt = new Intl.DateTimeFormat("en-GB", {
        timeZone: PH_TZ,
        hour: "2-digit", minute: "2-digit",
        hour12: false,
    });

    function fmtTime(iso) {
        if (!iso) return "never";
        const d = new Date(iso);
        // en-CA gives "YYYY-MM-DD, HH:MM" — normalize to "YYYY-MM-DD HH:MM PHT"
        const s = phDateTimeFmt.format(d).replace(",", "");
        return `${s} PHT`;
    }

    function fmtHHMM(iso) {
        if (!iso) return "";
        return phTimeFmt.format(new Date(iso));
    }

    // Mirror C# PhTime.FormatDurationMinutes — renders an attendance
    // duration as "HH:MM" (e.g. 510 → "08:30") so the modal matches the
    // standalone Attendance page.
    function fmtDurationMinutes(mins) {
        const n = Number(mins);
        if (!Number.isFinite(n) || n <= 0) return "00:00";
        const h = Math.floor(n / 60);
        const m = n % 60;
        return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
    }

    function renderRow(tr, u) {
        const dot = tr.querySelector('[data-cell="dot"]');
        const statusText = tr.querySelector('[data-cell="status-text"]');
        const lastSeen = tr.querySelector('[data-cell="last-seen"]');
        const clockIn = tr.querySelector('[data-cell="clock-in"]');
        const clockOut = tr.querySelector('[data-cell="clock-out"]');
        const duration = tr.querySelector('[data-cell="duration"]');
        const unit = tr.querySelector('[data-cell="unit"]');
        const businessUnit = tr.querySelector('[data-cell="business-unit"]');
        const offlineFor = tr.querySelector('[data-cell="offline-for"]');
        const workTypeFlag = tr.querySelector('[data-cell="worktype-flag"]');

        if (dot) {
            const s = u.state || (u.online ? "online" : "offline");
            dot.classList.remove("dot-online", "dot-away", "dot-lunch", "dot-break", "dot-offline");
            dot.classList.add(`dot-${s}`);
            dot.title = s === "online" ? "Online"
                       : s === "away" ? "Away"
                       : s === "lunch" ? "On lunch"
                       : s === "break" ? "On break"
                       : "Offline";
        }
        if (statusText) {
            const s = u.state || (u.online ? "online" : "offline");
            let label = s === "online" ? "Online"
                       : s === "away" ? "Away"
                       : s === "lunch" ? "Lunch"
                       : s === "break" ? "Break"
                       : "Offline";
            if (s === "lunch" && u.lunch_ends_at) {
                const left = Math.max(0, Math.round(
                    (new Date(u.lunch_ends_at).getTime() - Date.now()) / 60000));
                label = `Lunch (${left}m left)`;
            } else if (s === "break" && u.break_ends_at) {
                const left = Math.max(0, Math.round(
                    (new Date(u.break_ends_at).getTime() - Date.now()) / 60000));
                label = `Break (${left}m left)`;
            }
            statusText.textContent = label;
        }
        if (workTypeFlag) {
            const wt = (u.today_work_type || "").toLowerCase();
            workTypeFlag.hidden = false;
            workTypeFlag.className = "badge";
            workTypeFlag.style.marginLeft = "6px";
            if (wt === "onleave") {
                workTypeFlag.classList.add("badge-active");
                workTypeFlag.textContent = "Onleave";
            } else if (wt === "holiday") {
                workTypeFlag.classList.add("badge-warn");
                workTypeFlag.textContent = "Holiday";
            } else {
                workTypeFlag.textContent = "";
                workTypeFlag.hidden = true;
            }
        }
        if (lastSeen) lastSeen.textContent = u.last_seen ? fmtTime(u.last_seen) : "never";

        const dash = '<span class="muted">&mdash;</span>';
        if (clockIn) {
            clockIn.innerHTML = u.checked_in ? fmtHHMM(u.check_in) : dash;
        }
        if (clockOut) {
            if (u.check_out) {
                clockOut.innerHTML = fmtHHMM(u.check_out);
            } else if (u.checked_in) {
                clockOut.innerHTML = '<span class="badge badge-active">Working</span>';
            } else {
                clockOut.innerHTML = dash;
            }
        }
        if (duration) {
            duration.innerHTML = (u.checked_in && u.check_out)
                ? fmtDurationMinutes(u.duration_minutes)
                : dash;
        }
        if (businessUnit) {
            const bu = (u.business_unit || "").trim();
            if (bu) {
                businessUnit.textContent = "";
                const badge = document.createElement("span");
                badge.className = "badge badge-bu";
                badge.textContent = bu;
                businessUnit.appendChild(badge);
            } else {
                businessUnit.innerHTML = dash;
            }
        }
        if (unit) {
            const loc = u.last_login_location;
            const host = u.last_login_host;
            const ip = u.last_login_ip;
            if (loc || host || ip) {
                const tipParts = [];
                if (u.last_login_at) tipParts.push(`Signed in ${fmtTime(u.last_login_at)}`);
                if (host) tipParts.push(`Host: ${host}`);
                const tip = tipParts.join(" \u2022 ");
                const topLine = loc || host || "-";
                const wrapper = document.createElement("span");
                wrapper.className = "unit-cell";
                if (tip) wrapper.title = tip;

                const hostEl = document.createElement("span");
                hostEl.className = "unit-host";
                hostEl.textContent = topLine;

                const ipEl = document.createElement("span");
                ipEl.className = "unit-ip muted";
                ipEl.textContent = ip || "-";

                wrapper.append(hostEl, ipEl);
                unit.replaceChildren(wrapper);
            } else {
                unit.innerHTML = dash;
            }
        }
        if (offlineFor) {
            // -1 = online (no offline duration); -2 = never seen.
            if (u.online) {
                offlineFor.dataset.offlineMinutes = "-1";
                offlineFor.innerHTML = dash;
            } else if (u.last_seen) {
                const ms = Date.now() - new Date(u.last_seen).getTime();
                const minutes = Math.max(0, Math.round(ms / 60000));
                offlineFor.dataset.offlineMinutes = String(minutes);
                offlineFor.textContent = formatOfflineFor(minutes);
            } else {
                offlineFor.dataset.offlineMinutes = "-2";
                offlineFor.innerHTML = '<span class="muted">never</span>';
            }
        }
    }

    function formatOfflineFor(minutes) {
        if (minutes < 1) return "< 1 m";
        if (minutes < 60) return `${minutes} m`;
        const h = Math.floor(minutes / 60);
        const m = minutes % 60;
        if (minutes < 60 * 24) return m === 0 ? `${h} h` : `${h} h ${m} m`;
        const d = Math.floor(minutes / (60 * 24));
        const hr = Math.floor((minutes % (60 * 24)) / 60);
        return hr === 0 ? `${d} d` : `${d} d ${hr} h`;
    }

    // Re-render only the offline-for cells from existing data attributes.
    // Used between /api/status refreshes so the duration ticks up live.
    function refreshOfflineForCells() {
        const table = document.getElementById("status-table");
        if (!table) return;
        for (const cell of table.querySelectorAll('[data-cell="offline-for"]')) {
            const tr = cell.closest("tr");
            // Online / Away / Lunch rows have no offline duration to show.
            const dotEl = tr?.querySelector('[data-cell="dot"]');
            const isOffline = dotEl?.classList.contains("dot-offline");
            if (!isOffline) {
                cell.dataset.offlineMinutes = "-1";
                cell.innerHTML = '<span class="muted">&mdash;</span>';
                continue;
            }
            const stored = parseInt(cell.dataset.offlineMinutes || "-2", 10);
            if (stored === -2) continue; // never seen — leave alone
            const next = (stored < 0 ? 0 : stored) + 1;
            cell.dataset.offlineMinutes = String(next);
            cell.textContent = formatOfflineFor(next);
        }
    }

    async function refreshDashboard() {
        const table = document.getElementById("status-table");
        if (!table) return;

        try {
            const res = await fetch("/api/status", { credentials: "same-origin" });
            if (!res.ok) return;
            const data = await res.json();

            const setKpi = (key, val) => {
                const el = document.querySelector(`[data-kpi="${key}"]`);
                if (el) el.textContent = val;
            };
            setKpi("online", data.totals.online);
            setKpi("away", data.totals.away ?? 0);
            setKpi("lunch", data.totals.lunch ?? 0);
            setKpi("offline", data.totals.offline);
            setKpi("present", data.totals.present_today);
            setKpi("total", data.totals.total_users);

            for (const u of data.users) {
                const tr = table.querySelector(`tr[data-user-id="${u.id}"]`);
                if (tr) renderRow(tr, u);
            }

            // Re-apply the user's current sort: cell values changed, but the
            // selected column should still drive row order across refreshes.
            applyTableSort();

            const stamp = document.getElementById("last-refresh");
            if (stamp) {
                stamp.textContent = `updated ${phTimeFmt.format(new Date())} PHT`;
            }
        } catch (err) {
            console.warn("refresh failed", err);
        }
    }

    // ---------------------------------------------------------------
    // Sortable team-status table
    // ---------------------------------------------------------------
    const tableSort = { key: "name", dir: "asc" };

    // Per-column comparators read straight from the rendered cells so the
    // table stays sortable after each /api/status refresh without needing
    // any extra data attributes on the row.
    const sortValueByKey = {
        status: (tr) => {
            const dot = tr.querySelector('[data-cell="dot"]');
            if (!dot) return 4;
            if (dot.classList.contains("dot-online")) return 0;
            if (dot.classList.contains("dot-lunch")) return 1;
            if (dot.classList.contains("dot-away")) return 2;
            return 3; // offline
        },
        name: (tr) => (tr.querySelector('[data-cell="name"]')?.textContent || "").trim().toLowerCase(),
        "business-unit": (tr) => {
            const txt = (tr.querySelector('[data-cell="business-unit"]')?.textContent || "").trim();
            // Empty cells (dash) sort last in asc.
            return txt && txt !== "—" ? txt.toLowerCase() : "\uffff";
        },
        "clock-in": (tr) => parseTimeMinutes(tr.querySelector('[data-cell="clock-in"]')),
        "clock-out": (tr) => {
            const cell = tr.querySelector('[data-cell="clock-out"]');
            if (!cell) return Number.POSITIVE_INFINITY;
            // "Working" badge sorts after concrete check-out times in asc.
            if (cell.querySelector(".badge-active")) return 100000;
            return parseTimeMinutes(cell);
        },
        duration: (tr) => {
            const cell = tr.querySelector('[data-cell="duration"]');
            const txt = (cell?.textContent || "").trim();
            const m = txt.match(/^(\d+)/);
            return m ? parseInt(m[1], 10) : -1;
        },
        "last-seen": (tr) => {
            const txt = (tr.querySelector('[data-cell="last-seen"]')?.textContent || "").trim();
            // "never" sorts before any timestamp ascending; "" works for that.
            return txt.toLowerCase() === "never" ? "" : txt;
        },
        unit: (tr) => {
            // Sort by location (top line) first, fall back to IP. Empty
            // cells sort last in ascending order.
            const cell = tr.querySelector('[data-cell="unit"]');
            const top = (cell?.querySelector(".unit-host")?.textContent || "").trim();
            if (top && top !== "-") return top.toLowerCase();
            const ip = (cell?.querySelector(".unit-ip")?.textContent || "").trim();
            if (ip && ip !== "-") return ip.toLowerCase();
            return "\uffff"; // missing sorts last in asc
        },
        "offline-for": (tr) => {
            // -1 = online (sort first asc), -2 = never (sort last asc),
            // anything else = minutes offline.
            const raw = tr.querySelector('[data-cell="offline-for"]')?.dataset.offlineMinutes;
            const v = parseInt(raw ?? "-1", 10);
            if (v === -1) return -1;
            if (v === -2) return Number.MAX_SAFE_INTEGER;
            return v;
        },
    };

    function parseTimeMinutes(cell) {
        const txt = (cell?.textContent || "").trim();
        const m = txt.match(/^(\d{1,2}):(\d{2})/);
        if (!m) return 99999; // missing/dash sorts last in asc
        return parseInt(m[1], 10) * 60 + parseInt(m[2], 10);
    }

    function applyTableSort() {
        const table = document.getElementById("status-table");
        if (!table) return;

        const tbody = table.tBodies[0];
        if (!tbody) return;

        const extract = sortValueByKey[tableSort.key];
        if (!extract) return;

        const rows = Array.from(tbody.rows);
        const dir = tableSort.dir === "desc" ? -1 : 1;

        rows.sort((a, b) => {
            const av = extract(a);
            const bv = extract(b);
            if (av === bv) return 0;
            if (typeof av === "number" && typeof bv === "number") {
                return (av - bv) * dir;
            }
            return String(av).localeCompare(String(bv)) * dir;
        });

        // Re-append in sorted order (moves existing nodes; preserves listeners).
        for (const r of rows) tbody.appendChild(r);

        // Update header indicators
        for (const th of table.querySelectorAll("th.sortable")) {
            th.classList.remove("sort-asc", "sort-desc");
            if (th.dataset.sortKey === tableSort.key) {
                th.classList.add(tableSort.dir === "desc" ? "sort-desc" : "sort-asc");
            }
        }
    }

    function setupTableSort() {
        const table = document.getElementById("status-table");
        if (!table) return;
        const thead = table.tHead;
        if (!thead) return;

        thead.addEventListener("click", (e) => {
            const th = e.target.closest("th.sortable");
            if (!th) return;
            const key = th.dataset.sortKey;
            if (!key) return;

            if (tableSort.key === key) {
                tableSort.dir = tableSort.dir === "asc" ? "desc" : "asc";
            } else {
                tableSort.key = key;
                tableSort.dir = "asc";
            }
            applyTableSort();
        });

        // Initial render: sort by name asc to match server output (no-op visually).
        applyTableSort();
    }

    setupTableSort();

    // ---------------------------------------------------------------
    // Global confirmation for "extract / export" actions
    // ---------------------------------------------------------------
    // Any anchor or button decorated with `data-confirm="…"` pops a
    // native confirm() before the click proceeds. Used for every Excel /
    // CSV export so an admin doesn't accidentally trigger a heavy
    // server-side render with the wrong filters selected.
    document.addEventListener("click", (evt) => {
        const trigger = evt.target.closest("[data-confirm]");
        if (!trigger) return;
        // Skip when the button is disabled or already confirmed in this click.
        if (trigger.disabled || trigger.dataset.confirmInFlight === "1") return;
        const msg = trigger.getAttribute("data-confirm") || "Are you sure?";
        if (!window.confirm(msg)) {
            evt.preventDefault();
            evt.stopPropagation();
            return;
        }
        // Mark briefly so re-click during navigation doesn't double-prompt.
        trigger.dataset.confirmInFlight = "1";
        setTimeout(() => { delete trigger.dataset.confirmInFlight; }, 2000);
    }, true);

    function setSelfDot(state) {
        const dot = document.getElementById("self-status-dot");
        // Accept either a tri-state string or a legacy boolean.
        let s = state;
        if (typeof s === "boolean") s = s ? "online" : "offline";
        if (s !== "online" && s !== "away" && s !== "lunch" && s !== "break" && s !== "offline") s = "offline";

        const title = s === "online" ? "Online"
                    : s === "away" ? "Away"
                    : s === "lunch" ? "On lunch"
                    : s === "break" ? "On break"
                    : "Offline";

        // Topbar pill dot
        if (dot) {
            dot.classList.remove("dot-online", "dot-away", "dot-lunch", "dot-break", "dot-offline");
            dot.classList.add(`dot-${s}`);
            dot.title = title;
        }

        // Employee dashboard "My status" KPI card (only present on that
        // page). Reflect both the dot color and the label so the user
        // can see at a glance whether they're online / on break / on
        // lunch right after clicking the action button.
        const kpiDot = document.getElementById("self-state-dot");
        const kpiLabel = document.getElementById("self-state-label");
        const kpiCard = document.getElementById("self-state-card");
        const kpiSub = document.getElementById("self-state-sub");
        if (kpiDot) {
            kpiDot.classList.remove("dot-online", "dot-away", "dot-lunch", "dot-break", "dot-offline");
            kpiDot.classList.add(`dot-${s}`);
        }
        if (kpiLabel) kpiLabel.textContent = title;
        if (kpiCard) {
            kpiCard.classList.remove("kpi-online", "kpi-away", "kpi-lunch", "kpi-break", "kpi-offline");
            kpiCard.classList.add(`kpi-${s}`);
        }
        if (kpiSub) {
            // Show remaining minutes when on lunch or break so the user
            // doesn't have to look at the button label.
            if (s === "lunch" && typeof lunchEndsAt === "number" && lunchEndsAt) {
                const m = Math.max(0, Math.round((lunchEndsAt - Date.now()) / 60000));
                kpiSub.textContent = m > 0 ? `${m}m remaining` : "ending…";
            } else if (s === "break" && typeof breakEndsAt === "number" && breakEndsAt) {
                const m = Math.max(0, Math.round((breakEndsAt - Date.now()) / 60000));
                kpiSub.textContent = m > 0 ? `${m}m remaining` : "ending…";
            } else {
                kpiSub.textContent = "";
            }
        }
    }

    function sendOffline(useBeacon) {
        // sendBeacon survives tab close / navigation away; fall back to fetch.
        try {
            if (useBeacon && navigator.sendBeacon) {
                navigator.sendBeacon("/api/offline", new Blob([], { type: "application/json" }));
            } else {
                fetch("/api/offline", {
                    method: "POST",
                    headers: jsonHeaders(),
                    credentials: "same-origin",
                    keepalive: true,
                });
            }
        } catch (err) {
            // best-effort; nothing else to do
        }
        setSelfDot(false);
    }

    let heartbeatTimer = null;
    let refreshTimer = null;
    let offlineForTimer = null;

    function startTimers() {
        if (heartbeatWorker) {
            heartbeatWorker.postMessage("start");
        } else if (heartbeatTimer === null) {
            // Fallback: main-thread interval. Throttled on hidden tabs but
            // still better than nothing on browsers without Worker support.
            heartbeatTimer = setInterval(sendHeartbeat, HEARTBEAT_MS);
        }
        if (refreshTimer === null) {
            refreshTimer = setInterval(refreshDashboard, REFRESH_MS);
        }
        if (offlineForTimer === null) {
            // Bump the "Offline for" cells every 60s so durations tick
            // up live between full /api/status refreshes.
            offlineForTimer = setInterval(() => {
                refreshOfflineForCells();
                applyTableSort();
            }, 60000);
        }
    }

    function stopTimers() {
        if (heartbeatWorker) heartbeatWorker.postMessage("stop");
        if (heartbeatTimer !== null) { clearInterval(heartbeatTimer); heartbeatTimer = null; }
        if (refreshTimer !== null) { clearInterval(refreshTimer); refreshTimer = null; }
        if (offlineForTimer !== null) { clearInterval(offlineForTimer); offlineForTimer = null; }
    }

    // -------------------------------------------------------------------
    // Presence policy: Online unless Windows is locked
    // -------------------------------------------------------------------
    // We treat the user as Online whenever a heartbeat is landing. There
    // is no "Away" tier — being away from the keyboard while the screen
    // is still unlocked still counts as Online. The transitions out of
    // Online are:
    //   - User starts Lunch  → "lunch"
    //   - Windows locks      → "offline" (HTTPS only, via IdleDetector)
    //   - Heartbeat stops    → "offline" (server-side, after threshold)

    // Keep the user marked Online whenever the tab is alive. Lunch and
    // explicit offline (lock-screen) are user/OS-controlled and untouched.
    function recomputeFromActivity() {
        // Don't blindly flip the user back to "online" while they're on
        // an active lunch or short break — those are user-controlled
        // states with countdowns and have to live until the user ends
        // them (or they auto-expire server-side). Also leave "offline"
        // alone; that's the lock-screen path.
        if (currentState === "lunch"
            || currentState === "break"
            || currentState === "offline") return;
        if (currentState !== "online") setState("online");
    }

    // Always-on watchdog: a main-thread interval that fires every 30s
    // regardless of tab visibility. On hidden tabs Chromium will clamp
    // this to ~1/min (intensive throttling), which is still well under
    // the server's 5-min online threshold. This is the belt-and-suspenders
    // path that keeps presence flowing even if the dedicated Web Worker
    // gets frozen or fails to start.
    let watchdogTimer = null;
    function startWatchdog() {
        if (watchdogTimer !== null) return;
        watchdogTimer = setInterval(() => {
            recomputeFromActivity();
            // Direct main-thread heartbeat as a redundant path.
            sendHeartbeat();
        }, 30000);
    }
    startWatchdog();
    // Kick things off
    heartbeatWorker = createHeartbeatWorker();
    sendHeartbeat();
    refreshDashboard();
    startTimers();
    requestPreciseLocationOnce();

    // Tab hidden (minimize / switch tab / Windows lock screen): keep the
    // heartbeat running so a minimized browser still counts as "online".
    // The cadence ticks come from a dedicated Web Worker which is far
    // less throttled than the main thread on hidden tabs. We also send
    // an immediate "anchor" heartbeat the moment we go hidden so the
    // server records LastSeen at the minimize instant — that closes the
    // gap where browser throttling could otherwise delay the next tick
    // past the OnlineThresholdSeconds window and briefly flash the user
    // as offline on the admin dashboard.
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "visible") {
            // Coming back to the foreground: refresh immediately so the
            // dashboard catches up without waiting for the next tick.
            if (DEBUG) console.log("[heartbeat] visible → ping + restart timers");
            pingHeartbeat();
            refreshDashboard();
            startTimers();
        } else if (document.visibilityState === "hidden") {
            // Anchor LastSeen to "now" so the worker's next throttled
            // tick has plenty of headroom under the server threshold.
            if (DEBUG) console.log("[heartbeat] hidden → anchor ping");
            pingHeartbeat();
        }
    });

    // Tab/browser closing or page being unloaded — this is the only path
    // that explicitly marks the user offline.
    window.addEventListener("pagehide", () => {
        stopTimers();
        sendOffline(true);
    });
    window.addEventListener("beforeunload", () => {
        stopTimers();
        sendOffline(true);
    });

    // ---------------------------------------------------------------
    // Windows lock-screen + idle detection (Idle Detection API)
    // ---------------------------------------------------------------
    // The browser can't directly know "the user locked Windows" or
    // "the user stepped away", but the Idle Detection API exposes:
    //   * screenState = "locked" | "unlocked"   -> OS lock state
    //   * userState   = "active" | "idle"       -> no input across all apps
    //
    // Policy applied here:
    //   IF screenState === locked              -> OFFLINE  (clear LastSeen)
    //   ELSE IF userState === idle (15+ min)   -> AWAY     (still online,
    //                                                       just paused)
    //   ELSE                                   -> ONLINE
    //
    // Critically: minimizing the browser, switching tabs, and working in
    // another app do NOT flip state -- those are all still "online" because
    // the heartbeat (Web Worker) keeps firing and the Idle Detection API
    // reports userState=active as long as there's keyboard/mouse activity
    // anywhere on the OS.
    //
    // Requires HTTPS + a user gesture to request permission. When the API
    // is unavailable or denied, we stay in "online" mode and rely on the
    // server's LastSeen-aging fallback.
    let idleDetector = null;

    function recomputeFromIdle(detector) {
        if (!detector) return;
        // Don't override a manual lunch break — that state is user-controlled
        // and stays in effect until the user ends it (or it auto-expires
        // server-side after Constants.LunchBreakMinutes).
        if (currentState === "lunch") return;

        // Policy: presence is Online unless the workstation is locked.
        // The Idle Detection API's userState === "idle" signal is
        // intentionally ignored — being away from the keyboard while the
        // screen is still unlocked still counts as Online.
        if (detector.screenState === "locked") {
            // Locked workstation → explicitly offline. Clear LastSeen on
            // the server so the dashboard flips immediately instead of
            // waiting for the heartbeat threshold to age out.
            if (currentState !== "offline") {
                stopTimers();
                setState("offline");
                sendOffline(false);
            }
        } else {
            // Screen unlocked → Online. Resume timers if we were offline.
            if (currentState === "offline") startTimers();
            if (currentState !== "online") {
                setState("online");
                refreshDashboard();
            }
        }
    }

    async function activateIdleDetector() {
        if (idleDetector) return;                    // already running
        if (!("IdleDetector" in window)) return;     // unsupported browser
        if (!window.isSecureContext) return;         // requires HTTPS

        try {
            const perm = await IdleDetector.requestPermission();
            if (perm !== "granted") return;
        } catch {
            return; // user dismissed or feature gated
        }

        try {
            const detector = new IdleDetector();
            detector.addEventListener("change", () => recomputeFromIdle(detector));
            // 15-min idle threshold per policy. The API also fires
            // screenState change events immediately on lock/unlock
            // regardless of this value.
            await detector.start({ threshold: IDLE_THRESHOLD_MS });
            idleDetector = detector;
            recomputeFromIdle(detector); // seed from the initial state
        } catch {
            idleDetector = null;
        }
    }

    // Permission must be triggered by a user gesture. Attach once and
    // remove the listeners as soon as we've tried.
    function armIdleDetectorOnGesture() {
        const handler = () => {
            window.removeEventListener("pointerdown", handler);
            window.removeEventListener("keydown", handler);
            activateIdleDetector();
        };
        window.addEventListener("pointerdown", handler, { once: true });
        window.addEventListener("keydown", handler, { once: true });
    }
    armIdleDetectorOnGesture();

    // ---------------------------------------------------------------
    // Lunch break toggle (topbar button)
    // ---------------------------------------------------------------
    // Manual one-click way to suppress offline-alert emails for an hour.
    // Server auto-expires the break after Constants.LunchBreakMinutes; the
    // client also flips itself back to "online" when the countdown hits 0.
    const lunchBtn = document.getElementById("lunch-toggle");
    let lunchCountdownTimer = null;
    if (lunchBtn) {
        lunchesMax = parseInt(lunchBtn.dataset.lunchMax || "1", 10) || 1;
    }

    function renderLunchButton() {
        if (!lunchBtn) return;
        const startLabel = lunchBtn.dataset.lunchStartLabel || "Start lunch";
        const endLabel = lunchBtn.dataset.lunchEndLabel || "End lunch";
        const remaining = Math.max(0, lunchesMax - lunchesUsed);
        if (currentState === "lunch" && lunchEndsAt) {
            const left = Math.max(0, Math.round((lunchEndsAt - Date.now()) / 60000));
            lunchBtn.textContent = `${endLabel} (${left}m)`;
            lunchBtn.classList.add("is-active");
            lunchBtn.disabled = false;
            lunchBtn.title = "End your lunch early.";
        } else {
            // Show "Start lunch (1/1)" so the daily allowance is visible at
            // a glance. The button is disabled once the quota is consumed.
            lunchBtn.textContent = `${startLabel} (${remaining}/${lunchesMax})`;
            lunchBtn.classList.remove("is-active");
            lunchBtn.disabled = remaining <= 0;
            lunchBtn.title = remaining <= 0
                ? `You've already used your ${lunchesMax === 1 ? "lunch break" : `${lunchesMax} lunch breaks`} for today.`
                : "Start your one-hour lunch break. Pauses offline alerts.";
        }
        // Refresh the "My status" KPI sub-text so the minutes-remaining
        // value tracks the button label one-for-one.
        setSelfDot(currentState);
    }

    function startLunchCountdown() {
        if (lunchCountdownTimer !== null) return;
        lunchCountdownTimer = setInterval(() => {
            if (currentState !== "lunch" || lunchEndsAt === null) {
                stopLunchCountdown();
                return;
            }
            if (Date.now() >= lunchEndsAt) {
                // Locally auto-resume so the UI catches up even before the
                // next heartbeat round-trip clears the server-side flag.
                lunchEndsAt = null;
                setState("online");
                renderLunchButton();
                stopLunchCountdown();
                return;
            }
            renderLunchButton();
        }, 30000); // update label every 30s
    }

    function stopLunchCountdown() {
        if (lunchCountdownTimer !== null) {
            clearInterval(lunchCountdownTimer);
            lunchCountdownTimer = null;
        }
    }

    async function startLunch() {
        if (!lunchBtn) return;
        lunchBtn.disabled = true;
        try {
            const csrf = document.querySelector('input[name="__RequestVerificationToken"]');
            const headers = { "Content-Type": "application/json" };
            if (csrf) headers["RequestVerificationToken"] = csrf.value;
            const res = await fetch("/api/lunch/start", {
                method: "POST",
                credentials: "same-origin",
                headers,
            });
            if (!res.ok) {
                // 400 = daily quota exhausted; absorb the counter from the
                // body so the button shows "(0/1)" and stays disabled.
                try {
                    const err = await res.json();
                    if (err && typeof err.lunches_used === "number") {
                        lunchesUsed = err.lunches_used;
                    }
                    if (err && typeof err.lunches_max === "number") {
                        lunchesMax = err.lunches_max;
                    }
                } catch { /* non-json error */ }
                renderLunchButton();
                if (res.status === 400) {
                    alert(`You've already used your ${lunchesMax === 1 ? "one" : lunchesMax} lunch break${lunchesMax === 1 ? "" : "s"} for today.`);
                } else {
                    console.warn("lunch start failed", res.status);
                }
                return;
            }
            const data = await res.json();
            lunchEndsAt = data.lunch_ends_at ? new Date(data.lunch_ends_at).getTime() : null;
            if (typeof data.lunches_used === "number") lunchesUsed = data.lunches_used;
            if (typeof data.lunches_max === "number")  lunchesMax  = data.lunches_max;
            currentState = "lunch"; // bypass setState dedup; force ping below
            setSelfDot("lunch");
            pingHeartbeat();
            refreshDashboard();
            renderLunchButton();
            if (typeof renderBreakButton === "function") renderBreakButton();
            startLunchCountdown();
        } catch (err) {
            console.warn("lunch start error", err);
        } finally {
            // Don't blindly re-enable: renderLunchButton handles disabled
            // based on the current quota.
            renderLunchButton();
        }
    }

    async function endLunch() {
        if (!lunchBtn) return;
        lunchBtn.disabled = true;
        try {
            const csrf = document.querySelector('input[name="__RequestVerificationToken"]');
            const headers = { "Content-Type": "application/json" };
            if (csrf) headers["RequestVerificationToken"] = csrf.value;
            const res = await fetch("/api/lunch/end", {
                method: "POST",
                credentials: "same-origin",
                headers,
            });
            if (!res.ok) {
                console.warn("lunch end failed", res.status);
                return;
            }
            let data = null;
            try { data = await res.json(); } catch { /* non-json */ }
            if (data && typeof data.lunches_used === "number") {
                lunchesUsed = data.lunches_used;
            }
            if (data && typeof data.lunches_max === "number") {
                lunchesMax = data.lunches_max;
            }
            lunchEndsAt = null;
            currentState = "online";
            setSelfDot("online");
            pingHeartbeat();
            refreshDashboard();
            renderLunchButton();
            if (typeof renderBreakButton === "function") renderBreakButton();
            stopLunchCountdown();
        } catch (err) {
            console.warn("lunch end error", err);
        } finally {
            renderLunchButton();
        }
    }

    if (lunchBtn) {
        lunchBtn.addEventListener("click", () => {
            if (currentState === "lunch") endLunch();
            else startLunch();
        });
        renderLunchButton();
    }

    // ---------------------------------------------------------------
    // Short break toggle (topbar) — up to 2× 15-minute breaks per day
    // ---------------------------------------------------------------
    // Same idea as the lunch toggle but with a daily quota tracked
    // server-side. The button labels itself with remaining minutes
    // while a break is active and with the breaks-left count otherwise.
    const breakBtn = document.getElementById("break-toggle");
    let breakCountdownTimer = null;
    if (breakBtn) {
        breaksMax = parseInt(breakBtn.dataset.breakMax || "2", 10) || 2;
    }

    function renderBreakButton() {
        if (!breakBtn) return;
        const startLabel = breakBtn.dataset.breakStartLabel || "Break";
        const endLabel = breakBtn.dataset.breakEndLabel || "End break";
        const left = Math.max(0, breaksMax - breaksUsed);
        if (currentState === "break" && breakEndsAt) {
            const minsLeft = Math.max(0, Math.round((breakEndsAt - Date.now()) / 60000));
            breakBtn.textContent = `${endLabel} (${minsLeft}m)`;
            breakBtn.classList.add("is-active");
            breakBtn.disabled = false;
            breakBtn.title = "End the current break early.";
        } else {
            breakBtn.textContent = `${startLabel} (${left}/${breaksMax})`;
            breakBtn.classList.remove("is-active");
            const onLunch = currentState === "lunch";
            breakBtn.disabled = left <= 0 || onLunch;
            if (onLunch) {
                breakBtn.title = "End your lunch first to take a short break.";
            } else if (left <= 0) {
                breakBtn.title = `You've used all ${breaksMax} breaks for today.`;
            } else {
                breakBtn.title = `Take a 15-minute break. ${left} of ${breaksMax} left today.`;
            }
        }
        // Keep the "My status" KPI's sub-text aligned with the button.
        setSelfDot(currentState);
    }

    function startBreakCountdown() {
        if (breakCountdownTimer !== null) return;
        breakCountdownTimer = setInterval(() => {
            if (currentState !== "break" || breakEndsAt === null) {
                stopBreakCountdown();
                return;
            }
            if (Date.now() >= breakEndsAt) {
                breakEndsAt = null;
                setState("online");
                renderBreakButton();
                stopBreakCountdown();
                return;
            }
            renderBreakButton();
        }, 15000); // update label every 15s (15-min budget is tight)
    }

    function stopBreakCountdown() {
        if (breakCountdownTimer !== null) {
            clearInterval(breakCountdownTimer);
            breakCountdownTimer = null;
        }
    }

    async function startBreak() {
        if (!breakBtn) return;
        breakBtn.disabled = true;
        try {
            const csrf = document.querySelector('input[name="__RequestVerificationToken"]');
            const headers = { "Content-Type": "application/json" };
            if (csrf) headers["RequestVerificationToken"] = csrf.value;
            const res = await fetch("/api/break/start", {
                method: "POST",
                credentials: "same-origin",
                headers,
            });
            if (!res.ok) {
                // 400 = quota exhausted; surface a friendly message and resync.
                try {
                    const err = await res.json();
                    if (err && typeof err.breaks_used === "number") {
                        breaksUsed = err.breaks_used;
                    }
                } catch { /* non-json error */ }
                renderBreakButton();
                if (res.status === 400) {
                    alert(`You've already used all ${breaksMax} short breaks for today.`);
                } else {
                    console.warn("break start failed", res.status);
                }
                return;
            }
            const data = await res.json();
            breakEndsAt = data.break_ends_at ? new Date(data.break_ends_at).getTime() : null;
            breaksUsed = typeof data.breaks_used === "number" ? data.breaks_used : breaksUsed + 1;
            currentState = "break";
            setSelfDot("break");
            pingHeartbeat();
            refreshDashboard();
            renderBreakButton();
            startBreakCountdown();
        } catch (err) {
            console.warn("break start error", err);
        } finally {
            renderBreakButton(); // re-evaluates disabled based on quota
        }
    }

    async function endBreak() {
        if (!breakBtn) return;
        breakBtn.disabled = true;
        try {
            const csrf = document.querySelector('input[name="__RequestVerificationToken"]');
            const headers = { "Content-Type": "application/json" };
            if (csrf) headers["RequestVerificationToken"] = csrf.value;
            const res = await fetch("/api/break/end", {
                method: "POST",
                credentials: "same-origin",
                headers,
            });
            if (!res.ok) {
                console.warn("break end failed", res.status);
                return;
            }
            const data = await res.json();
            if (data && typeof data.breaks_used === "number") {
                breaksUsed = data.breaks_used;
            }
            breakEndsAt = null;
            currentState = "online";
            setSelfDot("online");
            pingHeartbeat();
            refreshDashboard();
            renderBreakButton();
            stopBreakCountdown();
        } catch (err) {
            console.warn("break end error", err);
        } finally {
            renderBreakButton();
        }
    }

    if (breakBtn) {
        breakBtn.addEventListener("click", () => {
            if (currentState === "break") endBreak();
            else startBreak();
        });
        renderBreakButton();
    }

    // Initial sync — fetch the user's current state on page load. Reuses
    // applyServerState() so lunch / break / online / quota counters all
    // line up with the database, regardless of which buttons happen to
    // be on the page.
    fetch("/api/heartbeat", {
        method: "POST",
        headers: jsonHeaders(),
        credentials: "same-origin",
        body: JSON.stringify({ state: currentState }),
    })
        .then(r => r.ok ? r.json() : null)
        .then(data => {
            if (data) applyServerState(data);
        })
        .catch(() => { /* offline initial sync — ignore */ });

    // ---------------------------------------------------------------
    // Attendance modal (admin dashboard)
    // ---------------------------------------------------------------
    const modal = document.getElementById("attendance-modal");
    if (modal) {
        const titleEl = modal.querySelector("#attendance-modal-title");
        const subtitleEl = modal.querySelector("#attendance-modal-subtitle");
        const tbody = modal.querySelector("#attendance-modal-table tbody");
        const countEl = modal.querySelector("#attendance-modal-count");
        const filterForm = modal.querySelector("#attendance-filters");
        const startInput = filterForm.querySelector('input[name="start"]');
        const endInput = filterForm.querySelector('input[name="end"]');
        const exportLink = modal.querySelector("#attendance-export");
        const offsitePanel = modal.querySelector("#offsite-panel");
        const offsiteChips = offsitePanel
            ? Array.from(offsitePanel.querySelectorAll("input[data-offsite-day]"))
            : [];
        const offsiteSaveBtn = modal.querySelector("#offsite-save");
        const offsiteResetBtn = modal.querySelector("#offsite-reset");
        const offsiteStatus = modal.querySelector("#offsite-status");
        let currentUserId = null;
        let currentUserName = "";
        let offsiteOriginal = []; // last server-loaded working-day set

        function buildQuery() {
            const params = new URLSearchParams();
            if (currentUserId != null) params.set("user_id", String(currentUserId));
            if (startInput.value) params.set("start", startInput.value);
            if (endInput.value) params.set("end", endInput.value);
            return params;
        }

        function refreshExportLink() {
            if (currentUserId == null) return;
            exportLink.href = `/attendance/export.xlsx?${buildQuery().toString()}`;
        }

        function appendTextCell(row, value) {
            const cell = document.createElement("td");
            cell.textContent = value;
            row.appendChild(cell);
            return cell;
        }

        function createBadge(text, className) {
            const badge = document.createElement("span");
            badge.className = `badge ${className}`;
            badge.textContent = text;
            return badge;
        }

        function createSelfieNode(source, label, showEmpty) {
            const validSource = typeof source === "string"
                && /^data:image\/(?:jpeg|png|webp);base64,[A-Za-z0-9+/=\r\n]+$/.test(source);
            if (validSource) {
                const image = document.createElement("img");
                image.src = source;
                image.className = "selfie-thumb";
                image.alt = label;
                image.title = label;
                image.dataset.selfieZoom = "";
                return image;
            }
            if (!showEmpty) return null;

            const empty = document.createElement("span");
            empty.className = "selfie-thumb-empty";
            empty.title = `No ${label.toLowerCase()}`;
            empty.textContent = "—";
            return empty;
        }

        function renderRecords(payload) {
            const recs = payload.records || [];
            tbody.innerHTML = "";
            if (recs.length === 0) {
                tbody.innerHTML = '<tr><td colspan="7" class="muted">No records in range.</td></tr>';
            } else {
                for (const r of recs) {
                    const tr = document.createElement("tr");
                    const ci = r.check_in ? fmtHHMM(r.check_in) : "—";
                    const dur = r.check_out ? fmtDurationMinutes(r.duration_minutes) : "—";

                    appendTextCell(tr, r.work_date || "—");
                    appendTextCell(tr, ci);

                    const checkOutCell = document.createElement("td");
                    if (r.check_out) checkOutCell.textContent = fmtHHMM(r.check_out);
                    else checkOutCell.appendChild(createBadge("Open", "badge-active"));
                    tr.appendChild(checkOutCell);

                    appendTextCell(tr, dur);

                    const statusCell = document.createElement("td");
                    const isOpen = r.status === "Open";
                    statusCell.appendChild(createBadge(
                        isOpen ? "Open" : "Closed",
                        isOpen ? "badge-active" : "badge-done"));
                    tr.appendChild(statusCell);

                    const photoCell = document.createElement("td");
                    const photoWrap = document.createElement("div");
                    photoWrap.style.display = "flex";
                    photoWrap.style.gap = "6px";
                    photoWrap.style.alignItems = "center";
                    const inThumb = createSelfieNode(
                        r.check_in_photo, "Check-in selfie", true);
                    const outThumb = createSelfieNode(
                        r.check_out_photo, "Check-out selfie", !!r.check_out);
                    if (inThumb) photoWrap.appendChild(inThumb);
                    if (outThumb) photoWrap.appendChild(outThumb);
                    photoCell.appendChild(photoWrap);
                    tr.appendChild(photoCell);

                    const actionCell = document.createElement("td");
                    if (r.id != null) {
                        const editLink = document.createElement("a");
                        editLink.className = "btn btn-ghost btn-sm";
                        editLink.href = `/attendance/${encodeURIComponent(String(r.id))}/edit`;
                        editLink.title = "Edit this clock-in / clock-out";
                        editLink.textContent = "Edit";
                        actionCell.appendChild(editLink);
                    }
                    tr.appendChild(actionCell);
                    tbody.appendChild(tr);
                }
            }
            countEl.textContent = `${recs.length} record${recs.length === 1 ? "" : "s"}`;
            const u = payload.user || {};
            const ids = [u.username, u.employee_id].filter(Boolean).join(" · ");
            subtitleEl.textContent = ids;
            titleEl.textContent = u.full_name || currentUserName;
        }

        function renderLoadError(message) {
            tbody.innerHTML = "";
            const row = document.createElement("tr");
            const cell = document.createElement("td");
            cell.colSpan = 7;
            cell.className = "flash flash-danger";
            cell.append(document.createTextNode(`${message} `));
            const retry = document.createElement("button");
            retry.type = "button";
            retry.className = "btn btn-ghost btn-sm";
            retry.dataset.attendanceRetry = "";
            retry.textContent = "Retry";
            cell.appendChild(retry);
            row.appendChild(cell);
            tbody.appendChild(row);
        }

        async function loadRecords() {
            tbody.innerHTML = '<tr><td colspan="7" class="muted">Loading…</td></tr>';
            countEl.textContent = "";
            try {
                const res = await fetch(`/api/attendance/${currentUserId}?${buildQuery().toString()}`,
                    { credentials: "same-origin" });
                if (!res.ok) {
                    renderLoadError(`Failed to load (${res.status}).`);
                    return;
                }
                const data = await res.json();
                renderRecords(data);
                refreshExportLink();
            } catch (err) {
                console.warn("attendance load failed", err);
                renderLoadError("Network error.");
            }
        }

        tbody.addEventListener("click", (event) => {
            if (event.target.closest("[data-attendance-retry]")) {
                loadRecords();
            }
        });

        function openModal(userId, userName) {
            currentUserId = userId;
            currentUserName = userName || "";
            titleEl.textContent = currentUserName || "Attendance";
            subtitleEl.textContent = "";
            startInput.value = "";
            endInput.value = "";
            modal.hidden = false;
            modal.setAttribute("aria-hidden", "false");
            document.body.classList.add("modal-open");
            loadRecords();
            loadOffsiteDays();
        }

        function closeModal() {
            modal.hidden = true;
            modal.setAttribute("aria-hidden", "true");
            document.body.classList.remove("modal-open");
            currentUserId = null;
        }

        // Click the user's name to open the modal
        document.addEventListener("click", (e) => {
            const link = e.target.closest(".user-link");
            if (link) {
                e.preventDefault();
                const id = parseInt(link.getAttribute("data-user-id"), 10);
                if (!Number.isNaN(id)) {
                    openModal(id, link.getAttribute("data-user-name") || link.textContent.trim());
                }
                return;
            }
            if (e.target.matches("[data-modal-close]") || e.target === modal) {
                closeModal();
            } else if (e.target.matches("[data-modal-apply]")) {
                loadRecords();
            } else if (e.target.matches("[data-modal-clear]")) {
                startInput.value = "";
                endInput.value = "";
                loadRecords();
            }
        });

        document.addEventListener("keydown", (e) => {
            if (e.key === "Escape" && !modal.hidden) closeModal();
        });

        startInput.addEventListener("change", refreshExportLink);
        endInput.addEventListener("change", refreshExportLink);

        // ----- Offsite-day chip editor (admin only) -----
        function setOffsiteStatus(text, kind) {
            if (!offsiteStatus) return;
            offsiteStatus.textContent = text || "";
            offsiteStatus.classList.remove("is-ok", "is-error");
            if (kind === "ok") offsiteStatus.classList.add("is-ok");
            if (kind === "error") offsiteStatus.classList.add("is-error");
        }

        function applyOffsiteDays(workingDays) {
            const set = new Set(workingDays || []);
            offsiteOriginal = Array.from(set).sort((a, b) => a - b);
            for (const cb of offsiteChips) {
                const wd = parseInt(cb.getAttribute("data-offsite-day"), 10);
                cb.checked = set.has(wd);
                cb.closest(".day-chip")?.classList.toggle("is-on", cb.checked);
            }
        }

        async function loadOffsiteDays() {
            if (!offsitePanel || offsiteChips.length === 0) return;
            offsitePanel.hidden = false;
            setOffsiteStatus("Loading…");
            for (const cb of offsiteChips) {
                cb.disabled = true;
            }
            try {
                const res = await fetch(`/api/offsite/${currentUserId}`,
                    { credentials: "same-origin" });
                if (res.status === 403) {
                    // Non-admin viewing the dashboard should not happen, but
                    // hide the panel just in case.
                    offsitePanel.hidden = true;
                    return;
                }
                if (!res.ok) {
                    setOffsiteStatus(`Failed to load (${res.status})`, "error");
                    return;
                }
                const data = await res.json();
                // Only days currently flagged Offsite get a checked chip.
                // Onsite working days are intentionally NOT ticked so the
                // admin can tick them to convert to Offsite without first
                // having to untick anything.
                const offsiteDays = (data.days || [])
                    .filter((d) => d.is_offsite)
                    .map((d) => d.weekday);
                applyOffsiteDays(offsiteDays);
                setOffsiteStatus("");
            } catch (err) {
                console.warn("offsite load failed", err);
                setOffsiteStatus("Network error.", "error");
            } finally {
                for (const cb of offsiteChips) {
                    cb.disabled = false;
                }
            }
        }

        async function saveOffsiteDays() {
            if (currentUserId == null) return;
            const days = offsiteChips
                .filter((cb) => cb.checked)
                .map((cb) => parseInt(cb.getAttribute("data-offsite-day"), 10));
            const csrf = document.querySelector('input[name="__RequestVerificationToken"]');
            const headers = { "Content-Type": "application/json" };
            if (csrf) headers["RequestVerificationToken"] = csrf.value;
            offsiteSaveBtn.disabled = true;
            setOffsiteStatus("Saving…");
            try {
                const res = await fetch(`/api/offsite/${currentUserId}`, {
                    method: "POST",
                    credentials: "same-origin",
                    headers,
                    body: JSON.stringify({ days }),
                });
                if (!res.ok) {
                    setOffsiteStatus(`Save failed (${res.status})`, "error");
                    return;
                }
                const data = await res.json();
                applyOffsiteDays(data.days || days);
                const weeks = Number.isFinite(Number(data.horizon_weeks))
                    ? Number(data.horizon_weeks)
                    : 12;
                setOffsiteStatus(`Saved (applied for ${weeks} weeks).`, "ok");
            } catch (err) {
                console.warn("offsite save failed", err);
                setOffsiteStatus("Network error.", "error");
            } finally {
                offsiteSaveBtn.disabled = false;
            }
        }

        if (offsitePanel) {
            for (const cb of offsiteChips) {
                cb.addEventListener("change", () => {
                    cb.closest(".day-chip")?.classList.toggle("is-on", cb.checked);
                    setOffsiteStatus("Unsaved changes");
                });
            }
            offsiteSaveBtn?.addEventListener("click", saveOffsiteDays);
            offsiteResetBtn?.addEventListener("click", () => {
                applyOffsiteDays(offsiteOriginal);
                setOffsiteStatus("");
            });
        }
    }
})();

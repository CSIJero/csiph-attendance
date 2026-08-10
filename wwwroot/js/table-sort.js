// ---------------------------------------------------------------------------
// Sortable data tables.
//
// Any `<table class="data-table sortable">` automatically becomes sortable —
// click a header to sort by that column, click again to toggle direction.
// Numeric columns are detected automatically (cells whose visible text parses
// as a number). Cells can override the sort key with `data-sort="..."`.
//
// Headers can opt out with `data-no-sort` or pin the type with
// `data-sort-type="num"` / `data-sort-type="text"` / `data-sort-type="date"`.
//
// Pure client-side, no dependencies; runs once on DOMContentLoaded.
// ---------------------------------------------------------------------------
(function () {
    'use strict';

    function getCellValue(row, index) {
        var cell = row.children[index];
        if (!cell) return '';
        if (cell.dataset && cell.dataset.sort != null) return cell.dataset.sort;
        return (cell.textContent || '').trim();
    }

    // Cheap date-shape detector. We only need this for sortability:
    // anything that *looks* like a date should sort chronologically, not
    // get classified as numeric and collapse on the leading year.
    //  - 2026-05-21 / 2026-05-21 08:30 (ISO-ish)
    //  - 05/21/2026 / 21-05-2026 (slash or dash separated)
    //  - May 21, 2026 / 21 May 2026 (month name)
    // We accept any string Date.parse can read AS LONG AS it also matches
    // one of these shapes so plain integers like '2026' don't sneak in.
    var DATE_SHAPE = /^(?:\d{4}[-/]\d{1,2}[-/]\d{1,2}|\d{1,2}[-/]\d{1,2}[-/]\d{2,4}|\d{1,2}\s+[A-Za-z]{3,}\s+\d{2,4}|[A-Za-z]{3,}\s+\d{1,2},?\s+\d{2,4})\b/;
    function looksLikeDate(v) {
        if (!v || !DATE_SHAPE.test(v)) return false;
        return !isNaN(Date.parse(v));
    }

    function detectType(rows, columnIndex, forcedType) {
        if (forcedType === 'num' || forcedType === 'date' || forcedType === 'text') {
            return forcedType;
        }
        // First pass: if every non-empty cell looks like a date, treat as
        // date. This has to win over the numeric pass because a string
        // like '2026-05-21' also parseFloats to a finite number (2026).
        var allDate = true;
        var seenAnyDate = false;
        for (var d = 0; d < rows.length && allDate; d++) {
            var dv = getCellValue(rows[d], columnIndex);
            if (!dv) continue;
            seenAnyDate = true;
            if (!looksLikeDate(dv)) allDate = false;
        }
        if (seenAnyDate && allDate) return 'date';

        // Second pass: numeric.
        var allNumeric = true;
        var seenAny = false;
        for (var i = 0; i < rows.length && allNumeric; i++) {
            var v = getCellValue(rows[i], columnIndex);
            if (!v) continue;
            seenAny = true;
            var n = parseFloat(v.replace(/[, ]/g, ''));
            if (!isFinite(n)) allNumeric = false;
        }
        return (seenAny && allNumeric) ? 'num' : 'text';
    }

    function comparator(columnIndex, type, direction) {
        var mult = direction === 'desc' ? -1 : 1;
        return function (a, b) {
            var av = getCellValue(a, columnIndex);
            var bv = getCellValue(b, columnIndex);
            if (type === 'num') {
                var na = parseFloat(av.replace(/[, ]/g, ''));
                var nb = parseFloat(bv.replace(/[, ]/g, ''));
                if (!isFinite(na)) na = direction === 'desc' ? -Infinity : Infinity;
                if (!isFinite(nb)) nb = direction === 'desc' ? -Infinity : Infinity;
                if (na < nb) return -1 * mult;
                if (na > nb) return 1 * mult;
                return 0;
            }
            if (type === 'date') {
                var da = Date.parse(av);
                var db = Date.parse(bv);
                if (isNaN(da)) da = direction === 'desc' ? -Infinity : Infinity;
                if (isNaN(db)) db = direction === 'desc' ? -Infinity : Infinity;
                if (da < db) return -1 * mult;
                if (da > db) return 1 * mult;
                return 0;
            }
            return av.localeCompare(bv, undefined, { numeric: true, sensitivity: 'base' }) * mult;
        };
    }

    function applySort(table, columnIndex, direction, forcedType) {
        var tbodies = table.tBodies;
        if (!tbodies || !tbodies.length) return;
        // Sort each <tbody> independently so date-grouped tables keep their
        // groups intact while rows within a group still re-order. Rows
        // tagged with `group-header` (or `data-group-header`) are pinned
        // to the top of their tbody.
        for (var t = 0; t < tbodies.length; t++) {
            var tbody = tbodies[t];
            var allRows = Array.prototype.slice.call(tbody.rows);
            // Single-row "no data" placeholders typically use colspan; leave them alone.
            if (allRows.length === 1 && allRows[0].cells.length === 1
                && allRows[0].cells[0].hasAttribute('colspan')) {
                continue;
            }
            var headers = [];
            var dataRows = [];
            for (var r = 0; r < allRows.length; r++) {
                var row = allRows[r];
                if (row.classList.contains('group-header') || row.hasAttribute('data-group-header')) {
                    headers.push(row);
                } else {
                    dataRows.push(row);
                }
            }
            if (!dataRows.length) continue;
            var type = detectType(dataRows, columnIndex, forcedType);
            dataRows.sort(comparator(columnIndex, type, direction));
            var frag = document.createDocumentFragment();
            for (var h = 0; h < headers.length; h++) frag.appendChild(headers[h]);
            for (var d = 0; d < dataRows.length; d++) frag.appendChild(dataRows[d]);
            tbody.appendChild(frag);
        }

        // Date-grouped tables use one tbody per calendar date. Sorting only
        // the rows inside each tbody makes the Date header appear broken, so
        // reorder the groups themselves when every group provides an ISO key.
        if (forcedType === 'date' && tbodies.length > 1) {
            var groups = Array.prototype.slice.call(tbodies);
            var hasGroupKeys = groups.every(function (body) {
                return body.dataset && body.dataset.groupSort;
            });
            if (hasGroupKeys) {
                var mult = direction === 'desc' ? -1 : 1;
                groups.sort(function (a, b) {
                    var av = Date.parse(a.dataset.groupSort);
                    var bv = Date.parse(b.dataset.groupSort);
                    return (av - bv) * mult;
                });
                groups.forEach(function (body) { table.appendChild(body); });
            }
        }
    }

    function wireTable(table) {
        var headers = table.tHead && table.tHead.rows.length
            ? table.tHead.rows[table.tHead.rows.length - 1].cells
            : null;
        if (!headers) return;
        Array.prototype.forEach.call(headers, function (th, idx) {
            if (th.hasAttribute('data-no-sort')) return;
            // Skip header cells that are pure action / icon columns — they
            // have no meaningful label OR opt out explicitly.
            var label = (th.textContent || '').trim();
            if (!label && !th.hasAttribute('data-sort-type')) return;

            th.classList.add('sortable');
            th.setAttribute('role', 'button');
            th.setAttribute('tabindex', '0');
            th.setAttribute('aria-sort', 'none');
            // Inject the indicator triangle the existing CSS expects.
            if (!th.querySelector('.sort-indicator')) {
                var ind = document.createElement('span');
                ind.className = 'sort-indicator';
                ind.setAttribute('aria-hidden', 'true');
                th.appendChild(ind);
            }

            function activate() {
                var nextDir;
                if (th.classList.contains('sort-asc')) {
                    nextDir = 'desc';
                } else {
                    nextDir = 'asc';
                }
                // Clear sort state on sibling headers so only one is active.
                Array.prototype.forEach.call(headers, function (h) {
                    if (h !== th) {
                        h.classList.remove('sort-asc', 'sort-desc');
                        h.setAttribute('aria-sort', 'none');
                    }
                });
                th.classList.remove('sort-asc', 'sort-desc');
                th.classList.add(nextDir === 'asc' ? 'sort-asc' : 'sort-desc');
                th.setAttribute('aria-sort', nextDir === 'asc' ? 'ascending' : 'descending');
                applySort(table, idx, nextDir, th.getAttribute('data-sort-type'));
            }

            th.addEventListener('click', activate);
            th.addEventListener('keydown', function (ev) {
                if (ev.key === 'Enter' || ev.key === ' ') {
                    ev.preventDefault();
                    activate();
                }
            });
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        var tables = document.querySelectorAll('table.data-table.sortable');
        Array.prototype.forEach.call(tables, wireTable);
    });
})();

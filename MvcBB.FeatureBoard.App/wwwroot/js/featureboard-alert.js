// New feature request alert for Administrators. Works like core's open-reports
// alert: the page renders the Requested column's count into the navbar badge
// and the toast's data-initial-count, then this polls a small JSON endpoint,
// updating the badge and toasting when the count goes up (a new request came in).
(function () {
    var toastEl = document.getElementById('newFeatureRequestToast');
    if (!toastEl) {
        return; // Not an Administrator, or the mod is disabled.
    }

    var POLL_INTERVAL_MS = 30000;
    var countUrl = toastEl.getAttribute('data-count-url');
    var initial = toastEl.getAttribute('data-initial-count');
    // No baseline when the page couldn't load the count: the first poll sets it.
    var lastKnownCount = initial === null || initial === '' ? null : parseInt(initial, 10);

    function updateBadge(count) {
        var badge = document.getElementById('featureRequestBadge');
        if (!badge) {
            return;
        }
        badge.textContent = count;
        badge.classList.toggle('d-none', count === 0);
    }

    function showToast(count) {
        var textEl = document.getElementById('newFeatureRequestToastText');
        if (!window.bootstrap) {
            return;
        }
        if (textEl) {
            textEl.textContent = count === 1
                ? '1 feature request to review.'
                : count + ' feature requests to review.';
        }
        window.bootstrap.Toast.getOrCreateInstance(toastEl).show();
    }

    function poll() {
        fetch(countUrl, { headers: { 'Accept': 'application/json' } })
            .then(function (response) { return response.ok ? response.json() : null; })
            .then(function (data) {
                if (!data || typeof data.count !== 'number') {
                    return;
                }
                updateBadge(data.count);
                if (lastKnownCount !== null && data.count > lastKnownCount) {
                    showToast(data.count);
                }
                lastKnownCount = data.count;
            })
            .catch(function () {
                // Transient network/API hiccup - just skip this tick, next poll retries.
            });
    }

    if (countUrl) {
        setInterval(poll, POLL_INTERVAL_MS);
    }
})();

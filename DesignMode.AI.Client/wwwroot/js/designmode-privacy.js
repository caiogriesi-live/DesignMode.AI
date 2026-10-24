(function (root) {
    "use strict";
    if (root.self !== root.top) return;
    let mounted = false;
    const wrapper = () => root.designModeAnalytics;
    function mount() {
        if (mounted || document.getElementById("dm-privacy-access")) return;
        mounted = true;
        const access = document.createElement("button");
        access.id = "dm-privacy-access"; access.type = "button";
        access.textContent = "Privacy / measurement";
        access.style.cssText = "position:fixed;bottom:12px;left:12px;z-index:10000;padding:8px 12px;background:white;color:#222;border:1px solid #777;border-radius:6px;font:14px Arial";
        const panel = document.createElement("dialog");
        panel.setAttribute("aria-labelledby", "dm-privacy-title");
        panel.style.cssText = "max-width:480px;width:calc(100% - 40px);padding:24px;border:1px solid #777;border-radius:8px;color:#222;background:white;font:16px/1.5 Arial";
        panel.innerHTML = '<h2 id="dm-privacy-title">Measurement preferences</h2>' +
            '<p>DesignMode internal advertising measurement and Google advertising measurement are enabled for new activity under the current product policy. Google ad personalization remains disabled.</p>' +
            '<p>Analytics remains an independent browser preference.</p>' +
            '<p><button type="button" data-analytics-on>Enable analytics</button> <button type="button" data-analytics-off>Disable analytics</button></p>' +
            '<p data-status role="status" aria-live="polite"></p><p><a href="/privacy.html">Privacy policy</a></p><button type="button" data-close>Close</button>';
        const status = panel.querySelector("[data-status]");
        function setAnalytics(value) {
            wrapper().setConsent({ ...wrapper().getConsent(), analytics: value });
            status.textContent = value ? "Analytics is enabled." : "Analytics is disabled.";
        }
        panel.querySelector("[data-analytics-on]").onclick = () => setAnalytics(true);
        panel.querySelector("[data-analytics-off]").onclick = () => setAnalytics(false);
        panel.querySelector("[data-close]").onclick = () => panel.close();
        access.onclick = () => { panel.showModal(); status.textContent = wrapper().getConsent().analytics ? "Analytics is enabled." : "Analytics is disabled."; };
        document.body.appendChild(access); document.body.appendChild(panel);
    }
    function initialize() { mount(); return Promise.resolve(); }
    function refresh() { return Promise.resolve(); }
    root.designModePrivacy = { initialize, refresh };
})(window);

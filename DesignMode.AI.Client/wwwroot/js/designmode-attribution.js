(function () {
    console.info("[ATTRIBUTION] script loaded");
    const visitorKey = "dm_visitor_id", sessionKey = "dm_session_context", funnelKey = "dm_social_funnel";
    const sessionIdKey = "dm_session_id", sessionActivityKey = "dm_session_last_activity", funnelIdKey = "dm_funnel_id", projectIdKey = "dm_project_id";
    const timeout = 30 * 60 * 1000;
    function uuid() {
        if (window.crypto && typeof window.crypto.randomUUID === "function") return window.crypto.randomUUID();
        const bytes = new Uint8Array(16);
        window.crypto.getRandomValues(bytes);
        bytes[6] = (bytes[6] & 0x0f) | 0x40;
        bytes[8] = (bytes[8] & 0x3f) | 0x80;
        const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
        return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
    }
    function ensureIds() {
        let visitorId = localStorage.getItem(visitorKey);
        if (!visitorId) { visitorId = uuid(); localStorage.setItem(visitorKey, visitorId); }
        console.info("[ATTRIBUTION] visitor id ready");
        const now = Date.now(); let session; let funnel;
        try { session = JSON.parse(localStorage.getItem(sessionKey)); } catch { session = null; }
        const storedSessionId = localStorage.getItem(sessionIdKey);
        const storedActivity = Number(localStorage.getItem(sessionActivityKey));
        if (!session?.id && storedSessionId) session = { id: storedSessionId, lastActivity: storedActivity || now };
        if (!session?.id || now - session.lastActivity > timeout) session = { id: uuid(), lastActivity: now };
        session.lastActivity = now;
        localStorage.setItem(sessionKey, JSON.stringify(session));
        localStorage.setItem(sessionIdKey, session.id);
        localStorage.setItem(sessionActivityKey, String(now));
        console.info("[ATTRIBUTION] session id ready");
        try { funnel = JSON.parse(localStorage.getItem(funnelKey)); } catch { funnel = null; }
        let funnelId = funnel?.status !== "complete" ? funnel?.id : null;
        if (!funnelId) funnelId = localStorage.getItem(funnelIdKey);
        if (!funnelId || funnel?.status === "complete") funnelId = uuid();
        localStorage.setItem(funnelIdKey, funnelId);
        localStorage.setItem(funnelKey, JSON.stringify({ ...(funnel || {}), id: funnelId, status: "active" }));
        console.info("[ATTRIBUTION] funnel id ready");
        return { visitorId, sessionId: session.id, funnelId, projectId: funnel?.projectId || null };
    }
    const first = (q, names) => names.map(x => q.get(x)).find(Boolean) || null;
    async function capture(base, landingVariant) {
        console.info("[ATTRIBUTION] initialization started");
        const ids = ensureIds(), q = new URLSearchParams(location.search);
        const url = base.replace(/\/?$/, "/") + "api/CaptureSocialAcquisition";
        console.info("[ATTRIBUTION] capture request starting");
        console.info(`[ATTRIBUTION] capture URL: ${url}`);
        const response = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({
            visitorId: ids.visitorId, sessionId: ids.sessionId, funnelId: ids.funnelId,
            gclid: q.get("gclid"), gbraid: q.get("gbraid"), wbraid: q.get("wbraid"), utmSource: q.get("utm_source"), utmMedium: q.get("utm_medium"), utmCampaign: q.get("utm_campaign"), utmTerm: q.get("utm_term"), utmContent: q.get("utm_content"),
            campaignId: first(q, ["campaignid", "campaign_id"]), adGroupId: first(q, ["adgroupid", "ad_group_id"]), adId: first(q, ["creative", "adid", "ad_id"]), keyword: q.get("keyword"), matchType: q.get("matchtype"), device: q.get("device"), network: q.get("network"),
            landingPage: location.pathname + location.search, landingVariant: landingVariant || q.get("landing_variant") || "social-default", referrer: document.referrer || null
        }) });
        console.info(`[ATTRIBUTION] capture response status: ${response.status}`);
        if (!response.ok) throw new Error("Acquisition capture failed.");
        const result = await response.json();
        localStorage.setItem(funnelKey, JSON.stringify({ id: result.funnelId, acquisitionId: result.acquisitionId, status: "active" }));
        localStorage.setItem(funnelIdKey, result.funnelId);
        console.info("[ATTRIBUTION] capture completed");
        return result;
    }
    async function createSocialProject(base) {
        const ids = ensureIds();
        const url = base.replace(/\/?$/, "/") + "api/CreateSocialProject";
        console.info("[ATTRIBUTION] project creation starting");
        const response = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(ids) });
        console.info(`[ATTRIBUTION] project creation response status: ${response.status}`);
        if (!response.ok) {
            const detail = await response.text();
            console.error(`[ATTRIBUTION] project creation failed: HTTP ${response.status}; detail=${detail}`);
            throw new Error("Project creation failed.");
        }
        const result = await response.json();
        localStorage.setItem(funnelKey, JSON.stringify({ id: result.funnelId, projectId: result.projectId, acquisitionId: result.acquisitionId, status: "active" }));
        localStorage.setItem(funnelIdKey, result.funnelId);
        localStorage.setItem(projectIdKey, result.projectId);
        return result;
    }
    function markFunnelComplete() {
        let funnel;
        try { funnel = JSON.parse(localStorage.getItem(funnelKey)); } catch { funnel = null; }
        if (funnel) { funnel.status = "complete"; localStorage.setItem(funnelKey, JSON.stringify(funnel)); }
        localStorage.removeItem(funnelIdKey);
    }
    window.designModeAttribution = { ensureIds, capture, createSocialProject, markFunnelComplete };
})();

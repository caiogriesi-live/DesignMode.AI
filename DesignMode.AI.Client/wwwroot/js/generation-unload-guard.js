(() => {
    let active = false;
    let wakeLock = null;
    let requestingWakeLock = false;
    let wakeLockVersion = 0;
    const supportsWakeLock = window.isSecureContext !== false &&
        typeof navigator !== 'undefined' && typeof navigator.wakeLock?.request === 'function';

    const releaseWakeLock = async () => {
        const held = wakeLock;
        wakeLock = null;
        try { if (held) await held.release(); }
        catch { /* Wake lock availability must never interrupt generation or closing. */ }
    };

    const acquireWakeLock = async () => {
        if (!supportsWakeLock || !active || document.visibilityState !== 'visible' ||
            wakeLock || requestingWakeLock) return;

        requestingWakeLock = true;
        const version = wakeLockVersion;
        try {
            const acquired = await navigator.wakeLock.request('screen');
            if (!active || version !== wakeLockVersion || document.visibilityState !== 'visible') {
                await acquired.release();
                return;
            }
            if (acquired.released) return;
            wakeLock = acquired;
            acquired.addEventListener('release', () => {
                if (wakeLock === acquired) wakeLock = null;
            }, { once: true });
        } catch {
            // Unsupported policy, battery restrictions, or a denied request are non-fatal.
            // Retry only on a later activation/visibility change, never in a busy loop.
        } finally {
            requestingWakeLock = false;
            if (active && version !== wakeLockVersion) void acquireWakeLock();
        }
    };

    const visibilityChanged = () => {
        if (document.visibilityState === 'visible') {
            void acquireWakeLock();
        } else {
            wakeLockVersion++;
            void releaseWakeLock();
        }
    };
    const beforeUnload = event => {
        event.preventDefault();
        event.returnValue = '';
    };
    window.designModeGenerationGuard = {
        setActive(value) {
            if (active === value) return;
            active = value;
            wakeLockVersion++;
            if (active) {
                window.addEventListener('beforeunload', beforeUnload);
                if (supportsWakeLock) document.addEventListener('visibilitychange', visibilityChanged);
                void acquireWakeLock();
            } else {
                window.removeEventListener('beforeunload', beforeUnload);
                if (supportsWakeLock) document.removeEventListener('visibilitychange', visibilityChanged);
                void releaseWakeLock();
            }
        }
    };
})();

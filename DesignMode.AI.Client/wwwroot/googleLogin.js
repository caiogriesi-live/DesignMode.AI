window.googleLogin = {
    getIdToken: async function (clientId) {
        return new Promise((resolve, reject) => {
            // Load Google Identity Services script if not loaded
            if (!window.google || !window.google.accounts || !window.google.accounts.id) {
                const script = document.createElement("script");
                script.src = "https://accounts.google.com/gsi/client";
                script.async = true;
                script.defer = true;
                script.onload = () => initGoogle(clientId, resolve, reject);
                script.onerror = () => reject("Failed to load Google Identity script");
                document.head.appendChild(script);
            } else {
                initGoogle(clientId, resolve, reject);
            }

            function initGoogle(clientId, resolve, reject) {
                try {
                    google.accounts.id.initialize({
                        client_id: clientId,
                        callback: (response) => {
                            if (response.credential) {
                                resolve(response.credential); // ID token
                            } else {
                                reject("No credential returned from Google.");
                            }
                        },
                        ux_mode: 'popup',      // Use popup to avoid FedCM issues on localhost
                        auto_select: false      // Disable One Tap auto-login
                    });

                    // Trigger popup login
                    google.accounts.id.prompt(notification => {
                        if (notification.isNotDisplayed() || notification.isSkippedMoment()) {
                            console.warn("Google One Tap skipped or not displayed. Using popup.");
                        }
                    });
                } catch (err) {
                    reject("Error initializing Google Identity: " + err);
                }
            }
        });
    }
};

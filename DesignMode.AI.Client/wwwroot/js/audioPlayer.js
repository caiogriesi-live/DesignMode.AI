// audioPlayer.js

// Audio playback functions
window.playAudio = async (audioUrl) => {
    try {
        const audio = new Audio(audioUrl);
        audio.play();
        return new Promise((resolve) => {
            audio.onended = () => resolve(true);
            audio.onerror = (error) => {
                console.error('Audio playback error:', error);
                resolve(false);
            };
        });
    } catch (error) {
        console.error('Error playing audio:', error);
        return false;
    }
};

window.pauseAudio = () => {
    try {
        const audioElements = document.getElementsByTagName('audio');
        for (let audio of audioElements) {
            audio.pause();
        }
    } catch (error) {
        console.error('Error pausing audio:', error);
    }
};

window.stopAllAudio = () => {
    try {
        const audioElements = document.getElementsByTagName('audio');
        for (let audio of audioElements) {
            audio.pause();
            audio.currentTime = 0;
        }
    } catch (error) {
        console.error('Error stopping audio:', error);
    }
};

// Scroll to specific scene
window.scrollToScene = (sceneNumber) => {
    try {
        // Try different selectors to find the scene element
        const selectors = [
            `[data-scene="${sceneNumber}"]`,
            `.mvsi-scene:nth-child(${sceneNumber})`,
            `.mvsi-customization-card[data-scene="${sceneNumber}"]`
        ];

        let sceneElement = null;
        for (const selector of selectors) {
            sceneElement = document.querySelector(selector);
            if (sceneElement) break;
        }

        if (sceneElement) {
            // Add highlight class for visual feedback
            sceneElement.classList.add('scene-highlight');

            // Scroll to the element
            sceneElement.scrollIntoView({
                behavior: 'smooth',
                block: 'center'
            });

            // Remove highlight after 2 seconds
            setTimeout(() => {
                sceneElement.classList.remove('scene-highlight');
            }, 2000);

            return true;
        } else {
            console.warn(`Scene element ${sceneNumber} not found`);
            // Fallback: scroll to top of customization section
            const customizationSection = document.querySelector('.mvsi-customization-section');
            if (customizationSection) {
                customizationSection.scrollIntoView({ behavior: 'smooth' });
            }
            return false;
        }
    } catch (error) {
        console.error('Error scrolling to scene:', error);
        return false;
    }
};

// Optional: Highlight the current playing scene with CSS
window.highlightScene = (sceneNumber) => {
    try {
        // Remove highlight from all scenes
        document.querySelectorAll('.scene-highlight').forEach(el => {
            el.classList.remove('scene-highlight');
        });

        // Add highlight to current scene
        const sceneElement = document.querySelector(`[data-scene="${sceneNumber}"]`);
        if (sceneElement) {
            sceneElement.classList.add('scene-highlight');
        }
    } catch (error) {
        console.error('Error highlighting scene:', error);
    }
};

// Audio duration estimation (helper function)
window.estimateAudioDuration = (text) => {
    // Average speaking speed: 150 words per minute
    const words = text.split(' ').length;
    const minutes = words / 150;
    const milliseconds = minutes * 60000;
    return Math.max(1000, Math.min(milliseconds, 30000)); // Between 1-30 seconds
};

// Initialize audio system
window.initializeAudioSystem = () => {
    console.log('Audio system initialized');
    return true;
};

// Cleanup function
window.cleanupAudioSystem = () => {
    window.stopAllAudio();
    console.log('Audio system cleaned up');
    return true;
};
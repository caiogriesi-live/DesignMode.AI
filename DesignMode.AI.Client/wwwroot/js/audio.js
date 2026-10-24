window.playAudioFromUrl = (audioUrl, dotNetHelper) => {
    try {
        const audio = new Audio(audioUrl);

        audio.onended = () => {
            if (dotNetHelper) {
                dotNetHelper.invokeMethodAsync("OnAudioEnded");
            }
        };

        audio.play();
    } catch (e) {
        console.error("Audio playback failed:", e);
    }
};


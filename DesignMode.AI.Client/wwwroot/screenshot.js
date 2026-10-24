window.captureScreenshot = async () => {
    const canvas = await html2canvas(document.body, {
        useCORS: true,
        scale: 1
    });

    return canvas.toDataURL("image/png");
};
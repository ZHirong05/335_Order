window.kitchenSound = {
    context: null,
    enable() {
        this.context = new (window.AudioContext || window.webkitAudioContext)();
        return this.context.resume().then(() => true).catch(() => false);
    },
    reconcile(ids) {
        const previous = JSON.parse(sessionStorage.getItem('335-seen-tickets') || 'null');
        sessionStorage.setItem('335-seen-tickets', JSON.stringify(ids));
        if (!this.context || !previous || !ids.some(id => !previous.includes(id))) return;
        const tone = this.context.createOscillator();
        const gain = this.context.createGain();
        gain.gain.value = 0.12;
        tone.connect(gain); gain.connect(this.context.destination);
        tone.frequency.value = 880;
        tone.start(); tone.stop(this.context.currentTime + 0.3);
    }
};

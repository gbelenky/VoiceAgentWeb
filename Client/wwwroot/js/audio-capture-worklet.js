// AudioWorklet processor for microphone capture at 24kHz mono
class AudioCaptureProcessor extends AudioWorkletProcessor {
    constructor() {
        super();
        this._buffer = [];
        this._bufferSize = 2400; // 100ms at 24kHz
    }

    process(inputs, outputs, parameters) {
        const input = inputs[0];
        if (input.length > 0) {
            const channelData = input[0];
            this._buffer.push(...channelData);

            while (this._buffer.length >= this._bufferSize) {
                const chunk = new Float32Array(this._buffer.splice(0, this._bufferSize));
                this.port.postMessage({ type: 'audio-data', audio: chunk });
            }
        }
        return true;
    }
}

registerProcessor('audio-capture-processor', AudioCaptureProcessor);

// AudioWorklet processor for PCM16 playback at 24kHz mono
class AudioPlaybackProcessor extends AudioWorkletProcessor {
    constructor() {
        super();
        this._queue = [];
        this._currentBuffer = null;
        this._currentOffset = 0;

        this.port.onmessage = (event) => {
            if (event.data.type === 'play-audio') {
                this._queue.push(event.data.audio);
            } else if (event.data.type === 'stop') {
                this._queue = [];
                this._currentBuffer = null;
                this._currentOffset = 0;
            }
        };
    }

    process(inputs, outputs, parameters) {
        const output = outputs[0];
        if (output.length === 0) return true;

        const channel = output[0];
        let written = 0;

        while (written < channel.length) {
            if (!this._currentBuffer || this._currentOffset >= this._currentBuffer.length) {
                if (this._queue.length === 0) {
                    // Fill remaining with silence
                    channel.fill(0, written);
                    break;
                }
                this._currentBuffer = this._queue.shift();
                this._currentOffset = 0;
            }

            const remaining = channel.length - written;
            const available = this._currentBuffer.length - this._currentOffset;
            const toCopy = Math.min(remaining, available);

            channel.set(
                this._currentBuffer.subarray(this._currentOffset, this._currentOffset + toCopy),
                written
            );

            written += toCopy;
            this._currentOffset += toCopy;
        }

        return true;
    }
}

registerProcessor('audio-playback-processor', AudioPlaybackProcessor);

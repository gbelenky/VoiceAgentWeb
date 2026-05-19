// Audio capture and playback interop for Blazor WASM
// Handles microphone capture (24kHz PCM16) and audio playback via AudioWorklet

let audioContext = null;
let captureStream = null;
let captureWorklet = null;
let playbackWorklet = null;
let dotNetRef = null;

/**
 * Initialize audio capture from microphone at 24kHz mono PCM16.
 * @param {object} dotNetReference - .NET object reference for callbacks
 */
export async function startAudioCapture(dotNetReference) {
    dotNetRef = dotNetReference;

    try {
        audioContext = new AudioContext({ sampleRate: 24000 });

        // Load the capture worklet
        await audioContext.audioWorklet.addModule('js/audio-capture-worklet.js');

        captureStream = await navigator.mediaDevices.getUserMedia({
            audio: {
                sampleRate: 24000,
                channelCount: 1,
                echoCancellation: true,
                noiseSuppression: true,
                autoGainControl: true
            }
        });

        const source = audioContext.createMediaStreamSource(captureStream);
        captureWorklet = new AudioWorkletNode(audioContext, 'audio-capture-processor');

        captureWorklet.port.onmessage = (event) => {
            if (event.data.type === 'audio-data') {
                // Convert Float32 to PCM16 and send as base64
                const float32Data = event.data.audio;
                const pcm16 = float32ToPcm16(float32Data);
                const base64 = arrayBufferToBase64(pcm16.buffer);
                dotNetRef.invokeMethodAsync('OnAudioCaptured', base64);
            }
        };

        source.connect(captureWorklet);
        captureWorklet.connect(audioContext.destination); // Required for worklet to process

        // Load playback worklet
        await audioContext.audioWorklet.addModule('js/audio-playback-worklet.js');
        playbackWorklet = new AudioWorkletNode(audioContext, 'audio-playback-processor');
        playbackWorklet.connect(audioContext.destination);

        return true;
    } catch (error) {
        console.error('Failed to start audio capture:', error);
        return false;
    }
}

/**
 * Stop audio capture and release resources.
 */
export function stopAudioCapture() {
    if (captureWorklet) {
        captureWorklet.disconnect();
        captureWorklet = null;
    }
    if (playbackWorklet) {
        playbackWorklet.disconnect();
        playbackWorklet = null;
    }
    if (captureStream) {
        captureStream.getTracks().forEach(track => track.stop());
        captureStream = null;
    }
    if (audioContext) {
        audioContext.close();
        audioContext = null;
    }
    dotNetRef = null;
}

/**
 * Enqueue base64 PCM16 audio for playback.
 * @param {string} base64Audio - Base64-encoded PCM16 audio data
 */
export function playAudio(base64Audio) {
    if (!playbackWorklet) return;

    const pcm16 = base64ToArrayBuffer(base64Audio);
    const float32 = pcm16ToFloat32(new Int16Array(pcm16));

    playbackWorklet.port.postMessage({
        type: 'play-audio',
        audio: float32
    });
}

/**
 * Stop any currently playing audio (barge-in).
 */
export function stopPlayback() {
    if (playbackWorklet) {
        playbackWorklet.port.postMessage({ type: 'stop' });
    }
}

// --- Utility functions ---

function float32ToPcm16(float32Array) {
    const pcm16 = new Int16Array(float32Array.length);
    for (let i = 0; i < float32Array.length; i++) {
        const s = Math.max(-1, Math.min(1, float32Array[i]));
        pcm16[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
    }
    return pcm16;
}

function pcm16ToFloat32(pcm16Array) {
    const float32 = new Float32Array(pcm16Array.length);
    for (let i = 0; i < pcm16Array.length; i++) {
        float32[i] = pcm16Array[i] / (pcm16Array[i] < 0 ? 0x8000 : 0x7FFF);
    }
    return float32;
}

function arrayBufferToBase64(buffer) {
    const bytes = new Uint8Array(buffer);
    let binary = '';
    for (let i = 0; i < bytes.byteLength; i++) {
        binary += String.fromCharCode(bytes[i]);
    }
    return btoa(binary);
}

function base64ToArrayBuffer(base64) {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes.buffer;
}

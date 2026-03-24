const peers = {};

export function initialize(dotNetRef) {
    window.dotNetRef = dotNetRef;
}

export async function createOffer(peerId) {
    const pc = getOrCreatePeer(peerId);
    const dc = pc.createDataChannel("fileTransfer", { ordered: true });
    setupDataChannel(peerId, dc);

    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);
    return JSON.stringify(offer);
}

export async function handleSignal(peerId, signalStr) {
    const pc = getOrCreatePeer(peerId);
    const signal = JSON.parse(signalStr);

    if (signal.type === "offer") {
        await pc.setRemoteDescription(new RTCSessionDescription(signal));
        const answer = await pc.createAnswer();
        await pc.setLocalDescription(answer);
        await window.dotNetRef.invokeMethodAsync("SendSignalInternal", peerId, JSON.stringify(answer));
    } else if (signal.type === "answer") {
        await pc.setRemoteDescription(new RTCSessionDescription(signal));
    } else if (signal.candidate) {
        await pc.addIceCandidate(new RTCIceCandidate(signal));
    }
}

function getOrCreatePeer(peerId) {
    if (peers[peerId]) return peers[peerId].pc;

    const pc = new RTCPeerConnection({
        iceServers: [{ urls: "stun:stun.l.google.com:19302" }]
    });

    pc.onicecandidate = event => {
        if (event.candidate) {
            window.dotNetRef.invokeMethodAsync("SendSignalInternal", peerId, JSON.stringify(event.candidate));
        }
    };

    pc.ondatachannel = event => {
        setupDataChannel(peerId, event.channel);
    };

    peers[peerId] = { pc };
    return pc;
}

function setupDataChannel(peerId, dc) {
    console.log(`Setting up data channel for peer ${peerId}`);
    peers[peerId].dc = dc;
    dc.onopen = () => {
        console.log(`Data channel open for ${peerId}`);
        window.dotNetRef.invokeMethodAsync("OnConnectionStateChangedInternal", peerId, "Connected");
    };
    dc.onclose = () => {
        console.log(`Data channel closed for ${peerId}`);
        window.dotNetRef.invokeMethodAsync("OnConnectionStateChangedInternal", peerId, "Disconnected");
    };
    dc.onmessage = event => {
        console.log(`Message received from ${peerId}:`, event.data);
        if (typeof event.data === 'string') {
             window.dotNetRef.invokeMethodAsync("OnMessageReceivedInternal", peerId, event.data);
        } else {
            window.dotNetRef.invokeMethodAsync("OnBinaryReceivedInternal", peerId, new Uint8Array(event.data));
        }
    };
}

export function sendData(peerId, data) {
    const peer = peers[peerId];
    if (peer && peer.dc && peer.dc.readyState === "open") {
        console.log(`Sending data to ${peerId}:`, data);
        peer.dc.send(data);
        return true;
    }
    console.warn(`Cannot send data to ${peerId}. ReadyState: ${peer?.dc?.readyState}`);
    return false;
}

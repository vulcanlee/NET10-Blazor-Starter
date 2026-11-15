// http（非 localhost）也能啟動（0.10.31 起）。
// .NET 11 的 dotnet.js 啟動時無條件檢查 crypto.subtle，瀏覽器只在安全連線（https、localhost）提供它，
// 用 http 加 IP 或網域開會出現「This engine doesn't support crypto.subtle」，畫面停在載入卡片。
// 執行環境實際只用到 crypto.subtle.digest('SHA-256')（dotnet.runtime.*.js 算快取鍵），所以只補這一個；
// 其他演算法丟出錯誤，不假裝支援。我們自己的 C#（SHA256、亂數）不靠 crypto.subtle，crypto.getRandomValues 在 http 照常可用。
// https 與 localhost 時瀏覽器有原生的 crypto.subtle，這支腳本什麼都不做。
// ⚠️ 必須排在 index.html 的 blazor.webassembly 之前（InsecureContextConventionTests 守門）；外部檔案，因為 CSP 不允許行內腳本。
// ⚠️ 升級 .NET 時，搜尋發佈後 _framework 底下的 dotnet*.js 有沒有用到 crypto.subtle 的其他功能，有的話這裡要跟著補。
(() => {
    if (window.isSecureContext || !window.crypto || window.crypto.subtle) {
        return;
    }

    const K = new Uint32Array([
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    ]);

    const rotr = (x, n) => (x >>> n) | (x << (32 - n));

    // FIPS 180-4 的 SHA-256；回傳 32 bytes 的 ArrayBuffer（與 crypto.subtle.digest 相同）。
    function sha256(bytes) {
        const length = bytes.length;
        const total = ((length + 9 + 63) >> 6) << 6;
        const message = new Uint8Array(total);
        message.set(bytes);
        message[length] = 0x80;
        const view = new DataView(message.buffer);
        view.setUint32(total - 8, Math.floor(length / 0x20000000));
        view.setUint32(total - 4, (length << 3) >>> 0);

        const hash = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]);
        const w = new Uint32Array(64);
        for (let offset = 0; offset < total; offset += 64) {
            for (let i = 0; i < 16; i++) {
                w[i] = view.getUint32(offset + i * 4);
            }
            for (let i = 16; i < 64; i++) {
                const s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >>> 3);
                const s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >>> 10);
                w[i] = (w[i - 16] + s0 + w[i - 7] + s1) >>> 0;
            }

            let [a, b, c, d, e, f, g, h] = hash;
            for (let i = 0; i < 64; i++) {
                const t1 = (h + (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) + ((e & f) ^ (~e & g)) + K[i] + w[i]) >>> 0;
                const t2 = ((rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c))) >>> 0;
                h = g; g = f; f = e; e = (d + t1) >>> 0; d = c; c = b; b = a; a = (t1 + t2) >>> 0;
            }
            hash[0] += a; hash[1] += b; hash[2] += c; hash[3] += d; hash[4] += e; hash[5] += f; hash[6] += g; hash[7] += h;
        }

        const result = new DataView(new ArrayBuffer(32));
        hash.forEach((value, i) => result.setUint32(i * 4, value));
        return result.buffer;
    }

    const toBytes = data => data instanceof ArrayBuffer
        ? new Uint8Array(data)
        : new Uint8Array(data.buffer, data.byteOffset, data.byteLength);

    const subtle = {
        digest: async (algorithm, data) => {
            const name = String(typeof algorithm === 'string' ? algorithm : algorithm?.name ?? '').toUpperCase();
            if (name !== 'SHA-256') {
                throw new Error(`http 連線只提供 SHA-256（js/insecure-context-crypto.js），不支援 ${name}。`);
            }
            return sha256(toBytes(data));
        },
    };

    Object.defineProperty(window.crypto, 'subtle', { value: subtle, configurable: true });
})();
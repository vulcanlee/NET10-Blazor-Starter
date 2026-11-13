// 附件上傳與下載（0.10.3 起，WASM 遷移 M4）。檔案不經過 .NET（WASM）的記憶體：
// 選檔時把瀏覽器的 File 物件留在這裡、只把 id／檔名／大小交給 .NET；存檔時由這裡組 FormData 直接送出。
// 1 GB 的附件若讀進 WASM 再送，會吃光記憶體或讓分頁當掉。

const pending = new Map();
let sequence = 0;

/** 收下 <input type=file> 這次選的檔案，回傳 [{ id, name, size }]。 */
export function capture(input) {
  const picked = [];
  for (const file of input?.files ?? []) {
    const id = `f${++sequence}`;
    pending.set(id, file);
    picked.push({ id, name: file.name, size: file.size });
  }

  // 清掉選取，同一個檔案移除後再選一次也會觸發 change。
  if (input) {
    input.value = '';
  }

  return picked;
}

/** 不再需要的檔案（移除、關窗、存檔完成）。 */
export function release(ids) {
  for (const id of ids ?? []) {
    pending.delete(id);
  }
}

/**
 * 送出 multipart：data（JSON 字串）＋ files（依 fileIds 取出的檔案）＋ removedFileIds。
 * 回傳 { status, body }；連不上伺服器時丟例外（.NET 端收到 JSException，視為離線）。
 */
export async function send(method, url, token, data, fileIds, removedFileIds) {
  const form = new FormData();
  form.append('data', data);
  for (const id of fileIds ?? []) {
    const file = pending.get(id);
    if (file) {
      form.append('files', file, file.name);
    }
  }

  for (const removed of removedFileIds ?? []) {
    form.append('removedFileIds', String(removed));
  }

  const headers = token ? { Authorization: `Bearer ${token}` } : {};
  const response = await fetch(url, { method, headers, body: form, credentials: 'same-origin' });
  return { status: response.status, body: await response.text() };
}

/**
 * 以一次性票證網址下載（伺服器回 Content-Disposition: attachment）。成功回 null，失敗回要顯示的訊息。
 * ⚠️ 同一個網址用帶 download 屬性的連結，不可用 location.assign：後者會先開始一次換頁（觸發 beforeunload），
 * 之後頁面上的 AntDesign 表格切換欄位時會出錯（顯示已刪除時整頁空白，M4 實機發現）。
 * ⚠️ 跨來源（0.10.10 起雙站台）時瀏覽器會忽略 download 屬性、改成整頁導過去（失敗時整個前端換成 API 的 JSON），
 * 所以改用 fetch 取回內容（檔案放在瀏覽器記憶體，不經過 WASM）再以 blob 網址存檔；CORS 已公開 Content-Disposition。
 */
export async function download(url) {
  if (new URL(url, location.href).origin === location.origin) {
    save(url, '');
    return null;
  }

  let response;
  try {
    response = await fetch(url, { credentials: 'omit' });
  } catch {
    return '目前無法連線到伺服器，請稍後再試。';
  }

  if (!response.ok) {
    try {
      const result = await response.json();
      return result.message || result.Message || `下載失敗（HTTP ${response.status}）。`;
    } catch {
      return `下載失敗（HTTP ${response.status}）。`;
    }
  }

  const blobUrl = URL.createObjectURL(await response.blob());
  save(blobUrl, fileNameOf(response.headers.get('Content-Disposition')));
  setTimeout(() => URL.revokeObjectURL(blobUrl), 60000);
  return null;
}

function save(href, fileName) {
  const anchor = document.createElement('a');
  anchor.href = href;
  anchor.download = fileName;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
}

/** Content-Disposition 的檔名：優先 filename*（RFC 5987，中文檔名），其次 filename。 */
function fileNameOf(disposition) {
  if (!disposition) {
    return '';
  }

  const extended = /filename\*\s*=\s*([^']*)''([^;]+)/i.exec(disposition);
  if (extended) {
    try {
      return decodeURIComponent(extended[2].trim());
    } catch { /* 編碼不對就退回 filename */ }
  }

  const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(disposition);
  return plain ? plain[1].trim() : '';
}

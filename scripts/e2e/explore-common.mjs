// .NET 11 全面實機探索（0.10.16 起）的共用工具：收集畫面上的問題、依資料表結構灌測試資料。
import { DatabaseSync } from 'node:sqlite';
import { databaseFile } from './common.mjs';

/**
 * 收集這個分頁的問題：頁面錯誤、主控台錯誤（含 CSP 違規「Refused to …」）、伺服器 5xx 回應、Blazor 錯誤列。
 * 回傳 { list, take() }：take() 取出目前累積的並清空，方便每個情境各自判斷。
 * ignore：可忽略的主控台訊息（正規式），例如情境刻意造成的 4xx 讓瀏覽器印出的「Failed to load resource」。
 */
export function collectProblems(page, { ignore = [/Failed to load resource: the server responded with a status of 4\d\d/] } = {}) {
  const list = [];
  page.on('pageerror', (e) => list.push(`頁面錯誤：${e.message.slice(0, 200)}`));
  page.on('console', (m) => {
    if (m.type() !== 'error') {
      return;
    }

    const text = m.text();
    if (!ignore.some((x) => x.test(text))) {
      list.push(`${/Refused to/.test(text) ? 'CSP' : '主控台'}：${text.slice(0, 200)}`);
    }
  });
  page.on('response', (r) => {
    if (r.status() >= 500) {
      list.push(`伺服器 ${r.status()}：${r.request().method()} ${new URL(r.url()).pathname}`);
    }
  });
  return {
    list,
    async take() {
      if (await page.locator('#blazor-error-ui').isVisible().catch(() => false)) {
        list.push('Blazor 錯誤列出現');
      }

      return list.splice(0);
    },
  };
}

/**
 * 在隔離資料庫插入一列：沒給的 NOT NULL 欄位依型別補值（文字空字串、數字 0、時間現在、ConcurrencyStamp 隨機）。
 * 只用於 Start-E2EHost.ps1 的隔離資料庫。rows 是物件陣列，同一個交易寫入。回傳插入的 Id。
 */
export function insertRows(table, rows) {
  const db = new DatabaseSync(databaseFile);
  const ids = [];
  try {
    const columns = db.prepare(`PRAGMA table_info("${table}")`).all();
    db.exec('BEGIN');
    for (const values of rows) {
      const filled = { ...values };
      for (const c of columns) {
        if (c.name === 'Id' || c.name in filled) {
          continue;
        }

        if (c.name === 'ConcurrencyStamp' || c.name === 'SecurityStamp') {
          filled[c.name] = crypto.randomUUID().replaceAll('-', '');
          continue;
        }

        if (!c.notnull || c.dflt_value !== null) {
          continue;
        }

        const type = (c.type ?? '').toUpperCase();
        filled[c.name] = /At$|Date|Time/.test(c.name) && type === 'TEXT' ? nowText() : type === 'TEXT' ? '' : 0;
      }

      const names = Object.keys(filled);
      const result = db.prepare(`INSERT INTO "${table}" (${names.map((n) => `"${n}"`).join(', ')}) VALUES (${names.map(() => '?').join(', ')})`)
        .run(...names.map((n) => filled[n]));
      ids.push(Number(result.lastInsertRowid));
    }

    db.exec('COMMIT');
  } catch (error) {
    try { db.exec('ROLLBACK'); } catch { /* 沒有交易 */ }
    throw error;
  } finally {
    db.close();
  }

  return ids;
}

/** 現在的時間（資料庫格式，本地時間）。 */
export function nowText() {
  const d = new Date();
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

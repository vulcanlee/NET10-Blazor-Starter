// .NET 11 全面實機探索（0.10.16 起）：維運功能的邊界（多數經 API，畫面最後逐頁檢查沒有錯誤）。
//   O1 備份：立即備份 → 清單出現新檔 → 刪除 → 清單不見、實體檔刪除。
//   O2 資料保留：自訂清除的預覽，不合法的條件（負的天數、未來的日期）回 400；合法的回筆數；以保留 3650 天實際清除不影響近期資料。
//   O3 排程：停用一個作業 → 立即執行（記錄伺服器怎麼處理，不可 5xx）→ 啟用 → 立即執行成功。
//   O4 系統參數：數字參數填文字、低於下限，布林參數填奇怪的值，一律 400 且值沒變；合法的值存得進去、還原後恢復；開窗後別人改過回 409。
//   O5 通知：測試通知 → 未讀數 +1 → 全部已讀 → 未讀 0 → 刪除已讀。
//   O6 寄信測試：Pickup 資料夾多一封信。
//   O7 逐頁開維運頁（排程、健康、備份、資料保留、資料庫用量、系統參數、日誌等級、日誌檢視），沒有頁面錯誤、主控台錯誤、伺服器 5xx。
// 結果寫到 E2E_OUT_DIR/explore-ops-results.json，截圖 explore-ops-*.png。
import { existsSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { apiJsonAsPageUser, createRecorder, databaseFile, execute, launchBrowser, login, newContext, screenshot } from './common.mjs';
import { collectProblems } from './explore-common.mjs';

execute("UPDATE MyUser SET Email = 'support@e2e.test' WHERE Account = 'support'");
const root = dirname(dirname(databaseFile));
const recorder = createRecorder('explore-ops-results.json');
const browser = await launchBrowser();
const msg = (r) => `HTTP ${r.status}「${(r.json?.errorMessage ?? r.json?.message ?? '').slice(0, 60)}」`;

try {
  const context = await newContext(browser);
  const page = await context.newPage();
  const problems = collectProblems(page);
  await login(page);
  const api = (path, options) => apiJsonAsPageUser(page, path, options);

  // O1 備份。
  {
    const before = (await api('/api/v1/backups')).json?.data?.items?.map((x) => x.fileName) ?? [];
    const run = await api('/api/v1/backups/run', { method: 'POST', body: {} });
    let created = null;
    for (let i = 0; i < 60 && !created; i++) {
      await page.waitForTimeout(1000);
      const now = (await api('/api/v1/backups')).json?.data?.items?.map((x) => x.fileName) ?? [];
      created = now.find((x) => !before.includes(x)) ?? null;
    }
    await page.goto('/backups');
    await page.waitForTimeout(2500);
    const shown = created ? (await page.getByText(created).count()) > 0 : false;
    await screenshot(page, 'explore-ops-O1-備份');
    const removed = created ? await api(`/api/v1/backups/${encodeURIComponent(created)}`, { method: 'DELETE' }) : { status: 0 };
    const after = (await api('/api/v1/backups')).json?.data?.items?.map((x) => x.fileName) ?? [];
    const fileLeft = created ? readdirSafe(join(root, 'Backup')).includes(created) : true;
    recorder.add('O1', '備份：立即備份、清單出現、刪除後清單與實體檔都不見', run.status === 200 && created && shown && removed.status === 200 && !after.includes(created) && !fileLeft,
      `立即備份 ${msg(run)}；新檔 ${created ?? '（沒出現）'}；畫面顯示=${shown}；刪除 ${msg(removed)}；之後清單仍有=${after.includes(created)}；實體檔仍在=${fileLeft}`);
  }

  // O2 資料保留。
  {
    const overview = await api('/api/v1/data-retention');
    const row = overview.json?.data?.rows?.find((x) => x.canCustomPurge);
    const key = row?.key;
    const tomorrow = new Date(Date.now() + 86400000).toISOString().slice(0, 10);
    const negative = key ? await api(`/api/v1/data-retention/${key}/preview`, { method: 'POST', body: { keepDays: -1 } }) : { status: 0 };
    const future = key ? await api(`/api/v1/data-retention/${key}/preview`, { method: 'POST', body: { beforeDate: tomorrow } }) : { status: 0 };
    const valid = key ? await api(`/api/v1/data-retention/${key}/preview`, { method: 'POST', body: { keepDays: 3650 } }) : { status: 0 };
    const purge = key ? await api(`/api/v1/data-retention/${key}/custom-purge`, { method: 'POST', body: { keepDays: 3650 } }) : { status: 0 };
    recorder.add('O2', '資料保留：不合法的自訂條件回 400，合法的預覽與清除正常', key && negative.status === 400 && future.status === 400 && valid.status === 200 && purge.status < 500,
      `種類 ${key ?? '（沒有可自訂清除的）'}；負的天數 ${msg(negative)}；未來日期 ${msg(future)}；保留 3650 天預覽 ${msg(valid)}；清除 ${msg(purge)}`);
  }

  // O3 排程。
  {
    const jobs = (await api('/api/v1/scheduled-jobs')).json?.data?.items ?? [];
    const job = jobs.find((x) => /notification|通知/i.test(x.name)) ?? jobs[0];
    const name = encodeURIComponent(job?.name ?? 'x');
    const disable = await api(`/api/v1/scheduled-jobs/${name}/enabled`, { method: 'PUT', body: { enabled: false } });
    const triggerDisabled = await api(`/api/v1/scheduled-jobs/${name}/trigger`, { method: 'POST', body: {} });
    // 等這一次跑完再按下一次（還在跑時回 409「已經在執行中」，那是正確的行為）。
    for (let i = 0; i < 60; i++) {
      const item = (await api('/api/v1/scheduled-jobs')).json?.data?.items?.find((x) => x.name === job?.name);
      if (!item?.isQueued && item?.lastRun?.status !== 'Running') {
        break;
      }
      await page.waitForTimeout(500);
    }
    const enable = await api(`/api/v1/scheduled-jobs/${name}/enabled`, { method: 'PUT', body: { enabled: true } });
    const trigger = await api(`/api/v1/scheduled-jobs/${name}/trigger`, { method: 'POST', body: {} });
    recorder.add('O3', '排程：停用、停用時立即執行（不可 5xx）、啟用、立即執行', disable.status === 200 && triggerDisabled.status < 500 && enable.status === 200 && trigger.status === 200,
      `作業 ${job?.name}；停用 ${msg(disable)}；停用時立即執行 ${msg(triggerDisabled)}；啟用 ${msg(enable)}；立即執行 ${msg(trigger)}`);
  }

  // O4 系統參數。
  {
    const items = (await api('/api/v1/system-parameters')).json?.data?.items ?? [];
    const intItem = items.find((x) => x.definition.kind === 0 && x.definition.min !== null && x.definition.min !== undefined);
    const boolItem = items.find((x) => x.definition.kind === 1);
    const save = (item, value, stamp) => api('/api/v1/system-parameters/save', { method: 'POST', body: { key: item.definition.key, value, expectedStamp: stamp ?? item.override?.concurrencyStamp ?? null } });
    const text = await save(intItem, 'abc');
    const low = await save(intItem, String(intItem.definition.min - 1));
    const weird = await save(boolItem, 'maybe');
    const unchanged = (await api('/api/v1/system-parameters')).json?.data?.items?.find((x) => x.definition.key === intItem.definition.key)?.effectiveValue === intItem.effectiveValue;
    const goodValue = String(Math.max(intItem.definition.min, Number(intItem.effectiveValue) || intItem.definition.min) + 1);
    const good = await save(intItem, goodValue);
    const afterGood = (await api('/api/v1/system-parameters')).json?.data?.items?.find((x) => x.definition.key === intItem.definition.key);
    const stale = await save(intItem, String(Number(goodValue) + 1), 'stale-stamp');
    const current = (await api('/api/v1/system-parameters')).json?.data?.items?.find((x) => x.definition.key === intItem.definition.key);
    const reset = await api('/api/v1/system-parameters/reset', { method: 'POST', body: { key: intItem.definition.key, expectedStamp: current?.override?.concurrencyStamp ?? '' } });
    const afterReset = (await api('/api/v1/system-parameters')).json?.data?.items?.find((x) => x.definition.key === intItem.definition.key);
    recorder.add('O4', '系統參數：不合法的值 400 且沒變、合法的存得進去、過期的版本 409、還原恢復',
      text.status === 400 && low.status === 400 && weird.status === 400 && unchanged && good.status === 200 && afterGood?.effectiveValue === goodValue && stale.status === 409 && reset.status === 200 && afterReset?.effectiveValue === intItem.effectiveValue,
      `${intItem.definition.key}：文字 ${msg(text)}；低於下限 ${msg(low)}；${boolItem.definition.key} 填 maybe ${msg(weird)}；值沒變=${unchanged}；合法 ${msg(good)}→${afterGood?.effectiveValue}；過期版本 ${msg(stale)}；還原 ${msg(reset)}→${afterReset?.effectiveValue}`);
  }

  // O5 通知。
  {
    const before = (await api('/api/v1/notifications/summary')).json?.data?.unreadCount ?? -1;
    const test = await api('/api/v1/system-health/test-notification', { method: 'POST', body: {} });
    let unread = before;
    for (let i = 0; i < 20 && unread <= before; i++) {
      await page.waitForTimeout(500);
      unread = (await api('/api/v1/notifications/summary')).json?.data?.unreadCount ?? -1;
    }
    await page.goto('/');
    await page.waitForTimeout(2500);
    await screenshot(page, 'explore-ops-O5-通知');
    const readAll = await api('/api/v1/notifications/read-all', { method: 'POST', body: {} });
    const zero = (await api('/api/v1/notifications/summary')).json?.data?.unreadCount;
    const removeRead = await api('/api/v1/notifications/read', { method: 'DELETE' });
    const left = (await api('/api/v1/notifications/summary')).json?.data?.latest?.length;
    recorder.add('O5', '通知：測試通知未讀 +1、全部已讀歸 0、刪除已讀', test.status === 200 && unread === before + 1 && readAll.status === 200 && zero === 0 && removeRead.status === 200 && left === 0,
      `測試通知 ${msg(test)}；未讀 ${before}→${unread}；全部已讀 ${msg(readAll)}→${zero}；刪除已讀 ${msg(removeRead)}，剩 ${left}`);
  }

  // O6 寄信測試。
  {
    const mails = join(root, 'Mails');
    const before = readdirSafe(mails).length;
    const test = await api('/api/v1/system-health/test-email', { method: 'POST', body: { to: 'ops@e2e.test' } });
    let after = before;
    for (let i = 0; i < 30 && after <= before; i++) {
      await page.waitForTimeout(500);
      after = readdirSafe(mails).length;
    }
    recorder.add('O6', '寄信測試：Pickup 資料夾多一封信', test.status === 200 && after === before + 1, `寄信測試 ${msg(test)}；信件 ${before}→${after}`);
  }

  // O7 逐頁開維運頁。
  {
    await problems.take();
    const routes = ['/scheduled-jobs', '/system-health', '/backups', '/data-retention', '/database-usage', '/system-parameters', '/log-level-setting', '/logs'];
    const bad = [];
    for (const route of routes) {
      await page.goto(route);
      await page.waitForTimeout(3000);
      await screenshot(page, `explore-ops-O7${route.replace(/[^A-Za-z0-9]+/g, '_')}`);
      const issues = await problems.take();
      if (issues.length) {
        bad.push(`${route}：${issues.join('；')}`);
      }
    }
    recorder.add('O7', '維運頁逐頁開啟：沒有頁面錯誤、主控台錯誤、伺服器 5xx', bad.length === 0, bad.length ? bad.join('｜') : `${routes.length} 頁都正常`);
  }

  await context.close();
} finally {
  await browser.close();
}

function readdirSafe(path) {
  return existsSync(path) ? readdirSync(path) : [];
}

process.exit(recorder.save() ? 0 : 1);

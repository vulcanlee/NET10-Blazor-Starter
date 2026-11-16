// 提示框滑鼠移開後不可殘留（0.10.32 起）：用真的滑鼠軌跡在專案項目頁移動，移開後等 1.5 秒，畫面上不可還有看得到的提示框。
//   T1：慢速滑入操作欄的「修改」停 0.8 秒（提示框要出現），再移到空白處 → 提示框要收起。
//   T2：以不同的停留時間（60～400 毫秒）快速掃過每一列的操作欄按鈕與工具列按鈕（每輪先整頁重新載入，提示框都還沒渲染過）。
//   T3：同樣快速掃過清單標題列的可排序欄位（AntDesign 表格內建的排序提示）。
//   T4：移開後再滑回同一個按鈕，提示框仍會出現（防殘留的處理不可把正常的提示框誤收）。
// 環境變數 E2E_CPU_THROTTLE（例如 6）以 CDP 把 CPU 降速，模擬較慢的電腦（提示框顯示途中滑鼠就離開的時間窗變大）。
// 結果寫到 E2E_OUT_DIR/ux-tooltips-results.json，殘留時截圖在 screenshots/ux-tooltip-*.png。
import { createRecorder, launchBrowser, login, newContext, screenshot } from './common.mjs';
import { seedProjects } from './m04-views.mjs';

seedProjects();

const recorder = createRecorder('ux-tooltips-results.json');
const browser = await launchBrowser();

/** 畫面上看得到的提示框文字（AntDesign 收起後留在 DOM 裡、加上 hidden 類別或 display:none）。 */
async function visibleTooltips(page) {
  return page.evaluate(() => [...document.querySelectorAll('.ant-tooltip')]
    .filter((el) => {
      const style = getComputedStyle(el);
      const rect = el.getBoundingClientRect();
      return !el.classList.contains('ant-tooltip-hidden') && style.display !== 'none' && style.visibility !== 'hidden'
        && Number(style.opacity) > 0 && rect.width > 0 && rect.height > 0;
    })
    .map((el) => el.innerText.trim()));
}

/** 中心點。 */
async function centers(locator) {
  const boxes = [];
  for (const handle of await locator.elementHandles()) {
    const box = await handle.boundingBox();
    if (box) {
      boxes.push({ x: box.x + box.width / 2, y: box.y + box.height / 2 });
    }
  }
  return boxes;
}

/** 依序滑過每個點，每個點停 dwell 毫秒（點與點之間分 4 步移動，像真的滑鼠軌跡）。 */
async function sweep(page, points, dwell) {
  for (const p of points) {
    await page.mouse.move(p.x, p.y, { steps: 4 });
    await page.waitForTimeout(dwell);
  }
}

/** 停在原處等含 text 的提示框出現（最多 3 秒；CPU 降速時首次顯示較慢），回傳看得到的提示框文字。 */
async function waitShown(page, text) {
  let shown = [];
  for (let waited = 0; waited < 3000; waited += 100) {
    await page.waitForTimeout(100);
    shown = await visibleTooltips(page);
    if (shown.some((t) => t.includes(text))) {
      break;
    }
  }
  return shown;
}

/** 移到空白處（頁面標題列左側的空白），等 1.5 秒後檢查殘留。 */
async function leaveAndCount(page) {
  await page.mouse.move(700, 40, { steps: 4 });
  await page.waitForTimeout(1500);
  return visibleTooltips(page);
}

async function openProjects(page) {
  await page.goto('/projects');
  await page.locator('button.crud-action-button').first().waitFor({ timeout: 60000 });
  await page.waitForTimeout(500);
}

try {
  const context = await newContext(browser);
  const page = await context.newPage();
  await login(page);
  const cpuThrottle = Number(process.env.E2E_CPU_THROTTLE ?? 1);
  if (cpuThrottle > 1) {
    const cdp = await context.newCDPSession(page);
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: cpuThrottle });
    console.log(`CPU 降速 ${cpuThrottle} 倍`);
  }

  // T1
  {
    await openProjects(page);
    const edit = page.locator('button.crud-action-button', { hasText: '修改' }).first();
    const [c] = await centers(edit);
    await page.mouse.move(c.x, c.y, { steps: 6 });
    const shown = await waitShown(page, '修改');
    const left = await leaveAndCount(page);
    if (left.length) await screenshot(page, 'ux-tooltip-T1-殘留');
    recorder.add('T1', '慢速滑入「修改」再移開：提示框出現後會收起',
      shown.includes('修改') && left.length === 0, `停留時看到 [${shown.join('、')}]；移開後殘留 [${left.join('、')}]`);
  }

  // T2
  {
    const failures = [];
    for (const dwell of [60, 90, 120, 150, 200, 260, 400]) {
      await openProjects(page);
      const points = [
        ...await centers(page.locator('button.crud-action-button')),
        ...await centers(page.locator('button.toolbar-icon-button')),
      ];
      await sweep(page, points, dwell);
      const left = await leaveAndCount(page);
      if (left.length) {
        failures.push(`${dwell}ms：[${left.join('、')}]`);
        await screenshot(page, `ux-tooltip-T2-${dwell}ms-殘留`);
      }
    }
    recorder.add('T2', '以不同速度掃過操作欄與工具列按鈕：移開後沒有殘留', failures.length === 0,
      failures.length ? `殘留：${failures.join('；')}` : '7 種速度都沒有殘留');
  }

  // T3
  {
    const failures = [];
    for (const dwell of [60, 90, 120, 150, 200, 260, 400]) {
      await openProjects(page);
      const points = await centers(page.locator('.ant-table-column-sorters'));
      await sweep(page, [...points, ...points.slice().reverse()], dwell);
      const left = await leaveAndCount(page);
      if (left.length) {
        failures.push(`${dwell}ms：[${left.join('、')}]`);
        await screenshot(page, `ux-tooltip-T3-${dwell}ms-殘留`);
      }
    }
    recorder.add('T3', '以不同速度掃過可排序的標題列：移開後沒有殘留', failures.length === 0,
      failures.length ? `殘留：${failures.join('；')}` : '7 種速度都沒有殘留');
  }

  // T4
  {
    await openProjects(page);
    const [c] = await centers(page.locator('button.crud-action-button', { hasText: '刪除' }).first());
    await page.mouse.move(c.x, c.y, { steps: 4 });
    await page.waitForTimeout(80);
    await leaveAndCount(page);
    await page.mouse.move(c.x, c.y, { steps: 4 });
    const again = await waitShown(page, '刪除');
    await leaveAndCount(page);
    const [h] = await centers(page.locator('.ant-table-column-sorters').first());
    await page.mouse.move(h.x, h.y, { steps: 4 });
    const sorter = await waitShown(page, '排序');
    const left = await leaveAndCount(page);
    recorder.add('T4', '移開後再滑回：按鈕與排序提示照常出現，移開後收起',
      again.includes('刪除') && sorter.length === 1 && sorter[0].includes('排序') && left.length === 0,
      `滑回「刪除」看到 [${again.join('、')}]；標題列看到 [${sorter.join('、')}]；移開後殘留 [${left.join('、')}]`);
  }

  await context.close();
} finally {
  await browser.close();
}

process.exit(recorder.save() ? 0 : 1);

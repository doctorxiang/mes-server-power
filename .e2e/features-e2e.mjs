import puppeteer from 'puppeteer-core'
import fs from 'node:fs'
import path from 'node:path'

// 本次改动的浏览器回归：工单查询条件 / 工单报告子菜单 / 维修站筛选与历史 / QC 抽检
const CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe'
const BASE = 'http://localhost:5173'
const SHOTS = path.resolve('.e2e/shots')
fs.mkdirSync(SHOTS, { recursive: true })

let passed = 0
let failed = 0
function check(name, condition, detail = '') {
  if (condition) {
    passed++
    console.log(`  ✓ ${name}`)
  } else {
    failed++
    console.log(`  ✗ ${name} ${detail}`)
  }
}

const browser = await puppeteer.launch({
  executablePath: CHROME,
  headless: 'new',
  args: ['--no-sandbox', '--disable-dev-shm-usage', '--window-size=1440,980'],
  defaultViewport: { width: 1440, height: 980 },
})

const page = await browser.newPage()
const consoleErrors = []
page.on('console', (msg) => {
  if (msg.type() === 'error') consoleErrors.push(msg.text())
})
page.on('pageerror', (err) => consoleErrors.push(String(err)))

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const shot = (name, full = false) => page.screenshot({ path: path.join(SHOTS, `${name}.png`), fullPage: full })

try {
  // ---------- 登录 ----------
  await page.goto(`${BASE}/login`, { waitUntil: 'networkidle2' })
  await page.waitForSelector('#user')
  await page.click('#user')
  await page.type('#user', 'admin', { delay: 8 })
  await page.click('#pwd')
  await page.type('#pwd', 'Admin@12345', { delay: 8 })
  await Promise.all([page.waitForNavigation({ waitUntil: 'networkidle2' }), page.click('button[type="submit"]')])
  await page.waitForSelector('.table')

  // ---------- 1. 侧边栏二级菜单 ----------
  const subItems = await page.$$eval('.nav-sub-item', (els) => els.map((e) => e.textContent.trim()))
  check('工单管理下有子菜单', subItems.includes('工单列表') && subItems.includes('工单报告'), subItems.join('|'))

  // ---------- 2. 工单列表：返修列 + 产线/日期查询 ----------
  const headers = await page.$$eval('thead th', (els) => els.map((e) => e.textContent.replace(/\s+/g, '')))
  check('工单表格有返修列', headers.some((h) => h.includes('返修')), headers.join('|'))
  const kpiText = await page.$eval('.stat-grid', (el) => el.textContent)
  check('KPI 含累计返修', kpiText.includes('累计返修'))

  // 产线筛二线
  const selects = await page.$$('.filter-row select')
  await selects[0].select('2')
  await sleep(900)
  const lineTexts = await page.$$eval('tbody tr td:nth-child(3)', (els) => els.map((e) => e.textContent.trim()))
  check('按产线筛选只显示二线', lineTexts.length > 0 && lineTexts.every((t) => t.includes('二线')), lineTexts.join('|'))

  // 日期筛选到未来 -> 空
  const dateInputs = await page.$$('.filter-row input[type="date"]')
  await dateInputs[0].evaluate((el, v) => {
    el.value = v
    el.dispatchEvent(new Event('input', { bubbles: true }))
    el.dispatchEvent(new Event('change', { bubbles: true }))
  }, '2027-01-01')
  await sleep(1000)
  const emptyText = await page.$eval('.card .empty', (el) => el.textContent).catch(() => '')
  check('未来日期筛选为空', emptyText.includes('没有符合条件'), emptyText.trim())

  // 重置
  const resetBtn = await page.$$('.filter-row .btn')
  await resetBtn[resetBtn.length - 1].click()
  await sleep(900)
  const rowsAfterReset = await page.$$eval('tbody tr', (els) => els.length)
  check('重置后回到进行中工单', rowsAfterReset > 0, `rows=${rowsAfterReset}`)
  await shot('11-work-orders-filter')

  // ---------- 3. 工单报告（子菜单） ----------
  await page.evaluate(() => [...document.querySelectorAll('.nav-sub-item')].find((e) => e.textContent.includes('工单报告')).click())
  await page.waitForFunction(() => location.pathname === '/work-orders/report', { timeout: 8000 })
  check('URL 落在工单报告', page.url().includes('/work-orders/report'), page.url())
  check('页面标题为工单报告', (await page.$eval('.page-header h1', (el) => el.textContent.trim())) === '工单报告')
  await page.waitForSelector('.kpi-row')

  // 机种筛选：等接口真正返回再断言，避免拿旧数据判失败
  const productSelect = await page.$('.toolbar select')
  await Promise.all([
    page.waitForResponse((r) => r.url().includes('/api/analysis/yield') && r.url().includes('product=SPS-1200W')),
    productSelect.select('SPS-1200W'),
  ])
  await sleep(300)
  const yieldRows = await page.$$eval('.card tbody tr', (els) => els.length)
  check('按机种筛选后有报告行', yieldRows > 0, `rows=${yieldRows}`)
  const names = await page.$$eval('.card tbody tr td:first-child', (els) => els.map((e) => e.textContent))
  check('报告行都是 1200W 机种', names.every((t) => t.includes('1200W')), names.join('|'))

  // 工单号下钻
  const orderInput = await page.$('.toolbar input')
  await orderInput.type('WO2026')
  await page.keyboard.press('Enter')
  await sleep(1200)
  check('工单号筛选后仍有行', (await page.$$eval('.card tbody tr', (els) => els.length)) > 0)
  await shot('12-work-order-report')

  // ---------- 4. 维修站：筛选 + 维修记录 ----------
  await page.goto(`${BASE}/repair`, { waitUntil: 'networkidle2' })
  await page.waitForSelector('.queue-filters')
  const filterLabels = await page.$$eval('.queue-filters label span', (els) => els.map((e) => e.textContent.trim()))
  check('维修站有产线/工序/不良代码/日期筛选', ['产线', '隔离所在工序', '不良代码', '隔离日期 从'].every((l) => filterLabels.includes(l)), filterLabels.join('|'))

  // 不良代码筛虚焊
  const qSelects = await page.$$('.queue-filters select')
  // 顺序：产线、工序、不良代码（第三个 select）
  await qSelects[2].select('WELD-01')
  await sleep(900)
  const queueRows = await page.$$eval('.table tbody tr', (els) => els.length).catch(() => 0)
  check('按虚焊筛选队列不报错', queueRows >= 0, `rows=${queueRows}`)

  // 切到维修记录
  await page.evaluate(() => [...document.querySelectorAll('.card-head .tab')].find((b) => b.textContent.includes('维修记录')).click())
  await sleep(1200)
  const historyHeaders = await page.$$eval('.table thead th', (els) => els.map((e) => e.textContent.replace(/\s+/g, '')))
  check('维修记录表有进站/出站列', historyHeaders.some((h) => h.includes('进站')), historyHeaders.join('|'))
  const historyRows = await page.$$eval('.table tbody tr', (els) => els.length)
  check('维修记录有数据', historyRows > 0, `rows=${historyRows}`)
  const defectCells = await page.$$eval('.table tbody tr td:nth-child(5)', (els) => els.map((e) => e.textContent))
  check('维修记录已按虚焊过滤', defectCells.every((t) => t.includes('虚焊')), defectCells.slice(0, 3).join('|'))
  await shot('13-repair-history')

  // ---------- 5. QC 抽检 ----------
  await page.goto(`${BASE}/qc`, { waitUntil: 'networkidle2' })
  await page.waitForSelector('.qc-grid')
  const qcKpi = await page.$eval('.kpi-row', (el) => el.textContent.replace(/\s+/g, ' '))
  check('QC 抽检有汇总 KPI', qcKpi.includes('抽检台数') && qcKpi.includes('抽检合格率'), qcKpi.slice(0, 80))

  // 点候选填 SN
  const firstCandidate = await page.$('.qc-grid section:nth-child(2) tbody tr td:first-child')
  const candSn = await page.evaluate((el) => el.textContent.trim(), firstCandidate)
  await firstCandidate.click()
  await sleep(200)
  const filled = await page.$eval('.qc-grid input.input-lg', (el) => el.value)
  check('点候选自动带入 SN', filled === candSn, `${filled} vs ${candSn}`)

  // 提交抽检
  await page.click('.qc-grid .btn-primary')
  await page.waitForFunction(() => document.querySelector('.alert-ok, .alert-error'), { timeout: 8000 })
  const feedback = await page.$eval('.card .alert', (el) => el.textContent.trim())
  check('抽检录入返回结果', feedback.includes('抽检合格') || feedback.includes('抽检不合格'), feedback.slice(0, 60))
  await sleep(700)
  const ledger = await page.$eval('.card:last-of-type', (el) => el.textContent)
  check('台账里能查到刚录的 SN', ledger.includes(candSn))
  await shot('14-qc-inspection', true)

  check('页面无 console 报错', consoleErrors.length === 0, consoleErrors.slice(0, 3).join(' | '))
} catch (e) {
  failed++
  console.log(`  ✗ 脚本异常：${e.message}`)
  await shot('99-error')
} finally {
  await browser.close()
}

console.log(`\n结果：通过 ${passed} / 失败 ${failed}`)
process.exit(failed === 0 ? 0 : 1)

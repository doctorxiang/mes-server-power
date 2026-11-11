import puppeteer from 'puppeteer-core'
import fs from 'node:fs'
import path from 'node:path'

const CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe'
const FRONT = 'http://localhost:5173'
const API = 'http://localhost:5080'
const SHOTS = path.resolve('.e2e/shots')
fs.mkdirSync(SHOTS, { recursive: true })

let passed = 0
let failed = 0
function check(name, cond, detail = '') {
  if (cond) { passed++; console.log(`  ✓ ${name}`) }
  else { failed++; console.log(`  ✗ ${name} ${detail}`) }
}

// ---- 先用接口造一条不良隔离数据 ----
async function api(path, opts = {}, token) {
  const res = await fetch(`${API}${path}`, {
    ...opts,
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
  })
  const text = await res.text()
  let body = null
  try { body = JSON.parse(text) } catch { body = text }
  return { status: res.status, body }
}

const SN = `E2ERP${String(Date.now()).slice(-7)}`
const login = await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ userNameOrEmail: 'admin', password: 'Admin@12345' }) })
const token = login.body.accessToken
const orders = await api('/api/work-orders?status=InProduction', {}, token)
const order = orders.body.find((o) => o.lineId === 1) ?? orders.body[0]
for (const [station, step, result, defect, key] of [
  ['A-01', 'PLUGIN', 'Pass', '', 'e1'],
  ['A-02', 'WAVE', 'Pass', '', 'e2'],
  ['A-03', 'TOUCHUP', 'Pass', '', 'e3'],
  ['A-04', 'VISUAL', 'Fail', 'WELD-01', 'e4'],
]) {
  const r = await api('/api/production/scan', { method: 'POST', body: JSON.stringify({ sn: SN, orderNo: order.orderNo, stationCode: station, stepCode: step, result, defectCode: defect || undefined, clientKey: `${key}-${SN}` }) }, token)
  if (r.status !== 200) { console.log('造数据失败', step, r.status, JSON.stringify(r.body)); process.exit(1) }
}
console.log(`造数完成：${SN} 在 ${order.orderNo} 目视检查判不良`)

// ---- 浏览器验证维修站 UI ----
const browser = await puppeteer.launch({
  executablePath: CHROME,
  headless: 'new',
  args: ['--no-sandbox', '--disable-dev-shm-usage', '--window-size=1440,980'],
  defaultViewport: { width: 1440, height: 980 },
})
const page = await browser.newPage()
const consoleErrors = []
page.on('pageerror', (err) => consoleErrors.push(String(err)))
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

try {
  await page.goto(`${FRONT}/login`, { waitUntil: 'networkidle2' })
  await page.waitForSelector('#user')
  await page.click('#user')
  await page.type('#user', 'admin', { delay: 10 })
  await page.click('#pwd')
  await page.type('#pwd', 'Admin@12345', { delay: 10 })
  await Promise.all([page.waitForNavigation({ waitUntil: 'networkidle2' }), page.click('button[type="submit"]')])
  await page.goto(`${FRONT}/repair`, { waitUntil: 'networkidle2' })
  await sleep(1000)

  check('维修站页面打开', page.url().includes('/repair'))
  const navOk = await page.evaluate(() => document.body.textContent.includes('维修站'))
  check('导航含维修站入口', navOk)

  // 队列应包含刚造的 SN
  await page.waitForFunction((sn) => document.body.textContent.includes(sn), { timeout: 8000 }, SN)
  check('待维修队列出现该 SN', true)

  // 点击队列行 → 详情卡
  await page.evaluate((sn) => {
    const cells = [...document.querySelectorAll('td')]
    const cell = cells.find((c) => c.textContent.includes(sn))
    cell?.closest('tr')?.click()
  }, SN)
  await sleep(800)
  const cardShown = await page.evaluate((sn) => document.body.textContent.includes(sn) && document.body.textContent.includes('确认进站维修'), SN)
  check('点击行带出 SN 详情与进站表单', cardShown)
  await page.screenshot({ path: path.join(SHOTS, 'repair-1-checkin.png') })

  // 进站
  await page.evaluate(() => {
    const btn = [...document.querySelectorAll('button')].find((b) => b.textContent.includes('确认进站维修'))
    btn?.click()
  })
  await sleep(1500)
  const bodyText = await page.evaluate(() => document.body.textContent)
  if (bodyText.includes('进站失败')) {
    const m = bodyText.match(/进站失败[\s\S]{0,120}/)
    console.log('  进站失败详情：', m ? m[0].replace(/\s+/g, ' ') : '')
  }
  check('进站成功显示维修中', bodyText.includes('维修中'))

  // 出站：修复完成
  await page.evaluate(() => {
    const btn = [...document.querySelectorAll('button')].find((b) => b.textContent.includes('修复完成'))
    btn?.click()
  })
  await page.waitForFunction(() => document.body.textContent.includes('出站成功'), { timeout: 8000 })
  check('修复出站成功提示', true)
  await sleep(600)
  await page.screenshot({ path: path.join(SHOTS, 'repair-2-checkout.png') })

  check('无未捕获前端异常', consoleErrors.length === 0, consoleErrors.join(' | '))
} finally {
  await browser.close()
}

console.log(`\n结果：通过 ${passed} / 失败 ${failed}`)
process.exit(failed ? 1 : 0)

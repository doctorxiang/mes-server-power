import puppeteer from 'puppeteer-core'
import fs from 'node:fs'
import path from 'node:path'

const CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe'
const BASE = 'http://localhost:5173'
const SHOTS = path.resolve('.e2e/shots')
fs.mkdirSync(SHOTS, { recursive: true })

let passed = 0
let failed = 0
const failures = []

function check(name, condition, detail = '') {
  if (condition) {
    passed++
    console.log(`  ✓ ${name}`)
  } else {
    failed++
    failures.push(`${name} ${detail}`)
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
const shot = async (name, full = false) => page.screenshot({ path: path.join(SHOTS, `${name}.png`), fullPage: full })

try {
  // 1. 登录页
  await page.goto(`${BASE}/login`, { waitUntil: 'networkidle2' })
  await page.waitForSelector('#user')
  check('登录页渲染', await page.$eval('h2', (el) => el.textContent.trim() === '登录'))
  await shot('01-login')

  // 2. 登录
  await page.click('#user')
  await page.type('#user', 'admin', { delay: 10 })
  await page.click('#pwd')
  await page.type('#pwd', 'Admin@12345', { delay: 10 })
  await Promise.all([page.waitForNavigation({ waitUntil: 'networkidle2' }), page.click('button[type="submit"]')])
  await page.waitForSelector('.table')
  check('登录后进入工单管理', page.url().includes('/work-orders'))
  const rowCount = await page.$$eval('tbody tr', (rows) => rows.length)
  check('工单列表有数据', rowCount >= 3, `rows=${rowCount}`)
  await shot('02-work-orders')

  // 3. 工单详情
  await Promise.all([page.waitForNavigation({ waitUntil: 'networkidle2' }), page.click('tbody tr')])
  await page.waitForSelector('.flow')
  const stepCount = await page.$$eval('.flow-step', (els) => els.length)
  check('工单详情展示 9 道工序', stepCount === 9, `steps=${stepCount}`)
  await shot('03-work-order-detail')

  // 4. 扫码报工
  const sn = `E2E${Date.now().toString().slice(-8)}`
  await page.goto(`${BASE}/scan`, { waitUntil: 'networkidle2' })
  await page.waitForSelector('.scan-card')
  await page.waitForFunction(() => document.querySelectorAll('.order-chip').length > 0)
  check('报工页带出在制工单', (await page.$$('.order-chip')).length > 0)

  await page.click('input.input-lg')
  await page.type('input.input-lg', sn, { delay: 5 })
  await page.keyboard.press('Enter')
  await page.waitForSelector('.feedback.ok, .feedback.fail', { timeout: 8000 })
  const fbTitle = await page.$eval('.feedback .fb-title', (el) => el.textContent.trim())
  check('首站扫码上线成功', fbTitle.includes('合格放行') || fbTitle.includes('完成'), fbTitle)
  check('反馈显示下一站', (await page.$eval('.feedback', (el) => el.textContent)).includes('过锡炉'))
  await shot('04-scan')

  // 5. 第二站（过锡炉）
  await page.select('select.select', 'A-02')
  await sleep(600)
  await page.click('input.input-lg')
  await page.type('input.input-lg', sn, { delay: 5 })
  await page.keyboard.press('Enter')
  await sleep(1200)
  const fb2 = await page.$eval('.feedback', (el) => el.textContent)
  check('第二站过站成功', fb2.includes('报工成功') || fb2.includes('合格放行'), fb2.slice(0, 60))

  // 6. 不良弹窗
  await page.select('select.select', 'A-04')
  await sleep(600)
  await page.click('input.input-lg')
  await page.type('input.input-lg', 'E2E-FAIL-001', { delay: 5 })
  await page.keyboard.press('F2')
  await page.waitForSelector('.defect-btn')
  check('F2 唤出不良选择', (await page.$$('.defect-btn')).length >= 10)
  await shot('05-defect-modal')
  await page.keyboard.press('Escape')
  await sleep(300)

  // 7. 追溯
  await page.goto(`${BASE}/trace?sn=${sn}`, { waitUntil: 'networkidle2' })
  await page.waitForSelector('.timeline')
  const tlCount = await page.$$eval('.timeline li', (els) => els.length)
  check('追溯时间线 9 步', tlCount === 9, `steps=${tlCount}`)
  const passCount = await page.$$eval('.timeline li.pass', (els) => els.length)
  check('追溯显示已过站工序', passCount >= 2, `pass=${passCount}`)
  await shot('06-trace')

  // 8. 看板
  await page.goto(`${BASE}/dashboard`, { waitUntil: 'networkidle2' })
  await page.waitForSelector('.board-kpis')
  await sleep(1200)
  const kpis = await page.$$eval('.kpi', (els) => els.length)
  check('看板 6 个 KPI', kpis === 6, `kpis=${kpis}`)
  const bars = await page.$$eval('.bar-col', (els) => els.length)
  check('小时产出柱状图渲染', bars > 0, `bars=${bars}`)
  await shot('07-dashboard')

  // 9. 主数据
  await page.goto(`${BASE}/master`, { waitUntil: 'networkidle2' })
  await page.waitForSelector('.route-step')
  const routes = await page.$$eval('.route-step', (els) => els.length)
  check('主数据工艺路线', routes >= 9, `routes=${routes}`)
  await shot('08-master')

  // 10. 账号管理
  await page.goto(`${BASE}/users`, { waitUntil: 'networkidle2' })
  await page.waitForSelector('tbody tr')
  check('账号管理列表', (await page.$$('tbody tr')).length >= 6)
  await shot('09-users')

  // 11. 移动端（PDA）
  await page.setViewport({ width: 390, height: 844, isMobile: true, hasTouch: true })
  await page.goto(`${BASE}/scan`, { waitUntil: 'networkidle2' })
  await page.waitForSelector('.scan-card')
  await sleep(600)
  await shot('10-mobile-scan')
  const bodyWidth = await page.evaluate(() => document.body.scrollWidth)
  check('移动端无横向溢出', bodyWidth <= 400, `width=${bodyWidth}`)

  // 12. 权限：普通用户访问账号管理被拦截
  await page.setViewport({ width: 1440, height: 980 })
  await page.evaluate(() => localStorage.clear())
  await page.goto(`${BASE}/login`, { waitUntil: 'networkidle2' })
  await page.waitForSelector('#user')
  await page.type('#user', 'operator1')
  await page.type('#pwd', 'Operator@123')
  await Promise.all([page.waitForNavigation({ waitUntil: 'networkidle2' }), page.click('button[type="submit"]')])
  await page.goto(`${BASE}/users`, { waitUntil: 'networkidle2' })
  await sleep(600)
  check('操作工访问账号管理被重定向', !page.url().includes('/users'), page.url())

  const realErrors = consoleErrors.filter(
    (e) => !e.includes('favicon') && !e.includes('WebSocket') && !e.includes('Failed to load resource'),
  )
  check('无未捕获前端异常', realErrors.length === 0, realErrors.slice(0, 2).join(' | '))
} catch (e) {
  failed++
  failures.push(`脚本异常：${e.message}`)
  console.error('脚本异常：', e)
  await shot('error-state').catch(() => {})
} finally {
  await browser.close()
}

console.log(`\n结果：通过 ${passed} / 失败 ${failed}`)
if (failures.length) console.log(failures.join('\n'))
process.exit(failed === 0 ? 0 : 1)

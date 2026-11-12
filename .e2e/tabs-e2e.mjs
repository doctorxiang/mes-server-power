import puppeteer from 'puppeteer-core'

const CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe'
const BASE = 'http://localhost:5173'
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

let pass = 0
let fail = 0
function check(name, ok, extra = '') {
  if (ok) {
    pass++
    console.log(`  ok   ${name}`)
  } else {
    fail++
    console.log(`  FAIL ${name} ${extra}`)
  }
}

const browser = await puppeteer.launch({
  executablePath: CHROME,
  headless: 'new',
  args: ['--no-sandbox'],
  defaultViewport: { width: 1440, height: 900 },
})
const page = await browser.newPage()

await page.goto(`${BASE}/login`, { waitUntil: 'networkidle2' })
await page.evaluate(() => {
  localStorage.clear()
  sessionStorage.clear()
})
await page.goto(`${BASE}/login`, { waitUntil: 'networkidle2' })
await page.waitForSelector('#user')
await page.click('#user')
await page.type('#user', 'admin')
await page.click('#pwd')
await page.type('#pwd', 'Admin@12345')
await Promise.all([page.waitForNavigation({ waitUntil: 'networkidle2' }), page.click('button[type="submit"]')])

const tabTitles = () => page.$$eval('.page-tab .tab-label', (els) => els.map((e) => e.textContent.trim()))
const activeTab = () => page.$eval('.page-tab.active .tab-label', (el) => el.textContent.trim())

// ---------- 1. 默认页签 ----------
await page.goto(`${BASE}/work-orders`, { waitUntil: 'networkidle2' })
await page.waitForSelector('.tabbar')
await sleep(400)
check('进入列表页只有一个（置顶）页签', (await tabTitles()).length === 1, JSON.stringify(await tabTitles()))
check('面包屑 = 工单管理 / 工单列表', (await page.$$eval('.crumb', (els) => els.map((e) => e.textContent.trim()))).join('|') === '工单管理|工单列表')
await page.screenshot({ path: '.e2e/shots/30-tabs-home.png' })

// ---------- 2. 打开多个页签 ----------
await page.goto(`${BASE}/work-orders/report`, { waitUntil: 'networkidle2' })
await page.waitForSelector('.kpi-row')
await page.goto(`${BASE}/trace`, { waitUntil: 'networkidle2' })
await page.waitForSelector('.page-header')
await page.goto(`${BASE}/qc`, { waitUntil: 'networkidle2' })
await page.waitForSelector('.page-header')
await sleep(300)
check('四个页签', (await tabTitles()).length === 4, JSON.stringify(await tabTitles()))
check('当前页签是 QC 抽检', (await activeTab()) === 'QC 抽检')
await page.screenshot({ path: '.e2e/shots/31-tabs-multi.png' })

// ---------- 3. 点页签切换 ----------
await (await page.$$('.page-tab'))[1].click()
await sleep(600)
check('点工单报告页签跳转', page.url().endsWith('/work-orders/report'), page.url())
check('激活态跟着走', (await activeTab()) === '工单报告')

// ---------- 4. 关闭当前页签 → 落到右侧邻居 ----------
const reportTab = (await page.$$('.page-tab'))[1]
await reportTab.hover()
await (await reportTab.$$('.tab-x'))[1].click() // 第二个槽位是关闭
await sleep(600)
check('关闭当前页签后落到右侧邻居（SN 追溯）', (await activeTab()) === 'SN 追溯', await activeTab())
check('页签剩 3 个', (await tabTitles()).length === 3, JSON.stringify(await tabTitles()))

// ---------- 5. 关闭非当前页签（QC 抽检） ----------
const all = await page.$$('.page-tab')
const titles = await tabTitles()
const qcIdx = titles.indexOf('QC 抽检')
await all[qcIdx].hover()
await (await all[qcIdx].$$('.tab-x'))[1].click()
await sleep(400)
check('关掉非当前页签后仍停在 SN 追溯', (await activeTab()) === 'SN 追溯')
check('页签剩 2 个', (await tabTitles()).length === 2, JSON.stringify(await tabTitles()))

// ---------- 6. 置顶 SN 追溯 → 挪到置顶区、无关闭 ----------
const titlesPre = await tabTitles()
const traceTab = (await page.$$('.page-tab'))[titlesPre.indexOf('SN 追溯')]
await traceTab.hover()
await (await traceTab.$$('.tab-x'))[0].click() // 第一个槽位是置顶
await sleep(400)
const titles6 = await tabTitles()
check('置顶后排到未置顶前面', titles6[1] === 'SN 追溯' && titles6.length === 2, JSON.stringify(titles6))
const traceAfter = (await page.$$('.page-tab'))[1]
check('置顶页签只有取消置顶一个按钮', (await traceAfter.$$('.tab-x')).length === 1)
check('置顶页签显示图钉', Boolean(await traceAfter.$('.tab-pin')))
await page.screenshot({ path: '.e2e/shots/32-tabs-pinned.png' })

// ---------- 7. 刷新后页签还在 ----------
await page.reload({ waitUntil: 'networkidle2' })
await page.waitForSelector('.tabbar')
await sleep(400)
check('刷新后页签保留 2 个', (await tabTitles()).length === 2, JSON.stringify(await tabTitles()))

// ---------- 8. 关闭全部（保留置顶） ----------
await page.click('.tabbar-actions .btn-icon:nth-child(2)')
await sleep(500)
const after = await tabTitles()
check('关闭全部后只剩置顶页签', after.length === 2 && after.includes('SN 追溯'), JSON.stringify(after))
check('当前页被关后跳到置顶页签', (await activeTab()) === 'SN 追溯', page.url())
await page.screenshot({ path: '.e2e/shots/33-tabs-closed-all.png' })

// ---------- 9. 工单详情页签带编号 ----------
await page.goto(`${BASE}/work-orders`, { waitUntil: 'networkidle2' })
await page.waitForSelector('tbody tr')
await page.click('tbody tr')
await page.waitForSelector('.page-header')
await sleep(400)
check('详情页签带 #编号', (await tabTitles()).some((t) => /工单详情 #\d+/.test(t)), JSON.stringify(await tabTitles()))
check('详情面包屑 = 工单管理 / 工单详情 #编号', (await page.$$eval('.crumb', (els) => els.map((e) => e.textContent.trim()))).join('|').includes('工单管理|工单详情 #'))

console.log(`\n结果：通过 ${pass} / 失败 ${fail}`)
await browser.close()
process.exit(fail ? 1 : 0)

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

const sidebarW = () => page.$eval('.sidebar', (el) => Math.round(el.getBoundingClientRect().width))
const subH = () =>
  page.$eval('.nav-sub-wrap', (el) => Math.round(el.getBoundingClientRect().height))
const visible = (sel) =>
  page.$eval(sel, (el) => {
    const r = el.getBoundingClientRect()
    return r.width > 0 && r.height > 0
  })

// ---------- 1. 默认展开 ----------
await page.goto(`${BASE}/work-orders`, { waitUntil: 'networkidle2' })
await page.waitForSelector('.nav-branch')
await sleep(400)
check('默认分组是展开的', (await page.$eval('.nav-branch', (el) => el.classList.contains('open'))))
check('二级菜单有高度（可见）', (await subH()) > 30, `h=${await subH()}`)
check('二级菜单两项', (await page.$$eval('.nav-sub-item', (els) => els.length)) === 2)
await page.screenshot({ path: '.e2e/shots/20-sidebar-full-open.png' })

// ---------- 2. 点分组标题收起 ----------
await page.click('.nav-parent')
await sleep(500)
check('点一下收起（无 open 类）', !(await page.$eval('.nav-branch', (el) => el.classList.contains('open'))))
check('收起后二级菜单高度为 0', (await subH()) < 3, `h=${await subH()}`)
const caretRot = () => page.$eval('.nav-caret', (el) => getComputedStyle(el).transform)
const rotClosed = await caretRot()
check('收起时箭头朝右（未旋转）', rotClosed === 'none', rotClosed)
await page.screenshot({ path: '.e2e/shots/21-sidebar-branch-collapsed.png' })

// ---------- 3. 刷新后记住收起状态 ----------
await page.reload({ waitUntil: 'networkidle2' })
await page.waitForSelector('.nav-branch')
await sleep(400)
check('刷新后仍是收起', !(await page.$eval('.nav-branch', (el) => el.classList.contains('open'))))

// ---------- 4. 再点一下展开 ----------
await page.click('.nav-parent')
await sleep(500)
check('再点一下展开', await page.$eval('.nav-branch', (el) => el.classList.contains('open')))
await sleep(300)
const rotOpen = await caretRot()
check('展开后箭头转 90 度朝下', rotOpen !== 'none' && rotOpen.startsWith('matrix(') && rotOpen.includes('1, -1'), rotOpen)

// ---------- 5. 收起侧边栏 → 图标条 ----------
await page.click('.layout-switch .btn-icon')
await sleep(500)
check('收起后侧边栏宽 64px', (await sidebarW()) === 64, `w=${await sidebarW()}`)
check('文字标签隐藏', !(await visible('.nav-label')))
check('图标仍显示', await visible('.nav-item svg'))
await page.screenshot({ path: '.e2e/shots/22-sidebar-icon.png' })

// ---------- 6. 图标条下点分组 → 自动展开侧边栏 ----------
await page.click('.nav-parent')
await sleep(500)
check('图标条下点分组恢复展开态', (await sidebarW()) === 224, `w=${await sidebarW()}`)
check('同时分组是打开的', await page.$eval('.nav-branch', (el) => el.classList.contains('open')))

// ---------- 7. 关闭侧边栏 → 内容占满 ----------
await page.click('.layout-switch .btn-icon:nth-child(2)')
await sleep(400)
check('关闭后侧边栏不显示', !(await visible('.sidebar')))
const pageW = await page.$eval('.page', (el) => Math.round(el.getBoundingClientRect().width))
check('内容区占满（>1400）', pageW > 1400, `page=${pageW}`)
await page.screenshot({ path: '.e2e/shots/23-sidebar-hidden.png' })

// ---------- 8. Ctrl+B 恢复 ----------
await page.keyboard.down('Control')
await page.keyboard.press('KeyB')
await page.keyboard.up('Control')
await sleep(400)
check('Ctrl+B 恢复展开', (await sidebarW()) === 224, `w=${await sidebarW()}`)

// ---------- 9. 进工单报告，分组自动展开 ----------
await page.click('.nav-parent') // 先手动收起
await sleep(400)
await page.goto(`${BASE}/work-orders/report`, { waitUntil: 'networkidle2' })
await page.waitForSelector('.nav-branch')
await sleep(400)
check('进入报告页自动展开分组', await page.$eval('.nav-branch', (el) => el.classList.contains('open')))
check('工单报告子项高亮', await page.$eval('.nav-sub-item.router-link-active', (el) => el.textContent.includes('工单报告')))
await page.screenshot({ path: '.e2e/shots/24-sidebar-report.png' })

console.log(`\n结果：通过 ${pass} / 失败 ${fail}`)
await browser.close()
process.exit(fail ? 1 : 0)

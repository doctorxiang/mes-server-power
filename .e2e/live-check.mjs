import puppeteer from 'puppeteer-core'

const CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe'
const SITE = 'https://doctorxiang.github.io/mes-server-power'
const browser = await puppeteer.launch({ executablePath: CHROME, headless: 'new', args: ['--no-sandbox'] })
const page = await browser.newPage()
const fails = []
try {
  // 1. 未登录访问深链，应重定向到子路径下的登录页
  await page.goto(SITE + '/work-orders', { waitUntil: 'networkidle2', timeout: 45000 })
  const u1 = page.url()
  console.log('深链跳转:', u1)
  if (!u1.startsWith(SITE + '/login')) fails.push('深链未跳到子路径登录页')

  // 2. 登录
  await page.waitForSelector('#user', { timeout: 15000 })
  await page.type('#user', 'admin')
  await page.type('#pwd', 'Admin@12345')
  await page.click('button[type=submit]')
  await page.waitForFunction(() => !location.pathname.endsWith('/login'), { timeout: 30000 })
  const u2 = page.url()
  console.log('登录后:', u2)
  if (!u2.startsWith(SITE)) fails.push('登录后跳出了项目子路径')

  // 3. 刷新后仍在子路径且不掉登录
  await page.reload({ waitUntil: 'networkidle2', timeout: 45000 })
  const u3 = page.url()
  console.log('刷新后:', u3)
  if (!u3.startsWith(SITE)) fails.push('刷新后路径不对')
  const rows = await page.$$eval('tbody tr', (els) => els.length).catch(() => 0)
  console.log('列表行数:', rows)
  if (rows === 0) fails.push('刷新后数据没加载出来')
} catch (e) {
  fails.push(e.message)
} finally {
  await browser.close()
}
console.log(fails.length ? 'FAIL: ' + fails.join('; ') : 'PASS: 深链/登录/刷新 全部正常')
process.exit(fails.length ? 1 : 0)

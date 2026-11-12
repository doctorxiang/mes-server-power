namespace MesApi.Infrastructure;

/// <summary>
/// 内网可用的接口文档页。刻意不引 CDN（车间内网多数不通外网），
/// 直接读 /openapi/v1.json 在前端渲染，无外部依赖。
/// </summary>
public static class DocsPage
{
    public static void MapDocsPage(this WebApplication app)
    {
        app.MapGet("/docs", () => Results.Content(Html, "text/html; charset=utf-8"))
           .AllowAnonymous()
           .ExcludeFromDescription();
    }

    private const string Html = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>MES API 文档</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body { margin:0; background:#141416; color:#e8e8ea; font:14px/1.6 system-ui,"Segoe UI","Microsoft YaHei",sans-serif; }
  header { padding:22px 28px 16px; border-bottom:1px solid #2a2a2e; display:flex; align-items:baseline; gap:14px; flex-wrap:wrap; }
  h1 { font-size:17px; font-weight:500; margin:0; }
  .hint { color:#8c8c94; font-size:12px; }
  main { padding:20px 28px 60px; max-width:1100px; }
  .tag { font-size:13px; font-weight:500; color:#9fc7f5; margin:26px 0 10px; letter-spacing:.02em; }
  .op { display:grid; grid-template-columns:74px 1fr; gap:12px; align-items:start; padding:10px 12px;
        border:1px solid #2a2a2e; border-radius:8px; margin-bottom:8px; background:#1a1a1e; }
  .m { font:500 11px/18px ui-monospace,Consolas,monospace; text-align:center; border-radius:4px; padding:2px 0; }
  .get { background:#10281c; color:#6fce8f; } .post { background:#12243a; color:#7db4ee; }
  .put { background:#33280f; color:#e0b063; } .delete { background:#331416; color:#e88b8b; }
  .path { font:13px/1.5 ui-monospace,Consolas,monospace; color:#e8e8ea; word-break:break-all; }
  .sum { color:#8c8c94; font-size:12px; }
  .lock { color:#8c8c94; font-size:11px; margin-left:6px; }
  .err { color:#e88b8b; }
</style>
</head>
<body>
<header>
  <h1>MES API 文档</h1>
  <span class="hint">来源 /openapi/v1.json · 生产环境可通过 Docs:Enabled=false 关闭</span>
</header>
<main id="root"><p class="hint">加载中…</p></main>
<script>
const METHOD_CLASS = { get:'get', post:'post', put:'put', delete:'delete', patch:'put' };
const esc = s => String(s ?? '').replace(/[&<>]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));

fetch('/openapi/v1.json').then(r => r.json()).then(doc => {
  const groups = {};
  for (const [path, ops] of Object.entries(doc.paths || {})) {
    for (const [method, op] of Object.entries(ops)) {
      const tag = (op.tags && op.tags[0]) || '其它';
      (groups[tag] ||= []).push({ path, method, op });
    }
  }
  const root = document.getElementById('root');
  if (!Object.keys(groups).length) { root.innerHTML = '<p class="hint">暂无接口</p>'; return; }
  root.innerHTML = Object.entries(groups).map(([tag, list]) => `
    <div class="tag">${esc(tag)} <span class="hint">(${list.length})</span></div>
    ${list.map(({path, method, op}) => `
      <div class="op">
        <div class="m ${METHOD_CLASS[method] || ''}">${method.toUpperCase()}</div>
        <div>
          <div class="path">${esc(path)}${op.security ? '<span class="lock">需登录</span>' : '<span class="lock">公开</span>'}</div>
          <div class="sum">${esc(op.summary || op.operationId || '')}</div>
        </div>
      </div>`).join('')}
  `).join('');
}).catch(e => {
  document.getElementById('root').innerHTML = '<p class="err">加载接口文档失败：' + esc(e.message) + '</p>';
});
</script>
</body>
</html>
""";
}

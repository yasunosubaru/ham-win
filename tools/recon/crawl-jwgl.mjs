#!/usr/bin/env node
/**
 * jwgl.whu.edu.cn 接口侦察爬虫。
 *
 * 用途：把教务系统的接口面**系统性地**爬成一份机器可读的契约，
 * 供 tests/Ham.Tests/ReconContractTests.cs 断言，避免"凭印象改路径"。
 *
 * 两种模式：
 *   1) 静态（默认，**零凭据**）——沿静态资源图 BFS，从所有 JS 里抽
 *      .html 动作路径、paramMap 字段、jqGrid 列名。结果**缓存**在 out/bfs-cache.json。
 *   2) 存在性判定（设 HAM_JWGL_COOKIES）——用**正文措辞**判断每个 .html
 *      动作是否真实存在。这是 zfsoft 上唯一有效的 oracle，理由见下方 JUDGE。
 *
 * 用法：
 *   node crawl-jwgl.mjs                      只跑静态 BFS（零凭据）
 *   node crawl-jwgl.mjs --verify             只跑存在性判定（需会话，读缓存）
 *   node crawl-jwgl.mjs --refresh            忽略缓存重爬
 *
 * 输出：out/contract.json + out/REPORT.md + out/bfs-cache.json（都不参与 .NET 构建）
 */
import { writeFileSync, mkdirSync, existsSync, readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const OUT = join(HERE, 'out');
const ORIGIN = 'https://jwgl.whu.edu.cn';
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 '
         + '(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36';

/** 两个种子，都不需要登录。登录页给出框架资源图，成绩页脚本是全部契约的来源。 */
const SEEDS = [
  '/xtgl/login_slogin.html',
  '/js/comp/jwglxt/cjgl/cjcx/cxDgXscj.js',
];

/** 需要会话才能拿到的入口。带会话时会把它们展开成动作表。 */
const AUTH_PAGES = [
  '/xtgl/index_initMenu.html',        // 菜单是权威路径来源
  '/kbcx/xskbcx_cxXskbcxIndex.html',  // 课表页，其 JS 里才有课表数据接口
];

/**
 * 我们代码里实际在用的接口。
 *
 * 为什么要显式列出来：静态 BFS 的种子是成绩页，**够不到课表模块**——
 * 课表页脚本的静态路径不是从页面名派生的（模块段猜不出来，实测 4 种猜法全 404），
 * 只能登录后拿页面再顺着 script src 走；而带会话的展开又依赖会话足够长寿。
 *
 * 既然目的是「验证我们用的接口是不是真的存在」，那就把它列成一份待验证清单，
 * 与 BFS 的发现**合并**成动作表。这样存在性判定永远覆盖它们，
 * 测试也不必依赖某一次爬取是否恰好覆盖到。
 */
const KNOWN_ACTIONS = [
  '/kbcx/xskbcx_cxXsgrkb.html',              // 课表（实测 200 / 32601B / kbList 20 门）
  '/cjcx/cjcx_cxDgXscj.html',                // 成绩：菜单里唯一的学生成绩查询入口
  '/cjcx/cjcx_cxXsgrcj.html',                // 成绩：页面 JS 三元的"学生分支"——实测 404
  '/xtgl/index_cxYhxxIndex.html',            // 学籍信息
  '/cjcx/cjcx_cxXmblbzlist.html',            // 课程性质下拉（成绩页内联脚本用到）
];

const log = (...a) => console.log(...a);

async function get(url, headers = {}) {
  try {
    const r = await fetch(url, {
      headers: { 'User-Agent': UA, 'Accept-Language': 'zh-CN,zh;q=0.9', ...headers },
      redirect: 'follow',
      signal: AbortSignal.timeout(30000),
    });
    return { status: r.status, url: r.url, text: await r.text(),
             type: r.headers.get('content-type') || '' };
  } catch (e) {
    return { status: 0, url, text: '', type: '', error: String(e) };
  }
}

// ───────────────────────────── 抽取器 ─────────────────────────────

function harvestAssets(text, baseUrl) {
  const out = new Set();
  const push = (u) => {
    try {
      const abs = new URL(u, baseUrl);
      if (abs.origin !== ORIGIN) return;
      if (!/\.(js|css|png|jpg|gif|svg|woff2?|ttf|ico)(\?|$)/i.test(abs.pathname + abs.search)) return;
      out.add(abs.pathname + abs.search);
    } catch { /* 非法 URL */ }
  };
  for (const m of text.matchAll(/<script[^>]+src=["']([^"']+)["']/gi)) push(m[1]);
  for (const m of text.matchAll(/<link[^>]+href=["']([^"']+)["']/gi)) push(m[1]);
  for (const m of text.matchAll(/["'(](\/(?:js|zftal-ui-v5-1\.0\.2|xtgl|commonShow|images|css)\/[^"'()\s]+\.(?:js|css))["')]/gi)) push(m[1]);
  return out;
}

function harvestActions(text) {
  const set = new Set();
  for (const m of text.matchAll(/["'(](\/[a-zA-Z0-9_\-]+\/[a-zA-Z0-9_\-]+\.html)([^"'()\s]*)/g)) set.add(m[1] + (m[2] || ''));
  for (const m of text.matchAll(/["'`](\/[a-zA-Z0-9_\-]+\/[a-zA-Z0-9_\-]+\.html)["'`]\s*\+\s*["'`]([^"'`]*)["'`]/g)) set.add(m[1] + m[2]);
  return set;
}

/** 抽 paramMap()，并区分学生分支 / 教工分支的字段。 */
function harvestParamMap(text) {
  const i = text.indexOf('function paramMap');
  if (i < 0) return null;
  let depth = 0;
  const j = text.indexOf('{', i);
  let k = j;
  for (; k < text.length; k++) {
    if (text[k] === '{') depth++;
    else if (text[k] === '}') { depth--; if (depth === 0) { k++; break; } }
  }
  const body = text.slice(i, k);
  const gateAt = body.indexOf('jsxx');
  const student = new Set();
  const staff = new Set();
  for (const m of body.matchAll(/requestMap\[["']([a-zA-Z0-9_]+)["']\]\s*=/g)) {
    (m.index > gateAt ? staff : student).add(m[1]);
  }
  for (const m of body.matchAll(/^\s*["']?([a-zA-Z_][a-zA-Z0-9_]*)["']?\s*:\s*\$\(/gm)) student.add(m[1]);
  return { student: [...student], staff: [...staff], raw: body };
}

/** 抽 getGridColModel() 的列名——一行成绩里实际有的字段。 */
function harvestColumns(text) {
  const i = text.indexOf('function getGridColModel');
  if (i < 0) return [];
  return [...new Set([...text.slice(i, i + 12000).matchAll(/name\s*[:=]\s*["']([a-zA-Z0-9_]+)["']/g)].map(m => m[1]))];
}

/** 抽 jqGrid 的 prmNames——分页参数是它自动带的，不是手写的。 */
function harvestPrmNames(text) {
  const m = text.match(/prmNames\s*:\s*\{([^}]*)\}/);
  if (!m) return {};
  return Object.fromEntries([...m[1].matchAll(/(\w+)\s*:\s*["']([^"']+)["']/g)].map(x => [x[1], x[2]]));
}

/**
 * 存在性判定。
 *
 * 三条都踩过，所以写在这里当纪律：
 *  1. **必须带会话**。否则全部拿到登录页，判定全是假的。
 *  2. **不能只看状态码**。zfsoft 对不存在的动作返回 200 + 一段中文错误。
 *  3. **不能看有没有 jQuery**。zftal 的错误页**自带 jquery.min.js**，
 *     「这个页面有没有 jQuery」完全不能用来判断页面存不存在。
 *     我已经因此误判过两次。
 */
function judge(status, finalUrl, text) {
  if (/请求的方法.+未定义|不存在或者已经移除|该操作的页面不存在/.test(text)) return 'missing';
  if (/统一身份认证|请先登录/.test(text) || /authserver\/login/.test(finalUrl)) return 'login-required';
  if (status === 200) return 'exists';
  return 'http-' + status;
}

// ───────────────────────────── 主流程 ─────────────────────────────

async function main() {
  if (!existsSync(OUT)) mkdirSync(OUT, { recursive: true });

  const argv = process.argv.slice(2);
  const refresh = argv.includes('--refresh');
  const cookieHeader = (process.env.HAM_JWGL_COOKIES || '').trim();
  const hasSession = cookieHeader.length > 0;

  const cachePath = join(OUT, 'bfs-cache.json');
  const contractPath = join(OUT, 'contract.json');

  // ── 静态 BFS（会话无关，可缓存）──
  let fetched;
  if (!refresh && existsSync(cachePath)) {
    log('[BFS] 读缓存（--refresh 可强制重爬）');
    fetched = new Map(Object.entries(JSON.parse(readFileSync(cachePath, 'utf8'))));
  } else {
    log(`[BFS] 沿静态资源图 BFS（种子 ${SEEDS.length}，零凭据）`);
    fetched = new Map();
    const queue = [...SEEDS];
    const seen = new Set();
    while (queue.length) {
      const p = queue.shift();
      if (seen.has(p)) continue;
      seen.add(p);
      const r = await get(ORIGIN + p);
      fetched.set(p, { status: r.status, text: r.text, type: r.type, url: r.url });
      for (const a of harvestAssets(r.text, ORIGIN + p)) if (!seen.has(a)) queue.push(a);
      if (fetched.size % 10 === 0) process.stdout.write(`      已取 ${fetched.size}…\r`);
    }
    log(`      共取回 ${fetched.size} 个资源`);
    writeFileSync(cachePath, JSON.stringify(Object.fromEntries(fetched)), 'utf8');
  }

  // ── 抽取 ──
  log('[抽取] .html 动作 / paramMap / 列定义');
  const actions = new Set();
  const paramMaps = [];
  const columnSets = [];
  const prmNames = {};

  /** 从一段正文里抽契约，累积到上面的集合。 */
  const absorb = (p, text) => {
    for (const a of harvestActions(text)) actions.add(a);
    const pm = harvestParamMap(text);
    if (pm) paramMaps.push({ file: p, ...pm });
    const cols = harvestColumns(text);
    if (cols.length) columnSets.push({ file: p, columns: cols });
    Object.assign(prmNames, harvestPrmNames(text));
  };

  for (const [p, r] of fetched) absorb(p, r.text);

  // 带会话时，把需要登录的入口也展开。菜单是权威路径来源；课表页的 JS
  // 里才有课表数据接口，而它的静态路径**猜不出来**（模块段不是从页面名派生的），
  // 只能登录后拿到页面再顺着 <script src> 走。
  if (hasSession) {
    log('[展开] 带会话抓取需要登录的入口');
    for (const p of AUTH_PAGES) {
      const r = await get(ORIGIN + p, { Cookie: cookieHeader });
      if (judge(r.status, r.url, r.text) === 'login-required') {
        log(`        会话已失效，跳过 ${p}`);
        continue;
      }
      absorb(p, r.text);
      log(`        ${p}  ${r.status}  ${r.text.length} 字符`);
      for (const a of harvestAssets(r.text, ORIGIN + p)) {
        if (fetched.has(a)) continue;
        const s = await get(ORIGIN + a, { Cookie: cookieHeader });
        if (s.status !== 200) continue;
        fetched.set(a, { status: s.status, text: s.text, type: s.type, url: s.url });
        absorb(a, s.text);
      }
    }
  }

  for (const a of KNOWN_ACTIONS) actions.add(a);
  const actionList = [...actions].sort();
  const biggestCols = Math.max(0, ...columnSets.map(c => c.columns.length));
  log(`      .html 动作 ${actionList.length} 个`);
  log(`      paramMap ${paramMaps.length} 处，学生字段最多 ${Math.max(0, ...paramMaps.map(p => p.student.length))} 个`);
  log(`      列定义 ${columnSets.length} 处，最多 ${biggestCols} 列`);

  // ── 存在性判定 ──
  let existence = {};
  if (!hasSession && existsSync(contractPath)) {
    try { existence = JSON.parse(readFileSync(contractPath, 'utf8')).existence || {}; } catch { /* 覆盖 */ }
  }

  if (hasSession) {
    log('[判定] 用正文措辞判断 .html 动作是否存在（务必趁会话新鲜）');
    const base = [...new Set(actionList.map(a => a.split('?')[0]))].sort();
    existence = {};
    let n = 0;
    for (const p of base) {
      const r = await get(ORIGIN + p, { Cookie: cookieHeader });
      const v = judge(r.status, r.url, r.text);
      existence[p] = v;
      if (v !== 'exists') log(`        ${v.padEnd(16)} ${p}`);
      n++;
      if (n % 20 === 0) process.stdout.write(`      已判 ${n}/${base.length}\r`);
    }
    const tally = {};
    for (const v of Object.values(existence)) tally[v] = (tally[v] || 0) + 1;
    log('');
    log(`      判定 ${base.length} 个：`);
    for (const [k, v] of Object.entries(tally).sort((a, b) => b[1] - a[1])) log(`        ${k.padEnd(16)} ${v}`);
  } else {
    log('[判定] 跳过（无会话）。设 HAM_JWGL_COOKIES 重跑即可。');
  }

  // ── 落盘 ──
  const contract = {
    origin: ORIGIN,
    generatedAt: new Date().toISOString(),
    mode: hasSession ? 'authed' : 'static',
    seeds: SEEDS,
    knownActions: KNOWN_ACTIONS,
    assetCount: fetched.size,
    actions: actionList,
    existence,
    paramMaps: paramMaps.map(p => ({ file: p.file, studentBranch: p.student, staffBranch: p.staff })),
    columns: columnSets,
    jqGridPrmNames: prmNames,
  };
  writeFileSync(contractPath, JSON.stringify(contract, null, 2), 'utf8');

  const md = ['# jwgl 接口契约（自动生成，勿手改）', '',
    `- 生成时间：${contract.generatedAt}`,
    `- 模式：${contract.mode === 'authed' ? '带会话' : '仅静态（零凭据）'}`,
    `- 取回静态资源：${fetched.size}`,
    `- .html 动作：${actionList.length}`, ''];
  if (Object.keys(existence).length) {
    md.push('## 动作存在性', '', '| 状态 | 路径 |', '|---|---|');
    for (const [p, v] of Object.entries(existence)
      .sort((a, b) => a[1].localeCompare(b[1]) || a[0].localeCompare(b[0]))) md.push(`| ${v} | \`${p}\` |`);
    md.push('');
  }
  md.push('## 全部 .html 动作', '');
  for (const a of actionList) md.push('- `' + a + '`');
  md.push('', '## paramMap', '');
  for (const p of paramMaps) {
    md.push(`### ${p.file}`, '',
      `学生分支（${p.student.length}）：\`${p.student.join(' ')}\``, '',
      `教工分支（${p.staff.length}）：\`${p.staff.join(' ')}\``, '');
  }
  md.push('## jqGrid 列定义', '');
  for (const c of columnSets) {
    md.push(`### ${c.file}（${c.columns.length} 列）`, '', '```', c.columns.join(' '), '```', '');
  }
  if (Object.keys(prmNames).length) {
    md.push('## jqGrid prmNames', '', '```json', JSON.stringify(prmNames, null, 2), '```', '');
  }
  writeFileSync(join(OUT, 'REPORT.md'), md.join('\n'), 'utf8');

  log('');
  log('产物:');
  log('  ' + contractPath);
  log('  ' + join(OUT, 'REPORT.md'));
}

main().catch(e => { console.error('爬虫失败:', e); process.exit(1); });

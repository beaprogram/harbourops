// Drives the real HarbourOps HTTP API (cookies + antiforgery, like the browser) for smoke tests and load.
//   node harness.mjs smoke <baseUrl> <imgDir>            -> JSON report of photo correctness checks
//   node harness.mjs load  <baseUrl> <imgDir> <scenario> -> JSON report of upload outcomes and timings
import { readFile } from 'node:fs/promises'
import { chromium } from '../../../e2e/node_modules/playwright/index.mjs'

const [mode, base, imgDir, scenario = 'burst'] = process.argv.slice(2)
const PASSWORD = 'HarbourDemo!2026'

class Client {
  constructor() { this.cookies = new Map(); this.token = null }
  cookieHeader() { return [...this.cookies].map(([k, v]) => `${k}=${v}`).join('; ') }
  remember(res) {
    for (const c of res.headers.getSetCookie()) {
      const [pair] = c.split(';'); const i = pair.indexOf('=')
      this.cookies.set(pair.slice(0, i), pair.slice(i + 1))
    }
  }
  async fetch(path, init = {}) {
    const headers = { ...(init.headers ?? {}), Cookie: this.cookieHeader() }
    if (init.method && init.method !== 'GET') headers['X-XSRF-TOKEN'] = this.token ?? await this.refresh()
    const res = await fetch(base + path, { ...init, headers, redirect: 'manual' })
    this.remember(res)
    return res
  }
  async refresh() {
    const res = await fetch(base + '/api/antiforgery/token', { headers: { Cookie: this.cookieHeader() } })
    this.remember(res); this.token = (await res.json()).token; return this.token
  }
  async json(path, body) {
    const res = await this.fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
    return { status: res.status, body: await res.json().catch(() => null) }
  }
  static async signIn(email) {
    const c = new Client(); await c.refresh()
    const r = await c.json('/api/auth/login', { email, password: PASSWORD })
    if (r.status !== 200) throw new Error(`login ${email}: ${r.status}`)
    await c.refresh(); return c
  }
  async createWorkOrder(title) {
    const props = await (await this.fetch('/api/properties')).json()
    const r = await this.json('/api/work-orders', { propertyId: props[0].id, title, description: 'Measurement run (fictional).', category: 'General' })
    if (r.status !== 201) throw new Error(`create: ${r.status} ${JSON.stringify(r.body)}`)
    return r.body.id
  }
  async upload(workOrderId, bytes, name, type) {
    const form = new FormData()
    form.append('file', new Blob([bytes], { type }), name)
    const started = performance.now()
    const res = await this.fetch(`/api/work-orders/${workOrderId}/attachments`, { method: 'POST', body: form })
    const body = await res.json().catch(() => null)
    return { status: res.status, code: body?.code, ms: Math.round(performance.now() - started), body, retryAfter: res.headers.get('retry-after') }
  }
}

async function decode(page, bytes) {
  // Chromium decodes the returned JPEG; we sample pixels from a canvas.
  return page.evaluate(async (b64) => {
    const img = new Image(); img.src = 'data:image/jpeg;base64,' + b64; await img.decode()
    const c = document.createElement('canvas'); c.width = img.naturalWidth; c.height = img.naturalHeight
    const g = c.getContext('2d'); g.drawImage(img, 0, 0)
    const px = (x, y) => Array.from(g.getImageData(x, y, 1, 1).data.slice(0, 3))
    return { width: img.naturalWidth, height: img.naturalHeight, top: px(c.width / 2, 5), bottom: px(c.width / 2, c.height - 6), center: px(c.width / 2, c.height / 2) }
  }, Buffer.from(bytes).toString('base64'))
}

const reddish = ([r, g, b]) => r > 180 && g < 90 && b < 90
const bluish = ([r, g, b]) => b > 180 && r < 90 && g < 90
const white = ([r, g, b]) => r > 240 && g > 240 && b > 240

async function smoke() {
  const riley = await Client.signIn('resident.riley@harbourview.example')
  const morgan = await Client.signIn('manager@harbourview.example')
  const dana = await Client.signIn('manager@covecommons.example')
  const wo = await riley.createWorkOrder('x64 smoke test')
  const browser = await chromium.launch(); const page = await browser.newPage()
  const checks = []
  const check = (name, ok, detail) => checks.push({ name, ok: !!ok, detail })

  const exif = await readFile(`${imgDir}/exif-gps-orient6.jpg`)
  const up = await riley.upload(wo, exif, 'kitchen ‮gpj.exe', 'image/jpeg')
  check('EXIF/GPS JPEG accepted (201)', up.status === 201, up.status)
  const full = await morgan.fetch(up.body.contentUrl)
  const fullBytes = Buffer.from(await full.arrayBuffer())
  const d = await decode(page, fullBytes)
  check('orientation 6 applied: 300x100 source stored as 100x300', d.width === 100 && d.height === 300, `${d.width}x${d.height}`)
  check('upright: red at top, blue at bottom', reddish(d.top) && bluish(d.bottom), JSON.stringify(d))
  check('no EXIF APP1 in stored bytes', !fullBytes.includes(Buffer.from('Exif\0\0')), '')
  check('no GPS/comment text in stored bytes', !fullBytes.includes(Buffer.from('harbour-secret-location-note')), '')
  check('Content-Type image/jpeg', full.headers.get('content-type') === 'image/jpeg', full.headers.get('content-type'))
  check('nosniff', full.headers.get('x-content-type-options') === 'nosniff', full.headers.get('x-content-type-options'))
  check('Cache-Control private, no-store', /private/.test(full.headers.get('cache-control')) && /no-store/.test(full.headers.get('cache-control')), full.headers.get('cache-control'))
  check('sanitised Content-Disposition', /^inline;/.test(full.headers.get('content-disposition')) && !/‮/.test(full.headers.get('content-disposition')), full.headers.get('content-disposition'))
  const preview = await morgan.fetch(up.body.previewUrl)
  const p = await decode(page, Buffer.from(await preview.arrayBuffer()))
  check('preview decodes and is no larger than 480 px', p.width <= 480 && p.height <= 480, `${p.width}x${p.height}`)

  const big = await readFile(`${imgDir}/max-40mp-5mib.jpg`)
  const upBig = await riley.upload(wo, big, 'big.jpg', 'image/jpeg')
  check('40 MP / 4.8 MiB JPEG accepted', upBig.status === 201, `${upBig.status} in ${upBig.ms} ms`)
  if (upBig.status === 201) {
    check('stored long edge is 2560', Math.max(upBig.body.width, upBig.body.height) === 2560, `${upBig.body.width}x${upBig.body.height}`)
    check('preview long edge is 480', Math.max(upBig.body.previewWidth, upBig.body.previewHeight) === 480, `${upBig.body.previewWidth}x${upBig.body.previewHeight}`)
  }
  const png = await readFile(`${imgDir}/max-40mp.png`)
  const upPng = await riley.upload(wo, png, 'gradient.png', 'image/png')
  check('40 MP PNG accepted and re-encoded as JPEG', upPng.status === 201 && upPng.body.mediaType === 'image/jpeg', `${upPng.status} in ${upPng.ms} ms`)
  const transparent = await riley.upload(wo, await readFile(`${imgDir}/transparent.png`), 't.png', 'image/png')
  const t = await decode(page, Buffer.from(await (await riley.fetch(transparent.body.contentUrl)).arrayBuffer()))
  check('transparent PNG flattened to white', white(t.center), JSON.stringify(t.center))
  const bad = await riley.upload(wo, await readFile(`${imgDir}/deceptive.jpg`), 'photo.jpg', 'image/jpeg')
  check('HTML named photo.jpg refused (415 unsupported_image)', bad.status === 415 && bad.code === 'unsupported_image', `${bad.status} ${bad.code}`)
  const other = await dana.fetch(up.body.contentUrl)
  check('other organization gets 404 for the photo', other.status === 404, other.status)
  await browser.close()
  console.log(JSON.stringify({ base, checks, passed: checks.filter(c => c.ok).length, total: checks.length }, null, 2))
  process.exit(checks.every(c => c.ok) ? 0 : 1)
}

async function load() {
  // Uploads go to several work orders (5 photos each at most). Background readers run at the same time.
  const files = {
    jpeg5mib: await readFile(`${imgDir}/max-40mp-5mib.jpg`),
    png40mp: await readFile(`${imgDir}/max-40mp.png`),
    edge: await readFile(`${imgDir}/max-edge-10000x4000.png`),
    phone: await readFile(`${imgDir}/phone-12mp.jpg`),
  }
  const plan = {
    // 12 simultaneous worst-case uploads: more than processing (2) + queue (4) admits.
    burst: { uploaders: 12, perUploader: 1, mix: ['png40mp', 'jpeg5mib', 'edge'] },
    // 3 sustained waves of 6 (fits processing + queue) with mixed limit-sized files.
    sustained: { uploaders: 6, perUploader: 3, mix: ['jpeg5mib', 'png40mp', 'phone', 'edge'] },
  }[scenario]
  const riley = await Client.signIn('resident.riley@harbourview.example')
  const morgan = await Client.signIn('manager@harbourview.example')
  const workOrders = []
  for (let i = 0; i < Math.ceil((plan.uploaders * plan.perUploader) / 5) + 1; i++) workOrders.push(await riley.createWorkOrder(`load ${scenario} ${i}`))

  let stop = false; const reads = { requests: 0, errors: 0 }
  const reader = async () => {
    while (!stop) {
      for (const path of ['/api/work-orders?pageSize=50', '/api/dashboard', '/api/work-orders/export?status=New']) {
        const r = await morgan.fetch(path); await r.arrayBuffer(); reads.requests++; if (!r.ok) reads.errors++
      }
      const list = await (await morgan.fetch(`/api/work-orders/${workOrders[0]}/attachments`)).json()
      for (const a of list.items ?? []) { const r = await morgan.fetch(a.previewUrl); await r.arrayBuffer(); reads.requests++; if (!r.ok) reads.errors++ }
    }
  }
  const readers = [reader(), reader()]
  const results = []; let slot = 0
  const started = performance.now()
  await Promise.all(Array.from({ length: plan.uploaders }, async (_, u) => {
    for (let k = 0; k < plan.perUploader; k++) {
      const n = slot++; const kind = plan.mix[n % plan.mix.length]
      const r = await riley.upload(workOrders[Math.floor(n / 5)], files[kind], `${kind}.bin`, kind.startsWith('png') || kind === 'edge' ? 'image/png' : 'image/jpeg')
      results.push({ kind, status: r.status, code: r.code, ms: r.ms, retryAfter: r.retryAfter })
    }
  }))
  stop = true; await Promise.all(readers)
  const byStatus = {}
  for (const r of results) byStatus[`${r.status}${r.code ? ' ' + r.code : ''}`] = (byStatus[`${r.status}${r.code ? ' ' + r.code : ''}`] ?? 0) + 1
  const ok = results.filter(r => r.status === 201).map(r => r.ms).sort((a, b) => a - b)
  console.log(JSON.stringify({ scenario, uploads: results.length, byStatus, createdMs: { min: ok[0], max: ok.at(-1) }, totalMs: Math.round(performance.now() - started), reads }, null, 2))
}

await (mode === 'smoke' ? smoke() : load())

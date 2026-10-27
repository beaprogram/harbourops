import { expect, test, type Page } from '@playwright/test'
import { accounts, signIn } from './helpers'

/**
 * Image processing inside the running container image (in CI: the linux/amd64 image on an x64 runner).
 * A JPEG made by the browser gets EXIF orientation 6, a GPS block, and a comment; the stored photo must come
 * back upright, re-encoded, small enough, and without any of that metadata.
 */
const SECRET_COMMENT = 'harbour-secret-location-note'

/** Inserts an EXIF APP1 segment (orientation + GPS IFD) and a COM segment right after the JPEG's SOI marker. */
function withExifGpsAndComment(jpeg: Buffer, orientation: number): Buffer {
  const tiff = Buffer.alloc(56)
  tiff.write('II*\0', 0, 'latin1')
  tiff.writeUInt32LE(8, 4)
  tiff.writeUInt16LE(2, 8) // two IFD0 entries
  const entry = (offset: number, tag: number, type: number, count: number, value: number) => {
    tiff.writeUInt16LE(tag, offset)
    tiff.writeUInt16LE(type, offset + 2)
    tiff.writeUInt32LE(count, offset + 4)
    tiff.writeUInt32LE(value, offset + 8)
  }
  entry(10, 0x0112, 3, 1, orientation) // Orientation
  entry(22, 0x8825, 4, 1, 38) // pointer to the GPS IFD
  tiff.writeUInt16LE(1, 38)
  entry(40, 0x0001, 2, 2, 'N'.charCodeAt(0)) // GPSLatitudeRef
  const exif = Buffer.concat([Buffer.from('Exif\0\0', 'latin1'), tiff])
  const app1 = Buffer.concat([Buffer.from([0xff, 0xe1]), u16(exif.length + 2), exif])
  const comment = Buffer.from(SECRET_COMMENT, 'latin1')
  const com = Buffer.concat([Buffer.from([0xff, 0xfe]), u16(comment.length + 2), comment])
  return Buffer.concat([jpeg.subarray(0, 2), app1, com, jpeg.subarray(2)])
}

function u16(value: number) {
  const b = Buffer.alloc(2)
  b.writeUInt16BE(value)
  return b
}

/** A 300×100 JPEG, red on the left and blue on the right, encoded by the browser. */
async function redBlueJpeg(page: Page): Promise<Buffer> {
  const dataUrl = await page.evaluate(() => {
    const canvas = document.createElement('canvas')
    canvas.width = 300
    canvas.height = 100
    const g = canvas.getContext('2d')!
    g.fillStyle = '#ff0000'
    g.fillRect(0, 0, 150, 100)
    g.fillStyle = '#0000ff'
    g.fillRect(150, 0, 150, 100)
    return canvas.toDataURL('image/jpeg', 0.95)
  })
  return Buffer.from(dataUrl.split(',')[1]!, 'base64')
}

async function decode(page: Page, bytes: Buffer) {
  return page.evaluate(async (b64) => {
    const img = new Image()
    img.src = `data:image/jpeg;base64,${b64}`
    await img.decode()
    const canvas = document.createElement('canvas')
    canvas.width = img.naturalWidth
    canvas.height = img.naturalHeight
    const g = canvas.getContext('2d')!
    g.drawImage(img, 0, 0)
    const pixel = (x: number, y: number) => Array.from(g.getImageData(x, y, 1, 1).data.slice(0, 3))
    return { width: img.naturalWidth, height: img.naturalHeight, top: pixel(img.naturalWidth / 2, 5), bottom: pixel(img.naturalWidth / 2, img.naturalHeight - 6) }
  }, bytes.toString('base64'))
}

test('uploaded photos are turned upright, stripped of EXIF/GPS/comments, and re-encoded with a small preview', async ({ browser }) => {
  const page = await signIn(browser, accounts.requester)
  const token = (await (await page.request.get('/api/antiforgery/token')).json()).token as string
  const properties = await (await page.request.get('/api/properties')).json()
  const created = await page.request.post('/api/work-orders', {
    headers: { 'X-XSRF-TOKEN': token },
    data: { propertyId: properties[0].id, title: `Photo processing check ${Date.now()}`, description: 'Automated image check.', category: 'General' },
  })
  expect(created.status()).toBe(201)
  const workOrder = await created.json()

  const original = withExifGpsAndComment(await redBlueJpeg(page), 6)
  const upload = await page.request.post(`/api/work-orders/${workOrder.id}/attachments`, {
    headers: { 'X-XSRF-TOKEN': token },
    multipart: { file: { name: 'rotated.jpg', mimeType: 'image/jpeg', buffer: original } },
  })
  expect(upload.status()).toBe(201)
  const photo = await upload.json()

  const content = await page.request.get(photo.contentUrl)
  expect(content.headers()['content-type']).toBe('image/jpeg')
  expect(content.headers()['x-content-type-options']).toBe('nosniff')
  expect(content.headers()['cache-control']).toContain('no-store')
  const stored = Buffer.from(await content.body())
  expect(stored.includes(Buffer.from('Exif\0\0', 'latin1')), 'no EXIF segment').toBe(false)
  expect(stored.includes(Buffer.from(SECRET_COMMENT, 'latin1')), 'no comment or GPS text').toBe(false)

  // Orientation 6 means "rotate 90° clockwise to view": the 300×100 picture is stored as 100×300, red on top.
  const full = await decode(page, stored)
  expect([full.width, full.height]).toEqual([100, 300])
  expect(full.top[0]).toBeGreaterThan(180)
  expect(full.top[2]).toBeLessThan(90)
  expect(full.bottom[2]).toBeGreaterThan(180)
  expect(full.bottom[0]).toBeLessThan(90)

  const preview = await decode(page, Buffer.from(await (await page.request.get(photo.previewUrl)).body()))
  expect(Math.max(preview.width, preview.height)).toBeLessThanOrEqual(480)
})

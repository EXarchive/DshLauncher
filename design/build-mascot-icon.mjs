// 由角色插画生成应用图标（多尺寸 ICO）的渲染管线。
//
// 步骤：
//   1. 边界洪水填充抠掉外部白底（角色内部的白色由深色描边隔开，不受影响）
//   2. 按尺寸裁剪到头部
//   3. 合成到圆角方块底上
//   4. 逐尺寸降采样（小尺寸用更紧的裁剪 + 轻锐化）
import fs from 'node:fs';
import path from 'node:path';

const require = (await import('node:module')).createRequire(
  'C:/Users/Administrator/.dsh/profiles/package.json');
const sharp = require('sharp');

const DESIGN = 'C:/Users/Administrator/PycharmProjects/DshLauncher/design';
const SRC = path.join(DESIGN, 'mascot-src.png');

// ── 1. 抠白底 ────────────────────────────────────────────────────────────────
const { data, info } = await sharp(SRC).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
const W = info.width, H = info.height, CH = info.channels;
console.log(`源图 ${W}x${H}, ${CH} 通道`);

const isWhite = (x, y) => {
  const o = (y * W + x) * CH;
  return data[o] >= 240 && data[o + 1] >= 240 && data[o + 2] >= 240;
};

const seen = new Uint8Array(W * H);
const queue = [];
const push = (x, y) => {
  if (x < 0 || y < 0 || x >= W || y >= H) return;
  const i = y * W + x;
  if (seen[i] || !isWhite(x, y)) return;
  seen[i] = 1; queue.push(i);
};
for (let x = 0; x < W; x++) { push(x, 0); push(x, H - 1); }
for (let y = 0; y < H; y++) { push(0, y); push(W - 1, y); }

let cleared = 0, head = 0;
while (head < queue.length) {
  const i = queue[head++];
  const x = i % W, y = (i / W) | 0;
  data[i * CH + 3] = 0;
  cleared++;
  push(x - 1, y); push(x + 1, y); push(x, y - 1); push(x, y + 1);
}
console.log(`抠掉外部白底 ${cleared} 像素（占 ${(cleared / (W * H) * 100).toFixed(1)}%）`);

const keyedPath = path.join(DESIGN, '_keyed.png');
await sharp(data, { raw: { width: W, height: H, channels: CH } }).png().toFile(keyedPath);
console.log(`已写出 ${keyedPath}`);

// ── 2. 裁剪规格：三档。尺寸越小裁得越紧，否则整张脸在 16px 下只剩十几个像素。
//   [x, y, side]  基于原图坐标（脸部中心约在 426, 305）
const CROP_WIDE  = { x: 108, y: 26,  side: 700 };  // 头部 + 头饰 + 肩
const CROP_TIGHT = { x: 168, y: 118, side: 540 };  // 收紧到脸
const CROP_FACE  = { x: 196, y: 96,  side: 452 };  // 只剩脸（16/20/24 用）

const cropFor = (size) => (size >= 64 ? CROP_WIDE : size >= 33 ? CROP_TIGHT : CROP_FACE);

// ── 3. 背景方案 ──────────────────────────────────────────────────────────────
const treatments = {
  navy:  { from: '#24406B', to: '#101B33' },   // 呼应角色的深蓝制服
  brand: { from: '#3D86FF', to: '#1B4FD8' },   // 与应用原图标一致的品牌蓝
  light: { from: '#FFFFFF', to: '#D9E6FA' },   // 近原作浅底
};

const tileSvg = (size, from, to) => Buffer.from(
  `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}">
     <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1">
       <stop offset="0" stop-color="${from}"/><stop offset="1" stop-color="${to}"/>
     </linearGradient></defs>
     <rect x="0" y="0" width="${size}" height="${size}" rx="${size * 0.22}" fill="url(#g)"/>
   </svg>`);

// ── 4. 逐尺寸合成 ────────────────────────────────────────────────────────────
// 角色占方块的比例：小尺寸给得更大，否则脸太小
const insetFor = (size) => (size <= 32 ? 0.80 : size <= 64 ? 0.78 : 0.76);

export async function renderOne(size, treat) {
  const { from, to } = treatments[treat];
  const crop = cropFor(size);

  // 先按裁剪框取出角色，再缩放到目标占位
  const box = Math.round(size * insetFor(size));
  let charBuf = await sharp(keyedPath)
    .extract({ left: crop.x, top: crop.y, width: crop.side, height: crop.side })
    .resize(box, box, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 }, kernel: 'lanczos3' })
    .png().toBuffer();

  // 小尺寸细节会糊成灰雾，轻微提高对比让它不显得脏
  if (size <= 48) {
    charBuf = await sharp(charBuf).linear(1.12, -10).png().toBuffer();
  }

  const tile = await sharp(tileSvg(size, from, to)).png().toBuffer();
  const off = Math.round((size - box) / 2);

  return sharp(tile)
    .composite([{ input: charBuf, left: off, top: off }])
    .png()
    .toBuffer();
}

// ── 7. 最终展示图 ────────────────────────────────────────────────────────────
if (process.argv[2] === 'show') {
  const TREAT = process.argv[3] || 'navy';
  const sizes = [256, 128, 64, 48, 40, 32, 24, 20, 16];
  const pad = 16, top = 20, bottom = 40;
  let x = pad;
  const placed = [];
  for (const s of sizes) {
    const buf = await renderOne(s, TREAT);
    // 展示时把小尺寸按整数倍放大，才能看清真实像素
    const disp = s <= 40 ? 96 : s <= 64 ? 128 : s;
    const scaled = await sharp(buf).resize(disp, disp, { kernel: 'nearest' }).png().toBuffer();
    placed.push({ input: scaled, left: x + Math.round((disp - Math.min(disp, 256)) / 2) - Math.round((disp - Math.min(disp, 256)) / 2), top: top + (256 - disp) });
    x += disp + pad;
  }
  const W2 = x, H2 = top + 256 + bottom;
  const bg = Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="${W2}" height="${H2}">
       <rect width="${W2}" height="${H2}" fill="#1B1C1F"/>
       ${sizes.map((s, i) => {
         const disp = s <= 40 ? 96 : s <= 64 ? 128 : s;
         let lx = pad; for (let j = 0; j < i; j++) lx += (sizes[j] <= 40 ? 96 : sizes[j] <= 64 ? 128 : sizes[j]) + pad;
         return `<text x="${lx + disp / 2}" y="${top + 256 + 22}" fill="#9AA0A6" font-size="12"
                      font-family="Segoe UI" text-anchor="middle">${s}px${disp !== s ? ` (${(disp / s).toFixed(disp % s === 0 ? 0 : 1)}x)` : ''}</text>`;
       }).join('')}
     </svg>`);
  const out = await sharp(bg).composite(placed).png().toFile(path.join(DESIGN, 'mascot-showcase.png'));
  console.log('展示图:', path.join(DESIGN, 'mascot-showcase.png'), out.width + 'x' + out.height);
}

// ── 6. 生成 ICO ──────────────────────────────────────────────────────────────
if (process.argv[2] === 'ico') {
  const TREAT = process.argv[3] || 'navy';
  const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

  const images = [];
  for (const s of sizes) {
    const buf = await renderOne(s, TREAT);
    images.push({ size: s, buf });
    console.log(`  ${s}px  ${buf.length} B`);
  }

  // ICO：6 字节头 + 每张 16 字节目录项 + 各张 PNG 数据
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);              // reserved
  header.writeUInt16LE(1, 2);              // type = icon
  header.writeUInt16LE(images.length, 4);

  const dir = Buffer.alloc(16 * images.length);
  let offset = 6 + dir.length;
  images.forEach((im, i) => {
    const o = i * 16;
    dir.writeUInt8(im.size >= 256 ? 0 : im.size, o + 0);   // 256 记作 0
    dir.writeUInt8(im.size >= 256 ? 0 : im.size, o + 1);
    dir.writeUInt8(0, o + 2);              // 调色板数
    dir.writeUInt8(0, o + 3);              // reserved
    dir.writeUInt16LE(1, o + 4);           // planes
    dir.writeUInt16LE(32, o + 6);          // 位深
    dir.writeUInt32LE(im.buf.length, o + 8);
    dir.writeUInt32LE(offset, o + 12);
    offset += im.buf.length;
  });

  const target = 'C:/Users/Administrator/PycharmProjects/DshLauncher/src/DshLauncher.ico';
  fs.writeFileSync(target, Buffer.concat([header, dir, ...images.map(i => i.buf)]));
  console.log(`已写出 ${target}  ${fs.statSync(target).size} B`);

  // 另存 256 供仓库/社交预览用
  fs.writeFileSync(path.join(DESIGN, 'icon-256.png'), images[images.length - 1].buf);
  console.log('已写出 icon-256.png');
}

// ── 5. 生成对比图（三种底色 × 若干尺寸）─────────────────────────────────────
if (process.argv[2] === 'compare') {
  const sizes = [128, 48, 32, 16];
  const cell = 150, pad = 14, labelH = 22;
  const rows = Object.keys(treatments).length;
  const W2 = pad + sizes.length * (cell + pad);
  const H2 = pad + rows * (cell + labelH + pad);

  const composites = [];
  let row = 0;
  for (const t of Object.keys(treatments)) {
    let col = 0;
    for (const s of sizes) {
      const buf = await renderOne(s, t);
      const disp = await sharp(buf).resize(
        s <= 48 ? 96 : 130, s <= 48 ? 96 : 130, { kernel: 'nearest' }).png().toBuffer();
      composites.push({ input: disp,
        left: pad + col * (cell + pad) + Math.round((cell - (s <= 48 ? 96 : 130)) / 2),
        top: pad + row * (cell + labelH + pad) + Math.round((cell - (s <= 48 ? 96 : 130)) / 2) });
      col++;
    }
    row++;
  }

  // 棋盘底 + 尺寸标签
  let svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${W2}" height="${H2}">`;
  svg += `<rect width="${W2}" height="${H2}" fill="#202124"/>`;
  for (let r = 0; r < rows; r++) for (let c = 0; c < sizes.length; c++) {
    const x = pad + c * (cell + pad), y = pad + r * (cell + labelH + pad);
    for (let i = 0; i < cell; i += 16) for (let j = 0; j < cell; j += 16) {
      const dark = ((i / 16 + j / 16) % 2) === 0;
      svg += `<rect x="${x + i}" y="${y + j}" width="16" height="16" fill="${dark ? '#2A2B2E' : '#232427'}"/>`;
    }
    svg += `<text x="${x + 4}" y="${y + cell + 16}" fill="#9AA0A6" font-size="12" font-family="Segoe UI">${sizes[c]}px</text>`;
  }
  svg += Object.keys(treatments).map((t, r) =>
    `<text x="${pad + 2}" y="${pad + r * (cell + labelH + pad) + 12}" fill="#E8EAED" font-size="13" font-weight="600" font-family="Segoe UI">${t}</text>`).join('');
  svg += '</svg>';

  const out = await sharp(Buffer.from(svg))
    .composite(composites).png().toFile(path.join(DESIGN, 'mascot-compare.png'));
  console.log('对比图:', path.join(DESIGN, 'mascot-compare.png'), out.width + 'x' + out.height);
}

// Genera el golden de paridad ejecutando el engine.js ORIGINAL del Imprime+ Tauri.
// Salida: engine_golden.json  (input + salida esperada por caso).
const fs = require('fs');
const path = require('path');

const enginePath = 'C:\\Users\\cdi\\imprime-plus-tauri\\src\\js\\engine.js';
const src = fs.readFileSync(enginePath, 'utf8');
// engine.js es `const Engine = (function(){...})();` -> lo evaluamos y devolvemos Engine.
const Engine = eval(src + '\n; Engine');

function imgs(specs) {
  // specs: array de [colSpan,rowSpan]  (o número => 1x1)
  return specs.map((s, i) => {
    const [cs, rs] = Array.isArray(s) ? s : [1, 1];
    return { id: 'img' + (i + 1), overrides: { colSpan: cs, rowSpan: rs } };
  });
}

function run(name, config, images) {
  const layout = Engine.computeLayout(config);
  const pages = Engine.paginate(images, layout).map(p => p.images.map(im => im.id));
  return { name, config, images, expectedLayout: layout, expectedPages: pages };
}

const N = (n) => imgs(Array.from({ length: n }, () => 1));

const cases = [];

// 1) Carta grid 3x3, sin margenes, spacing 0.3cm, 10 imgs
cases.push(run('carta-grid-3x3', {
  unit: 'cm', pageWidth: 21.59, pageHeight: 27.94,
  marginTop: 0, marginRight: 0, marginBottom: 0, marginLeft: 0,
  spacingH: 0.3, spacingV: 0.3, layoutMode: 'grid', gridRows: 3, gridCols: 3,
}, N(10)));

// 2) Carta count 9
cases.push(run('carta-count-9', {
  unit: 'cm', pageWidth: 21.59, pageHeight: 27.94,
  marginTop: 0, marginRight: 0, marginBottom: 0, marginLeft: 0,
  spacingH: 0.3, spacingV: 0.3, layoutMode: 'count', countPerPage: 9,
}, N(20)));

// 3) A4 size 5x5cm spacing 0.2
cases.push(run('a4-size-5x5', {
  unit: 'cm', pageWidth: 21.0, pageHeight: 29.7,
  marginTop: 0, marginRight: 0, marginBottom: 0, marginLeft: 0,
  spacingH: 0.2, spacingV: 0.2, layoutMode: 'size', imgWidth: 5, imgHeight: 5,
}, N(30)));

// 4) Legal grid 2x4 con margenes, pulgadas, con spans
cases.push(run('legal-grid-2x4-in-spans', {
  unit: 'in', pageWidth: 8.5, pageHeight: 14,
  marginTop: 0.5, marginRight: 0.5, marginBottom: 0.5, marginLeft: 0.5,
  spacingH: 0.1, spacingV: 0.1, layoutMode: 'grid', gridRows: 4, gridCols: 2,
}, imgs([[2, 1], 1, 1, [1, 2], 1, 1, 1, 1, 1])));

// 5) count 7 (prueba ceil + while)
cases.push(run('count-7', {
  unit: 'cm', pageWidth: 21.59, pageHeight: 27.94,
  marginTop: 0, marginRight: 0, marginBottom: 0, marginLeft: 0,
  spacingH: 0.3, spacingV: 0.3, layoutMode: 'count', countPerPage: 7,
}, N(15)));

// 6) mm unit grid 5x2
cases.push(run('mm-grid-5x2', {
  unit: 'mm', pageWidth: 210, pageHeight: 297,
  marginTop: 10, marginRight: 10, marginBottom: 10, marginLeft: 10,
  spacingH: 3, spacingV: 3, layoutMode: 'grid', gridRows: 2, gridCols: 5,
}, N(13)));

// 7) count 1
cases.push(run('count-1', {
  unit: 'cm', pageWidth: 21.59, pageHeight: 27.94,
  marginTop: 0, marginRight: 0, marginBottom: 0, marginLeft: 0,
  spacingH: 0, spacingV: 0, layoutMode: 'count', countPerPage: 1,
}, N(4)));

// 8) size con celda mas grande que la pagina (clamp cols/rows=1)
cases.push(run('size-bigger-than-page', {
  unit: 'cm', pageWidth: 10, pageHeight: 15,
  marginTop: 0, marginRight: 0, marginBottom: 0, marginLeft: 0,
  spacingH: 0.3, spacingV: 0.3, layoutMode: 'size', imgWidth: 20, imgHeight: 20,
}, N(3)));

// 9) grid 4x4 con spans variados -> multipagina
cases.push(run('grid-4x4-spans', {
  unit: 'cm', pageWidth: 21.59, pageHeight: 27.94,
  marginTop: 1, marginRight: 1, marginBottom: 1, marginLeft: 1,
  spacingH: 0.2, spacingV: 0.2, layoutMode: 'grid', gridRows: 4, gridCols: 4,
}, imgs([[2, 2], 1, 1, [3, 1], 1, [2, 2], 1, 1, 1, 1, 1, 1, [1, 3], 1, 1, 1, 1, 1])));

// 10) count 12 con algunos spans
cases.push(run('count-12-spans', {
  unit: 'cm', pageWidth: 21.59, pageHeight: 27.94,
  marginTop: 0, marginRight: 0, marginBottom: 0, marginLeft: 0,
  spacingH: 0.3, spacingV: 0.3, layoutMode: 'count', countPerPage: 12,
}, imgs([[2, 2], 1, 1, 1, [2, 1], 1, 1, 1, 1, 1, 1, 1, 1, 1, 1])));

// 11) grid 1x1 (una por pagina)
cases.push(run('grid-1x1', {
  unit: 'cm', pageWidth: 10.16, pageHeight: 15.24,
  marginTop: 0, marginRight: 0, marginBottom: 0, marginLeft: 0,
  spacingH: 0, spacingV: 0, layoutMode: 'grid', gridRows: 1, gridCols: 1,
}, N(5)));

// 12) sin imagenes
cases.push(run('empty', {
  unit: 'cm', pageWidth: 21.59, pageHeight: 27.94,
  marginTop: 0, marginRight: 0, marginBottom: 0, marginLeft: 0,
  spacingH: 0.3, spacingV: 0.3, layoutMode: 'grid', gridRows: 3, gridCols: 3,
}, []));

const out = path.join(__dirname, 'engine_golden.json');
fs.writeFileSync(out, JSON.stringify(cases, null, 2));
console.log('Casos: ' + cases.length + ' -> ' + out);

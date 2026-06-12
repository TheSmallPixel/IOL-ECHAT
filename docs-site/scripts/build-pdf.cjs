#!/usr/bin/env node
// Custom PDF generator for the ECHAT documentation.
//
// Why custom and not `mr-pdf`: mr-pdf bundles Puppeteer 2.1.1 (from 2020).
// That version of Chromium does not preserve internal anchor link
// annotations in `page.pdf()` reliably, so all the in-document hyperlinks
// would render as plain underlined text in the resulting PDF.
//
// This script uses modern Puppeteer (24.x) which preserves both internal
// (`href="#anchor"`) and external (`https://…`) link annotations as
// clickable elements in the PDF.
//
// Requires the docusaurus site to be reachable on http://localhost:3000/
// (e.g. via `npm run start` or `npm run build && npm run serve`).

const puppeteer = require('puppeteer');
const fs = require('fs');
const path = require('path');

const DOC_URL = 'http://localhost:3000/docs/';
const OUTPUT = path.join(__dirname, '..', 'echat-documentazione.pdf');

const COVER_TITLE = 'ECHAT: Documentazione di Progetto';
const COVER_SUB = 'Lorenzo Longiave / Cassia Giorgio, A.A. 2025/2026';

const printCss = `
@page { size: A4; margin: 20mm 21mm 21mm 21mm; }

/* === Cover === */
.pdf-cover {
  height: 90vh;
  display: flex;
  flex-direction: column;
  justify-content: center;
  align-items: center;
  text-align: center;
  page-break-after: always;
  break-after: page;
  font-family: ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
}
.pdf-cover h1 { font-size: 30pt; margin: 0 0 12pt 0; line-height: 1.15; }
.pdf-cover h3 { font-size: 13pt; margin: 0; font-weight: 400; color: #555; line-height: 1.4; }

/* === Body === */
html, body {
  font-size: 10pt;
  line-height: 1.4;
  margin: 0;
  padding: 0;
  -webkit-print-color-adjust: exact;
  print-color-adjust: exact;
}

article {
  padding: 0 !important;
  margin: 0 !important;
  max-width: 100% !important;
  width: 100% !important;
}

/* Hide docusaurus chrome */
nav, footer,
.theme-doc-toc-mobile, .theme-doc-toc-desktop,
.theme-doc-breadcrumbs, .breadcrumbs__item,
.pagination-nav, .theme-edit-this-page,
header.heroBanner_qdFl,
.skipToContent_fXgn {
  display: none !important;
}

/* Headings */
h1 { font-size: 22pt; page-break-after: avoid; margin-top: 12pt; }
h2 { font-size: 16pt; page-break-after: avoid; margin-top: 18pt; border-bottom: 1px solid #ddd; padding-bottom: 4pt; }
h3 { font-size: 13pt; page-break-after: avoid; margin-top: 14pt; }
h4 { font-size: 11.5pt; page-break-after: avoid; }
p, li { orphans: 3; widows: 3; }

/* Long tokens must break, not overflow */
body, p, li, td, th, a, code {
  word-wrap: break-word;
  overflow-wrap: break-word;
  hyphens: auto;
}

/* Make links visibly distinct in print */
a { color: #0654ba !important; text-decoration: none !important; }
a:hover { text-decoration: underline !important; }

/* Hide the "hash-link" icon next to each heading in docusaurus,
   it just clutters the PDF */
.hash-link { display: none !important; }

/* Images */
img {
  max-width: 100% !important;
  height: auto !important;
  page-break-inside: avoid;
  break-inside: avoid;
  display: block;
  margin: 8pt auto;
}
.markdown img, article img { max-width: 95% !important; }

/* Screenshot dell'interfaccia (cartella /assets/images/, prodotti dalle
   referenze /img/screenshots/ nel markdown). Le PlantUML stanno su URL
   remoti e non sono toccate da questo selettore. */
img[src*="/assets/images/"] {
  max-height: 20vh !important;
  object-fit: contain;
  border: 1px solid #ddd;
}

/* Tables */
table {
  width: 100% !important;
  border-collapse: collapse;
  page-break-inside: avoid;
  break-inside: avoid;
  font-size: 9.5pt;
  margin: 8pt 0;
}
table th, table td {
  border: 1px solid #ddd;
  padding: 4pt 6pt;
  vertical-align: top;
  word-break: break-word;
}
thead { display: table-header-group; }
tr { page-break-inside: avoid; }

/* Code blocks */
pre, code {
  font-size: 9pt;
  word-break: break-all;
  overflow-wrap: anywhere;
  white-space: pre-wrap;
}
pre {
  page-break-inside: avoid;
  border: 1px solid #ddd;
  padding: 6pt;
  background: #f8f8f8;
}

/* Admonitions */
.theme-admonition { page-break-inside: avoid; margin: 8pt 0; }

article > *:last-child { page-break-after: auto !important; }
`.trim();

(async () => {
  const browser = await puppeteer.launch({
    headless: 'new',
    args: ['--no-sandbox', '--disable-setuid-sandbox'],
  });
  const page = await browser.newPage();
  await page.emulateMediaType('print');

  // 1. Navigate to the docs page
  console.log(`Loading ${DOC_URL}`);
  await page.goto(DOC_URL, { waitUntil: 'networkidle0', timeout: 60_000 });

  // 2. Wait for the markdown content to be present
  await page.waitForSelector('article', { timeout: 30_000 });

  // 3. Extract the article HTML, build a fresh document with cover + content
  const articleHtml = await page.evaluate(() => {
    const a = document.querySelector('article');
    return a ? a.outerHTML : '';
  });

  if (!articleHtml) {
    throw new Error('Could not find <article> element in the page');
  }

  // 4. Replace the page body with cover + article (preserving the document so
  // Puppeteer can keep stylesheets and the runtime URL for anchor resolution)
  await page.evaluate(
    ({ coverTitle, coverSub, articleHtml }) => {
      const body = document.body;
      // Empty the body
      while (body.firstChild) body.removeChild(body.firstChild);

      // Cover
      const cover = document.createElement('div');
      cover.className = 'pdf-cover';
      cover.innerHTML = `<h1>${coverTitle}</h1><h3>${coverSub}</h3>`;
      body.appendChild(cover);

      // Article
      const wrap = document.createElement('div');
      wrap.innerHTML = articleHtml;
      while (wrap.firstChild) body.appendChild(wrap.firstChild);
    },
    { coverTitle: COVER_TITLE, coverSub: COVER_SUB, articleHtml }
  );

  // 5. Inject the print stylesheet
  await page.addStyleTag({ content: printCss });

  // 6. Force lazy-loaded images (PlantUML) to materialize by scrolling
  await page.evaluate(async () => {
    await new Promise((resolve) => {
      const distance = 300;
      let total = 0;
      const max = document.body.scrollHeight;
      const t = setInterval(() => {
        window.scrollBy(0, distance);
        total += distance;
        if (total >= max) {
          clearInterval(t);
          window.scrollTo(0, 0);
          resolve();
        }
      }, 50);
    });
  });

  // 7. Wait until every <img> in the body has finished loading (or errored).
  // After body.innerHTML reassignment the browser re-fetches every image and
  // a hard-coded sleep is not reliable enough; explicitly await the
  // complete/error event of each image so the PDF doesn't snapshot before
  // the screenshots are decoded.
  await page.evaluate(async () => {
    const imgs = Array.from(document.images);
    await Promise.all(
      imgs.map((img) => {
        if (img.complete && img.naturalWidth > 0) return Promise.resolve();
        return new Promise((resolve) => {
          img.addEventListener('load', resolve, { once: true });
          img.addEventListener('error', resolve, { once: true });
        });
      })
    );
  });

  // 8. Small additional settle window for fonts/layout to stabilise
  await new Promise((r) => setTimeout(r, 1000));

  // 8. Generate the PDF
  console.log(`Writing ${OUTPUT}`);
  await page.pdf({
    path: OUTPUT,
    format: 'A4',
    printBackground: true,
    preferCSSPageSize: true,
    margin: { top: '22mm', right: '24mm', bottom: '24mm', left: '24mm' },
  });

  await browser.close();
  const size = fs.statSync(OUTPUT).size;
  console.log(`Done: ${OUTPUT} (${(size / 1024).toFixed(1)} KB)`);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});

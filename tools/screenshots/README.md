# Screenshots and promo image

Not part of the plugin. These scripts drive the clickable mockup
(`ui/preview/mockup.tsx`, the real panels against a pretend backend) in a
headless browser.

```sh
cd ui && npm ci && npm run mockup && cd ..        # builds ui/preview/dist/
cd tools/screenshots && npm install
npm run docs     # docs/screenshots/*.png
npm run promo    # docs/promo/powerstation-promo.png (+ the crops it uses)
```

`puppeteer` downloads its own Chrome on install. To use a Chrome you already
have, set `CHROME_PATH` (and install `puppeteer-core`).

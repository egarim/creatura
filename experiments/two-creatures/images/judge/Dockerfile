# The judge: the outside browser test, with Playwright's Chromium. It sits on the sandbox network but on no creature.
FROM mcr.microsoft.com/playwright:v1.49.1-noble
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1
WORKDIR /judge
RUN npm init -y >/dev/null && npm install --no-audit --no-fund playwright@1.49.1 >/dev/null && npm pkg set type=module
COPY test.mjs .
ENTRYPOINT ["node", "test.mjs"]

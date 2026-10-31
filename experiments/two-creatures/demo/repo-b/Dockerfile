FROM node:22-alpine
WORKDIR /app
COPY package.json ./
COPY server.js ./
COPY public ./public
ENV PORT=8080
EXPOSE 8080
USER node
CMD ["node", "server.js"]

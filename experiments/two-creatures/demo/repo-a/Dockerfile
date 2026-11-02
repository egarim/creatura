FROM node:22-alpine
WORKDIR /app
COPY package.json server.js ./
ENV PORT=8080 DATA_DIR=/data NODE_ENV=production
EXPOSE 8080
CMD ["node", "server.js"]

# Trivial buildable image so `aspire do push` / `aspire deploy` exercises the GHCR push.
FROM nginx:latest
RUN echo "aspire-komodo-spike — built + pushed to GHCR by aspire" > /usr/share/nginx/html/index.html

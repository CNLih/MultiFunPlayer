FROM python:3.12-alpine

RUN addgroup -S -g 10001 mfp && adduser -S -D -H -u 10001 -G mfp mfp
WORKDIR /app
COPY server.py /app/server.py
USER 10001:10001
ENTRYPOINT ["python", "/app/server.py"]
